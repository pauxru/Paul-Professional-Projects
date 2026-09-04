namespace LoadRunner.Core.Scenarios;

/// <summary>
/// A simple mustache-style variable substitution engine used for URL, header and body
/// templating in scenario steps. Recognises <c>{{name}}</c> tokens; unknown tokens are
/// left untouched but reported for debugging.
/// </summary>
public static class Templating
{
    public static string Render(string template, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(template) || variables.Count == 0) return template;
        var sb = new System.Text.StringBuilder(template.Length + 32);
        var i = 0;
        while (i < template.Length)
        {
            if (i + 1 < template.Length && template[i] == '{' && template[i + 1] == '{')
            {
                var end = template.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    var key = template.Substring(i + 2, end - (i + 2)).Trim();
                    if (variables.TryGetValue(key, out var value)) sb.Append(value);
                    else sb.Append("{{").Append(key).Append("}}");
                    i = end + 2;
                    continue;
                }
            }
            sb.Append(template[i]);
            i++;
        }
        return sb.ToString();
    }
}
