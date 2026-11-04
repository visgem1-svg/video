import math

FIELDS = {
    "worker_capacity": (int, 1, 1000),
    "target_utilization": (float, 0.05, 1.0),
    "min_workers": (int, 0, 1000),
    "max_workers": (int, 1, 1000),
    "cooldown_s": (float, 0, 86400),
    "drain_timeout_s": (float, 1, 86400),
}


def validate_policy(current: dict, patch: dict) -> dict:
    """Merge patch into current; raise ValueError(list of messages) when invalid."""
    errors = []
    if not isinstance(patch, dict) or not patch:
        raise ValueError(["body must be a non-empty JSON object"])
    merged = dict(current)
    for k, v in patch.items():
        if k not in FIELDS:
            errors.append(f"unknown field '{k}'")
            continue
        typ, lo, hi = FIELDS[k]
        if isinstance(v, bool) or not isinstance(v, (int, float)):
            errors.append(f"'{k}' must be a number")
            continue
        if typ is int and float(v) != int(v):
            errors.append(f"'{k}' must be an integer")
            continue
        v = typ(v)
        if not (lo <= v <= hi):
            errors.append(f"'{k}' must be in [{lo}, {hi}]")
            continue
        merged[k] = v
    if not errors and merged["min_workers"] > merged["max_workers"]:
        errors.append("min_workers must be <= max_workers")
    if errors:
        raise ValueError(errors)
    return merged


def compute_desired(cameras: list, workers: list, policy: dict):
    """clamp(ceil(load / (capacity * utilization)), min, max), +1 if any enabled camera is unassigned.

    load = sum of the registry's weighted worker `load` (contract 9.8: full 1.0, idle 0.1); when no
    non-dead worker reports a numeric `load` (registry < v1.8) it is the enabled camera count."""
    enabled = [c for c in cameras if c.get("enabled", True)]
    owned = set()
    for w in workers:
        if w.get("state", "live") != "dead":
            owned.update(w.get("assigned") or [])
    unassigned = [c for c in enabled if c.get("camera_id") not in owned]
    per = policy["worker_capacity"] * policy["target_utilization"]
    loads = [w["load"] for w in workers if w.get("state", "live") != "dead"
             and isinstance(w.get("load"), (int, float)) and not isinstance(w.get("load"), bool)]
    load = sum(loads) if loads else float(len(enabled))
    base = math.ceil(round(load / per, 9)) if enabled else 0  # round: 0.1-steps must not float to ceil+1
    raw = base + (1 if unassigned else 0)
    desired = max(policy["min_workers"], min(policy["max_workers"], raw))
    return desired, {"enabled": len(enabled), "unassigned": len(unassigned), "base": base, "load": round(load, 2)}
