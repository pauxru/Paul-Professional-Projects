using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SubscriptionBilling.Application;
using SubscriptionBilling.Domain;

namespace SubscriptionBilling.Infrastructure;

public sealed class BillingEngine(
    BillingDbContext db,
    IClock clock,
    IIdGenerator ids,
    IPaymentProvider paymentProvider,
    ITaxProvider taxProvider,
    InvoiceGenerator invoiceGenerator,
    BillingRuntimeOptions options) : IBillingEngine, IPaymentWebhookProcessor
{
    public async Task<ProductView> CreateProductAsync(
        CreateProductCommand command,
        CancellationToken cancellationToken)
    {
        var domain = new Product(ids.NewGuid(), command.Name, command.Description);
        var entity = new ProductEntity
        {
            Id = domain.Id,
            Name = domain.Name,
            Description = domain.Description,
            IsActive = domain.IsActive,
            CreatedAt = clock.UtcNow
        };
        db.Products.Add(entity);
        AddAudit("product.created", "product", entity.Id, entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new ConflictException($"A product named '{entity.Name}' already exists. {exception.Message}");
        }

        return Map(entity);
    }

    public async Task<Page<ProductView>> ListProductsAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        var total = await db.Products.LongCountAsync(cancellationToken);
        var items = await db.Products.AsNoTracking()
            .OrderBy(item => item.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new ProductView(item.Id, item.Name, item.Description, item.IsActive))
            .ToListAsync(cancellationToken);
        return CreatePage(items, page, pageSize, total);
    }

    public async Task<PlanView> CreatePlanAsync(
        CreatePlanCommand command,
        CancellationToken cancellationToken)
    {
        if (!await db.Products.AnyAsync(item => item.Id == command.ProductId, cancellationToken))
        {
            throw new ResourceNotFoundException("Product was not found.");
        }

        if (command.MeterId is not null &&
            !await db.Meters.AnyAsync(item => item.Id == command.MeterId, cancellationToken))
        {
            throw new ResourceNotFoundException("Meter was not found.");
        }

        var interval = new BillingInterval(command.IntervalUnit, command.IntervalCount);
        _ = PricingStrategyFactory.Create(command.Pricing, command.Currency);
        var planId = ids.NewGuid();
        var plan = new PlanEntity
        {
            Id = planId,
            ProductId = command.ProductId,
            Name = command.Name.Trim(),
            IntervalUnit = interval.Unit,
            IntervalCount = interval.Count,
            MeterId = command.MeterId,
            CreatedAt = clock.UtcNow
        };
        var version = new PlanVersionEntity
        {
            Id = ids.NewGuid(),
            PlanId = planId,
            Version = 1,
            EffectiveFrom = command.EffectiveFrom,
            Currency = Money.Zero(command.Currency).Currency,
            PricingJson = JsonSerializer.Serialize(command.Pricing),
            TaxInclusive = command.TaxInclusive,
            CreatedAt = clock.UtcNow
        };
        db.Plans.Add(plan);
        db.PlanVersions.Add(version);
        AddAudit("plan.created", "plan", plan.Id, new { plan, version });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new ConflictException($"Plan name or initial version conflicts with existing data. {exception.Message}");
        }

        return Map(plan, [version]);
    }

    public async Task<PlanVersionView> AddPlanVersionAsync(
        Guid planId,
        AddPlanVersionCommand command,
        CancellationToken cancellationToken)
    {
        if (!await db.Plans.AnyAsync(item => item.Id == planId, cancellationToken))
        {
            throw new ResourceNotFoundException("Plan was not found.");
        }

        _ = PricingStrategyFactory.Create(command.Pricing, command.Currency);
        var versionNumber = await db.PlanVersions
            .Where(item => item.PlanId == planId)
            .MaxAsync(item => item.Version, cancellationToken) + 1;
        var entity = new PlanVersionEntity
        {
            Id = ids.NewGuid(),
            PlanId = planId,
            Version = versionNumber,
            EffectiveFrom = command.EffectiveFrom,
            Currency = Money.Zero(command.Currency).Currency,
            PricingJson = JsonSerializer.Serialize(command.Pricing),
            TaxInclusive = command.TaxInclusive,
            CreatedAt = clock.UtcNow
        };
        db.PlanVersions.Add(entity);
        AddAudit("plan.version_created", "plan-version", entity.Id, entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new ConflictException($"Plan version conflicts with an existing effective date. {exception.Message}");
        }

        return Map(entity);
    }

    public async Task<Page<PlanView>> ListPlansAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        var total = await db.Plans.LongCountAsync(cancellationToken);
        var plans = await db.Plans.AsNoTracking()
            .OrderBy(item => item.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var idsForPage = plans.Select(item => item.Id).ToArray();
        var versions = await db.PlanVersions.AsNoTracking()
            .Where(item => idsForPage.Contains(item.PlanId))
            .OrderBy(item => item.Version)
            .ToListAsync(cancellationToken);
        return CreatePage(
            plans.Select(plan => Map(
                plan,
                versions.Where(version => version.PlanId == plan.Id).ToList())).ToList(),
            page,
            pageSize,
            total);
    }

    public async Task<MeterView> CreateMeterAsync(
        CreateMeterCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Unit);
        var definition = new MeterDefinition(
            ids.NewGuid(),
            command.Name.Trim(),
            command.Unit.Trim(),
            command.Aggregation,
            command.RoundingIncrement,
            command.RoundingMode);
        _ = definition.RoundToBillableUnits(0m);
        var entity = new MeterEntity
        {
            Id = definition.Id,
            Name = definition.Name,
            Unit = definition.Unit,
            Aggregation = definition.Aggregation,
            RoundingIncrement = definition.RoundingIncrement,
            RoundingMode = definition.RoundingMode
        };
        db.Meters.Add(entity);
        AddAudit("meter.created", "meter", entity.Id, entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new ConflictException($"A meter named '{entity.Name}' already exists. {exception.Message}");
        }

        return Map(entity);
    }

    public async Task<Page<MeterView>> ListMetersAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        var total = await db.Meters.LongCountAsync(cancellationToken);
        var entities = await db.Meters.AsNoTracking()
            .OrderBy(item => item.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return CreatePage(entities.Select(Map).ToList(), page, pageSize, total);
    }

    public async Task<CustomerView> CreateCustomerAsync(
        CreateCustomerCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name);
        var currency = Money.Zero(command.Currency).Currency;
        if (command.CountryCode.Trim().Length != 2)
        {
            throw new DomainException("Country code must contain two letters.");
        }

        var entity = new CustomerEntity
        {
            Id = ids.NewGuid(),
            Name = command.Name.Trim(),
            Currency = currency,
            CountryCode = command.CountryCode.Trim().ToUpperInvariant(),
            TaxExempt = command.TaxExempt,
            ReverseCharge = command.ReverseCharge,
            CreatedAt = clock.UtcNow
        };
        db.Customers.Add(entity);
        AddAudit("customer.created", "customer", entity.Id, entity);
        await db.SaveChangesAsync(cancellationToken);
        return await MapCustomerAsync(entity, cancellationToken);
    }

    public async Task<Page<CustomerView>> ListCustomersAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        var total = await db.Customers.LongCountAsync(cancellationToken);
        var customers = await db.Customers.AsNoTracking()
            .OrderBy(item => item.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var result = new List<CustomerView>();
        foreach (var customer in customers)
        {
            result.Add(await MapCustomerAsync(customer, cancellationToken));
        }

        return CreatePage(result, page, pageSize, total);
    }

    public async Task<SubscriptionView> CreateSubscriptionAsync(
        CreateSubscriptionCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Quantity <= 0)
        {
            throw new DomainException("Subscription quantity must be positive.");
        }

        if (!await db.Customers.AnyAsync(item => item.Id == command.CustomerId, cancellationToken))
        {
            throw new ResourceNotFoundException("Customer was not found.");
        }

        var version = await db.PlanVersions.SingleOrDefaultAsync(
            item => item.Id == command.PlanVersionId,
            cancellationToken)
            ?? throw new ResourceNotFoundException("Plan version was not found.");
        var plan = await db.Plans.SingleAsync(item => item.Id == version.PlanId, cancellationToken);
        if (command.CouponId is not null &&
            !await db.Coupons.AnyAsync(item => item.Id == command.CouponId, cancellationToken))
        {
            throw new ResourceNotFoundException("Coupon was not found.");
        }

        var start = command.StartAt ?? clock.UtcNow;
        var anchor = BillingCycleAnchor.From(start);
        var periodEnd = anchor.Next(
            start,
            new BillingInterval(plan.IntervalUnit, plan.IntervalCount));
        var domain = new Subscription(
            ids.NewGuid(),
            command.CustomerId,
            command.PlanVersionId,
            command.Quantity,
            start,
            periodEnd,
            anchor,
            command.TrialEnd);
        var entity = new SubscriptionEntity
        {
            Id = domain.Id,
            CustomerId = domain.CustomerId,
            PlanVersionId = domain.PlanVersionId,
            Quantity = domain.Quantity,
            State = domain.State,
            CurrentPeriodStart = domain.CurrentPeriodStart,
            CurrentPeriodEnd = domain.CurrentPeriodEnd,
            AnchorOrigin = domain.Anchor.Origin,
            AnchorDay = domain.Anchor.Day,
            AnchorIsMonthEnd = domain.Anchor.IsMonthEnd,
            TrialEnd = domain.TrialEnd,
            TrialEndBehavior = command.TrialEndBehavior,
            CancelAtPeriodEnd = false,
            IsAccessSuspended = false,
            CouponId = command.CouponId,
            CouponApplications = 0,
            PreviouslyActive = false,
            CreatedAt = clock.UtcNow,
            Version = 1
        };
        db.Subscriptions.Add(entity);
        Enqueue($"subscription.{entity.State.ToString().ToLowerInvariant()}", entity.Id, entity);
        AddAudit("subscription.created", "subscription", entity.Id, entity);
        await db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<Page<SubscriptionView>> ListSubscriptionsAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        var total = await db.Subscriptions.LongCountAsync(cancellationToken);
        var items = await db.Subscriptions.AsNoTracking()
            .OrderByDescending(item => item.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return CreatePage(items.Select(Map).ToList(), page, pageSize, total);
    }

    public async Task<SubscriptionView> ChangeSubscriptionAsync(
        Guid id,
        ChangeSubscriptionCommand command,
        CancellationToken cancellationToken)
    {
        var subscription = await GetSubscriptionAsync(id, cancellationToken);
        if (subscription.State is SubscriptionState.Canceled or SubscriptionState.Unpaid or SubscriptionState.Paused)
        {
            throw new DomainException($"A {subscription.State} subscription cannot change plan.");
        }

        if (command.Quantity <= 0)
        {
            throw new DomainException("Subscription quantity must be positive.");
        }

        var oldVersion = await db.PlanVersions.SingleAsync(
            item => item.Id == subscription.PlanVersionId,
            cancellationToken);
        var newVersion = await db.PlanVersions.SingleOrDefaultAsync(
            item => item.Id == command.PlanVersionId,
            cancellationToken)
            ?? throw new ResourceNotFoundException("Proposed plan version was not found.");
        var oldPrice = PricingStrategyFactory
            .Create(InvoiceGenerator.DeserializePricing(oldVersion.PricingJson), oldVersion.Currency)
            .Calculate(subscription.Quantity);
        var newPrice = PricingStrategyFactory
            .Create(InvoiceGenerator.DeserializePricing(newVersion.PricingJson), newVersion.Currency)
            .Calculate(command.Quantity);
        var changedAt = command.ChangeAt ?? clock.UtcNow;
        var proration = ProrationEngine.Calculate(
            oldPrice,
            newPrice,
            subscription.CurrentPeriodStart,
            subscription.CurrentPeriodEnd,
            changedAt,
            command.ProrationBehavior);
        var changeId = ids.NewGuid();
        foreach (var line in proration.Lines)
        {
            db.PendingCharges.Add(new PendingChargeEntity
            {
                Id = ids.NewGuid(),
                SubscriptionId = subscription.Id,
                Type = InvoiceLineType.Proration,
                Description = line.Description,
                PeriodStart = line.PeriodStart,
                PeriodEnd = line.PeriodEnd,
                AmountMinor = line.Amount.MinorUnits,
                Currency = line.Amount.Currency,
                Taxable = true,
                Invoiced = false
            });
        }

        db.SubscriptionChanges.Add(new SubscriptionChangeEntity
        {
            Id = changeId,
            SubscriptionId = subscription.Id,
            OldPlanVersionId = subscription.PlanVersionId,
            NewPlanVersionId = command.PlanVersionId,
            OldQuantity = subscription.Quantity,
            NewQuantity = command.Quantity,
            ChangedAt = changedAt,
            NetAmountMinor = proration.NetAmount.MinorUnits,
            Currency = proration.NetAmount.Currency,
            Behavior = command.ProrationBehavior
        });
        subscription.PlanVersionId = command.PlanVersionId;
        subscription.Quantity = command.Quantity;
        subscription.Version++;
        Enqueue("subscription.changed", subscription.Id, new
        {
            subscription.Id,
            changeId,
            command.PlanVersionId,
            command.Quantity,
            command.ProrationBehavior,
            NetProrationMinor = proration.NetAmount.MinorUnits
        });
        AddAudit("subscription.changed", "subscription", subscription.Id, subscription);
        await db.SaveChangesAsync(cancellationToken);

        if (proration.InvoiceImmediately && proration.Lines.Count > 0)
        {
            await invoiceGenerator.GenerateProrationInvoiceAsync(
                subscription.Id,
                changeId,
                cancellationToken);
        }

        return Map(subscription);
    }

    public async Task<SubscriptionView> PauseSubscriptionAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var subscription = await GetSubscriptionAsync(id, cancellationToken);
        if (subscription.State is not (SubscriptionState.Active or SubscriptionState.Trialing or SubscriptionState.PastDue))
        {
            throw new DomainException($"A {subscription.State} subscription cannot be paused.");
        }

        subscription.StateBeforePause = subscription.State;
        subscription.State = SubscriptionState.Paused;
        subscription.Version++;
        Enqueue("subscription.paused", subscription.Id, subscription);
        await db.SaveChangesAsync(cancellationToken);
        return Map(subscription);
    }

    public async Task<SubscriptionView> ResumeSubscriptionAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var subscription = await GetSubscriptionAsync(id, cancellationToken);
        if (subscription.State != SubscriptionState.Paused)
        {
            throw new DomainException("Only a paused subscription can be resumed.");
        }

        subscription.State = subscription.StateBeforePause == SubscriptionState.Trialing
            ? SubscriptionState.Trialing
            : SubscriptionState.Active;
        subscription.StateBeforePause = null;
        subscription.Version++;
        Enqueue("subscription.resumed", subscription.Id, subscription);
        await db.SaveChangesAsync(cancellationToken);
        return Map(subscription);
    }

    public async Task<SubscriptionView> CancelSubscriptionAsync(
        Guid id,
        CancelSubscriptionCommand command,
        CancellationToken cancellationToken)
    {
        var subscription = await GetSubscriptionAsync(id, cancellationToken);
        if (subscription.State == SubscriptionState.Canceled)
        {
            return Map(subscription);
        }

        if (!command.Immediately && subscription.State == SubscriptionState.Unpaid)
        {
            throw new DomainException("An unpaid subscription must be canceled immediately.");
        }

        if (command.Immediately)
        {
            subscription.State = SubscriptionState.Canceled;
            subscription.CancelAtPeriodEnd = false;
            subscription.IsAccessSuspended = true;
        }
        else
        {
            subscription.CancelAtPeriodEnd = true;
        }

        subscription.Version++;
        Enqueue(
            command.Immediately ? "subscription.canceled" : "subscription.cancel_scheduled",
            subscription.Id,
            subscription);
        await db.SaveChangesAsync(cancellationToken);
        return Map(subscription);
    }

    public async Task<SubscriptionView> ReactivateSubscriptionAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var subscription = await GetSubscriptionAsync(id, cancellationToken);
        if (subscription.State != SubscriptionState.Canceled)
        {
            throw new DomainException("Only a canceled subscription can be reactivated.");
        }

        var version = await db.PlanVersions.SingleAsync(
            item => item.Id == subscription.PlanVersionId,
            cancellationToken);
        var plan = await db.Plans.SingleAsync(item => item.Id == version.PlanId, cancellationToken);
        var start = clock.UtcNow;
        var anchor = new BillingCycleAnchor(
            subscription.AnchorOrigin,
            subscription.AnchorDay,
            subscription.AnchorIsMonthEnd);
        subscription.CurrentPeriodStart = start;
        subscription.CurrentPeriodEnd = anchor.Next(
            start,
            new BillingInterval(plan.IntervalUnit, plan.IntervalCount));
        subscription.State = SubscriptionState.Active;
        subscription.CancelAtPeriodEnd = false;
        subscription.IsAccessSuspended = false;
        subscription.PreviouslyActive = true;
        subscription.Version++;
        Enqueue("subscription.reactivated", subscription.Id, subscription);
        await db.SaveChangesAsync(cancellationToken);
        return Map(subscription);
    }

    public async Task<OneOffChargeView> AddOneOffChargeAsync(
        Guid id,
        AddOneOffChargeCommand command,
        CancellationToken cancellationToken)
    {
        var subscription = await GetSubscriptionAsync(id, cancellationToken);
        if (subscription.State == SubscriptionState.Canceled)
        {
            throw new DomainException("Canceled subscriptions cannot receive new charges.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command.Description);
        if (command.AmountMinor == 0)
        {
            throw new DomainException("One-off charge amount cannot be zero.");
        }

        var version = await db.PlanVersions.SingleAsync(
            item => item.Id == subscription.PlanVersionId,
            cancellationToken);
        var amount = new OneOffPricing(new Money(command.AmountMinor, command.Currency))
            .Calculate(0);
        if (!string.Equals(amount.Currency, version.Currency, StringComparison.Ordinal))
        {
            throw new DomainException("One-off charge currency must match the subscription plan version.");
        }

        var entity = new PendingChargeEntity
        {
            Id = ids.NewGuid(),
            SubscriptionId = subscription.Id,
            Type = amount.MinorUnits < 0 ? InvoiceLineType.Credit : InvoiceLineType.OneOff,
            Description = command.Description.Trim(),
            PeriodStart = subscription.CurrentPeriodStart,
            PeriodEnd = subscription.CurrentPeriodEnd,
            AmountMinor = amount.MinorUnits,
            Currency = amount.Currency,
            Taxable = command.Taxable,
            Invoiced = false
        };
        db.PendingCharges.Add(entity);
        AddAudit("subscription.one_off_charge_added", "pending-charge", entity.Id, entity);
        await db.SaveChangesAsync(cancellationToken);
        if (command.InvoiceImmediately)
        {
            await invoiceGenerator.GenerateProrationInvoiceAsync(
                subscription.Id,
                entity.Id,
                cancellationToken);
        }

        return new OneOffChargeView(
            entity.Id,
            entity.SubscriptionId,
            entity.Description,
            entity.AmountMinor,
            entity.Currency,
            entity.Taxable,
            entity.Invoiced,
            entity.InvoiceId);
    }

    public async Task<UsageReceipt> RecordUsageAsync(
        RecordUsageCommand command,
        CancellationToken cancellationToken)
    {
        var duplicate = await db.UsageEvents.AsNoTracking().SingleOrDefaultAsync(
            item => item.EventId == command.EventId,
            cancellationToken);
        if (duplicate is not null)
        {
            var duplicateRollup = await db.UsageRollups.AsNoTracking().SingleAsync(
                item =>
                    item.SubscriptionId == duplicate.SubscriptionId &&
                    item.MeterId == duplicate.MeterId &&
                    item.PeriodStart == duplicate.RollupPeriodStart &&
                    item.PeriodEnd == duplicate.RollupPeriodEnd,
                cancellationToken);
            return new UsageReceipt(
                command.EventId,
                duplicate.Disposition,
                true,
                duplicateRollup.AggregateValue,
                duplicateRollup.BillableUnits);
        }

        var usage = new UsageEvent(
            command.EventId,
            command.SubscriptionId,
            command.MeterId,
            command.OccurredAt,
            command.Quantity,
            command.UniqueKey,
            command.AdjustmentOfEventId);
        var subscription = await GetSubscriptionAsync(command.SubscriptionId, cancellationToken);
        var meterEntity = await db.Meters.SingleOrDefaultAsync(
            item => item.Id == command.MeterId,
            cancellationToken)
            ?? throw new ResourceNotFoundException("Meter was not found.");
        if (meterEntity.Aggregation == UsageAggregationMode.UniqueCount &&
            string.IsNullOrWhiteSpace(command.UniqueKey))
        {
            throw new DomainException("Unique-count meters require a uniqueKey.");
        }

        var periodClosed = command.OccurredAt < subscription.CurrentPeriodStart ||
                           await db.Invoices.AnyAsync(
                               item =>
                                   item.SubscriptionId == subscription.Id &&
                                   item.PeriodStart <= command.OccurredAt &&
                                   item.PeriodEnd > command.OccurredAt &&
                                   item.Status != InvoiceStatus.Draft,
                               cancellationToken);
        var disposition = LateUsagePolicy.Decide(
            periodClosed,
            options.ClosedPeriodUsageBehavior);
        if (disposition == LateUsageDisposition.RejectedClosedPeriod)
        {
            throw new DomainException("Usage belongs to a closed billing period.");
        }

        var periodStart = subscription.CurrentPeriodStart;
        var periodEnd = subscription.CurrentPeriodEnd;
        var rollup = await db.UsageRollups.SingleOrDefaultAsync(
            item =>
                item.SubscriptionId == subscription.Id &&
                item.MeterId == meterEntity.Id &&
                item.PeriodStart == periodStart &&
                item.PeriodEnd == periodEnd,
            cancellationToken);
        if (rollup is null)
        {
            rollup = new UsageRollupEntity
            {
                Id = ids.NewGuid(),
                SubscriptionId = subscription.Id,
                MeterId = meterEntity.Id,
                PeriodStart = periodStart,
                PeriodEnd = periodEnd
            };
            db.UsageRollups.Add(rollup);
        }

        db.UsageEvents.Add(new UsageEventEntity
        {
            EventId = usage.EventId,
            SubscriptionId = usage.SubscriptionId,
            MeterId = usage.MeterId,
            OccurredAt = usage.OccurredAt,
            Quantity = usage.Quantity,
            UniqueKey = usage.UniqueKey,
            AdjustmentOfEventId = usage.AdjustmentOfEventId,
            RecordedAt = clock.UtcNow,
            RollupPeriodStart = periodStart,
            RollupPeriodEnd = periodEnd,
            Disposition = disposition
        });
        await UpdateRollupAsync(rollup, meterEntity, usage, cancellationToken);
        AddAudit("usage.recorded", "usage-event", usage.EventId, new
        {
            usage.EventId,
            usage.SubscriptionId,
            usage.MeterId,
            usage.OccurredAt,
            usage.Quantity,
            disposition
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var existing = await db.UsageEvents.AsNoTracking().SingleOrDefaultAsync(
                item => item.EventId == command.EventId,
                cancellationToken);
            if (existing is null)
            {
                throw;
            }

            var existingRollup = await db.UsageRollups.AsNoTracking().SingleAsync(
                item =>
                    item.SubscriptionId == existing.SubscriptionId &&
                    item.MeterId == existing.MeterId &&
                    item.PeriodStart == existing.RollupPeriodStart &&
                    item.PeriodEnd == existing.RollupPeriodEnd,
                cancellationToken);
            return new UsageReceipt(
                existing.EventId,
                existing.Disposition,
                true,
                existingRollup.AggregateValue,
                existingRollup.BillableUnits);
        }

        return new UsageReceipt(
            usage.EventId,
            disposition,
            false,
            rollup.AggregateValue,
            rollup.BillableUnits);
    }

    public async Task<InvoicePreviewView> PreviewInvoiceAsync(
        PreviewInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ProposedQuantity <= 0)
        {
            throw new DomainException("Proposed quantity must be positive.");
        }

        var subscription = await GetSubscriptionAsync(command.SubscriptionId, cancellationToken);
        var currentVersion = await db.PlanVersions.SingleAsync(
            item => item.Id == subscription.PlanVersionId,
            cancellationToken);
        var proposedVersion = await db.PlanVersions.SingleOrDefaultAsync(
            item => item.Id == command.ProposedPlanVersionId,
            cancellationToken)
            ?? throw new ResourceNotFoundException("Proposed plan version was not found.");
        var currentAmount = PricingStrategyFactory
            .Create(InvoiceGenerator.DeserializePricing(currentVersion.PricingJson), currentVersion.Currency)
            .Calculate(subscription.Quantity);
        var proposedAmount = PricingStrategyFactory
            .Create(InvoiceGenerator.DeserializePricing(proposedVersion.PricingJson), proposedVersion.Currency)
            .Calculate(command.ProposedQuantity);
        var asOf = command.AsOf ?? clock.UtcNow;
        var result = ProrationEngine.Calculate(
            currentAmount,
            proposedAmount,
            subscription.CurrentPeriodStart,
            subscription.CurrentPeriodEnd,
            asOf,
            command.ProrationBehavior);
        var lines = result.Lines.Select(line => new InvoiceLineView(
            ids.NewGuid(),
            InvoiceLineType.Proration,
            line.Description,
            line.PeriodStart,
            line.PeriodEnd,
            line.Amount.MinorUnits,
            line.Amount.Currency,
            true)).ToList();
        var customer = await db.Customers.AsNoTracking().SingleAsync(
            item => item.Id == subscription.CustomerId,
            cancellationToken);
        var discount = Money.Zero(result.NetAmount.Currency);
        if (subscription.CouponId is not null && result.NetAmount.MinorUnits > 0)
        {
            var couponEntity = await db.Coupons.AsNoTracking().SingleAsync(
                item => item.Id == subscription.CouponId,
                cancellationToken);
            var coupon = new Coupon(
                couponEntity.Id,
                couponEntity.Code,
                couponEntity.Type,
                couponEntity.Percentage,
                couponEntity.FixedAmountMinor is null
                    ? null
                    : new Money(couponEntity.FixedAmountMinor.Value, couponEntity.Currency!),
                couponEntity.Duration,
                couponEntity.DurationCycles,
                couponEntity.MaxRedemptions,
                couponEntity.RedemptionCount);
            discount = coupon.CalculateDiscount(
                new Money(result.NetAmount.MinorUnits, result.NetAmount.Currency),
                subscription.CouponApplications);
        }

        var taxLines = InvoiceGenerator.BuildDiscountedTaxLines(
            result.Lines.Select(line => new InvoiceLine(
                ids.NewGuid(),
                InvoiceLineType.Proration,
                line.Description,
                line.PeriodStart,
                line.PeriodEnd,
                line.Amount,
                true)).ToList(),
            discount);
        var tax = taxLines.Count == 0
            ? Money.Zero(result.NetAmount.Currency)
            : taxProvider.Calculate(new TaxRequest(
                customer.CountryCode,
                taxLines,
                proposedVersion.TaxInclusive
                    ? TaxPricingMode.Inclusive
                    : TaxPricingMode.Exclusive,
                options.TaxRoundingLevel,
                customer.TaxExempt,
                customer.ReverseCharge)).TotalTax;
        var beforeCredit = checked(
            result.NetAmount.MinorUnits -
            discount.MinorUnits +
            (proposedVersion.TaxInclusive ? 0 : tax.MinorUnits));
        var availableCredit = await db.Credits.AsNoTracking()
            .Where(item =>
                item.CustomerId == subscription.CustomerId &&
                item.Currency == result.NetAmount.Currency &&
                item.RemainingMinor > 0)
            .SumAsync(item => (long?)item.RemainingMinor, cancellationToken) ?? 0;
        var previewCredit = Math.Min(availableCredit, Math.Max(0, beforeCredit));
        var total = Math.Max(0, checked(beforeCredit - previewCredit));
        return new InvoicePreviewView(
            subscription.Id,
            subscription.PlanVersionId,
            command.ProposedPlanVersionId,
            asOf,
            lines,
            result.NetAmount.MinorUnits,
            discount.MinorUnits,
            tax.MinorUnits,
            previewCredit,
            total,
            result.NetAmount.Currency,
            result.InvoiceImmediately);
    }

    public Task<InvoiceRunResult> RunInvoicesAsync(CancellationToken cancellationToken) =>
        invoiceGenerator.RunDueAsync(cancellationToken);

    public async Task<Page<InvoiceView>> ListInvoicesAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        var total = await db.Invoices.LongCountAsync(cancellationToken);
        var invoices = await db.Invoices.AsNoTracking()
            .OrderByDescending(item => item.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var result = new List<InvoiceView>();
        foreach (var invoice in invoices)
        {
            result.Add(await MapInvoiceAsync(invoice, cancellationToken));
        }

        return CreatePage(result, page, pageSize, total);
    }

    public async Task<InvoiceView?> GetInvoiceAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == id,
            cancellationToken);
        return invoice is null ? null : await MapInvoiceAsync(invoice, cancellationToken);
    }

    public async Task<InvoiceView> VoidInvoiceAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(
            item => item.Id == id,
            cancellationToken)
            ?? throw new ResourceNotFoundException("Invoice was not found.");
        if (invoice.Status is InvoiceStatus.Paid or InvoiceStatus.Void)
        {
            throw new DomainException($"A {invoice.Status} invoice cannot be voided.");
        }

        invoice.Status = InvoiceStatus.Void;
        Enqueue("invoice.voided", invoice.Id, new { invoice.Id, invoice.Number });
        await db.SaveChangesAsync(cancellationToken);
        return await MapInvoiceAsync(invoice, cancellationToken);
    }

    public async Task<InvoiceView> PayInvoiceAsync(
        Guid id,
        string paymentMethodToken,
        CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(
            item => item.Id == id,
            cancellationToken)
            ?? throw new ResourceNotFoundException("Invoice was not found.");
        if (invoice.Status != InvoiceStatus.Open)
        {
            throw new DomainException("Only open invoices can be paid.");
        }

        var attemptNumber = await db.PaymentAttempts.CountAsync(
            item => item.InvoiceId == invoice.Id,
            cancellationToken) + 1;
        var paymentResult = await paymentProvider.ChargeAsync(
            new PaymentRequest(
                invoice.Id,
                new Money(invoice.TotalMinor, invoice.Currency),
                paymentMethodToken,
                $"invoice:{invoice.Id}:attempt:{attemptNumber}"),
            cancellationToken);
        db.PaymentAttempts.Add(new PaymentAttemptEntity
        {
            Id = ids.NewGuid(),
            InvoiceId = invoice.Id,
            AttemptNumber = attemptNumber,
            Outcome = paymentResult.Outcome,
            ProviderReference = paymentResult.ProviderReference,
            Message = paymentResult.Message,
            AttemptedAt = clock.UtcNow
        });
        var subscription = await GetSubscriptionAsync(invoice.SubscriptionId, cancellationToken);
        if (paymentResult.Succeeded)
        {
            invoice.Status = InvoiceStatus.Paid;
            RecoverSubscription(subscription);
            var dunning = await db.DunningCases.SingleOrDefaultAsync(
                item => item.InvoiceId == invoice.Id,
                cancellationToken);
            if (dunning is not null)
            {
                dunning.Recovered = true;
            }

            Enqueue("invoice.paid", invoice.Id, new
            {
                invoice.Id,
                invoice.Number,
                paymentResult.ProviderReference
            });
        }
        else
        {
            BillingTelemetry.FailedPayments.Add(1);
            if (subscription.State == SubscriptionState.Active)
            {
                subscription.State = SubscriptionState.PastDue;
            }

            if (!await db.DunningCases.AnyAsync(
                    item => item.InvoiceId == invoice.Id,
                    cancellationToken))
            {
                db.DunningCases.Add(new DunningCaseEntity
                {
                    Id = ids.NewGuid(),
                    InvoiceId = invoice.Id,
                    SubscriptionId = subscription.Id,
                    InitialFailureAt = clock.UtcNow,
                    PaymentMethodToken = paymentMethodToken
                });
            }

            Enqueue("invoice.payment_failed", invoice.Id, new
            {
                invoice.Id,
                invoice.Number,
                paymentResult.Outcome,
                paymentResult.ProviderReference
            });
        }

        subscription.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return await MapInvoiceAsync(invoice, cancellationToken);
    }

    public async Task<CreditNoteView> CreateCreditNoteAsync(
        Guid invoiceId,
        CreateCreditNoteCommand command,
        CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(
            item => item.Id == invoiceId,
            cancellationToken)
            ?? throw new ResourceNotFoundException("Invoice was not found.");
        if (command.AmountMinor <= 0 || command.AmountMinor > invoice.TotalMinor)
        {
            throw new DomainException("Credit-note amount must be positive and no greater than invoice total.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
        var next = await NextSequenceAsync("credit-note", cancellationToken);
        var entity = new CreditNoteEntity
        {
            Id = ids.NewGuid(),
            InvoiceId = invoice.Id,
            Number = $"CN-{next:000000}",
            AmountMinor = command.AmountMinor,
            Currency = invoice.Currency,
            Reason = command.Reason.Trim(),
            CreatedAt = clock.UtcNow
        };
        db.CreditNotes.Add(entity);
        db.Credits.Add(new CreditEntity
        {
            Id = ids.NewGuid(),
            CustomerId = invoice.CustomerId,
            OriginalMinor = command.AmountMinor,
            RemainingMinor = command.AmountMinor,
            Currency = invoice.Currency,
            CreatedAt = clock.UtcNow,
            Reason = $"Credit note {entity.Number}: {entity.Reason}"
        });
        Enqueue("invoice.credit_note_created", invoice.Id, entity);
        await db.SaveChangesAsync(cancellationToken);
        return new CreditNoteView(
            entity.Id,
            entity.InvoiceId,
            entity.Number,
            entity.AmountMinor,
            entity.Currency,
            entity.Reason,
            entity.CreatedAt);
    }

    public async Task<CouponView> CreateCouponAsync(
        CreateCouponCommand command,
        CancellationToken cancellationToken)
    {
        Money? fixedAmount = command.FixedAmountMinor is null
            ? null
            : new Money(command.FixedAmountMinor.Value, command.Currency!);
        var domain = new Coupon(
            ids.NewGuid(),
            command.Code,
            command.Type,
            command.Percentage,
            fixedAmount,
            command.Duration,
            command.DurationCycles,
            command.MaxRedemptions);
        var entity = new CouponEntity
        {
            Id = domain.Id,
            Code = domain.Code,
            Type = domain.Type,
            Percentage = domain.Percentage,
            FixedAmountMinor = domain.FixedAmount?.MinorUnits,
            Currency = domain.FixedAmount?.Currency,
            Duration = domain.Duration,
            DurationCycles = domain.DurationCycles,
            MaxRedemptions = domain.MaxRedemptions,
            RedemptionCount = 0,
            CreatedAt = clock.UtcNow
        };
        db.Coupons.Add(entity);
        AddAudit("coupon.created", "coupon", entity.Id, entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new ConflictException($"Coupon code already exists. {exception.Message}");
        }

        return Map(entity);
    }

    public async Task<Page<CouponView>> ListCouponsAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        var total = await db.Coupons.LongCountAsync(cancellationToken);
        var entities = await db.Coupons.AsNoTracking()
            .OrderBy(item => item.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return CreatePage(entities.Select(Map).ToList(), page, pageSize, total);
    }

    public async Task<CreditView> AddCreditAsync(
        AddCreditCommand command,
        CancellationToken cancellationToken)
    {
        if (!await db.Customers.AnyAsync(item => item.Id == command.CustomerId, cancellationToken))
        {
            throw new ResourceNotFoundException("Customer was not found.");
        }

        var amount = new Money(command.AmountMinor, command.Currency);
        if (amount.MinorUnits <= 0)
        {
            throw new DomainException("Credit amount must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
        var entity = new CreditEntity
        {
            Id = ids.NewGuid(),
            CustomerId = command.CustomerId,
            OriginalMinor = amount.MinorUnits,
            RemainingMinor = amount.MinorUnits,
            Currency = amount.Currency,
            CreatedAt = clock.UtcNow,
            Reason = command.Reason.Trim()
        };
        db.Credits.Add(entity);
        AddAudit("credit.added", "credit", entity.Id, entity);
        await db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<MrrReportView> GetMrrAsync(
        string currency,
        CancellationToken cancellationToken)
    {
        var normalized = Money.Zero(currency).Currency;
        var from = clock.UtcNow.AddMonths(-1);
        var subscriptions = await db.Subscriptions.AsNoTracking().ToListAsync(cancellationToken);
        var changes = await db.SubscriptionChanges.AsNoTracking()
            .Where(item => item.ChangedAt >= from && item.Currency == normalized)
            .OrderBy(item => item.ChangedAt)
            .ToListAsync(cancellationToken);
        var previous = new List<RecurringContractSnapshot>();
        var current = new List<RecurringContractSnapshot>();
        foreach (var subscription in subscriptions)
        {
            var currentSnapshot = await BuildMrrSnapshotAsync(
                subscription.Id,
                subscription.PlanVersionId,
                subscription.Quantity,
                subscription.State is SubscriptionState.Active or SubscriptionState.PastDue or SubscriptionState.Unpaid,
                subscription.PreviouslyActive,
                normalized,
                cancellationToken);
            if (currentSnapshot is not null)
            {
                current.Add(currentSnapshot);
            }

            if (subscription.CreatedAt > from)
            {
                continue;
            }

            var firstChange = changes.FirstOrDefault(item => item.SubscriptionId == subscription.Id);
            var previousSnapshot = await BuildMrrSnapshotAsync(
                subscription.Id,
                firstChange?.OldPlanVersionId ?? subscription.PlanVersionId,
                firstChange?.OldQuantity ?? subscription.Quantity,
                true,
                false,
                normalized,
                cancellationToken);
            if (previousSnapshot is not null)
            {
                previous.Add(previousSnapshot);
            }
        }

        var movement = RevenueReporting.CalculateMovement(previous, current, normalized);
        return new MrrReportView(
            normalized,
            movement.Opening.MinorUnits,
            movement.New.MinorUnits,
            movement.Expansion.MinorUnits,
            movement.Contraction.MinorUnits,
            movement.Churn.MinorUnits,
            movement.Reactivation.MinorUnits,
            movement.Closing.MinorUnits,
            movement.Arr.MinorUnits);
    }

    public async Task ProcessAsync(
        string eventType,
        Guid invoiceId,
        CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(
            item => item.Id == invoiceId,
            cancellationToken)
            ?? throw new ResourceNotFoundException("Invoice was not found.");
        var subscription = await GetSubscriptionAsync(invoice.SubscriptionId, cancellationToken);
        switch (eventType.Trim().ToLowerInvariant())
        {
            case "payment.succeeded":
                if (invoice.Status is InvoiceStatus.Open or InvoiceStatus.Uncollectible)
                {
                    invoice.Status = InvoiceStatus.Paid;
                }

                RecoverSubscription(subscription);
                var dunning = await db.DunningCases.SingleOrDefaultAsync(
                    item => item.InvoiceId == invoice.Id,
                    cancellationToken);
                if (dunning is not null)
                {
                    dunning.Recovered = true;
                }

                Enqueue("invoice.paid", invoice.Id, new { invoice.Id, Source = "payment-webhook" });
                break;
            case "payment.failed":
                if (subscription.State == SubscriptionState.Active)
                {
                    subscription.State = SubscriptionState.PastDue;
                }

                BillingTelemetry.FailedPayments.Add(1);
                Enqueue("invoice.payment_failed", invoice.Id, new { invoice.Id, Source = "payment-webhook" });
                break;
            default:
                throw new DomainException("Unsupported payment webhook event type.");
        }

        subscription.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdateRollupAsync(
        UsageRollupEntity rollup,
        MeterEntity meter,
        UsageEvent usage,
        CancellationToken cancellationToken)
    {
        rollup.EventCount++;
        switch (meter.Aggregation)
        {
            case UsageAggregationMode.Sum:
                rollup.SumValue += usage.Quantity;
                rollup.AggregateValue = rollup.SumValue;
                break;
            case UsageAggregationMode.Max:
                rollup.MaxValue = rollup.EventCount == 1
                    ? usage.Quantity
                    : Math.Max(rollup.MaxValue, usage.Quantity);
                rollup.AggregateValue = rollup.MaxValue;
                break;
            case UsageAggregationMode.LastValue:
                if (rollup.LastOccurredAt is null ||
                    usage.OccurredAt > rollup.LastOccurredAt ||
                    (usage.OccurredAt == rollup.LastOccurredAt &&
                     string.CompareOrdinal(usage.EventId, rollup.LastEventId) > 0))
                {
                    rollup.LastValue = usage.Quantity;
                    rollup.LastOccurredAt = usage.OccurredAt;
                    rollup.LastEventId = usage.EventId;
                }

                rollup.AggregateValue = rollup.LastValue;
                break;
            case UsageAggregationMode.UniqueCount:
                var exists = await db.UsageUniqueKeys.AnyAsync(
                    item =>
                        item.SubscriptionId == usage.SubscriptionId &&
                        item.MeterId == usage.MeterId &&
                        item.PeriodStart == rollup.PeriodStart &&
                        item.UniqueKey == usage.UniqueKey,
                    cancellationToken);
                if (!exists)
                {
                    db.UsageUniqueKeys.Add(new UsageUniqueKeyEntity
                    {
                        Id = ids.NewGuid(),
                        SubscriptionId = usage.SubscriptionId,
                        MeterId = usage.MeterId,
                        PeriodStart = rollup.PeriodStart,
                        UniqueKey = usage.UniqueKey!
                    });
                    rollup.UniqueCount++;
                }

                rollup.AggregateValue = rollup.UniqueCount;
                break;
            default:
                throw new DomainException("Unsupported aggregation mode.");
        }

        var definition = new MeterDefinition(
            meter.Id,
            meter.Name,
            meter.Unit,
            meter.Aggregation,
            meter.RoundingIncrement,
            meter.RoundingMode);
        rollup.BillableUnits = definition.RoundToBillableUnits(rollup.AggregateValue);
    }

    private async Task<RecurringContractSnapshot?> BuildMrrSnapshotAsync(
        Guid subscriptionId,
        Guid versionId,
        long quantity,
        bool active,
        bool previouslyActive,
        string currency,
        CancellationToken cancellationToken)
    {
        var version = await db.PlanVersions.AsNoTracking().SingleAsync(
            item => item.Id == versionId,
            cancellationToken);
        if (!string.Equals(version.Currency, currency, StringComparison.Ordinal))
        {
            return null;
        }

        var plan = await db.Plans.AsNoTracking().SingleAsync(
            item => item.Id == version.PlanId,
            cancellationToken);
        var amount = PricingStrategyFactory
            .Create(InvoiceGenerator.DeserializePricing(version.PricingJson), version.Currency)
            .Calculate(quantity);
        return new RecurringContractSnapshot(
            subscriptionId,
            amount,
            new BillingInterval(plan.IntervalUnit, plan.IntervalCount),
            active,
            previouslyActive);
    }

    private async Task<SubscriptionEntity> GetSubscriptionAsync(
        Guid id,
        CancellationToken cancellationToken) =>
        await db.Subscriptions.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
        ?? throw new ResourceNotFoundException("Subscription was not found.");

    private async Task<InvoiceView> MapInvoiceAsync(
        InvoiceEntity invoice,
        CancellationToken cancellationToken)
    {
        var lines = await db.InvoiceLines.AsNoTracking()
            .Where(item => item.InvoiceId == invoice.Id)
            .OrderBy(item => item.PeriodStart)
            .ThenBy(item => item.Id)
            .Select(item => new InvoiceLineView(
                item.Id,
                item.Type,
                item.Description,
                item.PeriodStart,
                item.PeriodEnd,
                item.AmountMinor,
                item.Currency,
                item.Taxable))
            .ToListAsync(cancellationToken);
        return new InvoiceView(
            invoice.Id,
            invoice.Number,
            invoice.CustomerId,
            invoice.SubscriptionId,
            invoice.Status,
            invoice.PeriodStart,
            invoice.PeriodEnd,
            invoice.SubtotalMinor,
            invoice.DiscountMinor,
            invoice.TaxMinor,
            invoice.CreditAppliedMinor,
            invoice.TotalMinor,
            invoice.Currency,
            lines);
    }

    private async Task<CustomerView> MapCustomerAsync(
        CustomerEntity entity,
        CancellationToken cancellationToken)
    {
        var creditBalance = await db.Credits
            .Where(item => item.CustomerId == entity.Id && item.Currency == entity.Currency)
            .SumAsync(item => (long?)item.RemainingMinor, cancellationToken) ?? 0;
        return new CustomerView(
            entity.Id,
            entity.Name,
            entity.Currency,
            entity.CountryCode,
            entity.TaxExempt,
            entity.ReverseCharge,
            creditBalance);
    }

    private async Task<long> NextSequenceAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var sequence = await db.Sequences.SingleOrDefaultAsync(
            item => item.Name == name,
            cancellationToken);
        if (sequence is null)
        {
            db.Sequences.Add(new SequenceEntity { Name = name, NextValue = 2 });
            return 1;
        }

        var value = sequence.NextValue;
        sequence.NextValue = checked(value + 1);
        return value;
    }

    private void RecoverSubscription(SubscriptionEntity subscription)
    {
        if (subscription.State is SubscriptionState.PastDue or SubscriptionState.Unpaid)
        {
            subscription.State = SubscriptionState.Active;
        }

        subscription.IsAccessSuspended = false;
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
                Attempts = 0,
                NextAttemptAt = clock.UtcNow,
                Status = OutboundWebhookStatus.Pending
            });
        }

        AddAudit(eventType, "domain-event", resourceId, payload);
    }

    private void AddAudit(string action, string resourceType, object resourceId, object state)
    {
        var stateJson = JsonSerializer.Serialize(state);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stateJson)))
            .ToLowerInvariant();
        db.AuditRecords.Add(new AuditRecordEntity
        {
            Id = ids.NewGuid(),
            Actor = "billing-engine",
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId.ToString() ?? string.Empty,
            OccurredAt = clock.UtcNow,
            CorrelationId = string.Empty,
            Source = "application",
            StateHash = hash
        });
    }

    private static ProductView Map(ProductEntity entity) =>
        new(entity.Id, entity.Name, entity.Description, entity.IsActive);

    private static PlanVersionView Map(PlanVersionEntity entity) =>
        new(
            entity.Id,
            entity.Version,
            entity.EffectiveFrom,
            entity.Currency,
            InvoiceGenerator.DeserializePricing(entity.PricingJson),
            entity.TaxInclusive);

    private static PlanView Map(
        PlanEntity entity,
        IReadOnlyList<PlanVersionEntity> versions) =>
        new(
            entity.Id,
            entity.ProductId,
            entity.Name,
            entity.IntervalUnit,
            entity.IntervalCount,
            entity.MeterId,
            versions.Select(Map).ToList());

    private static SubscriptionView Map(SubscriptionEntity entity) =>
        new(
            entity.Id,
            entity.CustomerId,
            entity.PlanVersionId,
            entity.Quantity,
            entity.State,
            entity.CurrentPeriodStart,
            entity.CurrentPeriodEnd,
            entity.AnchorDay,
            entity.AnchorIsMonthEnd,
            entity.TrialEnd,
            entity.CancelAtPeriodEnd,
            entity.IsAccessSuspended);

    private static MeterView Map(MeterEntity entity) =>
        new(
            entity.Id,
            entity.Name,
            entity.Unit,
            entity.Aggregation,
            entity.RoundingIncrement,
            entity.RoundingMode);

    private static CouponView Map(CouponEntity entity) =>
        new(
            entity.Id,
            entity.Code,
            entity.Type,
            entity.Percentage,
            entity.FixedAmountMinor,
            entity.Currency,
            entity.Duration,
            entity.DurationCycles,
            entity.MaxRedemptions,
            entity.RedemptionCount);

    private static CreditView Map(CreditEntity entity) =>
        new(
            entity.Id,
            entity.CustomerId,
            entity.OriginalMinor,
            entity.RemainingMinor,
            entity.Currency,
            entity.CreatedAt,
            entity.Reason);

    private static (int Page, int PageSize) NormalizePage(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, 100));

    private static Page<T> CreatePage<T>(
        IReadOnlyList<T> items,
        int page,
        int pageSize,
        long total) =>
        new(
            items,
            page,
            pageSize,
            total,
            total == 0 ? 0 : checked((int)((total + pageSize - 1) / pageSize)));
}
