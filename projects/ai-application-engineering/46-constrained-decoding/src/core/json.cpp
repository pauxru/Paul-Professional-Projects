#include "json.hpp"

#include <cmath>
#include <cstdio>
#include <cstdlib>

namespace cdec {
namespace {

struct Parser {
    const std::string& s;
    size_t i = 0;
    std::string error;

    explicit Parser(const std::string& text) : s(text) {}

    void skipWs() {
        while (i < s.size() && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) ++i;
    }

    bool fail(const std::string& msg) {
        if (error.empty()) error = msg + " at offset " + std::to_string(i);
        return false;
    }

    bool literal(const char* lit) {
        size_t n = std::char_traits<char>::length(lit);
        if (s.compare(i, n, lit) != 0) return fail(std::string("expected ") + lit);
        i += n;
        return true;
    }

    bool parseHex4(unsigned& out) {
        if (i + 4 > s.size()) return fail("truncated \\u escape");
        out = 0;
        for (int k = 0; k < 4; ++k) {
            char c = s[i + k];
            unsigned d;
            if (c >= '0' && c <= '9') d = static_cast<unsigned>(c - '0');
            else if (c >= 'a' && c <= 'f') d = static_cast<unsigned>(c - 'a' + 10);
            else if (c >= 'A' && c <= 'F') d = static_cast<unsigned>(c - 'A' + 10);
            else return fail("bad hex digit in \\u escape");
            out = out * 16 + d;
        }
        i += 4;
        return true;
    }

