using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SubscriptionBilling.Application;
using SubscriptionBilling.Domain;

namespace SubscriptionBilling.Infrastructure;

public sealed class InvoiceGenerator(
    BillingDbContext db,
    IClock clock,
    IIdGenerator ids,
    ITaxProvider taxProvider,
    BillingRuntimeOptions options)
{
    public async Task<InvoiceRunResult> RunDueAsync(CancellationToken cancellationToken)
    {
        using var activity = BillingTelemetry.ActivitySource.StartActivity("invoice.run");
        var started = Stopwatch.GetTimestamp();
        var generatedIds = new List<Guid>();
        var alreadyExisted = 0;

        await CompleteDueTrialsAsync(cancellationToken);
        var due = await db.Subscriptions
            .Where(item =>
                (item.State == SubscriptionState.Active ||
                 item.State == SubscriptionState.PastDue) &&
                item.CurrentPeriodEnd <= clock.UtcNow)
            .OrderBy(item => item.CurrentPeriodEnd)
            .ToListAsync(cancellationToken);

        foreach (var subscription in due)
        {
            var exists = await db.Invoices.AnyAsync(
                invoice =>
                    invoice.SubscriptionId == subscription.Id &&
                    invoice.PeriodStart == subscription.CurrentPeriodStart &&
                    invoice.PeriodEnd == subscription.CurrentPeriodEnd &&
                    invoice.BillingReason == "periodic",
                cancellationToken);
            if (exists)
            {
                alreadyExisted++;
                await AdvanceSubscriptionPeriodAsync(subscription, cancellationToken);
                continue;
            }

            try
            {
                var invoice = await GeneratePeriodicInvoiceAsync(subscription, cancellationToken);
                generatedIds.Add(invoice.Id);
                BillingTelemetry.InvoicesGenerated.Add(1);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                alreadyExisted++;
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(started).Ticks / TimeSpan.TicksPerMillisecond;
        BillingTelemetry.InvoiceRunDurationMilliseconds.Record(elapsed);
        return new InvoiceRunResult(generatedIds.Count, alreadyExisted, generatedIds);
    }

    public async Task<InvoiceEntity?> GenerateProrationInvoiceAsync(
        Guid subscriptionId,
        Guid changeId,
        CancellationToken cancellationToken)
    {
        var billingReason = changeId.ToString("N");
        var existing = await db.Invoices.SingleOrDefaultAsync(
            item => item.SubscriptionId == subscriptionId && item.BillingReason == billingReason,
            cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var subscription = await db.Subscriptions.SingleAsync(
            item => item.Id == subscriptionId,
            cancellationToken);
        var charges = await db.PendingCharges
            .Where(item => item.SubscriptionId == subscriptionId && !item.Invoiced)
            .OrderBy(item => item.PeriodStart)
            .ToListAsync(cancellationToken);
        if (charges.Count == 0)
        {
            return null;
        }

        var version = await db.PlanVersions.SingleAsync(
            item => item.Id == subscription.PlanVersionId,
            cancellationToken);
        var lines = charges.Select(charge => new InvoiceLine(
            charge.Id,
            charge.Type,
            charge.Description,
            charge.PeriodStart,
            charge.PeriodEnd,
            new Money(charge.AmountMinor, charge.Currency),
            charge.Taxable,
            false)).ToList();
        var invoice = await PersistInvoiceAsync(
            subscription,
            version,
            lines,
            billingReason,
            cancellationToken);
        foreach (var charge in charges)
        {
            charge.Invoiced = true;
            charge.InvoiceId = invoice.Id;
        }

        await db.SaveChangesAsync(cancellationToken);
        BillingTelemetry.InvoicesGenerated.Add(1);
        return invoice;
    }

    private async Task CompleteDueTrialsAsync(CancellationToken cancellationToken)
    {
        var trials = await db.Subscriptions
            .Where(item =>
                item.State == SubscriptionState.Trialing &&
                item.TrialEnd != null &&
                item.TrialEnd <= clock.UtcNow)
            .ToListAsync(cancellationToken);
        foreach (var subscription in trials)
        {
            switch (subscription.TrialEndBehavior)
            {
                case TrialEndBehavior.Activate:
                    subscription.State = SubscriptionState.Active;
                    subscription.CurrentPeriodStart = subscription.TrialEnd!.Value;
                    await SetNextPeriodEndAsync(subscription, cancellationToken);
                    Enqueue("subscription.activated", subscription.Id, subscription);
                    break;
                case TrialEndBehavior.Cancel:
                    subscription.State = SubscriptionState.Canceled;
                    subscription.IsAccessSuspended = true;
                    Enqueue("subscription.canceled", subscription.Id, subscription);
                    break;
                case TrialEndBehavior.Pause:
                    subscription.StateBeforePause = SubscriptionState.Active;
                    subscription.State = SubscriptionState.Paused;
                    Enqueue("subscription.paused", subscription.Id, subscription);
                    break;
            }
        }

        if (trials.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<InvoiceEntity> GeneratePeriodicInvoiceAsync(
        SubscriptionEntity subscription,
        CancellationToken cancellationToken)
    {
        var version = await db.PlanVersions.SingleAsync(
            item => item.Id == subscription.PlanVersionId,
            cancellationToken);
        var plan = await db.Plans.SingleAsync(
            item => item.Id == version.PlanId,
            cancellationToken);
        var pricing = PricingStrategyFactory.Create(
            DeserializePricing(version.PricingJson),
            version.Currency);
        var units = subscription.Quantity;
        if (plan.MeterId is not null)
        {
            var rollup = await db.UsageRollups.SingleOrDefaultAsync(
                item =>
                    item.SubscriptionId == subscription.Id &&
                    item.MeterId == plan.MeterId &&
                    item.PeriodStart == subscription.CurrentPeriodStart &&
                    item.PeriodEnd == subscription.CurrentPeriodEnd,
                cancellationToken);
            units = rollup?.BillableUnits ?? 0;
        }

        var recurring = pricing.Calculate(units);
        var lines = new List<InvoiceLine>
        {
            new(
                ids.NewGuid(),
                pricing.Model == PricingModel.OneOff
                    ? InvoiceLineType.OneOff
                    : plan.MeterId is null
                        ? InvoiceLineType.Recurring
                        : InvoiceLineType.Usage,
                $"{pricing.Model} charge for {units} unit(s)",
                subscription.CurrentPeriodStart,
                subscription.CurrentPeriodEnd,
                recurring,
                true,
                pricing.Model != PricingModel.OneOff)
        };

        var pending = await db.PendingCharges
            .Where(item => item.SubscriptionId == subscription.Id && !item.Invoiced)
            .OrderBy(item => item.PeriodStart)
            .ToListAsync(cancellationToken);
        lines.AddRange(pending.Select(charge => new InvoiceLine(
            charge.Id,
            charge.Type,
            charge.Description,
            charge.PeriodStart,
            charge.PeriodEnd,
            new Money(charge.AmountMinor, charge.Currency),
            charge.Taxable)));

        var invoice = await PersistInvoiceAsync(
            subscription,
            version,
            lines,
            "periodic",
            cancellationToken);
        foreach (var charge in pending)
        {
            charge.Invoiced = true;
            charge.InvoiceId = invoice.Id;
        }

        var rollups = await db.UsageRollups
            .Where(item =>
                item.SubscriptionId == subscription.Id &&
                item.PeriodStart == subscription.CurrentPeriodStart &&
                item.PeriodEnd == subscription.CurrentPeriodEnd)
            .ToListAsync(cancellationToken);
        foreach (var rollup in rollups)
        {
            rollup.Closed = true;
        }

        await AdvanceSubscriptionPeriodAsync(subscription, cancellationToken, save: false);
        await db.SaveChangesAsync(cancellationToken);
        return invoice;
    }

    private async Task<InvoiceEntity> PersistInvoiceAsync(
        SubscriptionEntity subscription,
        PlanVersionEntity version,
        IReadOnlyList<InvoiceLine> lines,
        string billingReason,
        CancellationToken cancellationToken)
    {
        var customer = await db.Customers.SingleAsync(
            item => item.Id == subscription.CustomerId,
            cancellationToken);
        var invoiceId = ids.NewGuid();
        var invoice = new Invoice(
            invoiceId,
            customer.Id,
            subscription.Id,
            lines.Min(item => item.PeriodStart),
            lines.Max(item => item.PeriodEnd),
            version.Currency,
            clock.UtcNow);
        foreach (var line in lines)
        {
            invoice.AddLine(line);
        }

        var discount = await CalculateDiscountAsync(
            subscription,
            invoice,
            cancellationToken);
        var taxableLines = BuildDiscountedTaxLines(lines, discount);
        var tax = taxableLines.Count == 0
            ? Money.Zero(version.Currency)
            : taxProvider.Calculate(new TaxRequest(
                customer.CountryCode,
                taxableLines,
                version.TaxInclusive ? TaxPricingMode.Inclusive : TaxPricingMode.Exclusive,
                options.TaxRoundingLevel,
                customer.TaxExempt,
                customer.ReverseCharge)).TotalTax;
        var beforeCredit = checked(
            invoice.Subtotal.MinorUnits -
            discount.MinorUnits +
            (version.TaxInclusive ? 0 : tax.MinorUnits));
        var creditApplied = await ApplyCreditsAsync(
            customer.Id,
            invoiceId,
            new Money(Math.Max(0, beforeCredit), version.Currency),
            cancellationToken);
        invoice.Recalculate(discount, tax, creditApplied, version.TaxInclusive);
        invoice.FinalizeInvoice(await NextNumberAsync("invoice", "INV", cancellationToken), clock.UtcNow);
        if (beforeCredit < 0)
        {
            var creditAmount = checked(-beforeCredit);
            var creditNoteNumber = await NextNumberAsync(
                "credit-note",
                "CN",
                cancellationToken);
            db.CreditNotes.Add(new CreditNoteEntity
            {
                Id = ids.NewGuid(),
                InvoiceId = invoice.Id,
                Number = creditNoteNumber,
                AmountMinor = creditAmount,
                Currency = invoice.Currency,
                Reason = "Automatic credit from a negative invoice adjustment.",
                CreatedAt = clock.UtcNow
            });
            db.Credits.Add(new CreditEntity
            {
                Id = ids.NewGuid(),
                CustomerId = customer.Id,
                OriginalMinor = creditAmount,
                RemainingMinor = creditAmount,
                Currency = invoice.Currency,
                CreatedAt = clock.UtcNow,
                Reason = $"Automatic credit from {invoice.Number}"
            });
        }

        var entity = new InvoiceEntity
        {
            Id = invoice.Id,
            Number = invoice.Number,
            CustomerId = invoice.CustomerId,
            SubscriptionId = invoice.SubscriptionId,
            Status = invoice.Status,
            PeriodStart = invoice.PeriodStart,
            PeriodEnd = invoice.PeriodEnd,
            SubtotalMinor = invoice.Subtotal.MinorUnits,
            DiscountMinor = invoice.Discount.MinorUnits,
            TaxMinor = invoice.Tax.MinorUnits,
            CreditAppliedMinor = invoice.CreditApplied.MinorUnits,
            TotalMinor = invoice.Total.MinorUnits,
            Currency = invoice.Currency,
            CreatedAt = invoice.CreatedAt,
            FinalizedAt = invoice.FinalizedAt,
            BillingReason = billingReason
        };
        db.Invoices.Add(entity);
        db.InvoiceLines.AddRange(lines.Select(line => new InvoiceLineEntity
        {
            Id = line.Id,
            InvoiceId = invoice.Id,
            Type = line.Type,
            Description = line.Description,
            PeriodStart = line.PeriodStart,
            PeriodEnd = line.PeriodEnd,
            AmountMinor = line.Amount.MinorUnits,
            Currency = line.Amount.Currency,
            Taxable = line.Taxable,
            RevenueRecognizedOverPeriod = line.RevenueRecognizedOverPeriod
        }));
        Enqueue("invoice.created", invoice.Id, new
        {
            invoice.Id,
            invoice.Number,
            invoice.Status,
            TotalMinor = invoice.Total.MinorUnits,
            invoice.Currency
        });
        AddAudit("invoice.finalized", "invoice", invoice.Id.ToString(), entity);
        return entity;
    }

    private async Task<Money> CalculateDiscountAsync(
        SubscriptionEntity subscription,
        Invoice invoice,
        CancellationToken cancellationToken)
    {
        if (subscription.CouponId is null)
        {
            return Money.Zero(invoice.Currency);
        }

        var couponEntity = await db.Coupons.SingleAsync(
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
        var discount = coupon.CalculateDiscount(
            new Money(Math.Max(0, invoice.Subtotal.MinorUnits), invoice.Currency),
            subscription.CouponApplications);
        if (discount.MinorUnits > 0)
        {
            couponEntity.RedemptionCount = coupon.RedemptionCount;
            subscription.CouponApplications++;
            db.CouponRedemptions.Add(new CouponRedemptionEntity
            {
                Id = ids.NewGuid(),
                CouponId = couponEntity.Id,
                SubscriptionId = subscription.Id,
                InvoiceId = invoice.Id,
                AmountMinor = discount.MinorUnits,
                Currency = discount.Currency,
                RedeemedAt = clock.UtcNow
            });
        }

        return discount;
    }

    internal static List<TaxLine> BuildDiscountedTaxLines(
        IReadOnlyList<InvoiceLine> lines,
        Money discount)
    {
        var remainingDiscount = discount.MinorUnits;
        var result = new List<TaxLine>();
        foreach (var line in lines.Where(item => item.Taxable && item.Amount.MinorUnits != 0))
        {
            var deduction = line.Amount.MinorUnits > 0
                ? Math.Min(line.Amount.MinorUnits, remainingDiscount)
                : 0;
            remainingDiscount -= deduction;
            var taxable = checked(line.Amount.MinorUnits - deduction);
            if (taxable != 0)
            {
                result.Add(new TaxLine(line.Id, new Money(taxable, line.Amount.Currency)));
            }
        }

        return result;
    }

    private async Task<Money> ApplyCreditsAsync(
        Guid customerId,
        Guid invoiceId,
        Money amountDue,
        CancellationToken cancellationToken)
    {
        var credits = await db.Credits
            .Where(item =>
                item.CustomerId == customerId &&
                item.Currency == amountDue.Currency &&
                item.RemainingMinor > 0)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var remaining = amountDue.MinorUnits;
        long appliedTotal = 0;
        foreach (var credit in credits)
        {
            if (remaining == 0)
            {
                break;
            }

            var applied = Math.Min(credit.RemainingMinor, remaining);
            credit.RemainingMinor -= applied;
            remaining -= applied;
            appliedTotal = checked(appliedTotal + applied);
            db.CreditApplications.Add(new CreditApplicationEntity
            {
                Id = ids.NewGuid(),
                CreditId = credit.Id,
                InvoiceId = invoiceId,
                AmountMinor = applied,
                Currency = amountDue.Currency,
                AppliedAt = clock.UtcNow
            });
        }

        return new Money(appliedTotal, amountDue.Currency);
    }

    private async Task AdvanceSubscriptionPeriodAsync(
        SubscriptionEntity subscription,
        CancellationToken cancellationToken,
        bool save = true)
    {
        if (subscription.CancelAtPeriodEnd)
        {
            subscription.State = SubscriptionState.Canceled;
            subscription.CancelAtPeriodEnd = false;
            subscription.IsAccessSuspended = true;
            Enqueue("subscription.canceled", subscription.Id, subscription);
        }
        else
        {
            subscription.CurrentPeriodStart = subscription.CurrentPeriodEnd;
            await SetNextPeriodEndAsync(subscription, cancellationToken);
        }

        subscription.Version++;
        if (save)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task SetNextPeriodEndAsync(
        SubscriptionEntity subscription,
        CancellationToken cancellationToken)
    {
        var version = await db.PlanVersions.SingleAsync(
            item => item.Id == subscription.PlanVersionId,
            cancellationToken);
        var plan = await db.Plans.SingleAsync(
            item => item.Id == version.PlanId,
            cancellationToken);
        var anchor = new BillingCycleAnchor(
            subscription.AnchorOrigin,
            subscription.AnchorDay,
            subscription.AnchorIsMonthEnd);
        subscription.CurrentPeriodEnd = anchor.Next(
            subscription.CurrentPeriodStart,
            new BillingInterval(plan.IntervalUnit, plan.IntervalCount));
    }

    private async Task<string> NextNumberAsync(
        string sequenceName,
        string prefix,
        CancellationToken cancellationToken)
    {
        var sequence = await db.Sequences.SingleOrDefaultAsync(
            item => item.Name == sequenceName,
            cancellationToken);
        long value;
        if (sequence is null)
        {
            value = 1;
            db.Sequences.Add(new SequenceEntity
            {
                Name = sequenceName,
                NextValue = 2
            });
        }
        else
        {
            value = sequence.NextValue;
            sequence.NextValue = checked(sequence.NextValue + 1);
        }

        return $"{prefix}-{value:000000}";
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

        AddAudit(eventType, "domain-event", resourceId.ToString(), payload);
    }

    private void AddAudit(string action, string resourceType, string resourceId, object state)
    {
        var stateJson = JsonSerializer.Serialize(state);
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(stateJson))).ToLowerInvariant();
        db.AuditRecords.Add(new AuditRecordEntity
        {
            Id = ids.NewGuid(),
            Actor = "billing-engine",
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            OccurredAt = clock.UtcNow,
            CorrelationId = string.Empty,
            Source = "internal",
            StateHash = hash
        });
    }

    internal static PricingConfiguration DeserializePricing(string json) =>
        JsonSerializer.Deserialize<PricingConfiguration>(json)
        ?? throw new DomainException("Stored pricing configuration is invalid.");
}
