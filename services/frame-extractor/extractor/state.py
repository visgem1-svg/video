"""Per-camera state machine (contract 5.5), exponential backoff and fps meter."""
from __future__ import annotations

import random
import time
from collections import deque
from enum import Enum
from typing import Callable


class CamState(str, Enum):
    STARTING = "starting"
    ONLINE = "online"
    STALLED = "stalled"
    RECONNECTING = "reconnecting"
    OFFLINE = "offline"
    ERROR = "error"


S = CamState
ALLOWED: dict[CamState, frozenset[CamState]] = {
    S.STARTING: frozenset({S.ONLINE, S.STALLED, S.RECONNECTING, S.ERROR, S.OFFLINE}),
    S.ONLINE: frozenset({S.STALLED, S.RECONNECTING, S.ERROR, S.OFFLINE}),
    S.STALLED: frozenset({S.RECONNECTING, S.ERROR, S.OFFLINE}),
    S.RECONNECTING: frozenset({S.ONLINE, S.STALLED, S.ERROR, S.OFFLINE}),
    S.ERROR: frozenset({S.RECONNECTING, S.ONLINE, S.STALLED, S.OFFLINE}),
    S.OFFLINE: frozenset({S.STARTING}),
}


class InvalidTransition(Exception):
    pass


class StateMachine:
    def __init__(self, on_change: Callable[[CamState, CamState], None] | None = None) -> None:
        self.state = CamState.STARTING
        self._on_change = on_change

    def to(self, new: CamState) -> bool:
        """Transition; returns True if the state changed. Same-state is a no-op."""
        if new == self.state:
            return False
        if new not in ALLOWED[self.state]:
            raise InvalidTransition(f"{self.state.value} -> {new.value}")
        old, self.state = self.state, new
        if self._on_change:
            self._on_change(old, new)
        return True


class Backoff:
    """1 s, 2 s, 4 s ... max 30 s, with +-jitter (never exceeds max)."""

    def __init__(
        self,
        base: float = 1.0,
        maximum: float = 30.0,
        jitter: float = 0.2,
        rng: Callable[[], float] = random.random,
    ) -> None:
        self.base, self.maximum, self.jitter, self._rng = base, maximum, jitter, rng
        self.attempts = 0

    def next_delay(self) -> float:
        d = min(self.maximum, self.base * (2 ** min(self.attempts, 16)))
        self.attempts += 1
        d *= 1 + self.jitter * (2 * self._rng() - 1)
        return min(self.maximum, max(0.0, d))

    def reset(self) -> None:
        self.attempts = 0


class FpsMeter:
    """Frames per second over a sliding window (default 10 s)."""

    def __init__(self, window_s: float = 10.0, clock: Callable[[], float] = time.monotonic) -> None:
        self.window = window_s
        self._clock = clock
        self._ts: deque[float] = deque()
        self._since = clock()

    def reset(self) -> None:
        self._ts.clear()
        self._since = self._clock()

    def record(self) -> None:
        self._ts.append(self._clock())

    def fps(self) -> float:
        now = self._clock()
        cutoff = now - self.window
        while self._ts and self._ts[0] < cutoff:
            self._ts.popleft()
        if not self._ts:
            return 0.0
        span = max(1.0, min(self.window, now - self._since))
        return len(self._ts) / span


class SkipMeter:
    """Share of gated frames over a sliding window, from cumulative (gated, emitted) samples."""

    def __init__(self, window_s: float = 60.0, clock: Callable[[], float] = time.monotonic) -> None:
        self.window = window_s
        self._clock = clock
        self._s: deque[tuple[float, int, int]] = deque()

    def reset(self) -> None:
        self._s.clear()

    def sample(self, gated_total: int, emitted_total: int) -> None:
        now = self._clock()
        self._s.append((now, gated_total, emitted_total))
        cutoff = now - self.window
        while len(self._s) > 2 and self._s[1][0] <= cutoff:  # keep one baseline at/before the cutoff
            self._s.popleft()

    def pct(self) -> float:
        if len(self._s) < 2:
            return 0.0
        _, g0, e0 = self._s[0]
        _, g1, e1 = self._s[-1]
        g, e = g1 - g0, e1 - e0
        return 100.0 * g / (g + e) if g + e > 0 else 0.0
