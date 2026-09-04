using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FeatureFlags.Domain;

namespace FeatureFlags.Sdk;

public sealed class FeatureFlagClient : IFeatureFlagClient
{
    private readonly HttpClient _httpClient;
    private readonly FeatureFlagClientOptions _options;
    private readonly FlagEvaluator _evaluator = new();
    private readonly object _configurationLock = new();
    private readonly object _eventLock = new();
    private readonly Queue<SdkEventDto> _events = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private EnvironmentConfiguration? _configuration;
    private string? _etag;
    private Task? _streamTask;
    private Task? _pollTask;
    private Task? _flushTask;
    private long _droppedEvents;
    private bool _initialized;

    public FeatureFlagClient(HttpClient httpClient, FeatureFlagClientOptions options, Func<DateTimeOffset>? utcNow = null)
    {
        _httpClient = httpClient;
        _options = options;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(EnsureTrailingSlash(options.ApiBaseUrl), UriKind.Absolute);
        }
    }

    public bool IsReady
    {
        get
        {
            lock (_configurationLock)
            {
                return _configuration is not null;
            }
        }
    }

    public long DroppedEventCount => Interlocked.Read(ref _droppedEvents);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await LoadPersistedConfigurationAsync(cancellationToken);
        try
        {
            await RefreshAsync(cancellationToken);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            // A local cache, if present, remains usable; callers never observe network exceptions.
        }

        _initialized = true;
        _streamTask = Task.Run(() => RunSseLoopAsync(_stop.Token));
        _pollTask = Task.Run(() => RunPollingLoopAsync(_stop.Token));
        _flushTask = Task.Run(() => RunFlushLoopAsync(_stop.Token));
    }

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/sdk/config/{Uri.EscapeDataString(_options.ProjectKey)}/{Uri.EscapeDataString(_options.EnvironmentKey)}");
        request.Headers.TryAddWithoutValidation("X-Sdk-Key", _options.SdkKey);
        string? currentEtag;
        lock (_configurationLock)
        {
            currentEtag = _etag;
        }
        if (!string.IsNullOrWhiteSpace(currentEtag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", currentEtag);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var configuration = await JsonSerializer.DeserializeAsync<EnvironmentConfiguration>(stream, FeatureFlagJson.Options, cancellationToken)
            ?? throw new InvalidOperationException("Feature flag service returned an empty configuration.");
        var etag = response.Headers.ETag?.ToString();
        lock (_configurationLock)
        {
            _configuration = configuration;
            _etag = etag;
        }
        await PersistConfigurationAsync(configuration, etag, cancellationToken);
        return true;
    }

    public void LoadConfiguration(EnvironmentConfiguration configuration, string? etag = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (_configurationLock)
        {
            _configuration = configuration;
            _etag = etag;
        }
    }

    public bool BoolVariation(string flagKey, EvaluationContext context, bool defaultValue) => BoolVariationDetail(flagKey, context, defaultValue).Value;
    public string StringVariation(string flagKey, EvaluationContext context, string defaultValue) => StringVariationDetail(flagKey, context, defaultValue).Value;
    public decimal NumberVariation(string flagKey, EvaluationContext context, decimal defaultValue) => NumberVariationDetail(flagKey, context, defaultValue).Value;
    public JsonElement JsonVariation(string flagKey, EvaluationContext context, JsonElement defaultValue) => JsonVariationDetail(flagKey, context, defaultValue).Value;

    public EvaluationDetail<bool> BoolVariationDetail(string flagKey, EvaluationContext context, bool defaultValue) => Evaluate(flagKey, context, defaultValue, FlagValueType.Boolean,
        value => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? (true, value.GetBoolean()) : (false, default));

    public EvaluationDetail<string> StringVariationDetail(string flagKey, EvaluationContext context, string defaultValue) => Evaluate(flagKey, context, defaultValue, FlagValueType.String,
        value => value.ValueKind == JsonValueKind.String ? (true, value.GetString() ?? string.Empty) : (false, default!));

    public EvaluationDetail<decimal> NumberVariationDetail(string flagKey, EvaluationContext context, decimal defaultValue) => Evaluate(flagKey, context, defaultValue, FlagValueType.Number,
        value => value.TryGetDecimal(out var number) ? (true, number) : (false, default));

    public EvaluationDetail<JsonElement> JsonVariationDetail(string flagKey, EvaluationContext context, JsonElement defaultValue) => Evaluate(flagKey, context, defaultValue, FlagValueType.Json,
        value => (true, value));

    public void Track(string eventKey, EvaluationContext context, decimal? numericValue = null)
    {
        if (string.IsNullOrWhiteSpace(eventKey))
        {
            return;
        }
        Enqueue(new SdkEventDto("custom", null, null, context.Key, eventKey, numericValue, _utcNow()));
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        List<SdkEventDto> batch;
        lock (_eventLock)
        {
            if (_events.Count == 0)
            {
                return;
            }
            batch = _events.ToList();
            _events.Clear();
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/events")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { projectKey = _options.ProjectKey, environmentKey = _options.EnvironmentKey, events = batch }, FeatureFlagJson.Options), Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-Sdk-Key", _options.SdkKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                {
                    Requeue(batch);
                }
            }
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            Requeue(batch);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        var tasks = new[] { _streamTask, _pollTask, _flushTask }.Where(task => task is not null).Cast<Task>();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private EvaluationDetail<T> Evaluate<T>(string flagKey, EvaluationContext context, T defaultValue, FlagValueType expectedType, Func<JsonElement, (bool Success, T Value)> convert)
    {
        EnvironmentConfiguration? configuration;
        lock (_configurationLock)
        {
            configuration = _configuration;
        }
        if (configuration is null)
        {
            return new EvaluationDetail<T>(defaultValue, null, EvaluationReason.Error(EvaluationErrorKind.ClientNotReady));
        }

        var flag = configuration.FindFlag(flagKey);
        if (flag is null)
        {
            return new EvaluationDetail<T>(defaultValue, null, EvaluationReason.Error(EvaluationErrorKind.FlagNotFound));
        }
        if (flag.ValueType != expectedType)
        {
            return new EvaluationDetail<T>(defaultValue, null, EvaluationReason.Error(EvaluationErrorKind.TypeMismatch));
        }

        var result = _evaluator.Evaluate(configuration, flagKey, context, _utcNow());
        if (result.Reason.Kind == EvaluationReasonKind.Error)
        {
            return new EvaluationDetail<T>(defaultValue, null, result.Reason);
        }

        var converted = convert(result.Value);
        if (!converted.Success)
        {
            return new EvaluationDetail<T>(defaultValue, null, EvaluationReason.Error(EvaluationErrorKind.TypeMismatch));
        }

        Enqueue(new SdkEventDto("evaluation", flagKey, result.VariationIndex, context.Key, null, null, _utcNow()));
        return new EvaluationDetail<T>(converted.Value, result.VariationIndex, result.Reason);
    }

    private async Task LoadPersistedConfigurationAsync(CancellationToken cancellationToken)
    {
        var path = StoragePath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var persisted = await JsonSerializer.DeserializeAsync<PersistedConfiguration>(stream, FeatureFlagJson.Options, cancellationToken);
            if (persisted is not null)
            {
                LoadConfiguration(persisted.Configuration, persisted.ETag);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable caches do not prevent the application from starting.
        }
    }

    private async Task PersistConfigurationAsync(EnvironmentConfiguration configuration, string? etag, CancellationToken cancellationToken)
    {
        var path = StoragePath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, new PersistedConfiguration(etag, configuration), FeatureFlagJson.Options, cancellationToken);
    }

    private async Task RunSseLoopAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/sdk/stream/{Uri.EscapeDataString(_options.ProjectKey)}/{Uri.EscapeDataString(_options.EnvironmentKey)}");
                request.Headers.TryAddWithoutValidation("X-Sdk-Key", _options.SdkKey);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);
                delay = TimeSpan.FromSeconds(1);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }
                    if (line?.StartsWith("data:", StringComparison.Ordinal) == true)
                    {
                        try { await RefreshAsync(cancellationToken); } catch (Exception exception) when (IsTransient(exception)) { }
                    }
                }
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                await DelayWithBackoffAsync(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    private async Task RunPollingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), cancellationToken);
            try { await RefreshAsync(cancellationToken); } catch (Exception exception) when (IsTransient(exception)) { }
        }
    }

    private async Task RunFlushLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.FlushIntervalSeconds), cancellationToken);
            await FlushAsync(cancellationToken);
        }
    }

    private void Enqueue(SdkEventDto item)
    {
        lock (_eventLock)
        {
            if (_events.Count >= _options.EventBufferCapacity)
            {
                Interlocked.Increment(ref _droppedEvents);
                return;
            }
            _events.Enqueue(item);
        }
    }

    private void Requeue(IReadOnlyList<SdkEventDto> batch)
    {
        lock (_eventLock)
        {
            foreach (var item in batch.Reverse())
            {
                if (_events.Count >= _options.EventBufferCapacity)
                {
                    Interlocked.Increment(ref _droppedEvents);
                    continue;
                }
                var combined = new Queue<SdkEventDto>();
                combined.Enqueue(item);
                foreach (var existing in _events) combined.Enqueue(existing);
                _events.Clear();
                foreach (var existing in combined) _events.Enqueue(existing);
            }
        }
    }

    private string StoragePath => string.IsNullOrWhiteSpace(_options.OfflineStoragePath)
        ? Path.Combine(AppContext.BaseDirectory, "featureflags-cache.json")
        : _options.OfflineStoragePath;

    private static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : value + "/";
    private static bool IsTransient(Exception exception) => exception is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException;

    private static async Task DelayWithBackoffAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken);
    }
}
