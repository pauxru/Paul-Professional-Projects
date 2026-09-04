using System.Diagnostics;
using System.Net.Http.Headers;
using LoadRunner.Core.Metrics;
using LoadRunner.Core.Scenarios;

namespace LoadRunner.Core.Http;

public interface IHttpRequestExecutor
{
    Task<RequestSample> ExecuteAsync(
        HttpStep step,
        Dictionary<string, string> variables,
        int vuId,
        long intendedStartUnixMs,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default HTTP executor. Executes a rendered <see cref="HttpStep"/> against a shared
/// <see cref="HttpClient"/>, records service and intended latencies, and classifies the
/// outcome into an <see cref="ErrorKind"/>. Response body is read as a stream so byte
/// counts are accurate; when the step has JSON extractors, the body is fully materialised
/// so the extractor can operate on it.
/// </summary>
public sealed class HttpRequestExecutor : IHttpRequestExecutor
{
    private readonly HttpClient _client;

    public HttpRequestExecutor(HttpClient client) => _client = client;

    public async Task<RequestSample> ExecuteAsync(
        HttpStep step,
        Dictionary<string, string> variables,
        int vuId,
        long intendedStartUnixMs,
        CancellationToken cancellationToken)
    {
        var url = Templating.Render(step.Url, variables);
        var body = step.Body is null ? null : Templating.Render(step.Body, variables);

        using var request = new HttpRequestMessage(new HttpMethod(step.Method), url);
        if (body is not null)
        {
            request.Content = new StringContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        if (step.Headers is not null)
        {
            foreach (var (k, v) in step.Headers)
            {
                var rendered = Templating.Render(v, variables);
                if (!request.Headers.TryAddWithoutValidation(k, rendered))
                    request.Content?.Headers.TryAddWithoutValidation(k, rendered);
            }
        }

        var actualStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var t0 = Stopwatch.GetTimestamp();
        int status = 0;
        long bytes = 0;
        var error = ErrorKind.None;
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            string? bodyText = null;
            var needsBody = step.ExtractFromJson is { Count: > 0 };
            if (needsBody)
            {
                using var reader = new StreamReader(stream);
                bodyText = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                bytes = bodyText.Length;
            }
            else
            {
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    bytes += read;
            }

            if (status >= 500) error = ErrorKind.Http5xx;
            else if (status >= 400) error = ErrorKind.Http4xx;
            else if (status != step.ExpectedStatus && step.ExpectedStatus != 0)
                error = ErrorKind.AssertionFailure;

            if (step.ExtractFromJson is not null && bodyText is not null)
            {
                foreach (var (variable, path) in step.ExtractFromJson)
                {
                    var value = ValueExtractor.ExtractJson(bodyText, path);
                    if (value is not null) variables[variable] = value;
                }
            }
            if (step.ExtractFromHeader is not null)
            {
                foreach (var (variable, header) in step.ExtractFromHeader)
                {
                    var value = ValueExtractor.ExtractHeader(response.Headers, header)
                                ?? ValueExtractor.ExtractContentHeader(response.Content.Headers, header);
                    if (value is not null) variables[variable] = value;
                }
            }
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            error = ErrorKind.Timeout;
        }
        catch (HttpRequestException)
        {
            error = ErrorKind.Connection;
        }
        catch (Exception)
        {
            error = ErrorKind.Other;
        }

        var elapsedNs = (long)Stopwatch.GetElapsedTime(t0).TotalNanoseconds;
        var completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var intendedNs = Math.Max(0, (completed - intendedStartUnixMs) * 1_000_000L);
        return new RequestSample(
            step.Name,
            intendedStartUnixMs,
            actualStart,
            completed,
            elapsedNs,
            intendedNs,
            status,
            error,
            vuId,
            bytes);
    }
}

/// <summary>
/// A stub executor used by unit tests to control latency and status without a real HTTP
/// server. Given an artificial per-step delay function, it produces deterministic samples.
/// </summary>
public sealed class StubExecutor : IHttpRequestExecutor
{
    public Func<HttpStep, int, TimeSpan> Latency { get; init; } = (_, _) => TimeSpan.FromMilliseconds(1);
    public Func<HttpStep, int, int> Status { get; init; } = (_, _) => 200;
    public Func<HttpStep, int, ErrorKind> Error { get; init; } = (_, _) => ErrorKind.None;
    public int Requests => _requests;
    private int _requests;

    public async Task<RequestSample> ExecuteAsync(
        HttpStep step,
        Dictionary<string, string> variables,
        int vuId,
        long intendedStartUnixMs,
        CancellationToken cancellationToken)
    {
        var latency = Latency(step, vuId);
        var actualStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            await Task.Delay(latency, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Deadline hit before we could complete the "call". Propagate so the load model
            // can stop the VU without silently recording spurious timeouts.
            throw;
        }
        System.Threading.Interlocked.Increment(ref _requests);
        var completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var intendedNs = Math.Max(0, (completed - intendedStartUnixMs) * 1_000_000L);
        return new RequestSample(step.Name, intendedStartUnixMs, actualStart, completed,
            (long)latency.TotalNanoseconds, intendedNs, Status(step, vuId), Error(step, vuId), vuId, 0);
    }
}
