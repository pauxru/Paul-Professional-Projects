namespace Northstar.Legacy.Web.Legacy;

public static class LegacyServiceLocator
{
    // LEGACY-SMELL: construction is hidden behind a global locator rather than an injected dependency.
    public static LegacyReferenceGenerator GetReferenceGenerator() => new();
}

public sealed class LegacyReferenceGenerator
{
    public string Generate() => $"CLM-{DateTime.Now:yyyyMMddHHmmss}";
}
