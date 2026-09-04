using FeatureFlags.Domain;
using FeatureFlags.Sdk;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5019");
builder.Services.AddFeatureFlags(builder.Configuration);
var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { service = "Demo consumer", message = "Try /checkout, /beta and /purchase with X-Feature-Context." }));
app.MapGet("/checkout", (HttpContext context, IFeatureFlagClient flags) =>
{
    var evaluationContext = new EvaluationContext(ContextKey(context));
    var detail = flags.BoolVariationDetail("new-checkout", evaluationContext, false);
    return Results.Ok(new { checkout = detail.Value ? "new" : "classic", detail.VariationIndex, detail.Reason });
});
app.MapGet("/beta", () => Results.Ok(new { feature = "beta", message = "The endpoint is locally gated by new-checkout." }))
    .RequireFeatureFlag("new-checkout");
app.MapGet("/purchase", (HttpContext context, IFeatureFlagClient flags) =>
{
    if (flags.BoolVariation("maintenance-mode", new EvaluationContext(ContextKey(context)), false))
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Purchases paused", detail: "The maintenance kill switch is active.");
    }
    return Results.Ok(new { accepted = true, message = "Purchase path is open." });
});
app.Run();

static string ContextKey(HttpContext context) => context.Request.Headers["X-Feature-Context"].FirstOrDefault() ?? "demo-user";
