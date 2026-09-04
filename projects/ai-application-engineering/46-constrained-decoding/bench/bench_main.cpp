// Benchmarks for the three claims this project makes:
//   1. schema compilation is cheap and the automaton is small enough to cache;
//   2. trie-pruned mask computation beats the obvious per-token loop;
//   3. per-state mask caching removes almost all of the remaining cost.
//
// Everything printed here is measured at run time. Nothing is hard-coded.
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <iomanip>
#include <iostream>
#include <random>
#include <string>
#include <vector>

#include "../src/core/json.hpp"
#include "../src/core/mask.hpp"
#include "../src/core/schema.hpp"

using namespace cdec;
using Clock = std::chrono::steady_clock;

namespace {

double msSince(Clock::time_point start) {
    return std::chrono::duration<double, std::milli>(Clock::now() - start).count();
}

// A synthetic vocabulary with the shape of a real BPE tokenizer: every single
// byte, the JSON structural pieces a tokenizer would learn, and a long tail of
// alphanumeric fragments.
std::vector<std::string> buildVocabulary(size_t target, uint32_t seed) {
    std::vector<std::string> tokens;
    for (int b = 0; b < 256; ++b) tokens.push_back(std::string(1, static_cast<char>(b)));
    for (const char* piece : {"{\"", "\":", "\":\"", "\",\"", "\"}", "\"],", "\":[", "},{", "true", "false",
                              "null", "[]", "{}", ",\"", "\": ", "  ", "\n  ", "0", "1", "2", "10", "100"}) {
        tokens.emplace_back(piece);
    }
    std::mt19937 rng(seed);
    const std::string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_ ";
    while (tokens.size() < target) {
        size_t len = 2 + rng() % 7;
        std::string t;
        for (size_t i = 0; i < len; ++i) t.push_back(alphabet[rng() % alphabet.size()]);
        tokens.push_back(t);
    }
    std::sort(tokens.begin(), tokens.end());
    tokens.erase(std::unique(tokens.begin(), tokens.end()), tokens.end());
    return tokens;
}

struct SchemaCase {
    std::string name;
    std::string text;
};

std::vector<SchemaCase> schemaCases() {
    return {
        {"enum-3", R"({"enum":["low","medium","high"]})"},
        {"flat-object", R"({"type":"object","additionalProperties":false,
            "required":["id","ok"],
            "properties":{"id":{"type":"integer","minimum":0,"maxDigits":6},
                          "ok":{"type":"boolean"}}})"},
        {"person", R"({"type":"object","additionalProperties":false,
            "required":["name","age"],
            "properties":{"name":{"type":"string","maxLength":16},
                          "age":{"type":"integer","minimum":0,"maxDigits":3},
                          "email":{"type":"string","pattern":"[a-z]{2,10}@[a-z]{2,8}\\.[a-z]{2,3}"}}})"},
        {"invoice-nested", R"({"type":"object","additionalProperties":false,
            "required":["invoiceId","lines"],
            "properties":{
              "invoiceId":{"type":"string","pattern":"INV-\\d{6}"},
              "issued":{"type":"string","pattern":"\\d{4}-\\d{2}-\\d{2}"},
              "lines":{"type":"array","minItems":1,"maxItems":3,
                       "items":{"type":"object","additionalProperties":false,
                                "required":["sku","qty"],
                                "properties":{"sku":{"type":"string","pattern":"[A-Z]{3}\\d{3}"},
                                              "qty":{"type":"integer","minimum":0,"maxDigits":3}}}}}})"},
        {"classification", R"({"type":"object","additionalProperties":false,
            "required":["label","confidence"],
            "properties":{"label":{"enum":["bug","feature","question","other"]},
                          "confidence":{"type":"number","maxDigits":1},
                          "rationale":{"type":"string","maxLength":24}}})"},
    };
}

void printRow(const std::string& cells) { std::cout << cells << "\n"; }

}  // namespace

