using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IntegrationHub.Application;
using IntegrationHub.Domain;

namespace IntegrationHub.Infrastructure;

public sealed record RestAuthenticationOptions(
    ConnectorAuthKind Kind,
    string? ApiKeyHeader = null,
    string? ApiKey = null,
    string? Username = null,
    string? Password = null,
    string? BearerToken = null,
    string? TokenEndpoint = null,
    string? ClientId = null,
    string? ClientSecret = null,
    string? Scope = null);

public sealed record RestOperationOptions(
    string Name,
    string Method,
    string PathTemplate,
    string? RequestTemplate = null,
    string? ResponsePath = null,
    PaginationStyle PaginationStyle = PaginationStyle.None,
    string PageParameter = "page",
    string PageSizeParameter = "pageSize",
    string OffsetParameter = "offset",
    string LimitParameter = "limit",
    string CursorParameter = "cursor",
    string NextCursorHeader = "X-Next-Cursor",
    IReadOnlyDictionary<int, string>? ErrorMapping = null);

public sealed record RestConnectorOptions(
    ConnectorDescriptor Descriptor,
    Uri BaseUri,
    RestAuthenticationOptions Authentication,
    IReadOnlyDictionary<string, RestOperationOptions> Operations,
    int MaxAttempts = 4,
    int TimeoutSeconds = 30);

public sealed class ConnectorException(
    string message,
    int? statusCode = null,
    string? responseBody = null,
    Exception? inner = null) : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
    public string? ResponseBody { get; } = responseBody;
}

public sealed class RestConnector : IConnector
{
    public const string MeterName = "IntegrationHub.Connectors";
    private static readonly ActivitySource Activity = new("IntegrationHub.Connectors");
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> Latency = Meter.CreateHistogram<double>("integrationhub.connector.latency.ms");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("integrationhub.connector.retries");
    private readonly HttpClient _httpClient;
    private readonly RestConnectorOptions _options;
    private readonly SecretReferenceResolver _secrets;
    private readonly ConnectorUrlGuard _urlGuard;
    private readonly IClock _clock;
    private readonly RetryExecutor _retry;
    private readonly ConnectorCircuitBreaker _circuit;
    private readonly ConnectorBulkhead _bulkhead;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private OAuthToken? _token;

    public RestConnector(
        HttpClient httpClient,
        RestConnectorOptions options,
        SecretReferenceResolver secrets,
        ConnectorUrlGuard urlGuard,
        IClock clock,
        RetryExecutor? retry = null,
        ConnectorCircuitBreaker? circuit = null,
        ConnectorBulkhead? bulkhead = null)
    {
        _httpClient = httpClient;
        _options = options;
        _secrets = secrets;
        _urlGuard = urlGuard;
        _clock = clock;
        _retry = retry ?? new RetryExecutor();
        _circuit = circuit ?? new ConnectorCircuitBreaker(
            clock,
            3,
            TimeSpan.FromSeconds(30),
            ex => ex is HttpRequestException or TimeoutException
                  || ex is ConnectorException { StatusCode: 429 or >= 500 });
        _bulkhead = bulkhead ?? new ConnectorBulkhead(options.Descriptor.RateLimit.MaxConcurrency);
    }

    public ConnectorDescriptor Descriptor => _options.Descriptor;

    public async Task<ConnectorResult> ExecuteAsync(
        string operation,
        JsonNode? input,
        ConnectorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Operations.TryGetValue(operation, out var operationOptions))
        {
            throw new KeyNotFoundException($"Connector operation '{operation}' was not found.");
        }

        await _urlGuard.ValidateAsync(_options.BaseUri, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        using var activity = Activity.StartActivity($"{Descriptor.Id}.{operation}", ActivityKind.Client);
        var timer = Stopwatch.StartNew();
        activity?.SetTag("connector.id", Descriptor.Id);
        activity?.SetTag("connector.version", Descriptor.Version);
        try
        {
            return await _circuit.ExecuteAsync(() => _bulkhead.ExecuteAsync(
                () => ExecuteOperationAsync(operationOptions, input, context, timeout.Token),
                TimeSpan.FromSeconds(2),
                timeout.Token));
        }
        finally
        {
            timer.Stop();
            Latency.Record(timer.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("connector.id", Descriptor.Id),
                new KeyValuePair<string, object?>("operation", operation));
        }
    }

