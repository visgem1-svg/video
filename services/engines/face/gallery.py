"""Face gallery: several SFace embeddings per person + a thumbnail, persisted as one JSON file.

File format (backward compatible): each person has `embedding` (first photo, the v1 field) and, since v1.4,
`embeddings` (all photos). Old files with only `embedding` load as one photo.
Identify = max cosine over every stored embedding (one numpy matmul).
"""
from __future__ import annotations

import base64
import json
import logging
import os
import threading
import time
import uuid
from dataclasses import dataclass
from pathlib import Path

import numpy as np

log = logging.getLogger("vp.face.gallery")
EMB_DIM = 128


@dataclass(frozen=True)
class Person:
    person_id: str
    name: str
    created_at: int      # epoch ms
    thumbnail_b64: str   # small JPEG, base64
    embeddings: tuple = ()   # tuple of normalised float32 arrays

    @property
    def photos(self) -> int:
        return len(self.embeddings)


class Gallery:
    def __init__(self, directory: Path):
        self.path = directory / "gallery.json"
        self._lock = threading.Lock()
        self._persons: list[Person] = []
        # (matrix (M,128) one row per photo, owner person_id per row, person_id -> name); swapped atomically
        self._snap = (np.zeros((0, EMB_DIM), np.float32), [], {})

    @staticmethod
    def _normalise(v) -> np.ndarray:
        v = np.asarray(v, np.float32).reshape(-1)
        n = float(np.linalg.norm(v))
        return v / n if n > 0 else v

    # ---- persistence -------------------------------------------------------------------------
    def load(self) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        if not self.path.exists():
            return
        doc = json.loads(self.path.read_text(encoding="utf-8"))  # corrupt file -> startup fails loudly
        persons = []
        for p in doc.get("persons", []):
            raw = p.get("embeddings") or [p["embedding"]]
            persons.append(Person(p["person_id"], p["name"], int(p["created_at"]), p.get("thumbnail", ""),
                                  tuple(self._normalise(e) for e in raw)))
        self._persons = persons
        self._rebuild()
        log.info("gallery loaded", extra={"persons": len(persons)})

    def _rebuild(self) -> None:
        rows, owners = [], []
        for p in self._persons:
            for e in p.embeddings:
                rows.append(e)
                owners.append(p.person_id)
        matrix = np.stack(rows).astype(np.float32) if rows else np.zeros((0, EMB_DIM), np.float32)
        self._snap = (matrix, owners, {p.person_id: p.name for p in self._persons})

    def _save(self) -> None:
        r6 = lambda e: [round(float(x), 6) for x in e]
        doc = {"version": 2, "persons": [
            {"person_id": p.person_id, "name": p.name, "created_at": p.created_at, "thumbnail": p.thumbnail_b64,
             "embedding": r6(p.embeddings[0]), "embeddings": [r6(e) for e in p.embeddings]}
            for p in self._persons]}
        tmp = self.path.with_suffix(".tmp")
        tmp.write_text(json.dumps(doc), encoding="utf-8")
        os.replace(tmp, self.path)  # atomic

    # ---- operations ----------------------------------------------------------------------------
    def add(self, name: str, embedding, thumbnail_jpeg: bytes) -> Person:
        person = Person(str(uuid.uuid4()), name, int(time.time() * 1000),
                        base64.b64encode(thumbnail_jpeg).decode(), (self._normalise(embedding),))
        with self._lock:
            self._persons = self._persons + [person]
            self._rebuild()
            self._save()
        return person

    def add_photo(self, person_id: str, embedding) -> Person | None:
        with self._lock:
            for i, p in enumerate(self._persons):
                if p.person_id == person_id:
                    new = Person(p.person_id, p.name, p.created_at, p.thumbnail_b64,
                                 p.embeddings + (self._normalise(embedding),))
                    self._persons = self._persons[:i] + [new] + self._persons[i + 1:]
                    self._rebuild()
                    self._save()
                    return new
        return None

    def rename(self, person_id: str, name: str) -> Person | None:
        with self._lock:
            for i, p in enumerate(self._persons):
                if p.person_id == person_id:
                    new = Person(p.person_id, name, p.created_at, p.thumbnail_b64, p.embeddings)
                    self._persons = self._persons[:i] + [new] + self._persons[i + 1:]
                    self._rebuild()
                    self._save()
                    return new
        return None

    def delete(self, person_id: str) -> bool:
        with self._lock:
            keep = [p for p in self._persons if p.person_id != person_id]
            if len(keep) == len(self._persons):
                return False
            self._persons = keep
            self._rebuild()
            self._save()
        return True

    def get(self, person_id: str) -> Person | None:
        return next((p for p in self._persons if p.person_id == person_id), None)

    def list(self) -> list[Person]:
        return list(self._persons)

    def __len__(self) -> int:
        return len(self._snap[1])

    def search(self, embedding) -> tuple[str | None, str | None, float | None]:
        """Best cosine over all photos -> (person_id, name, score); (None, None, None) if empty."""
        matrix, owners, names = self._snap
        if len(owners) == 0:
            return None, None, None
        scores = matrix @ self._normalise(embedding)
        i = int(scores.argmax())
        return owners[i], names[owners[i]], float(scores[i])
