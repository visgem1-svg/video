"""Docker access. Security: the Docker socket is root-equivalent. This module only reads the
template container and only stops/removes containers carrying the label
vp.managed-by=worker-controller (checked before every destructive call)."""
import re

LABEL_KEY = "vp.managed-by"
LABEL_VAL = "worker-controller"
NAME_RE = re.compile(r"^vp-fx-auto-(\d+)$")


class DockerRuntime:
    def __init__(self, settings, client=None):
        if client is None:
            import docker
            client = docker.from_env()
        self.client = client
        self.s = settings

    @staticmethod
    def _info(c):
        a = c.attrs or {}
        env = {}
        for e in (a.get("Config") or {}).get("Env") or []:
            k, _, v = e.partition("=")
            env[k] = v
        nets = (a.get("NetworkSettings") or {}).get("Networks") or {}
        ip = ""
        for n in nets.values():
            if n.get("IPAddress"):
                ip = n["IPAddress"]
                break
        st = a.get("State") or {}
        return {
            "name": c.name,
            "worker_id": env.get("WORKER_ID") or c.name,
            "state": st.get("Status") or c.status,
            "health": (st.get("Health") or {}).get("Status"),
            "ip": ip,
        }

    def list_managed(self):
        cs = self.client.containers.list(all=True, filters={"label": f"{LABEL_KEY}={LABEL_VAL}"})
        return [self._info(c) for c in cs]

    def _template(self):
        t = self.s.template_container
        try:
            return self.client.containers.get(t)
        except Exception:
            found = self.client.containers.list(all=True, filters={"label": f"com.docker.compose.service={t}"})
            if not found:
                raise RuntimeError(f"template container '{t}' not found")
            return found[0]

    def next_index(self, taken_names):
        used = {int(m.group(1)) for n in taken_names if (m := NAME_RE.match(n))}
        i = 1
        while i in used:
            i += 1
        return i

    def create(self, index: int):
        name, wid = f"vp-fx-auto-{index}", f"fx-auto-{index}"
        t = self._template().attrs
        cfg, host = t.get("Config") or {}, t.get("HostConfig") or {}
        env = [e for e in (cfg.get("Env") or []) if not e.startswith("WORKER_ID=")] + [f"WORKER_ID={wid}"]
        labels = {k: v for k, v in (cfg.get("Labels") or {}).items() if not k.startswith("com.docker.compose.")}
        labels[LABEL_KEY] = LABEL_VAL
        kw = dict(name=name, detach=True, environment=env, network=self.s.docker_network, labels=labels,
                  restart_policy={"Name": "unless-stopped"})
        if host.get("Memory"):
            kw["mem_limit"] = host["Memory"]
        if host.get("NanoCpus"):
            kw["nano_cpus"] = host["NanoCpus"]
        if host.get("ExtraHosts"):
            kw["extra_hosts"] = host["ExtraHosts"]
        if cfg.get("Healthcheck"):
            kw["healthcheck"] = cfg["Healthcheck"]
        self.client.containers.run(self.s.extractor_image, **kw)
        return name, wid

    def remove(self, name: str):
        c = self.client.containers.get(name)
        if (c.labels or {}).get(LABEL_KEY) != LABEL_VAL:
            raise PermissionError(f"refusing to touch unmanaged container {name}")
        try:
            c.stop(timeout=15)
        except Exception:
            pass
        c.remove(force=True)
