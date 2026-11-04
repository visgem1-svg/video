"""Prompt construction, LLM output schema and the Ollama client."""
from __future__ import annotations

import json
import time
from typing import Any, Protocol

import httpx

from .metrics import LLM_SECONDS, REPAIRS


class LLMError(Exception):
    """LLM unreachable / timed out / returned a non-JSON body."""


class LLMClient(Protocol):
    async def generate(self, messages: list[dict]) -> dict:
        """Return the parsed JSON object produced by the model ({name, explanation, steps})."""
        ...


# --------------------------------------------------------------------------- schema
def _class_enum() -> dict:
    from .validator import COCO_CLASSES
    return {"type": "string", "enum": sorted(COCO_CLASSES)}


def build_llm_schema(plan_schema: dict, tools: dict) -> dict:
    """JSON schema of the LLM output: only {name, explanation, steps}.

    Constrained decoding makes invalid classes impossible: `params.classes` and `when.class` are arrays of the
    80 COCO names (the service joins them into the comma-separated strings of the plan). `mode` is not asked
    from the LLM (the service sets it from tool cost). `when` is one flat object (grammar decoding handles flat
    objects far better than oneOf); `normalize_llm_output` strips keys that do not belong to the condition type.
    """
    classes = {"type": "array", "items": _class_enum(), "minItems": 1}
    return {
        "type": "object",
        "required": ["name", "explanation", "steps"],
        "properties": {
            "name": {"type": "string"},
            "explanation": {"type": "string"},
            "steps": {
                "type": "array",
                "items": {
                    "type": "object",
                    "required": ["id", "tool"],
                    "properties": {
                        "id": {"type": "string"},
                        "tool": {"enum": [t["name"] for t in tools["tools"]]},
                        "params": {
                            "type": "object",
                            "properties": {
                                "classes": classes,
                                "min_confidence": {"type": "number"},
                                "threshold": {"type": "number"},
                            },
                        },
                        "when": {
                            "type": "object",
                            "required": ["type"],
                            "properties": {
                                "type": {"enum": ["always", "found", "count", "failed"]},
                                "class": classes,
                                "min_confidence": {"type": "number"},
                                "gt": {"type": "integer"},
                                "step": {"type": "string"},
                            },
                        },
                    },
                },
            },
        },
    }


_WHEN_KEYS = {
    "always": set(),
    "found": {"class", "min_confidence"},
    "count": {"class", "gt"},
    "failed": {"step"},
}


def _join(v: Any) -> Any:
    """['car','truck'] -> 'car,truck' (de-duplicated, order kept). Other values pass through."""
    if isinstance(v, list):
        seen: list[str] = []
        for i in v:
            i = str(i).strip().lower()
            if i and i not in seen:
                seen.append(i)
        return ",".join(seen)
    return v


def normalize_llm_output(raw: Any, tools: dict) -> dict:
    """Convert the LLM shape to the plan shape: class arrays -> comma strings, mode from tool cost,
    drop noise keys. Never fixes semantics (the validator does that)."""
    cost = {t["name"]: t.get("cost") for t in tools["tools"]}
    if not isinstance(raw, dict):
        return {"name": "", "explanation": "", "steps": raw}
    out: dict[str, Any] = {
        "name": str(raw.get("name", ""))[:80],
        "explanation": str(raw.get("explanation", "")),
        "steps": raw.get("steps"),
    }
    if isinstance(out["steps"], list):
        steps = []
        seen_detect = False
        for st in out["steps"]:
            if not isinstance(st, dict):
                steps.append(st)
                continue
            st = dict(st)
            params = st.get("params")
            if isinstance(params, dict) and params:
                params = {k: _join(v) for k, v in params.items() if v is not None}
            if params:
                st["params"] = params
            else:
                st.pop("params", None)
            if st.get("mode") not in ("inline", "queue"):
                st["mode"] = "queue" if cost.get(st.get("tool")) == "high" else "inline"
            when = st.get("when")
            if isinstance(when, dict) and when.get("type") in _WHEN_KEYS:
                keep = _WHEN_KEYS[when["type"]] | {"type"}
                when = {k: _join(v) for k, v in when.items() if k in keep and v is not None}
                if when["type"] == "always":
                    st.pop("when", None)
                elif when["type"] in ("found", "count") and not seen_detect:
                    # can never be satisfied: no earlier detect_objects produced objects. Pure repair.
                    st.pop("when", None)
                    REPAIRS.labels(rule="drop_unsatisfiable_when").inc()
                else:
                    st["when"] = when
            if st.get("tool") == "detect_objects":
                seen_detect = True
            steps.append(st)
        out["steps"] = steps
    return out


