using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubscriptionBilling.Application;
using SubscriptionBilling.Domain;

namespace SubscriptionBilling.Infrastructure;

public sealed class DunningProcessor(
    BillingDbContext db,
    IClock clock,
    IIdGenerator ids,
    IPaymentProvider paymentProvider,
    IDunningNotificationHook notifications,
    BillingRuntimeOptions options)
{
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        var retryDays = options.DunningRetryDays;
        var cases = await db.DunningCases
            .Where(item => !item.Recovered && !item.Escalated)
            .ToListAsync(cancellationToken);
        var processed = 0;
        foreach (var dunning in cases)
        {
            if (dunning.AttemptsCompleted >= retryDays.Count)
            {
                await EscalateAsync(dunning, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            var dueAt = dunning.InitialFailureAt.AddDays(retryDays[dunning.AttemptsCompleted]);
            if (clock.UtcNow < dueAt)
            {
                continue;
            }

            var invoice = await db.Invoices.SingleAsync(
                item => item.Id == dunning.InvoiceId,
                cancellationToken);
            if (invoice.Status != InvoiceStatus.Open)
            {
                dunning.Recovered = invoice.Status == InvoiceStatus.Paid;
                continue;
            }

            var attemptNumber = await db.PaymentAttempts.CountAsync(
                item => item.InvoiceId == invoice.Id,
                cancellationToken) + 1;
            var result = await paymentProvider.ChargeAsync(
                new PaymentRequest(
                    invoice.Id,
                    new Money(invoice.TotalMinor, invoice.Currency),
                    dunning.PaymentMethodToken,
                    $"dunning:{dunning.Id}:attempt:{attemptNumber}"),
                cancellationToken);
            db.PaymentAttempts.Add(new PaymentAttemptEntity
            {
                Id = ids.NewGuid(),
                InvoiceId = invoice.Id,
                AttemptNumber = attemptNumber,
                Outcome = result.Outcome,
                ProviderReference = result.ProviderReference,
                Message = result.Message,
                AttemptedAt = clock.UtcNow
            });
            dunning.AttemptsCompleted++;
            dunning.LastAttemptAt = clock.UtcNow;
            processed++;
            BillingTelemetry.DunningAttempts.Add(1);
            await notifications.NotifyAttemptAsync(
                invoice.Id,
                dunning.AttemptsCompleted,
                result.Outcome,
                cancellationToken);

            var subscription = await db.Subscriptions.SingleAsync(
                item => item.Id == dunning.SubscriptionId,
                cancellationToken);
            if (result.Succeeded)
            {
                invoice.Status = InvoiceStatus.Paid;
                dunning.Recovered = true;
                subscription.State = SubscriptionState.Active;
                subscription.IsAccessSuspended = false;
                subscription.Version++;
                Enqueue("invoice.paid", invoice.Id, new { invoice.Id, Source = "dunning" });
                Enqueue("subscription.recovered", subscription.Id, new { subscription.Id });
            }
            else
            {
                BillingTelemetry.FailedPayments.Add(1);
                if (subscription.State == SubscriptionState.Active)
                {
                    subscription.State = SubscriptionState.PastDue;
                }

                if (dunning.AttemptsCompleted >= retryDays.Count)
                {
                    await EscalateAsync(dunning, cancellationToken, invoice, subscription);
                }
                else
                {
                    Enqueue("invoice.payment_failed", invoice.Id, new
                    {
                        invoice.Id,
                        result.Outcome,
                        Attempt = dunning.AttemptsCompleted,
                        Source = "dunning"
                    });
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return processed;
    }

    private async Task EscalateAsync(
        DunningCaseEntity dunning,
        CancellationToken cancellationToken,
        InvoiceEntity? invoice = null,
        SubscriptionEntity? subscription = null)
    {
        invoice ??= await db.Invoices.SingleAsync(
            item => item.Id == dunning.InvoiceId,
            cancellationToken);
        subscription ??= await db.Subscriptions.SingleAsync(
            item => item.Id == dunning.SubscriptionId,
            cancellationToken);
        dunning.Escalated = true;
        invoice.Status = InvoiceStatus.Uncollectible;
        subscription.State = SubscriptionState.Unpaid;
        subscription.IsAccessSuspended = true;
        subscription.Version++;
        await notifications.NotifyEscalationAsync(invoice.Id, cancellationToken);
        Enqueue("subscription.unpaid", subscription.Id, new
        {
            subscription.Id,
            InvoiceId = invoice.Id,
            Suspended = true
        });
    }

    private void Enqueue(string eventType, Guid resourceId, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        foreach (var endpoint in options.OutboundWebhookEndpoints)
        {
            db.OutboundWebhooks.Add(new OutboundWebhookEntity
            {
                Id = ids.NewGuid(),
                Endpoint = endpoint,
                EventType = eventType,
                Payload = json,
                CreatedAt = clock.UtcNow,
                MaxAttempts = 5,
                NextAttemptAt = clock.UtcNow,
                Status = OutboundWebhookStatus.Pending
            });
        }
    }
}

public sealed class OutboundWebhookProcessor(
    BillingDbContext db,
    IClock clock,
    WebhookSignatureService signatures,
    IOutboundWebhookTransport transport)
{
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        var deliveries = await db.OutboundWebhooks
            .Where(item =>
                item.Status == OutboundWebhookStatus.Pending &&
                item.NextAttemptAt <= clock.UtcNow)
            .OrderBy(item => item.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        foreach (var delivery in deliveries)
        {
            var signature = signatures.Sign(delivery.Payload, clock.UtcNow);
            var succeeded = await transport.SendAsync(
                new Uri(delivery.Endpoint),
                delivery.EventType,
                delivery.Payload,
                signature,
                cancellationToken);
            delivery.Attempts++;
            if (succeeded)
            {
                delivery.Status = OutboundWebhookStatus.Delivered;
                delivery.LastError = null;
            }
            else if (delivery.Attempts >= delivery.MaxAttempts)
            {
                delivery.Status = OutboundWebhookStatus.DeadLetter;
                delivery.LastError = "Simulated endpoint rejected delivery.";
            }
            else
            {
                var shift = Math.Min(delivery.Attempts, 20);
                delivery.NextAttemptAt = clock.UtcNow.AddSeconds(1L << shift);
                delivery.LastError = "Simulated endpoint rejected delivery.";
            }
        }

        if (deliveries.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return deliveries.Count;
    }
}

public sealed class InvoiceRunBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<InvoiceRunBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var runner = scope.ServiceProvider.GetRequiredService<InvoiceGenerator>();
                await runner.RunDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Background invoice run failed");
            }
        }
    }
}

public sealed class DunningBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<DunningBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<DunningProcessor>();
                await processor.RunDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Dunning processor failed");
            }
        }
    }
}

public sealed class OutboundWebhookBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboundWebhookBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboundWebhookProcessor>();
                await processor.RunDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbound webhook dispatcher failed");
            }
        }
    }
}
