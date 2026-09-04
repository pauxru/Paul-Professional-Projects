using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FeatureFlags.Domain;
using FeatureFlags.Sdk;

namespace FeatureFlags.UnitTests;

public sealed class BucketingAndSdkTests
{
    [Fact]
    public void Bucketing_IsDeterministicAcrossRepeatedCalls()
    {
        var first = Bucketing.GetBucket("checkout", "salt-v1", "stable-user");
        for (var index = 0; index < 100; index++) Assert.Equal(first, Bucketing.GetBucket("checkout", "salt-v1", "stable-user"));
        Assert.InRange(first, 0, Bucketing.BucketCount - 1);
    }

    [Fact]
    public void Bucketing_HasNearUniformDistributionAcrossTenThousandSharedFixtureKeys()
    {
        var bins = new int[10];
        var keys = FixtureKeys();
        foreach (var key in keys) bins[Bucketing.GetBucket("checkout", "uniform-salt", key) / 10_000]++;
        var chiSquare = bins.Sum(bin => Math.Pow(bin - 1_000, 2) / 1_000d);
        Assert.True(chiSquare < 30d, $"chi-square={chiSquare:F3}; bins={string.Join(',', bins)}");
    }

    [Fact]
    public void Bucketing_IncreasingRolloutFromTenToTwentyPercent_PreservesExistingCohort()
    {
        var baseFlag = TestFlags.Boolean("rollout", fallthrough: 0) with { Salt = "sticky-salt" };
        var ten = baseFlag with { Rollout = new PercentageRollout([new WeightedVariation(1, 10_000)]) };
        var twenty = baseFlag with { Rollout = new PercentageRollout([new WeightedVariation(1, 20_000)]) };
        var evaluator = new FlagEvaluator();
        foreach (var key in FixtureKeys())
        {
            var atTen = evaluator.Evaluate(TestFlags.Configuration(ten), ten.Key, TestFlags.Context(key), DateTimeOffset.UnixEpoch);
            var atTwenty = evaluator.Evaluate(TestFlags.Configuration(twenty), twenty.Key, TestFlags.Context(key), DateTimeOffset.UnixEpoch);
            if (atTen.VariationIndex == 1) Assert.Equal(1, atTwenty.VariationIndex);
        }
    }

    [Fact]
    public async Task ServerAndSdkEvaluation_ParityOverTenThousandKeySharedFixture()
    {
        var flag = TestFlags.Boolean("parity", fallthrough: 0) with { Salt = "parity-salt", Rollout = new PercentageRollout([new WeightedVariation(1, 37_000)]) };
        var config = TestFlags.Configuration(flag);
        using var client = new HttpClient(new StubHandler(_ => throw new HttpRequestException("No network expected"))) { BaseAddress = new Uri("http://localhost/") };
        var options = TestFlags.ClientOptions(capacity: 10_000);
        await using var sdk = new FeatureFlagClient(client, options, () => DateTimeOffset.UnixEpoch);
        sdk.LoadConfiguration(config);
        var evaluator = new FlagEvaluator();
        foreach (var key in FixtureKeys())
        {
            var server = evaluator.Evaluate(config, flag.Key, TestFlags.Context(key), DateTimeOffset.UnixEpoch);
            var local = sdk.BoolVariationDetail(flag.Key, TestFlags.Context(key), false);
            Assert.Equal(server.VariationIndex, local.VariationIndex);
            Assert.Equal(server.Value.GetBoolean(), local.Value);
            Assert.Equal(server.Reason, local.Reason);
        }
    }

    [Fact]
    public async Task Sdk_WhenUnready_ReturnsCallerDefaultWithoutThrowing()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new HttpRequestException()));
        var sdk = new FeatureFlagClient(http, TestFlags.ClientOptions());
        var detail = sdk.BoolVariationDetail("unknown", TestFlags.Context(), true);
        Assert.True(detail.Value);
        Assert.Equal(EvaluationErrorKind.ClientNotReady, detail.Reason.ErrorKind);
        await sdk.DisposeAsync();
    }

    [Fact]
    public async Task Sdk_OfflineBootstrap_UsesPersistedLastKnownGoodConfiguration()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"offline-{Guid.NewGuid():N}.json");
        var flag = TestFlags.Boolean("cached", fallthrough: 1);
        var config = TestFlags.Configuration(flag);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { eTag = "\"1\"", configuration = config }, FeatureFlagJson.Options));
        try
        {
            using var http = new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline"))) { BaseAddress = new Uri("http://localhost/") };
            await using var sdk = new FeatureFlagClient(http, TestFlags.ClientOptions(path));
            await sdk.InitializeAsync();
            var detail = sdk.BoolVariationDetail("cached", TestFlags.Context(), false);
            Assert.True(detail.Value);
            Assert.True(sdk.IsReady);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Sdk_EventBuffer_FlushesBatchesAndCountsDroppedEvents()
    {
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Accepted));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await using var sdk = new FeatureFlagClient(http, TestFlags.ClientOptions(capacity: 2));
        sdk.LoadConfiguration(TestFlags.Configuration(TestFlags.Boolean("events", fallthrough: 1)));
        _ = sdk.BoolVariation("events", TestFlags.Context("one"), false);
        _ = sdk.BoolVariation("events", TestFlags.Context("two"), false);
        _ = sdk.BoolVariation("events", TestFlags.Context("three"), false);
        await sdk.FlushAsync();
        Assert.Equal(1, sdk.DroppedEventCount);
        Assert.Single(handler.Bodies);
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(2, body.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task Sdk_EventBuffer_RetriesTransientFailure()
    {
        var handler = new CapturingHandler((_, count) => new HttpResponseMessage(count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await using var sdk = new FeatureFlagClient(http, TestFlags.ClientOptions());
        sdk.LoadConfiguration(TestFlags.Configuration(TestFlags.Boolean("events", fallthrough: 1)));
        _ = sdk.BoolVariation("events", TestFlags.Context(), false);
        await sdk.FlushAsync();
        await sdk.FlushAsync();
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task Sdk_ConditionalRefresh_HonorsEtag304()
    {
        var flag = TestFlags.Boolean("etag", fallthrough: 1);
        var config = TestFlags.Configuration(flag);
        var handler = new CapturingHandler((request, count) =>
        {
            if (count == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(config, FeatureFlagJson.Options), Encoding.UTF8, "application/json") };
                response.Headers.ETag = new EntityTagHeaderValue("\"1\"");
                return response;
            }
            Assert.True(request.Headers.TryGetValues("If-None-Match", out var values));
            Assert.Contains("\"1\"", values);
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        var path = Path.Combine(AppContext.BaseDirectory, $"etag-{Guid.NewGuid():N}.json");
        try
        {
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            await using var sdk = new FeatureFlagClient(http, TestFlags.ClientOptions(path));
            Assert.True(await sdk.RefreshAsync());
            Assert.False(await sdk.RefreshAsync());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string[] FixtureKeys() => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bucketing-keys.txt"));
}
