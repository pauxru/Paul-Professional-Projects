using Idp.Api.Contracts;
using Idp.Application.Metrics;
using Idp.Application.Suppliers;

namespace Idp.Api.Endpoints;

public static class SupplierEndpoints
{
    public static RouteGroupBuilder MapSupplierEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/suppliers", ListAsync)
            .RequireAuthorization()
            .WithTags("Suppliers")
            .WithName("ListSuppliers");

        group.MapGet("/metrics/stp", StpAsync)
            .RequireAuthorization()
            .WithTags("Metrics")
            .WithName("GetStpMetrics");

        return group;
    }

    private static async Task<IResult> ListAsync(SupplierService suppliers, CancellationToken ct)
    {
        var list = await suppliers.ListAsync(ct);
        return Results.Ok(list.Select(DtoMapper.ToSupplier).ToList());
    }

    private static async Task<IResult> StpAsync(IStpMetricsService metrics, CancellationToken ct)
    {
        var snapshot = await metrics.ComputeAsync(ct);
        return Results.Ok(new
        {
            totalDocuments = snapshot.TotalDocuments,
            processed = snapshot.Processed,
            autoApproved = snapshot.AutoApproved,
            inReview = snapshot.InReview,
            rejected = snapshot.Rejected,
            exported = snapshot.Exported,
            failed = snapshot.Failed,
            reviewQueueDepth = snapshot.ReviewQueueDepth,
            straightThroughRate = snapshot.StraightThroughRate
        });
    }
}
