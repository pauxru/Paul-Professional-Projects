using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Contoso.Payments.IntegrationTests.Fixture;

/// <summary>
/// Thin helpers used by every endpoint test.  Attaches auth + idempotency headers and returns
/// deserialized DTOs (or a raw <see cref="HttpResponseMessage"/> where the test wants headers).
/// </summary>
public sealed class ApiClient
{
    private readonly HttpClient _http;
    private readonly string _token;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ApiClient(HttpClient http, string token)
    {
        _http = http;
        _token = token;
        _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public HttpClient Http => _http;

    public async Task<HttpResponseMessage> PostRaw(string path, object body, string? idempotencyKey = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await _http.SendAsync(req);
    }

    public async Task<T> PostOk<T>(string path, object body, string? idempotencyKey = null)
    {
        var resp = await PostRaw(path, body, idempotencyKey);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"POST {path} failed with {(int)resp.StatusCode}: {raw}");
        return JsonSerializer.Deserialize<T>(raw, Json)!;
    }

    public async Task<T> GetOk<T>(string path)
    {
        var resp = await _http.GetAsync(path);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"GET {path} failed with {(int)resp.StatusCode}: {raw}");
        return JsonSerializer.Deserialize<T>(raw, Json)!;
    }
}
