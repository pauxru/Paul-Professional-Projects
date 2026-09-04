using FraudPipeline.Application.Abstractions;
using FraudPipeline.Application.FeatureStore;
using FraudPipeline.Domain.ValueObjects;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FraudPipeline.Api.Endpoints;

public static class FeatureEndpoints
{
    public static IEndpointRouteBuilder MapFeatureEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/features").WithTags("Features");
        group.MapGet("/{entityType}/{id}", GetAsync).RequireAuthorization("risk:investigate");
        return app;
    }

    private static Results<Ok<FeatureView>, NotFound, BadRequest<Microsoft.AspNetCore.Mvc.ProblemDetails>> GetAsync(
        string entityType,
        string id,
        FeatureStoreService features,
        Domain.Abstractions.IClock clock)
    {
        if (!Enum.TryParse<EntityType>(entityType, ignoreCase: true, out var type))
            return TypedResults.BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails { Title = "Invalid entity type" });
        var eid = EntityId.Of(type, id);
        if (!features.Runtime.TryGet(eid, out var state)) return TypedResults.NotFound();
        var now = clock.UtcNow;
        var a1m = state.Aggregator.Aggregate(now, 60);
        var a5m = state.Aggregator.Aggregate(now, 300);
        var a1h = state.Aggregator.Aggregate(now, 3600);
        var a24h = state.Aggregator.Aggregate(now, 86400);
        var a7d = state.Aggregator.Aggregate(now, 7L * 86400);
        return TypedResults.Ok(new FeatureView(
            entityType, id,
            a1m.Count, a5m.Count, a1h.Count, a24h.Count, a7d.Count,
            a1m.AmountSum, a5m.AmountSum, a1h.AmountSum, a24h.AmountSum, a7d.AmountSum,
            a24h.DistinctCountries, a24h.DistinctDevices, a1h.DistinctMerchants,
            state.LastLocation?.CountryIso2,
            state.LastObservationAt));
    }
}

public sealed record FeatureView(
    string EntityType, string Id,
    int TxnCount1m, int TxnCount5m, int TxnCount1h, int TxnCount24h, int TxnCount7d,
    decimal AmountSum1m, decimal AmountSum5m, decimal AmountSum1h, decimal AmountSum24h, decimal AmountSum7d,
    int DistinctCountries24h, int DistinctDevices24h, int DistinctMerchants1h,
    string? LastCountry,
    DateTimeOffset? LastObservationAt);
