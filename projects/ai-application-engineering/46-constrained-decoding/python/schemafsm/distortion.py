"""Exact analysis of what masking does to a distribution.

The claim this module proves numerically:

    Locally renormalising a model over the set of tokens the automaton
    currently permits does NOT sample from the model conditioned on validity.

Both distributions put all their mass on valid strings, so no amount of
sampling and validating will reveal the difference. The difference is only
visible if you can compute the true conditional -- which needs the partition
function over all valid completions. This module computes it exactly on a
product graph small enough to enumerate, and measures the gap.

The fix is also implemented: reweighting each candidate token by the total
probability mass of valid continuations behind it recovers the exact
conditional. It is included precisely to show what it costs -- you need a
model whose future mass is computable, which a transformer's is not.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Iterable

from .model import BOS, BigramModel
from ._ffi import DEAD, Engine

# A product-graph node: (automaton state, previous token id).
Node = tuple[int, int]


@dataclass
class ProductGraph:
    """The automaton crossed with the model's state.

    Nodes are reachable (dfa_state, prev_token) pairs. Edges are token ids.
    Only tokens that keep the automaton alive appear; only nodes from which
    some accepting string is reachable are kept.
    """

    start: Node
    edges: dict[Node, dict[int, Node]] = field(default_factory=dict)
    accepting: set[Node] = field(default_factory=set)

    @property
    def node_count(self) -> int:
        return len(self.edges)

    @property
    def edge_count(self) -> int:
        return sum(len(v) for v in self.edges.values())


def build_product_graph(engine: Engine, *, max_nodes: int = 200_000) -> ProductGraph:
    """Explore every (state, prev-token) pair reachable under the vocabulary.

    Raises if the graph exceeds `max_nodes`. That is intentional: this analysis
    is only meaningful when the graph is finite and small, and silently
    truncating it would turn an exact result into a wrong one.
    """
    start: Node = (engine.start_state, BOS)
    edges: dict[Node, dict[int, Node]] = {}
    accepting: set[Node] = set()
    stack = [start]
    seen = {start}
    while stack:
        node = stack.pop()
        state, _ = node
        out: dict[int, Node] = {}
        for token_id in engine.allowed_token_ids(state):
            nxt = engine.advance_token(state, token_id)
            if nxt == DEAD:
                continue
            child: Node = (nxt, token_id)
            out[token_id] = child
            if child not in seen:
                if len(seen) >= max_nodes:
                    raise ValueError(
                        f"product graph exceeded {max_nodes} nodes; this schema is too "
                        "large for exact analysis"
                    )
                seen.add(child)
                stack.append(child)
        edges[node] = out
        if engine.is_accepting(state):
            accepting.add(node)
    return _prune_unproductive(ProductGraph(start=start, edges=edges, accepting=accepting))


def _prune_unproductive(graph: ProductGraph) -> ProductGraph:
    """Drop nodes from which no accepting node is reachable.

    The automaton itself is already trimmed, but the product graph can still
    contain dead ends when a token's bytes strand the model mid-way. Leaving
    them in would put mass on continuations that can never terminate.
    """
    reverse: dict[Node, set[Node]] = {}
    for src, out in graph.edges.items():
        for dst in out.values():
            reverse.setdefault(dst, set()).add(src)
    productive = set(graph.accepting)
    stack = list(graph.accepting)
    while stack:
        node = stack.pop()
        for parent in reverse.get(node, ()):
            if parent not in productive:
                productive.add(parent)
                stack.append(parent)
    edges = {
        node: {t: d for t, d in out.items() if d in productive}
        for node, out in graph.edges.items()
        if node in productive
    }
    return ProductGraph(start=graph.start, edges=edges,
                        accepting=graph.accepting & productive)


def partition(graph: ProductGraph, model: BigramModel) -> dict[Node, float]:
    """Z(node) = total model probability of all valid completions from `node`.

    Computed by memoised recursion. The graph must be acyclic; a cycle means
    the schema admits unbounded strings, for which Z is a linear system rather
    than a recursion and, more importantly, for which exact enumeration of the
    valid set is impossible anyway.
    """
    z: dict[Node, float] = {}
    IN_PROGRESS = object()
    marks: dict[Node, object] = {}

    def visit(node: Node) -> float:
        cached = z.get(node)
        if cached is not None:
            return cached
        if marks.get(node) is IN_PROGRESS:
            raise ValueError("product graph contains a cycle; the valid set is infinite")
        marks[node] = IN_PROGRESS
        _, prev = node
        total = model.prob(prev, model.eos_id) if node in graph.accepting else 0.0
        for token_id, child in graph.edges[node].items():
            total += model.prob(prev, token_id) * visit(child)
        marks.pop(node, None)
        z[node] = total
        return total

    for node in graph.edges:
        visit(node)
    return z


@dataclass(frozen=True)
class Sequence:
    tokens: tuple[int, ...]
    text: bytes

    def __str__(self) -> str:  # pragma: no cover - display only
        return self.text.decode("utf-8", "replace")


def count_valid(graph: ProductGraph) -> int:
    """Number of complete token sequences, without enumerating them.

    Counting is a linear-time DP over the DAG while enumeration is exponential
    in the worst case, so this is what decides whether enumeration is even
    worth attempting. Skipping this check is how a "small" schema turns into a
    process that never returns: a 40-byte string over a two-letter vocabulary
    has a few thousand product-graph nodes and 2^40 paths through them.
    """
    _assert_acyclic(graph)
    memo: dict[Node, int] = {}

    def visit(node: Node) -> int:
        cached = memo.get(node)
        if cached is not None:
            return cached
        total = 1 if node in graph.accepting else 0
        for child in graph.edges[node].values():
            total += visit(child)
        memo[node] = total
        return total

    order = _topological_order(graph)
    for node in reversed(order):
        visit(node)
    return visit(graph.start)


def _topological_order(graph: ProductGraph) -> list[Node]:
    """Iterative post-order, so deep graphs do not exhaust the Python stack."""
    seen: set[Node] = set()
    order: list[Node] = []
    for root in graph.edges:
        if root in seen:
            continue
        stack: list[tuple[Node, bool]] = [(root, False)]
        while stack:
            node, leaving = stack.pop()
            if leaving:
                order.append(node)
                continue
            if node in seen:
                continue
            seen.add(node)
            stack.append((node, True))
            for child in graph.edges[node].values():
                if child not in seen:
                    stack.append((child, False))
    return order


def enumerate_valid(graph: ProductGraph, engine: Engine,
                    max_sequences: int = 2_000_000) -> list[Sequence]:
    """Every complete token sequence the automaton accepts, in DFS order.

    Refuses to run when the count is too large. The count is computed first, in
    linear time, because discovering the problem by running out of memory is
    not a diagnostic.
    """
    total = count_valid(graph)
    if total > max_sequences:
        raise ValueError(
            f"schema admits {total} valid token sequences, above the limit of "
            f"{max_sequences}; exact analysis needs a smaller schema or vocabulary"
        )
    tokens = engine.tokens
    out: list[Sequence] = []
    path: list[int] = []

    def walk(node: Node) -> None:
        if node in graph.accepting:
            out.append(Sequence(tuple(path), b"".join(tokens[t] for t in path)))
        for token_id, child in graph.edges[node].items():
            path.append(token_id)
            walk(child)
            path.pop()

    walk(graph.start)
    return out


def _assert_acyclic(graph: ProductGraph) -> None:
    """Iterative colouring; recursion would blow the stack before finding the cycle."""
    WHITE, GREY, BLACK = 0, 1, 2
    colour: dict[Node, int] = {}
    for root in graph.edges:
        if colour.get(root, WHITE) != WHITE:
            continue
        stack: list[tuple[Node, bool]] = [(root, False)]
        while stack:
            node, leaving = stack.pop()
            if leaving:
                colour[node] = BLACK
                continue
            if colour.get(node, WHITE) == BLACK:
                continue
            colour[node] = GREY
            stack.append((node, True))
            for child in graph.edges[node].values():
                state = colour.get(child, WHITE)
                if state == GREY:
                    raise ValueError(
                        "product graph contains a cycle: the schema admits unbounded "
                        "documents, so the valid set cannot be enumerated"
                    )
                if state == WHITE:
                    stack.append((child, False))


def exact_conditional(graph: ProductGraph, model: BigramModel,
                      sequences: Iterable[Sequence]) -> list[float]:
    """P(sequence | sequence is valid) -- the distribution we actually want."""
    z = partition(graph, model)
    total = z[graph.start]
    probs = []
    for seq in sequences:
        p = 1.0
        prev = BOS
        for token_id in seq.tokens:
            p *= model.prob(prev, token_id)
            prev = token_id
        p *= model.prob(prev, model.eos_id)
        probs.append(p / total)
    return probs


def masked_distribution(graph: ProductGraph, model: BigramModel,
                        sequences: Iterable[Sequence]) -> list[float]:
    """The distribution produced by greedy per-token mask-and-renormalise.

    This is what every production constrained-decoding implementation does.
    """
    denom: dict[Node, float] = {}
    for node, out in graph.edges.items():
        _, prev = node
        total = model.prob(prev, model.eos_id) if node in graph.accepting else 0.0
        for token_id in out:
            total += model.prob(prev, token_id)
        denom[node] = total

    probs = []
    for seq in sequences:
        node = graph.start
        p = 1.0
        prev = BOS
        for token_id in seq.tokens:
            p *= model.prob(prev, token_id) / denom[node]
            node = graph.edges[node][token_id]
            prev = token_id
        p *= model.prob(prev, model.eos_id) / denom[node]
        probs.append(p)
    return probs


def lookahead_distribution(graph: ProductGraph, model: BigramModel,
                           sequences: Iterable[Sequence]) -> list[float]:
    """Mask-and-renormalise, but weighting each token by its future valid mass.

    Provably equal to `exact_conditional`; computed independently so the tests
    can assert the two agree rather than asserting a rearrangement of the same
    arithmetic.
    """
    z = partition(graph, model)
    probs = []
    for seq in sequences:
        node = graph.start
        p = 1.0
        prev = BOS
        for token_id in seq.tokens:
            child = graph.edges[node][token_id]
            p *= model.prob(prev, token_id) * z[child] / z[node]
            node = child
            prev = token_id
        p *= model.prob(prev, model.eos_id) / z[node]
        probs.append(p)
    return probs


def kl_divergence(p: list[float], q: list[float]) -> float:
    """KL(p || q) in nats. Zero terms in p contribute nothing, as usual."""
    total = 0.0
    for pi, qi in zip(p, q):
        if pi <= 0.0:
            continue
        if qi <= 0.0:
            return math.inf
        total += pi * math.log(pi / qi)
    return total


def total_variation(p: list[float], q: list[float]) -> float:
    return 0.5 * sum(abs(pi - qi) for pi, qi in zip(p, q))


@dataclass(frozen=True)
class DistortionReport:
    sequences: int
    texts: int
    graph_nodes: int
    graph_edges: int
    kl_masked_from_exact: float
    kl_exact_from_masked: float
    total_variation: float
    max_ratio: float
    mode_matches: bool
    exact_mode: str
    masked_mode: str
    lookahead_error: float
    # String-level view: the same document can have several tokenisations, so a
    # distribution over token sequences is not a distribution over documents.
    kl_text: float
    total_variation_text: float
    text_mode_matches: bool
    exact_text_mode: str
    masked_text_mode: str

    def as_markdown_row(self, label: str) -> str:
        return (
            f"| {label} | {self.sequences} | {self.texts} | {self.graph_nodes} | "
            f"{self.kl_masked_from_exact:.4f} | {self.total_variation:.4f} | "
            f"{self.kl_text:.4f} | {self.total_variation_text:.4f} | "
            f"{'same' if self.text_mode_matches else 'DIFFERENT'} |"
        )


def _group_by_text(sequences: list[Sequence], probs: list[float]) -> dict[bytes, float]:
    """Marginalise over tokenisation.

    A model that emits tokens defines a distribution over token sequences. Users
    care about the distribution over documents, and the map from one to the
    other is many-to-one -- `"red"` can be produced by several different token
    splits. Any claim about "the probability the model assigns to this JSON" has
    to sum over them, and the constrained decoder never gets to.
    """
    out: dict[bytes, float] = {}
    for seq, p in zip(sequences, probs):
        out[seq.text] = out.get(seq.text, 0.0) + p
    return out


def analyse(engine: Engine, model: BigramModel) -> DistortionReport:
    graph = build_product_graph(engine)
    sequences = enumerate_valid(graph, engine)
    if not sequences:
        raise ValueError("schema admits no strings over this vocabulary")
    exact = exact_conditional(graph, model, sequences)
    masked = masked_distribution(graph, model, sequences)
    lookahead = lookahead_distribution(graph, model, sequences)

    ratios = [m / e for m, e in zip(masked, exact) if e > 0.0]
    exact_mode = max(zip(exact, sequences), key=lambda pair: pair[0])[1]
    masked_mode = max(zip(masked, sequences), key=lambda pair: pair[0])[1]

    exact_text = _group_by_text(sequences, exact)
    masked_text = _group_by_text(sequences, masked)
    keys = sorted(exact_text)
    ev = [exact_text[k] for k in keys]
    mv = [masked_text.get(k, 0.0) for k in keys]
    exact_text_mode = max(keys, key=lambda k: exact_text[k])
    masked_text_mode = max(keys, key=lambda k: masked_text.get(k, 0.0))

    return DistortionReport(
        sequences=len(sequences),
        texts=len(keys),
        graph_nodes=graph.node_count,
        graph_edges=graph.edge_count,
        kl_masked_from_exact=kl_divergence(masked, exact),
        kl_exact_from_masked=kl_divergence(exact, masked),
        total_variation=total_variation(masked, exact),
        max_ratio=max(max(ratios), 1.0 / min(ratios)) if ratios else 1.0,
        mode_matches=exact_mode.tokens == masked_mode.tokens,
        exact_mode=str(exact_mode),
        masked_mode=str(masked_mode),
        lookahead_error=max(abs(a - b) for a, b in zip(lookahead, exact)),
        kl_text=kl_divergence(mv, ev),
        total_variation_text=total_variation(mv, ev),
        text_mode_matches=exact_text_mode == masked_text_mode,
        exact_text_mode=exact_text_mode.decode("utf-8", "replace"),
        masked_text_mode=masked_text_mode.decode("utf-8", "replace"),
    )
