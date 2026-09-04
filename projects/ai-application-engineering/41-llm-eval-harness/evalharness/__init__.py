"""A harness for deciding whether an LLM change is real.

The interesting problem in LLM evaluation is not scoring. It is that the
scores are noisy, the eval sets are small, the judges are themselves models,
and the loop from "try a prompt" to "look at the number" is fast enough to run
twenty times before lunch. Every one of those properties independently
manufactures false confidence, and together they make a system in which a team
can spend a quarter shipping improvements that never existed.

This package is the statistical machinery for not doing that.
"""

from .agreement import (Agreement, Calibration, agreement, baseline_margin,
                        calibration)
from .compare import Comparison, SliceResult, Verdict, compare
from .dataset import Dataset, DatasetDiff, Item, TIERS
from .stats import (Interval, PowerResult, benjamini_hochberg,
                    minimum_detectable_effect, paired_bca_bootstrap,
                    paired_bootstrap, paired_permutation_test, sign_test, power_paired,
                    required_n)
from .systems import Judge, Model, Response, RunResult, SimulatedJudge, SimulatedModel, run

__all__ = [
    "Agreement", "Calibration", "agreement", "baseline_margin", "calibration",
    "Comparison", "SliceResult", "Verdict", "compare",
    "Dataset", "DatasetDiff", "Item", "TIERS",
    "Interval", "PowerResult", "benjamini_hochberg", "minimum_detectable_effect",
    "paired_bca_bootstrap", "paired_bootstrap", "paired_permutation_test", "sign_test",
    "power_paired", "required_n",
    "Judge", "Model", "Response", "RunResult", "SimulatedJudge", "SimulatedModel", "run",
]
