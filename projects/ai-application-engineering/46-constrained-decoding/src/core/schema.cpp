#include "schema.hpp"

#include <algorithm>
#include <numeric>

#include "regex.hpp"

namespace cdec {
namespace {

class SchemaCompiler {
public:
    SchemaCompiler(NfaBuilder& builder, const CompileOptions& options)
        : b_(builder), opt_(options) {}

    std::string error;
    std::vector<std::string> diagnostics;

    bool compile(const JsonPtr& schema, int depth, Frag& out) {
        if (depth > opt_.maxNestingDepth) return fail("schema nesting exceeds maxNestingDepth");
        if (!schema || schema->type != JsonType::Object) return fail("schema node must be an object");

        if (schema->has("$ref")) {
            return fail("$ref is not supported: a recursive schema describes a context-free "
                        "language, which no finite automaton can recognise exactly");
        }
        if (schema->has("const")) {
            out = b_.literal(serializeJson(schema->get("const")));
            return true;
        }
        if (schema->has("enum")) {
            JsonPtr values = schema->get("enum");
            if (values->type != JsonType::Array || values->array.empty()) return fail("enum must be a non-empty array");
            std::vector<Frag> options;
            for (const JsonPtr& v : values->array) options.push_back(b_.literal(serializeJson(v)));
            out = b_.alt(options);
            return true;
        }
        if (schema->has("anyOf") || schema->has("oneOf")) {
            JsonPtr branches = schema->has("anyOf") ? schema->get("anyOf") : schema->get("oneOf");
            if (branches->type != JsonType::Array || branches->array.empty()) return fail("anyOf/oneOf must be a non-empty array");
            // Note: `oneOf` requires *exactly* one branch to match. A union
            // automaton implements `anyOf`. When the branches are disjoint the
            // two coincide; disjointness is not checked, so this is recorded.
            if (schema->has("oneOf")) diagnostics.push_back("oneOf compiled as anyOf (branch disjointness not verified)");
            std::vector<Frag> options;
            for (const JsonPtr& s : branches->array) {
                Frag f;
                if (!compile(s, depth + 1, f)) return false;
                options.push_back(f);
            }
            out = b_.alt(options);
            return true;
        }

        JsonPtr typeNode = schema->get("type");
        if (!typeNode) return fail("schema node needs `type`, `enum`, `const` or `anyOf`");
        if (typeNode->type == JsonType::Array) {
            std::vector<Frag> options;
            for (const JsonPtr& t : typeNode->array) {
                if (t->type != JsonType::String) return fail("type array entries must be strings");
                JsonPtr clone = std::make_shared<JsonValue>(*schema);
                clone->object["type"] = JsonValue::makeString(t->str);
                Frag f;
                if (!compile(clone, depth + 1, f)) return false;
                options.push_back(f);
            }
            out = b_.alt(options);
            return true;
        }
        if (typeNode->type != JsonType::String) return fail("`type` must be a string or array of strings");
        const std::string& type = typeNode->str;

        if (type == "null") { out = b_.literal("null"); return true; }
        if (type == "boolean") { out = b_.alt({b_.literal("true"), b_.literal("false")}); return true; }
        if (type == "string") return compileString(schema, out);
        if (type == "integer") return compileInteger(schema, out);
        if (type == "number") return compileNumber(schema, out);
        if (type == "array") return compileArray(schema, depth, out);
        if (type == "object") return compileObject(schema, depth, out);
        return fail("unsupported type: " + type);
    }

    // Optional insignificant whitespace, used only when enabled.
    Frag ws() {
        if (!opt_.allowWhitespace) return b_.empty();
        return b_.star([this]() {
            return b_.alt({b_.byte(' '), b_.byte('\t'), b_.byte('\n'), b_.byte('\r')});
        });
    }

private:
    NfaBuilder& b_;
    const CompileOptions& opt_;

    bool fail(const std::string& msg) {
        if (error.empty()) error = msg;
        return false;
    }

    static int intField(const JsonPtr& schema, const char* name, int fallback) {
        JsonPtr v = schema->get(name);
        if (!v || v->type != JsonType::Number) return fallback;
        return static_cast<int>(v->number);
    }

