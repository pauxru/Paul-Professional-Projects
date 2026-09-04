"""Public surface of the schemafsm package."""

from ._ffi import DEAD, Engine, LibraryNotBuilt, SchemaError, is_valid_json, version
from .decoding import (
    CostPoint,
    Decoded,
    cost_curve,
    decode_constrained,
    decode_retry,
    decode_unconstrained,
)
from .distortion import (
    DistortionReport,
    analyse,
    build_product_graph,
    count_valid,
    enumerate_valid,
    exact_conditional,
    kl_divergence,
    lookahead_distribution,
    masked_distribution,
    partition,
    total_variation,
)
from .model import BOS, BigramModel, SplitMix64, mix, random_bigram, sample

__all__ = [
    "BOS",
    "BigramModel",
    "CostPoint",
    "DEAD",
    "Decoded",
    "DistortionReport",
    "Engine",
    "LibraryNotBuilt",
    "SchemaError",
    "SplitMix64",
    "analyse",
    "build_product_graph",
    "cost_curve",
    "count_valid",
    "decode_constrained",
    "decode_retry",
    "decode_unconstrained",
    "enumerate_valid",
    "exact_conditional",
    "is_valid_json",
    "kl_divergence",
    "lookahead_distribution",
    "masked_distribution",
    "mix",
    "partition",
    "random_bigram",
    "sample",
    "total_variation",
    "version",
]
