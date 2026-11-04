"""Entry point: `python -m vp_common.serve <module:app> <default_port>`; port from env PORT."""
from __future__ import annotations

import os
import sys

import uvicorn


def main() -> None:
    target, default_port = sys.argv[1], int(sys.argv[2])
    uvicorn.run(target, host="0.0.0.0", port=int(os.environ.get("PORT") or default_port),
                workers=1,  # models are loaded once per process; scale with replicas
                access_log=False, timeout_graceful_shutdown=20, log_config=None)


if __name__ == "__main__":
    main()
