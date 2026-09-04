import ast
import inspect

import pytest

from redteam import target as target_module
from redteam.channels import Tainted
from redteam.corpus import Family, Goal, load_corpus
from redteam.target import AgentTurn, SimulatedAgent, TurnLog

CORPUS = load_corpus()
ATTACKS = CORPUS.attacks
DOC = Tainted.untrusted("Quarterly figures are attached.")


def by_family(family):
    return [a for a in ATTACKS if a.family is family]


class TestSimulatorIndependence:
    def test_the_target_never_calls_defence_code(self):
        # Bug 5. The simulated model once derived its comprehension penalty
        # from the defender's decoder, so any encoding the defence failed to
        # recognise was scored as free for the attacker. The measurement
        # consulted the thing it was measuring. Nothing in this module may
        # reach into defence code again.
        tree = ast.parse(inspect.getsource(target_module))
        imported = {n.module for n in ast.walk(tree)
                    if isinstance(n, ast.ImportFrom)}
        imported |= {alias.name for n in ast.walk(tree)
                     if isinstance(n, ast.Import) for alias in n.names}
        assert not any("normalize" in (m or "") or "defenses" in (m or "")
                       for m in imported)
        called = {n.func.id for n in ast.walk(tree)
                  if isinstance(n, ast.Call) and isinstance(n.func, ast.Name)}
        assert "normalize" not in called

    def test_legibility_comes_from_the_corpus(self):
        assert all(0.0 < a.legibility <= 1.0 for a in ATTACKS)

    def test_obfuscated_attacks_are_the_only_illegible_ones(self):
        for attack in ATTACKS:
            if attack.family is not Family.OBFUSCATED:
                assert attack.legibility == 1.0, attack.id
        obfuscated = by_family(Family.OBFUSCATED)
        assert min(a.legibility for a in obfuscated) < 1.0


class TestDeterminism:
    def test_the_same_agent_gives_the_same_answer_twice(self):
        agent = SimulatedAgent()
        for attack in ATTACKS[:20]:
            assert (agent.respond(attack, DOC).complied
                    == agent.respond(attack, DOC).complied)

    def test_two_agents_with_the_same_seed_agree(self):
        a, b = SimulatedAgent(seed=3), SimulatedAgent(seed=3)
        assert ([a.respond(x, DOC).complied for x in ATTACKS]
                == [b.respond(x, DOC).complied for x in ATTACKS])

    def test_different_seeds_disagree_somewhere(self):
        a, b = SimulatedAgent(seed=3), SimulatedAgent(seed=4)
        assert ([a.respond(x, DOC).complied for x in ATTACKS]
                != [b.respond(x, DOC).complied for x in ATTACKS])

    def test_response_does_not_depend_on_call_order(self):
        forward = SimulatedAgent()
        backward = SimulatedAgent()
        a = {x.id: forward.respond(x, DOC).complied for x in ATTACKS}
        b = {x.id: backward.respond(x, DOC).complied
             for x in reversed(ATTACKS)}
        assert a == b


class TestSusceptibility:
    def test_zero_susceptibility_never_complies(self):
        agent = SimulatedAgent(susceptibility=0.0)
        assert not any(agent.respond(x, DOC).complied for x in ATTACKS)

    def test_full_susceptibility_complies_with_most(self):
        agent = SimulatedAgent(susceptibility=1.0)
        rate = sum(agent.respond(x, DOC).complied for x in ATTACKS)
        assert rate > 0.7 * len(ATTACKS)

    def test_compliance_is_monotone_in_susceptibility(self):
        rates = []
        for s in (0.0, 0.25, 0.5, 0.75, 1.0):
            agent = SimulatedAgent(susceptibility=s)
            rates.append(sum(agent.respond(x, DOC).complied for x in ATTACKS))
        assert rates == sorted(rates)

    def test_susceptibility_is_pointwise_monotone(self):
        # Stronger than the aggregate: no individual attack may stop
        # working as the model becomes more suggestible.
        low = SimulatedAgent(susceptibility=0.3)
        high = SimulatedAgent(susceptibility=0.9)
        for attack in ATTACKS:
            if low.respond(attack, DOC).complied:
                assert high.respond(attack, DOC).complied, attack.id


class TestSpotlighting:
    def test_spotlighting_reduces_compliance(self):
        agent = SimulatedAgent(susceptibility=0.7)
        plain = sum(agent.respond(x, DOC).complied for x in ATTACKS)
        lit = sum(agent.respond(x, DOC, spotlighted=True).complied
                  for x in ATTACKS)
        assert lit < plain

    def test_spotlighting_never_helps_an_attack(self):
        agent = SimulatedAgent(susceptibility=0.7)
        for attack in ATTACKS:
            if agent.respond(attack, DOC, spotlighted=True).complied:
                assert agent.respond(attack, DOC).complied, attack.id

    def test_a_unit_multiplier_spotlight_changes_nothing(self):
        agent = SimulatedAgent(susceptibility=0.7, spotlight_effect=1.0)
        for attack in ATTACKS:
            assert (agent.respond(attack, DOC, spotlighted=True).complied
                    == agent.respond(attack, DOC).complied)

    def test_a_zero_multiplier_spotlight_stops_everything(self):
        agent = SimulatedAgent(susceptibility=1.0, spotlight_effect=0.0)
        assert not any(agent.respond(x, DOC, spotlighted=True).complied
                       for x in ATTACKS)

    def test_the_multiplier_bounds_the_drop(self):
        agent = SimulatedAgent(susceptibility=0.9, spotlight_effect=0.5)
        for attack in ATTACKS:
            plain = agent.respond(attack, DOC)
            lit = agent.respond(attack, DOC, spotlighted=True)
            assert not (lit.complied and not plain.complied), attack.id


