namespace LoadRunner.Core.Scenarios;

/// <summary>
/// Extracts values from an HTTP response body (JSON path) or response headers, so that
/// subsequent steps of the same virtual user can reference them via <c>{{variable}}</c>.
/// Only trivial JSON paths are supported: <c>root.field</c> or <c>field[0].id</c>.
/// This is intentional — a real scenario language would want a full JMESPath, but for
/// portfolio purposes the deterministic minimalism is a feature.
/// </summary>
public static class ValueExtractor
{
    public static string? ExtractJson(string json, string path)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var element = doc.RootElement;
            foreach (var raw in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var segment = raw;
                var idx = -1;
                var bracket = segment.IndexOf('[');
                if (bracket >= 0)
                {
                    var end = segment.IndexOf(']', bracket);
                    if (end < 0) return null;
                    idx = int.Parse(segment.Substring(bracket + 1, end - bracket - 1));
                    segment = segment.Substring(0, bracket);
                }
                if (segment.Length > 0)
                {
                    if (element.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                    if (!element.TryGetProperty(segment, out var next)) return null;
                    element = next;
                }
                if (idx >= 0)
                {
                    if (element.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
                    if (idx >= element.GetArrayLength()) return null;
                    element = element[idx];
                }
            }
            return element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => element.GetString(),
                System.Text.Json.JsonValueKind.Number => element.GetRawText(),
                System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => element.GetBoolean().ToString().ToLowerInvariant(),
                System.Text.Json.JsonValueKind.Null => null,
                _ => element.GetRawText()
            };
        }
        catch { return null; }
    }

    public static string? ExtractHeader(System.Net.Http.Headers.HttpResponseHeaders headers, string name)
    {
        if (headers.TryGetValues(name, out var values)) return string.Join(",", values);
        return null;
    }

    public static string? ExtractContentHeader(System.Net.Http.Headers.HttpContentHeaders headers, string name)
    {
        if (headers.TryGetValues(name, out var values)) return string.Join(",", values);
        return null;
    }
}
