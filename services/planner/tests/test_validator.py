import copy

import pytest

from tests.conftest import make_plan

DET = {"id": "s1", "tool": "detect_objects", "mode": "inline"}


def rules(validator, plan):
    return {i.rule for i in validator.validate(plan)}


def msgs(validator, plan):
    return [str(i) for i in validator.validate(plan)]


def test_default_plan_ok(validator):
    assert validator.validate(make_plan([DET])) == []


def test_complex_plan_ok(validator):
    plan = make_plan([
        {"id": "s1", "tool": "detect_objects", "mode": "inline", "params": {"classes": "truck,car", "min_confidence": 0.5}},
        {"id": "s2", "tool": "read_plate", "mode": "inline", "when": {"type": "found", "class": "truck", "min_confidence": 0.6}},
        {"id": "s3", "tool": "read_text", "mode": "queue", "when": {"type": "count", "class": "car", "gt": 2}},
        {"id": "s4", "tool": "detect_objects", "mode": "inline", "params": {"classes": "person"},
         "when": {"type": "failed", "step": "s2"}},
    ])
    assert msgs(validator, plan) == []


def test_faces_behind_person_ok(validator):
    plan = make_plan([
        {**DET, "params": {"classes": "person"}},
        {"id": "s2", "tool": "identify_faces", "mode": "inline", "when": {"type": "found", "class": "person"},
         "params": {"threshold": 0.4}},
    ])
    assert validator.validate(plan) == []


def test_only_step_plate_and_faces_ok(validator):
    for tool in ("read_plate", "identify_faces", "detect_faces"):
        assert validator.validate(make_plan([{"id": "s1", "tool": tool, "mode": "inline"}])) == []


def test_schema_errors_precise(validator):
    p = make_plan([DET])
    del p["name"]
    p["steps"][0]["tool"] = "teleport"
    out = msgs(validator, p)
    assert any("'name' is a required property" in m for m in out)
    assert any("plan.steps[0].tool" in m and "teleport" in m for m in out)


def test_schema_when_oneof_message_precise(validator):
    p = make_plan([DET, {"id": "s2", "tool": "read_text", "mode": "queue", "when": {"type": "found"}}])
    out = msgs(validator, p)
    assert any("steps[1].when" in m and "'class' is a required property" in m for m in out), out


def test_too_many_steps_and_empty(validator):
    many = [{"id": f"s{i}", "tool": "detect_objects", "mode": "inline", "params": {"classes": "car", "min_confidence": i / 10}}
            for i in range(1, 10)]
    assert "schema" in rules(validator, make_plan(many))
    assert "schema" in rules(validator, make_plan([]))


def test_not_a_dict(validator):
    assert "schema" in rules(validator, [1, 2])


def test_duplicate_ids(validator):
    p = make_plan([DET, {**DET, "params": {"classes": "car"}}])
    assert "step_ids" in rules(validator, p)
    assert any("duplicate step id" in m for m in msgs(validator, p))


def test_non_sequential_ids(validator):
    p = make_plan([DET, {"id": "s3", "tool": "read_text", "mode": "queue"}])
    assert any("must be 's2'" in m for m in msgs(validator, p))


def test_failed_references_earlier_step(validator):
    for ref in ("s2", "s5", "s1"):  # forward / unknown / self
        p = make_plan([{**DET, "when": {"type": "failed", "step": ref}}])
        assert "failed_ref" in rules(validator, p), ref
    ok = make_plan([DET, {"id": "s2", "tool": "read_text", "mode": "queue", "when": {"type": "failed", "step": "s1"}}])
    assert validator.validate(ok) == []


def test_non_coco_condition_class(validator):
    p = make_plan([DET, {"id": "s2", "tool": "read_text", "mode": "queue", "when": {"type": "found", "class": "spaceship"}}])
    assert "coco_class" in rules(validator, p)
    assert any("spaceship" in m for m in msgs(validator, p))


def test_non_coco_params_classes(validator):
    p = make_plan([{**DET, "params": {"classes": "truck,unicorn"}}])
    assert any("unicorn" in m for m in msgs(validator, p))


def test_multiword_coco_class_uses_space(validator):
    ok = make_plan([{**DET, "params": {"classes": "cell phone"}},
                    {"id": "s2", "tool": "read_text", "mode": "queue", "when": {"type": "found", "class": "cell phone"}}])
    assert validator.validate(ok) == []
    assert "coco_class" in rules(validator, make_plan([{**DET, "params": {"classes": "cell_phone"}}]))


@pytest.mark.parametrize("when", [None, {"type": "always"}, {"type": "found", "class": "person"},
                                  {"type": "count", "class": "bicycle", "gt": 1}, {"type": "failed", "step": "s1"}])
