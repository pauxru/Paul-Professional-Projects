using AgentPlatform.Application.Abstractions;
using AgentPlatform.Domain.Runs;

namespace AgentPlatform.UnitTests.TestSupport;

/// <summary>Thrown by <see cref="TestFaultInjector"/> to simulate a process crash mid-run.</summary>
public sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException(string checkpoint, string? stepId)
        : base($"Simulated crash at '{checkpoint}' (step '{stepId}').") { }
}

/// <summary>
/// Fault injector that throws exactly once, at a named checkpoint for a named step, to simulate a
/// crash between a step completing its work and the run's position being committed. After firing
/// once it disarms, so a subsequent resume runs to completion.
/// </summary>
public sealed class TestFaultInjector : IFaultInjector
{
    private readonly string _checkpoint;
    private readonly string _stepId;
    private bool _armed;

    public TestFaultInjector(string checkpoint, string stepId)
    {
        _checkpoint = checkpoint;
        _stepId = stepId;
        _armed = true;
    }

    public int SignalCount { get; private set; }
    public bool Fired { get; private set; }

    public Task SignalAsync(string checkpoint, WorkflowRun run, string? stepId, CancellationToken cancellationToken)
    {
        SignalCount++;
        if (_armed && checkpoint == _checkpoint && stepId == _stepId)
        {
            _armed = false;
            Fired = true;
            throw new SimulatedCrashException(checkpoint, stepId);
        }
        return Task.CompletedTask;
    }
}
