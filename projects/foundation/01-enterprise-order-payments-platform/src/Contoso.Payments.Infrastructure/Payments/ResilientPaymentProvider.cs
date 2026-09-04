using System.Diagnostics;

using Microsoft.Extensions.Logging;

using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Infrastructure.Observability;

namespace Contoso.Payments.Infrastructure.Payments;

/// <summary>
/// Wraps <see cref="IPaymentProvider"/> with retry + timeout + circuit breaker.  This is the
/// canonical "resilience is a policy at the edge, not a coincidence" pattern.  Timeouts and
/// declines are surfaced to the domain via the outcome; only transient exceptions ever bubble.
/// </summary>
public sealed class ResilientPaymentProvider : IPaymentProvider
{
    private static readonly ActivitySource Activity = new(TelemetryConstants.SourceName);

    private readonly IPaymentProvider _inner;
    private readonly ILogger<ResilientPaymentProvider> _log;
    private readonly ResiliencePipeline<PaymentProviderResult> _pipeline;

    public ResilientPaymentProvider(IPaymentProvider inner, ILogger<ResilientPaymentProvider> log)
    {
        _inner = inner;
        _log = log;
        _pipeline = new ResiliencePipelineBuilder<PaymentProviderResult>()
            .AddRetry(new RetryStrategyOptions<PaymentProviderResult>
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(50),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<PaymentProviderResult>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(r => r.Outcome == PaymentProviderOutcome.Timeout),
                OnRetry = args =>
                {
                    _log.LogWarning("Payment provider retry #{Attempt}", args.AttemptNumber);
                    return default;
                }
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(15),
                OnTimeout = args =>
                {
                    _log.LogWarning("Payment provider timeout after {Timeout}", args.Timeout);
                    return default;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<PaymentProviderResult>
            {
                FailureRatio = 0.6,
                MinimumThroughput = 8,
                BreakDuration = TimeSpan.FromSeconds(15),
                SamplingDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder<PaymentProviderResult>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(r => r.Outcome == PaymentProviderOutcome.Timeout ||
                                       r.Outcome == PaymentProviderOutcome.ProviderError)
            })
            .Build();
    }

    public async Task<PaymentProviderResult> AuthorizeAsync(PaymentProviderRequest request, CancellationToken ct)
    {
        using var activity = Activity.StartActivity("payment.authorize", ActivityKind.Client);
        activity?.SetTag("payment.intent_id", request.PaymentIntentId);
        activity?.SetTag("payment.currency", request.Currency);

        try
        {
            return await _pipeline.ExecuteAsync(async token => await _inner.AuthorizeAsync(request, token), ct);
        }
        catch (BrokenCircuitException)
        {
            return new PaymentProviderResult(PaymentProviderOutcome.ProviderError, null,
                "circuit_open", "circuit breaker open", 0);
        }
        catch (TimeoutRejectedException)
        {
            return new PaymentProviderResult(PaymentProviderOutcome.Timeout, null,
                "gateway_timeout", "timeout policy exhausted", 0);
        }
    }

    public Task<PaymentProviderResult> CaptureAsync(string providerReference, CancellationToken ct)
        => _inner.CaptureAsync(providerReference, ct);

    public Task<PaymentProviderResult> VoidAsync(string providerReference, CancellationToken ct)
        => _inner.VoidAsync(providerReference, ct);

    public Task<PaymentProviderResult> RefundAsync(string providerReference, decimal amount, string currency, CancellationToken ct)
        => _inner.RefundAsync(providerReference, amount, currency, ct);
}
