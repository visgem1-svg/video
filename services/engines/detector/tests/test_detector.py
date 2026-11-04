from vp_common.testing import burst, image_bytes


def post(client, name, **form):
    return client.post("/detect_objects", files={"image": (name, image_bytes(name), "image/jpeg")}, data=form)


def classes_of(r):
    return [o["class"] for o in r.json()["objects"]]


def test_street_has_bus_and_four_people(client):
    r = post(client, "street.jpg")
    assert r.status_code == 200
    body = r.json()
    assert {"objects", "count", "ms"} <= body.keys() and body["count"] == len(body["objects"])
    cls = classes_of(r)
    assert "bus" in cls
    assert cls.count("person") >= 4
    o = body["objects"][0]
    assert len(o["box"]) == 4 and o["box"][0] < o["box"][2] and o["box"][1] < o["box"][3]


def test_car_found(client):
    assert "car" in classes_of(post(client, "car.jpg"))


def test_classes_filter(client):
    r = post(client, "street.jpg", classes="person")
    assert set(classes_of(r)) == {"person"} and r.json()["count"] >= 4
    r = post(client, "street.jpg", classes="Bus, truck")
    assert set(classes_of(r)) <= {"bus", "truck"} and "bus" in classes_of(r)


def test_min_confidence(client):
    low = post(client, "street.jpg", min_confidence="0.2").json()["count"]
    high = post(client, "street.jpg", min_confidence="0.9").json()["count"]
    assert high <= low


def test_bad_inputs(client):
    assert post(client, "street.jpg", classes="spaceship").status_code == 400
    assert post(client, "street.jpg", min_confidence="2").status_code == 422
    r = client.post("/detect_objects", files={"image": ("x.jpg", b"not an image", "image/jpeg")})
    assert r.status_code == 400
    assert client.post("/detect_objects").status_code == 422


def test_metrics_and_health(client):
    post(client, "car.jpg")
    m = client.get("/metrics").text
    assert 'vp_engine_requests_total{status="ok",tool="detect_objects"}' in m
    assert "vp_engine_inference_seconds_bucket" in m and "vp_engine_inflight" in m
    assert client.get("/health").status_code == 200


def test_overload_returns_429_and_not_ready_503(client):
    codes = burst(client.app, "/detect_objects", {"image": ("a.jpg", image_bytes("street.jpg"), "image/jpeg")})
    assert set(codes) <= {200, 429} and 200 in codes and 429 in codes
    rt = client.app.state.runtime
    rt.ready = False
    try:
        assert post(client, "car.jpg").status_code == 503
        assert client.get("/health").status_code == 503
    finally:
        rt.ready = True
