namespace NotificationPlatform.UnitTests;

using NotificationPlatform.Application.Localization;
using Xunit;

public sealed class LocaleResolverTests
{
    [Fact]
    public void RegionSpecificFallsBackToLanguageThenTenantDefaultThenEn()
    {
        var r = new LocaleResolver();
        var chain = r.ResolveChain("sw-KE", tenantDefault: "en-KE");
        Assert.Equal(new[] { "sw-KE", "sw", "en-KE", "en" }, chain);
    }

    [Fact]
    public void UnknownLocaleFallsBackToTenantDefault()
    {
        var r = new LocaleResolver();
        var chain = r.ResolveChain("xx-YY", tenantDefault: "en-US");
        Assert.Contains("en-US", chain);
        Assert.Equal("en", chain[^1]);
    }

    [Fact]
    public void NoRequestedFallsBackToTenantDefault()
    {
        var r = new LocaleResolver();
        var chain = r.ResolveChain(null, tenantDefault: "fr-FR");
        Assert.Equal(new[] { "fr-FR", "fr", "en" }, chain);
    }

    [Fact]
    public void CultureInfoResolvesWhenValid()
    {
        var r = new LocaleResolver();
        Assert.Equal("en", r.GetCulture("en").TwoLetterISOLanguageName);
    }

    [Fact]
    public void CultureInfoFallsBackWhenInvalid()
    {
        var r = new LocaleResolver();
        // The exact fallback culture may differ by ICU; the important part is that no exception escapes.
        var culture = r.GetCulture("this-is-not-a-real-locale-string-!!");
        Assert.NotNull(culture);
    }
}
