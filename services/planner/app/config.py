"""Environment configuration (see README for the documented list)."""
from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Settings:
    database_url: str
    kafka_bootstrap: str
    registry_url: str
    ollama_url: str
    ollama_model: str
    contracts_dir: Path
    plans_topic: str = "pipeline.plans"
    llm_timeout_s: float = 60.0
    llm_max_retries: int = 2  # retries AFTER the first attempt
    registry_timeout_s: float = 5.0
    log_level: str = "INFO"

    @staticmethod
    def from_env() -> "Settings":
        here = Path(__file__).resolve().parent.parent
        return Settings(
            database_url=os.getenv("DATABASE_URL", "postgresql://vp:vp@postgres:5432/planner"),
            kafka_bootstrap=os.getenv("KAFKA_BOOTSTRAP", "redpanda:9092"),
            registry_url=os.getenv("REGISTRY_URL", "http://registry:5001").rstrip("/"),
            ollama_url=os.getenv("OLLAMA_URL", "http://host.docker.internal:11434").rstrip("/"),
            ollama_model=os.getenv("OLLAMA_MODEL", "qwen2.5:3b"),
            contracts_dir=Path(os.getenv("CONTRACTS_DIR", str(here / "contracts"))),
            llm_timeout_s=float(os.getenv("LLM_TIMEOUT_S", "60")),
            llm_max_retries=int(os.getenv("LLM_MAX_RETRIES", "2")),
            registry_timeout_s=float(os.getenv("REGISTRY_TIMEOUT_S", "5")),
            log_level=os.getenv("LOG_LEVEL", "INFO").upper(),
        )
