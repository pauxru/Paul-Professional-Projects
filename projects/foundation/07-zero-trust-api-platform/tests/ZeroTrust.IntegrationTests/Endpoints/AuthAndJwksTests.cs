using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZeroTrust.Api.Endpoints;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Identity;
using ZeroTrust.IntegrationTests.Fixtures;
using ZeroTrust.Infrastructure.Persistence;

namespace ZeroTrust.IntegrationTests.Endpoints;

public class AuthAndJwksTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public AuthAndJwksTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Jwks_Endpoint_Returns_Keys()
    {
        var res = await _client.GetAsync("/.well-known/jwks.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        var keys = doc.GetProperty("keys");
        Assert.True(keys.GetArrayLength() >= 1);
        var first = keys[0];
        Assert.Equal("RSA", first.GetProperty("kty").GetString());
        Assert.Equal("RS256", first.GetProperty("alg").GetString());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("kid").GetString()));
    }

    [Fact]
    public async Task OpenId_Configuration_Advertises_Endpoints()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");
        Assert.Equal("https://zero-trust-demo.localhost", doc.GetProperty("issuer").GetString());
        Assert.Contains("/api/v1/auth/token", doc.GetProperty("token_endpoint").GetString());
        Assert.Contains("/.well-known/jwks.json", doc.GetProperty("jwks_uri").GetString());
    }

    [Fact]
    public async Task Password_Grant_Issues_Access_And_Refresh()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            grant_type = "password",
            subject = "alice",
            password = "CustomerPassw0rd!",
            audience = "ntsf-customer-api",
            scope = "customer.read customer.write"
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("access_token").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("refresh_token").GetString()));
        Assert.Equal("RS256", body.GetProperty("alg").GetString());
    }

    [Fact]
    public async Task Wrong_Password_Is_401_Unauthorized()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            grant_type = "password",
            subject = "alice",
            password = "wrong",
            audience = "ntsf-customer-api"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Refresh_Rotation_Detects_Reuse_And_Revokes_Family()
    {
        // Get a refresh token
        var loginRes = await _client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            grant_type = "password",
            subject = "alice",
            password = "CustomerPassw0rd!",
            audience = "ntsf-customer-api",
            scope = "customer.read"
        });
        var login = await loginRes.Content.ReadFromJsonAsync<JsonElement>();
        var refresh1 = login.GetProperty("refresh_token").GetString()!;

        // Use it once — new refresh returned
        var r1Res = await _client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            grant_type = "refresh_token",
            refresh_token = refresh1
        });
        Assert.Equal(HttpStatusCode.OK, r1Res.StatusCode);
        var r1 = await r1Res.Content.ReadFromJsonAsync<JsonElement>();
        var refresh2 = r1.GetProperty("refresh_token").GetString()!;

        // Reuse the old (consumed) token — must be rejected + whole family revoked.
        var reuseRes = await _client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            grant_type = "refresh_token",
            refresh_token = refresh1
        });
        Assert.Equal(HttpStatusCode.Unauthorized, reuseRes.StatusCode);

        // Newer refresh should now also be revoked.
        var newerRes = await _client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            grant_type = "refresh_token",
            refresh_token = refresh2
        });
        Assert.Equal(HttpStatusCode.Unauthorized, newerRes.StatusCode);
    }

    [Fact]
    public async Task Client_Credentials_Grant_Issues_Partner_Token()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            grant_type = "client_credentials",
            client_id = "acme-treasury-client",
            client_secret = "PartnerSecret!ExampleOnly",
            audience = "ntsf-partner-api",
            scope = "partner.payments.initiate partner.payments.read"
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var access = body.GetProperty("access_token").GetString();
        Assert.False(string.IsNullOrEmpty(access));
        Assert.Contains("partner.payments.initiate", body.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Client_Credentials_Narrows_To_Allowed_Scopes()
    {
        // Savanna partner only has partner.payments.read allowed.
        var res = await _client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            grant_type = "client_credentials",
            client_id = "savanna-client",
            client_secret = "PartnerSecret!ExampleOnly2",
            audience = "ntsf-partner-api",
            scope = "partner.payments.initiate partner.payments.read"
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var scope = body.GetProperty("scope").GetString();
        Assert.Contains("partner.payments.read", scope);
        Assert.DoesNotContain("partner.payments.initiate", scope);
    }
}