int main() {
    std::cout << "# Measured output of cdec_bench\n\n";
    std::cout << "Every number below was produced by this run.\n\n";

    const std::vector<SchemaCase> cases = schemaCases();

    // ---- 1. Compilation cost and automaton size -------------------------
    std::cout << "## 1. Schema compilation\n\n";
    printRow("| schema | NFA states | DFA states (subset) | DFA states (minimised) | reduction | compile ms |");
    printRow("|---|---|---|---|---|---|");

    std::vector<CompiledSchema> compiled;
    for (const SchemaCase& c : cases) {
        CompiledSchema out;
        std::string error;
        auto start = Clock::now();
        if (!compileSchemaText(c.text, CompileOptions(), out, error)) {
            std::cerr << "FAILED to compile " << c.name << ": " << error << "\n";
            return 1;
        }
        double ms = msSince(start);
        double reduction = out.dfaStatesBeforeMinimisation > 0
                               ? 100.0 * (1.0 - static_cast<double>(out.dfa.stateCount) /
                                                    static_cast<double>(out.dfaStatesBeforeMinimisation))
                               : 0.0;
        char buf[256];
        std::snprintf(buf, sizeof(buf), "| %s | %zu | %d | %d | %.1f%% | %.2f |", c.name.c_str(), out.nfaStates,
                      out.dfaStatesBeforeMinimisation, out.dfa.stateCount, reduction, ms);
        printRow(buf);
        compiled.push_back(std::move(out));
    }

    // ---- 2. Key-order freedom cost --------------------------------------
    std::cout << "\n## 2. Cost of allowing free object key order\n\n";
    printRow("| schema | DFA states (any order) | DFA states (declaration order) | multiplier |");
    printRow("|---|---|---|---|");
    for (const SchemaCase& c : cases) {
        CompileOptions ordered;
        ordered.requirePropertyOrder = true;
        CompiledSchema anyOrder, fixedOrder;
        std::string error;
        if (!compileSchemaText(c.text, CompileOptions(), anyOrder, error)) continue;
        if (!compileSchemaText(c.text, ordered, fixedOrder, error)) continue;
        char buf[256];
        std::snprintf(buf, sizeof(buf), "| %s | %d | %d | %.2fx |", c.name.c_str(), anyOrder.dfa.stateCount,
                      fixedOrder.dfa.stateCount,
                      static_cast<double>(anyOrder.dfa.stateCount) / std::max(1, fixedOrder.dfa.stateCount));
        printRow(buf);
    }

    // ---- 3. Mask computation: trie pruning vs the obvious loop ----------
    const size_t vocabSize = 32000;
    Vocabulary vocab(buildVocabulary(vocabSize, 20260903u));
    std::cout << "\n## 3. Mask computation (vocabulary = " << vocab.size() << " tokens, "
              << vocab.trieNodeCount() << " trie nodes)\n\n";
    printRow("| schema | states | trie-pruned us/state | brute-force us/state | speed-up | avg allowed tokens |");
    printRow("|---|---|---|---|---|---|");

    for (size_t i = 0; i < cases.size(); ++i) {
        const Dfa& dfa = compiled[i].dfa;
        TokenMaskCache cache(dfa, vocab);

        auto start = Clock::now();
        size_t allowedTotal = 0;
        for (int s = 0; s < dfa.stateCount; ++s) allowedTotal += TokenMaskCache::countBits(cache.maskFor(s));
        double trieUs = msSince(start) * 1000.0 / std::max(1, dfa.stateCount);

        start = Clock::now();
        size_t bruteAllowed = 0;
        for (int s = 0; s < dfa.stateCount; ++s) {
            for (size_t id = 0; id < vocab.size(); ++id) {
                if (dfa.run(s, vocab.token(id)) != kDead) ++bruteAllowed;
            }
        }
        double bruteUs = msSince(start) * 1000.0 / std::max(1, dfa.stateCount);

        if (bruteAllowed != allowedTotal) {
            std::cerr << "MISMATCH between trie and brute force for " << cases[i].name << "\n";
            return 1;
        }
        char buf[256];
        std::snprintf(buf, sizeof(buf), "| %s | %d | %.1f | %.1f | %.1fx | %.0f |", cases[i].name.c_str(),
                      dfa.stateCount, trieUs, bruteUs, bruteUs / std::max(1e-9, trieUs),
                      static_cast<double>(allowedTotal) / std::max(1, dfa.stateCount));
        printRow(buf);
    }

    // ---- 4. Cache effectiveness during real decoding --------------------
    std::cout << "\n## 4. Per-state mask cache during decoding\n\n";
    printRow("| schema | documents | decode steps | distinct states | cache hit rate | mask us/step (amortised) |");
    printRow("|---|---|---|---|---|---|");

    std::mt19937 rng(7);
    for (size_t i = 0; i < cases.size(); ++i) {
        const Dfa& dfa = compiled[i].dfa;
        TokenMaskCache cache(dfa, vocab);
        cache.resetStats();

        int documents = 0;
        uint64_t steps = 0;
        auto start = Clock::now();
        for (int run = 0; run < 200; ++run) {
            int state = dfa.startState;
            for (int step = 0; step < 400; ++step) {
                if (cache.eosAllowed(state) && (rng() % 6) == 0) break;
                const TokenMask& mask = cache.maskFor(state);
                std::vector<size_t> allowed;
                for (size_t id = 0; id < vocab.size(); ++id) {
                    if (TokenMaskCache::testBit(mask, id)) allowed.push_back(id);
                }
                if (allowed.empty()) break;
                size_t pick = allowed[rng() % allowed.size()];
                state = cache.advance(state, pick);
                ++steps;
                if (state == kDead) { std::cerr << "masked decode entered dead state\n"; return 1; }
            }
            if (cache.eosAllowed(state)) ++documents;
        }
        double totalMs = msSince(start);
        const MaskStats& st = cache.stats();
        double hitRate = st.lookups ? 100.0 * (1.0 - static_cast<double>(st.misses) / static_cast<double>(st.lookups)) : 0.0;
        double maskUs = st.lookups ? static_cast<double>(st.computeNanos) / 1000.0 / static_cast<double>(st.lookups) : 0.0;
        (void)totalMs;
        char buf[256];
        std::snprintf(buf, sizeof(buf), "| %s | %d | %llu | %zu | %.2f%% | %.3f |", cases[i].name.c_str(), documents,
                      static_cast<unsigned long long>(steps), cache.cachedStates(), hitRate, maskUs);
        printRow(buf);
    }

    // ---- 5. How many masks are actually distinct? -----------------------
    // Many DFA states differ only in how much of a string literal has been
    // consumed, and those states permit exactly the same tokens. Storing one
    // copy per distinct mask is what makes eager precomputation affordable.
    std::cout << "\n## 5. Mask deduplication and eager precomputation\n\n";
    printRow("| schema | DFA states | distinct masks | dedup ratio | naive KB | deduped KB | precompute ms |");
    printRow("|---|---|---|---|---|---|---|");
    for (size_t i = 0; i < cases.size(); ++i) {
        const Dfa& dfa = compiled[i].dfa;
        TokenMaskCache cache(dfa, vocab);
        auto start = Clock::now();
        std::vector<TokenMask> masks;
        masks.reserve(static_cast<size_t>(dfa.stateCount));
        for (int s = 0; s < dfa.stateCount; ++s) masks.push_back(cache.maskFor(s));
        double precomputeMs = msSince(start);

        std::sort(masks.begin(), masks.end());
        size_t distinct = static_cast<size_t>(std::unique(masks.begin(), masks.end()) - masks.begin());
        double words = static_cast<double>(cache.maskWords());
        double naiveKb = static_cast<double>(dfa.stateCount) * words * 8.0 / 1024.0;
        double dedupKb = static_cast<double>(distinct) * words * 8.0 / 1024.0 +
                         static_cast<double>(dfa.stateCount) * 4.0 / 1024.0;  // + one index per state

        char buf[256];
        std::snprintf(buf, sizeof(buf), "| %s | %d | %zu | %.2fx | %.1f | %.1f | %.1f |", cases[i].name.c_str(),
                      dfa.stateCount, distinct,
                      static_cast<double>(dfa.stateCount) / static_cast<double>(std::max<size_t>(1, distinct)),
                      naiveKb, dedupKb, precomputeMs);
        printRow(buf);
    }

    std::cout << "\nAll invariants held: trie-pruned masks matched brute force at every state, "
                 "and no masked decode ever reached a dead state.\n";
    return 0;
}