    // One character inside a JSON string: any unescaped JSON-safe byte, a
    // two-character escape, a \u escape, or a well-formed UTF-8 sequence.
    Frag stringChar() {
        std::vector<Frag> options;
        options.push_back(b_.range(0x20, 0x21));
        options.push_back(b_.range(0x23, 0x5B));
        options.push_back(b_.range(0x5D, 0x7E));

        std::vector<Frag> escapes;
        for (char c : std::string("\"\\/bfnrt")) escapes.push_back(b_.byte(static_cast<unsigned char>(c)));
        options.push_back(b_.concat(b_.byte('\\'), b_.alt(escapes)));

        // \uXXXX escapes must respect surrogate pairing. A lone surrogate is
        // not valid JSON, and "is this hex4 a surrogate?" is decidable from
        // the first two digits alone, so the constraint stays regular:
        //   D800-DBFF high, DC00-DFFF low, anything else standalone.
        auto hex = [this]() { return b_.alt({b_.range('0', '9'), b_.range('a', 'f'), b_.range('A', 'F')}); };
        auto hexD = [this]() { return b_.alt({b_.byte('d'), b_.byte('D')}); };
        auto hexNotD = [this]() {
            return b_.alt({b_.range('0', '9'), b_.range('a', 'c'), b_.range('e', 'f'),
                           b_.range('A', 'C'), b_.range('E', 'F')});
        };
        auto uEsc = [this]() { return b_.concat(b_.byte('\\'), b_.byte('u')); };

        // Not a surrogate: either the first digit is not D, or it is D and the
        // second digit is 0-7.
        options.push_back(b_.concatAll({uEsc(), hexNotD(), hex(), hex(), hex()}));
        options.push_back(b_.concatAll({uEsc(), hexD(), b_.range('0', '7'), hex(), hex()}));

        // A surrogate pair is a single code point, so it is a single character
        // for the purposes of minLength/maxLength.
        auto highSurrogate = [&]() {
            return b_.concatAll({uEsc(), hexD(),
                                 b_.alt({b_.range('8', '9'), b_.range('a', 'b'), b_.range('A', 'B')}),
                                 hex(), hex()});
        };
        auto lowSurrogate = [&]() {
            return b_.concatAll({uEsc(), hexD(),
                                 b_.alt({b_.range('c', 'f'), b_.range('C', 'F')}),
                                 hex(), hex()});
        };
        options.push_back(b_.concat(highSurrogate(), lowSurrogate()));

        // UTF-8 continuation bytes, with the ranges that exclude overlongs,
        // surrogates and values above U+10FFFF.
        auto cont = [this]() { return b_.range(0x80, 0xBF); };
        options.push_back(b_.concat(b_.range(0xC2, 0xDF), cont()));
        options.push_back(b_.concatAll({b_.byte(0xE0), b_.range(0xA0, 0xBF), cont()}));
        options.push_back(b_.concatAll({b_.range(0xE1, 0xEC), cont(), cont()}));
        options.push_back(b_.concatAll({b_.byte(0xED), b_.range(0x80, 0x9F), cont()}));
        options.push_back(b_.concatAll({b_.range(0xEE, 0xEF), cont(), cont()}));
        options.push_back(b_.concatAll({b_.byte(0xF0), b_.range(0x90, 0xBF), cont(), cont()}));
        options.push_back(b_.concatAll({b_.range(0xF1, 0xF3), cont(), cont(), cont()}));
        options.push_back(b_.concatAll({b_.byte(0xF4), b_.range(0x80, 0x8F), cont(), cont()}));

        return b_.alt(options);
    }

    bool compileString(const JsonPtr& schema, Frag& out) {
        int minLen = intField(schema, "minLength", 0);
        int maxLen = intField(schema, "maxLength", -1);
        JsonPtr pattern = schema->get("pattern");
        const bool hasPattern = pattern && pattern->type == JsonType::String;

        if (hasPattern) {
            Frag body;
            std::string err;
            if (!compileRegex(b_, pattern->str, body, err)) return fail(err);
            if (schema->has("minLength") || schema->has("maxLength")) {
                // `pattern` and the length bounds are a conjunction. Honouring
                // only one of them was a real defect: the automaton accepted
                // strings the schema rejects, and a constrained decoder would
                // then emit them confidently. The fix is a product construction.
                if (minLen > (maxLen < 0 ? minLen : maxLen)) return fail("minLength exceeds maxLength");
                Dfa patternDfa = compileNfa(b_, body);
                NfaBuilder lengthBuilder;
                Frag lengthFrag = lengthBuilder.repeat(
                    [&lengthBuilder]() { return lengthBuilder.range(0x00, 0xFF); },
                    minLen, maxLen);
                Dfa lengthDfa = compileNfa(lengthBuilder, lengthFrag);
                Dfa combined = intersect(patternDfa, lengthDfa);
                bool empty = true;
                for (uint8_t flag : combined.accepting) {
                    if (flag) { empty = false; break; }
                }
                if (empty) return fail("pattern and length bounds have no common solution");
                // Length is measured in bytes, which equals characters only for
                // the ASCII subset the regex compiler accepts. Say so rather
                // than let a caller assume code points.
                diagnostics.push_back("minLength/maxLength on a patterned string are measured in bytes");
                body = embed(b_, combined);
            }
            out = b_.concatAll({b_.byte('"'), body, b_.byte('"')});
            return true;
        }

        if (maxLen < 0) {
            maxLen = opt_.defaultMaxStringLength;
            diagnostics.push_back("string without maxLength bounded at " + std::to_string(maxLen));
        }
        if (minLen > maxLen) return fail("minLength exceeds maxLength");
        Frag body = b_.repeat([this]() { return stringChar(); }, minLen, maxLen);
        out = b_.concatAll({b_.byte('"'), body, b_.byte('"')});
        return true;
    }

