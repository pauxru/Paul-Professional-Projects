using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FeatureFlags.Domain;

namespace FeatureFlags.Sdk;

public sealed class FeatureFlagClientOptions
{
    public const string SectionName = "FeatureFlags";

    [Required, Url]
    public string ApiBaseUrl { get; set; } = "http://localhost:5018";
    [Required, MinLength(1)]
    public string ProjectKey { get; set; } = "acme";
    [Required, MinLength(1)]
    public string EnvironmentKey { get; set; } = "dev";
    [Required, MinLength(1)]
    public string SdkKey { get; set; } = "client-dev-acme-public-demo";
    [Range(1, 3600)]
    public int PollIntervalSeconds { get; set; } = 30;
    [Range(1, 3600)]
    public int FlushIntervalSeconds { get; set; } = 15;
    [Range(1, 100_000)]
    public int EventBufferCapacity { get; set; } = 1_000;
    public string? OfflineStoragePath { get; set; }
}

public sealed record EvaluationDetail<T>(T Value, int? VariationIndex, EvaluationReason Reason);

public interface IFeatureFlagClient : IAsyncDisposable
{
    bool IsReady { get; }
    long DroppedEventCount { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
    bool BoolVariation(string flagKey, EvaluationContext context, bool defaultValue);
    string StringVariation(string flagKey, EvaluationContext context, string defaultValue);
    decimal NumberVariation(string flagKey, EvaluationContext context, decimal defaultValue);
    JsonElement JsonVariation(string flagKey, EvaluationContext context, JsonElement defaultValue);
    EvaluationDetail<bool> BoolVariationDetail(string flagKey, EvaluationContext context, bool defaultValue);
    EvaluationDetail<string> StringVariationDetail(string flagKey, EvaluationContext context, string defaultValue);
    EvaluationDetail<decimal> NumberVariationDetail(string flagKey, EvaluationContext context, decimal defaultValue);
    EvaluationDetail<JsonElement> JsonVariationDetail(string flagKey, EvaluationContext context, JsonElement defaultValue);
    void Track(string eventKey, EvaluationContext context, decimal? numericValue = null);
}

public sealed record SdkEventDto(
    string Kind,
    string? FlagKey,
    int? VariationIndex,
    string ContextKey,
    string? MetricKey,
    decimal? NumericValue,
    DateTimeOffset OccurredAt);

internal sealed record PersistedConfiguration(string? ETag, EnvironmentConfiguration Configuration);
