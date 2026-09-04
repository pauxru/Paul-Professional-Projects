using Microsoft.EntityFrameworkCore;
using Northstar.Application.Abstractions;
using Northstar.Domain.Claims;
using Northstar.Domain.Policies;

namespace Northstar.Infrastructure.Persistence;

public static class DemoDataSeeder
{
    public static async Task SeedAsync(NorthstarDbContext dbContext, IClock clock, CancellationToken cancellationToken)
    {
        if (await dbContext.Policies.AnyAsync(cancellationToken))
        {
            return;
        }

        var acme = new Policyholder(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Acme Manufacturing (fictional)", "claims@acme.example");
        var contoso = new Policyholder(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Contoso Retail (fictional)", "claims@contoso.example");
        var acmePolicy = new Policy(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), acme.Id, "POL-ACME-001", 500m, 10_000m, "USD");
        var contosoPolicy = new Policy(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), contoso.Id, "POL-CONTOSO-001", 250m, 5_000m, "USD");
        var claim = Claim.Create(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), acmePolicy.Id, "CLM-MODERN-1001", 3_400m, "USD", clock.UtcNow);
        claim.SetReserve(2_900m);

        dbContext.AddRange(acme, contoso, acmePolicy, contosoPolicy, claim);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