    bool compileInteger(const JsonPtr& schema, Frag& out) {
        int maxDigits = intField(schema, "maxDigits", opt_.defaultMaxIntegerDigits);
        bool allowNegative = true;
        JsonPtr minimum = schema->get("minimum");
        if (minimum && minimum->type == JsonType::Number && minimum->number >= 0) allowNegative = false;
        if (schema->has("minimum") || schema->has("maximum")) {
            // Numeric ranges are regular but the construction is per-digit and
            // is not implemented; only the sign is honoured.
            diagnostics.push_back("numeric minimum/maximum not enforced by the automaton (only the sign is)");
        }
        // -?(0|[1-9][0-9]{0,maxDigits-1})
        Frag zero = b_.byte('0');
        Frag nonZero = b_.concat(b_.range('1', '9'),
                                 b_.repeat([this]() { return b_.range('0', '9'); }, 0, std::max(0, maxDigits - 1)));
        Frag magnitude = b_.alt({zero, nonZero});
        out = allowNegative ? b_.concat(b_.opt(b_.byte('-')), magnitude) : magnitude;
        return true;
    }

    bool compileNumber(const JsonPtr& schema, Frag& out) {
        Frag intPart;
        if (!compileInteger(schema, intPart)) return false;
        Frag fraction = b_.opt(b_.concat(
            b_.byte('.'),
            b_.repeat([this]() { return b_.range('0', '9'); }, 1, opt_.defaultMaxFractionDigits)));
        Frag exponent = b_.opt(b_.concatAll({
            b_.alt({b_.byte('e'), b_.byte('E')}),
            b_.opt(b_.alt({b_.byte('+'), b_.byte('-')})),
            b_.repeat([this]() { return b_.range('0', '9'); }, 1, 3),
        }));
        out = b_.concatAll({intPart, fraction, exponent});
        return true;
    }

    bool compileArray(const JsonPtr& schema, int depth, Frag& out) {
        JsonPtr items = schema->get("items");
        if (!items) return fail("array schema requires `items`");
        int minItems = intField(schema, "minItems", 0);
        int maxItems = intField(schema, "maxItems", -1);
        if (maxItems < 0) {
            maxItems = opt_.defaultMaxItems;
            diagnostics.push_back("array without maxItems bounded at " + std::to_string(maxItems));
        }
        if (minItems > maxItems) return fail("minItems exceeds maxItems");

        bool ok = true;
        auto item = [&]() -> Frag {
            Frag f;
            if (!compile(items, depth + 1, f)) { ok = false; return b_.empty(); }
            return f;
        };

        std::vector<Frag> lengths;
        for (int n = minItems; n <= maxItems; ++n) {
            std::vector<Frag> parts;
            parts.push_back(b_.byte('['));
            parts.push_back(ws());
            for (int k = 0; k < n; ++k) {
                if (k) { parts.push_back(b_.byte(',')); parts.push_back(ws()); }
                parts.push_back(item());
                if (!ok) return fail(error.empty() ? "failed to compile array items" : error);
                parts.push_back(ws());
            }
            parts.push_back(b_.byte(']'));
            lengths.push_back(b_.concatAll(parts));
        }
        out = b_.alt(lengths);
        return true;
    }

    static double factorialCapped(size_t n) {
        double acc = 1.0;
        for (size_t k = 2; k <= n; ++k) {
            acc *= static_cast<double>(k);
            if (acc > 1e12) return acc;
        }
        return acc;
    }

