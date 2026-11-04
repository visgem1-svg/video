"""Incremental JPEG frame splitter for an MJPEG byte stream (SOI/EOI markers).

JPEG entropy-coded data byte-stuffs 0xFF as FF00 and RSTn are FFD0-D7, so the
pair FF D9 only appears as a real EOI. ffmpeg's mjpeg output carries no
embedded thumbnails, hence no nested SOI/EOI.

Efficiency: one bytearray, a persistent scan offset (old bytes are never
rescanned), one slice copy per emitted frame, one compaction per ``feed``.
"""
from __future__ import annotations

SOI = b"\xff\xd8"
EOI = b"\xff\xd9"


class JpegStreamParser:
    def __init__(self, max_frame_bytes: int = 8 * 1024 * 1024) -> None:
        self._buf = bytearray()
        self._scan = 0  # offset before which bytes were already examined
        self._start = -1  # offset of the current SOI, or -1 when between frames
        self._max = max_frame_bytes
        self.resyncs = 0  # garbage-skip / oversize events

    def feed(self, data: bytes | bytearray | memoryview) -> list[bytes]:
        buf = self._buf
        buf += data
        out: list[bytes] = []
        consumed = 0
        while True:
            if self._start < 0:
                i = buf.find(SOI, self._scan)
                if i < 0:
                    # everything is garbage, except a trailing FF (maybe half an SOI)
                    keep_from = len(buf) - 1 if buf.endswith(b"\xff") else len(buf)
                    if keep_from > consumed:
                        self.resyncs += 1
                    consumed = max(consumed, keep_from)
                    self._scan = consumed
                    break
                if i > consumed:
                    self.resyncs += 1
                self._start = i
                self._scan = i + 2
            j = buf.find(EOI, self._scan)
            if j < 0:
                # EOI may be split across chunks: re-examine the last byte next time
                self._scan = max(self._start + 2, len(buf) - 1)
                if len(buf) - self._start > self._max:  # runaway frame: resync
                    self.resyncs += 1
                    consumed = len(buf)
                    self._start = -1
                    self._scan = consumed
                break
            end = j + 2
            out.append(bytes(buf[self._start:end]))
            consumed = end
            self._start = -1
            self._scan = end
        if consumed:
            del buf[:consumed]
            self._scan -= consumed
            if self._start >= 0:
                self._start -= consumed
        return out

    @property
    def buffered(self) -> int:
        return len(self._buf)


_SOF = {0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF}


def jpeg_dimensions(data: bytes) -> tuple[int, int] | None:
    """Return (width, height) from the SOF segment, or None if not parseable."""
    n = len(data)
    if n < 4 or data[0] != 0xFF or data[1] != 0xD8:
        return None
    p = 2
    while p + 4 <= n:
        if data[p] != 0xFF:
            return None
        m = data[p + 1]
        if m == 0xFF:  # fill byte
            p += 1
            continue
        if m in _SOF:
            if p + 9 > n:
                return None
            h = (data[p + 5] << 8) | data[p + 6]
            w = (data[p + 7] << 8) | data[p + 8]
            return (w, h) if w and h else None
        if m in (0xD9, 0xDA):  # EOI / SOS before SOF: malformed
            return None
        if m == 0x01 or 0xD0 <= m <= 0xD8:
            p += 2
            continue
        seglen = (data[p + 2] << 8) | data[p + 3]
        if seglen < 2:
            return None
        p += 2 + seglen
    return None
