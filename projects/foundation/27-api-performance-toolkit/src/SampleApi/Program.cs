using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Endpoints;
using SampleApi.Pathologies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<PathologyState>();

var connString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=sampleapi.db;Cache=Shared;Pooling=True";
builder.Services.AddDbContext<CatalogDbContext>(o => o.UseSqlite(connString));

var app = builder.Build();

app.MapOpenApi();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    if (!app.Environment.IsEnvironment("Testing"))
    {
        await Seed.EnsureAsync(db);
    }
}

app.MapGet("/", () => Results.Ok(new { name = "SampleApi", description = "LoadRunner sample target" }));
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (CatalogDbContext db) =>
{
    var canRead = await db.Products.AnyAsync();
    return Results.Ok(new { status = "ready", canRead });
});
app.MapCatalog();
app.MapOrders();
app.MapPathology();

app.Run();

public partial class Program;

