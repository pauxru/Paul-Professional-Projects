using System.ComponentModel.DataAnnotations;
using Contoso.Storefront.Application.Configuration;
using Contoso.Storefront.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

namespace Contoso.Storefront.UnitTests;

public sealed class ConfigurationTests
{
    [Fact]
    public void SecurityOptions_WithShortSigningKey_FailsDataAnnotationValidation()
    {
        var options = new SecurityOptions { SigningKey = "short" };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            results,
            validateAllProperties: true);

        Assert.False(valid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(SecurityOptions.SigningKey)));
    }

    [Fact]
    public void OperationalOptions_WithInvalidDrainTimeout_FailsValidation()
    {
        var options = new OperationalOptions { DrainTimeoutSeconds = 0 };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            results,
            validateAllProperties: true);

        Assert.False(valid);
    }

    [Fact]
    public void ProductionGuard_WithDevelopmentDefaults_ReportsEveryUnsafeDefault()
    {
        var violations = ProductionDefaultsGuard.FindViolations(
            "Production",
            new DatabaseOptions(),
            new SecurityOptions(),
            new CacheOptions(),
            new MessagingOptions(),
            new KeyVaultOptions());

        Assert.Equal(6, violations.Count);
        Assert.Contains(violations, item => item.Contains("JWT", StringComparison.Ordinal));
        Assert.Contains(violations, item => item.Contains("SQLite", StringComparison.Ordinal));
        Assert.Contains(violations, item => item.Contains("cache", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(violations, item => item.Contains("message bus", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(violations, item => item.Contains("Key Vault", StringComparison.Ordinal));
        Assert.Contains(violations, item => item.Contains("OIDC", StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionGuard_WithProductionConfiguration_HasNoViolations()
    {
        var violations = ProductionDefaultsGuard.FindViolations(
            "Production",
            new DatabaseOptions
            {
                Provider = "Postgres",
                ConnectionString = "Host=example.invalid;Database=storefront"
            },
            new SecurityOptions
            {
                SigningKey = new string('x', 48),
                Authority = "https://login.microsoftonline.com/example/v2.0"
            },
            new CacheOptions { Provider = "Redis" },
            new MessagingOptions { Provider = "ServiceBus" },
            new KeyVaultOptions
            {
                Enabled = true,
                VaultUri = "https://example.vault.azure.net/"
            });

        Assert.Empty(violations);
    }

    [Fact]
    public void ProductionGuard_OutsideProduction_AllowsLocalDefaults()
    {
        var violations = ProductionDefaultsGuard.FindViolations(
            "Development",
            new DatabaseOptions(),
            new SecurityOptions(),
            new CacheOptions(),
            new MessagingOptions(),
            new KeyVaultOptions());

        Assert.Empty(violations);
    }

    [Fact]
    public void SecretConfigurationSource_OverridesEarlierConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:SigningKey"] = "appsettings-value"
            })
            .AddSecretProvider(
                new DictionarySecretProvider(
                    new Dictionary<string, string?>
                    {
                        ["Security:SigningKey"] = "key-vault-value"
                    }))
            .Build();

        Assert.Equal("key-vault-value", configuration["Security:SigningKey"]);
    }

    [Fact]
    public void ConfigurationLayering_LastProviderWinsAcrossAllFourLayers()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Layered"] = "appsettings" })
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Layered"] = "environment" })
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Layered"] = "user-secrets" })
            .AddSecretProvider(
                new DictionarySecretProvider(
                    new Dictionary<string, string?> { ["Layered"] = "key-vault" }))
            .Build();

        Assert.Equal("key-vault", configuration["Layered"]);
    }

    [Fact]
    public void SecretConfigurationSource_PreservesNestedKeys()
    {
        var configuration = new ConfigurationBuilder()
            .AddSecretProvider(
                new DictionarySecretProvider(
                    new Dictionary<string, string?>
                    {
                        ["Database:ConnectionString"] = "Data Source=fake.db"
                    }))
            .Build();

        Assert.Equal(
            "Data Source=fake.db",
            configuration.GetSection("Database")["ConnectionString"]);
    }
}
