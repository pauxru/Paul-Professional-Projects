using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZeroTrust.Api.Endpoints;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Identity;
using ZeroTrust.Infrastructure.Persistence;
using ZeroTrust.IntegrationTests.Fixtures;

namespace ZeroTrust.IntegrationTests.Endpoints;

public class ApiKeyMigrationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private const string ThumbprintAcme = "AA11BB22CC33DD44EE55FF66AA11BB22CC33DD44";

    public ApiKeyMigrationTests(ApiFactory f) { _factory = f; }

    [Fact]
    public async Task Valid_Api_Key_Grants_Partner_Access_During_Dual_Accept()
    {
        using var scope = _factory.Services.CreateScope();
        var toggle = scope.ServiceProvider.GetRequiredService<IApiKeyToggle>();
        toggle.EnforcementActive = false;

        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", "acme-legacy-key-01:legacy-key-plaintext-example");
        c.DefaultRequestHeaders.Add("X-Client-Cert-Thumbprint", ThumbprintAcme);
        var res = await c.GetAsync($"/api/v1/partner/payments/{Guid.NewGuid()}");
        // Payment doesn't exist so 404, but auth succeeded (not 401/403).
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Invalid_Api_Key_Secret_Yields_401()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", "acme-legacy-key-01:wrong-secret");
        c.DefaultRequestHeaders.Add("X-Client-Cert-Thumbprint", ThumbprintAcme);
        var res = await c.GetAsync($"/api/v1/partner/payments/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Api_Key_Rejected_After_Cutover_When_Past_Deprecation()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZeroTrustDbContext>();
        var toggle = scope.ServiceProvider.GetRequiredService<IApiKeyToggle>();

        // Force deprecation into the past using an ISO-8601 timestamp (matches EF Sqlite's
        // canonical DateTime encoding, so the round-trip has no ambiguity).
        var originalDeprecation = await db.ApiKeys
            .Where(x => x.KeyId == "acme-legacy-key-01")
            .Select(x => x.DeprecatedAfterUtc)
            .FirstAsync();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE api_keys SET DeprecatedAfterUtc = '2020-01-01 00:00:00' WHERE KeyId = 'acme-legacy-key-01';");
        toggle.EnforcementActive = true;

        try
        {
            var c = _factory.CreateClient();
            c.DefaultRequestHeaders.Add("X-Api-Key", "acme-legacy-key-01:legacy-key-plaintext-example");
            c.DefaultRequestHeaders.Add("X-Client-Cert-Thumbprint", ThumbprintAcme);
            var res = await c.GetAsync($"/api/v1/partner/payments/{Guid.NewGuid()}");
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
        finally
        {
            // Restore. Use ExecuteSqlAsync (FormattableString overload) so the value is bound
            // as a parameter, not concatenated — EF1002 clean, and the right pattern to
            // demonstrate on a zero-trust codebase even in tests.
            toggle.EnforcementActive = false;
            var restoreValue = originalDeprecation ?? DateTime.UtcNow.AddDays(30);
            await db.Database.ExecuteSqlAsync(
                $"UPDATE api_keys SET DeprecatedAfterUtc = {restoreValue} WHERE KeyId = 'acme-legacy-key-01'");
        }
    }
}
