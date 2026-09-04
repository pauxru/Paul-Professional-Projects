// Tests for the schema compiler, tokenizer alignment, and the exhaustive
// cross-check between the automaton and the independent JSON validity oracle.
#include <random>
#include <string>
#include <vector>

#include "../../src/core/json.hpp"
#include "../../src/core/mask.hpp"
#include "../../src/core/schema.hpp"
#include "test_framework.hpp"

using namespace cdec;

namespace {

CompiledSchema compile(const std::string& text, CompileOptions options = CompileOptions()) {
    CompiledSchema out;
    std::string error;
    bool ok = compileSchemaText(text, options, out, error);
    if (!ok) ::testing::fail("schema failed to compile: " + error, __FILE__, __LINE__);
    return out;
}

const char* kPersonSchema = R"({
  "type": "object",
  "additionalProperties": false,
  "required": ["name", "age"],
  "properties": {
    "name": {"type": "string", "maxLength": 6},
    "age":  {"type": "integer", "minimum": 0, "maxDigits": 3},
    "tags": {"type": "array", "maxItems": 2, "items": {"type": "string", "maxLength": 3}}
  }
})";

// A small vocabulary shaped like a real tokenizer: multi-byte pieces, pieces
// that straddle structural boundaries, and pieces that are illegal everywhere
// in this grammar.
std::vector<std::string> demoVocab() {
    return {
        "{", "}", "[", "]", ":", ",", "\"",
        "\"name\"", "\"age\"", "\"tags\"",
        "\"name\":", "{\"name\"", "\":\"",
        "Ada", "Bob", "Zoe", "ab", "c",
        "0", "1", "2", "3", "42", "007",
        "true", "false", "null",
        " ", "\n", "hello world", "<|endoftext|>",
    };
}

}  // namespace

TEST(schema_scalar_types) {
    CompiledSchema b = compile(R"({"type":"boolean"})");
    CHECK(b.dfa.matches("true"));
    CHECK(b.dfa.matches("false"));
    CHECK(!b.dfa.matches("True"));

    CompiledSchema n = compile(R"({"type":"null"})");
    CHECK(n.dfa.matches("null"));
    CHECK(!n.dfa.matches("nul"));

    CompiledSchema i = compile(R"({"type":"integer","maxDigits":3})");
    CHECK(i.dfa.matches("0"));
    CHECK(i.dfa.matches("-42"));
    CHECK(i.dfa.matches("999"));
    CHECK(!i.dfa.matches("1000"));
    CHECK(!i.dfa.matches("01"));    // JSON forbids a leading zero
    CHECK(!i.dfa.matches("1.5"));
    CHECK(!i.dfa.matches("+1"));
}

TEST(schema_integer_minimum_zero_forbids_sign) {
    CompiledSchema s = compile(R"({"type":"integer","minimum":0,"maxDigits":2})");
    CHECK(s.dfa.matches("7"));
    CHECK(!s.dfa.matches("-7"));
    // The compiler must say out loud that it only honoured the sign.
    bool warned = false;
    for (const std::string& d : s.diagnostics) {
        if (d.find("minimum/maximum not enforced") != std::string::npos) warned = true;
    }
    CHECK(warned);
}

TEST(schema_number_allows_fraction_and_exponent) {
    CompiledSchema s = compile(R"({"type":"number","maxDigits":3})");
    CHECK(s.dfa.matches("1"));
    CHECK(s.dfa.matches("-1.25"));
    CHECK(s.dfa.matches("2e10"));
    CHECK(s.dfa.matches("2E-3"));
    CHECK(!s.dfa.matches("."));
    CHECK(!s.dfa.matches("1e"));
}

TEST(schema_string_length_bounds) {
    CompiledSchema s = compile(R"({"type":"string","minLength":2,"maxLength":4})");
    CHECK(!s.dfa.matches("\"a\""));
    CHECK(s.dfa.matches("\"ab\""));
    CHECK(s.dfa.matches("\"abcd\""));
    CHECK(!s.dfa.matches("\"abcde\""));
    // Escapes count as one character, not as their byte length.
    CHECK(s.dfa.matches("\"\\n\\t\""));
    CHECK(s.dfa.matches("\"\\u00e9\\u00e9\""));
}

