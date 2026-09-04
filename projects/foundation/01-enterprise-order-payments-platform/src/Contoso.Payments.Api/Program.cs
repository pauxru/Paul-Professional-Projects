using Microsoft.EntityFrameworkCore;

using Contoso.Payments.Api.Endpoints;
using Contoso.Payments.Api.Middleware;
using Contoso.Payments.Api.Startup;
using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Domain.Common;
using Contoso.Payments.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddPaymentsPlatform();
builder.AddPaymentsAuth();
builder.AddPaymentsTelemetry();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

var app = builder.Build();

// --------- request pipeline ---------
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
// Domain / db-conflict translation — must come BEFORE the endpoint mappings so it wraps
// endpoint execution (Use middleware runs on the request path before terminal handlers).
app.Use(async (ctx, next) =>
{
    try { await next(ctx); }
    catch (DomainException dex)
    {
        ctx.Response.StatusCode = 422;
        ctx.Response.ContentType = "application/problem+json";
        var problem = new
        {
            type = $"https://contoso-payments.local/errors/{dex.Code}",
            title = dex.Code,
            status = 422,
            detail = dex.Message,
            traceId = ctx.CorrelationId()
        };
        await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(problem));
    }
    catch (Microsoft.EntityFrameworkCore.DbUpdateException)
    {
        ctx.Response.StatusCode = 409;
        ctx.Response.ContentType = "application/problem+json";
        await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "https://contoso-payments.local/errors/db.conflict",
            title = "db.conflict",
            status = 409,
            detail = "Database conflict — retry.",
            traceId = ctx.CorrelationId()
        }));
    }
    catch (Microsoft.Data.Sqlite.SqliteException)
    {
        ctx.Response.StatusCode = 409;
        ctx.Response.ContentType = "application/problem+json";
        await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "https://contoso-payments.local/errors/db.busy",
            title = "db.busy",
            status = 409,
            detail = "Database busy — retry.",
            traceId = ctx.CorrelationId()
        }));
    }
});
app.UseMiddleware<IdempotencyMiddleware>();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/v1.json");
}
else
{
    app.MapOpenApi("/openapi/v1.json");
}

app.MapAuthEndpoints();
app.MapProductEndpoints();
app.MapOrderEndpoints();
app.MapPaymentEndpoints();
app.MapRefundEndpoints();
app.MapWebhookEndpoints();
app.MapReconciliationEndpoints();
app.MapAdminOutboxEndpoints();

// --------- startup migrations + seed ---------
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    if (app.Environment.IsDevelopment())
    {
        var ids = scope.ServiceProvider.GetRequiredService<IIdGenerator>();
        await DevSeeder.SeedAsync(db, ids, CancellationToken.None);
    }
}

app.Run();

public partial class Program;