# --------------------------------------------------------------------------- prompt
_EXAMPLES = [
    (
        "watch for cars and trucks and read their license plates",
        {
            "name": "Vehicle plates",
            "explanation": "Detect vehicles, read plates only when one is found.",
            "steps": [
                {"id": "s1", "tool": "detect_objects", "params": {"classes": ["car", "truck"]}},
                {"id": "s2", "tool": "read_plate", "when": {"type": "found", "class": ["car", "truck"]}},
            ],
        },
    ),
    (
        "recognize people at the lobby entrance",
        {
            "name": "Lobby face recognition",
            "explanation": "Identify faces only when a person is present.",
            "steps": [
                {"id": "s1", "tool": "detect_objects", "params": {"classes": ["person"]}},
                {"id": "s2", "tool": "identify_faces", "when": {"type": "found", "class": ["person"]}},
            ],
        },
    ),
    (
        "read the labels on trucks in the loading yard",
        {
            "name": "Truck label reading",
            "explanation": "OCR only when a truck is present.",
            "steps": [
                {"id": "s1", "tool": "detect_objects", "params": {"classes": ["truck"]}},
                {"id": "s2", "tool": "read_text", "when": {"type": "found", "class": ["truck"]}},
            ],
        },
    ),
    (
        "count the dogs in the yard",
        {
            "name": "Dog counting",
            "explanation": "Object detection is enough.",
            "steps": [{"id": "s1", "tool": "detect_objects", "params": {"classes": ["dog"]}}],
        },
    ),
]


def build_system_prompt(tools: dict) -> str:
    lines = []
    for t in tools["tools"]:
        params = ", ".join(t.get("params", {})) or "none"
        lines.append(f"- {t['name']} [{t['cost']}, gives {t['output']}]: {t['description']} Params: {params}.")
    return (
        "Convert the user request about camera video into a JSON pipeline plan {name, explanation, steps}.\n"
        "Tools:\n" + "\n".join(lines) + "\n"
        "Step: {id, tool, params?, when?}; ids s1, s2, ... in order. "
        "when (default always): {type:found, class:[...], min_confidence?} | {type:count, class:[...], gt} | "
        "{type:failed, step}.\n"
        "Rules: start with detect_objects (cheap) listing only the classes needed in params.classes. "
        "Put read_plate (vehicle classes), read_text, and face tools (person) AFTER it behind a found/count "
        "condition whose class list is within the detected classes. Max 8 steps, usually 2-3. "
        "Never repeat a tool with the same params."
    )


def build_messages(tools: dict, query: str) -> list[dict]:
    msgs = [{"role": "system", "content": build_system_prompt(tools)}]
    for q, plan in _EXAMPLES:
        msgs.append({"role": "user", "content": q})
        msgs.append({"role": "assistant", "content": json.dumps(plan, separators=(",", ":"))})
    msgs.append({"role": "user", "content": query})
    return msgs


def retry_message(errors: list[str]) -> dict:
    return {
        "role": "user",
        "content": "That plan is invalid:\n" + "\n".join(f"- {e}" for e in errors)
        + "\nFix every error and reply with the corrected JSON only.",
    }


# --------------------------------------------------------------------------- client
class OllamaClient:
    def __init__(self, url: str, model: str, schema: dict, timeout_s: float = 60.0,
                 transport: httpx.AsyncBaseTransport | None = None):
        self.url, self.model, self.schema, self.timeout_s = url.rstrip("/"), model, schema, timeout_s
        self._http = httpx.AsyncClient(timeout=timeout_s, transport=transport)

    async def aclose(self) -> None:
        await self._http.aclose()

    async def generate(self, messages: list[dict]) -> dict:
        body = {
            "model": self.model,
            "messages": messages,
            "stream": False,
            "format": self.schema,
            "options": {"temperature": 0},
        }
        t0 = time.perf_counter()
        try:
            r = await self._http.post(f"{self.url}/api/chat", json=body)
            r.raise_for_status()
            content = r.json()["message"]["content"]
        except (httpx.HTTPError, KeyError, ValueError) as e:
            raise LLMError(f"LLM request failed: {type(e).__name__}: {e}") from e
        finally:
            LLM_SECONDS.observe(time.perf_counter() - t0)
        try:
            return json.loads(content)
        except ValueError as e:
            raise LLMError(f"LLM returned non-JSON content: {content[:200]!r}") from e
