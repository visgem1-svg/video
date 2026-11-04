from vp_common.testing import burst, image_bytes


def post(client, name):
    return client.post("/read_plate", files={"image": (name, image_bytes(name), "image/jpeg")})


def test_reads_plate(client):
    r = post(client, "car.jpg")
    assert r.status_code == 200
    body = r.json()
    assert body["count"] == len(body["plates"]) >= 1 and "ms" in body
    p = body["plates"][0]
    assert p["plate"] == "PL8REC"
    assert 0 < p["confidence"] <= 1 and 0 < p["detection_confidence"] <= 1
    assert len(p["box"]) == 4 and p["box"][0] < p["box"][2]


def test_no_plate(client):
    assert post(client, "messi.jpg").json()["count"] == 0


def test_bad_image(client):
    r = client.post("/read_plate", files={"image": ("x", b"junk", "image/jpeg")})
    assert r.status_code == 400


def test_metrics(client):
    post(client, "car.jpg")
    assert 'vp_engine_requests_total{status="ok",tool="read_plate"}' in client.get("/metrics").text


def test_overload_returns_429(client):
    codes = burst(client.app, "/read_plate", {"image": ("a.jpg", image_bytes("car.jpg"), "image/jpeg")})
    assert set(codes) <= {200, 429} and 200 in codes and 429 in codes


def test_filter_fields_and_edge_rejection(client):
    import cv2
    p = post(client, "car.jpg").json()["plates"][0]
    assert p["confidence"] == min(p["char_confidences"]) and len(p["char_confidences"]) == len(p["plate"])
    # shift the image so the plate is cut by the right border: the truncated read must be rejected
    img = cv2.imdecode(__import__("numpy").frombuffer(image_bytes("car.jpg"), "uint8"), 1)
    x1, x2 = p["box"][0], p["box"][2]
    cut = img[:, : x1 + (x2 - x1) // 2]
    ok, jpg = cv2.imencode(".jpg", cut)
    r = client.post("/read_plate", files={"image": ("c.jpg", jpg.tobytes(), "image/jpeg")}).json()
    assert all(q["plate"] == "PL8REC" for q in r["plates"])
    m = client.get("/metrics").text
    assert 'vp_engine_plate_rejected_total{reason="cropped_at_edge"}' in m


def test_min_chars_and_confidence_filters(client):
    import numpy as np
    from types import SimpleNamespace as NS
    import lpr_service as L
    box = NS(x1=100, y1=100, x2=200, y2=150)
    def fake(text, confs):
        return NS(detection=NS(bounding_box=box, confidence=0.9), ocr=NS(text=text, confidence=confs))
    class A:
        def __init__(self, r): self.r = r
        def predict(self, img): return [self.r]
    img = np.zeros((400, 600, 3), np.uint8)
    assert L._read(A(fake("PL8REC", [1.0] * 6)), img)[0]["plate"] == "PL8REC"
    assert L._read(A(fake("PL8REC", [1, 1, .5, 1, 1, 1])), img) == []          # min char conf < 0.7
    assert L._read(A(fake("PL8", [1, 1, 1])), img) == []                        # shorter than 4
    assert L._read(A(fake("PL8R__", [1] * 6)), img)[0]["plate"] == "PL8R"       # padding stripped
    before = L.rejected.labels("low_confidence")._value.get()
    L._read(A(fake("ABCDEF", [.1] * 6)), img)
    assert L.rejected.labels("low_confidence")._value.get() == before + 1
