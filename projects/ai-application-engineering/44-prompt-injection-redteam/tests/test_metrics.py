import math

import pytest

from redteam.corpus import load_corpus
from redteam.metrics import (Rate, Tradeoff, _z, evaluate_all_subsets,
                             frontier, interaction, macro_asr, per_family,
                             per_goal, rule_of_three, shapley_values)
from redteam.pipeline import ALL_LAYERS, Layer, Pipeline
from redteam.target import SimulatedAgent

CORPUS = load_corpus()


def run(layers):
    return Pipeline(SimulatedAgent(susceptibility=0.6), layers).run(CORPUS)


@pytest.fixture(scope="module")
def table():
    return evaluate_all_subsets(run)


class TestNormalQuantile:
    def test_ninety_five_percent_is_the_familiar_value(self):
        assert abs(_z(0.95) - 1.959964) < 1e-4

    def test_ninety_nine_percent(self):
        assert abs(_z(0.99) - 2.575829) < 1e-4

    def test_ninety_percent(self):
        assert abs(_z(0.90) - 1.644854) < 1e-4

    def test_is_increasing_in_confidence(self):
        assert _z(0.80) < _z(0.90) < _z(0.95) < _z(0.99)


class TestRate:
    def test_point_estimate(self):
        assert Rate(1, 4).point == 0.25

    def test_zero_trials_does_not_divide_by_zero(self):
        assert Rate(0, 0).point == 0.0

    def test_wilson_interval_contains_the_point(self):
        r = Rate(5, 20)
        low, high = r.wilson
        assert low <= r.point <= high

    def test_wilson_interval_is_within_zero_and_one(self):
        for successes in range(0, 21):
            low, high = Rate(successes, 20).wilson
            assert 0.0 <= low <= high <= 1.0

    def test_wilson_at_zero_successes_has_positive_width(self):
        # The reason Wilson is used rather than the normal approximation:
        # observing zero must not produce an interval of zero width.
        low, high = Rate(0, 20).wilson
        assert low == 0.0 and high > 0.0

    def test_wilson_at_full_successes_has_positive_width(self):
        low, high = Rate(20, 20).wilson
        assert high == 1.0 and low < 1.0

    def test_wilson_narrows_as_n_grows(self):
        def width(n):
            low, high = Rate(n // 2, n).wilson
            return high - low
        assert width(400) < width(100) < width(25)

    def test_wilson_is_symmetric_under_relabelling(self):
        low_a, high_a = Rate(3, 10).wilson
        low_b, high_b = Rate(7, 10).wilson
        assert abs((1 - low_b) - high_a) < 1e-9
        assert abs((1 - high_b) - low_a) < 1e-9

    def test_str_includes_the_interval(self):
        assert "[" in str(Rate(1, 10))

    def test_zero_trials_gives_the_widest_interval(self):
        low, high = Rate(0, 0).wilson
        assert low == 0.0 and high == 1.0


class TestRuleOfThree:
    def test_is_approximately_three_over_n(self):
        assert abs(rule_of_three(100) - 0.03) < 0.001

    def test_shrinks_as_n_grows(self):
        assert rule_of_three(1000) < rule_of_three(100) < rule_of_three(10)

    def test_zero_trials_licenses_nothing(self):
        assert rule_of_three(0) == 1.0

    def test_matches_the_closed_form(self):
        assert abs(rule_of_three(67) - (-math.log(0.05) / 67)) < 1e-12

    def test_bounds_the_wilson_upper_limit_loosely(self):
        # Both answer "we saw zero, how high could it be"; they should agree
        # to within a factor of two rather than disagree in kind.
        _, high = Rate(0, 100).wilson
        assert 0.5 < rule_of_three(100) / high < 2.0


class TestAveraging:
    def test_macro_is_the_mean_of_family_rates(self):
        result = run(frozenset())
        rates = [r.point for r in per_family(result, CORPUS).values()]
        assert abs(macro_asr(result, CORPUS) - sum(rates) / len(rates)) < 1e-9

    def test_micro_and_macro_differ_on_an_unbalanced_corpus(self):
        result = run(frozenset())
        assert abs(macro_asr(result, CORPUS) - result.asr) > 0.01

    def test_per_family_covers_every_family(self):
        result = run(frozenset())
        assert set(per_family(result, CORPUS)) == set(CORPUS.families)

    def test_per_goal_trials_sum_to_the_corpus(self):
        result = run(frozenset())
        total = sum(r.trials for r in per_goal(result, CORPUS).values())
        assert total == len(CORPUS.attacks)

    def test_per_family_trials_sum_to_the_corpus(self):
        result = run(frozenset())
        total = sum(r.trials for r in per_family(result, CORPUS).values())
        assert total == len(CORPUS.attacks)


class TestSubsetEnumeration:
    def test_all_thirty_two_configurations_are_present(self, table):
        assert len(table) == 2 ** len(ALL_LAYERS)

    def test_the_empty_set_is_present(self, table):
        assert frozenset() in table

    def test_the_full_set_is_present(self, table):
        assert frozenset(ALL_LAYERS) in table

    def test_every_result_records_its_own_layers(self, table):
        for layers, result in table.items():
            assert result.layers == layers

    def test_defences_never_increase_attack_success(self, table):
        # Monotonicity: adding a layer must not make things worse. A
        # violation would mean a layer is actively enabling an attack.
        empty = table[frozenset()].asr
        for layers, result in table.items():
            assert result.asr <= empty + 1e-9, layers

    def test_the_full_stack_is_the_strongest(self, table):
        best = min(r.asr for r in table.values())
        assert abs(table[frozenset(ALL_LAYERS)].asr - best) < 1e-9


class TestShapley:
    def test_efficiency_axiom(self, table):
        # The values must sum to the total reduction. This is the cheapest
        # available check on a factorial-weight error, and it is why the
        # decomposition is trustworthy at all.
        values = shapley_values(table, lambda r: -r.asr)
        total = table[frozenset()].asr - table[frozenset(ALL_LAYERS)].asr
        assert abs(sum(values.values()) - total) < 1e-9

    def test_every_layer_receives_a_value(self, table):
        assert set(shapley_values(table, lambda r: -r.asr)) == set(ALL_LAYERS)

    def test_a_layer_that_changes_nothing_scores_zero(self, table):
        values = shapley_values(table, lambda r: -r.asr)
        # Egress in permissive mode changes no outcome in this corpus.
        assert abs(values[Layer.EGRESS]) < 1e-9

    def test_values_are_non_negative_for_a_monotone_game(self, table):
        values = shapley_values(table, lambda r: -r.asr)
        assert all(v >= -1e-9 for v in values.values())

    def test_symmetry_under_a_constant_value_function(self, table):
        values = shapley_values(table, lambda r: 1.0)
        assert all(abs(v) < 1e-9 for v in values.values())

    def test_dummy_layer_axiom(self, table):
        # A layer whose marginal contribution is zero everywhere gets zero.
        values = shapley_values(table, lambda r: -len(r.layers - {Layer.EGRESS}))
        assert abs(values[Layer.EGRESS]) < 1e-9

    def test_additivity_over_value_functions(self, table):
        a = shapley_values(table, lambda r: -r.asr)
        b = shapley_values(table, lambda r: -r.fpr)
        both = shapley_values(table, lambda r: -r.asr - r.fpr)
        for layer in ALL_LAYERS:
            assert abs(both[layer] - (a[layer] + b[layer])) < 1e-9


class TestInteraction:
    def test_is_symmetric(self, table):
        ab = interaction(table, lambda r: -r.asr, Layer.CLASSIFY, Layer.BROKER)
        ba = interaction(table, lambda r: -r.asr, Layer.BROKER, Layer.CLASSIFY)
        assert abs(ab - ba) < 1e-9

    def test_independent_layers_interact_at_zero(self, table):
        value = interaction(table, lambda r: -r.asr, Layer.BROKER, Layer.EGRESS)
        assert abs(value) < 1e-9

    def test_normalisation_and_classification_are_complementary(self, table):
        # Positive means genuine defence in depth: the pair does more
        # together than the sum of their solo contributions.
        value = interaction(table, lambda r: -r.asr,
                            Layer.NORMALIZE, Layer.CLASSIFY)
        assert value > 0.0

    def test_a_constant_game_has_no_interaction(self, table):
        value = interaction(table, lambda r: 1.0, Layer.CLASSIFY, Layer.BROKER)
        assert abs(value) < 1e-9

    def test_redundant_layers_interact_negatively(self, table):
        value = interaction(table, lambda r: -r.asr,
                            Layer.CLASSIFY, Layer.SPOTLIGHT)
        assert value < 0.0


class TestFrontier:
    def test_frontier_is_non_empty(self, table):
        assert frontier(self._tradeoffs(table))

    def test_frontier_is_a_subset(self, table):
        tradeoffs = self._tradeoffs(table)
        labels = {t.label for t in tradeoffs}
        assert all(t.label in labels for t in frontier(tradeoffs))

    def test_no_frontier_point_dominates_another(self, table):
        front = frontier(self._tradeoffs(table))
        for a in front:
            for b in front:
                if a is b:
                    continue
                strictly_better = (a.asr.point <= b.asr.point
                                   and a.fpr.point <= b.fpr.point
                                   and (a.asr.point < b.asr.point
                                        or a.fpr.point < b.fpr.point))
                assert not strictly_better

    def test_every_dominated_point_is_excluded(self, table):
        tradeoffs = self._tradeoffs(table)
        front = {t.label for t in frontier(tradeoffs)}
        for candidate in tradeoffs:
            if candidate.label in front:
                continue
            assert any(o.asr.point <= candidate.asr.point
                       and o.fpr.point <= candidate.fpr.point
                       for o in tradeoffs if o.label != candidate.label)

    def test_an_empty_input_gives_an_empty_frontier(self):
        assert frontier([]) == []

    @staticmethod
    def _tradeoffs(table):
        return [Tradeoff(layers=layers,
                         asr=Rate(len(r.successes), len(r.outcomes)),
                         macro=macro_asr(r, CORPUS),
                         fpr=Rate(sum(1 for b in r.benign if b.blocked),
                                  len(r.benign)))
                for layers, r in table.items()]


class TestTradeoff:
    def test_label_lists_layers_alphabetically(self):
        t = Tradeoff(layers=frozenset({Layer.SPOTLIGHT, Layer.BROKER}),
                     asr=Rate(0, 10), macro=0.0, fpr=Rate(0, 10))
        assert t.label == "broker+spotlight"

    def test_label_of_the_empty_configuration(self):
        t = Tradeoff(layers=frozenset(), asr=Rate(0, 10), macro=0.0,
                     fpr=Rate(0, 10))
        assert t.label

    def test_usable_when_false_positives_are_low(self):
        t = Tradeoff(layers=frozenset(), asr=Rate(0, 10), macro=0.0,
                     fpr=Rate(0, 100))
        assert t.usable

    def test_not_usable_when_false_positives_are_high(self):
        t = Tradeoff(layers=frozenset(), asr=Rate(0, 10), macro=0.0,
                     fpr=Rate(50, 100))
        assert not t.usable
