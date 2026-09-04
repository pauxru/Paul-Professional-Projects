using Contoso.Storefront.Application.Configuration;
using Contoso.Storefront.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Contoso.Storefront.Api.Configuration;

public static class ConfigurationSetup
{
    public static void ConfigureSources(WebApplicationBuilder builder, string[] args)
    {
        builder.Configuration.Sources.Clear();
        builder.Configuration
            .SetBasePath(builder.Environment.ContentRootPath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile(
                $"appsettings.{builder.Environment.EnvironmentName}.json",
                optional: true,
                reloadOnChange: true)
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>(optional: true)
            .AddCommandLine(args);

        var keyVault = builder.Configuration
            .GetSection(KeyVaultOptions.SectionName)
            .Get<KeyVaultOptions>() ?? new KeyVaultOptions();

        if (keyVault.Enabled)
        {
            if (!Uri.TryCreate(keyVault.VaultUri, UriKind.Absolute, out var vaultUri))
            {
                throw new InvalidOperationException(
                    "KeyVault:VaultUri must be an absolute URI when Key Vault is enabled.");
            }

            builder.Configuration.AddSecretProvider(
                new AzureKeyVaultSecretProvider(vaultUri, keyVault.SecretNamePrefix));
        }
    }

    public static IServiceCollection AddValidatedStorefrontOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => new[] { "Sqlite", "Postgres", "PostgreSQL", "SqlServer" }
                    .Contains(options.Provider, StringComparer.OrdinalIgnoreCase),
                "Database:Provider must be Sqlite, Postgres, PostgreSQL, or SqlServer.")
            .ValidateOnStart();
        services.AddOptions<SecurityOptions>()
            .Bind(configuration.GetSection(SecurityOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<CacheOptions>()
            .Bind(configuration.GetSection(CacheOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => string.Equals(options.Provider, "Memory", StringComparison.OrdinalIgnoreCase) ||
                           (string.Equals(options.Provider, "Redis", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(options.ConnectionString)),
                "Cache must use Memory, or Redis with Cache:ConnectionString.")
            .ValidateOnStart();
        services.AddOptions<MessagingOptions>()
            .Bind(configuration.GetSection(MessagingOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => string.Equals(options.Provider, "InMemory", StringComparison.OrdinalIgnoreCase) ||
                           (string.Equals(options.Provider, "ServiceBus", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(options.QueueName) &&
                            (!string.IsNullOrWhiteSpace(options.ConnectionString) ||
                             !string.IsNullOrWhiteSpace(options.FullyQualifiedNamespace))),
                "Messaging must use InMemory, or ServiceBus with a queue and connection string/namespace.")
            .ValidateOnStart();
        services.AddOptions<KeyVaultOptions>()
            .Bind(configuration.GetSection(KeyVaultOptions.SectionName))
            .Validate(
                options => !options.Enabled || Uri.TryCreate(options.VaultUri, UriKind.Absolute, out _),
                "KeyVault:VaultUri must be an absolute URI when enabled.")
            .ValidateOnStart();
        services.AddOptions<OperationalOptions>()
            .Bind(configuration.GetSection(OperationalOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<ResilienceOptions>()
            .Bind(configuration.GetSection(ResilienceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<DownstreamOptions>()
            .Bind(configuration.GetSection(DownstreamOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<ReleaseOptions>()
            .Bind(configuration.GetSection(ReleaseOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<ObservabilityOptions>()
            .Bind(configuration.GetSection(ObservabilityOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => new[] { "None", "Console", "Otlp", "AzureMonitor" }
                    .Contains(options.Exporter, StringComparer.OrdinalIgnoreCase),
                "Observability:Exporter must be None, Console, Otlp, or AzureMonitor.")
            .Validate(
                options => !string.Equals(options.Exporter, "Otlp", StringComparison.OrdinalIgnoreCase) ||
                           Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out _),
                "Observability:OtlpEndpoint must be an absolute URI for the OTLP exporter.")
            .Validate(
                options => !string.Equals(options.Exporter, "AzureMonitor", StringComparison.OrdinalIgnoreCase) ||
                           !string.IsNullOrWhiteSpace(options.ApplicationInsightsConnectionString),
                "An Application Insights connection string is required for AzureMonitor.")
            .ValidateOnStart();

        services.AddHostedService<StartupConfigurationGuard>();
        return services;
    }
}

public sealed class StartupConfigurationGuard(
    IHostEnvironment environment,
    IOptions<DatabaseOptions> database,
    IOptions<SecurityOptions> security,
    IOptions<CacheOptions> cache,
    IOptions<MessagingOptions> messaging,
    IOptions<KeyVaultOptions> keyVault) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var violations = ProductionDefaultsGuard.FindViolations(
            environment.EnvironmentName,
            database.Value,
            security.Value,
            cache.Value,
            messaging.Value,
            keyVault.Value);

        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                "Unsafe Production configuration: " + string.Join(" ", violations));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
