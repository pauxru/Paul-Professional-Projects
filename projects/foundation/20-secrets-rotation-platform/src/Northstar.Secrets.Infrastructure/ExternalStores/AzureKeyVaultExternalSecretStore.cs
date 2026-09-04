using Northstar.Secrets.Application;

namespace Northstar.Secrets.Infrastructure.ExternalStores;

public sealed record ExternalSecretVersion(
    string Name,
    string Version,
    string? Value,
    bool Enabled,
    IReadOnlyDictionary<string, string> Tags);

public interface IAzureKeyVaultClient
{
    Task<ExternalSecretVersion> SetSecretAsync(
        string name,
        string value,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken cancellationToken);

    Task<ExternalSecretVersion?> GetSecretAsync(
        string name,
        string version,
        CancellationToken cancellationToken);

    Task DisableSecretVersionAsync(
        string name,
        string version,
        CancellationToken cancellationToken);
}

public sealed class AzureKeyVaultExternalSecretStore(IAzureKeyVaultClient client)
    : IExternalSecretStore
{
    public Task SetVersionAsync(
        string hierarchicalName,
        int version,
        string value,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        var tags = new Dictionary<string, string>(metadata)
        {
            ["northstar-version"] = version.ToString(),
            ["northstar-path"] = hierarchicalName
        };
        return SetAsync();

        async Task SetAsync()
        {
            await client.SetSecretAsync(ToVaultName(hierarchicalName), value, tags, cancellationToken);
        }
    }

    public async Task<string?> GetVersionAsync(
        string hierarchicalName,
        int version,
        CancellationToken cancellationToken)
    {
        var result = await client.GetSecretAsync(
            ToVaultName(hierarchicalName),
            version.ToString(),
            cancellationToken);
        return result is { Enabled: true } ? result.Value : null;
    }

    public Task DisableVersionAsync(
        string hierarchicalName,
        int version,
        CancellationToken cancellationToken) =>
        client.DisableSecretVersionAsync(
            ToVaultName(hierarchicalName),
            version.ToString(),
            cancellationToken);

    public static string ToVaultName(string hierarchicalName) =>
        hierarchicalName.Replace("/", "--", StringComparison.Ordinal).ToLowerInvariant();
}

public sealed class UnconfiguredAzureKeyVaultClient : IAzureKeyVaultClient
{
    private const string Message =
        "Azure Key Vault is not configured. This compile-checked adapter provisions no Azure resources.";

    public Task<ExternalSecretVersion> SetSecretAsync(
        string name,
        string value,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken cancellationToken) =>
        Task.FromException<ExternalSecretVersion>(new NotSupportedException(Message));

    public Task<ExternalSecretVersion?> GetSecretAsync(
        string name,
        string version,
        CancellationToken cancellationToken) =>
        Task.FromException<ExternalSecretVersion?>(new NotSupportedException(Message));

    public Task DisableSecretVersionAsync(
        string name,
        string version,
        CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException(Message));
}
