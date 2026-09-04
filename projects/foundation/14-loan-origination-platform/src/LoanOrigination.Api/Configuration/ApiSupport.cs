using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using LoanOrigination.Application.Ports;
using LoanOrigination.Domain.Models;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace LoanOrigination.Api.Configuration;

public static class RequestMetadata
{
    public const string CorrelationItem = "correlation-id";

    public static string CorrelationId(HttpContext context) =>
        context.Items.TryGetValue(CorrelationItem, out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    public static string Actor(HttpContext context) =>
        context.User.Identity?.Name ??
        context.User.FindFirst("sub")?.Value ??
        "anonymous";

    public static string SourceIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static string UserAgent(HttpContext context) =>
        context.Request.Headers.UserAgent.ToString();
}

public static class MiddlewareExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app, ILogger logger) =>
        app.Use(async (context, next) =>
        {
            var requested = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
            var correlationId = !string.IsNullOrWhiteSpace(requested) && requested.Length <= 128
                ? requested
                : Guid.NewGuid().ToString("N");
            context.Items[RequestMetadata.CorrelationItem] = correlationId;
            context.Response.Headers["X-Correlation-Id"] = correlationId;
            using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
            {
                await next(context);
            }
        });

    public static IApplicationBuilder UsePlatformSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'";
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            await next(context);
        });
}

public interface IWebhookReplayStore
{
    bool TryRecord(string nonce, DateTimeOffset expiresAt);
}

public sealed class InMemoryWebhookReplayStore : IWebhookReplayStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    public bool TryRecord(string nonce, DateTimeOffset expiresAt)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _seen.Where(pair => pair.Value < now))
        {
            _seen.TryRemove(pair.Key, out _);
        }

        return _seen.TryAdd(nonce, expiresAt);
    }
}

public sealed class ProviderCallbackSignatureMiddleware(
    RequestDelegate next,
    IHostEnvironment environment,
    Microsoft.Extensions.Options.IOptions<WebhookOptions> options,
    IWebhookReplayStore replayStore)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!environment.IsProduction() ||
            !context.Request.Path.Equals("/api/v1/disbursements/callback", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var timestampText = context.Request.Headers["X-Provider-Timestamp"].FirstOrDefault();
        var nonce = context.Request.Headers["X-Provider-Nonce"].FirstOrDefault();
        var suppliedSignature = context.Request.Headers["X-Provider-Signature"].FirstOrDefault()?.Replace("sha256=", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (!long.TryParse(timestampText, out var unixSeconds) || string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(suppliedSignature))
        {
            await RejectAsync(context, "Provider callback signature headers are required.");
            return;
        }

        var timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var window = TimeSpan.FromMinutes(options.Value.TimestampWindowMinutes);
        if (DateTimeOffset.UtcNow - timestamp > window || timestamp - DateTimeOffset.UtcNow > window)
        {
            await RejectAsync(context, "Provider callback timestamp is invalid.");
            return;
        }

        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        context.Request.Body.Position = 0;
        var signedPayload = $"{timestampText}.{nonce}.{body}";
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Value.SigningSecret), Encoding.UTF8.GetBytes(signedPayload));
        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(suppliedSignature);
        }
        catch (FormatException)
        {
            await RejectAsync(context, "Provider callback signature format is invalid.");
            return;
        }

        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
        {
            await RejectAsync(context, "Provider callback signature is invalid.");
            return;
        }

        if (!replayStore.TryRecord(nonce, timestamp.Add(window)))
        {
            await RejectAsync(context, "Provider callback nonce was replayed.");
            return;
        }

        await next(context);
    }

    private static Task RejectAsync(HttpContext context, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid provider callback", detail: detail).ExecuteAsync(context);
}

public sealed class DomainExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var status = exception switch
        {
            LoanOrigination.Application.Services.DuplicateCustomerException => StatusCodes.Status409Conflict,
            DomainException => StatusCodes.Status422UnprocessableEntity,
            ArgumentException => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status500InternalServerError
        };
        var problem = new ProblemDetails
        {
            Status = status,
            Title = status == StatusCodes.Status500InternalServerError ? "Unexpected server error" : "Business rule rejected",
            Detail = status == StatusCodes.Status500InternalServerError ? "An unexpected error occurred." : exception.Message,
            Type = $"https://httpstatuses.com/{status}"
        };
        problem.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        problem.Extensions["correlationId"] = RequestMetadata.CorrelationId(httpContext);
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }
}

public static class LoanTelemetry
{
    public const string MeterName = "RiftValleyCredit.LoanOrigination";
    public static readonly Meter Meter = new(MeterName);
    public static readonly Histogram<double> DecisionLatencyMilliseconds = Meter.CreateHistogram<double>("loan.decision.latency.ms");
    public static readonly Counter<long> ApprovalCount = Meter.CreateCounter<long>("loan.approval.count");
    public static readonly Counter<long> ReferralCount = Meter.CreateCounter<long>("loan.referral.count");
    public static readonly Counter<long> SlaBreachCount = Meter.CreateCounter<long>("loan.sla.breach.count");
    private static long _underwritingDecisions;
    private static long _approvals;
    private static long _screeningDecisions;
    private static long _referrals;

    public static readonly ObservableGauge<double> ApprovalRate = Meter.CreateObservableGauge(
        "loan.approval.rate",
        () => Volatile.Read(ref _underwritingDecisions) == 0
            ? 0d
            : (double)Volatile.Read(ref _approvals) / Volatile.Read(ref _underwritingDecisions));
    public static readonly ObservableGauge<double> ReferralRate = Meter.CreateObservableGauge(
        "loan.referral.rate",
        () => Volatile.Read(ref _screeningDecisions) == 0
            ? 0d
            : (double)Volatile.Read(ref _referrals) / Volatile.Read(ref _screeningDecisions));

    public static void RecordScreeningOutcome(bool referred)
    {
        Interlocked.Increment(ref _screeningDecisions);
        if (referred)
        {
            Interlocked.Increment(ref _referrals);
            ReferralCount.Add(1);
        }
    }

    public static void RecordUnderwritingOutcome(string? decision)
    {
        Interlocked.Increment(ref _underwritingDecisions);
        if (string.Equals(decision, "APPROVE", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _approvals);
            ApprovalCount.Add(1);
        }
    }
}

public static class ApiValidation
{
    public static IResult Invalid(string property, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [property] = [message] });

    public static void EnsurePositive(decimal amount, string property)
    {
        if (amount <= 0m)
        {
            throw new ArgumentException($"{property} must be greater than zero.");
        }
    }
}
