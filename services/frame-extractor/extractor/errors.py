"""RTSP/ffmpeg failure classification (contract 9.9 section 5). Pure functions, no I/O."""
from __future__ import annotations

import re

AUTH_FAILED = "auth_failed"
NOT_FOUND = "not_found"
CODEC_UNSUPPORTED = "codec_unsupported"
UNREACHABLE = "unreachable"
STALLED = "stalled"
DECODE_ERROR = "decode_error"
EOF_ = "eof"

# Retrying cannot help until the camera config (URL, credentials, substream) is changed.
TERMINAL = frozenset({AUTH_FAILED, NOT_FOUND, CODEC_UNSUPPORTED})

# First match wins. ffmpeg's exit code is useless here (8 for 400/401/404/"Protocol not found"), so only text counts.
# 'Protocol not found' (a URL scheme ffmpeg lacks) is filed under not_found: the URL cannot work until changed.
# 'Server returned 400' is what MediaMTX answers for a path that is not configured at all (404 = configured, no
# publisher), so both mean "this path does not exist".
_RULES: tuple[tuple[str, re.Pattern[str]], ...] = tuple(
    (cls, re.compile(rx, re.I)) for cls, rx in (
        (AUTH_FAILED, r"(returned|failed:) ?40[13]|unauthori[sz]ed|authorization failed|authentication failed|forbidden"),
        (NOT_FOUND, r"(returned|failed:) ?40[04]|protocol not found"),
        (CODEC_UNSUPPORTED, r"does not contain any stream|decoder \(codec .*\) not found|unsupported codec"
                            r"|unknown codec|failed to open codec|codec not currently supported"),
        (UNREACHABLE, r"connection refused|connection timed out|timed out|no route to host|network is unreachable"
                      r"|failed to resolve hostname|name or service not known|connection reset|host is down"
                      r"|temporary failure in name resolution|broken pipe"),
        (DECODE_ERROR, r"invalid data found|error while decoding|non-existing pps|invalid nal|corrupt|"
                       r"concealing|decode_slice_header|missing reference"),
        (EOF_, r"end of file|immediate exit requested"),
    )
)


def classify(stderr: str, rc: int | None = None) -> str | None:
    """Class of a failed ffmpeg run from the tail of its stderr; None = nothing recognisable and rc==0-ish.

    A run that exited with unrecognised text is `eof` when it exited cleanly or silently, else `decode_error`.
    """
    text = stderr or ""
    for cls, rx in _RULES:
        if rx.search(text):
            return cls
    if rc in (0, None) or not text.strip():
        return EOF_
    return DECODE_ERROR


def is_terminal(cls: str | None) -> bool:
    return cls in TERMINAL