TEST(schema_string_accepts_valid_utf8_and_rejects_invalid) {
    CompiledSchema s = compile(R"({"type":"string","maxLength":4})");
    CHECK(s.dfa.matches("\"\xC3\xA9\""));              // U+00E9
    CHECK(s.dfa.matches("\"\xE2\x82\xAC\""));          // U+20AC
    CHECK(s.dfa.matches("\"\xF0\x9F\x98\x80\""));      // U+1F600
    CHECK(!s.dfa.matches("\"\xC0\xA9\""));             // overlong
    CHECK(!s.dfa.matches("\"\xED\xA0\x80\""));         // surrogate
    CHECK(!s.dfa.matches("\"\xE2\x82\""));             // truncated
    CHECK(!s.dfa.matches("\"\x80\""));                 // stray continuation
}

// Regression test for a real defect found by every_accepted_string_is_valid_json:
// the automaton originally allowed any four hex digits after \u, so it accepted
// lone surrogates, which are not valid JSON.
TEST(schema_string_requires_surrogate_pairing) {
    CompiledSchema s = compile(R"({"type":"string","maxLength":4})");
    CHECK(s.dfa.matches("\"\\u00e9\""));
    CHECK(s.dfa.matches("\"\\ud83d\\ude04\""));   // well-formed pair
    CHECK(!s.dfa.matches("\"\\ude04\""));         // lone low surrogate
    CHECK(!s.dfa.matches("\"\\ud83d\""));         // lone high surrogate
    CHECK(!s.dfa.matches("\"\\ud83d\\u0041\""));  // high not followed by a low
    CHECK(!s.dfa.matches("\"\\ud83dx\""));
    // D000-D7FF are ordinary code points and must still be allowed.
    CHECK(s.dfa.matches("\"\\ud7ff\""));
}

TEST(schema_string_pattern) {
    CompiledSchema s = compile(R"({"type":"string","pattern":"[A-Z]{3}-\\d{4}"})");
    CHECK(s.dfa.matches("\"ABC-1234\""));
    CHECK(!s.dfa.matches("\"ABC-123\""));
    CHECK(!s.dfa.matches("ABC-1234"));  // the quotes are part of the language
}

TEST(schema_enum_and_const) {
    CompiledSchema e = compile(R"({"enum":["red","green",7,null]})");
    CHECK(e.dfa.matches("\"red\""));
    CHECK(e.dfa.matches("7"));
    CHECK(e.dfa.matches("null"));
    CHECK(!e.dfa.matches("\"blue\""));

    CompiledSchema c = compile(R"({"const":{"a":1}})");
    CHECK(c.dfa.matches("{\"a\":1}"));
    CHECK(!c.dfa.matches("{\"a\":2}"));
}

TEST(schema_array_item_bounds) {
    CompiledSchema s = compile(R"({"type":"array","minItems":1,"maxItems":3,"items":{"type":"integer","maxDigits":1}})");
    CHECK(!s.dfa.matches("[]"));
    CHECK(s.dfa.matches("[1]"));
    CHECK(s.dfa.matches("[1,2,3]"));
    CHECK(!s.dfa.matches("[1,2,3,4]"));
    CHECK(!s.dfa.matches("[1,]"));
}

TEST(schema_object_requires_additional_properties_false) {
    CompiledSchema out;
    std::string error;
    bool ok = compileSchemaText(R"({"type":"object","properties":{"a":{"type":"integer"}}})",
                                CompileOptions(), out, error);
    CHECK(!ok);
    CHECK(error.find("additionalProperties") != std::string::npos);
}

TEST(schema_object_rejects_recursive_ref) {
    CompiledSchema out;
    std::string error;
    bool ok = compileSchemaText(R"({"type":"object","additionalProperties":false,
        "properties":{"child":{"$ref":"#"}}})", CompileOptions(), out, error);
    CHECK(!ok);
    CHECK(error.find("context-free") != std::string::npos);
}

TEST(schema_object_required_and_optional_keys) {
    CompiledSchema s = compile(kPersonSchema);
    CHECK(s.dfa.matches(R"({"name":"Ada","age":42})"));
    CHECK(s.dfa.matches(R"({"name":"Ada","age":42,"tags":["a"]})"));
    CHECK(s.dfa.matches(R"({"age":42,"name":"Ada"})"));      // key order is free
    CHECK(s.dfa.matches(R"({"tags":[],"age":0,"name":""})"));
    CHECK(!s.dfa.matches(R"({"name":"Ada"})"));               // missing required key
    CHECK(!s.dfa.matches(R"({"name":"Ada","age":42,"x":1})")); // unknown key
    CHECK(!s.dfa.matches(R"({"name":"Ada","age":42,})"));
    CHECK(!s.dfa.matches(R"({"name":"Adalovelace","age":42})"));  // maxLength 6
}

