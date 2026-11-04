import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "common"))
from vp_common.testing import setup_env, wait_ready  # noqa: E402

setup_env("ocr")

import pytest  # noqa: E402
from fastapi.testclient import TestClient  # noqa: E402


@pytest.fixture(scope="session")
def client():
    import ocr_service
    with TestClient(ocr_service.app) as c:
        wait_ready(c)
        yield c
