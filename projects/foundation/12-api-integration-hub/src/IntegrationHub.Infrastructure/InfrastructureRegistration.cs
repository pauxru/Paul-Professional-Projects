using IntegrationHub.Application;
using IntegrationHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntegrationHub.Infrastructure;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddIntegrationHubInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<SecretsOptions>()
            .Bind(configuration.GetSection(SecretsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<ConnectorHostOptions>()
            .Bind(configuration.GetSection(ConnectorHostOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<RetentionOptions>()
            .Bind(configuration.GetSection(RetentionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var database = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
                       ?? new DatabaseOptions();
        services.AddDbContext<IntegrationHubDbContext>(options => options.UseSqlite(database.ConnectionString));
        services.AddHttpClient("connectors")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, GuidIdGenerator>();
        services.AddSingleton<IFlowDefinitionParser, FlowDefinitionParser>();
        services.AddSingleton<SafeExpressionEvaluator>();
        services.AddSingleton<PayloadContractValidator>();
        services.AddSingleton<SchemaDriftDetector>();
        services.AddSingleton<SecretRedactor>();
        services.AddSingleton<ISecretRedactor>(sp => sp.GetRequiredService<SecretRedactor>());
        services.AddSingleton<ISecretStore, EncryptedFileSecretStore>();
        services.AddSingleton<SecretReferenceResolver>();
        services.AddScoped<IFlowStore, EfFlowStore>();
        services.AddScoped<IExecutionStore, EfExecutionStore>();
        services.AddScoped<IDeadLetterStore, EfDeadLetterStore>();
        services.AddScoped<ICheckpointStore, EfCheckpointStore>();
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
        services.AddScoped<IContractDriftStore, EfContractDriftStore>();
        services.AddScoped<IWebhookNonceStore, EfWebhookNonceStore>();
        services.AddScoped<WebhookVerifier>();
        services.AddScoped<IFlowRunner, FlowRunner>();
        services.AddScoped<IConnectorRegistry>(sp =>
        {
            var hosts = sp.GetRequiredService<IOptions<ConnectorHostOptions>>().Value;
            var guard = new ConnectorUrlGuard(
                hosts.AllowedHosts.ToHashSet(StringComparer.OrdinalIgnoreCase),
                allowHttp: true,
                allowPrivateNetworks: hosts.AllowPrivateNetworks);
            var webhookContract = new JsonContract("webhook", [
                new ContractField("$.eventType", ContractValueType.String),
                new ContractField("$.data", ContractValueType.Object)
            ]);
            var webhook = new WebhookSourceConnector(
                sp.GetRequiredService<WebhookVerifier>(),
                sp.GetRequiredService<ISecretStore>(),
                sp.GetRequiredService<PayloadContractValidator>(),
                webhookContract,
                "@secret:webhooks/signingKey");
            var file = new FileConnector(Path.Combine(AppContext.BaseDirectory, "drop"));
            var connectors = ConnectorCatalog.Create(
                hosts,
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<SecretReferenceResolver>(),
                guard,
                sp.GetRequiredService<IClock>(),
                file,
                webhook);
            return new ConnectorRegistry(connectors);
        });
        return services;
    }
}
