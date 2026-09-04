using LoanOrigination.Application.Ports;
using LoanOrigination.Domain.Models;
using LoanOrigination.Domain.Rules;

namespace LoanOrigination.UnitTests;

public sealed class FakeClock(DateTimeOffset initial) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = initial;

    public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
}

public sealed class InMemoryObjectStore : IObjectStore
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public async Task<string> PutAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        await content.CopyToAsync(output, cancellationToken);
        _files.Add(objectKey, output.ToArray());
        return objectKey;
    }

    public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(new MemoryStream(_files[objectKey], writable: false));
    }
}

public sealed class CollectingAuditWriter : IAuditWriter
{
    public List<string> Actions { get; } = [];

    public Task WriteAsync(string actor, string action, string resource, object? before, object after, string correlationId, string sourceIp, string userAgent, CancellationToken cancellationToken)
    {
        Actions.Add(action);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryLoanRepository : ILoanRepository
{
    public Dictionary<Guid, CustomerProfile> Customers { get; } = [];
    public Dictionary<(string, int), LoanProductVersion> Products { get; } = [];
    public Dictionary<(string, int), RuleSetDefinition> Rulesets { get; } = [];
    public Dictionary<Guid, LoanApplication> Applications { get; } = [];
    public Dictionary<Guid, UnderwritingQueueItem> Queue { get; } = [];
    public Dictionary<Guid, LoanOffer> Offers { get; } = [];
    public Dictionary<string, DisbursementRecord> Disbursements { get; } = [];
    public List<AuditEntry> Audits { get; } = [];
    public Dictionary<Guid, DecisionRecord> DecisionRecords { get; } = [];

    public Task AddCustomerAsync(CustomerProfile customer, CancellationToken cancellationToken)
    {
        Customers.Add(customer.Id, customer);
        return Task.CompletedTask;
    }

    public Task<CustomerProfile?> GetCustomerAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Customers.GetValueOrDefault(id));

    public Task<IReadOnlyList<CustomerProfile>> ListCustomersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CustomerProfile>>(Customers.Values.ToArray());

    public Task SaveCustomerAsync(CustomerProfile customer, CancellationToken cancellationToken)
    {
        Customers[customer.Id] = customer;
        return Task.CompletedTask;
    }

    public Task AddProductAsync(LoanProductVersion product, CancellationToken cancellationToken)
    {
        Products.Add((product.ProductCode, product.Version), product);
        return Task.CompletedTask;
    }

    public Task<LoanProductVersion?> GetProductAsync(string productCode, int version, CancellationToken cancellationToken) =>
        Task.FromResult(Products.GetValueOrDefault((productCode.ToUpperInvariant(), version)));

    public Task<IReadOnlyList<LoanProductVersion>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LoanProductVersion>>(Products.Values.ToArray());

    public Task AddRulesetAsync(RuleSetDefinition ruleset, CancellationToken cancellationToken)
    {
        Rulesets.Add((ruleset.Id, ruleset.Version), ruleset);
        return Task.CompletedTask;
    }

    public Task<RuleSetDefinition?> GetRulesetAsync(string id, int version, CancellationToken cancellationToken) =>
        Task.FromResult(Rulesets.GetValueOrDefault((id, version)));

    public Task<IReadOnlyList<RuleSetDefinition>> ListRulesetsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RuleSetDefinition>>(Rulesets.Values.ToArray());

    public Task AddApplicationAsync(LoanApplication application, CancellationToken cancellationToken)
    {
        Applications.Add(application.Id, application);
        return Task.CompletedTask;
    }

    public Task<LoanApplication?> GetApplicationAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Applications.GetValueOrDefault(id));

    public Task<IReadOnlyList<LoanApplication>> ListApplicationsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LoanApplication>>(Applications.Values.ToArray());

    public Task SaveApplicationAsync(LoanApplication application, int expectedVersion, CancellationToken cancellationToken)
    {
        if (Applications.TryGetValue(application.Id, out var existing) && existing.Version != expectedVersion)
        {
            throw new DomainException("Application changed concurrently; reload and retry.");
        }

        Applications[application.Id] = application;
        return Task.CompletedTask;
    }

    public Task UpsertQueueItemAsync(UnderwritingQueueItem item, CancellationToken cancellationToken)
    {
        Queue[item.ApplicationId] = item;
        return Task.CompletedTask;
    }

    public Task<UnderwritingQueueItem?> GetQueueItemAsync(Guid applicationId, CancellationToken cancellationToken) =>
        Task.FromResult(Queue.GetValueOrDefault(applicationId));

    public Task<IReadOnlyList<UnderwritingQueueItem>> ListQueueItemsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UnderwritingQueueItem>>(Queue.Values.ToArray());

    public Task AddOfferAsync(LoanOffer offer, CancellationToken cancellationToken)
    {
        Offers.Add(offer.Id, offer);
        return Task.CompletedTask;
    }

    public Task<LoanOffer?> GetOfferAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Offers.GetValueOrDefault(id));

    public Task<IReadOnlyList<LoanOffer>> ListOffersForApplicationAsync(Guid applicationId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LoanOffer>>(Offers.Values.Where(offer => offer.ApplicationId == applicationId).ToArray());

    public Task SaveOfferAsync(LoanOffer offer, CancellationToken cancellationToken)
    {
        Offers[offer.Id] = offer;
        return Task.CompletedTask;
    }

    public Task AddDisbursementAsync(DisbursementRecord disbursement, CancellationToken cancellationToken)
    {
        Disbursements.Add(disbursement.ProviderReference, disbursement);
        return Task.CompletedTask;
    }

    public Task<DisbursementRecord?> GetDisbursementByReferenceAsync(string providerReference, CancellationToken cancellationToken) =>
        Task.FromResult(Disbursements.GetValueOrDefault(providerReference));

    public Task SaveDisbursementAsync(DisbursementRecord disbursement, CancellationToken cancellationToken)
    {
        Disbursements[disbursement.ProviderReference] = disbursement;
        return Task.CompletedTask;
    }

    public Task AddAuditAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        Audits.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ListAuditsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>(Audits.ToArray());

    public Task AddDecisionRecordAsync(DecisionRecord record, CancellationToken cancellationToken)
    {
        DecisionRecords[record.ApplicationId] = record;
        return Task.CompletedTask;
    }

    public Task<DecisionRecord?> GetDecisionRecordAsync(Guid applicationId, CancellationToken cancellationToken) =>
        Task.FromResult(DecisionRecords.GetValueOrDefault(applicationId));
}
