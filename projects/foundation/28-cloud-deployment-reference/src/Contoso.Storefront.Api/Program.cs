using System.Threading.RateLimiting;
using Contoso.Storefront.Api.Configuration;
using Contoso.Storefront.Api.Endpoints;
using Contoso.Storefront.Api.Health;
using Contoso.Storefront.Api.Hosting;
using Contoso.Storefront.Api.Middleware;
using Contoso.Storefront.Api.Observability;
using Contoso.Storefront.Api.Security;
using Contoso.Storefront.Application.Configuration;
using Contoso.Storefront.Application.Orders;
using Contoso.Storefront.Application.Release;
using Contoso.Storefront.Infrastructure;
using Contoso.Storefront.Infrastructure.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
ConfigurationSetup.ConfigureSources(builder, args);

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
});
builder.Services.AddOpenApi();
builder.Services.AddValidatedStorefrontOptions(builder.Configuration);
builder.Services.AddStorefrontInfrastructure();
builder.Services.AddStorefrontAuthentication(builder.Configuration);
builder.Services.AddStorefrontHealthChecks();
builder.Services.AddStorefrontObservability(builder);

builder.Services.AddScoped<OrderService>();
builder.Services.AddSingleton<PricingPreviewService>();
builder.Services.AddSingleton<StartupState>();
builder.Services.AddSingleton<RequestDrainTracker>();

builder.Services.AddHttpClient(
        "simulated-downstream",
        (serviceProvider, client) =>
        {
            var downstream = serviceProvider.GetRequiredService<IOptions<DownstreamOptions>>().Value;
            client.BaseAddress = new Uri(downstream.BaseUrl);
        })
    .AddHttpMessageHandler<CorrelationPropagationHandler>()
    .AddHttpMessageHandler<ResilientHttpMessageHandler>();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "1";
        return ValueTask.CompletedTask;
    };
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.Identity?.Name ??
            httpContext.Connection.RemoteIpAddress?.ToString() ??
            "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

builder.Services.AddHostedService<StartupDependencyWaitService>();
builder.Services.AddHostedService<DevelopmentDataSeeder>();
builder.Services.AddHostedService<OutboxBackgroundWorker>();
builder.Services.AddHostedService<LifecycleLoggingService>();
builder.Services.AddHostedService<GracefulShutdownService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<ConnectionDrainingMiddleware>();
app.UseMiddleware<RequestMetricsMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapStorefrontHealthChecks();
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi("/openapi/v1.json");
    app.MapGet(
        "/docs",
        () => Results.Text(
            "OpenAPI document: /openapi/v1.json",
            "text/plain"));
}

app.MapStorefrontEndpoints();
app.Run();

public partial class Program;
