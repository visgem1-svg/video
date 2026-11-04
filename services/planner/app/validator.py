"""Plan validator: JSON schema + semantic rules. Runs on every plan (LLM or hand-written)."""
from __future__ import annotations

import json
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator

# The 80 COCO classes, canonical (ultralytics) spelling: multi-word names use spaces.
COCO_CLASSES = frozenset(
    "person,bicycle,car,motorcycle,airplane,bus,train,truck,boat,traffic light,fire hydrant,stop sign,"
    "parking meter,bench,bird,cat,dog,horse,sheep,cow,elephant,bear,zebra,giraffe,backpack,umbrella,handbag,tie,"
    "suitcase,frisbee,skis,snowboard,sports ball,kite,baseball bat,baseball glove,skateboard,surfboard,"
    "tennis racket,bottle,wine glass,cup,fork,knife,spoon,bowl,banana,apple,sandwich,orange,broccoli,carrot,"
    "hot dog,pizza,donut,cake,chair,couch,potted plant,bed,dining table,toilet,tv,laptop,mouse,remote,keyboard,"
    "cell phone,microwave,oven,toaster,sink,refrigerator,book,clock,vase,scissors,teddy bear,hair drier,"
    "toothbrush".split(",")
)

VEHICLES = frozenset({"car", "truck", "bus", "motorcycle"})
PERSON = frozenset({"person"})
PLATE_TOOL = "read_plate"
FACE_TOOLS = frozenset({"identify_faces", "detect_faces"})


def norm_class(c: Any) -> str:
    return str(c).strip().lower()


def class_list(c: Any) -> list[str]:
    """`found`/`count` class and detect_objects `classes` are comma-separated any-of lists (contract 9.2)."""
    return [x for x in (norm_class(i) for i in str(c).split(",")) if x]


@dataclass(frozen=True)
class ValidationIssue:
    rule: str
    message: str

    def __str__(self) -> str:
        return f"[{self.rule}] {self.message}"


