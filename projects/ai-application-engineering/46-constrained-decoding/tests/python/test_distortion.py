"""Tests for the distribution analysis.

The properties asserted here are mathematical facts, not implementation
details, so they are the right things to pin down: both distributions are
normalised, the lookahead correction provably recovers the conditional, and
masking never puts mass outside the valid set.
"""

from __future__ import annotations

import math

import pytest

from schemafsm import (
    BOS,
    Engine,
    build_product_graph,
    count_valid,
    decode_constrained,
    decode_retry,
    decode_unconstrained,
    enumerate_valid,
    exact_conditional,
    is_valid_json,
    kl_divergence,
    lookahead_distribution,
    masked_distribution,
    partition,
    random_bigram,
    total_variation,
)
from schemafsm.model import SplitMix64

ENUM_SCHEMA = '{"enum":["red","green","blue"]}'
ENUM_VOCAB = ['"', "r", "e", "d", "g", "n", "b", "l", "u", "re", "ed", "en", "lue"]


def make(schema: str = ENUM_SCHEMA, vocab: list[str] | None = None) -> Engine:
    vocab = vocab if vocab is not None else ENUM_VOCAB
    engine = Engine(schema)
    engine.set_vocabulary(vocab, eos_token_id=len(vocab))
    return engine


def test_splitmix64_is_the_reference_generator():
    # Known-answer test against the published SplitMix64 vectors; a homegrown
    # PRNG with no KAT is indistinguishable from a broken one.
    rng = SplitMix64(0)
    assert rng.next_u64() == 0xE220A8397B1DCDAF
    assert rng.next_u64() == 0x6E789E6AA1B965F4
    assert rng.next_u64() == 0x06C45D188009454F


def test_generator_is_reproducible():
    assert [SplitMix64(7).next_u64() for _ in range(3)] == \
           [SplitMix64(7).next_u64() for _ in range(3)]


def test_bigram_rows_are_normalised():
    model = random_bigram(6, 11)
    for row in model.rows:
        assert abs(sum(row) - 1.0) < 1e-12
        assert all(p >= 0.0 for p in row)


def test_product_graph_is_finite_and_trimmed():
    with make() as engine:
        graph = build_product_graph(engine)
        assert graph.node_count > 0
        # Every surviving node must be able to reach an accepting node,
        # otherwise the enumerated "valid set" would contain dead ends.
        for node, out in graph.edges.items():
            assert node in graph.accepting or out


def test_enumeration_matches_the_automaton_and_the_oracle():
    with make() as engine:
        graph = build_product_graph(engine)
        docs = enumerate_valid(graph, engine)
        assert docs
        for doc in docs:
            assert engine.matches(doc.text)
            assert is_valid_json(doc.text)
        assert {d.text for d in docs} == {b'"red"', b'"green"', b'"blue"'}


def test_enumeration_refuses_cyclic_graphs():
    # The schema compiler always bounds strings, so a cycle cannot arise from a
    # real schema today. The guard still has to work, because the moment
    # unbounded repetition is supported an un-guarded DFS would hang instead of
    # reporting the real problem. Constructed directly rather than pretending a
    # schema can produce it.
    from schemafsm.distortion import ProductGraph, _assert_acyclic

    a, b = (0, -1), (1, 0)
    graph = ProductGraph(start=a, edges={a: {0: b}, b: {1: a}}, accepting={b})
    with pytest.raises(ValueError, match="cycle"):
        _assert_acyclic(graph)


def test_product_graph_refuses_to_grow_without_bound():
    # Truncating silently would turn an exact result into a wrong one, so the
    # explosion has to be an error rather than a warning.
    with make('{"type":"string","maxLength":8}', ['"', "a", "b", "ab"]) as engine:
        with pytest.raises(ValueError, match="exceeded"):
            build_product_graph(engine, max_nodes=5)


def test_enumeration_refuses_exponentially_many_sequences():
    # The graph here is small and acyclic; the problem is the number of paths
    # through it, which no node-count guard would catch. This is the case that
    # actually hung during development.
    with make('{"type":"string","maxLength":40}', ['"', "a", "b"]) as engine:
        graph = build_product_graph(engine)
        assert graph.node_count < 5000
        assert count_valid(graph) > 10 ** 9
        with pytest.raises(ValueError, match="above the limit"):
            enumerate_valid(graph, engine)


