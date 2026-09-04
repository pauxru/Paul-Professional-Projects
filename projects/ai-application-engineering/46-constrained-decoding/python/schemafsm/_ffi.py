"""ctypes bindings for the constrained-decoding core.

ctypes is used deliberately in preference to pybind11: the C ABI is the thing
worth demonstrating, and ctypes means the Python package has no build step and
no compiler dependency of its own. The cost is that every argument type has to
be declared by hand, which is exactly the discipline the boundary requires.
"""

from __future__ import annotations

import ctypes
import os
import sys
from pathlib import Path
from typing import Iterable, Sequence

_ERR_LEN = 512


def _candidate_library_paths() -> list[Path]:
    if override := os.environ.get("CDEC_LIBRARY"):
        return [Path(override)]
    root = Path(__file__).resolve().parents[2]
    name = "cdec.dll" if sys.platform == "win32" else "libcdec.so"
    if sys.platform == "darwin":
        name = "libcdec.dylib"
    return [
        root / "build" / "lib" / name,
        root / "build" / name,
        root / "build" / "lib" / "Release" / name,
    ]


class LibraryNotBuilt(RuntimeError):
    """Raised when the native library has not been compiled yet."""


def _load() -> ctypes.CDLL:
    tried = _candidate_library_paths()
    for path in tried:
        if path.exists():
            return ctypes.CDLL(str(path))
    raise LibraryNotBuilt(
        "native library not found; run build.ps1 first. Looked in:\n  "
        + "\n  ".join(str(p) for p in tried)
    )


_lib: ctypes.CDLL | None = None


def lib() -> ctypes.CDLL:
    global _lib
    if _lib is None:
        _lib = _load()
        _declare(_lib)
    return _lib


def _declare(l: ctypes.CDLL) -> None:
    c_engine = ctypes.c_void_p
    l.cdec_create.restype = c_engine
    l.cdec_create.argtypes = [ctypes.c_char_p, ctypes.c_int, ctypes.c_int, ctypes.c_char_p, ctypes.c_size_t]
    l.cdec_destroy.restype = None
    l.cdec_destroy.argtypes = [c_engine]
    l.cdec_set_vocab.restype = ctypes.c_int
    l.cdec_set_vocab.argtypes = [c_engine, ctypes.c_char_p, ctypes.POINTER(ctypes.c_int32), ctypes.c_int32, ctypes.c_int32]
    for name in ("cdec_start_state", "cdec_dfa_states", "cdec_dfa_states_premin", "cdec_nfa_states",
                 "cdec_cached_states", "cdec_trie_nodes", "cdec_mask_words"):
        getattr(l, name).restype = ctypes.c_int32
        getattr(l, name).argtypes = [c_engine]
    l.cdec_advance_token.restype = ctypes.c_int32
    l.cdec_advance_token.argtypes = [c_engine, ctypes.c_int32, ctypes.c_int32]
    l.cdec_advance_bytes.restype = ctypes.c_int32
    l.cdec_advance_bytes.argtypes = [c_engine, ctypes.c_int32, ctypes.c_char_p, ctypes.c_int32]
    for name in ("cdec_is_accepting", "cdec_eos_allowed"):
        getattr(l, name).restype = ctypes.c_int
        getattr(l, name).argtypes = [c_engine, ctypes.c_int32]
    l.cdec_matches.restype = ctypes.c_int
    l.cdec_matches.argtypes = [c_engine, ctypes.c_char_p, ctypes.c_int32]
    l.cdec_mask.restype = ctypes.c_int32
    l.cdec_mask.argtypes = [c_engine, ctypes.c_int32, ctypes.POINTER(ctypes.c_uint64), ctypes.c_int32]
    l.cdec_apply_mask.restype = None
    l.cdec_apply_mask.argtypes = [ctypes.POINTER(ctypes.c_uint64), ctypes.c_int32,
                                  ctypes.POINTER(ctypes.c_float),
                                  ctypes.c_int32, ctypes.c_int, ctypes.c_int32]
    l.cdec_diagnostics.restype = ctypes.c_int32
    l.cdec_diagnostics.argtypes = [c_engine, ctypes.c_char_p, ctypes.c_int32]
    l.cdec_mask_stats.restype = None
    l.cdec_mask_stats.argtypes = [c_engine] + [ctypes.POINTER(ctypes.c_uint64)] * 4
    l.cdec_reset_stats.restype = None
    l.cdec_reset_stats.argtypes = [c_engine]
    l.cdec_valid_json.restype = ctypes.c_int
    l.cdec_valid_json.argtypes = [ctypes.c_char_p, ctypes.c_int32]
    l.cdec_version.restype = ctypes.c_char_p
    l.cdec_version.argtypes = []


class SchemaError(ValueError):
    """The schema could not be compiled into a finite automaton."""


DEAD = -1