    static void appendUtf8(std::string& out, unsigned cp) {
        if (cp < 0x80) {
            out.push_back(static_cast<char>(cp));
        } else if (cp < 0x800) {
            out.push_back(static_cast<char>(0xC0 | (cp >> 6)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        } else if (cp < 0x10000) {
            out.push_back(static_cast<char>(0xE0 | (cp >> 12)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        } else {
            out.push_back(static_cast<char>(0xF0 | (cp >> 18)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        }
    }

    bool parseString(std::string& out) {
        if (i >= s.size() || s[i] != '"') return fail("expected '\"'");
        ++i;
        out.clear();
        while (true) {
            if (i >= s.size()) return fail("unterminated string");
            unsigned char c = static_cast<unsigned char>(s[i]);
            if (c == '"') { ++i; return true; }
            if (c < 0x20) return fail("unescaped control character in string");
            if (c == '\\') {
                ++i;
                if (i >= s.size()) return fail("unterminated escape");
                char e = s[i++];
                switch (e) {
                    case '"': out.push_back('"'); break;
                    case '\\': out.push_back('\\'); break;
                    case '/': out.push_back('/'); break;
                    case 'b': out.push_back('\b'); break;
                    case 'f': out.push_back('\f'); break;
                    case 'n': out.push_back('\n'); break;
                    case 'r': out.push_back('\r'); break;
                    case 't': out.push_back('\t'); break;
                    case 'u': {
                        unsigned cp;
                        if (!parseHex4(cp)) return false;
                        if (cp >= 0xD800 && cp <= 0xDBFF) {
                            if (i + 1 < s.size() && s[i] == '\\' && s[i + 1] == 'u') {
                                i += 2;
                                unsigned lo;
                                if (!parseHex4(lo)) return false;
                                if (lo < 0xDC00 || lo > 0xDFFF) return fail("bad low surrogate");
                                cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                            } else {
                                return fail("lone high surrogate");
                            }
                        } else if (cp >= 0xDC00 && cp <= 0xDFFF) {
                            return fail("lone low surrogate");
                        }
                        appendUtf8(out, cp);
                        break;
                    }
                    default: return fail("invalid escape");
                }
                continue;
            }
            out.push_back(static_cast<char>(c));
            ++i;
        }
    }

    bool parseNumber(double& out) {
        size_t start = i;
        if (i < s.size() && s[i] == '-') ++i;
        if (i >= s.size()) return fail("truncated number");
        if (s[i] == '0') {
            ++i;
        } else if (s[i] >= '1' && s[i] <= '9') {
            while (i < s.size() && s[i] >= '0' && s[i] <= '9') ++i;
        } else {
            return fail("expected digit");
        }
        if (i < s.size() && s[i] == '.') {
            ++i;
            if (i >= s.size() || s[i] < '0' || s[i] > '9') return fail("expected digit after '.'");
            while (i < s.size() && s[i] >= '0' && s[i] <= '9') ++i;
        }
        if (i < s.size() && (s[i] == 'e' || s[i] == 'E')) {
            ++i;
            if (i < s.size() && (s[i] == '+' || s[i] == '-')) ++i;
            if (i >= s.size() || s[i] < '0' || s[i] > '9') return fail("expected digit in exponent");
            while (i < s.size() && s[i] >= '0' && s[i] <= '9') ++i;
        }
        out = std::strtod(s.substr(start, i - start).c_str(), nullptr);
        return true;
    }

    bool parseValue(JsonPtr& out) {
        skipWs();
        if (i >= s.size()) return fail("unexpected end of input");
        char c = s[i];
        if (c == '{') return parseObject(out);
        if (c == '[') return parseArray(out);
        if (c == '"') {
            std::string v;
            if (!parseString(v)) return false;
            out = JsonValue::makeString(std::move(v));
            return true;
        }
        if (c == 't') { if (!literal("true")) return false; out = JsonValue::makeBool(true); return true; }
        if (c == 'f') { if (!literal("false")) return false; out = JsonValue::makeBool(false); return true; }
        if (c == 'n') { if (!literal("null")) return false; out = JsonValue::makeNull(); return true; }
        double d;
        if (!parseNumber(d)) return false;
        out = JsonValue::makeNumber(d);
        return true;
    }

    bool parseArray(JsonPtr& out) {
        ++i;  // '['
        out = JsonValue::makeArray();
        skipWs();
        if (i < s.size() && s[i] == ']') { ++i; return true; }
        while (true) {
            JsonPtr item;
            if (!parseValue(item)) return false;
            out->array.push_back(item);
            skipWs();
            if (i < s.size() && s[i] == ',') { ++i; continue; }
            if (i < s.size() && s[i] == ']') { ++i; return true; }
            return fail("expected ',' or ']'");
        }
    }

    bool parseObject(JsonPtr& out) {
        ++i;  // '{'
        out = JsonValue::makeObject();
        skipWs();
        if (i < s.size() && s[i] == '}') { ++i; return true; }
        while (true) {
            skipWs();
            std::string key;
            if (!parseString(key)) return false;
            skipWs();
            if (i >= s.size() || s[i] != ':') return fail("expected ':'");
            ++i;
            JsonPtr val;
            if (!parseValue(val)) return false;
            if (!out->object.count(key)) out->keys.push_back(key);
            out->object[key] = val;
            skipWs();
            if (i < s.size() && s[i] == ',') { ++i; continue; }
            if (i < s.size() && s[i] == '}') { ++i; return true; }
            return fail("expected ',' or '}'");
        }
    }
};

}  // namespace

JsonPtr JsonValue::makeNull() { auto v = std::make_shared<JsonValue>(); v->type = JsonType::Null; return v; }
JsonPtr JsonValue::makeBool(bool b) { auto v = std::make_shared<JsonValue>(); v->type = JsonType::Bool; v->boolean = b; return v; }
JsonPtr JsonValue::makeNumber(double d) { auto v = std::make_shared<JsonValue>(); v->type = JsonType::Number; v->number = d; return v; }
JsonPtr JsonValue::makeString(std::string s) { auto v = std::make_shared<JsonValue>(); v->type = JsonType::String; v->str = std::move(s); return v; }
JsonPtr JsonValue::makeArray() { auto v = std::make_shared<JsonValue>(); v->type = JsonType::Array; return v; }
JsonPtr JsonValue::makeObject() { auto v = std::make_shared<JsonValue>(); v->type = JsonType::Object; return v; }

bool JsonValue::has(const std::string& key) const { return object.count(key) != 0; }

JsonPtr JsonValue::get(const std::string& key) const {
    auto it = object.find(key);
    return it == object.end() ? nullptr : it->second;
}

JsonPtr parseJson(const std::string& text, std::string& error) {
    Parser p(text);
    JsonPtr root;
    if (!p.parseValue(root)) { error = p.error; return nullptr; }
    p.skipWs();
    if (p.i != text.size()) { error = "trailing data at offset " + std::to_string(p.i); return nullptr; }
    return root;
}

bool isValidJson(const std::string& text) {
    std::string err;
    return parseJson(text, err) != nullptr;
}

std::string encodeJsonString(const std::string& s) {
    std::string out = "\"";
    for (unsigned char c : s) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\b': out += "\\b"; break;
            case '\f': out += "\\f"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20) {
                    char buf[8];
                    std::snprintf(buf, sizeof(buf), "\\u%04x", c);
                    out += buf;
                } else {
                    out.push_back(static_cast<char>(c));
                }
        }
    }
    out += "\"";
    return out;
}

std::string serializeJson(const JsonPtr& value) {
    if (!value) return "null";
    switch (value->type) {
        case JsonType::Null: return "null";
        case JsonType::Bool: return value->boolean ? "true" : "false";
        case JsonType::Number: {
            double d = value->number;
            if (d == std::floor(d) && std::fabs(d) < 1e15) {
                return std::to_string(static_cast<long long>(d));
            }
            char buf[40];
            std::snprintf(buf, sizeof(buf), "%.17g", d);
            return buf;
        }
        case JsonType::String: return encodeJsonString(value->str);
        case JsonType::Array: {
            std::string out = "[";
            for (size_t k = 0; k < value->array.size(); ++k) {
                if (k) out += ",";
                out += serializeJson(value->array[k]);
            }
            return out + "]";
        }
        case JsonType::Object: {
            std::string out = "{";
            bool first = true;
            for (const auto& key : value->keys) {
                if (!first) out += ",";
                first = false;
                out += encodeJsonString(key) + ":" + serializeJson(value->object.at(key));
            }
            return out + "}";
        }
    }
    return "null";
}

}  // namespace cdec
