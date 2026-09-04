using Contoso.Storefront.Application.Ports;
using Contoso.Storefront.Infrastructure.Adapters;
using Contoso.Storefront.Infrastructure.Http;
using Contoso.Storefront.Infrastructure.Messaging;
using Contoso.Storefront.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Contoso.Storefront.Application.Configuration;

namespace Contoso.Storefront.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddStorefrontInfrastructure(this IServiceCollection services)
    {
        services.AddStorefrontDatabase();

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, GuidIdGenerator>();
        services.AddSingleton<IAsyncDelay, SystemAsyncDelay>();
        services.AddSingleton<ICorrelationContext, AsyncLocalCorrelationContext>();
        services.AddSingleton<IFeatureFlagProvider, OptionsFeatureFlagProvider>();

        services.AddSingleton<InMemoryMessageBus>();
        services.AddSingleton<AzureServiceBusAdapter>();
        services.AddSingleton<IOutboxTransport>(provider =>
            IsProvider<MessagingOptions>(provider, "ServiceBus", options => options.Provider)
                ? provider.GetRequiredService<AzureServiceBusAdapter>()
                : provider.GetRequiredService<InMemoryMessageBus>());
        services.AddSingleton<IMessageBusHealthProbe>(provider =>
            IsProvider<MessagingOptions>(provider, "ServiceBus", options => options.Provider)
                ? provider.GetRequiredService<AzureServiceBusAdapter>()
                : provider.GetRequiredService<InMemoryMessageBus>());
        services.AddSingleton<MemoryCacheHealthProbe>();
        services.AddSingleton<RedisCacheHealthProbe>();
        services.AddSingleton<ICacheHealthProbe>(provider =>
            IsProvider<CacheOptions>(provider, "Redis", options => options.Provider)
                ? provider.GetRequiredService<RedisCacheHealthProbe>()
                : provider.GetRequiredService<MemoryCacheHealthProbe>());

        services.AddScoped<IStorefrontStore, EfStorefrontStore>();
        services.AddScoped<IMigrationStatus, DatabaseMigrationStatus>();
        services.AddScoped<IStartupDependencyProbe, CompositeStartupDependencyProbe>();
        services.AddScoped<OutboxProcessor>();

        services.AddTransient<CorrelationPropagationHandler>();
        services.AddTransient<ResilientHttpMessageHandler>();
        return services;
    }

    private static bool IsProvider<TOptions>(
        IServiceProvider provider,
        string expected,
        Func<TOptions, string> selectProvider)
        where TOptions : class
    {
        var configured = selectProvider(provider.GetRequiredService<IOptions<TOptions>>().Value);
        return string.Equals(configured, expected, StringComparison.OrdinalIgnoreCase);
    }
}
