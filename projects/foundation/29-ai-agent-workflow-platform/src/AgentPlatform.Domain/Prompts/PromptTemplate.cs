using System.Text;
using System.Text.RegularExpressions;

namespace AgentPlatform.Domain.Prompts;

/// <summary>Raised when a template references a variable that was not supplied, or vice-versa.</summary>
public sealed class PromptRenderException(string message) : Exception(message);

/// <summary>
/// A versioned prompt template. The body uses <c>{{variable}}</c> placeholders. Rendering is
/// strict: every declared variable must be supplied and every placeholder must be declared, so a
/// prompt can never silently render with a missing value. The exact version used is recorded on
/// every run for reproducibility and A/B evaluation.
/// </summary>
public sealed class PromptTemplate
{
    private static readonly Regex Placeholder = new(@"\{\{\s*(?<name>[a-zA-Z0-9_]+)\s*\}\}",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private PromptTemplate() { } // EF

    public PromptTemplate(string id, string name, int version, string body, string description = "")
    {
        Id = id;
        Name = name;
        Version = version;
        Body = body;
        Description = description;
        DeclaredVariables = ExtractVariables(body);
    }

    public string Id { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public int Version { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;

    /// <summary>Distinct variable names referenced by the body (persisted as CSV).</summary>
    public IReadOnlyList<string> DeclaredVariables { get; private set; } = Array.Empty<string>();

    public string Key => $"{Name}@v{Version}";

    public static IReadOnlyList<string> ExtractVariables(string body) =>
        Placeholder.Matches(body).Select(m => m.Groups["name"].Value).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Strictly render the template. Throws when a placeholder has no value; optionally throws when
    /// values are supplied that the template never uses (defends against silent prompt drift).
    /// </summary>
    public string Render(IReadOnlyDictionary<string, string> values, bool rejectExtraValues = true)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);

        var result = Placeholder.Replace(Body, match =>
        {
            var name = match.Groups["name"].Value;
            if (!values.TryGetValue(name, out var value))
                throw new PromptRenderException($"Prompt '{Key}' is missing a value for '{{{{{name}}}}}'.");
            used.Add(name);
            return value;
        });

        if (rejectExtraValues)
        {
            var extra = values.Keys.Where(k => !used.Contains(k)).ToArray();
            if (extra.Length > 0)
                throw new PromptRenderException(
                    $"Prompt '{Key}' was given unused values: {string.Join(", ", extra)}.");
        }

        return result;
    }
}
