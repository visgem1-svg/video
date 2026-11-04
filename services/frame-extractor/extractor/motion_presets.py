"""mpdecimate thresholds per motion sensitivity (contract 9.9 section 3).

THIS FILE IS THE ONLY PLACE the numbers live: the perf benchmark calibrates them here and nowhere else.

mpdecimate drops a frame when NO 8x8 block differs by more than `hi` (sum of absolute pixel differences,
64 pixels per block) AND fewer than `frac` of the blocks differ by more than `lo`. Larger numbers therefore
drop more frames = LOWER sensitivity. `medium` is ffmpeg's own default.
"""
from __future__ import annotations

from typing import NamedTuple


class MpDecimate(NamedTuple):
    hi: int
    lo: int
    frac: float


PRESETS: dict[str, MpDecimate] = {
    "low": MpDecimate(hi=3072, lo=2560, frac=0.4),   # calibrated (qa/perf/decode-bench/RESULTS.md): clear change only
    "medium": MpDecimate(hi=1536, lo=1280, frac=0.33),  # calibrated: ~0.7 % noise false triggers, 100 % event recall
    "high": MpDecimate(hi=1024, lo=960, frac=0.2),    # calibrated: small / low-contrast change passes (3-7 % noise triggers)
}
DEFAULT_SENSITIVITY = "medium"
