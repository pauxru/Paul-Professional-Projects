using System.Security.Claims;
using FieldOps.Api;
using Microsoft.AspNetCore.Http;

namespace FieldOps.IntegrationTests;

public sealed class TenantStrategyTests
{
    [Fact]
    public async Task JwtStrategy_ReadsTenantClaim()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tenant_id", "savanna-logistics")],
                "test"))
        };
        Assert.Equal(
            "savanna-logistics",
            await new JwtTenantResolutionStrategy().ResolveAsync(context));
    }

    [Fact]
    public async Task HeaderStrategy_ReadsConfiguredHeader()
    {
        var options = new TenantResolutionOptions { HeaderName = "X-Tenant" };
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant"] = "jua-kali-manufacturing";
        Assert.Equal(
            "jua-kali-manufacturing",
            await new HeaderTenantResolutionStrategy(options).ResolveAsync(context));
    }

    [Fact]
    public async Task SubdomainStrategy_ExtractsSingleTenantLabel()
    {
        var options = new TenantResolutionOptions { BaseDomain = "fieldops.local" };
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("acme-manufacturing.fieldops.local");
        Assert.Equal(
            "acme-manufacturing",
            await new SubdomainTenantResolutionStrategy(options).ResolveAsync(context));
    }

    [Fact]
    public async Task SubdomainStrategy_RejectsNestedSubdomain()
    {
        var options = new TenantResolutionOptions { BaseDomain = "fieldops.local" };
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("nested.acme.fieldops.local");
        Assert.Null(await new SubdomainTenantResolutionStrategy(options).ResolveAsync(context));
    }
}
