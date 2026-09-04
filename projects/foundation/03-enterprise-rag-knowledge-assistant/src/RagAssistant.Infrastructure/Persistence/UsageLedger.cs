using Microsoft.EntityFrameworkCore;
using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Cost;

namespace RagAssistant.Infrastructure.Persistence;

public sealed class UsageLedger : IUsageLedger
{
    private readonly RagDbContext _db;
    private readonly IPriceBook _priceBook;

    public UsageLedger(RagDbContext db, IPriceBook priceBook)
    {
        _db = db;
        _priceBook = priceBook;
    }

    public async Task RecordAsync(UsageEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var price = _priceBook.GetPrice(entry.Model);
        var computedCost = price is null
            ? entry.Cost
            : Math.Round(
                (entry.PromptTokens / 1000m) * price.PromptPricePer1K
                + (entry.CompletionTokens / 1000m) * price.CompletionPricePer1K,
                6);

        var record = new UsageEntryRecord
        {
            Tenant = entry.Tenant,
            UserId = entry.UserId,
            Model = entry.Model,
            PromptTokens = entry.PromptTokens,
            CompletionTokens = entry.CompletionTokens,
            Cost = computedCost,
            Timestamp = entry.Timestamp,
            PromptVersion = entry.PromptVersion ?? string.Empty,
        };

        await _db.Usage.AddAsync(record, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<UsageSummary> GetDailyAsync(string tenant, DateOnly day, CancellationToken ct)
    {
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = start.AddDays(1);
        var entries = await _db.Usage
            .Where(u => u.Tenant == tenant && u.Timestamp >= start && u.Timestamp < end)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        return new UsageSummary(
            tenant,
            day,
            entries.Sum(e => e.PromptTokens),
            entries.Sum(e => e.CompletionTokens),
            entries.Sum(e => e.Cost),
            entries.Length);
    }
}