    bool compileObject(const JsonPtr& schema, int depth, Frag& out) {
        JsonPtr properties = schema->get("properties");
        if (!properties || properties->type != JsonType::Object) return fail("object schema requires `properties`");

        JsonPtr additional = schema->get("additionalProperties");
        if (!additional || additional->type != JsonType::Bool || additional->boolean) {
            // Allowing unknown keys would make the language "anything at all",
            // which defeats the purpose of constraining decoding.
            return fail("object schema must set \"additionalProperties\": false");
        }

        std::vector<std::string> required;
        if (JsonPtr req = schema->get("required")) {
            if (req->type != JsonType::Array) return fail("`required` must be an array");
            for (const JsonPtr& r : req->array) {
                if (r->type != JsonType::String) return fail("`required` entries must be strings");
                if (!properties->has(r->str)) return fail("required key not present in properties: " + r->str);
                required.push_back(r->str);
            }
        }

        std::vector<std::string> optionalKeys;
        for (const std::string& key : properties->keys) {
            if (std::find(required.begin(), required.end(), key) == required.end()) optionalKeys.push_back(key);
        }
        if (optionalKeys.size() > 16) return fail("too many optional properties to enumerate (limit 16)");

        // Estimate the expansion before building anything.
        double alternatives = 0.0;
        const size_t subsets = static_cast<size_t>(1) << optionalKeys.size();
        for (size_t mask = 0; mask < subsets; ++mask) {
            size_t present = required.size();
            for (size_t k = 0; k < optionalKeys.size(); ++k) {
                if (mask & (static_cast<size_t>(1) << k)) ++present;
            }
            alternatives += opt_.requirePropertyOrder ? 1.0 : factorialCapped(present);
            if (alternatives > 1e12) break;
        }
        bool permute = !opt_.requirePropertyOrder;
        if (permute && alternatives > static_cast<double>(opt_.maxObjectAlternatives)) {
            permute = false;
            diagnostics.push_back(
                "object key permutation disabled: " + std::to_string(static_cast<long long>(alternatives)) +
                " alternatives exceeds cap " + std::to_string(opt_.maxObjectAlternatives) +
                "; declaration order is enforced instead");
        }

        bool ok = true;
        auto member = [&](const std::string& key) -> Frag {
            Frag value;
            if (!compile(properties->get(key), depth + 1, value)) { ok = false; return b_.empty(); }
            return b_.concatAll({b_.literal(encodeJsonString(key)), ws(), b_.byte(':'), ws(), value});
        };

        std::vector<Frag> layouts;
        for (size_t mask = 0; mask < subsets; ++mask) {
            std::vector<std::string> keys = required;
            for (size_t k = 0; k < optionalKeys.size(); ++k) {
                if (mask & (static_cast<size_t>(1) << k)) keys.push_back(optionalKeys[k]);
            }
            if (!permute) {
                // Declaration order, filtered to the selected keys.
                std::vector<std::string> ordered;
                for (const std::string& key : properties->keys) {
                    if (std::find(keys.begin(), keys.end(), key) != keys.end()) ordered.push_back(key);
                }
                keys = ordered;
                layouts.push_back(buildLayout(keys, member));
                if (!ok) return fail(error.empty() ? "failed to compile object members" : error);
                continue;
            }
            std::vector<std::string> perm = keys;
            std::sort(perm.begin(), perm.end());
            do {
                layouts.push_back(buildLayout(perm, member));
                if (!ok) return fail(error.empty() ? "failed to compile object members" : error);
            } while (std::next_permutation(perm.begin(), perm.end()));
        }
        out = b_.alt(layouts);
        return true;
    }

    template <typename MemberFn>
    Frag buildLayout(const std::vector<std::string>& keys, MemberFn& member) {
        std::vector<Frag> parts;
        parts.push_back(b_.byte('{'));
        parts.push_back(ws());
        for (size_t k = 0; k < keys.size(); ++k) {
            if (k) { parts.push_back(b_.byte(',')); parts.push_back(ws()); }
            parts.push_back(member(keys[k]));
            parts.push_back(ws());
        }
        parts.push_back(b_.byte('}'));
        return b_.concatAll(parts);
    }
};

}  // namespace

bool compileSchema(const JsonPtr& schema,
                   const CompileOptions& options,
                   CompiledSchema& out,
                   std::string& error) {
    NfaBuilder builder;
    SchemaCompiler compiler(builder, options);
    Frag root;
    if (!compiler.compile(schema, 0, root)) {
        error = compiler.error;
        return false;
    }
    // Wrap in optional leading/trailing whitespace when enabled.
    Frag whole = builder.concatAll({compiler.ws(), root, compiler.ws()});

    out.nfaStates = builder.stateCount();
    Dfa raw = determinize(builder, whole);
    out.dfaStatesBeforeMinimisation = raw.stateCount;
    out.dfa = minimize(raw);
    out.diagnostics = compiler.diagnostics;
    return true;
}

bool compileSchemaText(const std::string& schemaText,
                       const CompileOptions& options,
                       CompiledSchema& out,
                       std::string& error) {
    JsonPtr schema = parseJson(schemaText, error);
    if (!schema) return false;
    return compileSchema(schema, options, out, error);
}

}  // namespace cdec
