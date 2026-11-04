from pathlib import Path

from app.llm import LLMError
from app.ports import RegistryError

CONTRACTS = Path(__file__).resolve().parent.parent / "contracts"


class FakeLLM:
    """Returns queued outputs in order (last one repeats). Records the messages it was called with."""
    def __init__(self, *outputs):
        self.outputs = list(outputs)
        self.calls: list[list[dict]] = []

    async def generate(self, messages):
        self.calls.append(list(messages))
        out = self.outputs.pop(0) if len(self.outputs) > 1 else self.outputs[0]
        if isinstance(out, Exception):
            raise out
        return out


class MemRepo:
    def __init__(self):
        self.data: dict[str, dict] = {}

    async def get(self, pid):
        return self.data.get(pid)

    async def list(self):
        return list(self.data.values())

    async def save(self, plan):
        self.data[plan["plan_id"]] = dict(plan)

    async def save_many(self, plans):
        for p in plans:
            self.data[p["plan_id"]] = dict(p)

    async def delete(self, pid):
        return self.data.pop(pid, None) is not None


class FakePublisher:
    def __init__(self):
        self.sent: list[tuple[str, dict | None]] = []

    async def publish(self, plan_id, plan):
        self.sent.append((plan_id, plan))


class FakeRegistry:
    def __init__(self, cameras=("cam-001", "cam-002", "cam-003")):
        self.cams = {c: None for c in cameras}  # camera_id -> plan_id

    async def set_plan(self, camera_id, plan_id, only_if_plan=None):
        if camera_id not in self.cams:
            raise RegistryError(f"camera '{camera_id}' not found in registry")
        if only_if_plan is not None and self.cams[camera_id] != only_if_plan:
            return False
        changed = self.cams[camera_id] != plan_id
        self.cams[camera_id] = plan_id
        return changed


def good_llm_output():
    return {
        "name": "Truck plates",
        "explanation": "Detect trucks, read plates when found.",
        "steps": [
            {"id": "s1", "tool": "detect_objects", "params": {"classes": ["truck"]}},
            {"id": "s2", "tool": "read_plate", "when": {"type": "found", "class": ["truck"]}},
        ],
    }


def bad_llm_output():
    return {
        "name": "Bad",
        "explanation": "",
        "steps": [
            {"id": "s1", "tool": "detect_objects", "params": {"classes": ["truck"]}},
            {"id": "s2", "tool": "read_plate"},  # ungated
        ],
    }


class MemCache:
    def __init__(self):
        self.d: dict[str, dict] = {}
        self.clock = 0

    def _tick(self):
        self.clock += 1
        return self.clock

    async def get(self, key):
        e = self.d.get(key)
        return {"query": e["query"], "draft": e["draft"], "hits": e["hits"]} if e else None

    async def touch(self, key):
        self.d[key]["hits"] += 1
        self.d[key]["last"] = self._tick()
        return self.d[key]["hits"]

    async def put(self, key, nq, query, draft):
        self.d[key] = {"nq": nq, "query": query, "draft": draft, "hits": 0, "last": self._tick()}

    async def evict(self, key):
        self.d.pop(key, None)

    async def recent(self, limit):
        best = {}
        for e in self.d.values():
            if e["nq"] not in best or e["last"] > best[e["nq"]]["last"]:
                best[e["nq"]] = e
        rows = sorted(best.values(), key=lambda e: -e["last"])[:limit]
        return [{"query": e["query"], "hits": e["hits"], "last_used_at": e["last"]} for e in rows]
