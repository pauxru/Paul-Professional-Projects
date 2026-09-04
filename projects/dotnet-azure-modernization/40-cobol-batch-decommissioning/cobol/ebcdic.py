"""EBCDIC <-> ASCII translation.

The tables are built explicitly rather than taken from :mod:`codecs`. Two
reasons, and the second is the one that matters.

First, ``bytes.decode("cp037")`` raises on nothing, because CP037 is a complete
single-byte mapping — every one of the 256 values means something. That sounds
convenient and it means a corrupt record decodes silently into plausible text.

Second, and this is the part that bites people: **there is no such thing as
"EBCDIC".** There are dozens of code pages that agree on A-Z, a-z and 0-9 and
disagree on exactly the characters that appear in real business data. CP037 (US)
and CP500 (International) differ on ``[``, ``]``, ``!``, ``^`` and ``~``. A file
written on a European mainframe and read with a US table produces
``ACCOUNT!123`` where the original said ``ACCOUNT[123`` — and nothing errors.

Making the code page an explicit, required argument is the whole point of this
module.
"""

from __future__ import annotations

from typing import Dict

# CP037 (IBM-037, US/Canada). Index = EBCDIC byte, value = Unicode code point.
_CP037 = (
    "\x00\x01\x02\x03\x9c\t\x86\x7f\x97\x8d\x8e\x0b\x0c\r\x0e\x0f"
    "\x10\x11\x12\x13\x9d\x85\x08\x87\x18\x19\x92\x8f\x1c\x1d\x1e\x1f"
    "\x80\x81\x82\x83\x84\n\x17\x1b\x88\x89\x8a\x8b\x8c\x05\x06\x07"
    "\x90\x91\x16\x93\x94\x95\x96\x04\x98\x99\x9a\x9b\x14\x15\x9e\x1a"
    " \xa0\xe2\xe4\xe0\xe1\xe3\xe5\xe7\xf1\xa2.<(+|"
    "&\xe9\xea\xeb\xe8\xed\xee\xef\xec\xdf!$*);\xac"
    "-/\xc2\xc4\xc0\xc1\xc3\xc5\xc7\xd1\xa6,%_>?"
    "\xf8\xc9\xca\xcb\xc8\xcd\xce\xcf\xcc`:#@'=\""
    "\xd8abcdefghi\xab\xbb\xf0\xfd\xfe\xb1"
    "\xb0jklmnopqr\xaa\xba\xe6\xb8\xc6\xa4"
    "\xb5~stuvwxyz\xa1\xbf\xd0\xdd\xde\xae"
    "^\xa3\xa5\xb7\xa9\xa7\xb6\xbc\xbd\xbe[]\xaf\xa8\xb4\xd7"
    "{ABCDEFGHI\xad\xf4\xf6\xf2\xf3\xf5"
    "}JKLMNOPQR\xb9\xfb\xfc\xf9\xfa\xff"
    "\\\xf7STUVWXYZ\xb2\xd4\xd6\xd2\xd3\xd5"
    "0123456789\xb3\xdb\xdc\xd9\xda\x9f"
)

# CP500 (IBM-500, International). Differs from CP037 in exactly five positions,
# which is precisely enough to corrupt data without breaking anything.
_CP500_OVERRIDES: Dict[int, str] = {
    0x4A: "[",
    0x4F: "!",
    0x5A: "]",
    0x5F: "^",
    0xA1: "~",
    0xB0: "\xa2",
    0xB1: "\xa3",
    0xBA: "\xa6",
    0xBB: "\xac",
}

_TABLES: Dict[str, str] = {}


def _build(name: str) -> str:
    if name == "cp037":
        return _CP037
    if name == "cp500":
        chars = list(_CP037)
        for i, c in _CP500_OVERRIDES.items():
            chars[i] = c
        return "".join(chars)
    raise ValueError(
        f"unknown EBCDIC code page {name!r}; "
        "supported: cp037 (US), cp500 (International)"
    )


def table(code_page: str) -> str:
    """Returns the 256-character decode table for ``code_page``."""
    key = code_page.lower().replace("-", "").replace("ibm", "cp")
    if key not in _TABLES:
        _TABLES[key] = _build(key)
    return _TABLES[key]


def to_text(data: bytes, code_page: str) -> str:
    t = table(code_page)
    return "".join(t[b] for b in data)


def from_text(text: str, code_page: str) -> bytes:
    t = table(code_page)
    rev = {c: i for i, c in enumerate(t)}
    out = bytearray()
    for ch in text:
        if ch not in rev:
            raise ValueError(
                f"character {ch!r} (U+{ord(ch):04X}) has no representation in {code_page}"
            )
        out.append(rev[ch])
    return bytes(out)


def code_pages_differ_on(a: str, b: str) -> Dict[int, str]:
    """Returns the byte values where two code pages disagree.

    Used by the migration report: if a file contains any of these bytes, the
    code page is not a detail you can guess at.
    """
    ta, tb = table(a), table(b)
    return {i: f"{ta[i]!r} vs {tb[i]!r}" for i in range(256) if ta[i] != tb[i]}


# EBCDIC digits are 0xF0-0xF9 in every code page in the family, which is why
# numeric handling can ignore the code page entirely.
EBCDIC_ZERO = 0xF0
EBCDIC_SPACE = 0x40
