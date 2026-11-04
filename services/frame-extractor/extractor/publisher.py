"""Outbound side: Kafka only (contract 9.7: frames travel in `frames.raw`, no object store)."""
from __future__ import annotations

import json
import logging
import threading
import time
from typing import Any, Protocol

from .config import Settings
from .metrics import Metrics
from .models import Frame, frame_meta

log = logging.getLogger("publisher")

TOPIC_FRAMES = "frames.raw"
TOPIC_STATUS = "camera.status"
Headers = list[tuple[str, bytes]]

AVG_FRAME_KB = 200  # sizing assumption for the producer queue (960 px JPEG is typically 50-120 KB)
QUEUE_SECONDS = 2


class MessageBus(Protocol):
    def send(self, topic: str, key: str, value: bytes, headers: Headers | None = None) -> bool: ...
    def flush(self, timeout: float) -> None: ...  # blocking
    def close(self) -> None: ...


class KafkaBus:
    """confluent-kafka (librdkafka) producer.

    Chosen over aiokafka: batching/IO run in native threads and `produce()` is non-blocking and far
    cheaper in CPU than pure-Python aiokafka at ~1,200 msg/s. acks=1: frames are ephemeral live data.
    compression=none: JPEG is already compressed (lz4 only burns CPU). linger 10 ms.
    The producer queue is bounded to ~2 s of frames for this worker (capacity x 3 fps x 2 s x 200 KB);
    when full, `send` returns False and the caller counts a drop (live system: never block, never grow).
    """

    def __init__(self, s: Settings, metrics: Metrics) -> None:
        from confluent_kafka import Producer

        self._metrics = metrics
        queue_kb = max(8192, s.worker_capacity * 3 * QUEUE_SECONDS * AVG_FRAME_KB)
        self._p = Producer({
            "bootstrap.servers": s.kafka_bootstrap,
            "client.id": s.worker_id,
            "linger.ms": 10,
            "compression.type": "none",
            "acks": 1,
            "message.max.bytes": 2 * 1024 * 1024 + 4096,
            "queue.buffering.max.messages": 100000,
            "queue.buffering.max.kbytes": queue_kb,
            "message.timeout.ms": 10000,
            "socket.keepalive.enable": True,
            "log.connection.close": False,
        })
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._poll_loop, name="kafka-poll", daemon=True)
        self._thread.start()
        self._last_err_log = 0.0

    def _poll_loop(self) -> None:
        while not self._stop.is_set():
            self._p.poll(0.2)

    def _on_delivery(self, err: Any, msg: Any) -> None:
        if err is None:
            return
        if msg.topic() == TOPIC_FRAMES and msg.key():
            self._metrics.dropped.labels(msg.key().decode()).inc()
        self._metrics.errors.labels("kafka_delivery").inc()
        now = time.monotonic()
        if now - self._last_err_log > 10:
            self._last_err_log = now
            log.error("kafka delivery failed: %s", err)

    def send(self, topic: str, key: str, value: bytes, headers: Headers | None = None) -> bool:
        try:
            self._p.produce(topic, value, key=key.encode(), headers=headers, on_delivery=self._on_delivery)
            return True
        except BufferError:
            self._metrics.errors.labels("kafka_queue_full").inc()
            return False

    def flush(self, timeout: float) -> None:
        self._p.flush(timeout)

    def close(self) -> None:
        self.flush(5)
        self._stop.set()
        self._thread.join(timeout=2)


class FramePublisher:
    """Announce frames (JPEG in the value, meta in a header) and camera status."""

    def __init__(self, s: Settings, bus: MessageBus, metrics: Metrics) -> None:
        self._worker_id = s.worker_id
        self._bus, self._m = bus, metrics

    def publish_frame(self, f: Frame) -> bool:
        headers: Headers = [
            ("meta", json.dumps(frame_meta(f, self._worker_id), separators=(",", ":")).encode()),
            ("content-type", b"image/jpeg"),
        ]
        return self._bus.send(TOPIC_FRAMES, f.camera_id, f.data, headers)

    def publish_status(self, msg: dict[str, Any]) -> None:
        self._bus.send(TOPIC_STATUS, msg["camera_id"], json.dumps(msg, separators=(",", ":")).encode())

    def close(self) -> None:
        self._bus.close()
