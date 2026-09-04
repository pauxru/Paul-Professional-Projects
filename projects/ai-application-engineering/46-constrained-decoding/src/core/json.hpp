// Minimal JSON value model and parser.
//
// This exists so the library has no external dependencies: the schema compiler
// needs to read a schema document, and the validator needs to check that a
// generated string really is valid JSON. Using our own parser also means the
// validity oracle in the benchmarks is independent of the automaton under test.
#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <string>
#include <vector>

namespace cdec {

enum class JsonType { Null, Bool, Number, String, Array, Object };

class JsonValue;
using JsonPtr = std::shared_ptr<JsonValue>;

class JsonValue {
public:
    JsonType type = JsonType::Null;
    bool boolean = false;
    double number = 0.0;
    std::string str;
    std::vector<JsonPtr> array;
    // Insertion order is preserved separately because JSON object key order is
    // significant to the automaton we build, even though it is not significant
    // to JSON itself.
    std::vector<std::string> keys;
    std::map<std::string, JsonPtr> object;

    static JsonPtr makeNull();
    static JsonPtr makeBool(bool v);
    static JsonPtr makeNumber(double v);
    static JsonPtr makeString(std::string v);
    static JsonPtr makeArray();
    static JsonPtr makeObject();

    bool has(const std::string& key) const;
    JsonPtr get(const std::string& key) const;
};

// Parses `text`. On failure returns nullptr and fills `error`.
JsonPtr parseJson(const std::string& text, std::string& error);

// True when `text` is a complete, well-formed JSON document (no trailing data).
bool isValidJson(const std::string& text);

// Serialises back to compact JSON. Used by tests and by enum literal expansion.
std::string serializeJson(const JsonPtr& value);

// Encodes `s` as a JSON string literal, including the surrounding quotes.
std::string encodeJsonString(const std::string& s);

}  // namespace cdec
