using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentPlatform.Domain.Json;

/// <summary>
/// A deliberately small, dependency-free JSON Schema validator (a practical subset of
/// draft-07). It exists so that <b>every argument object produced by a model is validated
/// before a tool ever executes</b>. There is no code generation, no expression evaluation
/// and no reflection over arbitrary types — the validator only inspects data.
///
/// Supported keywords: <c>type</c> (object/array/string/number/integer/boolean/null and
/// arrays thereof), <c>properties</c>, <c>required</c>, <c>additionalProperties</c> (bool),
/// <c>enum</c>, <c>const</c>, <c>minimum</c>/<c>maximum</c>/<c>exclusiveMinimum</c>/
/// <c>exclusiveMaximum</c>, <c>minLength</c>/<c>maxLength</c>, <c>pattern</c>,
/// <c>items</c>, <c>minItems</c>/<c>maxItems</c>, <c>uniqueItems</c>, and <c>format</c>
/// (<c>email</c>, <c>uri</c>, <c>date-time</c>).
/// </summary>
public sealed class JsonSchema
{
    private readonly JsonObject _schema;

    public JsonSchema(JsonObject schema) => _schema = schema ?? throw new ArgumentNullException(nameof(schema));

    public JsonObject Root => _schema;

    public static JsonSchema Parse(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new ArgumentException("Schema must be a JSON object.", nameof(json));
        return new JsonSchema(node);
    }

    public string ToJsonString() => _schema.ToJsonString();

    /// <summary>Validate a value against this schema. Never throws for invalid data.</summary>
    public SchemaValidationResult Validate(JsonNode? value)
    {
        var errors = new List<SchemaError>();
        ValidateNode(value, _schema, "$", errors);
        return errors.Count == 0
            ? SchemaValidationResult.Valid
            : new SchemaValidationResult(false, errors);
    }

    /// <summary>
    /// Attempt to coerce a value into schema-compatible shapes (e.g. the string "5" into the
    /// integer 5, "true" into a boolean) <b>then</b> validate. Coercion is conservative and
    /// lossless-or-nothing: an ambiguous value is left untouched and reported by validation.
    /// This returns a *new* node graph, never mutating the caller's input.
    /// </summary>
    public (JsonNode? Coerced, SchemaValidationResult Result) CoerceAndValidate(JsonNode? value)
    {
        var coerced = Coerce(value, _schema);
        return (coerced, Validate(coerced));
    }

    private static JsonNode? Coerce(JsonNode? value, JsonObject schema)
    {
        var type = SchemaTypes(schema);

        if (value is JsonObject obj)
        {
            var result = new JsonObject();
            var props = schema["properties"] as JsonObject;
            foreach (var kvp in obj)
            {
                var childSchema = props?[kvp.Key] as JsonObject;
                result[kvp.Key] = childSchema is null
                    ? kvp.Value?.DeepClone()
                    : Coerce(kvp.Value, childSchema);
            }
            return result;
        }

        if (value is JsonArray arr)
        {
            var result = new JsonArray();
            var itemSchema = schema["items"] as JsonObject;
            foreach (var item in arr)
                result.Add(itemSchema is null ? item?.DeepClone() : Coerce(item, itemSchema));
            return result;
        }

        if (value is JsonValue jv && jv.TryGetValue<string>(out var s))
        {
            // Only coerce scalar strings when the schema unambiguously wants a non-string.
            if (type.Contains("string"))
                return value.DeepClone();

            var trimmed = s.Trim();
            if ((type.Contains("integer")) && long.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l))
                return JsonValue.Create(l);
            if (type.Contains("number") && decimal.TryParse(trimmed, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d))
                return JsonValue.Create(d);
            if (type.Contains("boolean"))
            {
                if (bool.TryParse(trimmed, out var b)) return JsonValue.Create(b);
                if (trimmed is "1") return JsonValue.Create(true);
                if (trimmed is "0") return JsonValue.Create(false);
            }
        }

