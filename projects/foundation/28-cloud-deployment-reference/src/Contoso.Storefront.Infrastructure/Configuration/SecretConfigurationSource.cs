using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Contoso.Storefront.Application.Ports;
using Microsoft.Extensions.Configuration;

namespace Contoso.Storefront.Infrastructure.Configuration;

public sealed class DictionarySecretProvider(IReadOnlyDictionary<string, string?> secrets) : ISecretProvider
{
    public IReadOnlyDictionary<string, string?> LoadSecrets() => secrets;
}

public sealed class AzureKeyVaultSecretProvider : ISecretProvider
{
    private readonly SecretClient _client;
    private readonly string _secretNamePrefix;

    public AzureKeyVaultSecretProvider(Uri vaultUri, string secretNamePrefix)
    {
        _client = new SecretClient(vaultUri, new DefaultAzureCredential());
        _secretNamePrefix = secretNamePrefix;
    }

    public IReadOnlyDictionary<string, string?> LoadSecrets()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in _client.GetPropertiesOfSecrets())
        {
            if (!property.Enabled.GetValueOrDefault(true) ||
                !property.Name.StartsWith(_secretNamePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = property.Name[_secretNamePrefix.Length..].Replace("--", ":", StringComparison.Ordinal);
            values[key] = _client.GetSecret(property.Name).Value.Value;
        }

        return values;
    }
}

public sealed class SecretConfigurationSource(ISecretProvider secretProvider) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new SecretConfigurationProvider(secretProvider);
}

public sealed class SecretConfigurationProvider(ISecretProvider secretProvider) : ConfigurationProvider
{
    public override void Load()
    {
        Data = new Dictionary<string, string?>(
            secretProvider.LoadSecrets(),
            StringComparer.OrdinalIgnoreCase);
    }
}

public static class SecretConfigurationExtensions
{
    public static IConfigurationBuilder AddSecretProvider(
        this IConfigurationBuilder builder,
        ISecretProvider secretProvider)
    {
        return builder.Add(new SecretConfigurationSource(secretProvider));
    }
}
