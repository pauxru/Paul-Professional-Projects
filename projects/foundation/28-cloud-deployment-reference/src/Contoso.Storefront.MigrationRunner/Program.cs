using Contoso.Storefront.Application.Configuration;
using Contoso.Storefront.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddStorefrontDatabase();

using var host = builder.Build();
await using var scope = host.Services.CreateAsyncScope();
var database = scope.ServiceProvider.GetRequiredService<StorefrontDbContext>();
var pendingBefore = (await database.Database.GetPendingMigrationsAsync()).ToArray();

Console.WriteLine(
    pendingBefore.Length == 0
        ? "Database is already current; no migrations were applied."
        : $"Applying {pendingBefore.Length} migration(s): {string.Join(", ", pendingBefore)}");

await database.Database.MigrateAsync();

var pendingAfter = (await database.Database.GetPendingMigrationsAsync()).ToArray();
if (pendingAfter.Length > 0)
{
    Console.Error.WriteLine(
        $"Migration runner completed with {pendingAfter.Length} pending migration(s).");
    return 1;
}

Console.WriteLine("Migration runner completed successfully; database is current.");
return 0;
