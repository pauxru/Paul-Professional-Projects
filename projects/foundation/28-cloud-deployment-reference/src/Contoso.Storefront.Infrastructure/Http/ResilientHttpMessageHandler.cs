using System.Diagnostics;
using System.Net;
using Contoso.Storefront.Application.Configuration;
using Contoso.Storefront.Application.Ports;
using Microsoft.Extensions.Options;

namespace Contoso.Storefront.Infrastructure.Http;

public sealed class CircuitOpenException(string message) : HttpRequestException(message);

public sealed class ResilientHttpMessageHandler(
    IOptions<ResilienceOptions> options,
    IClock clock,
    IAsyncDelay delay) : DelegatingHandler
{
    private readonly object _stateLock = new();
    private CircuitState _state;
    private int _consecutiveFailures;
    private DateTimeOffset _openedAt;
    private bool _halfOpenProbeInProgress;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        EnterCircuit();
        var settings = options.Value;
        Exception? lastException = null;

        for (var attempt = 0; attempt <= settings.RetryCount; attempt++)
        {
            using var attemptRequest = await CloneRequestAsync(request, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(settings.TimeoutMs));

            try
            {
                var response = await base.SendAsync(attemptRequest, timeout.Token);
                if (!IsTransient(response.StatusCode))
                {
                    MarkSuccess();
                    return response;
                }

                if (attempt == settings.RetryCount)
                {
                    MarkFailure();
                    return response;
                }

                response.Dispose();
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new TimeoutException(
                    $"The downstream request exceeded {settings.TimeoutMs} ms.",
                    exception);
                if (attempt == settings.RetryCount)
                {
                    MarkFailure();
                    throw lastException;
                }
            }
            catch (HttpRequestException exception)
            {
                lastException = exception;
                if (attempt == settings.RetryCount)
                {
                    MarkFailure();
                    throw;
                }
            }

            var backoff = TimeSpan.FromMilliseconds(
                settings.RetryBaseDelayMs * Math.Pow(2, attempt));
            await delay.DelayAsync(backoff, cancellationToken);
        }

        MarkFailure();
        throw lastException ?? new HttpRequestException("The downstream request failed.");
    }

    private void EnterCircuit()
    {
        lock (_stateLock)
        {
            if (_state == CircuitState.Closed)
            {
                return;
            }

            if (_state == CircuitState.Open)
            {
                var breakDuration = TimeSpan.FromMilliseconds(options.Value.CircuitBreakDurationMs);
                if (clock.UtcNow - _openedAt < breakDuration)
                {
                    throw new CircuitOpenException("The downstream circuit is open.");
                }

                _state = CircuitState.HalfOpen;
                _halfOpenProbeInProgress = false;
            }

            if (_halfOpenProbeInProgress)
            {
                throw new CircuitOpenException("A half-open circuit probe is already in progress.");
            }

            _halfOpenProbeInProgress = true;
        }
    }

    private void MarkSuccess()
    {
        lock (_stateLock)
        {
            _state = CircuitState.Closed;
            _consecutiveFailures = 0;
            _halfOpenProbeInProgress = false;
        }
    }

    private void MarkFailure()
    {
        lock (_stateLock)
        {
            _consecutiveFailures++;
            if (_state == CircuitState.HalfOpen ||
                _consecutiveFailures >= options.Value.CircuitFailureThreshold)
            {
                _state = CircuitState.Open;
                _openedAt = clock.UtcNow;
            }

            _halfOpenProbeInProgress = false;
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var content = new ByteArrayContent(
                await request.Content.ReadAsByteArrayAsync(cancellationToken));
            foreach (var header in request.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = content;
        }

        return clone;
    }

    private enum CircuitState
    {
        Closed,
        Open,
        HalfOpen
    }
}

public sealed class CorrelationPropagationHandler(ICorrelationContext correlationContext) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(correlationContext.CorrelationId))
        {
            request.Headers.TryAddWithoutValidation(
                "X-Correlation-Id",
                correlationContext.CorrelationId);
        }

        if (Activity.Current is { } activity && !request.Headers.Contains("traceparent"))
        {
            request.Headers.TryAddWithoutValidation(
                "traceparent",
                $"00-{activity.TraceId}-{activity.SpanId}-{(activity.Recorded ? "01" : "00")}");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