class Engine:
    """A compiled schema plus an installed vocabulary.

    Owns a native handle. Use as a context manager, or rely on __del__; the
    handle is released exactly once either way.
    """

    def __init__(self, schema_json: str, *, allow_whitespace: bool = False,
                 require_property_order: bool = False) -> None:
        l = lib()
        err = ctypes.create_string_buffer(_ERR_LEN)
        handle = l.cdec_create(schema_json.encode("utf-8"), int(allow_whitespace),
                               int(require_property_order), err, _ERR_LEN)
        if not handle:
            raise SchemaError(err.value.decode("utf-8", "replace"))
        self._handle: int | None = handle
        self._tokens: list[bytes] = []
        self._eos_id = -1

    # -- lifetime ------------------------------------------------------
    def close(self) -> None:
        if self._handle is not None:
            lib().cdec_destroy(ctypes.c_void_p(self._handle))
            self._handle = None

    def __enter__(self) -> "Engine":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()

    def __del__(self) -> None:
        try:
            self.close()
        except Exception:  # pragma: no cover - interpreter teardown
            pass

    @property
    def _h(self) -> ctypes.c_void_p:
        if self._handle is None:
            raise RuntimeError("engine has been closed")
        return ctypes.c_void_p(self._handle)

    # -- vocabulary ----------------------------------------------------
    def set_vocabulary(self, tokens: Sequence[bytes | str], eos_token_id: int = -1) -> None:
        encoded = [t.encode("utf-8") if isinstance(t, str) else bytes(t) for t in tokens]
        blob = b"".join(encoded)
        lengths = (ctypes.c_int32 * len(encoded))(*[len(t) for t in encoded])
        rc = lib().cdec_set_vocab(self._h, blob, lengths, len(encoded), eos_token_id)
        if rc != 0:
            raise RuntimeError("cdec_set_vocab failed")
        self._tokens = encoded
        self._eos_id = eos_token_id

    @property
    def tokens(self) -> list[bytes]:
        return list(self._tokens)

    @property
    def eos_token_id(self) -> int:
        return self._eos_id

    # -- automaton -----------------------------------------------------
    @property
    def start_state(self) -> int:
        return lib().cdec_start_state(self._h)

    def advance_token(self, state: int, token_id: int) -> int:
        return lib().cdec_advance_token(self._h, state, token_id)

    def advance_bytes(self, state: int, data: bytes) -> int:
        return lib().cdec_advance_bytes(self._h, state, data, len(data))

    def is_accepting(self, state: int) -> bool:
        return bool(lib().cdec_is_accepting(self._h, state))

    def eos_allowed(self, state: int) -> bool:
        return bool(lib().cdec_eos_allowed(self._h, state))

    def matches(self, text: str | bytes) -> bool:
        data = text.encode("utf-8") if isinstance(text, str) else text
        return bool(lib().cdec_matches(self._h, data, len(data)))

    # -- masks ---------------------------------------------------------
    def mask_words(self) -> int:
        return lib().cdec_mask_words(self._h)

    def allowed_token_ids(self, state: int) -> list[int]:
        words = self.mask_words()
        if words < 0:
            raise RuntimeError("vocabulary has not been installed")
        buf = (ctypes.c_uint64 * words)()
        count = lib().cdec_mask(self._h, state, buf, words)
        if count < 0:
            raise RuntimeError("cdec_mask failed")
        allowed = []
        for i in range(len(self._tokens)):
            if (buf[i >> 6] >> (i & 63)) & 1:
                allowed.append(i)
        return allowed

    def mask_words_buffer(self, state: int) -> "ctypes.Array[ctypes.c_uint64]":
        words = self.mask_words()
        buf = (ctypes.c_uint64 * words)()
        if lib().cdec_mask(self._h, state, buf, words) < 0:
            raise RuntimeError("cdec_mask failed")
        return buf

    def apply_mask(self, state: int, logits: Iterable[float]) -> list[float]:
        values = list(logits)
        buf = self.mask_words_buffer(state)
        arr = (ctypes.c_float * len(values))(*values)
        lib().cdec_apply_mask(buf, len(buf), arr, len(values),
                              int(self.eos_allowed(state)), self._eos_id)
        return list(arr)

    # -- introspection -------------------------------------------------
    @property
    def dfa_states(self) -> int:
        return lib().cdec_dfa_states(self._h)

    @property
    def dfa_states_before_minimisation(self) -> int:
        return lib().cdec_dfa_states_premin(self._h)

    @property
    def nfa_states(self) -> int:
        return lib().cdec_nfa_states(self._h)

    @property
    def trie_nodes(self) -> int:
        return lib().cdec_trie_nodes(self._h)

    @property
    def diagnostics(self) -> list[str]:
        needed = lib().cdec_diagnostics(self._h, None, 0)
        buf = ctypes.create_string_buffer(max(needed, 1))
        lib().cdec_diagnostics(self._h, buf, len(buf))
        text = buf.value.decode("utf-8", "replace")
        return [line for line in text.splitlines() if line]

    def mask_stats(self) -> dict[str, int]:
        values = [ctypes.c_uint64(0) for _ in range(4)]
        lib().cdec_mask_stats(self._h, *[ctypes.byref(v) for v in values])
        return dict(zip(("lookups", "misses", "trie_nodes_visited", "compute_nanos"),
                        (v.value for v in values)))

    def reset_stats(self) -> None:
        lib().cdec_reset_stats(self._h)


def is_valid_json(text: str | bytes) -> bool:
    """The C++ validity oracle, exposed so tests can cross-check independently."""
    data = text.encode("utf-8") if isinstance(text, str) else text
    return bool(lib().cdec_valid_json(data, len(data)))


def version() -> str:
    return lib().cdec_version().decode("ascii")
