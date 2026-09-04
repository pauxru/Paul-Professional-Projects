using LoadRunner.Core.Metrics;
using LoadRunner.Core.Scenarios;

namespace LoadRunner.Core.LoadModels;

/// <summary>
/// Executes the scenario steps once for a single virtual user. Variables are cloned per
/// user so extraction from one VU doesn't leak into another.
/// </summary>
public static class VirtualUser
{
    public static async Task RunAsync(int vuId, RunContext context, CancellationToken cancellationToken)
    {
        var sampler = new ThinkTimeSampler(vuId * 7919 + 31);
        while (!cancellationToken.IsCancellationRequested)
        {
            var variables = BuildVariables(vuId, context);
            foreach (var step in context.Scenario.Steps)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var intended = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                RequestSample sample;
                try
                {
                    sample = await context.Executor.ExecuteAsync(
                        step, variables, vuId, intended, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                context.Metrics.Record(sample);

                if (step.ThinkTime is { } tt && tt.Distribution != ThinkTimeDistribution.None)
                {
                    var delay = sampler.Sample(tt);
                    if (delay > TimeSpan.Zero)
                    {
                        try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }
                }
            }
        }
    }

    public static Dictionary<string, string> BuildVariables(int vuId, RunContext context)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["vu_id"] = vuId.ToString(),
            ["vu"] = vuId.ToString(),
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
        };
        if (context.Scenario.Variables is not null)
            foreach (var (k, v) in context.Scenario.Variables) dict[k] = v;

        if (context.Feeder is not null)
        {
            var row = context.Feeder.Next();
            if (row is not null) foreach (var (k, v) in row) dict[k] = v;
        }
        return dict;
    }
}
