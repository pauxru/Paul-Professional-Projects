namespace RagAssistant.Application.Abstractions;

public interface IUsageLedger
{
    Task RecordAsync(UsageEntry entry, CancellationToken ct);
    Task<UsageSummary> GetDailyAsync(string tenant, DateOnly day, CancellationToken ct);
}

public sealed record UsageEntry(
    string Tenant,
    string UserId,
    string Model,
    int PromptTokens,
    int CompletionTokens,
    decimal Cost,
    DateTimeOffset Timestamp,
    string PromptVersion);

public sealed record UsageSummary(
    string Tenant,
    DateOnly Day,
    int PromptTokens,
    int CompletionTokens,
    decimal Cost,
    int Requests);
