import os
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "common"))
from vp_common.testing import setup_env, wait_ready  # noqa: E402

setup_env("face")

import pytest  # noqa: E402
from fastapi.testclient import TestClient  # noqa: E402


@pytest.fixture(scope="session")
def client(tmp_path_factory):
    os.environ["FACE_DATA_DIR"] = str(tmp_path_factory.mktemp("gallery"))
    import face_service
    with TestClient(face_service.app) as c:
        wait_ready(c)
        yield c
