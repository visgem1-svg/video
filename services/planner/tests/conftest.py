import json

import pytest

from app.llm import build_llm_schema
from app.service import PlannerService
from app.validator import PlanValidator
from tests.fakes import CONTRACTS, FakeLLM, FakePublisher, FakeRegistry, MemRepo, good_llm_output


@pytest.fixture(scope="session")
def tools():
    return json.loads((CONTRACTS / "tools.json").read_text(encoding="utf-8"))


@pytest.fixture(scope="session")
def validator():
    return PlanValidator.from_dir(CONTRACTS)


@pytest.fixture
def parts():
    return {"repo": MemRepo(), "pub": FakePublisher(), "reg": FakeRegistry(), "llm": FakeLLM(good_llm_output())}


@pytest.fixture
def service(parts, validator, tools):
    return PlannerService(parts["repo"], parts["pub"], parts["reg"], parts["llm"], validator, tools, max_retries=2)


def make_plan(steps, **kw):
    import copy
    steps = copy.deepcopy(steps)
    p = {"plan_id": "11111111-1111-4111-8111-111111111111", "version": 1, "name": "t", "query": "q",
         "camera_ids": ["cam-001"], "status": "draft", "created_at": 1761030000000, "steps": steps}
    p.update(kw)
    return p

