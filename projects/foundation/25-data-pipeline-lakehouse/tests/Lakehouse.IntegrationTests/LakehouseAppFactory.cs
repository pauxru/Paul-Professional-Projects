using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Lakehouse.IntegrationTests;

/// <summary>
/// Spins up the real API host via <see cref="WebApplicationFactory{Program}"/> against an isolated temp
/// lake and serving DB, seeded once with a deliberately small synthetic dataset. Disposed (and its temp
/// directory removed) at the end of the test class.
/// </summary>
public sealed class LakehouseAppFactory : WebApplicationFactory<Program>
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "lh-itest-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("environment", "Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Lakehouse:LakeRoot"] = Path.Combine(Root, "lake"),
                ["Lakehouse:ServingDbPath"] = Path.Combine(Root, "serving", "serving.db"),
                ["Lakehouse:SeedOnStartup"] = "true",
                ["Lakehouse:Generator:Customers"] = "40",
                ["Lakehouse:Generator:Products"] = "20",
                ["Lakehouse:Generator:Orders"] = "150",
                ["Lakehouse:Generator:Sessions"] = "100",
                ["Lakehouse:Generator:Days"] = "15"
            });
        });
    }

    /// <summary>Obtain a bearer token for the given role from the dev token endpoint.</summary>
    public async Task<string> TokenAsync(HttpClient client, string role)
    {
        var resp = await client.PostAsJsonAsync("/api/auth/token", new { role });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("token").GetString()!;
    }

    public async Task<HttpClient> ClientForAsync(string role)
    {
        var client = CreateClient();
        var token = await TokenAsync(client, role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
