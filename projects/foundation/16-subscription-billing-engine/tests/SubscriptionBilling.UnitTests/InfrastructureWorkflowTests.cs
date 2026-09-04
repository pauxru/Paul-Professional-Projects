using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SubscriptionBilling.Application;
using SubscriptionBilling.Domain;
using SubscriptionBilling.Infrastructure;

namespace SubscriptionBilling.UnitTests;

public sealed class InfrastructureWorkflowTests
{
    [Fact]
    public async Task InvoiceRun_RepeatedForSameSubscriptionPeriod_CreatesOneInvoice()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var clock = new ReliabilityTests.FakeClock(ReliabilityTests.At(2026, 2, 1));
        var options = RuntimeOptions();
        var seeded = await SeedBillingPeriodAsync(
            fixture.Db,
            clock.UtcNow.AddMonths(-1),
            clock.UtcNow);
        var generator = Generator(fixture.Db, clock, options);

        var first = await generator.RunDueAsync(CancellationToken.None);
        var second = await generator.RunDueAsync(CancellationToken.None);

        Assert.Equal(1, first.Generated);
        Assert.Equal(0, second.Generated);
        Assert.Equal(1, await fixture.Db.Invoices.CountAsync());
        Assert.Equal(1, await fixture.Db.InvoiceLines.CountAsync());
        var invoice = await fixture.Db.Invoices.SingleAsync();
        var line = await fixture.Db.InvoiceLines.SingleAsync();
        var schedule = RevenueRecognition.SpreadEvenly(
            new Money(line.AmountMinor, line.Currency),
            line.PeriodStart,
            line.PeriodEnd);
        Assert.Equal(invoice.SubtotalMinor, schedule.Sum(entry => entry.Amount.MinorUnits));
        var mrr = RevenueReporting.MonthlyRecurringRevenue(
            new RecurringContractSnapshot(
                seeded.Subscription.Id,
                new Money(line.AmountMinor, line.Currency),
                new BillingInterval(BillingIntervalUnit.Month, 1),
                true,
                false));
        Assert.Equal(invoice.SubtotalMinor, mrr.MinorUnits);
    }

    [Fact]
    public async Task UsageEvent_RepeatedEventId_ReturnsDuplicateWithoutCountingTwice()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var clock = new ReliabilityTests.FakeClock(ReliabilityTests.At(2026, 1, 15));
        var ids = new GuidGenerator();
        var options = RuntimeOptions();
        var seeded = await SeedBillingPeriodAsync(
            fixture.Db,
            ReliabilityTests.At(2026, 1, 1),
            ReliabilityTests.At(2026, 2, 1),
            withMeter: true);
        var engine = Engine(fixture.Db, clock, ids, options);
        var command = new RecordUsageCommand(
            "evt-100",
            seeded.SubscriptionId,
            seeded.MeterId!.Value,
            clock.UtcNow,
            10m,
            null,
            null);

        var first = await engine.RecordUsageAsync(command, CancellationToken.None);
        var second = await engine.RecordUsageAsync(command, CancellationToken.None);

        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
        Assert.Equal(10m, second.RollupValue);
        Assert.Equal(1, await fixture.Db.UsageEvents.CountAsync());
    }

    [Fact]
    public async Task LateUsage_ClosedPeriodCreditPolicy_RollsIntoCurrentOpenPeriod()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var clock = new ReliabilityTests.FakeClock(ReliabilityTests.At(2026, 2, 15));
        var seeded = await SeedBillingPeriodAsync(
            fixture.Db,
            ReliabilityTests.At(2026, 2, 1),
            ReliabilityTests.At(2026, 3, 1),
            withMeter: true);
        var options = RuntimeOptions() with
        {
            ClosedPeriodUsageBehavior = ClosedPeriodUsageBehavior.CreditNextOpenPeriod
        };
        var engine = Engine(fixture.Db, clock, new GuidGenerator(), options);

        var receipt = await engine.RecordUsageAsync(
            new RecordUsageCommand(
                "late-credit-event",
                seeded.SubscriptionId,
                seeded.MeterId!.Value,
                ReliabilityTests.At(2026, 1, 20),
                9m,
                null,
                null),
            CancellationToken.None);

        Assert.Equal(LateUsageDisposition.CreditedToNextOpenPeriod, receipt.Disposition);
        Assert.Equal(9m, receipt.RollupValue);
        var stored = await fixture.Db.UsageEvents.SingleAsync();
        Assert.Equal(ReliabilityTests.At(2026, 1, 20), stored.OccurredAt);
        Assert.Equal(ReliabilityTests.At(2026, 2, 1), stored.RollupPeriodStart);
    }

    [Fact]
    public async Task Dunning_FourFailedRetries_SuspendsThenWebhookRecoveryRestoresAccess()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var start = ReliabilityTests.At(2026, 1, 1);
        var clock = new ReliabilityTests.FakeClock(start);
        var ids = new GuidGenerator();
        var options = RuntimeOptions();
        var seeded = await SeedBillingPeriodAsync(
            fixture.Db,
            start,
            start.AddMonths(1));
        var invoice = await AddOpenInvoiceAsync(fixture.Db, seeded, start);
        var engine = Engine(fixture.Db, clock, ids, options);

        _ = await engine.PayInvoiceAsync(
            invoice.Id,
            "pm_insufficient",
            CancellationToken.None);
        Assert.Equal(SubscriptionState.PastDue, seeded.Subscription.State);

        var notifications = new RecordingNotifications();
        var processor = new DunningProcessor(
            fixture.Db,
            clock,
            ids,
            new PaymentSimulator(ids),
            notifications,
            options);
        foreach (var day in new[] { 1, 3, 5, 7 })
        {
            clock.Set(start.AddDays(day));
            await processor.RunDueAsync(CancellationToken.None);
        }

        Assert.Equal(SubscriptionState.Unpaid, seeded.Subscription.State);
        Assert.True(seeded.Subscription.IsAccessSuspended);
        Assert.Equal(InvoiceStatus.Uncollectible, invoice.Status);
        Assert.Equal(4, notifications.Attempts);
        Assert.Equal(1, notifications.Escalations);

        await engine.ProcessAsync("payment.succeeded", invoice.Id, CancellationToken.None);

        Assert.Equal(SubscriptionState.Active, seeded.Subscription.State);
        Assert.False(seeded.Subscription.IsAccessSuspended);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
    }

    [Fact]
    public async Task OutboundWebhook_ProcessorMovesExhaustedDeliveryToDeadLetter()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var clock = new ReliabilityTests.FakeClock(ReliabilityTests.At(2026, 1, 1));
        var delivery = new OutboundWebhookEntity
        {
            Id = Guid.NewGuid(),
            Endpoint = "https://fail.example.invalid/hook",
            EventType = "invoice.created",
            Payload = "{}",
            CreatedAt = clock.UtcNow,
            MaxAttempts = 2,
            NextAttemptAt = clock.UtcNow,
            Status = OutboundWebhookStatus.Pending
        };
        fixture.Db.OutboundWebhooks.Add(delivery);
        await fixture.Db.SaveChangesAsync();
        var signatures = new WebhookSignatureService(
            clock,
            new AlwaysReplayStore(),
            "test-only-signing-secret-at-least-thirty-two-characters",
            TimeSpan.FromMinutes(5));
        var processor = new OutboundWebhookProcessor(
            fixture.Db,
            clock,
            signatures,
            new SimulatedOutboundWebhookTransport());

        await processor.RunDueAsync(CancellationToken.None);
        clock.Set(delivery.NextAttemptAt);
        await processor.RunDueAsync(CancellationToken.None);

        Assert.Equal(OutboundWebhookStatus.DeadLetter, delivery.Status);
        Assert.Equal(2, delivery.Attempts);
    }

    [Fact]
    public async Task OutboundWebhook_ProcessorSignsSuccessfulDelivery()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var clock = new ReliabilityTests.FakeClock(ReliabilityTests.At(2026, 1, 1));
        const string secret = "test-only-signing-secret-at-least-thirty-two-characters";
        var delivery = new OutboundWebhookEntity
        {
            Id = Guid.NewGuid(),
            Endpoint = "https://receiver.example.invalid/hook",
            EventType = "invoice.paid",
            Payload = """{"invoiceId":"demo"}""",
            CreatedAt = clock.UtcNow,
            MaxAttempts = 3,
            NextAttemptAt = clock.UtcNow,
            Status = OutboundWebhookStatus.Pending
        };
        fixture.Db.OutboundWebhooks.Add(delivery);
        await fixture.Db.SaveChangesAsync();
        var signatures = new WebhookSignatureService(
            clock,
            new AlwaysReplayStore(),
            secret,
            TimeSpan.FromMinutes(5));
        var transport = new CapturingTransport();
        var processor = new OutboundWebhookProcessor(
            fixture.Db,
            clock,
            signatures,
            transport);

        await processor.RunDueAsync(CancellationToken.None);

        Assert.Equal(OutboundWebhookStatus.Delivered, delivery.Status);
        Assert.NotNull(transport.Signature);
        Assert.True(await signatures.VerifyAsync(
            delivery.Payload,
            transport.Signature!,
            "outbound-verification",
            CancellationToken.None));
    }

    private static BillingRuntimeOptions RuntimeOptions() =>
        new()
        {
            ClosedPeriodUsageBehavior = ClosedPeriodUsageBehavior.Reject,
            TaxRoundingLevel = TaxRoundingLevel.Line,
            DunningRetryDays = [1, 3, 5, 7],
            OutboundWebhookEndpoints = []
        };

    private static InvoiceGenerator Generator(
        BillingDbContext db,
        IClock clock,
        BillingRuntimeOptions options) =>
        new(db, clock, new GuidGenerator(), new LocalTaxProvider(), options);

    private static BillingEngine Engine(
        BillingDbContext db,
        IClock clock,
        IIdGenerator ids,
        BillingRuntimeOptions options)
    {
        var tax = new LocalTaxProvider();
        var generator = new InvoiceGenerator(db, clock, ids, tax, options);
        return new BillingEngine(
            db,
            clock,
            ids,
            new PaymentSimulator(ids),
            tax,
            generator,
            options);
    }

    private static async Task<SeededData> SeedBillingPeriodAsync(
        BillingDbContext db,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        bool withMeter = false)
    {
        var product = new ProductEntity
        {
            Id = Guid.NewGuid(),
            Name = $"Product-{Guid.NewGuid():N}",
            Description = "test"
        };
        MeterEntity? meter = null;
        if (withMeter)
        {
            meter = new MeterEntity
            {
                Id = Guid.NewGuid(),
                Name = $"Meter-{Guid.NewGuid():N}",
                Unit = "request",
                Aggregation = UsageAggregationMode.Sum,
                RoundingIncrement = 1m,
                RoundingMode = UsageRoundingMode.Up
            };
            db.Meters.Add(meter);
        }

        var plan = new PlanEntity
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            Name = $"Plan-{Guid.NewGuid():N}",
            IntervalUnit = BillingIntervalUnit.Month,
            IntervalCount = 1,
            MeterId = meter?.Id
        };
        var version = new PlanVersionEntity
        {
            Id = Guid.NewGuid(),
            PlanId = plan.Id,
            Version = 1,
            EffectiveFrom = periodStart,
            Currency = "USD",
            PricingJson = JsonSerializer.Serialize(
                withMeter
                    ? new PricingConfiguration(PricingModel.PerUnit, UnitPriceMinor: 10)
                    : new PricingConfiguration(PricingModel.FlatRecurring, FlatFeeMinor: 10_000)),
            TaxInclusive = false
        };
        var customer = new CustomerEntity
        {
            Id = Guid.NewGuid(),
            Name = $"Customer-{Guid.NewGuid():N}",
            Currency = "USD",
            CountryCode = "US"
        };
        var subscription = new SubscriptionEntity
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            PlanVersionId = version.Id,
            Quantity = 1,
            State = SubscriptionState.Active,
            CurrentPeriodStart = periodStart,
            CurrentPeriodEnd = periodEnd,
            AnchorOrigin = periodStart,
            AnchorDay = periodStart.Day,
            AnchorIsMonthEnd = periodStart.Day == DateTime.DaysInMonth(periodStart.Year, periodStart.Month),
            TrialEndBehavior = TrialEndBehavior.Activate,
            CreatedAt = periodStart,
            Version = 1
        };
        db.AddRange(product, plan, version, customer, subscription);
        db.Sequences.Add(new SequenceEntity { Name = "invoice", NextValue = 1 });
        await db.SaveChangesAsync();
        return new SeededData(subscription, customer, meter?.Id);
    }

    private static async Task<InvoiceEntity> AddOpenInvoiceAsync(
        BillingDbContext db,
        SeededData seeded,
        DateTimeOffset start)
    {
        var invoice = new InvoiceEntity
        {
            Id = Guid.NewGuid(),
            Number = "INV-TEST",
            CustomerId = seeded.Customer.Id,
            SubscriptionId = seeded.Subscription.Id,
            Status = InvoiceStatus.Open,
            PeriodStart = start,
            PeriodEnd = start.AddMonths(1),
            SubtotalMinor = 10_000,
            TotalMinor = 10_000,
            Currency = "USD",
            CreatedAt = start,
            FinalizedAt = start,
            BillingReason = "manual-test"
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice;
    }

    private sealed record SeededData(
        SubscriptionEntity Subscription,
        CustomerEntity Customer,
        Guid? MeterId)
    {
        public Guid SubscriptionId => Subscription.Id;
    }

    private sealed class RecordingNotifications : IDunningNotificationHook
    {
        public int Attempts { get; private set; }
        public int Escalations { get; private set; }

        public Task NotifyAttemptAsync(
            Guid invoiceId,
            int attemptNumber,
            PaymentOutcome outcome,
            CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.CompletedTask;
        }

        public Task NotifyEscalationAsync(Guid invoiceId, CancellationToken cancellationToken)
        {
            Escalations++;
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysReplayStore : IWebhookReplayStore
    {
        public Task<bool> TryRecordAsync(
            string nonce,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class CapturingTransport : IOutboundWebhookTransport
    {
        public string? Signature { get; private set; }

        public Task<bool> SendAsync(
            Uri endpoint,
            string eventType,
            string payload,
            string signature,
            CancellationToken cancellationToken)
        {
            Signature = signature;
            return Task.FromResult(true);
        }
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private DatabaseFixture(SqliteConnection connection, BillingDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        public SqliteConnection Connection { get; }
        public BillingDbContext Db { get; }

        public static async Task<DatabaseFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<BillingDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new BillingDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new DatabaseFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
