using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Identity;
using ZeroTrust.Infrastructure.Persistence;
using ZeroTrust.IntegrationTests.Fixtures;

namespace ZeroTrust.IntegrationTests.Endpoints;

public class KeyRotationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public KeyRotationTests(ApiFactory f) { _factory = f; }

    [Fact]
    public async Task Token_Issued_Before_Rotation_Still_Valid_After_Rotation()
    {
        // 1. Issue a customer access token.
        var oldToken = await _factory.IssueUserAccess("alice", "CustomerPassw0rd!", Audience.Customer,
            "customer.read customer.write");
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oldToken);
        var beforeRotation = await c.GetAsync("/api/v1/customer/accounts");
        Assert.Equal(HttpStatusCode.OK, beforeRotation.StatusCode);

        // 2. Rotate keys.
        using var scope = _factory.Services.CreateScope();
        var jwks = scope.ServiceProvider.GetRequiredService<IJwksProvider>();
        await jwks.RotateAsync(CancellationToken.None);

        // 3. Old token still validates.
        var afterRotation = await c.GetAsync("/api/v1/customer/accounts");
        Assert.Equal(HttpStatusCode.OK, afterRotation.StatusCode);

        // 4. New tokens now signed with new kid.
        var newToken = await _factory.IssueUserAccess("alice", "CustomerPassw0rd!", Audience.Customer,
            "customer.read customer.write");
        Assert.NotEqual(oldToken, newToken);
    }

    [Fact]
    public async Task Kid_In_JWKS_Includes_Primary_Signing_Key()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZeroTrustDbContext>();
        var primary = await db.SigningKeys.Where(k => k.IsPrimary && k.RetiredAtUtc == null).FirstAsync();

        var res = await _factory.CreateClient().GetFromJsonAsync<System.Text.Json.JsonElement>("/.well-known/jwks.json");
        var kids = res.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()).ToArray();
        Assert.Contains(primary.Kid, kids);
    }
}