    private async Task<ConnectorResult> ExecuteOperationAsync(
        RestOperationOptions operation,
        JsonNode? input,
        ConnectorExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (operation.PaginationStyle == PaginationStyle.None)
        {
            var single = await SendWithResilienceAsync(operation, BuildUri(operation, input), input, context, cancellationToken);
            return await ToResultAsync(single, operation, cancellationToken);
        }

        var aggregate = new JsonArray();
        Uri? next = BuildUri(operation, input);
        var page = 1;
        var offset = 0;
        string? cursor = null;
        IReadOnlyDictionary<string, string> lastHeaders = new Dictionary<string, string>();
        var status = 200;

        for (var requestCount = 0; requestCount < 10_000 && next is not null; requestCount++)
        {
            var requestUri = operation.PaginationStyle switch
            {
                PaginationStyle.PageNumber => AddQuery(next, operation.PageParameter, page.ToString(CultureInfo.InvariantCulture),
                    operation.PageSizeParameter, context.PageSize.ToString(CultureInfo.InvariantCulture)),
                PaginationStyle.Offset => AddQuery(next, operation.OffsetParameter, offset.ToString(CultureInfo.InvariantCulture),
                    operation.LimitParameter, context.PageSize.ToString(CultureInfo.InvariantCulture)),
                PaginationStyle.Cursor when cursor is not null => AddQuery(next, operation.CursorParameter, cursor),
                _ => next
            };
            await _urlGuard.ValidateAsync(requestUri, cancellationToken);

            using var response = await SendWithResilienceAsync(operation, requestUri, input, context, cancellationToken);
            status = (int)response.StatusCode;
            lastHeaders = ReadHeaders(response);
            var payload = await ReadJsonAsync(response, cancellationToken);
            var extracted = Extract(payload, operation.ResponsePath);
            var pageItems = extracted as JsonArray ?? new JsonArray(extracted?.DeepClone());
            foreach (var item in pageItems)
            {
                aggregate.Add(item?.DeepClone());
            }

            next = operation.PaginationStyle switch
            {
                PaginationStyle.PageNumber => pageItems.Count < context.PageSize
                    || (response.Headers.TryGetValues("X-Has-More", out var hasMore)
                        && string.Equals(hasMore.FirstOrDefault(), "false", StringComparison.OrdinalIgnoreCase))
                        ? null
                        : BuildUri(operation, input),
                PaginationStyle.Offset => pageItems.Count < context.PageSize
                    ? null
                    : BuildUri(operation, input),
                PaginationStyle.Cursor => GetNextCursor(response, payload, operation, out cursor)
                    ? BuildUri(operation, input)
                    : null,
                PaginationStyle.LinkHeader => GetNextLink(response),
                _ => null
            };
            page++;
            offset += context.PageSize;
        }

        return new ConnectorResult(aggregate, status, lastHeaders, aggregate.Count);
    }

