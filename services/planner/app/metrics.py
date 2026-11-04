from prometheus_client import CollectorRegistry, Counter, Histogram, generate_latest, CONTENT_TYPE_LATEST

REGISTRY = CollectorRegistry()
REQUESTS = Counter("vp_planner_requests_total", "Planner requests by result", ["result"], registry=REGISTRY)
LLM_SECONDS = Histogram(
    "vp_planner_llm_seconds", "LLM call duration", registry=REGISTRY,
    buckets=(0.5, 1, 2, 5, 10, 20, 30, 45, 60, 90),
)
VALIDATION_ERRORS = Counter(
    "vp_planner_validation_errors_total", "Plan validation errors by rule", ["rule"], registry=REGISTRY
)
REPAIRS = Counter("vp_planner_repairs_total", "Deterministic repairs of LLM output by rule", ["rule"], registry=REGISTRY)
CACHE = Counter("vp_planner_cache_total", "Draft cache lookups", ["result"], registry=REGISTRY)

__all__ = ["REGISTRY", "REQUESTS", "LLM_SECONDS", "VALIDATION_ERRORS", "REPAIRS", "CACHE", "generate_latest", "CONTENT_TYPE_LATEST"]
