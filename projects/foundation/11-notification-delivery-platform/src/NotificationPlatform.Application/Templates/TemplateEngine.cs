namespace NotificationPlatform.Application.Templates;

using System.Text;
using System.Text.Json;

public sealed record TemplateRenderResult(string Body, IReadOnlyList<string> UnknownTokens);

public sealed class TemplateRenderException : Exception
{
    public TemplateRenderException(string message) : base(message) { }
}

/// <summary>
/// Small, safe template engine.
/// Supports:
///   {{ path.to.value }}                — token substitution, HTML-escaped by default
///   {{ raw:path.to.value }}            — explicit raw (unescaped) marker
///   {{#if path.to.flag}}...{{/if}}     — simple conditionals (truthy check)
///   {{#each path.to.list}}...{{/each}} — simple loops; inside, "." refers to current item
/// Strict mode: unknown tokens throw TemplateRenderException.
/// Non-strict mode: unknown tokens are recorded and rendered as empty strings.
/// </summary>
public sealed class TemplateEngine : ITemplateEngine
{
    public TemplateRenderResult Render(string template, JsonElement data, bool strict)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));

        var unknownTokens = new List<string>();
        var sb = new StringBuilder(template.Length);
        RenderRange(template.AsSpan(), data, sb, unknownTokens, strict);
        return new TemplateRenderResult(sb.ToString(), unknownTokens);
    }

    public bool ValidateSyntax(string template, out string? error)
    {
        try
        {
            using var doc = JsonDocument.Parse("{}");
            _ = Render(template, doc.RootElement, strict: false);
            error = null;
            return true;
        }
        catch (TemplateRenderException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void RenderRange(ReadOnlySpan<char> src, JsonElement data, StringBuilder sb, List<string> unknownTokens, bool strict)
    {
        int i = 0;
        while (i < src.Length)
        {
            int start = src[i..].IndexOf("{{", StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(src[i..]);
                return;
            }
            sb.Append(src.Slice(i, start));
            int absoluteStart = i + start;
            int end = src[(absoluteStart + 2)..].IndexOf("}}", StringComparison.Ordinal);
            if (end < 0)
                throw new TemplateRenderException("Unterminated '{{' expression.");
            int tagEnd = absoluteStart + 2 + end;
            var inner = src.Slice(absoluteStart + 2, end).Trim();
            if (inner.Length == 0)
                throw new TemplateRenderException("Empty template expression.");

            if (inner[0] == '#')
            {
                // Block: parse to closing tag
                var blockHeader = inner[1..].ToString();
                var (name, expr) = SplitFirst(blockHeader);
                var closing = "{{/" + name + "}}";
                var afterOpen = tagEnd + 2;
                int closeStart = FindMatchingClose(src, afterOpen, name);
                if (closeStart < 0)
                    throw new TemplateRenderException($"Missing closing tag for block '{name}'.");
                var inner2 = src.Slice(afterOpen, closeStart - afterOpen);
                int afterClose = closeStart + closing.Length;

                if (name == "if")
                {
                    if (IsTruthy(Resolve(expr, data, out _)))
                        RenderRange(inner2, data, sb, unknownTokens, strict);
                }
                else if (name == "each")
                {
                    var arr = Resolve(expr, data, out var found);
                    if (found && arr.HasValue && arr.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in arr.Value.EnumerateArray())
                            RenderRange(inner2, item, sb, unknownTokens, strict);
                    }
                    else if (strict && !found)
                    {
                        throw new TemplateRenderException($"Unknown token '{expr}'.");
                    }
                    else if (!found)
                    {
                        unknownTokens.Add(expr);
                    }
                }
                else
                {
                    throw new TemplateRenderException($"Unknown block '{name}'.");
                }
                i = afterClose;
                continue;
            }

            // Substitution
            var text = inner.ToString();
            bool raw = false;
            if (text.StartsWith("raw:", StringComparison.OrdinalIgnoreCase))
            {
                raw = true;
                text = text[4..].Trim();
            }

            // "." within loops refers to current item.
            var value = Resolve(text, data, out var wasFound);
            if (!wasFound)
            {
                if (strict) throw new TemplateRenderException($"Unknown token '{text}'.");
                unknownTokens.Add(text);
                i = tagEnd + 2;
                continue;
            }

            var rendered = value.HasValue ? ValueToString(value.Value) : string.Empty;
            sb.Append(raw ? rendered : HtmlEscape(rendered));
            i = tagEnd + 2;
        }
    }

    private static int FindMatchingClose(ReadOnlySpan<char> src, int startFrom, string name)
    {
        int depth = 1;
        int idx = startFrom;
        var openTag = "{{#" + name;
        var closeTag = "{{/" + name + "}}";
        while (idx < src.Length)
        {
            int openAt = src[idx..].IndexOf(openTag, StringComparison.Ordinal);
            int closeAt = src[idx..].IndexOf(closeTag, StringComparison.Ordinal);
            if (closeAt < 0) return -1;
            int absClose = idx + closeAt;
            if (openAt >= 0 && openAt < closeAt)
            {
                depth++;
                idx = idx + openAt + openTag.Length;
            }
            else
            {
                depth--;
                if (depth == 0) return absClose;
                idx = absClose + closeTag.Length;
            }
        }
        return -1;
    }

    private static (string Name, string Expr) SplitFirst(string s)
    {
        var trimmed = s.Trim();
        int sp = trimmed.IndexOf(' ');
        if (sp < 0) return (trimmed, string.Empty);
        return (trimmed[..sp], trimmed[(sp + 1)..].Trim());
    }

    private static JsonElement? Resolve(string path, JsonElement root, out bool found)
    {
        if (string.IsNullOrEmpty(path))
        {
            found = false;
            return null;
        }
        if (path == ".")
        {
            found = true;
            return root;
        }
        var segments = path.Split('.');
        JsonElement current = root;
        foreach (var seg in segments)
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(seg, out var next))
            {
                current = next;
            }
            else
            {
                found = false;
                return null;
            }
        }
        found = true;
        return current;
    }

    private static bool IsTruthy(JsonElement? el)
    {
        if (!el.HasValue) return false;
        return el.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => false,
            JsonValueKind.Undefined => false,
            JsonValueKind.Number => el.Value.TryGetDouble(out var d) && d != 0,
            JsonValueKind.String => !string.IsNullOrEmpty(el.Value.GetString()),
            JsonValueKind.Array => el.Value.GetArrayLength() > 0,
            JsonValueKind.Object => true,
            _ => false,
        };
    }

    private static string ValueToString(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? string.Empty,
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => el.GetRawText(),
        };
    }

    public static string HtmlEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}

public interface ITemplateEngine
{
    TemplateRenderResult Render(string template, JsonElement data, bool strict);
    bool ValidateSyntax(string template, out string? error);
}
