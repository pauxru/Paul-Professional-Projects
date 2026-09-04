using System.Diagnostics;
using Contoso.Storefront.Api.Observability;
using Contoso.Storefront.Api.Security;
using Contoso.Storefront.Application.Orders;
using Contoso.Storefront.Application.Ports;
using Contoso.Storefront.Application.Release;

namespace Contoso.Storefront.Api.Endpoints;

public sealed record TokenRequest(string Subject, IReadOnlyList<string> Scopes);
public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);
public sealed record CreateOrderItemRequest(Guid ProductId, int Quantity);
public sealed record CreateOrderRequest(string CustomerReference, IReadOnlyList<CreateOrderItemRequest> Items);

public static class StorefrontEndpoints
{
    public static WebApplication MapStorefrontEndpoints(this WebApplication app)
    {
        app.MapAuthEndpoints();
        app.MapCatalogueEndpoints();
        app.MapOrderEndpoints();
        app.MapOperationalEndpoints();
        return app;
    }

    private static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Authentication");
        group.MapPost(
            "/token",
            (TokenRequest request, ITokenIssuer tokenIssuer, IHostEnvironment environment) =>
            {
                if (!environment.IsDevelopment() &&
                    !environment.IsEnvironment("Testing"))
                {
                    return Results.NotFound();
                }

                var errors = new Dictionary<string, string[]>();
                if (string.IsNullOrWhiteSpace(request.Subject))
                {
                    errors["subject"] = ["Subject is required."];
                }

                if (request.Scopes is null || request.Scopes.Count == 0)
                {
                    errors["scopes"] = ["At least one scope is required."];
                }

                var allowed = new[]
                {
                    StorefrontScopes.CatalogueRead,
                    StorefrontScopes.OrdersWrite
                };
                if (request.Scopes is not null &&
                    request.Scopes.Any(scope => !allowed.Contains(scope, StringComparer.Ordinal)))
                {
                    errors["scopes"] = ["One or more requested scopes are not allowed."];
                }

                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                const int expiresIn = 3_600;
                return Results.Ok(new TokenResponse(
                    tokenIssuer.Issue(
                        request.Subject,
                        request.Scopes!,
                        TimeSpan.FromSeconds(expiresIn)),
                    "Bearer",
                    expiresIn));
            })
            .AllowAnonymous();
    }

    private static void MapCatalogueEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/catalogue")
            .WithTags("Catalogue")
            .RequireAuthorization("CatalogueRead");

        group.MapGet(
            "/products",
            async (IStorefrontStore store, CancellationToken cancellationToken) =>
                Results.Ok(await store.ListProductsAsync(cancellationToken)));

        group.MapGet(
            "/products/{id:guid}",
            async (Guid id, IStorefrontStore store, CancellationToken cancellationToken) =>
            {
                var product = await store.FindProductAsync(id, cancellationToken);
                return product is null
                    ? Results.NotFound(ApiProblems.Create(404, "Product not found"))
                    : Results.Ok(product);
            });

        group.MapGet(
            "/products/{id:guid}/price-preview",
            async (
                Guid id,
                IStorefrontStore store,
                PricingPreviewService pricing,
                CancellationToken cancellationToken) =>
            {
                var product = await store.FindProductAsync(id, cancellationToken);
                return product is null
                    ? Results.NotFound(ApiProblems.Create(404, "Product not found"))
                    : Results.Ok(pricing.Preview(product));
            });
    }

    private static void MapOrderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/orders")
            .WithTags("Orders")
            .RequireAuthorization("OrdersWrite");

        group.MapPost(
            "/",
            async (
                CreateOrderRequest request,
                HttpContext context,
                OrderService service,
                CancellationToken cancellationToken) =>
            {
                var idempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
                var errors = ValidateOrder(request, idempotencyKey);
                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                try
                {
                    using var activity = StorefrontTelemetry.ActivitySource.StartActivity("order.create");
                    var result = await service.CreateAsync(
                        new CreateOrderCommand(
                            request.CustomerReference,
                            idempotencyKey!,
                            request.Items
                                .Select(item => new CreateOrderItem(item.ProductId, item.Quantity))
                                .ToList()),
                        cancellationToken);
                    activity?.SetTag("order.id", result.Order.Id);
                    activity?.SetTag("order.replayed", result.IsReplay);

                    return result.IsReplay
                        ? Results.Ok(result.Order)
                        : Results.Created($"/api/v1/orders/{result.Order.Id}", result.Order);
                }
                catch (ProductNotFoundException exception)
                {
                    return Results.UnprocessableEntity(
                        ApiProblems.Create(
                            422,
                            "Order could not be submitted",
                            $"Product {exception.ProductId} does not exist."));
                }
                catch (InvalidOperationException exception)
                {
                    return Results.UnprocessableEntity(
                        ApiProblems.Create(422, "Order could not be submitted", exception.Message));
                }
            });

        group.MapGet(
            "/{id:guid}",
            async (Guid id, IStorefrontStore store, CancellationToken cancellationToken) =>
            {
                var order = await store.FindOrderAsync(id, cancellationToken);
                return order is null
                    ? Results.NotFound(ApiProblems.Create(404, "Order not found"))
                    : Results.Ok(order);
            });
    }

    private static void MapOperationalEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/metrics",
            (StorefrontMetrics metrics) =>
                Results.Text(
                    metrics.RenderPrometheus(),
                    "text/plain; version=0.0.4"));

        app.MapGet(
            "/simulated/downstream",
            async (bool? fail, int? delayMs, CancellationToken cancellationToken) =>
            {
                var requestedDelay = delayMs.GetValueOrDefault();
                if (requestedDelay > 0)
                {
                    await Task.Delay(Math.Min(requestedDelay, 10_000), cancellationToken);
                }

                return fail.GetValueOrDefault()
                    ? Results.Problem(
                        title: "Simulated downstream failure",
                        statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Ok(new
                    {
                        status = "ok",
                        traceId = Activity.Current?.TraceId.ToString()
                    });
            })
            .AllowAnonymous();

        app.MapGet(
            "/api/v1/operations/downstream",
            async (
                IHttpClientFactory clients,
                CancellationToken cancellationToken) =>
            {
                var response = await clients
                    .CreateClient("simulated-downstream")
                    .GetAsync(string.Empty, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return Results.Content(
                    body,
                    response.Content.Headers.ContentType?.MediaType ?? "application/json",
                    statusCode: (int)response.StatusCode);
            })
            .RequireAuthorization("CatalogueRead")
            .WithTags("Operations");
    }

    private static Dictionary<string, string[]> ValidateOrder(
        CreateOrderRequest request,
        string? idempotencyKey)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.CustomerReference) ||
            request.CustomerReference.Length > 100)
        {
            errors["customerReference"] = ["Customer reference is required and cannot exceed 100 characters."];
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            errors["idempotencyKey"] = ["Idempotency-Key header is required and cannot exceed 100 characters."];
        }

        if (request.Items is null || request.Items.Count == 0)
        {
            errors["items"] = ["At least one item is required."];
        }
        else if (request.Items.Any(item => item.ProductId == Guid.Empty || item.Quantity is < 1 or > 100))
        {
            errors["items"] = ["Each item requires a product id and quantity between 1 and 100."];
        }

        return errors;
    }
}

internal static class ApiProblems
{
    public static object Create(int status, string title, string? detail = null) => new
    {
        type = $"https://httpstatuses.com/{status}",
        status,
        title,
        detail
    };
}
