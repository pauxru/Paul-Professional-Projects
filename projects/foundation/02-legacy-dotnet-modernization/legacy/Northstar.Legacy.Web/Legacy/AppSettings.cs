using System.Xml.Linq;

namespace Northstar.Legacy.Web.Legacy;

/// <summary>ConfigurationManager-style global configuration retained solely for the legacy simulation.</summary>
public static class AppSettings
{
    private static readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static bool _loaded;

    public static string Get(string key)
    {
        EnsureLoaded();
        return Values.TryGetValue(key, out var value)
            ? value
            : throw new InvalidOperationException($"Missing legacy appSetting '{key}'.");
    }

    public static void LoadFrom(string path)
    {
        lock (Gate)
        {
            Values.Clear();
            var document = XDocument.Load(path);
            foreach (var entry in document.Root?.Elements("add") ?? [])
            {
                var key = entry.Attribute("key")?.Value;
                var value = entry.Attribute("value")?.Value;
                if (!string.IsNullOrWhiteSpace(key) && value is not null)
                {
                    Values[key] = value;
                }
            }

            _loaded = true;
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (Gate)
        {
            if (!_loaded)
            {
                LoadFrom(Path.Combine(AppContext.BaseDirectory, "AppSettings.xml"));
            }
        }
    }
}
