using Idp.Application.Abstractions;
using Idp.Application.Configuration;
using Idp.Application.Exporting;
using Microsoft.Extensions.Options;

namespace Idp.Infrastructure.Exporting;

/// <summary>
/// In-process simulated ERP client. It books via the shared <see cref="SimulatedErpLedger"/> and
/// honours the idempotency key (a retry of an already-accepted key returns the original reference
/// instead of double-booking). A configurable, seeded transient-failure rate lets the demo exercise
/// the retry/dead-letter path; the default rate is 0 so <c>dotnet test</c> is fully deterministic.
/// </summary>
public sealed class SimulatedErpExportClient : IErpExportClient
{
    private readonly SimulatedErpLedger _ledger;
    private readonly IClock _clock;
    private readonly ExportOptions _options;
    private int _counter;

    public SimulatedErpExportClient(
        SimulatedErpLedger ledger, IClock clock, IOptions<ExportOptions> options)
    {
        _ledger = ledger;
        _clock = clock;
        _options = options.Value;
    }

    public Task<ErpExportResult> SendAsync(ErpExportRequest request, CancellationToken ct = default)
    {
        // Idempotent replay: if already booked, always succeed with the original reference.
        if (_ledger.TryGet(request.IdempotencyKey, out var existing) && existing is not null)
            return Task.FromResult(ErpExportResult.Ok(existing.Reference));

        // Deterministic simulated transient failure based on the configured rate.
        if (_options.SimulatedFailureRate > 0)
        {
            var n = Interlocked.Increment(ref _counter);
            var everyN = (int)Math.Round(1.0 / Math.Clamp(_options.SimulatedFailureRate, 0.0001, 1.0));
            if (everyN > 0 && n % everyN != 0)
                return Task.FromResult(
                    ErpExportResult.TransientFailure($"Simulated transient ERP failure (attempt {n})."));
        }

        var (booking, _) = _ledger.Book(
            request.IdempotencyKey, request.DocumentId, request.Payload, _clock.UtcNow);
        return Task.FromResult(ErpExportResult.Ok(booking.Reference));
    }
}