def test_read_plate_needs_vehicle_condition(validator, when):
    step = {"id": "s2", "tool": "read_plate", "mode": "inline"}
    if when:
        step["when"] = when
    assert "plate_gating" in rules(validator, make_plan([DET, step]))


@pytest.mark.parametrize("cls", ["car", "truck", "bus", "motorcycle"])
@pytest.mark.parametrize("wt", [{"type": "found"}, {"type": "count", "gt": 0}])
def test_read_plate_vehicle_ok(validator, cls, wt):
    step = {"id": "s2", "tool": "read_plate", "mode": "inline", "when": {**wt, "class": cls}}
    assert validator.validate(make_plan([DET, step])) == []


@pytest.mark.parametrize("tool", ["identify_faces", "detect_faces"])
def test_faces_need_person(validator, tool):
    ungated = make_plan([DET, {"id": "s2", "tool": tool, "mode": "inline"}])
    wrong = make_plan([DET, {"id": "s2", "tool": tool, "mode": "inline", "when": {"type": "found", "class": "car"}}])
    assert "face_gating" in rules(validator, ungated)
    assert "face_gating" in rules(validator, wrong)


def test_duplicate_tool_identical_params(validator):
    p = make_plan([DET, {"id": "s2", "tool": "detect_objects", "mode": "inline"}])
    assert "duplicate_tool" in rules(validator, p)
    p2 = make_plan([{**DET, "params": {"classes": "car", "min_confidence": 0.5}},
                    {"id": "s2", "tool": "detect_objects", "mode": "inline",
                     "params": {"min_confidence": 0.5, "classes": "car"}}])
    assert "duplicate_tool" in rules(validator, p2)
    p3 = make_plan([DET, {"id": "s2", "tool": "detect_objects", "mode": "inline", "params": {"classes": "car"}}])
    assert "duplicate_tool" not in rules(validator, p3)


def test_at_most_one_queue_step(validator):
    p = make_plan([DET,
                   {"id": "s2", "tool": "read_text", "mode": "queue", "when": {"type": "found", "class": "car"}},
                   {"id": "s3", "tool": "read_plate", "mode": "queue", "when": {"type": "found", "class": "car"}}])
    assert "queue_chain" in rules(validator, p)
    p["steps"][2]["mode"] = "inline"
    assert validator.validate(p) == []


def test_condition_needs_detect_source(validator):
    p = make_plan([{"id": "s1", "tool": "read_text", "mode": "queue"},
                   {"id": "s2", "tool": "read_plate", "mode": "inline", "when": {"type": "found", "class": "car"}}])
    assert "condition_source" in rules(validator, p)


def test_condition_class_excluded_by_filter(validator):
    p = make_plan([{**DET, "params": {"classes": "person"}},
                   {"id": "s2", "tool": "read_plate", "mode": "inline", "when": {"type": "found", "class": "truck"}}])
    assert any("never detected" in m for m in msgs(validator, p))


def test_min_confidence_range(validator):
    assert "params" in rules(validator, make_plan([{**DET, "params": {"min_confidence": 1.5}}]))


def test_validator_does_not_mutate(validator):
    p = make_plan([DET])
    before = copy.deepcopy(p)
    validator.validate(p)
    assert p == before


def test_any_of_class_lists(validator):
    det = {**DET, "params": {"classes": "car,truck"}}
    ok = make_plan([det, {"id": "s2", "tool": "read_plate", "mode": "inline", "when": {"type": "found", "class": "car,truck"}}])
    assert validator.validate(ok) == []
    mixed = make_plan([{**DET, "params": {"classes": "person,car"}},
                       {"id": "s2", "tool": "read_plate", "mode": "inline", "when": {"type": "found", "class": "person,car"}}])
    assert validator.validate(mixed) == []  # ANY vehicle satisfies plate gating
    novehicle = make_plan([{**DET, "params": {"classes": "person,dog"}},
                           {"id": "s2", "tool": "read_plate", "mode": "inline", "when": {"type": "found", "class": "person,dog"}}])
    assert "plate_gating" in rules(validator, novehicle)
    faces = make_plan([{**DET, "params": {"classes": "person,dog"}},
                       {"id": "s2", "tool": "detect_faces", "mode": "inline", "when": {"type": "count", "class": "dog,person", "gt": 1}}])
    assert validator.validate(faces) == []


def test_any_of_each_class_checked(validator):
    bad = make_plan([DET, {"id": "s2", "tool": "read_text", "mode": "queue", "when": {"type": "found", "class": "car,spaceship"}}])
    assert any("spaceship" in m for m in msgs(validator, bad))
    excl = make_plan([{**DET, "params": {"classes": "car"}},
                      {"id": "s2", "tool": "read_plate", "mode": "inline", "when": {"type": "found", "class": "car,truck"}}])
    out = msgs(validator, excl)
    assert any("condition_source" in m and "'truck'" in m for m in out)
