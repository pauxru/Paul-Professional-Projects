using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IntegrationHub.Application;

public sealed record FieldMapping(string TargetPath, string Expression);

public sealed record MappingTrace(
    string TargetPath,
    string Expression,
    string? SourceValue,
    string? ResultValue,
    bool Success,
    string? Error);

public sealed record MappingResult(JsonObject Output, IReadOnlyList<MappingTrace> Trace);

public sealed class SafeExpressionException(string message) : Exception(message);

public sealed class SafeExpressionEvaluator
{
    private const int MaxDepth = 12;
    private const int MaxExpressionLength = 2_048;
    private const int MaxResultLength = 1_000_000;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _lookupTables;

    public SafeExpressionEvaluator(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? lookupTables = null)
    {
        _lookupTables = lookupTables
            ?? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    }

    public JsonNode? Evaluate(string expression, JsonNode? input)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > MaxExpressionLength)
        {
            throw new SafeExpressionException("Expression is empty or exceeds the size limit.");
        }

        var result = EvaluateCore(expression.Trim(), input, 0);
        if (result?.ToJsonString().Length > MaxResultLength)
        {
            throw new SafeExpressionException("Expression result exceeds the size limit.");
        }

        return result;
    }

    private JsonNode? EvaluateCore(string expression, JsonNode? input, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new SafeExpressionException("Expression nesting limit exceeded.");
        }

        if (expression.StartsWith('$'))
        {
            return JsonPath.Get(input, expression)?.DeepClone();
        }

        if ((expression.StartsWith('"') && expression.EndsWith('"'))
            || (expression.StartsWith('\'') && expression.EndsWith('\'')))
        {
            return JsonValue.Create(Unescape(expression[1..^1], expression[0]));
        }

        if (string.Equals(expression, "null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (bool.TryParse(expression, out var boolean))
        {
            return JsonValue.Create(boolean);
        }

        if (decimal.TryParse(expression, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            return JsonValue.Create(number);
        }

        var open = expression.IndexOf('(');
        if (open <= 0 || !expression.EndsWith(')'))
        {
            throw new SafeExpressionException("Only JSON paths, literals and whitelisted function calls are allowed.");
        }

        var name = expression[..open].Trim().ToLowerInvariant();
        if (name.Any(c => !char.IsAsciiLetter(c) && c != '_'))
        {
            throw new SafeExpressionException("Invalid function name.");
        }

        var arguments = SplitArguments(expression[(open + 1)..^1])
            .Select(x => EvaluateCore(x, input, depth + 1))
            .ToArray();

        return name switch
        {
            "trim" => UnaryString(arguments, x => x.Trim()),
            "upper" => UnaryString(arguments, x => x.ToUpperInvariant()),
            "lower" => UnaryString(arguments, x => x.ToLowerInvariant()),
            "date_parse" => DateParse(arguments),
            "date_format" => DateFormat(arguments),
            "decimal_scale" => DecimalScale(arguments),
            "currency" => Currency(arguments),
            "concat" => JsonValue.Create(string.Concat(arguments.Select(AsString))),
            "split" => Split(arguments),
            "lookup" => Lookup(arguments),
            "default" => Default(arguments),
            "coalesce" => arguments.FirstOrDefault(x => !IsEmpty(x))?.DeepClone(),
            "conditional" => Conditional(arguments),
            "eq" => JsonValue.Create(EqualsValue(Require(arguments, 2)[0], arguments[1])),
            "not" => JsonValue.Create(!AsBoolean(Require(arguments, 1)[0])),
            _ => throw new SafeExpressionException($"Function '{name}' is not allowed.")
        };
    }

    private static JsonNode? UnaryString(JsonNode?[] arguments, Func<string, string> operation)
    {
        Require(arguments, 1);
        return arguments[0] is null ? null : JsonValue.Create(operation(AsString(arguments[0])));
    }

    private static JsonNode? DateParse(JsonNode?[] arguments)
    {
        if (arguments.Length is < 1 or > 2)
        {
            throw new SafeExpressionException("date_parse expects one or two arguments.");
        }

        var text = AsString(arguments[0]);
        DateTimeOffset parsed;
        if (arguments.Length == 2)
        {
            if (!DateTimeOffset.TryParseExact(
                    text,
                    AsString(arguments[1]),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out parsed))
            {
                throw new SafeExpressionException($"'{text}' is not a valid date for the supplied format.");
            }
        }
        else if (!DateTimeOffset.TryParse(
                     text,
                     CultureInfo.InvariantCulture,
                     DateTimeStyles.AssumeUniversal,
                     out parsed))
        {
            throw new SafeExpressionException($"'{text}' is not a valid date.");
        }

        return JsonValue.Create(parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    private static JsonNode DateFormat(JsonNode?[] arguments)
    {
        Require(arguments, 3);
        if (!DateTimeOffset.TryParse(
                AsString(arguments[0]),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            throw new SafeExpressionException("date_format received an invalid date.");
        }

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(AsString(arguments[2]));
        }
        catch (TimeZoneNotFoundException ex)
        {
            throw new SafeExpressionException($"Unknown timezone: {ex.Message}");
        }

        return JsonValue.Create(TimeZoneInfo.ConvertTime(parsed, zone).ToString(
            AsString(arguments[1]),
            CultureInfo.InvariantCulture))!;
    }

    private static JsonNode DecimalScale(JsonNode?[] arguments)
    {
        Require(arguments, 2);
        if (!decimal.TryParse(AsString(arguments[0]), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            || !decimal.TryParse(AsString(arguments[1]), NumberStyles.Number, CultureInfo.InvariantCulture, out var factor))
        {
            throw new SafeExpressionException("decimal_scale requires numeric arguments.");
        }

        return JsonValue.Create(value * factor)!;
    }

    private static JsonNode Currency(JsonNode?[] arguments)
    {
        Require(arguments, 1);
        var code = AsString(arguments[0]).Trim().ToUpperInvariant();
        var mapped = code switch
        {
            "KSH" or "KES SHILLING" => "KES",
            "US$" or "DOLLAR" => "USD",
            "EURO" => "EUR",
            _ => code
        };
        return JsonValue.Create(mapped)!;
    }

    private static JsonNode? Split(JsonNode?[] arguments)
    {
        Require(arguments, 3);
        if (!int.TryParse(AsString(arguments[2]), CultureInfo.InvariantCulture, out var index))
        {
            throw new SafeExpressionException("split index must be an integer.");
        }

        var parts = AsString(arguments[0]).Split(AsString(arguments[1]), StringSplitOptions.None);
        return index >= 0 && index < parts.Length ? JsonValue.Create(parts[index]) : null;
    }

    private JsonNode? Lookup(JsonNode?[] arguments)
    {
        Require(arguments, 2);
        var tableName = AsString(arguments[0]);
        var key = AsString(arguments[1]);
        return _lookupTables.TryGetValue(tableName, out var table) && table.TryGetValue(key, out var value)
            ? JsonValue.Create(value)
            : null;
    }

    private static JsonNode? Default(JsonNode?[] arguments)
    {
        Require(arguments, 2);
        return IsEmpty(arguments[0]) ? arguments[1]?.DeepClone() : arguments[0]?.DeepClone();
    }

    private static JsonNode? Conditional(JsonNode?[] arguments)
    {
        Require(arguments, 3);
        return AsBoolean(arguments[0]) ? arguments[1]?.DeepClone() : arguments[2]?.DeepClone();
    }

    private static bool EqualsValue(JsonNode? left, JsonNode? right) =>
        string.Equals(AsString(left), AsString(right), StringComparison.OrdinalIgnoreCase);

    private static bool AsBoolean(JsonNode? value)
    {
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var boolean))
        {
            return boolean;
        }

        return bool.TryParse(AsString(value), out var parsed) && parsed;
    }

    private static bool IsEmpty(JsonNode? value) =>
        value is null || string.IsNullOrWhiteSpace(AsString(value));

    private static string AsString(JsonNode? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<string>(out var text))
            {
                return text;
            }
            if (jsonValue.TryGetValue<decimal>(out var number))
            {
                return number.ToString(CultureInfo.InvariantCulture);
            }
            if (jsonValue.TryGetValue<bool>(out var boolean))
            {
                return boolean.ToString(CultureInfo.InvariantCulture);
            }
        }

        return value.ToJsonString();
    }

    private static JsonNode?[] Require(JsonNode?[] arguments, int count)
    {
        if (arguments.Length != count)
        {
            throw new SafeExpressionException($"Function expects {count} argument(s).");
        }

        return arguments;
    }

    private static IReadOnlyList<string> SplitArguments(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        var result = new List<string>();
        var builder = new StringBuilder();
        var depth = 0;
        char? quote = null;
        var escaped = false;

        foreach (var character in input)
        {
            if (escaped)
            {
                builder.Append(character);
                escaped = false;
                continue;
            }

            if (character == '\\' && quote is not null)
            {
                builder.Append(character);
                escaped = true;
                continue;
            }

            if (quote is not null)
            {
                builder.Append(character);
                if (character == quote)
                {
                    quote = null;
                }
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                builder.Append(character);
            }
            else if (character == '(')
            {
                depth++;
                builder.Append(character);
            }
            else if (character == ')')
            {
                depth--;
                if (depth < 0)
                {
                    throw new SafeExpressionException("Unbalanced expression.");
                }
                builder.Append(character);
            }
            else if (character == ',' && depth == 0)
            {
                result.Add(builder.ToString().Trim());
                builder.Clear();
            }
            else
            {
                builder.Append(character);
            }
        }

        if (quote is not null || depth != 0)
        {
            throw new SafeExpressionException("Unbalanced expression.");
        }

        result.Add(builder.ToString().Trim());
        return result;
    }

    private static string Unescape(string value, char quote) =>
        value.Replace($"\\{quote}", quote.ToString(), StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
}

public sealed class MappingEngine(SafeExpressionEvaluator evaluator)
{
    public MappingResult Map(JsonNode? input, IReadOnlyList<FieldMapping> mappings)
    {
        if (input?.ToJsonString().Length > 2_000_000 || mappings.Count > 500)
        {
            throw new SafeExpressionException("Mapping input or field count exceeds the safety limit.");
        }

        var output = new JsonObject();
        var traces = new List<MappingTrace>(mappings.Count);
        foreach (var mapping in mappings)
        {
            try
            {
                var result = evaluator.Evaluate(mapping.Expression, input);
                JsonPath.Set(output, mapping.TargetPath, result?.DeepClone());
                traces.Add(new MappingTrace(
                    mapping.TargetPath,
                    mapping.Expression,
                    ExtractSourceValue(mapping.Expression, input),
                    result?.ToJsonString(),
                    true,
                    null));
            }
            catch (Exception ex) when (ex is SafeExpressionException or FormatException or InvalidOperationException)
            {
                traces.Add(new MappingTrace(
                    mapping.TargetPath,
                    mapping.Expression,
                    ExtractSourceValue(mapping.Expression, input),
                    null,
                    false,
                    ex.Message));
            }
        }

        return new MappingResult(output, traces);
    }

    private static string? ExtractSourceValue(string expression, JsonNode? input)
    {
        var path = expression.Split(new[] { '(', ',', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith('$'));
        return path is null ? null : JsonPath.Get(input, path)?.ToJsonString();
    }
}

public static class JsonPath
{
    public static JsonNode? Get(JsonNode? root, string path)
    {
        if (root is null)
        {
            return null;
        }

        var tokens = Tokenize(path);
        JsonNode? current = root;
        foreach (var token in tokens)
        {
            if (current is JsonObject obj && token.Property is not null)
            {
                current = obj[token.Property];
            }
            else if (current is JsonArray array && token.Index is int index && index >= 0 && index < array.Count)
            {
                current = array[index];
            }
            else if (current is JsonArray wildcardArray && token.Wildcard)
            {
                current = new JsonArray(wildcardArray.Select(x => x?.DeepClone()).ToArray());
            }
            else
            {
                return null;
            }
        }

        return current;
    }

    public static void Set(JsonObject root, string path, JsonNode? value)
    {
        var tokens = Tokenize(path);
        if (tokens.Count == 0)
        {
            throw new SafeExpressionException("Target path must identify a field.");
        }

        JsonNode current = root;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var last = i == tokens.Count - 1;
            if (token.Property is not null && current is JsonObject obj)
            {
                if (last)
                {
                    obj[token.Property] = value;
                    return;
                }

                obj[token.Property] ??= tokens[i + 1].Index is not null ? new JsonArray() : new JsonObject();
                current = obj[token.Property]!;
            }
            else if (token.Index is int index && current is JsonArray array)
            {
                while (array.Count <= index)
                {
                    array.Add(null);
                }

                if (last)
                {
                    array[index] = value;
                    return;
                }

                array[index] ??= tokens[i + 1].Index is not null ? new JsonArray() : new JsonObject();
                current = array[index]!;
            }
            else
            {
                throw new SafeExpressionException($"Target path '{path}' cannot be constructed.");
            }
        }
    }

    private static IReadOnlyList<PathToken> Tokenize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('$'))
        {
            throw new SafeExpressionException("JSON paths must start with '$'.");
        }

        var result = new List<PathToken>();
        var index = 1;
        while (index < path.Length)
        {
            if (path[index] == '.')
            {
                index++;
                var start = index;
                while (index < path.Length && path[index] is not ('.' or '['))
                {
                    index++;
                }

                if (start == index)
                {
                    throw new SafeExpressionException($"Invalid JSON path '{path}'.");
                }
                result.Add(new PathToken(path[start..index], null, false));
            }
            else if (path[index] == '[')
            {
                var close = path.IndexOf(']', index);
                if (close < 0)
                {
                    throw new SafeExpressionException($"Invalid JSON path '{path}'.");
                }

                var segment = path[(index + 1)..close];
                if (segment == "*")
                {
                    result.Add(new PathToken(null, null, true));
                }
                else if (int.TryParse(segment, out var arrayIndex) && arrayIndex >= 0)
                {
                    result.Add(new PathToken(null, arrayIndex, false));
                }
                else
                {
                    throw new SafeExpressionException($"Invalid array index in '{path}'.");
                }
                index = close + 1;
            }
            else
            {
                throw new SafeExpressionException($"Invalid JSON path '{path}'.");
            }
        }

        return result;
    }

    private sealed record PathToken(string? Property, int? Index, bool Wildcard);
}
