using System.Text.Json;
using System.Text.RegularExpressions;

namespace Contoso.Storefront.UnitTests;

public sealed class InfrastructureAsCodeTests
{
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string BicepEnvironments =
        Path.Combine(Root, "infra", "bicep", "environments");
    private static readonly string TerraformEnvironments =
        Path.Combine(Root, "infra", "terraform", "environments");
    private static readonly string[] RequiredTags =
        ["application", "environment", "owner", "managed-by", "data-classification"];

    [Fact]
    public void BicepParameters_DefineAllThreeEnvironments()
    {
        var files = Directory.GetFiles(BicepEnvironments, "*.bicepparam")
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(["dev.bicepparam", "prod.bicepparam", "staging.bicepparam"], files);
    }

    [Fact]
    public void BicepProductionParameters_EnableRedundancyAndPrivateNetworking()
    {
        var parameters = ParseBicepParameters(
            Path.Combine(BicepEnvironments, "prod.bicepparam"));

        Assert.Equal("true", parameters["postgresZoneRedundant"]);
        Assert.Equal("true", parameters["usePrivateEndpoints"]);
        Assert.Equal("3", parameters["containerMinReplicas"]);
        Assert.Equal("'Premium'", parameters["serviceBusSku"]);
    }

    [Fact]
    public void TerraformProductionParameters_EnableRedundancyAndPrivateNetworking()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TerraformEnvironments, "prod.tfvars.json")));
        var root = document.RootElement;

        Assert.True(root.GetProperty("postgres_zone_redundant").GetBoolean());
        Assert.True(root.GetProperty("use_private_endpoints").GetBoolean());
        Assert.True(root.GetProperty("container_min_replicas").GetInt32() >= 3);
        Assert.Equal("Premium", root.GetProperty("service_bus_sku").GetString());
    }

    [Fact]
    public void EveryBicepEnvironment_DefinesRequiredTags()
    {
        foreach (var file in Directory.GetFiles(BicepEnvironments, "*.bicepparam"))
        {
            var content = File.ReadAllText(file);
            foreach (var tag in RequiredTags)
            {
                Assert.Matches(
                    new Regex($@"(?m)^\s*'?{Regex.Escape(tag)}'?\s*:", RegexOptions.CultureInvariant),
                    content);
            }
        }
    }

    [Fact]
    public void EveryTerraformEnvironment_DefinesRequiredTags()
    {
        foreach (var file in Directory.GetFiles(TerraformEnvironments, "*.tfvars.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var tags = document.RootElement.GetProperty("required_tags");
            foreach (var tag in RequiredTags)
            {
                Assert.True(tags.TryGetProperty(tag, out _), $"{Path.GetFileName(file)} lacks tag {tag}.");
            }
        }
    }

    [Fact]
    public void EnvironmentParameterFiles_DoNotEmbedLiteralSecrets()
    {
        var files = Directory.GetFiles(BicepEnvironments, "*.*")
            .Concat(Directory.GetFiles(TerraformEnvironments, "*.*"));

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotMatch(
                new Regex(
                    @"(?im)(password|signing.?key|client.?secret)\s*[:=]\s*['""][^'""]+['""]",
                    RegexOptions.CultureInvariant),
                content);
            Assert.DoesNotContain("DefaultEndpointsProtocol=", content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void BicepModules_UseManagedIdentityRbacAndKeyVaultSecretReferences()
    {
        var rbac = File.ReadAllText(
            Path.Combine(Root, "infra", "bicep", "modules", "rbac.bicep"));
        var containerApps = File.ReadAllText(
            Path.Combine(Root, "infra", "bicep", "modules", "container-apps.bicep"));

        Assert.Contains("roleAssignments", rbac, StringComparison.Ordinal);
        Assert.Contains("AcrPull", RoleNamesFromCommentsOrIds(rbac), StringComparison.Ordinal);
        Assert.Contains("keyVaultUrl", containerApps, StringComparison.Ordinal);
        Assert.Contains("secretRef: 'database-connection'", containerApps, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", containerApps, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseBicepParameters(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
                     File.ReadAllText(path),
                     @"(?m)^\s*param\s+(\w+)\s*=\s*(.+?)\s*$"))
        {
            values[match.Groups[1].Value] = match.Groups[2].Value.Trim();
        }

        return values;
    }

    private static string RoleNamesFromCommentsOrIds(string content)
    {
        // The role-definition GUID for AcrPull is asserted alongside the resource shape.
        return content.Contains("7f951dda-4ed3-4680-a7ca-43fe172d538d", StringComparison.Ordinal)
            ? "AcrPull"
            : string.Empty;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "infra")) &&
                File.Exists(Path.Combine(current.FullName, "Contoso.Storefront.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test output path.");
    }
}
