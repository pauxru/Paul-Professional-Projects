using SavannaLogistics.Application;

namespace SavannaLogistics.Api;

public static class OperationalEndpoints
{
    public static IEndpointRouteBuilder MapOperationalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var alerts = endpoints.MapGroup("/api/v1/alerts").WithTags("Alerts");
        alerts.MapGet("/", async (
            int? page,
            int? pageSize,
            ILogisticsRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(await repository.GetAlertsAsync(page ?? 1, pageSize ?? 50, cancellationToken)))
            .RequireAuthorization("Operations");

        var eta = endpoints.MapGroup("/api/v1/eta").WithTags("ETA");
        eta.MapGet("/vehicles/{vehicleId:guid}", async (
            Guid vehicleId,
            int? take,
            ILogisticsRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(await repository.GetEtaHistoryAsync(vehicleId, take ?? 100, cancellationToken)))
            .RequireAuthorization("FleetRead");
        eta.MapGet("/accuracy", async (
            ILogisticsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var accuracy = await repository.GetEtaAccuracyAsync(cancellationToken);
            return Results.Ok(new
            {
                accuracy.Samples,
                accuracy.MeanAbsoluteErrorSeconds,
                accuracy.P90AbsoluteErrorSeconds
            });
        }).RequireAuthorization("Operations");

        var replay = endpoints.MapGroup("/api/v1/replay").WithTags("Replay");
        replay.MapPost("/", async (
            ReplayCommand command,
            IReplayService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.ReplayAsync(command, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["replay"] = [exception.Message]
                });
            }
        }).RequireAuthorization("Operations");
        replay.MapPost("/rebuild-projection", async (
            IReplayService service,
            CancellationToken cancellationToken) =>
            Results.Ok(new { eventsReplayed = await service.RebuildProjectionAsync(cancellationToken) }))
            .RequireAuthorization("Operations");

        return endpoints;
    }
}
