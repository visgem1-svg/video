import cv2
import numpy as np

from vp_common.testing import burst, image_bytes


def post(client, name):
    return client.post("/read_text", files={"image": (name, image_bytes(name), "image/png")})


def test_reads_gate_sign(client):
    r = post(client, "text.png")
    assert r.status_code == 200
    body = r.json()
    assert body["count"] == len(body["lines"]) >= 1 and "ms" in body
    text = " ".join(l["text"] for l in body["lines"]).upper()
    assert "GATE" in text
    for l in body["lines"]:
        x1, y1, x2, y2 = l["box"]          # [x1,y1,x2,y2], not a 4-point polygon
        assert len(l["box"]) == 4 and x1 < x2 and y1 < y2
        assert 0 < l["confidence"] <= 1


def test_image_without_text(client):
    ok, png = cv2.imencode(".png", np.full((200, 300, 3), 200, np.uint8))
    r = client.post("/read_text", files={"image": ("blank.png", png.tobytes(), "image/png")})
    assert r.status_code == 200 and r.json()["count"] == 0


def test_bad_image(client):
    assert client.post("/read_text", files={"image": ("x", b"junk", "image/png")}).status_code == 400


def test_overload_returns_429(client):
    codes = burst(client.app, "/read_text", {"image": ("a.png", image_bytes("text.png"), "image/png")})
    assert set(codes) <= {200, 429} and 200 in codes and 429 in codes
