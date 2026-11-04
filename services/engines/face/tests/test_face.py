import json

import cv2
import numpy as np

from gallery import Gallery
from vp_common.testing import VP_ROOT, burst, image_bytes


def img(name):
    return {"image": (name, image_bytes(name), "image/jpeg")}


def test_detect_one_face_in_messi(client):
    r = client.post("/detect_faces", files=img("messi.jpg"))
    assert r.status_code == 200
    body = r.json()
    assert body["count"] == len(body["faces"]) == 1 and "ms" in body
    f = body["faces"][0]
    assert len(f["box"]) == 4 and f["box"][0] < f["box"][2] and 0.8 <= f["confidence"] <= 1
    assert f["person_id"] is None and f["person_name"] is None and f["score"] is None


def test_enroll_identify_unknown_delete(client):
    assert client.get("/persons").json()["count"] == 0
    r = client.post("/persons", data={"name": "Lena"}, files=img("lena.jpg"))
    assert r.status_code == 200
    pid = r.json()["person_id"]
    assert r.json()["name"] == "Lena"

    listing = client.get("/persons").json()
    assert listing["count"] == 1 and listing["persons"][0]["person_id"] == pid
    row = listing["persons"][0]
    assert row["photos"] == 1 and row["name"] == "Lena" and row["created_at"] > 0
    assert row["photo_url"] == f"/api/persons/{pid}/photo"
    ph = client.get(f"/persons/{pid}/photo")
    assert ph.status_code == 200 and ph.headers["content-type"] == "image/jpeg" and ph.content[:2] == bytes([255, 216])

    same = client.post("/identify_faces", files=img("lena_flip.jpg")).json()
    assert same["count"] == 1
    f = same["faces"][0]
    assert f["person_id"] == pid and f["person_name"] == "Lena" and f["score"] >= 0.363

    other = client.post("/identify_faces", files=img("messi.jpg")).json()["faces"][0]
    assert other["person_id"] is None and other["person_name"] is None
    assert other["score"] is not None and other["score"] < 0.363

    strict = client.post("/identify_faces", files=img("lena_flip.jpg"), data={"threshold": "0.999"}).json()
    assert strict["faces"][0]["person_id"] is None

    assert client.delete(f"/persons/{pid}").status_code == 200
    assert client.delete(f"/persons/{pid}").status_code == 404
    assert client.get("/persons").json()["count"] == 0
    after = client.post("/identify_faces", files=img("lena_flip.jpg")).json()["faces"][0]
    assert after["person_id"] is None and after["score"] is None


def test_gallery_persists_to_disk(client):
    pid = client.post("/persons", data={"name": "Persisted"}, files=img("lena.jpg")).json()["person_id"]
    g = Gallery(client.app.state.settings.data_dir)   # a fresh process would do exactly this
    g.load()
    assert [p.person_id for p in g.list()] == [pid]
    assert g.search(np.ones(128, "float32"))[0] == pid
    client.delete(f"/persons/{pid}")


def test_enroll_errors(client):
    r = client.post("/persons", data={"name": "Nobody"},
                    files={"image": ("t.png", image_bytes("text.png"), "image/png")})
    assert r.status_code == 400 and "no face" in r.json()["detail"]
    assert client.post("/persons", files=img("lena.jpg")).status_code == 422  # name missing


def test_overload_returns_429(client):
    codes = burst(client.app, "/detect_faces", img("messi.jpg"))
    assert set(codes) <= {200, 429} and 200 in codes and 429 in codes


def _two_faces_jpeg():
    a, b = cv2.imread(str(VP_ROOT / "test-images" / "lena.jpg")), cv2.imread(str(VP_ROOT / "test-images" / "messi.jpg"))
    h = 400
    a, b = (cv2.resize(x, (int(x.shape[1] * h / x.shape[0]), h)) for x in (a, b))
    return cv2.imencode(".jpg", np.hstack([a, b]))[1].tobytes()


def test_library_photos_rename_multiface(client):
    pid = client.post("/persons", data={"name": "Messi"}, files=img("messi.jpg")).json()["person_id"]

    r = client.post("/persons", data={"name": "Two"}, files={"image": ("t.jpg", _two_faces_jpeg(), "image/jpeg")})
    assert r.status_code == 400 and "2 faces" in r.json()["detail"]
    r = client.post(f"/persons/{pid}/photos", files={"image": ("t.jpg", _two_faces_jpeg(), "image/jpeg")})
    assert r.status_code == 400

    # add a second photo (lena: deliberately a different person to prove max-over-photos matching)
    r = client.post(f"/persons/{pid}/photos", files=img("lena.jpg"))
    assert r.status_code == 200 and r.json()["photos"] == 2 and r.json()["person_id"] == pid
    assert client.post("/persons/nope/photos", files=img("lena.jpg")).status_code == 404
    assert client.post(f"/persons/{pid}/photos", files={"image": ("t.png", image_bytes("text.png"), "image/png")}).status_code == 400

    # identify matches through either photo
    for name in ("messi.jpg", "lena_flip.jpg"):
        f = client.post("/identify_faces", files=img(name)).json()["faces"][0]
        assert f["person_id"] == pid and f["score"] >= 0.363

    r = client.put(f"/persons/{pid}", json={"name": "  Leo  "})
    assert r.status_code == 200 and r.json()["name"] == "Leo"
    assert client.get("/persons").json()["persons"][0]["name"] == "Leo"
    assert client.post("/identify_faces", files=img("messi.jpg")).json()["faces"][0]["person_name"] == "Leo"
    assert client.put(f"/persons/{pid}", json={"name": ""}).status_code == 422
    assert client.put("/persons/nope", json={"name": "x"}).status_code == 404
    assert client.get("/persons/nope/photo").status_code == 404

    assert client.delete(f"/persons/{pid}").status_code == 200
    assert client.get(f"/persons/{pid}/photo").status_code == 404


def test_old_gallery_format_still_loads(tmp_path):
    emb = np.random.default_rng(1).normal(size=128).tolist()
    (tmp_path / "gallery.json").write_text(json.dumps({"version": 1, "persons": [
        {"person_id": "old-1", "name": "Lena", "created_at": 1, "thumbnail": "", "embedding": emb}]}))
    g = Gallery(tmp_path)
    g.load()
    assert [(p.person_id, p.photos) for p in g.list()] == [("old-1", 1)]
    assert g.search(np.array(emb))[0] == "old-1"
    g.add_photo("old-1", np.ones(128))
    g2 = Gallery(tmp_path)
    g2.load()
    assert g2.list()[0].photos == 2
