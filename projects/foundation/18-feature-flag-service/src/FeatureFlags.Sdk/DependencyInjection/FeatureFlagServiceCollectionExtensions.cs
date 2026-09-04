using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FeatureFlags.Sdk;

public static class FeatureFlagServiceCollectionExtensions
{
    public static IServiceCollection AddFeatureFlags(this IServiceCollection services, Action<FeatureFlagClientOptions> configure)
    {
        services.AddOptions<FeatureFlagClientOptions>().Configure(configure).ValidateDataAnnotations().ValidateOnStart();
        services.AddHttpClient("FeatureFlagsSdk");
        services.AddSingleton<FeatureFlagClient>(provider => new FeatureFlagClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("FeatureFlagsSdk"),
            provider.GetRequiredService<IOptions<FeatureFlagClientOptions>>().Value));
        services.AddSingleton<IFeatureFlagClient>(provider => provider.GetRequiredService<FeatureFlagClient>());
        services.AddSingleton<IFeatureGate, FeatureGate>();
        services.AddHostedService<FeatureFlagClientHostedService>();
        return services;
    }

    public static IServiceCollection AddFeatureFlags(this IServiceCollection services, IConfiguration configuration) =>
        services.AddFeatureFlags(options => configuration.GetSection(FeatureFlagClientOptions.SectionName).Bind(options));
}

public sealed class FeatureFlagClientHostedService(FeatureFlagClient client) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => client.InitializeAsync(cancellationToken);
    public async Task StopAsync(CancellationToken cancellationToken) => await client.DisposeAsync();
}
