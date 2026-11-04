import os


def env(name, default):
    v = os.environ.get(name)
    return default if v is None or v == "" else v


class Settings:
    def __init__(self):
        self.port = int(env("PORT", "5007"))
        self.registry_url = env("REGISTRY_URL", "http://registry:5001").rstrip("/")
        self.interval_s = float(env("INTERVAL_S", "10"))
        self.extractor_image = env("EXTRACTOR_IMAGE", "vp/frame-extractor:dev")
        self.docker_network = env("DOCKER_NETWORK", "vp-net")
        self.template_container = env("TEMPLATE_CONTAINER", "frame-extractor-1")
        self.file_sd_path = env("FILE_SD_PATH", "/sd/extractors.json")
        self.metrics_port = int(env("EXTRACTOR_METRICS_PORT", "9101"))
        self.log_level = env("LOG_LEVEL", "INFO")
        self.default_policy = {
            "worker_capacity": int(env("WORKER_CAPACITY", "12")),
            "target_utilization": float(env("TARGET_UTILIZATION", "0.8")),
            "min_workers": int(env("MIN_WORKERS", "2")),
            "max_workers": int(env("MAX_WORKERS", "6")),
            "cooldown_s": float(env("COOLDOWN_S", "60")),
            "drain_timeout_s": float(env("DRAIN_TIMEOUT_S", "120")),
        }
        self.initial_mode = env("MODE", "auto")
