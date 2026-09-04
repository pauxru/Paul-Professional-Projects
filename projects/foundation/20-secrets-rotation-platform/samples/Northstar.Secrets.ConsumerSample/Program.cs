using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

var endpoint = args.FirstOrDefault() ?? "http://localhost:5020";
var consumerId = args.Length > 1 && Guid.TryParse(args[1], out var parsed)
    ? parsed
    : Guid.Empty;

using var httpClient = new HttpClient { BaseAddress = new Uri(endpoint) };
var tokenResponse = await httpClient.PostAsJsonAsync(
    "/api/v1/auth/token",
    new
    {
        subject = "sample-consumer",
        scopes = new[] { "secrets.read", "secrets.ack" }
    });
tokenResponse.EnsureSuccessStatusCode();
var tokenPayload = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
    "Bearer", tokenPayload.GetProperty("accessToken").GetString());

var client = new CachingSecretsClient(httpClient, consumerId);
if (consumerId == Guid.Empty)
{
    Console.WriteLine("Pass a registered consumer GUID as the second argument to poll rotations.");
    return;
}

var refreshed = await client.PollRefreshAndAcknowledgeAsync(CancellationToken.None);
Console.WriteLine($"Processed {refreshed} pending rotation notice(s); values were never printed.");

internal sealed class CachingSecretsClient(HttpClient httpClient, Guid consumerId)
{
    private readonly ConcurrentDictionary<string, CachedSecret> _cache = new(StringComparer.Ordinal);

    public async Task<int> PollRefreshAndAcknowledgeAsync(CancellationToken cancellationToken)
    {
        var pending = await httpClient.GetFromJsonAsync<JsonElement>(
            $"/api/v1/consumers/{consumerId}/pending", cancellationToken);
        var count = 0;
        foreach (var item in pending.EnumerateArray())
        {
            var reference = item.GetProperty("secretReference").GetString();
            var rotationId = item.GetProperty("rotation").GetProperty("id").GetGuid();
            if (reference is null)
            {
                continue;
            }

            await ReceiveRotationNoticeAsync(reference, rotationId, cancellationToken);
            count++;
        }

        return count;
    }

    public async Task ReceiveRotationNoticeAsync(
        string secretReference,
        Guid rotationId,
        CancellationToken cancellationToken)
    {
        var parsed = ParseReference(secretReference);
        var encodedName = parsed.Name.Replace('/', '~');
        var value = await httpClient.GetFromJsonAsync<SecretValueResponse>(
            $"/api/v1/secrets/{encodedName}/value?version={parsed.Version}&reason=rotation-refresh",
            cancellationToken)
            ?? throw new InvalidOperationException("Secret refresh returned no payload.");
        _cache[parsed.Name] = new CachedSecret(value.Value, value.ExpiresAt);

        var acknowledgement = await httpClient.PostAsync(
            $"/api/v1/consumers/{consumerId}/rotations/{rotationId}/acknowledge",
            content: null,
            cancellationToken);
        acknowledgement.EnsureSuccessStatusCode();
    }

    public string? TryGetCached(string hierarchicalName) =>
        _cache.TryGetValue(hierarchicalName, out var cached) &&
        cached.ExpiresAt > DateTimeOffset.UtcNow
            ? cached.Value
            : null;

    private static (string Name, int Version) ParseReference(string value)
    {
        const string prefix = "@secret:";
        var separator = value.LastIndexOf("#v", StringComparison.Ordinal);
        if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
            separator < prefix.Length ||
            !int.TryParse(value[(separator + 2)..], out var version))
        {
            throw new FormatException("Invalid secret reference.");
        }

        return (value[prefix.Length..separator], version);
    }

    private sealed record CachedSecret(string Value, DateTimeOffset ExpiresAt);
    private sealed record SecretValueResponse(string Value, DateTimeOffset ExpiresAt);
}
