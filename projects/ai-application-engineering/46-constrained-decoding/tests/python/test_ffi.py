"""Tests for the ctypes boundary itself.

These exist because the C ABI is the most dangerous part of the project: a
mismatched signature does not fail loudly, it corrupts memory. Every entry
point is exercised at least once, including the failure paths.
"""

from __future__ import annotations

import ctypes
import math

import pytest

import schemafsm
from schemafsm import Engine, SchemaError, is_valid_json


def test_version_is_reported():
    assert schemafsm.version()


def test_invalid_schema_raises_with_a_message():
    with pytest.raises(SchemaError) as excinfo:
        Engine('{"type":"object","properties":{"a":{"$ref":"#/x"}}}')
    assert str(excinfo.value)


def test_malformed_schema_json_raises():
    with pytest.raises(SchemaError):
        Engine("{not json")


def test_engine_can_be_used_as_a_context_manager():
    with Engine('{"type":"boolean"}') as engine:
        assert engine.matches("true")
    # Using it after close must fail loudly rather than dereference a dangling
    # pointer, which is the whole reason the handle is nulled on close.
    with pytest.raises(RuntimeError):
        engine.start_state


def test_double_close_is_safe():
    engine = Engine('{"type":"boolean"}')
    engine.close()
    engine.close()


def test_matches_agrees_with_the_json_oracle():
    with Engine('{"type":"boolean"}') as engine:
        assert engine.matches("true")
        assert engine.matches("false")
        assert not engine.matches("tru")
        assert not engine.matches("TRUE")
    assert is_valid_json('{"a":1}')
    assert not is_valid_json('{"a":}')


def test_matches_handles_embedded_nul_bytes():
    # A length-prefixed ABI must not be fooled by NUL. If the binding were
    # passing a C string this would silently truncate and wrongly accept.
    with Engine('{"type":"string","maxLength":4}') as engine:
        assert not engine.matches(b'"a\x00b"')


def test_vocabulary_and_masks():
    with Engine('{"enum":["red","blue"]}') as engine:
        vocab = ['"', "red", "blue", "green", '"red"', "x"]
        engine.set_vocabulary(vocab, eos_token_id=len(vocab))
        start = engine.start_state
        allowed = engine.allowed_token_ids(start)
        assert 0 in allowed        # a lone quote is a live prefix
        assert 4 in allowed        # the whole document in one token
        assert 3 not in allowed    # "green" is not in the enum
        assert 5 not in allowed
        assert not engine.eos_allowed(start)


def test_advance_and_accept():
    with Engine('{"enum":["red"]}') as engine:
        engine.set_vocabulary(['"', "red", '"'], eos_token_id=3)
        state = engine.start_state
        state = engine.advance_token(state, 0)
        assert state != schemafsm.DEAD
        state = engine.advance_token(state, 1)
        state = engine.advance_token(state, 2)
        assert engine.is_accepting(state)
        assert engine.eos_allowed(state)


def test_advance_returns_dead_for_an_illegal_token():
    with Engine('{"type":"boolean"}') as engine:
        engine.set_vocabulary(["true", "nope"], eos_token_id=2)
        assert engine.advance_token(engine.start_state, 1) == schemafsm.DEAD


def test_apply_mask_sets_illegal_logits_to_negative_infinity():
    with Engine('{"type":"boolean"}') as engine:
        vocab = ["true", "false", "banana"]
        engine.set_vocabulary(vocab, eos_token_id=len(vocab))
        logits = engine.apply_mask(engine.start_state, [1.0, 2.0, 3.0, 4.0])
        assert logits[0] == 1.0
        assert logits[1] == 2.0
        assert logits[2] == -math.inf
        assert logits[3] == -math.inf  # EOS, not legal at the start


def test_apply_mask_allows_eos_only_in_an_accepting_state():
    with Engine('{"type":"boolean"}') as engine:
        vocab = ["true", "false"]
        engine.set_vocabulary(vocab, eos_token_id=len(vocab))
        state = engine.advance_token(engine.start_state, 0)
        logits = engine.apply_mask(state, [1.0, 1.0, 1.0])
        assert logits[2] == 1.0


def test_apply_mask_with_a_vocabulary_that_exactly_fills_a_mask_word():
    # 64 tokens means the mask has no spare bits, so the EOS id sits one past
    # the end. This is the case that used to read out of bounds.
    vocab = ["true", "false"] + [f"z{i}" for i in range(62)]
    assert len(vocab) == 64
    with Engine('{"type":"boolean"}') as engine:
        engine.set_vocabulary(vocab, eos_token_id=64)
        assert engine.mask_words() == 1
        logits = engine.apply_mask(engine.start_state, [1.0] * 65)
        assert logits[0] == 1.0
        assert logits[64] == -math.inf


def test_statistics_are_reported_and_resettable():
    with Engine('{"type":"boolean"}') as engine:
        engine.set_vocabulary(["true", "false"], eos_token_id=2)
        engine.allowed_token_ids(engine.start_state)
        engine.allowed_token_ids(engine.start_state)
        stats = engine.mask_stats()
        assert stats["lookups"] >= 2
        assert stats["misses"] >= 1
        assert stats["lookups"] > stats["misses"]  # the cache did its job
        engine.reset_stats()
        assert engine.mask_stats()["lookups"] == 0


def test_diagnostics_are_surfaced():
    # A string with no maxLength has to be bounded to stay regular, and the
    # compiler is required to say so rather than quietly pick a number.
    with Engine('{"type":"string"}') as engine:
        assert any("maxLength" in d for d in engine.diagnostics)


def test_mask_before_vocabulary_is_installed_is_an_error():
    with Engine('{"type":"boolean"}') as engine:
        with pytest.raises(RuntimeError):
            engine.allowed_token_ids(engine.start_state)


def test_state_counts_are_exposed_and_minimisation_helps():
    with Engine('{"enum":["alpha","beta","gamma"]}') as engine:
        assert engine.nfa_states > 0
        assert engine.dfa_states > 0
        assert engine.dfa_states <= engine.dfa_states_before_minimisation
