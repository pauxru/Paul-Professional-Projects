namespace AgentPlatform.Application.Evaluation;

/// <summary>Supplies the seeded evaluation scenarios. Implemented in Infrastructure with fixed data.</summary>
public interface IEvalScenarioProvider
{
    IReadOnlyList<EvalScenario> GetScenarios();
}
