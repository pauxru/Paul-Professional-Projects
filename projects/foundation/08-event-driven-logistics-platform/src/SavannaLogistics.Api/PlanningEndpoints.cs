using SavannaLogistics.Application;
using SavannaLogistics.Domain;
using SavannaLogistics.Infrastructure;

namespace SavannaLogistics.Api;

public sealed record CreateRouteStopRequest(string Name, double Latitude, double Longitude, double RadiusKm, int DwellMinutes);
public sealed record CreateRouteRequest(string Name, IReadOnlyList<GeoPoint> Polyline, IReadOnlyList<CreateRouteStopRequest> Stops);
public sealed record CreateGeofenceRequest(
    string Name,
    string Shape,
    double? CenterLatitude,
    double? CenterLongitude,
    double? RadiusKm,
    IReadOnlyList<GeoPoint>? Polygon);
public sealed record CreateTripRequest(Guid VehicleId, Guid RouteId, DateTimeOffset PlannedStart, DateTimeOffset SlaDueAt);

public static class PlanningEndpoints
{
    public static IEndpointRouteBuilder MapPlanningEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapRoutes(endpoints);
        MapGeofences(endpoints);
        MapTrips(endpoints);
        return endpoints;
    }

    private static void MapRoutes(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/routes").WithTags("Routes");
        group.MapGet("/", async (
            int? page,
            int? pageSize,
            ILogisticsRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(await repository.GetRoutesAsync(page ?? 1, pageSize ?? 50, cancellationToken)))
            .RequireAuthorization("FleetRead");
        group.MapGet("/{id:guid}", async (
            Guid id,
            ILogisticsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var route = await repository.GetRouteAsync(id, cancellationToken);
            if (route is null) return Results.NotFound();
            var stops = await repository.GetRouteStopsAsync(id, cancellationToken);
            return Results.Ok(new { route, stops });
        }).RequireAuthorization("FleetRead");
        group.MapPost("/", async (
            CreateRouteRequest request,
            ILogisticsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateRoute(request);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var route = new RoutePlan(Guid.NewGuid(), request.Name, request.Polyline);
            var stops = request.Stops.Select((stop, sequence) => new RouteStop(
                Guid.NewGuid(),
                route.Id,
                sequence,
                stop.Name,
                new GeoPoint(stop.Latitude, stop.Longitude),
                stop.RadiusKm,
                stop.DwellMinutes)).ToArray();
            await repository.AddRouteAsync(route, stops, cancellationToken);
            return Results.Created($"/api/v1/routes/{route.Id}", new { route, stops });
        }).RequireAuthorization("FleetWrite");
    }

    private static void MapGeofences(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/geofences").WithTags("Geofences");
        group.MapGet("/", async (
            int? page,
            int? pageSize,
            ILogisticsRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(await repository.GetGeofencesAsync(page ?? 1, pageSize ?? 50, cancellationToken)))
            .RequireAuthorization("FleetRead");
        group.MapPost("/", async (
            CreateGeofenceRequest request,
            ILogisticsRepository repository,
            GeofenceIndexCatalog catalog,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateGeofence(request);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var geofence = string.Equals(request.Shape, "circle", StringComparison.OrdinalIgnoreCase)
                ? Geofence.Circle(
                    Guid.NewGuid(),
                    request.Name,
                    new GeoPoint(request.CenterLatitude!.Value, request.CenterLongitude!.Value),
                    request.RadiusKm!.Value)
                : Geofence.Polygon(Guid.NewGuid(), request.Name, request.Polygon!);
            await repository.AddGeofenceAsync(geofence, cancellationToken);
            catalog.Replace(await repository.GetAllGeofencesAsync(cancellationToken));
            return Results.Created($"/api/v1/geofences/{geofence.Id}", geofence);
        }).RequireAuthorization("FleetWrite");
    }

    private static void MapTrips(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/trips").WithTags("Trips");
        group.MapGet("/", async (
            int? page,
            int? pageSize,
            ILogisticsRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(await repository.GetTripsAsync(page ?? 1, pageSize ?? 50, cancellationToken)))
            .RequireAuthorization("FleetRead");
        group.MapPost("/", async (
            CreateTripRequest request,
            ILogisticsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (await repository.GetVehicleAsync(request.VehicleId, cancellationToken) is null)
                errors["vehicleId"] = ["Vehicle does not exist."];
            if (await repository.GetRouteAsync(request.RouteId, cancellationToken) is null)
                errors["routeId"] = ["Route does not exist."];
            if (request.SlaDueAt <= request.PlannedStart)
                errors["slaDueAt"] = ["SLA due time must be after planned start."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var trip = new Trip(Guid.NewGuid(), request.VehicleId, request.RouteId, request.PlannedStart, request.SlaDueAt);
            await repository.AddTripAsync(trip, cancellationToken);
            return Results.Created($"/api/v1/trips/{trip.Id}", trip);
        }).RequireAuthorization("FleetWrite");
        group.MapPost("/{id:guid}/start", async (
            Guid id,
            ILogisticsRepository repository,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var trip = await repository.GetTripAsync(id, cancellationToken);
            if (trip is null) return Results.NotFound();
            try
            {
                trip.Start(clock.UtcNow);
                trip.MarkInTransit();
                await repository.SaveTripAsync(trip, cancellationToken);
                return Results.Ok(trip);
            }
            catch (InvalidOperationException exception)
            {
                return Results.UnprocessableEntity(new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Title = "Trip transition rejected",
                    Detail = exception.Message,
                    Status = StatusCodes.Status422UnprocessableEntity
                });
            }
        }).RequireAuthorization("FleetWrite");
        group.MapPost("/{id:guid}/abort", async (
            Guid id,
            ILogisticsRepository repository,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var trip = await repository.GetTripAsync(id, cancellationToken);
            if (trip is null) return Results.NotFound();
            try
            {
                trip.Abort(clock.UtcNow);
                await repository.SaveTripAsync(trip, cancellationToken);
                return Results.Ok(trip);
            }
            catch (InvalidOperationException exception)
            {
                return Results.UnprocessableEntity(new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Title = "Trip transition rejected",
                    Detail = exception.Message,
                    Status = StatusCodes.Status422UnprocessableEntity
                });
            }
        }).RequireAuthorization("FleetWrite");
    }

    private static Dictionary<string, string[]> ValidateRoute(CreateRouteRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name)) errors["name"] = ["Name is required."];
        if (request.Polyline is null || request.Polyline.Count < 2 || request.Polyline.Any(point => !point.IsValid))
            errors["polyline"] = ["At least two valid points are required."];
        if (request.Stops is null || request.Stops.Count == 0)
            errors["stops"] = ["At least one stop is required."];
        else if (request.Stops.Any(stop =>
                     string.IsNullOrWhiteSpace(stop.Name) ||
                     !new GeoPoint(stop.Latitude, stop.Longitude).IsValid ||
                     stop.RadiusKm <= 0 ||
                     stop.DwellMinutes < 0))
            errors["stops"] = ["Every stop needs a name, valid point, positive radius and non-negative dwell."];
        return errors;
    }

    private static Dictionary<string, string[]> ValidateGeofence(CreateGeofenceRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name)) errors["name"] = ["Name is required."];
        if (string.Equals(request.Shape, "circle", StringComparison.OrdinalIgnoreCase))
        {
            if (request.CenterLatitude is null || request.CenterLongitude is null ||
                !new GeoPoint(request.CenterLatitude ?? 999, request.CenterLongitude ?? 999).IsValid)
                errors["center"] = ["A valid centre is required for a circle."];
            if (request.RadiusKm is null or <= 0) errors["radiusKm"] = ["A positive radius is required."];
        }
        else if (string.Equals(request.Shape, "polygon", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Polygon is null || request.Polygon.Count < 3 || request.Polygon.Any(point => !point.IsValid))
                errors["polygon"] = ["At least three valid points are required."];
        }
        else
        {
            errors["shape"] = ["Shape must be circle or polygon."];
        }

        return errors;
    }
}
