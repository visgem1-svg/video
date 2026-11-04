import asyncio
import json
import logging
import os
import time
from collections import deque

from prometheus_client import CollectorRegistry, Counter, Gauge

from .policy import compute_desired, validate_policy

log = logging.getLogger("controller")


class Controller:
    def __init__(self, settings, registry, runtime, clock=time.time):
        self.s = settings
        self.registry = registry
        self.runtime = runtime
        self.clock = clock
        self.policy = dict(settings.default_policy)
        self.mode = settings.initial_mode if settings.initial_mode in ("auto", "manual") else "auto"
        self.manual_target = None
        self.events = deque(maxlen=100)
        self.last_action = None          # clock() of last scale action
        self.draining = {}               # worker_id -> {"since": t, "container": name}
        self.current = 0
        self.desired = 0
        self.workers_view = []
        self.last_error = None
        self.ticks = 0
        self._last_workers = []
        self._sd_last = None
        self._lock = asyncio.Lock()
        self.m = CollectorRegistry()
        self.g_workers = Gauge("vp_controller_workers", "workers by state", ["state"], registry=self.m)
        self.c_decisions = Counter("vp_controller_decisions_total", "decisions", ["action"], registry=self.m)
        self.g_desired = Gauge("vp_controller_desired_workers", "desired workers", registry=self.m)

    # ---------- helpers
    def event(self, action, worker_id=None, reason=""):
        self.events.append({"ts": int(self.clock() * 1000), "action": action, "worker_id": worker_id, "reason": reason})
        self.c_decisions.labels(action).inc()
        log.info(json.dumps({"event": action, "worker_id": worker_id, "reason": reason}))

    async def _rt(self, fn, *a):
        return await asyncio.to_thread(fn, *a)

    def _cooldown_ok(self):
        return self.last_action is None or self.clock() - self.last_action >= self.policy["cooldown_s"]

    def write_file_sd(self, managed):
        targets = []
        for c in sorted(managed, key=lambda x: x["name"]):
            if c["state"] not in ("running", "restarting"):
                continue
            host = c["ip"] or c["name"]
            targets.append({"targets": [f"{host}:{self.s.metrics_port}"],
                            "labels": {"worker_id": c["worker_id"], "managed_by": "worker-controller"}})
        payload = json.dumps(targets, indent=2)
        if payload == self._sd_last:
            return
        path = self.s.file_sd_path
        try:
            os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
            tmp = path + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                f.write(payload)
            os.replace(tmp, path)
            self._sd_last = payload
        except OSError as e:
            log.warning("file_sd write failed: %s", e)

    # ---------- main tick
    async def tick(self):
        async with self._lock:
            await self._tick()

    async def _tick(self):
        self.ticks += 1
        try:
            cameras = await self.registry.cameras()
            workers = await self.registry.workers()
            managed = await self._rt(self.runtime.list_managed)
        except Exception as e:
            self.last_error = str(e)
            self.event("error", None, f"read failed: {e}")
            return
        self.last_error = None
        self._last_workers = workers
        reg = {w["worker_id"]: w for w in workers}

        # adopt: registry says draining + managed container, but we lost the state (restart)
        for c in managed:
            w = reg.get(c["worker_id"])
            if w and w.get("draining") and c["worker_id"] not in self.draining and c["state"] == "running":
                self.draining[c["worker_id"]] = {"since": self.clock(), "container": c["name"]}
                self.event("adopt", c["worker_id"], "resumed drain after controller restart")

        # dead managed containers are removed (replacement happens via normal scale-up)
        for c in list(managed):
            if c["state"] in ("exited", "dead"):
                try:
                    await self._rt(self.runtime.remove, c["name"])
                    self.event("remove_dead", c["worker_id"], f"container {c['state']}")
                except Exception as e:
                    self.event("error", c["worker_id"], f"remove dead failed: {e}")
                managed.remove(c)
                self.draining.pop(c["worker_id"], None)
        by_wid = {c["worker_id"]: c for c in managed}

        # advance drains
        acted = False
        for wid, d in list(self.draining.items()):
            c = by_wid.get(wid)
            if c is None:
                self.draining.pop(wid)
                continue
            w = reg.get(wid)
            owned = (w or {}).get("assigned") or []
            if not owned:
                try:
                    await self._rt(self.runtime.remove, c["name"])
                except Exception as e:
                    self.event("error", wid, f"remove failed: {e}")
                    continue
                self.draining.pop(wid)
                managed.remove(c)
                by_wid.pop(wid)
                self.event("scale_down", wid, "drained, container removed")
                self.last_action = self.clock()
                acted = True
            elif self.clock() - d["since"] >= self.policy["drain_timeout_s"]:
                try:
                    await self.registry.undrain(wid)
                except Exception as e:
                    log.warning("undrain failed: %s", e)
                self.draining.pop(wid)
                self.event("drain_timeout", wid, f"still owns {len(owned)} cameras, drain cancelled")
                self.last_action = self.clock()
                acted = True

        self.write_file_sd(managed)

        # current / desired
        active = set()
        for w in workers:
            if w.get("state", "live") == "live" and not w.get("draining") and w["worker_id"] not in self.draining:
                active.add(w["worker_id"])
        for c in managed:  # starting containers not yet heartbeating
            if c["state"] in ("running", "created", "restarting") and c["worker_id"] not in self.draining:
                active.add(c["worker_id"])
        self.current = len(active)
        auto_desired, info = compute_desired(cameras, workers, self.policy)
        if self.mode == "manual" and self.manual_target is not None:
            self.desired = max(self.policy["min_workers"], min(self.policy["max_workers"], self.manual_target))
            why = f"manual target {self.manual_target}"
        else:
            self.desired = auto_desired
            why = (f"{info['enabled']} enabled cameras, load {info['load']:g}, {info['unassigned']} unassigned, "
                   f"capacity {self.policy['worker_capacity']}*{self.policy['target_utilization']}")
        self.g_desired.set(self.desired)
        self._update_view(workers, managed)

        if acted or (self.mode == "manual" and self.manual_target is None):
            return

        # a pending drain is cancelled if we now need the capacity
        if self.draining and self.desired > self.current:
            wid = next(iter(self.draining))
            try:
                await self.registry.undrain(wid)
                self.draining.pop(wid)
                self.event("drain_cancel", wid, f"need {self.desired} workers, have {self.current}")
            except Exception as e:
                self.event("error", wid, f"undrain failed: {e}")
            return
        if self.draining:
            return  # one scale-down at a time
        if not self._cooldown_ok():
            return

        if self.current < self.desired:
            await self._scale_up(managed, why)
        elif self.current > self.desired:
            await self._scale_down(managed, workers, by_wid, why)

    async def _scale_up(self, managed, why):
        try:
            idx = self.runtime.next_index([c["name"] for c in managed])
            name, wid = await self._rt(self.runtime.create, idx)
        except Exception as e:
            self.event("error", None, f"create failed: {e}")
            self.last_action = self.clock()  # avoid hammering on repeated failure
            return
        self.last_action = self.clock()
        self.event("scale_up", wid, f"{why}; desired {self.desired}, current {self.current}")
        managed.append({"name": name, "worker_id": wid, "state": "running", "health": None, "ip": ""})
        self.current += 1
        managed = await self._rt(self.runtime.list_managed)
        self.write_file_sd(managed)
        self._update_view(self._last_workers, managed)

    async def _scale_down(self, managed, workers, by_wid, why):
        if self.current - 1 < self.policy["min_workers"]:
            return
        reg = {w["worker_id"]: w for w in workers}
        cands = [c for c in managed if c["state"] == "running" and c["worker_id"] not in self.draining]
        if not cands:
            return  # only static workers left: never touched
        cands.sort(key=lambda c: len((reg.get(c["worker_id"]) or {}).get("assigned") or []))
        c = cands[0]
        try:
            await self.registry.drain(c["worker_id"])
        except Exception as e:
            self.event("error", c["worker_id"], f"drain failed: {e}")
            return
        self.draining[c["worker_id"]] = {"since": self.clock(), "container": c["name"]}
        self.event("drain_start", c["worker_id"], f"{why}; desired {self.desired}, current {self.current}")

    def _update_view(self, workers, managed):
        states = {"live": 0, "draining": 0, "dead": 0}
        for w in workers:
            st = w.get("state", "live")
            states[st] = states.get(st, 0) + 1
        for k, v in states.items():
            self.g_workers.labels(k).set(v)
        reg = {w["worker_id"]: w for w in workers}
        view = []
        for w in workers:
            c = next((m for m in managed if m["worker_id"] == w["worker_id"]), None)
            view.append(self._wview(w["worker_id"], w, c))
        for c in managed:
            if c["worker_id"] not in reg:
                view.append(self._wview(c["worker_id"], None, c))
        self.workers_view = view

    def _wview(self, wid, w, c):
        return {
            "worker_id": wid,
            "managed_by": "controller" if c else (w or {}).get("managed_by", "static"),
            "registry_state": (w or {}).get("state", "unregistered"),
            "draining": bool((w or {}).get("draining")) or wid in self.draining,
            "cameras": len((w or {}).get("assigned") or []),
            "capacity": (w or {}).get("capacity"),
            "container": None if not c else {"name": c["name"], "state": c["state"], "health": c["health"], "ip": c["ip"]},
        }

    # ---------- API operations
    def set_policy(self, patch):
        self.policy = validate_policy(self.policy, patch)
        return self.policy

    def set_mode(self, mode):
        if mode not in ("auto", "manual"):
            raise ValueError(["mode must be 'auto' or 'manual'"])
        self.mode = mode
        if mode == "auto":
            self.manual_target = None
        else:
            self.manual_target = self.current or self.policy["min_workers"]
        self.event("mode", None, f"mode set to {mode}")

    def scale(self, workers):
        if self.mode != "manual":
            raise PermissionError("scale is only allowed in manual mode")
        if isinstance(workers, bool) or not isinstance(workers, int):
            raise ValueError(["workers must be an integer"])
        lo, hi = self.policy["min_workers"], self.policy["max_workers"]
        if not lo <= workers <= hi:
            raise ValueError([f"workers must be in [{lo}, {hi}]"])
        self.manual_target = workers
        self.event("manual_target", None, f"manual target {workers}")

    def snapshot(self):
        return {
            "mode": self.mode, "policy": self.policy, "current": self.current, "desired": self.desired,
            "manual_target": self.manual_target, "last_error": self.last_error,
            "workers": self.workers_view, "events": list(self.events),
        }

    async def run(self):
        while True:
            try:
                await self.tick()
            except Exception:
                log.exception("tick failed")
            await asyncio.sleep(self.s.interval_s)