TEST(schema_property_order_mode_shrinks_the_automaton) {
    CompileOptions ordered;
    ordered.requirePropertyOrder = true;
    CompiledSchema free = compile(kPersonSchema);
    CompiledSchema fixed = compile(kPersonSchema, ordered);

    CHECK(fixed.dfa.matches(R"({"name":"Ada","age":42})"));
    CHECK(!fixed.dfa.matches(R"({"age":42,"name":"Ada"})"));
    // Permitting any key order is strictly more expensive; this is the
    // tradeoff measured in docs/benchmark-results.md.
    CHECK(free.dfa.stateCount > fixed.dfa.stateCount);
}

TEST(schema_permutation_cap_falls_back_and_says_so) {
    CompileOptions capped;
    capped.maxObjectAlternatives = 2;
    CompiledSchema s = compile(kPersonSchema, capped);
    bool warned = false;
    for (const std::string& d : s.diagnostics) {
        if (d.find("permutation disabled") != std::string::npos) warned = true;
    }
    CHECK(warned);
    CHECK(s.dfa.matches(R"({"name":"Ada","age":42})"));
    CHECK(!s.dfa.matches(R"({"age":42,"name":"Ada"})"));
}

TEST(schema_whitespace_mode) {
    CompileOptions ws;
    ws.allowWhitespace = true;
    CompiledSchema s = compile(R"({"type":"array","maxItems":2,"items":{"type":"integer","maxDigits":1}})", ws);
    CHECK(s.dfa.matches("[1,2]"));
    CHECK(s.dfa.matches("[ 1 , 2 ]"));
    CHECK(s.dfa.matches("  [1]  "));
}

// The strongest correctness statement available without a formal proof: every
// string the automaton accepts must also be accepted by the independent JSON
// parser. Any disagreement is a real bug in the compiler.
TEST(every_accepted_string_is_valid_json) {
    CompiledSchema s = compile(kPersonSchema);
    std::mt19937 rng(20260903);

    int generated = 0;
    for (int attempt = 0; attempt < 4000 && generated < 400; ++attempt) {
        int state = s.dfa.startState;
        std::string text;
        bool stuck = false;
        for (int step = 0; step < 200; ++step) {
            if (s.dfa.isAccepting(state) && (rng() % 4) == 0) break;
            std::vector<int> choices;
            for (int b = 0; b < kAlphabet; ++b) {
                if (s.dfa.step(state, static_cast<unsigned char>(b)) != kDead) choices.push_back(b);
            }
            if (choices.empty()) { stuck = !s.dfa.isAccepting(state); break; }
            int b = choices[rng() % choices.size()];
            text.push_back(static_cast<char>(b));
            state = s.dfa.step(state, static_cast<unsigned char>(b));
        }
        if (stuck || !s.dfa.isAccepting(state)) continue;
        ++generated;
        if (!isValidJson(text)) {
            ::testing::fail("automaton accepted a string the JSON parser rejects: " + text, __FILE__, __LINE__);
        }
    }
    CHECK(generated > 100);
}

TEST(mask_allows_only_tokens_that_keep_the_automaton_alive) {
    CompiledSchema s = compile(kPersonSchema);
    Vocabulary vocab(demoVocab());
    TokenMaskCache cache(s.dfa, vocab);

    const TokenMask& start = cache.maskFor(s.dfa.startState);
    // Cross-check the mask against the definition, token by token.
    for (size_t id = 0; id < vocab.size(); ++id) {
        bool allowed = TokenMaskCache::testBit(start, id);
        bool alive = s.dfa.run(s.dfa.startState, vocab.token(id)) != kDead;
        CHECK_EQ(allowed, alive);
    }
    // Only tokens starting with '{' can open this document.
    CHECK(TokenMaskCache::testBit(start, 0));   // "{"
    CHECK(TokenMaskCache::testBit(start, 11));  // "{\"name\""
    CHECK(!TokenMaskCache::testBit(start, 1));  // "}"
    CHECK(!TokenMaskCache::testBit(start, 29)); // "hello world"
}