class TestFenceForgery:
    def test_forgeable_fences_help_context_forgery(self):
        agent = SimulatedAgent(susceptibility=0.6)
        forgery = by_family(Family.CONTEXT_FORGERY)
        soft = sum(agent.respond(x, DOC, fence_forgeable=True).complied
                   for x in forgery)
        hard = sum(agent.respond(x, DOC, fence_forgeable=False).complied
                   for x in forgery)
        assert soft > hard

    def test_unforgeable_fences_do_not_help_other_families(self):
        agent = SimulatedAgent(susceptibility=0.6)
        others = [a for a in ATTACKS if a.family is not Family.CONTEXT_FORGERY]
        soft = sum(agent.respond(x, DOC, fence_forgeable=True).complied
                   for x in others)
        hard = sum(agent.respond(x, DOC, fence_forgeable=False).complied
                   for x in others)
        assert soft == hard


class TestToolCalls:
    def test_compliance_with_a_tool_attack_produces_a_call(self):
        agent = SimulatedAgent(susceptibility=1.0)
        tool_attacks = [a for a in ATTACKS if a.goal is Goal.TOOL_CALL]
        assert tool_attacks
        for attack in tool_attacks:
            turn = agent.respond(attack, DOC)
            if turn.complied:
                assert turn.calls, attack.id

    def test_refusal_never_produces_a_call(self):
        agent = SimulatedAgent(susceptibility=0.0)
        assert not any(agent.respond(x, DOC).calls for x in ATTACKS)

    def test_authorised_tool_calls_carry_attacker_arguments(self):
        # This family is the whole point of the broker section: the tool is
        # one the user may legitimately invoke, so only the provenance of
        # the arguments distinguishes the attack from the task.
        agent = SimulatedAgent(susceptibility=1.0)
        for attack in by_family(Family.AUTHORISED_TOOL):
            turn = agent.respond(attack, DOC)
            if turn.complied:
                assert any(c.args for c in turn.calls), attack.id

    def test_every_call_names_a_tool(self):
        agent = SimulatedAgent(susceptibility=1.0)
        for attack in ATTACKS:
            for call in agent.respond(attack, DOC).calls:
                assert call.tool


class TestTurnText:
    def test_a_turn_always_produces_text(self):
        agent = SimulatedAgent(susceptibility=0.5)
        assert all(agent.respond(x, DOC).text for x in ATTACKS)

    def test_compliance_emits_the_marker(self):
        agent = SimulatedAgent(susceptibility=1.0)
        for attack in ATTACKS:
            turn = agent.respond(attack, DOC)
            if turn.complied and attack.goal is not Goal.TOOL_CALL:
                assert attack.marker in turn.text, attack.id

    def test_refusal_never_emits_the_marker(self):
        agent = SimulatedAgent(susceptibility=0.0)
        for attack in ATTACKS:
            assert attack.marker not in agent.respond(attack, DOC).text

    def test_a_refusal_gives_a_reason(self):
        agent = SimulatedAgent(susceptibility=0.0)
        assert all(agent.respond(x, DOC).reason for x in ATTACKS)


class TestParaphrase:
    def test_paraphrase_is_deterministic(self):
        agent = SimulatedAgent()
        for attack in ATTACKS:
            assert agent.paraphrases(attack) == agent.paraphrases(attack)

    def test_some_attacks_survive_paraphrase_and_some_do_not(self):
        agent = SimulatedAgent()
        answers = {agent.paraphrases(x) for x in ATTACKS}
        assert answers == {True, False}


class TestTurnLog:
    def test_an_empty_log_has_no_compliance(self):
        assert TurnLog().compliance_rate == 0.0

    def test_compliance_rate_counts_complied_turns(self):
        log = TurnLog()
        log.record(ATTACKS[0], AgentTurn("a", complied=True))
        log.record(ATTACKS[1], AgentTurn("b", complied=False))
        assert log.compliance_rate == 0.5

    def test_records_are_kept_in_order(self):
        log = TurnLog()
        for attack in ATTACKS[:5]:
            log.record(attack, AgentTurn("x"))
        assert [a.id for a, _ in log.turns] == [
            a.id for a in ATTACKS[:5]]


@pytest.mark.parametrize("susceptibility", [-0.1, 1.1])
def test_susceptibility_outside_the_unit_interval_is_rejected(susceptibility):
    with pytest.raises(ValueError):
        SimulatedAgent(susceptibility=susceptibility)


