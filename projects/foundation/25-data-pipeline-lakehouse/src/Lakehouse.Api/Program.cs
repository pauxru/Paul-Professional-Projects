using System.Text;
using Lakehouse.Api;
using Lakehouse.Api.Auth;
using Lakehouse.Api.Configuration;
using Lakehouse.Api.Endpoints;
using Lakehouse.Api.Seed;
using Lakehouse.Application.Observability;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, _, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    builder.Services.AddLakehouse(builder.Configuration, builder.Environment.ContentRootPath);
    builder.Services.AddSingleton<DevTokenService>();

    var auth = builder.Configuration.GetSection("Lakehouse:Auth").Get<AuthSettings>() ?? new AuthSettings();
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = auth.Issuer,
                ValidateAudience = true,
                ValidAudience = auth.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.SigningKey)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
        });

    builder.Services.AddAuthorizationBuilder()
        .AddPolicy(AuthPolicies.Reader, p => p.RequireAuthenticatedUser().RequireRole(AuthPolicies.ReaderRole))
        .AddPolicy(AuthPolicies.Operator, p => p.RequireAuthenticatedUser().RequireRole(AuthPolicies.OperatorRole));

    builder.Services.AddOpenApi();
    builder.Services.AddHealthChecks().AddCheck<LakehouseHealthCheck>("lakehouse");

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("Lakehouse.Api"))
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation()
            .AddSource(Telemetry.SourceName)
            .AddConsoleExporter());

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
        app.MapOpenApi();

    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/api/info", (LakehouseOptions opt) => Results.Ok(new
    {
        name = "Contoso Retail Lakehouse",
        description = "Medallion lakehouse (bronze -> silver -> gold) with a custom table format, data-quality gates, lineage and a guarded SQL serving layer.",
        port = 5025,
        lakeRoot = opt.LakeRoot,
        docs = "/openapi/v1.json"
    })).AllowAnonymous().WithTags("Info");

    app.MapAuthEndpoints();
    app.MapPipelineEndpoints();
    app.MapQualityEndpoints();
    app.MapLineageEndpoints();
    app.MapMetricsEndpoints();
    app.MapServingEndpoints();
    app.MapObservabilityEndpoints();
    app.MapDashboardEndpoints();

    app.MapHealthChecks("/health").AllowAnonymous();

    var options = app.Services.GetRequiredService<LakehouseOptions>();
    if (options.SeedOnStartup)
    {
        using var scope = app.Services.CreateScope();
        try
        {
            scope.ServiceProvider.GetRequiredService<Seeder>().EnsureSeeded();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Startup seed failed; API will start with whatever data exists");
        }
    }

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Exposed so integration tests can spin up the real host via WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