    private async Task<HttpResponseMessage> SendWithResilienceAsync(
        RestOperationOptions operation,
        Uri uri,
        JsonNode? input,
        ConnectorExecutionContext context,
        CancellationToken cancellationToken)
    {
        var refreshedAfter401 = false;
        return await _retry.ExecuteAsync(
            async (_, token) =>
            {
                var response = await SendOnceAsync(operation, uri, input, context, token);
                if (response.StatusCode == HttpStatusCode.Unauthorized
                    && _options.Authentication.Kind == ConnectorAuthKind.OAuth2ClientCredentials
                    && !refreshedAfter401)
                {
                    response.Dispose();
                    _token = null;
                    refreshedAfter401 = true;
                    response = await SendOnceAsync(operation, uri, input, context, token);
                }

                if (response.IsSuccessStatusCode)
                {
                    return new RetryOutcome<HttpResponseMessage>(true, response);
                }

                var body = await response.Content.ReadAsStringAsync(token);
                var status = (int)response.StatusCode;
                var message = operation.ErrorMapping is not null && operation.ErrorMapping.TryGetValue(status, out var mapped)
                    ? mapped
                    : $"Connector returned HTTP {status}.";
                var exception = new ConnectorException(message, status, body);
                var transient = response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500;
                var retryAfter = GetRetryAfter(response);
                response.Dispose();
                return new RetryOutcome<HttpResponseMessage>(false, IsTransient: transient, RetryAfter: retryAfter, Error: exception);
            },
            _options.MaxAttempts,
            TimeSpan.FromMilliseconds(100),
            (_, _) => Retries.Add(1,
                new KeyValuePair<string, object?>("connector.id", Descriptor.Id),
                new KeyValuePair<string, object?>("operation", operation.Name)),
            cancellationToken: cancellationToken);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        RestOperationOptions operation,
        Uri uri,
        JsonNode? input,
        ConnectorExecutionContext context,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(operation.Method), uri);
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", context.CorrelationId);
        if (!string.IsNullOrWhiteSpace(context.IdempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", context.IdempotencyKey);
        }
        if (context.Headers is not null)
        {
            foreach (var header in context.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        await AddAuthenticationAsync(request, cancellationToken);
        if (operation.Method is not ("GET" or "HEAD"))
        {
            var payload = operation.RequestTemplate is null
                ? input?.ToJsonString() ?? "{}"
                : ApplyTemplate(operation.RequestTemplate, input);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }

        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task AddAuthenticationAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var auth = _options.Authentication;
        switch (auth.Kind)
        {
            case ConnectorAuthKind.ApiKey:
                request.Headers.TryAddWithoutValidation(
                    auth.ApiKeyHeader ?? "X-Api-Key",
                    await _secrets.ResolveAsync(auth.ApiKey, cancellationToken));
                break;
            case ConnectorAuthKind.Basic:
                var username = await _secrets.ResolveAsync(auth.Username, cancellationToken) ?? string.Empty;
                var password = await _secrets.ResolveAsync(auth.Password, cancellationToken) ?? string.Empty;
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
                break;
            case ConnectorAuthKind.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    await _secrets.ResolveAsync(auth.BearerToken, cancellationToken));
                break;
            case ConnectorAuthKind.OAuth2ClientCredentials:
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    (await GetOAuthTokenAsync(cancellationToken)).AccessToken);
                break;
        }
    }

    private async Task<OAuthToken> GetOAuthTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && _token.ExpiresAt > _clock.UtcNow.AddSeconds(15))
        {
            return _token;
        }

        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && _token.ExpiresAt > _clock.UtcNow.AddSeconds(15))
            {
                return _token;
            }

            var auth = _options.Authentication;
            var tokenUri = new Uri(_options.BaseUri, auth.TokenEndpoint
                ?? throw new InvalidOperationException("OAuth token endpoint is required."));
            await _urlGuard.ValidateAsync(tokenUri, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = await _secrets.ResolveAsync(auth.ClientId, cancellationToken) ?? string.Empty,
                    ["client_secret"] = await _secrets.ResolveAsync(auth.ClientSecret, cancellationToken) ?? string.Empty,
                    ["scope"] = auth.Scope ?? string.Empty
                })
            };
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ConnectorException("OAuth token request failed.", (int)response.StatusCode);
            }
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?.AsObject()
                       ?? throw new ConnectorException("OAuth token response was invalid.");
            var accessToken = json["access_token"]?.GetValue<string>()
                              ?? throw new ConnectorException("OAuth token response omitted access_token.");
            var expiresIn = json["expires_in"]?.GetValue<int>() ?? 300;
            _token = new OAuthToken(accessToken, _clock.UtcNow.AddSeconds(Math.Max(30, expiresIn)));
            return _token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private Uri BuildUri(RestOperationOptions operation, JsonNode? input)
    {
        var path = Regex.Replace(operation.PathTemplate, "\\{([^}]+)\\}", match =>
        {
            var key = match.Groups[1].Value;
            var value = JsonPath.Get(input, key.StartsWith('$') ? key : $"$.{key}");
            return Uri.EscapeDataString(value?.GetValue<string>() ?? value?.ToString()
                ?? throw new ConnectorException($"Path parameter '{key}' was not supplied."));
        });
        return new Uri(_options.BaseUri, path);
    }

    private static string ApplyTemplate(string template, JsonNode? input) =>
        Regex.Replace(template, "\\{\\{([^}]+)\\}\\}", match =>
        {
            var path = match.Groups[1].Value.Trim();
            var value = JsonPath.Get(input, path);
            return value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
                ? JsonSerializer.Serialize(text)[1..^1]
                : value?.ToJsonString() ?? "null";
        });

    private static async Task<ConnectorResult> ToResultAsync(
        HttpResponseMessage response,
        RestOperationOptions operation,
        CancellationToken cancellationToken)
    {
        using (response)
        {
            var payload = await ReadJsonAsync(response, cancellationToken);
            var extracted = Extract(payload, operation.ResponsePath);
            var count = extracted is JsonArray array ? array.Count : extracted is null ? 0 : 1;
            return new ConnectorResult(extracted, (int)response.StatusCode, ReadHeaders(response), count);
        }
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength == 0)
        {
            return null;
        }
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static JsonNode? Extract(JsonNode? payload, string? responsePath) =>
        string.IsNullOrWhiteSpace(responsePath) ? payload : JsonPath.Get(payload, responsePath);

    private static IReadOnlyDictionary<string, string> ReadHeaders(HttpResponseMessage response) =>
        response.Headers.Concat(response.Content.Headers)
            .ToDictionary(x => x.Key, x => string.Join(",", x.Value), StringComparer.OrdinalIgnoreCase);

    private TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            return date - _clock.UtcNow;
        }
        return null;
    }

    private static Uri AddQuery(Uri uri, params string[] keyValues)
    {
        var builder = new UriBuilder(uri);
        var existing = builder.Query.TrimStart('?');
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            query.Add(existing);
        }
        for (var i = 0; i < keyValues.Length; i += 2)
        {
            query.Add($"{Uri.EscapeDataString(keyValues[i])}={Uri.EscapeDataString(keyValues[i + 1])}");
        }
        builder.Query = string.Join("&", query);
        return builder.Uri;
    }

    private static bool GetNextCursor(
        HttpResponseMessage response,
        JsonNode? payload,
        RestOperationOptions operation,
        out string? cursor)
    {
        cursor = response.Headers.TryGetValues(operation.NextCursorHeader, out var values)
            ? values.FirstOrDefault()
            : payload?["nextCursor"]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(cursor);
    }

    private static Uri? GetNextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }
        foreach (var part in string.Join(",", values).Split(','))
        {
            var segments = part.Split(';', StringSplitOptions.TrimEntries);
            if (segments.Length >= 2 && segments.Skip(1).Any(x => x.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)))
            {
                var url = segments[0].Trim().Trim('<', '>');
                return Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : null;
            }
        }
        return null;
    }

    private sealed record OAuthToken(string AccessToken, DateTimeOffset ExpiresAt);
}

