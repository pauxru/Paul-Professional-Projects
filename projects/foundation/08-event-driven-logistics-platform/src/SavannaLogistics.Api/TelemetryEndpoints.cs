using SavannaLogistics.Application;

namespace SavannaLogistics.Api;

public sealed record TelemetryBatchRequest(IReadOnlyList<VehiclePingInput> Pings);

public static class TelemetryEndpoints
{
    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/telemetry").WithTags("Telemetry");
        group.MapPost("/", async (
            TelemetryBatchRequest request,
            ITelemetryIngestion ingestion,
            CancellationToken cancellationToken) =>
        {
            if (request.Pings is null || request.Pings.Count is < 1 or > 10_000)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["pings"] = ["A batch must contain between 1 and 10,000 pings."]
                });
            }

            try
            {
                return Results.Accepted(value: await ingestion.IngestAsync(request.Pings, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["pings"] = [exception.Message]
                });
            }
        }).RequireAuthorization("TelemetryIngest").RequireRateLimiting("telemetry-ingest");

        group.MapGet("/stats", async (
            ILogisticsRepository repository,
            ITelemetryEventBus eventBus,
            CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                storedPings = await repository.CountPingsAsync(cancellationToken),
                lateArrivals = await repository.CountLateArrivalsAsync(cancellationToken),
                deadLetters = await repository.CountDeadLettersAsync(cancellationToken),
                consumerLag = eventBus.ConsumerLag,
                busDeadLetters = eventBus.DeadLetterCount
            })).RequireAuthorization("Operations");

        group.MapPost("/simulate", async (
            SimulatorCommand command,
            ISimulatorService simulator,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await simulator.RunAsync(command, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["simulator"] = [exception.Message]
                });
            }
        }).RequireAuthorization("Operations");

        return endpoints;
    }
}
