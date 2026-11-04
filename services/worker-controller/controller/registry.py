import httpx


class RegistryClient:
    def __init__(self, base_url: str):
        self.c = httpx.AsyncClient(base_url=base_url, timeout=5.0)

    async def cameras(self):
        r = await self.c.get("/api/cameras")
        r.raise_for_status()
        return r.json()

    async def workers(self):
        r = await self.c.get("/api/workers")
        r.raise_for_status()
        return r.json()

    async def drain(self, worker_id):
        (await self.c.post(f"/api/workers/{worker_id}/drain")).raise_for_status()

    async def undrain(self, worker_id):
        (await self.c.post(f"/api/workers/{worker_id}/undrain")).raise_for_status()

    async def close(self):
        await self.c.aclose()
