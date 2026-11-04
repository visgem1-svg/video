import json

import httpx
import pytest

from app.validator import COCO_CLASSES
from app.llm import LLMError, OllamaClient, build_llm_schema, build_messages, build_system_prompt, normalize_llm_output
from tests.fakes import good_llm_output


async def test_ollama_request_shape(validator, tools):
    seen = {}

    def handler(req: httpx.Request):
        seen["url"] = str(req.url)
        seen["body"] = json.loads(req.content)
        return httpx.Response(200, json={"message": {"role": "assistant", "content": json.dumps(good_llm_output())}})

    schema = build_llm_schema(validator.schema, tools)
    c = OllamaClient("http://ollama:11434", "qwen2.5:3b", schema, transport=httpx.MockTransport(handler))
    out = await c.generate([{"role": "user", "content": "hi"}])
    assert out["name"] == "Truck plates"
    assert seen["url"] == "http://ollama:11434/api/chat"
    b = seen["body"]
    assert b["model"] == "qwen2.5:3b" and b["stream"] is False
    assert b["options"]["temperature"] == 0 and b["format"] == schema


async def test_ollama_errors(validator, tools):
    schema = build_llm_schema(validator.schema, tools)
    c = OllamaClient("http://o", "m", schema, transport=httpx.MockTransport(lambda r: httpx.Response(500)))
    with pytest.raises(LLMError):
        await c.generate([])
    c = OllamaClient("http://o", "m", schema, transport=httpx.MockTransport(
        lambda r: httpx.Response(200, json={"message": {"content": "not json"}})))
    with pytest.raises(LLMError):
        await c.generate([])


def test_llm_schema_constrains_classes_and_has_no_mode(validator, tools):
    s = build_llm_schema(validator.schema, tools)
    assert set(s["properties"]) == {"name", "explanation", "steps"}
    item = s["properties"]["steps"]["items"]
    assert "mode" not in item["properties"] and "mode" not in item["required"]
    assert item["properties"]["tool"]["enum"] == [t["name"] for t in tools["tools"]]
    for arr in (item["properties"]["params"]["properties"]["classes"], item["properties"]["when"]["properties"]["class"]):
        assert arr["type"] == "array" and arr["minItems"] == 1
        assert set(arr["items"]["enum"]) == COCO_CLASSES and len(arr["items"]["enum"]) == 80


def test_prompt_built_from_tools_and_examples_valid(validator, tools):
    p = build_system_prompt(tools)
    for t in tools["tools"]:
        assert t["name"] in p
    assert len(p) < 2200  # short: prompt eval is slow on CPU
    msgs = build_messages(tools, "q")
    assert msgs[-1] == {"role": "user", "content": "q"} and len(msgs) == 2 + 2 * 4
    assert "cars and trucks" in msgs[1]["content"]
    for m in msgs:  # every few-shot example must itself pass the validator after normalization
        if m["role"] == "assistant":
            ex = normalize_llm_output(json.loads(m["content"]), tools)
            plan = {"plan_id": "x", "version": 1, "query": "q", "camera_ids": [], "status": "draft",
                    "created_at": 1, "name": ex["name"], "steps": ex["steps"]}
            assert validator.validate(plan) == [], ex["name"]


def test_normalize_converts_arrays_sets_mode_strips_noise(tools):
    raw = {"name": "x" * 200, "explanation": "e", "steps": [
        {"id": "s1", "tool": "detect_objects", "params": {"classes": ["car", "truck", "car"]},
         "when": {"type": "always", "class": ["car"]}},
        {"id": "s2", "tool": "read_plate", "when": {"type": "found", "class": ["car", "truck"], "gt": 0, "step": None}},
        {"id": "s3", "tool": "read_text", "params": {}},
    ]}
    out = normalize_llm_output(raw, tools)
    s1, s2, s3 = out["steps"]
    assert len(out["name"]) == 80
    assert s1["params"] == {"classes": "car,truck"} and "when" not in s1 and s1["mode"] == "inline"
    assert s2["when"] == {"type": "found", "class": "car,truck"} and s2["mode"] == "inline"
    assert s3["mode"] == "queue" and "params" not in s3


def test_repair_drops_unsatisfiable_when_on_first_step(tools):
    from app.metrics import REPAIRS
    before = REPAIRS.labels(rule="drop_unsatisfiable_when")._value.get()
    raw = {"name": "n", "explanation": "e", "steps": [
        {"id": "s1", "tool": "detect_objects", "params": {"classes": ["car", "truck"]},
         "when": {"type": "found", "class": ["car", "truck"]}},
        {"id": "s2", "tool": "read_plate", "when": {"type": "found", "class": ["car", "truck"]}},
    ]}
    out = normalize_llm_output(raw, tools)
    assert "when" not in out["steps"][0] and out["steps"][1]["when"]["class"] == "car,truck"
    assert REPAIRS.labels(rule="drop_unsatisfiable_when")._value.get() == before + 1
