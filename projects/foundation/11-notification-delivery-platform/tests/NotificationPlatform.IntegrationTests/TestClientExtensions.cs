namespace NotificationPlatform.IntegrationTests;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

public static class TestClientExtensions
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<HttpClient> AsTenantAsync(this HttpClient client, Guid tenantId, params string[] scopes)
    {
        var payload = new { TenantId = tenantId, Scopes = scopes.Length == 0 ? null : scopes };
        var resp = await client.PostAsJsonAsync("/api/v1/auth/token", payload);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        var token = doc.GetProperty("access_token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
