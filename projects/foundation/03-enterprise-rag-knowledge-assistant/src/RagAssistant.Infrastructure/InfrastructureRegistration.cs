using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Answering;
using RagAssistant.Application.Chat;
using RagAssistant.Application.Chunking;
using RagAssistant.Application.Cost;
using RagAssistant.Application.Embeddings;
using RagAssistant.Application.Evaluation;
using RagAssistant.Application.Feedback;
using RagAssistant.Application.Ingestion;
using RagAssistant.Application.Prompts;
using RagAssistant.Application.Retrieval;
using RagAssistant.Domain.Common;
using RagAssistant.Infrastructure.Ai.OpenAi;
using RagAssistant.Infrastructure.Options;
using RagAssistant.Infrastructure.Persistence;
using RagAssistant.Infrastructure.Retrieval;
using RagAssistant.Infrastructure.Seeding;

namespace RagAssistant.Infrastructure;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddRagInfrastructure(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<BudgetOptions>()
            .Bind(configuration.GetSection(BudgetOptions.SectionName));

        services.AddOptions<RagOptions>()
            .Bind(configuration.GetSection(RagOptions.SectionName));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, GuidIdGenerator>();
        services.AddSingleton<ITokenCounter, HeuristicTokenCounter>();

        services.AddDbContextFactory<RagDbContext>((provider, options) =>
        {
            var dbOpts = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlite(dbOpts.ConnectionString);
        });

        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<RagDbContext>>().CreateDbContext());
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<IChatSessionRepository, ChatSessionRepository>();
        services.AddScoped<IFeedbackRepository, FeedbackRepository>();
        services.AddScoped<IPromptRepository, PromptRepository>();
        services.AddScoped<IUsageLedger, UsageLedger>();

        services.AddSingleton<IPriceBook>(_ => new InMemoryPriceBook(new[]
        {
            new PriceEntry(LocalDeterministicEmbeddingModel.DefaultModelId, 0m, 0m),
            new PriceEntry(TemplateChatModel.DefaultModelId, 0m, 0m),
            new PriceEntry("gpt-4o-mini", 0.15m, 0.60m),
            new PriceEntry("text-embedding-3-small", 0.02m, 0m),
        }));

        services.AddSingleton<IChunkerFactory, DefaultChunkerFactory>();
        services.AddSingleton<LocalDeterministicEmbeddingModel>(sp =>
            new LocalDeterministicEmbeddingModel(sp.GetRequiredService<IOptions<AiOptions>>().Value.EmbeddingDimensions));

        services.AddSingleton<IEmbeddingModel>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            return options.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                ? new OpenAiEmbeddingModel(sp.GetRequiredService<IHttpClientFactory>().CreateClient("openai"), options)
                : sp.GetRequiredService<LocalDeterministicEmbeddingModel>();
        });

        services.AddSingleton<TemplateChatModel>();
        services.AddSingleton<IChatModel>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            return options.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                ? new OpenAiChatModel(sp.GetRequiredService<IHttpClientFactory>().CreateClient("openai"), options)
                : sp.GetRequiredService<TemplateChatModel>();
        });

        services.AddHttpClient("openai");

        services.AddSingleton<IVectorStore, SqliteVectorStore>();
        services.AddScoped<IRetriever, HybridRetriever>();
        services.AddScoped<IGroundingChecker, LexicalGroundingChecker>();

        services.AddSingleton<IQueryRewriter, ContextualQueryRewriter>();

        services.AddScoped<AnsweringService>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RagOptions>>().Value;
            return new AnsweringService(
                sp.GetRequiredService<IRetriever>(),
                sp.GetRequiredService<IChatModel>(),
                sp.GetRequiredService<IEmbeddingModel>(),
                sp.GetRequiredService<IGroundingChecker>(),
                sp.GetRequiredService<IPromptRepository>(),
                sp.GetRequiredService<ITokenCounter>(),
                new RagAnsweringOptions
                {
                    MinRetrievalScore = options.MinRetrievalScore,
                    MinSupportForSentence = options.MinSupportForSentence,
                    MinSupportRatio = options.MinSupportRatio,
                });
        });

        services.AddScoped<IngestionService>();
        services.AddScoped<PromptService>();
        services.AddScoped<FeedbackService>();
        services.AddScoped<CorpusSeeder>();
        services.AddScoped<IBudgetGuard>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<BudgetOptions>>().Value;
            return new UsageBudgetGuard(
                sp.GetRequiredService<IUsageLedger>(),
                new BudgetPolicy(options.DailyLimitUsd, options.DailyRequestLimit));
        });
        services.AddScoped<ChatOrchestrator>();

        services.AddScoped<EvaluationHarness>(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<RagDbContext>>();
            var titleCache = new Dictionary<Guid, string>();
            using (var db = factory.CreateDbContext())
            {
                foreach (var pair in db.Documents.Select(d => new { d.Id, d.Title }).ToArray())
                {
                    titleCache[pair.Id] = pair.Title;
                }
            }

            return new EvaluationHarness(
                sp.GetRequiredService<IRetriever>(),
                sp.GetRequiredService<AnsweringService>(),
                id => titleCache.TryGetValue(id, out var title) ? title : null);
        });

        return services;
    }
}
