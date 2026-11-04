import random

from conftest import make_jpeg
from extractor.jpeg_parser import JpegStreamParser, jpeg_dimensions


def test_single_frame_and_dimensions():
    j = make_jpeg(960, 540)
    assert JpegStreamParser().feed(j) == [j]
    assert jpeg_dimensions(j) == (960, 540)


def test_merged_chunk_many_frames():
    frames = [make_jpeg(640, 360, bytes([i]) * 50) for i in range(5)]
    assert JpegStreamParser().feed(b"".join(frames)) == frames


def test_split_at_every_byte_boundary():
    frames = [make_jpeg(320, 240, b"\xff\xd8\x10" * 3), make_jpeg(320, 240, b"abc")]
    stream = b"".join(frames)
    for cut in range(1, len(stream)):
        p = JpegStreamParser()
        got = p.feed(stream[:cut]) + p.feed(stream[cut:])
        assert got == frames, f"cut={cut}"


def test_byte_by_byte_and_random_chunks():
    frames = [make_jpeg(100 + i, 50, bytes(range(40))) for i in range(6)]
    stream = b"".join(frames)
    p = JpegStreamParser()
    got = []
    for b in stream:
        got += p.feed(bytes([b]))
    assert got == frames
    rng = random.Random(1)
    p = JpegStreamParser()
    got, i = [], 0
    while i < len(stream):
        n = rng.randint(1, 200)
        got += p.feed(stream[i:i + n])
        i += n
    assert got == frames
    assert p.buffered == 0


def test_garbage_before_between_and_after():
    a, b = make_jpeg(10, 10), make_jpeg(20, 20)
    p = JpegStreamParser()
    got = p.feed(b"\x00\x11garbage" + a + b"\xff\x00junk\xff" + b + b"trailing")
    assert got == [a, b]
    assert p.resyncs >= 2
    # nothing but garbage never grows the buffer
    p = JpegStreamParser()
    for _ in range(100):
        p.feed(b"x" * 1000)
    assert p.buffered <= 1


def test_trailing_ff_kept_for_split_soi():
    j = make_jpeg()
    p = JpegStreamParser()
    assert p.feed(b"junk\xff") == []
    assert p.feed(j[1:]) == [j]


def test_runaway_frame_resyncs():
    p = JpegStreamParser(max_frame_bytes=1000)
    assert p.feed(b"\xff\xd8" + b"a" * 5000) == []
    assert p.buffered == 0 and p.resyncs == 1
    j = make_jpeg()
    assert p.feed(j) == [j]


def test_dimensions_invalid():
    assert jpeg_dimensions(b"") is None
    assert jpeg_dimensions(b"\xff\xd8\xff\xd9") is None
    assert jpeg_dimensions(b"not a jpeg at all") is None
