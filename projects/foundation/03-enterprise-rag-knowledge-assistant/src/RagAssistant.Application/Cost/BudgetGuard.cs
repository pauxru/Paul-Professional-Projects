using RagAssistant.Application.Abstractions;

namespace RagAssistant.Application.Cost;

public sealed record BudgetPolicy(decimal DailyLimitUsd, int DailyRequestLimit);

public sealed record PriceEntry(string Model, decimal PromptPricePer1K, decimal CompletionPricePer1K);

public interface IPriceBook
{
    PriceEntry? GetPrice(string model);
}

public sealed class InMemoryPriceBook : IPriceBook
{
    private readonly Dictionary<string, PriceEntry> _entries;

    public InMemoryPriceBook(IEnumerable<PriceEntry> entries)
    {
        _entries = new Dictionary<string, PriceEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            _entries[entry.Model] = entry;
        }
    }

    public PriceEntry? GetPrice(string model)
        => _entries.TryGetValue(model, out var entry) ? entry : null;
}

public interface IBudgetGuard
{
    Task EnsureAllowedAsync(string tenant, DateTimeOffset now, CancellationToken ct);
}

public sealed class BudgetExceededException : Exception
{
    public BudgetExceededException(string tenant, decimal limit)
        : base($"Daily budget for tenant '{tenant}' has been exceeded (limit ${limit}).")
    {
        Tenant = tenant;
        Limit = limit;
    }

    public string Tenant { get; }
    public decimal Limit { get; }
}

public sealed class UsageBudgetGuard : IBudgetGuard
{
    private readonly IUsageLedger _ledger;
    private readonly BudgetPolicy _policy;

    public UsageBudgetGuard(IUsageLedger ledger, BudgetPolicy policy)
    {
        _ledger = ledger;
        _policy = policy;
    }

    public async Task EnsureAllowedAsync(string tenant, DateTimeOffset now, CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var summary = await _ledger.GetDailyAsync(tenant, day, ct).ConfigureAwait(false);
        if (_policy.DailyLimitUsd > 0 && summary.Cost >= _policy.DailyLimitUsd)
        {
            throw new BudgetExceededException(tenant, _policy.DailyLimitUsd);
        }

        if (_policy.DailyRequestLimit > 0 && summary.Requests >= _policy.DailyRequestLimit)
        {
            throw new BudgetExceededException(tenant, _policy.DailyLimitUsd);
        }
    }
}
