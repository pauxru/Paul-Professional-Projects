using Microsoft.EntityFrameworkCore;
using RagAssistant.Application.Answering;
using RagAssistant.Application.Ingestion;
using RagAssistant.Application.Prompts;
using RagAssistant.Infrastructure.Persistence;

namespace RagAssistant.Infrastructure.Seeding;

public sealed class CorpusSeeder
{
    private readonly IDbContextFactory<RagDbContext> _factory;
    private readonly IngestionService _ingestion;
    private readonly PromptService _prompts;

    public CorpusSeeder(IDbContextFactory<RagDbContext> factory, IngestionService ingestion, PromptService prompts)
    {
        _factory = factory;
        _ingestion = ingestion;
        _prompts = prompts;
    }

    public async Task<int> SeedAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Documents.AnyAsync(ct).ConfigureAwait(false);
        if (existing)
        {
            await EnsureDefaultPromptAsync(ct).ConfigureAwait(false);
            return 0;
        }

        var corpus = SyntheticCorpus.Build();
        var ingested = 0;
        foreach (var entry in corpus)
        {
            await _ingestion.IngestAsync(
                new IngestionRequest(entry.Title, entry.Source, entry.Content, entry.Acl, entry.Strategy),
                ct).ConfigureAwait(false);
            ingested++;
        }

        await EnsureDefaultPromptAsync(ct).ConfigureAwait(false);
        return ingested;
    }

    private async Task EnsureDefaultPromptAsync(CancellationToken ct)
    {
        var existing = await _prompts.GetActiveAsync(RagPrompts.DefaultPromptName, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return;
        }

        await _prompts.RegisterAsync(
            new RegisterPromptRequest(RagPrompts.DefaultPromptName, RagPrompts.DefaultPromptVersion, RagPrompts.DefaultPromptBody),
            ct).ConfigureAwait(false);
    }
}