def test_count_valid_agrees_with_enumeration():
    with make() as engine:
        graph = build_product_graph(engine)
        assert count_valid(graph) == len(enumerate_valid(graph, engine))


def test_both_distributions_are_normalised():
    with make() as engine:
        graph = build_product_graph(engine)
        docs = enumerate_valid(graph, engine)
        model = random_bigram(len(ENUM_VOCAB), 3)
        assert abs(sum(exact_conditional(graph, model, docs)) - 1.0) < 1e-12
        assert abs(sum(masked_distribution(graph, model, docs)) - 1.0) < 1e-12


@pytest.mark.parametrize("seed", [1, 2, 3, 4, 5])
def test_lookahead_reweighting_recovers_the_exact_conditional(seed: int):
    with make() as engine:
        graph = build_product_graph(engine)
        docs = enumerate_valid(graph, engine)
        model = random_bigram(len(ENUM_VOCAB), seed)
        exact = exact_conditional(graph, model, docs)
        corrected = lookahead_distribution(graph, model, docs)
        for a, b in zip(exact, corrected):
            assert a == pytest.approx(b, abs=1e-12)


@pytest.mark.parametrize("seed", [1, 2, 3, 4, 5])
def test_masking_differs_from_conditioning(seed: int):
    # The headline claim. If this ever passes trivially the experiment is
    # measuring nothing, so it is asserted as a strict inequality.
    with make() as engine:
        graph = build_product_graph(engine)
        docs = enumerate_valid(graph, engine)
        model = random_bigram(len(ENUM_VOCAB), seed)
        exact = exact_conditional(graph, model, docs)
        masked = masked_distribution(graph, model, docs)
        assert kl_divergence(masked, exact) > 1e-3
        assert total_variation(masked, exact) > 1e-3


def test_partition_function_at_the_start_is_the_probability_of_validity():
    with make() as engine:
        graph = build_product_graph(engine)
        docs = enumerate_valid(graph, engine)
        model = random_bigram(len(ENUM_VOCAB), 9)
        z = partition(graph, model)
        raw = 0.0
        for doc in docs:
            p, prev = 1.0, BOS
            for token in doc.tokens:
                p *= model.prob(prev, token)
                prev = token
            raw *= 1
            raw += p * model.prob(prev, model.eos_id)
        assert z[graph.start] == pytest.approx(raw, rel=1e-12)
        assert 0.0 < z[graph.start] < 1.0


def test_kl_is_zero_for_identical_distributions_and_infinite_for_disjoint():
    assert kl_divergence([0.5, 0.5], [0.5, 0.5]) == pytest.approx(0.0)
    assert kl_divergence([1.0, 0.0], [0.0, 1.0]) == math.inf


def test_constrained_decoding_always_produces_valid_documents():
    with make() as engine:
        model = random_bigram(len(ENUM_VOCAB), 21)
        rng = SplitMix64(5)
        for _ in range(200):
            result = decode_constrained(engine, model, rng)
            assert result.valid
            assert engine.matches(result.text)
            assert is_valid_json(result.text)


def test_unconstrained_decoding_mostly_fails_on_a_random_model():
    # Establishes that the constraint is doing real work rather than the model
    # happening to be well behaved.
    with make() as engine:
        model = random_bigram(len(ENUM_VOCAB), 21)
        rng = SplitMix64(5)
        valid = sum(decode_unconstrained(engine, model, rng).valid for _ in range(200))
        assert valid < 20


def test_retry_reports_the_total_cost_it_paid():
    with make() as engine:
        model = random_bigram(len(ENUM_VOCAB), 21)
        rng = SplitMix64(5)
        result = decode_retry(engine, model, rng, max_attempts=5)
        assert result.attempts >= 1
        assert result.tokens_generated >= result.attempts - 1


def test_decoding_is_reproducible_from_a_seed():
    with make() as engine:
        model = random_bigram(len(ENUM_VOCAB), 21)
        a = [decode_constrained(engine, model, SplitMix64(99)).text for _ in range(3)]
        b = [decode_constrained(engine, model, SplitMix64(99)).text for _ in range(3)]
        assert a == b


def test_tokenisation_ambiguity_is_real_for_this_vocabulary():
    # If every document had exactly one tokenisation the distinction between
    # sequence-level and document-level distributions would be vacuous.
    with make() as engine:
        graph = build_product_graph(engine)
        docs = enumerate_valid(graph, engine)
        assert len(docs) > len({d.text for d in docs})
