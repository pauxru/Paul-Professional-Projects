namespace Contoso.Storefront.Application.Ports;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IIdGenerator
{
    Guid NewId();
}

public interface IAsyncDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface IFeatureFlagProvider
{
    bool IsEnabled(string flagName);
}

public interface IOutboxTransport
{
    Task PublishAsync(Guid messageId, string type, string payload, CancellationToken cancellationToken);
}

public interface ICacheHealthProbe
{
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
}

public interface IMessageBusHealthProbe
{
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
}

public interface IMigrationStatus
{
    Task<bool> AreAllMigrationsAppliedAsync(CancellationToken cancellationToken);
}

public interface IStartupDependencyProbe
{
    Task<StartupProbeResult> CheckAsync(CancellationToken cancellationToken);
}

public sealed record StartupProbeResult(bool IsHealthy, string Detail)
{
    public static StartupProbeResult Healthy() => new(true, "All startup dependencies are available.");
    public static StartupProbeResult Unhealthy(string detail) => new(false, detail);
}

public interface ISecretProvider
{
    IReadOnlyDictionary<string, string?> LoadSecrets();
}

public interface ICorrelationContext
{
    string? CorrelationId { get; set; }
}

public interface ITokenIssuer
{
    string Issue(string subject, IReadOnlyCollection<string> scopes, TimeSpan lifetime);
}
