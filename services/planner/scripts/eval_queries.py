"""Run representative queries against a live Ollama and print the plans + validity rate.

Usage (from services/planner):
    .venv\\Scripts\\python.exe scripts\\eval_queries.py [--url http://localhost:11434] [--model qwen2.5:3b] [--retries 2]
No Kafka / Postgres / registry needed: only the LLM + validator are exercised.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from app.llm import LLMError, OllamaClient, build_llm_schema  # noqa: E402
from app.service import PlannerService  # noqa: E402
from app.validator import PlanValidator  # noqa: E402

QUERIES = [
    "alert me when a truck enters gate 3 and read its plate",
    "detect all people in the lobby",
    "recognize known employees entering the main door",
    "read license plates of cars in the parking lot",
    "count the cars on the road",
    "read the text on signs and labels near the loading dock",
    "notify me if more than 5 people gather at the entrance",
    "find buses and motorcycles and read their plates",
    "is there a dog or a bicycle in the yard",
    "identify faces of people and read the plate of any vehicle at the gate",
    "show me a spaceship landing in the car park",  # nonsense: should still yield a valid plan or fail cleanly
]


class _Null:
    async def get(self, *_): return None
    async def list(self): return []
    async def save(self, *_): pass
    async def delete(self, *_): return False
    async def publish(self, *_): pass
    async def set_plan(self, *_, **__): return False


async def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="http://localhost:11434")
    ap.add_argument("--model", default="qwen2.5:3b")
    ap.add_argument("--retries", type=int, default=2)
    ap.add_argument("--timeout", type=float, default=60)
    ap.add_argument("--first-attempt-only", action="store_true", help="measure raw LLM quality (no retries)")
    a = ap.parse_args()

    contracts = ROOT / "contracts"
    validator = PlanValidator.from_dir(contracts)
    tools = json.loads((contracts / "tools.json").read_text(encoding="utf-8"))
    llm = OllamaClient(a.url, a.model, build_llm_schema(validator.schema, tools), timeout_s=a.timeout)
    n = _Null()
    svc = PlannerService(n, n, n, llm, validator, tools, max_retries=0 if a.first_attempt_only else a.retries)

    ok = 0
    times = []
    for q in QUERIES:
        t0 = time.perf_counter()
        try:
            r = await svc.draft(q, ["cam-001"])
        except LLMError as e:
            print(f"\n### {q}\n  LLM ERROR: {e}")
            continue
        dt = time.perf_counter() - t0
        times.append(dt)
        ok += r["valid"]
        print(f"\n### {q}\n  valid={r['valid']}  ({dt:.1f}s)  explanation: {r['explanation']}")
        if r["plan"]:
            print("  name:", r["plan"]["name"])
            for s in r["plan"]["steps"] if isinstance(r["plan"]["steps"], list) else []:
                print("   ", json.dumps(s))
        for e in r["errors"]:
            print("  ERROR", e)
    await llm.aclose()
    print(f"\nvalidity: {ok}/{len(QUERIES)} = {100 * ok / len(QUERIES):.0f}%"
          + (f"   avg {sum(times) / len(times):.1f}s, max {max(times):.1f}s" if times else ""))
    return 0 if ok == len(QUERIES) else 1


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
