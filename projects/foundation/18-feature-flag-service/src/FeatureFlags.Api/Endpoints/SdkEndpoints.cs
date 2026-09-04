using System.Text.Json;
using FeatureFlags.Api.Contracts;
using FeatureFlags.Application;
using FeatureFlags.Domain;

namespace FeatureFlags.Api.Endpoints;

public static class SdkEndpoints
{
    public static IEndpointRouteBuilder MapSdkEndpoints(this IEndpointRouteBuilder app)
    {
        var sdk = app.MapGroup("/api/v1/sdk").RequireRateLimiting("sdk");
        sdk.MapGet("/config/{projectKey}/{environmentKey}", async (string projectKey, string environmentKey, HttpContext context, FlagService service) =>
        {
            var key = context.Request.Headers["X-Sdk-Key"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(key)) return Results.Unauthorized();
            var configuration = await service.GetPublicConfigurationAsync(projectKey, environmentKey, key, context.RequestAborted);
            var etag = $"\"{configuration.Version}\"";
            if (context.Request.Headers.IfNoneMatch.Any(value => string.Equals(value?.ToString(), etag, StringComparison.Ordinal)))
            {
                context.Response.Headers.ETag = etag;
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            context.Response.Headers.ETag = etag;
            context.Response.Headers.CacheControl = "private, max-age=0";
            return Results.Json(configuration, FeatureFlagJson.Options);
        });

        sdk.MapGet("/stream/{projectKey}/{environmentKey}", async (string projectKey, string environmentKey, HttpContext context, FlagService service, IConfigurationBroadcaster broadcaster) =>
        {
            var key = context.Request.Headers["X-Sdk-Key"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(key))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            try
            {
                await service.GetPublicConfigurationAsync(projectKey, environmentKey, key, context.RequestAborted);
            }
            catch (UnauthorizedAccessException)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await context.Response.WriteAsync("event: connected\ndata: {\"status\":\"connected\"}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await foreach (var change in broadcaster.SubscribeAsync(projectKey, environmentKey, context.RequestAborted))
            {
                var payload = JsonSerializer.Serialize(new { change.Version }, FeatureFlagJson.Options);
                await context.Response.WriteAsync($"event: config\ndata: {payload}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        });

        app.MapPost("/api/v1/events", async (SdkEventsRequest request, HttpContext context, FlagService service) =>
        {
            if (ApiValidation.Errors(request) is { } errors) return Results.ValidationProblem(errors);
            var key = context.Request.Headers["X-Sdk-Key"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(key)) return Results.Unauthorized();
            await service.GetPublicConfigurationAsync(request.ProjectKey, request.EnvironmentKey, key, context.RequestAborted);
            var events = request.Events.Select(item => new AnalyticsEvent(request.ProjectKey, request.EnvironmentKey, item.Kind, item.FlagKey,
                item.VariationIndex, item.ContextKey, item.MetricKey, item.NumericValue, item.OccurredAt == default ? DateTimeOffset.UtcNow : item.OccurredAt)).ToArray();
            await service.RecordEventsAsync(events, context.RequestAborted);
            return Results.Accepted();
        }).RequireRateLimiting("sdk");

        return app;
    }
}
