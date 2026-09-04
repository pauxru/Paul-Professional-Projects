using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubscriptionBilling.Application;
using SubscriptionBilling.Domain;

namespace SubscriptionBilling.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class GuidGenerator : IIdGenerator
{
    public Guid NewGuid() => Guid.NewGuid();
}

public sealed class LocalTaxProvider(decimal usRate = 0m) : ITaxProvider
{
    public TaxCalculation Calculate(TaxRequest request)
    {
        var rate = request.CountryCode.Trim().ToUpperInvariant() switch
        {
            "KE" => 0.16m,
            "US" => usRate,
            _ => 0m
        };
        return TaxCalculator.Calculate(
            request.Lines,
            rate,
            request.PricingMode,
            request.RoundingLevel,
            request.Exempt,
            request.ReverseCharge);
    }
}

public sealed class PaymentSimulator(IIdGenerator ids) : IPaymentProvider
{
    public Task<PaymentProviderResult> ChargeAsync(
        PaymentRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var outcome = request.PaymentMethodToken.Trim().ToLowerInvariant() switch
        {
            "pm_insufficient" => PaymentOutcome.InsufficientFunds,
            "pm_expired" => PaymentOutcome.ExpiredCard,
            "pm_decline" => PaymentOutcome.HardDecline,
            "pm_timeout" => PaymentOutcome.GatewayTimeout,
            _ => PaymentOutcome.Succeeded
        };
        var message = outcome switch
        {
            PaymentOutcome.Succeeded => "Simulated payment succeeded.",
            PaymentOutcome.InsufficientFunds => "Simulated insufficient funds.",
            PaymentOutcome.ExpiredCard => "Simulated expired card.",
            PaymentOutcome.HardDecline => "Simulated hard decline.",
            PaymentOutcome.GatewayTimeout => "Simulated gateway timeout with unknown provider outcome.",
            _ => "Simulated payment result."
        };
        return Task.FromResult(new PaymentProviderResult(
            outcome,
            $"sim_{ids.NewGuid():N}",
            message));
    }
}

public sealed class DatabaseWebhookReplayStore(
    BillingDbContext db,
    IClock clock) : IWebhookReplayStore
{
    public async Task<bool> TryRecordAsync(
        string nonce,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await db.WebhookNonces
            .Where(item => item.ExpiresAt < clock.UtcNow)
            .ExecuteDeleteAsync(cancellationToken);
        if (await db.WebhookNonces.AnyAsync(item => item.Nonce == nonce, cancellationToken))
        {
            return false;
        }

        db.WebhookNonces.Add(new WebhookNonceEntity
        {
            Nonce = nonce,
            ExpiresAt = expiresAt
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

public sealed class LoggingDunningNotificationHook(
    ILogger<LoggingDunningNotificationHook> logger)
    : IDunningNotificationHook
{
    public Task NotifyAttemptAsync(
        Guid invoiceId,
        int attemptNumber,
        PaymentOutcome outcome,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Dunning notification for invoice {InvoiceId}, attempt {AttemptNumber}, outcome {Outcome}",
            invoiceId,
            attemptNumber,
            outcome);
        return Task.CompletedTask;
    }

    public Task NotifyEscalationAsync(
        Guid invoiceId,
        CancellationToken cancellationToken)
    {
        logger.LogWarning("Dunning escalated for invoice {InvoiceId}", invoiceId);
        return Task.CompletedTask;
    }
}

public sealed class SimulatedOutboundWebhookTransport : IOutboundWebhookTransport
{
    public Task<bool> SendAsync(
        Uri endpoint,
        string eventType,
        string payload,
        string signature,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var succeeds = !endpoint.Host.Contains("fail", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(succeeds);
    }
}

public static class BillingTelemetry
{
    public const string SourceName = "SubscriptionBilling";
    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName);
    public static readonly Histogram<long> InvoiceRunDurationMilliseconds =
        Meter.CreateHistogram<long>("billing.invoice_run.duration", "ms");
    public static readonly Counter<long> InvoicesGenerated =
        Meter.CreateCounter<long>("billing.invoices.generated");
    public static readonly Counter<long> DunningAttempts =
        Meter.CreateCounter<long>("billing.dunning.attempts");
    public static readonly Counter<long> FailedPayments =
        Meter.CreateCounter<long>("billing.payments.failed");
}
