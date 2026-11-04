import json
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

from controller.config import Settings  # noqa: E402
from controller.core import Controller  # noqa: E402


class Clock:
    def __init__(self):
        self.t = 1000.0

    def __call__(self):
        return self.t

    def advance(self, s):
        self.t += s


class FakeRegistry:
    """Mimics the registry client (9.5): cameras/workers + drain/undrain."""

    def __init__(self):
        self.cams = []
        self.wmap = {}
        self.calls = []

    def set_cams(self, n, disabled=0):
        self.cams = [{"camera_id": f"cam-{i:03d}", "enabled": i >= disabled} for i in range(n)]

    def assign(self, wid, cams, **kw):
        w = self.wmap.setdefault(wid, {"worker_id": wid, "capacity": 12, "state": "live", "draining": False})
        w["assigned"] = list(cams)
        w.update(kw)

    async def cameras(self):
        return self.cams

    async def workers(self):
        return list(self.wmap.values())

    async def drain(self, wid):
        self.calls.append(("drain", wid))
        self.wmap[wid]["draining"] = True
        self.wmap[wid]["state"] = "draining"

    async def undrain(self, wid):
        self.calls.append(("undrain", wid))
        self.wmap[wid]["draining"] = False
        self.wmap[wid]["state"] = "live"


class FakeRuntime:
    def __init__(self):
        self.containers = {}
        self.created = []
        self.removed = []

    def add(self, name, worker_id, state="running", ip="10.0.0.5"):
        self.containers[name] = {"name": name, "worker_id": worker_id, "state": state, "health": "healthy", "ip": ip}

    def list_managed(self):
        return [dict(c) for c in self.containers.values()]

    def next_index(self, names):
        i = 1
        while f"vp-fx-auto-{i}" in names:
            i += 1
        return i

    def create(self, i):
        name, wid = f"vp-fx-auto-{i}", f"fx-auto-{i}"
        self.add(name, wid, ip=f"10.0.1.{i}")
        self.created.append(name)
        return name, wid

    def remove(self, name):
        self.containers.pop(name)
        self.removed.append(name)


@pytest.fixture
def env(tmp_path):
    s = Settings()
    s.file_sd_path = str(tmp_path / "sd" / "extractors.json")
    clock = Clock()
    reg, rt = FakeRegistry(), FakeRuntime()
    return Controller(s, reg, rt, clock=clock), reg, rt, clock, s


def read_sd(s):
    with open(s.file_sd_path, encoding="utf-8") as f:
        return json.load(f)
