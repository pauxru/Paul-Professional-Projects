"""Shared fixtures.

The estate is expensive enough to build that rebuilding it per test would
dominate the suite, and it is immutable, so it is session-scoped. Anything
that needs to mutate it builds its own copy from ``dataclasses.replace``.
"""

from __future__ import annotations

import pytest

from wave.costs import evaluate
from wave.estate import build_estate
from wave.solvers import SOLVERS, annealed, make_objective
from wave.units import cheapest_feasible_decoupling, contract


@pytest.fixture(scope="session")
def estate():
    return build_estate()


@pytest.fixture(scope="session")
def decoupling(estate):
    return cheapest_feasible_decoupling(estate)


@pytest.fixture(scope="session")
def remediation(decoupling):
    return decoupling[1]


@pytest.fixture(scope="session")
def contracted(estate, decoupling):
    return contract(estate, decoupling[0])


@pytest.fixture(scope="session")
def raw_contracted(estate):
    return contract(estate)


@pytest.fixture(scope="session")
def objective(estate, contracted, remediation):
    return make_objective(estate, contracted, remediation_cost=remediation)


@pytest.fixture(scope="session")
def naive_plans(estate, contracted):
    return {name: f(estate, contracted) for name, f in SOLVERS.items()}


@pytest.fixture(scope="session")
def best_plan(estate, contracted, objective, naive_plans):
    return annealed(
        estate,
        contracted,
        objective,
        list(naive_plans.values()),
        iterations=2000,
    )


@pytest.fixture(scope="session")
def all_plans(naive_plans, best_plan):
    out = dict(naive_plans)
    out["annealed"] = best_plan
    return out


@pytest.fixture(scope="session")
def best_schedule(estate, contracted, best_plan, remediation):
    return evaluate(estate, contracted, best_plan, remediation_cost=remediation)[1]