public sealed class ConnectorRegistry(IEnumerable<IConnector> connectors) : IConnectorRegistry
{
    private readonly IReadOnlyList<IConnector> _connectors = connectors.ToArray();

    public IReadOnlyCollection<ConnectorDescriptor> List() =>
        _connectors.Select(x => x.Descriptor)
            .OrderBy(x => x.Id)
            .ThenByDescending(x => x.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IConnector Get(string id, string? version = null)
    {
        var matches = _connectors.Where(x => string.Equals(x.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase));
        if (version is not null)
        {
            matches = matches.Where(x => string.Equals(x.Descriptor.Version, version, StringComparison.OrdinalIgnoreCase));
        }
        return matches.OrderByDescending(x => x.Descriptor.Version, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
               ?? throw new KeyNotFoundException($"Connector '{id}' version '{version ?? "latest"}' was not found.");
    }
}

public sealed class FileConnector : IConnector
{
    private readonly string _root;

    public FileConnector(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public ConnectorDescriptor Descriptor { get; } = new(
        "file",
        "Drop-folder File Connector",
        "1.0.0",
        ConnectorAuthKind.None,
        [
            new("read", "GET", "{path}", null, null, true, "Read CSV or JSON from the configured drop folder."),
            new("write", "POST", "{path}", null, null, true, "Write CSV or JSON into the configured drop folder.")
        ],
        new RateLimitDescriptor(1_000, TimeSpan.FromMinutes(1), 4),
        PaginationStyle.None,
        "Imports and exports JSON and RFC-4180-style CSV files within a restricted root.");

    public async Task<ConnectorResult> ExecuteAsync(
        string operation,
        JsonNode? input,
        ConnectorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var relativePath = input?["path"]?.GetValue<string>()
                           ?? throw new ConnectorException("File path is required.");
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConnectorException("File path escapes the configured drop folder.");
        }

        if (operation == "read")
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            JsonNode? payload = Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)
                ? ParseCsv(content)
                : JsonNode.Parse(content);
            return new ConnectorResult(payload, 200, new Dictionary<string, string>(), payload is JsonArray array ? array.Count : 1);
        }
        if (operation == "write")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var data = input?["data"];
            var content = Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)
                ? WriteCsv(data as JsonArray ?? throw new ConnectorException("CSV output requires an array."))
                : data?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
            await File.WriteAllTextAsync(path, content, cancellationToken);
            return new ConnectorResult(new JsonObject { ["path"] = relativePath }, 201, new Dictionary<string, string>(), 1);
        }

        throw new KeyNotFoundException($"File operation '{operation}' was not found.");
    }

    private static JsonArray ParseCsv(string content)
    {
        var rows = ParseCsvRows(content);
        if (rows.Count == 0)
        {
            return [];
        }
        var headers = rows[0];
        return new JsonArray(rows.Skip(1).Select(row =>
        {
            var item = new JsonObject();
            for (var i = 0; i < headers.Count; i++)
            {
                item[headers[i]] = i < row.Count ? row[i] : string.Empty;
            }
            return (JsonNode)item;
        }).ToArray());
    }

    private static List<List<string>> ParseCsvRows(string content)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < content.Length; i++)
        {
            var character = content[i];
            if (quoted && character == '"' && i + 1 < content.Length && content[i + 1] == '"')
            {
                field.Append('"');
                i++;
            }
            else if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if ((character == '\n' || character == '\r') && !quoted)
            {
                if (character == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
                {
                    i++;
                }
                row.Add(field.ToString());
                field.Clear();
                if (row.Any(x => x.Length > 0))
                {
                    rows.Add(row);
                }
                row = [];
            }
            else
            {
                field.Append(character);
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    private static string WriteCsv(JsonArray array)
    {
        var objects = array.OfType<JsonObject>().ToArray();
        if (objects.Length == 0)
        {
            return string.Empty;
        }
        var headers = objects.SelectMany(x => x.Select(p => p.Key)).Distinct().ToArray();
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", headers.Select(EscapeCsv)));
        foreach (var item in objects)
        {
            builder.AppendLine(string.Join(",", headers.Select(x => EscapeCsv(item[x]?.ToString() ?? string.Empty))));
        }
        return builder.ToString();
    }

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}

public sealed class WebhookSourceConnector(
    WebhookVerifier verifier,
    ISecretStore secretStore,
    PayloadContractValidator validator,
    JsonContract contract,
    string secretReference) : IConnector
{
    public ConnectorDescriptor Descriptor { get; } = new(
        "webhook",
        "Signed Webhook Source",
        "1.0.0",
        ConnectorAuthKind.Hmac,
        [new("verify", "POST", "/api/v1/webhooks/{flowId}", contract, contract, false)],
        new RateLimitDescriptor(120, TimeSpan.FromMinutes(1), 8),
        PaginationStyle.None,
        "Validates HMAC-SHA256 signatures, timestamp windows, nonces and payload contracts.");

    public async Task<ConnectorResult> ExecuteAsync(
        string operation,
        JsonNode? input,
        ConnectorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (operation != "verify" || input is not JsonObject envelope)
        {
            throw new ConnectorException("Webhook verify envelope is required.");
        }
        var body = envelope["body"]?.GetValue<string>() ?? string.Empty;
        var secretName = secretReference.StartsWith("@secret:", StringComparison.OrdinalIgnoreCase)
            ? secretReference[8..]
            : secretReference;
        var result = await verifier.VerifyAsync(
            Encoding.UTF8.GetBytes(body),
            envelope["signature"]?.GetValue<string>() ?? string.Empty,
            envelope["timestamp"]?.GetValue<string>() ?? string.Empty,
            envelope["nonce"]?.GetValue<string>() ?? string.Empty,
            await secretStore.GetAsync(secretName, cancellationToken),
            TimeSpan.FromMinutes(5),
            cancellationToken);
        if (!result.IsValid)
        {
            throw new ConnectorException(result.Error ?? "Webhook verification failed.", 401);
        }

        var payload = JsonNode.Parse(body);
        var violations = validator.Validate(payload, contract);
        if (violations.Count > 0)
        {
            throw new ConnectorException(string.Join("; ", violations.Select(x => $"{x.Path}: {x.Message}")), 422);
        }
        return new ConnectorResult(payload, 200, new Dictionary<string, string>(), 1);
    }
}
