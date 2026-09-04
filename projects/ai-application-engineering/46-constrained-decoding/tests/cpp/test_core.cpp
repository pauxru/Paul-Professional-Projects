// Tests for the JSON parser, the regex subset, and the automaton machinery.
#include <string>
#include <vector>

#include "../../src/core/automaton.hpp"
#include "../../src/core/json.hpp"
#include "../../src/core/regex.hpp"
#include "test_framework.hpp"

using namespace cdec;

namespace {

Dfa compilePattern(const std::string& pattern) {
    NfaBuilder builder;
    Frag frag;
    std::string error;
    CHECK(compileRegex(builder, pattern, frag, error));
    return compileNfa(builder, frag);
}

}  // namespace

TEST(json_accepts_well_formed_documents) {
    CHECK(isValidJson("{}"));
    CHECK(isValidJson("[]"));
    CHECK(isValidJson("{\"a\":1,\"b\":[true,false,null]}"));
    CHECK(isValidJson("-0.5e+10"));
    CHECK(isValidJson("\"\\u00e9\""));
    CHECK(isValidJson("\"\\ud83d\\ude00\""));  // surrogate pair
}

TEST(json_rejects_malformed_documents) {
    CHECK(!isValidJson(""));
    CHECK(!isValidJson("{"));
    CHECK(!isValidJson("{\"a\":}"));
    CHECK(!isValidJson("{'a':1}"));
    CHECK(!isValidJson("01"));            // leading zero
    CHECK(!isValidJson("1."));            // trailing decimal point
    CHECK(!isValidJson("[1,]"));          // trailing comma
    CHECK(!isValidJson("{\"a\":1} junk"));  // trailing data
    CHECK(!isValidJson("\"\\ud83d\""));   // lone surrogate
    CHECK(!isValidJson("\"raw\tcontrol\""));
}

TEST(json_round_trips_through_serialisation) {
    std::string error;
    JsonPtr value = parseJson("{\"b\":2,\"a\":[1,\"x\"]}", error);
    CHECK(value != nullptr);
    // Key order is preserved from the source document.
    CHECK_EQ(serializeJson(value), std::string("{\"b\":2,\"a\":[1,\"x\"]}"));
}

TEST(json_escapes_are_encoded_correctly) {
    CHECK_EQ(encodeJsonString("a\"b\\c\nd"), std::string("\"a\\\"b\\\\c\\nd\""));
    CHECK_EQ(encodeJsonString(std::string("\x01")), std::string("\"\\u0001\""));
}

TEST(automaton_literal_matches_exactly) {
    NfaBuilder builder;
    Frag frag = builder.literal("hello");
    Dfa dfa = compileNfa(builder, frag);
    CHECK(dfa.matches("hello"));
    CHECK(!dfa.matches("hell"));
    CHECK(!dfa.matches("helloo"));
    CHECK(!dfa.matches(""));
}

TEST(automaton_alternation_and_repetition) {
    NfaBuilder builder;
    Frag frag = builder.alt({builder.literal("ab"), builder.plus([&]() { return builder.byte('c'); })});
    Dfa dfa = compileNfa(builder, frag);
    CHECK(dfa.matches("ab"));
    CHECK(dfa.matches("c"));
    CHECK(dfa.matches("ccccc"));
    CHECK(!dfa.matches(""));
    CHECK(!dfa.matches("abc"));
}

TEST(automaton_bounded_repeat_respects_both_bounds) {
    NfaBuilder builder;
    Frag frag = builder.repeat([&]() { return builder.range('0', '9'); }, 2, 4);
    Dfa dfa = compileNfa(builder, frag);
    CHECK(!dfa.matches("1"));
    CHECK(dfa.matches("12"));
    CHECK(dfa.matches("123"));
    CHECK(dfa.matches("1234"));
    CHECK(!dfa.matches("12345"));
}

TEST(automaton_minimisation_preserves_language) {
    NfaBuilder builder;
    Frag frag = builder.alt({builder.literal("cat"), builder.literal("car"), builder.literal("cart")});
    Dfa raw = determinize(builder, frag);
    Dfa small = minimize(raw);
    for (const std::string& word : {"cat", "car", "cart"}) {
        CHECK(raw.matches(word));
        CHECK(small.matches(word));
    }
    for (const std::string& word : {"ca", "carts", "dog", ""}) {
        CHECK(!raw.matches(word));
        CHECK(!small.matches(word));
    }
    CHECK(small.stateCount <= raw.stateCount);
}

TEST(automaton_minimisation_drops_unproductive_states) {
    // "ab" followed by a byte that leads nowhere useful: after minimisation
    // there should be no state from which no accepting state is reachable.
    NfaBuilder builder;
    Frag frag = builder.literal("abc");
    Dfa small = compileNfa(builder, frag);
    for (int s = 0; s < small.stateCount; ++s) {
        bool hasOut = small.isAccepting(s);
        for (int b = 0; b < kAlphabet && !hasOut; ++b) hasOut = small.step(s, static_cast<unsigned char>(b)) != kDead;
        CHECK(hasOut);
    }
}

TEST(regex_literals_classes_and_quantifiers) {
    Dfa dfa = compilePattern("[A-Z]{2}-[0-9]+");
    CHECK(dfa.matches("AB-1"));
    CHECK(dfa.matches("ZZ-99999"));
    CHECK(!dfa.matches("A-1"));
    CHECK(!dfa.matches("ABC-1"));
    CHECK(!dfa.matches("AB-"));
    CHECK(!dfa.matches("ab-1"));
}

TEST(regex_alternation_and_groups) {
    Dfa dfa = compilePattern("(cat|dog)s?");
    CHECK(dfa.matches("cat"));
    CHECK(dfa.matches("cats"));
    CHECK(dfa.matches("dogs"));
    CHECK(!dfa.matches("catdog"));
    CHECK(!dfa.matches("cast"));
}

TEST(regex_negated_class_excludes_json_unsafe_bytes) {
    Dfa dfa = compilePattern("[^0-9]+");
    CHECK(dfa.matches("abc"));
    CHECK(!dfa.matches("a1c"));
    // A quote can never be produced, because the match is embedded in a JSON
    // string literal.
    CHECK(!dfa.matches("a\"c"));
    CHECK(!dfa.matches("a\\c"));
}

TEST(regex_date_shape) {
    Dfa dfa = compilePattern("\\d{4}-\\d{2}-\\d{2}");
    CHECK(dfa.matches("2026-09-03"));
    CHECK(!dfa.matches("2026-9-03"));
    CHECK(!dfa.matches("2026-09-03T00:00"));
}

TEST(regex_rejects_unsupported_syntax) {
    NfaBuilder builder;
    Frag frag;
    std::string error;
    CHECK(!compileRegex(builder, "a**", frag, error));
    CHECK(!error.empty());

    std::string error2;
    CHECK(!compileRegex(builder, "(unclosed", frag, error2));
    CHECK(!error2.empty());

    // A pattern demanding a literal quote cannot be honoured inside a JSON
    // string without escaping, so it is rejected rather than silently altered.
    std::string error3;
    CHECK(!compileRegex(builder, "a\"b", frag, error3));
    CHECK(!error3.empty());
}