class PlanValidator:
    def __init__(self, schema: dict, tools: dict):
        self.schema = schema
        self.tool_names = {t["name"] for t in tools["tools"]}
        self._v = Draft202012Validator(schema)

    @classmethod
    def from_dir(cls, contracts_dir: Path) -> "PlanValidator":
        schema = json.loads((contracts_dir / "plan.schema.json").read_text(encoding="utf-8"))
        tools = json.loads((contracts_dir / "tools.json").read_text(encoding="utf-8"))
        return cls(schema, tools)

    # ------------------------------------------------------------------ public
    def validate(self, plan: Any) -> list[ValidationIssue]:
        issues = self._schema_issues(plan)
        if issues:  # semantic rules assume a structurally sound plan
            return issues
        return self._semantic_issues(plan)

    def validate_strings(self, plan: Any) -> list[str]:
        return [str(i) for i in self.validate(plan)]

    # ------------------------------------------------------------------ schema
    def _schema_issues(self, plan: Any) -> list[ValidationIssue]:
        out: list[ValidationIssue] = []
        for err in sorted(self._v.iter_errors(plan), key=lambda e: list(map(str, e.absolute_path))):
            for msg in self._describe(err):
                out.append(ValidationIssue("schema", f"{self._loc(err)}: {msg}"))
        return out

    @staticmethod
    def _loc(err) -> str:
        s = ""
        for p in err.absolute_path:
            s += f"[{p}]" if isinstance(p, int) else f".{p}"
        return "plan" + s

    def _describe(self, err) -> list[str]:
        if err.validator == "oneOf" and err.context:
            # pick the branch with the fewest errors: the one the author most likely meant
            by_branch: dict[int, list] = defaultdict(list)
            for c in err.context:
                by_branch[c.relative_schema_path[0]].append(c)
            # drop branches whose `type` const did not match: the author chose another branch
            matching = [b for b in by_branch.values() if not any(c.validator == "const" for c in b)]
            if not matching:
                return ["unknown condition type; 'type' must be one of always, found, count, failed"]
            best = min(matching, key=len)
            msgs = []
            for c in best:
                sub = ".".join(str(p) for p in c.relative_path)
                msgs.append(f"{c.message}{(' (at ' + sub + ')') if sub else ''}")
            return msgs or [err.message]
        if err.validator == "enum":
            return [f"{err.instance!r} is not one of {err.validator_value}"]
        msg = str(err.message)
        return [msg[:200] + "..." if len(msg) > 200 else msg]

    # ---------------------------------------------------------------- semantic
    def _semantic_issues(self, plan: dict) -> list[ValidationIssue]:
        issues: list[ValidationIssue] = []
        steps: list[dict] = plan["steps"]
        only_step = len(steps) == 1
        seen_ids: set[str] = set()
        seen_sig: dict[str, str] = {}
        queue_steps: list[str] = []
        det_filters: list[set[str] | None] = []  # per earlier detect_objects; None = no filter (all classes)

        for idx, st in enumerate(steps):
            sid, tool = st["id"], st["tool"]
            where = f"step {sid}"
            expected = f"s{idx + 1}"
            if sid in seen_ids:
                issues.append(ValidationIssue("step_ids", f"{where}: duplicate step id '{sid}'"))
            elif sid != expected:
                issues.append(ValidationIssue(
                    "step_ids",
                    f"{where}: step ids must be sequential s1, s2, ...; position {idx + 1} must be '{expected}'"))

            when = st.get("when") or {"type": "always"}
            wt = when["type"]
            if wt == "failed":
                ref = when["step"]
                if ref not in seen_ids:  # seen_ids holds only EARLIER steps here
                    issues.append(ValidationIssue(
                        "failed_ref", f"{where}: failed.step '{ref}' must reference an earlier step"))
            elif wt in ("found", "count"):
                listed = class_list(when["class"])
                bad = [c for c in listed if c not in COCO_CLASSES]
                if not listed:
                    issues.append(ValidationIssue("coco_class", f"{where}: when.class is empty"))
                if bad:
                    issues.append(ValidationIssue(
                        "coco_class", f"{where}: when.class has non-COCO class(es): {', '.join(bad)} "
                        "(comma-separated any-of list of COCO names, e.g. 'car,truck'; multi-word names use "
                        "spaces, e.g. 'cell phone')"))
                elif listed and not det_filters:
                    issues.append(ValidationIssue(
                        "condition_source", f"{where}: '{wt}' condition needs an earlier detect_objects step"))
                elif listed:
                    for cls in listed:
                        if all(f is not None and cls not in f for f in det_filters):
                            issues.append(ValidationIssue(
                                "condition_source",
                                f"{where}: class '{cls}' is never detected: the earlier detect_objects classes "
                                "filter excludes it"))

            if tool == PLATE_TOOL and not only_step and not self._gated_by(when, VEHICLES):
                issues.append(ValidationIssue(
                    "plate_gating", f"{where}: read_plate must run behind a found/count condition on a vehicle "
                    "class (car, truck, bus, motorcycle) unless it is the only step"))
            if tool in FACE_TOOLS and not only_step and not self._gated_by(when, PERSON):
                issues.append(ValidationIssue(
                    "face_gating", f"{where}: {tool} must run behind a found/count condition on class 'person' "
                    "unless it is the only step"))

            params = st.get("params") or {}
            sig = tool + "|" + json.dumps(params, sort_keys=True)
            if sig in seen_sig:
                issues.append(ValidationIssue(
                    "duplicate_tool",
                    f"{where}: tool '{tool}' with identical params already used in step {seen_sig[sig]}"))
            else:
                seen_sig[sig] = sid

            if tool == "detect_objects":
                classes = params.get("classes")
                if classes is None or str(classes).strip() == "":
                    det_filters.append(None)
                else:
                    names = set(class_list(classes))
                    bad = sorted(n for n in names if n not in COCO_CLASSES)
                    if bad:
                        issues.append(ValidationIssue(
                            "coco_class", f"{where}: params.classes has non-COCO class(es): {', '.join(bad)}"))
                    det_filters.append(names)
                mc = params.get("min_confidence")
                if mc is not None and not (isinstance(mc, (int, float)) and 0 <= mc <= 1):
                    issues.append(ValidationIssue(
                        "params", f"{where}: params.min_confidence must be a number in 0..1"))

            if st["mode"] == "queue":
                queue_steps.append(sid)
            seen_ids.add(sid)

        if len(queue_steps) > 1:
            issues.append(ValidationIssue(
                "queue_chain",
                f"at most one queue-mode (heavy) step is allowed, found {len(queue_steps)}: "
                f"{', '.join(queue_steps)}; make the others inline"))
        return issues

    @staticmethod
    def _gated_by(when: dict, classes: frozenset[str]) -> bool:
        return when["type"] in ("found", "count") and any(c in classes for c in class_list(when["class"]))
