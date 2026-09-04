using SubscriptionBilling.Application;

namespace SubscriptionBilling.Api;

public static class CatalogueEndpoints
{
    public static IEndpointRouteBuilder MapCatalogueEndpoints(this IEndpointRouteBuilder app)
    {
        var products = app.MapGroup("/api/v1/products").WithTags("Products");
        products.MapGet("/", (
            int? page,
            int? pageSize,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
            engine.ListProductsAsync(DefaultPage(page), DefaultPageSize(pageSize), cancellationToken))
            .RequireAuthorization("billing.read");
        products.MapPost("/", async (
            CreateProductCommand command,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
        {
            ValidateRequired(("name", command.Name));
            var created = await engine.CreateProductAsync(command, cancellationToken);
            return Results.Created($"/api/v1/products/{created.Id}", created);
        }).RequireAuthorization("billing.write");

        var plans = app.MapGroup("/api/v1/plans").WithTags("Plans");
        plans.MapGet("/", (
            int? page,
            int? pageSize,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
            engine.ListPlansAsync(DefaultPage(page), DefaultPageSize(pageSize), cancellationToken))
            .RequireAuthorization("billing.read");
        plans.MapPost("/", async (
            CreatePlanCommand command,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
        {
            ValidateRequired(("name", command.Name), ("currency", command.Currency));
            var created = await engine.CreatePlanAsync(command, cancellationToken);
            return Results.Created($"/api/v1/plans/{created.Id}", created);
        }).RequireAuthorization("billing.write");
        plans.MapPost("/{planId:guid}/versions", async (
            Guid planId,
            AddPlanVersionCommand command,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
        {
            ValidateRequired(("currency", command.Currency));
            var created = await engine.AddPlanVersionAsync(planId, command, cancellationToken);
            return Results.Created($"/api/v1/plans/{planId}/versions/{created.Version}", created);
        }).RequireAuthorization("billing.write");
        plans.MapGet("/meters", (
            int? page,
            int? pageSize,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
            engine.ListMetersAsync(DefaultPage(page), DefaultPageSize(pageSize), cancellationToken))
            .RequireAuthorization("billing.read");
        plans.MapPost("/meters", async (
            CreateMeterCommand command,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
        {
            ValidateRequired(("name", command.Name), ("unit", command.Unit));
            var created = await engine.CreateMeterAsync(command, cancellationToken);
            return Results.Created($"/api/v1/plans/meters/{created.Id}", created);
        }).RequireAuthorization("billing.write");

        var customers = app.MapGroup("/api/v1/customers").WithTags("Customers");
        customers.MapGet("/", (
            int? page,
            int? pageSize,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
            engine.ListCustomersAsync(DefaultPage(page), DefaultPageSize(pageSize), cancellationToken))
            .RequireAuthorization("billing.read");
        customers.MapPost("/", async (
            CreateCustomerCommand command,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
        {
            ValidateRequired(
                ("name", command.Name),
                ("currency", command.Currency),
                ("countryCode", command.CountryCode));
            var created = await engine.CreateCustomerAsync(command, cancellationToken);
            return Results.Created($"/api/v1/customers/{created.Id}", created);
        }).RequireAuthorization("billing.write");

        var coupons = app.MapGroup("/api/v1/coupons").WithTags("Coupons");
        coupons.MapGet("/", (
            int? page,
            int? pageSize,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
            engine.ListCouponsAsync(DefaultPage(page), DefaultPageSize(pageSize), cancellationToken))
            .RequireAuthorization("billing.read");
        coupons.MapPost("/", async (
            CreateCouponCommand command,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
        {
            ValidateRequired(("code", command.Code));
            var created = await engine.CreateCouponAsync(command, cancellationToken);
            return Results.Created($"/api/v1/coupons/{created.Id}", created);
        }).RequireAuthorization("billing.write");

        var credits = app.MapGroup("/api/v1/credits").WithTags("Credits");
        credits.MapPost("/", async (
            AddCreditCommand command,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
        {
            ValidateRequired(("currency", command.Currency), ("reason", command.Reason));
            var created = await engine.AddCreditAsync(command, cancellationToken);
            return Results.Created($"/api/v1/credits/{created.Id}", created);
        }).RequireAuthorization("billing.write");

        return app;
    }

    internal static void ValidateRequired(params (string Name, string? Value)[] values)
    {
        var errors = values
            .Where(item => string.IsNullOrWhiteSpace(item.Value))
            .ToDictionary(
                item => item.Name,
                _ => new[] { "The field is required." },
                StringComparer.Ordinal);
        if (errors.Count > 0)
        {
            throw new RequestValidationException("One or more fields are invalid.", errors);
        }
    }

    private static int DefaultPage(int? page) => page is null or <= 0 ? 1 : page.Value;
    private static int DefaultPageSize(int? pageSize) =>
        pageSize is null or <= 0 ? 20 : pageSize.Value;
}
