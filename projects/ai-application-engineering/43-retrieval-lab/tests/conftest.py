"""Shared fixtures.

Corpus and chunking construction is deterministic but not free, so the objects
every test needs are built once per session. Nothing here mutates them; the
dataclasses that matter are frozen.
"""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from rqlab.chunking import CHUNKERS, chunk_all  # noqa: E402
from rqlab.corpus import build_corpus  # noqa: E402
from rqlab.distractors import build_distractors  # noqa: E402
from rqlab.indexes import analyse  # noqa: E402
from rqlab.queries import build_queries  # noqa: E402


@pytest.fixture(scope="session")
def corpus():
    return build_corpus()


@pytest.fixture(scope="session")
def distractors():
    return build_distractors()


@pytest.fixture(scope="session")
def all_docs(corpus, distractors):
    return list(corpus.documents) + list(distractors)


@pytest.fixture(scope="session")
def queries():
    return build_queries()


@pytest.fixture(scope="session")
def chunkings(all_docs):
    """name -> chunks, for every registered chunker."""
    return {name: chunk_all(all_docs, fn) for name, fn in CHUNKERS.items()}


@pytest.fixture(scope="session")
def answer_chunkings(corpus):
    """Chunkings over answer-bearing documents only. Faster for coverage work."""
    docs = list(corpus.documents)
    return {name: chunk_all(docs, fn) for name, fn in CHUNKERS.items()}


@pytest.fixture(scope="session")
def small_analysed(corpus):
    chunks = chunk_all(list(corpus.documents), CHUNKERS["fixed_240"])
    return chunks, analyse(chunks)
