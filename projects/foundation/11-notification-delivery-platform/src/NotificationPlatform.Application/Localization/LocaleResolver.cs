namespace NotificationPlatform.Application.Localization;

using System.Globalization;

public interface ILocaleResolver
{
    /// <summary>
    /// Returns an ordered list of locale candidates for lookup.
    /// Example: given "sw-KE" returns ["sw-KE", "sw", "en-KE" (if tenantDefault contains region), tenantDefault, "en"].
    /// </summary>
    IReadOnlyList<string> ResolveChain(string? requested, string tenantDefault);

    CultureInfo GetCulture(string locale);
}

public sealed class LocaleResolver : ILocaleResolver
{
    public IReadOnlyList<string> ResolveChain(string? requested, string tenantDefault)
    {
        var chain = new List<string>();
        void Add(string? x)
        {
            if (string.IsNullOrWhiteSpace(x)) return;
            if (!chain.Any(c => c.Equals(x, StringComparison.OrdinalIgnoreCase)))
                chain.Add(x);
        }

        if (!string.IsNullOrWhiteSpace(requested))
        {
            Add(requested);
            var dash = requested.IndexOf('-');
            if (dash > 0) Add(requested[..dash]);
        }

        Add(tenantDefault);
        if (!string.IsNullOrWhiteSpace(tenantDefault))
        {
            var d = tenantDefault.IndexOf('-');
            if (d > 0) Add(tenantDefault[..d]);
        }

        Add("en");
        return chain;
    }

    public CultureInfo GetCulture(string locale)
    {
        try
        {
            return CultureInfo.GetCultureInfo(locale);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo("en");
        }
    }
}