        return value?.DeepClone();
    }

    private static void ValidateNode(JsonNode? value, JsonObject schema, string path, List<SchemaError> errors)
    {
        var types = SchemaTypes(schema);

        if (types.Count > 0 && !MatchesAnyType(value, types))
        {
            errors.Add(new SchemaError(path, $"expected type {string.Join("|", types)} but was {DescribeType(value)}"));
            return; // further keyword checks are meaningless if the type is wrong
        }

        if (schema["const"] is JsonNode constNode && !JsonNode.DeepEquals(value, constNode))
            errors.Add(new SchemaError(path, $"must equal const {constNode.ToJsonString()}"));

        if (schema["enum"] is JsonArray enumArr && !enumArr.Any(e => JsonNode.DeepEquals(e, value)))
            errors.Add(new SchemaError(path, $"must be one of [{string.Join(", ", enumArr.Select(e => e?.ToJsonString()))}]"));

        switch (value)
        {
            case JsonObject obj:
                ValidateObject(obj, schema, path, errors);
                break;
            case JsonArray arr:
                ValidateArray(arr, schema, path, errors);
                break;
            case JsonValue jv:
                ValidateScalar(jv, schema, path, errors);
                break;
        }
    }

    private static void ValidateObject(JsonObject obj, JsonObject schema, string path, List<SchemaError> errors)
    {
        var properties = schema["properties"] as JsonObject;

        if (schema["required"] is JsonArray required)
        {
            foreach (var req in required)
            {
                var name = req?.GetValue<string>();
                if (name is not null && !obj.ContainsKey(name))
                    errors.Add(new SchemaError(path, $"missing required property '{name}'"));
            }
        }

        var additionalAllowed = schema["additionalProperties"]?.GetValueKind() != JsonValueKind.False;

        foreach (var kvp in obj)
        {
            var childPath = $"{path}.{kvp.Key}";
            if (properties?[kvp.Key] is JsonObject childSchema)
                ValidateNode(kvp.Value, childSchema, childPath, errors);
            else if (!additionalAllowed)
                errors.Add(new SchemaError(childPath, "additional properties are not permitted"));
        }
    }

    private static void ValidateArray(JsonArray arr, JsonObject schema, string path, List<SchemaError> errors)
    {
        if (schema["minItems"]?.GetValue<int>() is int min && arr.Count < min)
            errors.Add(new SchemaError(path, $"must have at least {min} items"));
        if (schema["maxItems"]?.GetValue<int>() is int max && arr.Count > max)
            errors.Add(new SchemaError(path, $"must have at most {max} items"));

        if (schema["uniqueItems"]?.GetValueKind() == JsonValueKind.True)
        {
            for (var i = 0; i < arr.Count; i++)
                for (var j = i + 1; j < arr.Count; j++)
                    if (JsonNode.DeepEquals(arr[i], arr[j]))
                        errors.Add(new SchemaError($"{path}[{j}]", "duplicate item not permitted"));
        }

        if (schema["items"] is JsonObject itemSchema)
        {
            for (var i = 0; i < arr.Count; i++)
                ValidateNode(arr[i], itemSchema, $"{path}[{i}]", errors);
        }
    }

    private static void ValidateScalar(JsonValue jv, JsonObject schema, string path, List<SchemaError> errors)
    {
        if (jv.TryGetValue<string>(out var s))
        {
            if (schema["minLength"]?.GetValue<int>() is int minLen && s.Length < minLen)
                errors.Add(new SchemaError(path, $"must be at least {minLen} characters"));
            if (schema["maxLength"]?.GetValue<int>() is int maxLen && s.Length > maxLen)
                errors.Add(new SchemaError(path, $"must be at most {maxLen} characters"));
            if (schema["pattern"]?.GetValue<string>() is string pattern &&
                !Regex.IsMatch(s, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250)))
                errors.Add(new SchemaError(path, $"must match pattern '{pattern}'"));
            if (schema["format"]?.GetValue<string>() is string format && !MatchesFormat(s, format))
                errors.Add(new SchemaError(path, $"must be a valid {format}"));
            return;
        }

        if (TryGetDecimal(jv, out var number))
        {
            if (TryGetDecimal(schema["minimum"], out var minimum) && number < minimum)
                errors.Add(new SchemaError(path, $"must be >= {minimum}"));
            if (TryGetDecimal(schema["maximum"], out var maximum) && number > maximum)
                errors.Add(new SchemaError(path, $"must be <= {maximum}"));
            if (TryGetDecimal(schema["exclusiveMinimum"], out var exMin) && number <= exMin)
                errors.Add(new SchemaError(path, $"must be > {exMin}"));
            if (TryGetDecimal(schema["exclusiveMaximum"], out var exMax) && number >= exMax)
                errors.Add(new SchemaError(path, $"must be < {exMax}"));
        }
    }

    private static bool MatchesFormat(string value, string format) => format switch
    {
        "email" => Regex.IsMatch(value, "^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$", RegexOptions.None, TimeSpan.FromMilliseconds(250)),
        "uri" => Uri.TryCreate(value, UriKind.Absolute, out _),
        "date-time" => DateTimeOffset.TryParse(value, out _),
        _ => true, // unknown formats are advisory only
    };

    private static HashSet<string> SchemaTypes(JsonObject schema)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        switch (schema["type"])
        {
            case JsonValue v when v.TryGetValue<string>(out var t):
                result.Add(t);
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    if (item?.GetValue<string>() is string t) result.Add(t);
                break;
        }
        return result;
    }

    private static bool MatchesAnyType(JsonNode? value, HashSet<string> types)
    {
        foreach (var type in types)
            if (MatchesType(value, type)) return true;
        return false;
    }

    private static bool MatchesType(JsonNode? value, string type) => type switch
    {
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "null" => value is null,
        "string" => value is JsonValue s && s.TryGetValue<string>(out _),
        "boolean" => value is JsonValue b && b.TryGetValue<bool>(out _),
        "integer" => value is JsonValue i && IsInteger(i),
        "number" => value is JsonValue n && TryGetDecimal(n, out _),
        _ => false,
    };

    private static bool IsInteger(JsonValue value)
    {
        if (value.TryGetValue<long>(out _)) return true;
        if (value.TryGetValue<int>(out _)) return true;
        if (value.TryGetValue<decimal>(out var d)) return decimal.Truncate(d) == d;
        if (value.TryGetValue<double>(out var db)) return Math.Truncate(db) == db;
        return false;
    }

    private static bool TryGetDecimal(JsonNode? node, out decimal value)
    {
        value = 0m;
        if (node is not JsonValue jv) return false;
        if (jv.TryGetValue<decimal>(out value)) return true;
        if (jv.TryGetValue<double>(out var d)) { value = (decimal)d; return true; }
        if (jv.TryGetValue<long>(out var l)) { value = l; return true; }
        return false;
    }

    private static string DescribeType(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v when v.TryGetValue<string>(out _) => "string",
        JsonValue v when v.TryGetValue<bool>(out _) => "boolean",
        JsonValue v when IsInteger(v) => "integer",
        JsonValue => "number",
        _ => "unknown",
    };
}
