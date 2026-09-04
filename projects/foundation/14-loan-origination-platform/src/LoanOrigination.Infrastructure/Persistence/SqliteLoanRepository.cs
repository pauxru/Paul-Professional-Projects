using System.Text.Json;
using System.Text.Json.Serialization;
using LoanOrigination.Application.Ports;
using LoanOrigination.Domain.Models;
using LoanOrigination.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace LoanOrigination.Infrastructure.Persistence;

public sealed class SqliteLoanRepository(LoanDbContext dbContext) : ILoanRepository
{
    public async Task AddCustomerAsync(CustomerProfile customer, CancellationToken cancellationToken)
    {
        dbContext.Customers.Add(new CustomerRow
        {
            Id = customer.Id,
            LegalName = customer.LegalName,
            DeduplicationKey = customer.DeduplicationKey,
            CreatedAtUnixMilliseconds = customer.CreatedAt.ToUnixTimeMilliseconds(),
            Payload = JsonStore.Serialize(customer)
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<CustomerProfile?> GetCustomerAsync(Guid id, CancellationToken cancellationToken) =>
        (await dbContext.Customers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken)) is { } row
            ? JsonStore.Deserialize<CustomerProfile>(row.Payload)
            : null;

    public async Task<IReadOnlyList<CustomerProfile>> ListCustomersAsync(CancellationToken cancellationToken) =>
        (await dbContext.Customers.AsNoTracking().ToListAsync(cancellationToken))
        .Select(row => JsonStore.Deserialize<CustomerProfile>(row.Payload))
        .ToArray();

    public async Task SaveCustomerAsync(CustomerProfile customer, CancellationToken cancellationToken)
    {
        var row = await dbContext.Customers.SingleOrDefaultAsync(candidate => candidate.Id == customer.Id, cancellationToken)
            ?? throw new DomainException("Customer was not found.");
        row.LegalName = customer.LegalName;
        row.DeduplicationKey = customer.DeduplicationKey;
        row.Payload = JsonStore.Serialize(customer);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddProductAsync(LoanProductVersion product, CancellationToken cancellationToken)
    {
        dbContext.Products.Add(new ProductRow
        {
            Id = product.Id,
            ProductCode = product.ProductCode,
            Version = product.Version,
            EffectiveFromUnixMilliseconds = product.EffectiveFrom.ToUnixTimeMilliseconds(),
            Payload = JsonStore.Serialize(product)
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<LoanProductVersion?> GetProductAsync(string productCode, int version, CancellationToken cancellationToken) =>
        (await dbContext.Products.AsNoTracking().SingleOrDefaultAsync(
            row => row.ProductCode == productCode.ToUpperInvariant() && row.Version == version,
            cancellationToken)) is { } row
                ? JsonStore.Deserialize<LoanProductVersion>(row.Payload)
                : null;

    public async Task<IReadOnlyList<LoanProductVersion>> ListProductsAsync(CancellationToken cancellationToken) =>
        (await dbContext.Products.AsNoTracking().ToListAsync(cancellationToken))
        .Select(row => JsonStore.Deserialize<LoanProductVersion>(row.Payload))
        .ToArray();

    public async Task AddRulesetAsync(RuleSetDefinition ruleset, CancellationToken cancellationToken)
    {
        dbContext.Rulesets.Add(new RulesetRow
        {
            RulesetId = ruleset.Id,
            Version = ruleset.Version,
            EffectiveFromUnixMilliseconds = ruleset.EffectiveFrom.ToUnixTimeMilliseconds(),
            Payload = JsonStore.Serialize(ruleset)
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<RuleSetDefinition?> GetRulesetAsync(string id, int version, CancellationToken cancellationToken) =>
        (await dbContext.Rulesets.AsNoTracking().SingleOrDefaultAsync(
            row => row.RulesetId == id && row.Version == version,
            cancellationToken)) is { } row
                ? JsonStore.Deserialize<RuleSetDefinition>(row.Payload)
                : null;

    public async Task<IReadOnlyList<RuleSetDefinition>> ListRulesetsAsync(CancellationToken cancellationToken) =>
        (await dbContext.Rulesets.AsNoTracking().ToListAsync(cancellationToken))
        .Select(row => JsonStore.Deserialize<RuleSetDefinition>(row.Payload))
        .ToArray();

    public async Task AddApplicationAsync(LoanApplication application, CancellationToken cancellationToken)
    {
        dbContext.Applications.Add(ToRow(application));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<LoanApplication?> GetApplicationAsync(Guid id, CancellationToken cancellationToken) =>
        (await dbContext.Applications.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken)) is { } row
            ? JsonStore.Deserialize<LoanApplication>(row.Payload)
            : null;

    public async Task<IReadOnlyList<LoanApplication>> ListApplicationsAsync(CancellationToken cancellationToken) =>
        (await dbContext.Applications.AsNoTracking().ToListAsync(cancellationToken))
        .Select(row => JsonStore.Deserialize<LoanApplication>(row.Payload))
        .ToArray();

    public async Task SaveApplicationAsync(LoanApplication application, int expectedVersion, CancellationToken cancellationToken)
    {
        var row = await dbContext.Applications.SingleOrDefaultAsync(candidate => candidate.Id == application.Id, cancellationToken)
            ?? throw new DomainException("Application was not found.");
        if (row.Version != expectedVersion)
        {
            throw new DomainException("Application changed concurrently; reload and retry.");
        }

        row.CustomerId = application.CustomerId;
        row.Stage = application.Stage.ToString();
        row.Version = application.Version;
        row.CreatedAtUnixMilliseconds = application.CreatedAt.ToUnixTimeMilliseconds();
        row.Payload = JsonStore.Serialize(application);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertQueueItemAsync(UnderwritingQueueItem item, CancellationToken cancellationToken)
    {
        var row = await dbContext.UnderwritingQueue.SingleOrDefaultAsync(candidate => candidate.ApplicationId == item.ApplicationId, cancellationToken);
        if (row is null)
        {
            dbContext.UnderwritingQueue.Add(ToRow(item));
        }
        else
        {
            row.Priority = item.Priority;
            row.SlaDueAtUnixMilliseconds = item.SlaDueAt.ToUnixTimeMilliseconds();
            row.Payload = JsonStore.Serialize(item);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<UnderwritingQueueItem?> GetQueueItemAsync(Guid applicationId, CancellationToken cancellationToken) =>
        (await dbContext.UnderwritingQueue.AsNoTracking().SingleOrDefaultAsync(row => row.ApplicationId == applicationId, cancellationToken)) is { } row
            ? JsonStore.Deserialize<UnderwritingQueueItem>(row.Payload)
            : null;

    public async Task<IReadOnlyList<UnderwritingQueueItem>> ListQueueItemsAsync(CancellationToken cancellationToken) =>
        (await dbContext.UnderwritingQueue.AsNoTracking().ToListAsync(cancellationToken))
        .Select(row => JsonStore.Deserialize<UnderwritingQueueItem>(row.Payload))
        .ToArray();

    public async Task AddOfferAsync(LoanOffer offer, CancellationToken cancellationToken)
    {
        dbContext.Offers.Add(ToRow(offer));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<LoanOffer?> GetOfferAsync(Guid id, CancellationToken cancellationToken) =>
        (await dbContext.Offers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken)) is { } row
            ? JsonStore.Deserialize<LoanOffer>(row.Payload)
            : null;

    public async Task<IReadOnlyList<LoanOffer>> ListOffersForApplicationAsync(Guid applicationId, CancellationToken cancellationToken) =>
        (await dbContext.Offers.AsNoTracking().Where(row => row.ApplicationId == applicationId).ToListAsync(cancellationToken))
        .Select(row => JsonStore.Deserialize<LoanOffer>(row.Payload))
        .ToArray();

    public async Task SaveOfferAsync(LoanOffer offer, CancellationToken cancellationToken)
    {
        var row = await dbContext.Offers.SingleOrDefaultAsync(candidate => candidate.Id == offer.Id, cancellationToken)
            ?? throw new DomainException("Offer was not found.");
        row.Status = offer.Status.ToString();
        row.Payload = JsonStore.Serialize(offer);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddDisbursementAsync(DisbursementRecord disbursement, CancellationToken cancellationToken)
    {
        dbContext.Disbursements.Add(ToRow(disbursement));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<DisbursementRecord?> GetDisbursementByReferenceAsync(string providerReference, CancellationToken cancellationToken) =>
        (await dbContext.Disbursements.AsNoTracking().SingleOrDefaultAsync(row => row.ProviderReference == providerReference, cancellationToken)) is { } row
            ? JsonStore.Deserialize<DisbursementRecord>(row.Payload)
            : null;

    public async Task SaveDisbursementAsync(DisbursementRecord disbursement, CancellationToken cancellationToken)
    {
        var row = await dbContext.Disbursements.SingleOrDefaultAsync(candidate => candidate.Id == disbursement.Id, cancellationToken)
            ?? throw new DomainException("Disbursement was not found.");
        row.Status = disbursement.Status.ToString();
        row.Payload = JsonStore.Serialize(disbursement);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddAuditAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        dbContext.Audits.Add(new AuditRow
        {
            Id = entry.Id,
            OccurredAtUnixMilliseconds = entry.OccurredAt.ToUnixTimeMilliseconds(),
            CorrelationId = entry.CorrelationId,
            Payload = JsonStore.Serialize(entry)
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAuditsAsync(CancellationToken cancellationToken) =>
        (await dbContext.Audits.AsNoTracking().OrderBy(row => row.OccurredAtUnixMilliseconds).ToListAsync(cancellationToken))
        .Select(row => JsonStore.Deserialize<AuditEntry>(row.Payload))
        .ToArray();

    public async Task AddDecisionRecordAsync(DecisionRecord record, CancellationToken cancellationToken)
    {
        dbContext.DecisionRecords.Add(new DecisionRecordRow
        {
            Id = record.Id,
            ApplicationId = record.ApplicationId,
            CreatedAtUnixMilliseconds = record.CreatedAt.ToUnixTimeMilliseconds(),
            Payload = JsonStore.Serialize(record)
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<DecisionRecord?> GetDecisionRecordAsync(Guid applicationId, CancellationToken cancellationToken) =>
        (await dbContext.DecisionRecords.AsNoTracking().SingleOrDefaultAsync(row => row.ApplicationId == applicationId, cancellationToken)) is { } row
            ? JsonStore.Deserialize<DecisionRecord>(row.Payload)
            : null;

    private static ApplicationRow ToRow(LoanApplication application) => new()
    {
        Id = application.Id,
        CustomerId = application.CustomerId,
        Stage = application.Stage.ToString(),
        Version = application.Version,
        CreatedAtUnixMilliseconds = application.CreatedAt.ToUnixTimeMilliseconds(),
        Payload = JsonStore.Serialize(application)
    };

    private static QueueRow ToRow(UnderwritingQueueItem item) => new()
    {
        ApplicationId = item.ApplicationId,
        Priority = item.Priority,
        SlaDueAtUnixMilliseconds = item.SlaDueAt.ToUnixTimeMilliseconds(),
        Payload = JsonStore.Serialize(item)
    };

    private static OfferRow ToRow(LoanOffer offer) => new()
    {
        Id = offer.Id,
        ApplicationId = offer.ApplicationId,
        Version = offer.Version,
        Status = offer.Status.ToString(),
        Payload = JsonStore.Serialize(offer)
    };

    private static DisbursementRow ToRow(DisbursementRecord record) => new()
    {
        Id = record.Id,
        ProviderReference = record.ProviderReference,
        Status = record.Status.ToString(),
        Payload = JsonStore.Serialize(record)
    };
}

public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string payload) =>
        JsonSerializer.Deserialize<T>(payload, Options)
        ?? throw new InvalidOperationException("Stored JSON payload was invalid.");
}
