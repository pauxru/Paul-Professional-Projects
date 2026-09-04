using Microsoft.EntityFrameworkCore;
using SavannaLogistics.Application;
using SavannaLogistics.Domain;

namespace SavannaLogistics.Api;

public sealed record CreateVehicleRequest(
    string Registration,
    string Make,
    string Model,
    double CapacityKg,
    double CapacityCubicMetres);

public sealed record CreateDriverRequest(string Name, string LicenceNumber, string PhoneAlias);
public sealed record AssignDriverRequest(Guid DriverId);
public sealed record UpdateVehicleStatusRequest(VehicleStatus Status);

public static class VehicleEndpoints
{
    public static IEndpointRouteBuilder MapVehicleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/vehicles").WithTags("Fleet");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            ILogisticsRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(await repository.GetVehiclesAsync(page ?? 1, pageSize ?? 50, cancellationToken)))
            .RequireAuthorization("FleetRead");

        group.MapGet("/states", async (
            ILogisticsRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(await repository.GetVehicleStatesAsync(cancellationToken)))
            .RequireAuthorization("FleetRead");

        group.MapGet("/{id:guid}", async (
            Guid id,
            ILogisticsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var vehicle = await repository.GetVehicleAsync(id, cancellationToken);
            return vehicle is null ? Results.NotFound() : Results.Ok(vehicle);
        }).RequireAuthorization("FleetRead");

        group.MapPost("/", async (
            CreateVehicleRequest request,
            ILogisticsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = Validate(request);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var vehicle = new Vehicle(
                Guid.NewGuid(),
                request.Registration,
                request.Make,
                request.Model,
                request.CapacityKg,
                request.CapacityCubicMetres);
            try
            {
                await repository.AddVehicleAsync(vehicle, cancellationToken);
                return Results.Created($"/api/v1/vehicles/{vehicle.Id}", vehicle);
            }
            catch (DbUpdateException)
            {
                return Results.Conflict(new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Title = "Vehicle registration conflict",
                    Detail = "A vehicle with the same registration already exists.",
                    Status = StatusCodes.Status409Conflict
                });
            }
        }).RequireAuthorization("FleetWrite");

        group.MapPost("/{id:guid}/assign", async (
            Guid id,
            AssignDriverRequest request,
            ILogisticsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var vehicle = await repository.GetVehicleAsync(id, cancellationToken);
            if (vehicle is null) return Results.NotFound();
            var driver = await repository.GetDriverAsync(request.DriverId, cancellationToken);
            if (driver is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["driverId"] = ["Driver does not exist."]
                });
            }

            vehicle.AssignDriver(driver.Id);
            await repository.SaveVehicleAsync(vehicle, cancellationToken);
            return Results.Ok(vehicle);
        }).RequireAuthorization("FleetWrite");

        group.MapPut("/{id:guid}/status", async (
            Guid id,
            UpdateVehicleStatusRequest request,
            ILogisticsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var vehicle = await repository.GetVehicleAsync(id, cancellationToken);
            if (vehicle is null) return Results.NotFound();
            vehicle.SetStatus(request.Status);
            await repository.SaveVehicleAsync(vehicle, cancellationToken);
            return Results.Ok(vehicle);
        }).RequireAuthorization("FleetWrite");

        var drivers = group.MapGroup("/drivers");
        drivers.MapGet("/", async (
            int? page,
            int? pageSize,
            ILogisticsRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(await repository.GetDriversAsync(page ?? 1, pageSize ?? 50, cancellationToken)))
            .RequireAuthorization("FleetRead");
        drivers.MapPost("/", async (
            CreateDriverRequest request,
            ILogisticsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Name)) errors["name"] = ["Name is required."];
            if (string.IsNullOrWhiteSpace(request.LicenceNumber)) errors["licenceNumber"] = ["Licence number is required."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var driver = new Driver(Guid.NewGuid(), request.Name, request.LicenceNumber, request.PhoneAlias ?? string.Empty);
            await repository.AddDriverAsync(driver, cancellationToken);
            return Results.Created($"/api/v1/vehicles/drivers/{driver.Id}", driver);
        }).RequireAuthorization("FleetWrite");

        return endpoints;
    }

    private static Dictionary<string, string[]> Validate(CreateVehicleRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Registration)) errors["registration"] = ["Registration is required."];
        if (string.IsNullOrWhiteSpace(request.Make)) errors["make"] = ["Make is required."];
        if (string.IsNullOrWhiteSpace(request.Model)) errors["model"] = ["Model is required."];
        if (request.CapacityKg <= 0) errors["capacityKg"] = ["Capacity must be greater than zero."];
        if (request.CapacityCubicMetres <= 0) errors["capacityCubicMetres"] = ["Capacity must be greater than zero."];
        return errors;
    }
}
