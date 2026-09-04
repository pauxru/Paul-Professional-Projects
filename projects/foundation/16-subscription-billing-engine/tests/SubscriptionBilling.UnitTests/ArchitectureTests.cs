using SubscriptionBilling.Application;
using SubscriptionBilling.Domain;

namespace SubscriptionBilling.UnitTests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Domain_DoesNotReferenceApplicationInfrastructureOrAspNet()
    {
        var references = typeof(Money).Assembly.GetReferencedAssemblies()
            .Select(item => item.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, name => name.StartsWith("SubscriptionBilling.", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_ReferencesDomainButNotInfrastructureOrAspNet()
    {
        var references = typeof(IBillingEngine).Assembly.GetReferencedAssemblies()
            .Select(item => item.Name ?? string.Empty)
            .ToArray();

        Assert.Contains("SubscriptionBilling.Domain", references);
        Assert.DoesNotContain("SubscriptionBilling.Infrastructure", references);
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }
}
