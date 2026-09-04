using System.Net;
using System.Text;
using System.Text.Json;
using FeatureFlags.Domain;
using FeatureFlags.Sdk;
using FeatureFlags.Application;

namespace FeatureFlags.UnitTests;

internal static class TestFlags
{
    public static FlagDefinition Boolean(string key = "flag", bool on = true, int fallthrough = 0, int off = 0) => new()
    {
        Key = key, Name = key, ValueType = FlagValueType.Boolean, IsOn = on, OffVariation = off, FallthroughVariation = fallthrough,
        Salt = "test-salt", LifecycleStatus = FlagLifecycleStatus.Active,
        Variations = [FlagVariation.Create(0, "off", false), FlagVariation.Create(1, "on", true), FlagVariation.Create(2, "alternate", true)]
    };

    public static EnvironmentConfiguration Configuration(params FlagDefinition[] flags) => new()
    {
        ProjectKey = "test", EnvironmentKey = "dev", Version = 1, GeneratedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), Flags = flags
    };

    public static EvaluationContext Context(string key = "user-1", object? attributes = null) => EvaluationContext.Create(key, attributes);

    public static FeatureFlagClientOptions ClientOptions(string? storagePath = null, int capacity = 1_000) => new()
    {
        ApiBaseUrl = "http://localhost", ProjectKey = "test", EnvironmentKey = "dev", SdkKey = "client-key",
        PollIntervalSeconds = 3600, FlushIntervalSeconds = 3600, EventBufferCapacity = capacity,
        OfflineStoragePath = storagePath ?? Path.Combine(AppContext.BaseDirectory, $"sdk-cache-{Guid.NewGuid():N}.json")
    };

    public static JsonElement Json(object? value) => FeatureFlagJson.Element(value);
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(handler(request));
    }
}

internal sealed class CapturingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> handler) : HttpMessageHandler
{
    public int CallCount { get; private set; }
    public List<string> Bodies { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        return handler(request, CallCount);
    }

}

internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = now;
    public void Advance(TimeSpan duration) => UtcNow += duration;
}
