using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Northstar.Secrets.Application;
using Northstar.Secrets.Domain;

namespace Northstar.Secrets.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface ISimulatedVerificationControl
{
    void SetFailure(string secretName, bool shouldFail);
}

public sealed class SimulatedSecretVerifier : ISecretVerifier, ISimulatedVerificationControl
{
    private readonly ConcurrentDictionary<string, byte> _failures =
        new(StringComparer.OrdinalIgnoreCase);

    public void SetFailure(string secretName, bool shouldFail)
    {
        if (shouldFail)
        {
            _failures[secretName] = 0;
        }
        else
        {
            _failures.TryRemove(secretName, out _);
        }
    }

    public Task<VerificationResult> VerifyAsync(
        SecretRecord secret,
        SecretVersion candidate,
        string plaintext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_failures.ContainsKey(secret.Name) ||
            secret.Tags.Any(x => x.Value == "verification-fail"))
        {
            return Task.FromResult(VerificationResult.Failure("Simulated downstream verification failed."));
        }

        return Task.FromResult(
            string.IsNullOrWhiteSpace(plaintext)
                ? VerificationResult.Failure("Generated material was empty.")
                : VerificationResult.Success("Simulated downstream login or signature check passed."));
    }
}

public sealed class PlatformMetrics : IPlatformMetrics, IDisposable
{
    public const string MeterName = "Northstar.Secrets";
    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _rotations;
    private readonly Counter<long> _valueReads;
    private readonly Histogram<double> _acknowledgementLatency;
    private readonly ConcurrentDictionary<string, double> _secretAges = new(StringComparer.Ordinal);
    private readonly ObservableGauge<double> _secretAgeGauge;

    public PlatformMetrics()
    {
        _rotations = _meter.CreateCounter<long>("northstar.rotations", "operations");
        _valueReads = _meter.CreateCounter<long>("northstar.secret_value_reads", "reads");
        _acknowledgementLatency = _meter.CreateHistogram<double>(
            "northstar.consumer_ack_latency", "s");
        _secretAgeGauge = _meter.CreateObservableGauge(
            "northstar.secret_age",
            () => _secretAges.Select(x =>
                new Measurement<double>(x.Value, new KeyValuePair<string, object?>("secret", x.Key))),
            "s");
    }

    public void RotationCompleted(RotationState outcome, RotationStrategyKind strategy) =>
        _rotations.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcome.ToString()),
            new KeyValuePair<string, object?>("strategy", strategy.ToString()));

    public void ValueRead(string environment, SecretType type, bool allowed) =>
        _valueReads.Add(
            1,
            new KeyValuePair<string, object?>("environment", environment),
            new KeyValuePair<string, object?>("type", type.ToString()),
            new KeyValuePair<string, object?>("allowed", allowed));

    public void AcknowledgementObserved(TimeSpan latency) =>
        _acknowledgementLatency.Record(latency.TotalSeconds);

    public void ObserveSecretAge(string secretName, TimeSpan age) =>
        _secretAges[secretName] = Math.Max(0, age.TotalSeconds);

    public void Dispose() => _meter.Dispose();
}