TEST(mask_matches_brute_force_at_every_reachable_state) {
    CompiledSchema s = compile(kPersonSchema);
    Vocabulary vocab(demoVocab());
    TokenMaskCache cache(s.dfa, vocab);

    for (int state = 0; state < s.dfa.stateCount; ++state) {
        const TokenMask& mask = cache.maskFor(state);
        for (size_t id = 0; id < vocab.size(); ++id) {
            bool allowed = TokenMaskCache::testBit(mask, id);
            bool alive = s.dfa.run(state, vocab.token(id)) != kDead;
            if (allowed != alive) {
                ::testing::fail("mask disagrees with brute force at state " + std::to_string(state) +
                                    " token '" + vocab.token(id) + "'",
                                __FILE__, __LINE__);
            }
        }
    }
}

TEST(mask_cache_is_reused_across_identical_states) {
    CompiledSchema s = compile(kPersonSchema);
    Vocabulary vocab(demoVocab());
    TokenMaskCache cache(s.dfa, vocab);
    cache.resetStats();
    for (int i = 0; i < 50; ++i) cache.maskFor(s.dfa.startState);
    CHECK_EQ(cache.stats().lookups, static_cast<uint64_t>(50));
    CHECK_EQ(cache.stats().misses, static_cast<uint64_t>(1));
}

TEST(eos_is_only_legal_in_an_accepting_state) {
    CompiledSchema s = compile(kPersonSchema);
    Vocabulary vocab(demoVocab());
    TokenMaskCache cache(s.dfa, vocab);

    int state = s.dfa.startState;
    CHECK(!cache.eosAllowed(state));
    state = s.dfa.run(state, R"({"name":"Ada","age":42)");
    CHECK(state != kDead);
    CHECK(!cache.eosAllowed(state));
    state = s.dfa.step(state, '}');
    CHECK(state != kDead);
    CHECK(cache.eosAllowed(state));
}

// Greedy decoding under a mask can never produce an invalid document. This is
// the "by construction" claim, exercised rather than asserted.
TEST(masked_decoding_always_yields_valid_json) {
    CompiledSchema s = compile(kPersonSchema);
    Vocabulary vocab(demoVocab());
    TokenMaskCache cache(s.dfa, vocab);
    std::mt19937 rng(4242);

    int completed = 0;
    for (int run = 0; run < 300; ++run) {
        int state = s.dfa.startState;
        std::string text;
        for (int step = 0; step < 80; ++step) {
            if (cache.eosAllowed(state) && (rng() % 5) == 0) break;
            const TokenMask& mask = cache.maskFor(state);
            std::vector<size_t> allowed;
            for (size_t id = 0; id < vocab.size(); ++id) {
                if (TokenMaskCache::testBit(mask, id)) allowed.push_back(id);
            }
            if (allowed.empty()) break;
            size_t pick = allowed[rng() % allowed.size()];
            text += vocab.token(pick);
            state = cache.advance(state, pick);
            CHECK(state != kDead);
        }
        if (!cache.eosAllowed(state)) continue;  // ran out of budget mid-document
        ++completed;
        CHECK(isValidJson(text));
        CHECK(s.dfa.matches(text));
    }
    CHECK(completed > 20);
}

// Regression: `pattern` and `maxLength` are a conjunction, but the compiler
// used to return as soon as it saw `pattern`, silently dropping the bounds.
// The automaton then accepted strings the schema rejects -- and a constrained
// decoder emits those confidently, which is the worst possible failure here.
TEST(schema_intersects_pattern_with_length_bounds) {
    CompiledSchema s = compile(R"({"type":"string","pattern":"[ab]*","maxLength":2})");
    CHECK(s.dfa.matches("\"\""));
    CHECK(s.dfa.matches("\"a\""));
    CHECK(s.dfa.matches("\"ab\""));
    CHECK(!s.dfa.matches("\"abb\""));
    CHECK(!s.dfa.matches("\"aaa\""));
    CHECK(!s.dfa.matches("\"c\""));
}

