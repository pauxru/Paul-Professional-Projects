namespace ExampleBank.Ledger.Application.Abstractions;

/// <summary>
/// Business-event telemetry port. Implemented over an OpenTelemetry <c>Meter</c> in the host so
/// that posting latency, throughput, rejected-imbalance attempts and hold expiries are observable.
/// </summary>
public interface ILedgerMetrics
{
    void RecordPostingLatency(double milliseconds, string entryType);

    void EntryPosted(string entryType);

    void ImbalanceAttempt(string reason);

    void HoldExpired();
}
