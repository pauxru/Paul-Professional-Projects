using System.Diagnostics;
using Lab.Application.Contracts;
using Lab.Application.Services;
using Lab.Diagnostics.Measurement;

namespace Lab.SampleApp.Endpoints;

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/orders")
            .WithTags("Orders")
            .RequireRateLimiting("northstar-api");

        group.MapGet("", async (int? page, int? pageSize, OrderService service, CancellationToken cancellationToken) =>
            {
                var stopwatch = Stopwatch.StartNew();
                using var activity = LabTelemetry.ActivitySource.StartActivity("orders.list");
                var normalizedPage = Math.Max(page ?? 1, 1);
                var normalizedPageSize = Math.Clamp(pageSize ?? 20, 1, 100);
                var result = await service.ListAsync(normalizedPage, normalizedPageSize, cancellationToken);
                stopwatch.Stop();
                LabTelemetry.RequestLatencyMilliseconds.Record(stopwatch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("operation", "orders.list"));
                return Results.Ok(result);
            })
            .RequireAuthorization("orders.read")
            .WithName("ListOrders");

        group.MapGet("/{id:guid}", async (Guid id, OrderService service, CancellationToken cancellationToken) =>
            {
                using var activity = LabTelemetry.ActivitySource.StartActivity("orders.get");
                var result = await service.GetAsync(id, cancellationToken);
                return result is null
                    ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Order not found")
                    : Results.Ok(result);
            })
            .RequireAuthorization("orders.read")
            .WithName("GetOrder");

        group.MapPost("", async (CreateOrderRequest request, OrderService service, CancellationToken cancellationToken) =>
            {
                var errors = Validation.Validate(request);
                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                var stopwatch = Stopwatch.StartNew();
                using var activity = LabTelemetry.ActivitySource.StartActivity("orders.book");
                try
                {
                    var created = await service.BookAsync(request, cancellationToken);
                    LabTelemetry.OrdersBooked.Add(1);
                    stopwatch.Stop();
                    LabTelemetry.RequestLatencyMilliseconds.Record(stopwatch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("operation", "orders.book"));
                    return Results.Created($"/api/v1/orders/{created.Id}", created);
                }
                catch (ArgumentException exception)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["request"] = [exception.Message]
                    });
                }
            })
            .RequireAuthorization("orders.write")
            .WithName("CreateOrder");

        return endpoints;
    }
}
