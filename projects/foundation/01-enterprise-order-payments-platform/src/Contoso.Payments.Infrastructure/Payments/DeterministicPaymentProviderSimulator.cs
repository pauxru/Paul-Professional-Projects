using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Application.Common;

namespace Contoso.Payments.Infrastructure.Payments;

/// <summary>
/// Deterministic in-repo simulator for a payment provider.  Behaviour is a function of the
/// idempotency key by default; specific keys can force outcomes (see <see cref="ForcedOutcomes"/>),
/// and configuration flags inject broad failures (timeout / decline percentages) for demos.
/// This adapter is safe to run under xUnit — no wall-clock delays greater than <see cref="PaymentProviderOptions.SimulatedLatencyMs"/>.
/// </summary>
public sealed class DeterministicPaymentProviderSimulator : IPaymentProvider
{
    private readonly IOptionsMonitor<PaymentProviderOptions> _options;
    private readonly ILogger<DeterministicPaymentProviderSimulator> _log;
    private readonly ConcurrentDictionary<string, int> _authorizeCount = new();

    /// <summary>
    /// Test hook — inject outcomes keyed by idempotency key.  In test code, seed this before
    /// invoking the handler under test.  Not intended for production.
    /// </summary>
    public ConcurrentDictionary<string, PaymentProviderOutcome> ForcedOutcomes { get; } = new();

    /// <summary>
    /// Test hook — after how many attempts a forced Timeout outcome flips to Succeeded.  Default 2
    /// models a real retry recovery.  Set to <c>null</c> to make timeouts permanent (so the
    /// Polly retry policy is exhausted and the domain remains in Requires state).
    /// </summary>
    public int? ForcedTimeoutRecoveryAttempt { get; set; } = 2;

    /// <summary>
    /// Test hook — count of times authorize was invoked for each idempotency key.
    /// </summary>
    public IReadOnlyDictionary<string, int> AuthorizeAttempts => _authorizeCount;

    public DeterministicPaymentProviderSimulator(IOptionsMonitor<PaymentProviderOptions> options,
        ILogger<DeterministicPaymentProviderSimulator> log)
    {
        _options = options;
        _log = log;
    }

    public async Task<PaymentProviderResult> AuthorizeAsync(PaymentProviderRequest request, CancellationToken ct)
    {
        var opt = _options.CurrentValue;
        var attempt = _authorizeCount.AddOrUpdate(request.IdempotencyKey, 1, (_, v) => v + 1);

        await SimulateLatencyAsync(opt.SimulatedLatencyMs, ct);

        // Forced outcome always wins.
        if (ForcedOutcomes.TryGetValue(request.IdempotencyKey, out var forced))
        {
            // Timeout: recover after configured attempt threshold (default 2) so the resilience
            // policy is exercised.  If threshold is null, always time out.
            if (forced == PaymentProviderOutcome.Timeout &&
                ForcedTimeoutRecoveryAttempt is int recover && attempt >= recover)
            {
                var providerRef = "ref-" + Guid.NewGuid().ToString("N")[..12];
                return new PaymentProviderResult(PaymentProviderOutcome.Succeeded, providerRef, null, null, opt.SimulatedLatencyMs);
            }
            return BuildResult(forced, opt.SimulatedLatencyMs);
        }

        // Deterministic key-driven outcomes for tests / demo.
        var suffix = request.IdempotencyKey.Length > 0
            ? request.IdempotencyKey[^1..].ToUpperInvariant()
            : "";

        if (opt.AsynchronousCaptureMode)
            return new PaymentProviderResult(PaymentProviderOutcome.AsynchronousPending, null, null,
                "Awaiting webhook capture", opt.SimulatedLatencyMs);

        // Threshold-based injection.
        var seed = request.IdempotencyKey.GetHashCode() & 0x7FFFFFFF;
        var bucket = seed % 100;
        if (bucket < opt.TimeoutInjectionPercent && attempt == 1)
            return BuildResult(PaymentProviderOutcome.Timeout, opt.SimulatedLatencyMs);
        if (bucket >= opt.TimeoutInjectionPercent && bucket < opt.TimeoutInjectionPercent + opt.DeclineInjectionPercent)
            return BuildResult(PaymentProviderOutcome.Declined, opt.SimulatedLatencyMs);

        return new PaymentProviderResult(PaymentProviderOutcome.Succeeded,
            "ref-" + Guid.NewGuid().ToString("N")[..12], null, null, opt.SimulatedLatencyMs);
    }

    public async Task<PaymentProviderResult> CaptureAsync(string providerReference, CancellationToken ct)
    {
        var opt = _options.CurrentValue;
        await SimulateLatencyAsync(opt.SimulatedLatencyMs, ct);
        if (string.IsNullOrWhiteSpace(providerReference))
            return new PaymentProviderResult(PaymentProviderOutcome.ProviderError, null, "missing_reference",
                "capture without provider reference", opt.SimulatedLatencyMs);
        return new PaymentProviderResult(PaymentProviderOutcome.Succeeded, providerReference, null, null,
            opt.SimulatedLatencyMs);
    }

    public async Task<PaymentProviderResult> VoidAsync(string providerReference, CancellationToken ct)
    {
        var opt = _options.CurrentValue;
        await SimulateLatencyAsync(opt.SimulatedLatencyMs, ct);
        return new PaymentProviderResult(PaymentProviderOutcome.Succeeded, providerReference, null, null,
            opt.SimulatedLatencyMs);
    }

    public async Task<PaymentProviderResult> RefundAsync(string providerReference, decimal amount, string currency, CancellationToken ct)
    {
        var opt = _options.CurrentValue;
        await SimulateLatencyAsync(opt.SimulatedLatencyMs, ct);
        return new PaymentProviderResult(PaymentProviderOutcome.Succeeded, providerReference, null, null,
            opt.SimulatedLatencyMs);
    }

    private static PaymentProviderResult BuildResult(PaymentProviderOutcome outcome, int latencyMs) =>
        outcome switch
        {
            PaymentProviderOutcome.Succeeded =>
                new(outcome, "ref-" + Guid.NewGuid().ToString("N")[..12], null, null, latencyMs),
            PaymentProviderOutcome.Declined =>
                new(outcome, null, "card_declined", "issuer declined the request", latencyMs),
            PaymentProviderOutcome.Timeout =>
                new(outcome, null, "gateway_timeout", "provider did not respond", latencyMs),
            PaymentProviderOutcome.Duplicate =>
                new(outcome, "dup-" + Guid.NewGuid().ToString("N")[..12], null, null, latencyMs),
            PaymentProviderOutcome.AsynchronousPending =>
                new(outcome, null, null, "awaiting webhook capture", latencyMs),
            _ => new(PaymentProviderOutcome.ProviderError, null, "provider_error", "unspecified error", latencyMs),
        };

    private static async Task SimulateLatencyAsync(int ms, CancellationToken ct)
    {
        if (ms <= 0) return;
        try { await Task.Delay(ms, ct); } catch (TaskCanceledException) { }
    }
}