TEST(schema_intersects_pattern_with_minimum_length) {
    CompiledSchema s = compile(R"({"type":"string","pattern":"[ab]*","minLength":2,"maxLength":3})");
    CHECK(!s.dfa.matches("\"a\""));
    CHECK(s.dfa.matches("\"ab\""));
    CHECK(s.dfa.matches("\"aba\""));
    CHECK(!s.dfa.matches("\"abab\""));
}

TEST(schema_rejects_unsatisfiable_pattern_length_combination) {
    // "ab" is exactly two bytes, so requiring three is unsatisfiable. Reporting
    // that is much better than compiling an automaton that accepts nothing and
    // wedging the decoder at the first token.
    CompiledSchema out;
    std::string error;
    bool ok = compileSchemaText(R"({"type":"string","pattern":"ab","minLength":3})",
                                CompileOptions(), out, error);
    CHECK(!ok);
    CHECK(error.find("no common solution") != std::string::npos);
}

TEST(intersect_is_language_intersection) {
    NfaBuilder a;
    Frag fa = a.repeat([&a]() { return a.range('a', 'b'); }, 0, -1);
    Dfa da = compileNfa(a, fa);
    NfaBuilder b;
    Frag fb = b.repeat([&b]() { return b.range(0x00, 0xFF); }, 2, 2);
    Dfa db = compileNfa(b, fb);
    Dfa both = intersect(da, db);
    CHECK(both.matches("ab"));
    CHECK(both.matches("bb"));
    CHECK(!both.matches("a"));
    CHECK(!both.matches("abb"));
    CHECK(!both.matches("ac"));
}

TEST(embed_round_trips_a_dfa_through_the_builder) {
    NfaBuilder src;
    Frag f = src.literal("hello");
    Dfa original = compileNfa(src, f);

    NfaBuilder dst;
    Frag embedded = embed(dst, original);
    Dfa rebuilt = compileNfa(dst, embedded);
    CHECK(rebuilt.matches("hello"));
    CHECK(!rebuilt.matches("hell"));
    CHECK(!rebuilt.matches("helloo"));
    CHECK(rebuilt.stateCount == original.stateCount);
}

TEST(apply_mask_sets_illegal_logits_to_negative_infinity) {
    CompiledSchema s = compile(R"({"type":"boolean"})");
    Vocabulary vocab({"true", "false", "maybe"});
    TokenMaskCache cache(s.dfa, vocab);
    std::vector<float> logits{1.0f, 2.0f, 3.0f};
    applyMask(cache.maskFor(s.dfa.startState), logits.data(), logits.size(), false, -1);
    CHECK(logits[0] == 1.0f);
    CHECK(logits[1] == 2.0f);
    CHECK(logits[2] == -std::numeric_limits<float>::infinity());
}

// Regression: the logit vector is longer than the vocabulary whenever the model
// has special ids. When the vocabulary size is an exact multiple of 64 the mask
// has no spare bits, and indexing it by the EOS id read past the end of the
// buffer. Found while writing the ctypes bindings, where the caller allocates
// the mask buffer and the overread is a genuine out-of-bounds access.
TEST(apply_mask_tolerates_logits_longer_than_the_mask) {
    CompiledSchema s = compile(R"({"type":"boolean"})");
    std::vector<std::string> tokens;
    tokens.push_back("true");
    tokens.push_back("false");
    for (int i = 0; i < 62; ++i) tokens.push_back("z" + std::to_string(i));
    CHECK(tokens.size() == 64);
    Vocabulary vocab(tokens);
    TokenMaskCache cache(s.dfa, vocab);
    CHECK(cache.maskWords() == 1);  // exactly 64 bits, no spare capacity

    std::vector<float> logits(65, 5.0f);  // 64 vocabulary ids + EOS at index 64
    applyMask(cache.maskFor(s.dfa.startState), logits.data(), logits.size(), false, 64);
    CHECK(logits[0] == 5.0f);
    CHECK(logits[1] == 5.0f);
    CHECK(logits[2] == -std::numeric_limits<float>::infinity());
    CHECK(logits[64] == -std::numeric_limits<float>::infinity());

    // applyMask only ever suppresses, so the vector has to be refilled before
    // asking the opposite question.
    std::fill(logits.begin(), logits.end(), 5.0f);
    applyMask(cache.maskFor(s.dfa.startState), logits.data(), logits.size(), true, 64);
    CHECK(logits[64] == 5.0f);
}
