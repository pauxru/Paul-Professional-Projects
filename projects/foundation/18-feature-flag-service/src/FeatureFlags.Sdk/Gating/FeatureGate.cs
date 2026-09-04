using FeatureFlags.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureFlags.Sdk;

public interface IFeatureGate
{
    ValueTask<bool> IsEnabledAsync(string flagKey, EvaluationContext context, CancellationToken cancellationToken = default);
}

public sealed class FeatureGate(IFeatureFlagClient client) : IFeatureGate
{
    public ValueTask<bool> IsEnabledAsync(string flagKey, EvaluationContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(client.BoolVariation(flagKey, context, false));
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class FeatureGateAttribute(string flagKey) : Attribute
{
    public string FlagKey { get; } = flagKey;
}

public sealed class FeatureGateEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var metadata = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<FeatureGateAttribute>();
        if (metadata is null)
        {
            return await next(context);
        }

        var client = context.HttpContext.RequestServices.GetRequiredService<IFeatureFlagClient>();
        var contextKey = context.HttpContext.Request.Headers["X-Feature-Context"].FirstOrDefault()
            ?? context.HttpContext.User.Identity?.Name
            ?? "anonymous";
        if (!client.BoolVariation(metadata.FlagKey, new EvaluationContext(contextKey), false))
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Feature unavailable", detail: "This endpoint is currently disabled by a feature flag.");
        }

        return await next(context);
    }
}

public static class FeatureGateEndpointExtensions
{
    public static RouteHandlerBuilder RequireFeatureFlag(this RouteHandlerBuilder builder, string flagKey)
    {
        builder.WithMetadata(new FeatureGateAttribute(flagKey));
        builder.AddEndpointFilter<FeatureGateEndpointFilter>();
        return builder;
    }
}
