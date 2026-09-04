#include "capi.h"

#include <cstring>
#include <memory>
#include <new>
#include <string>
#include <vector>

#include "../core/json.hpp"
#include "../core/mask.hpp"
#include "../core/schema.hpp"

// Every entry point is wrapped so that no C++ exception can cross the ABI
// boundary. An exception unwinding into a ctypes caller is undefined behaviour.
#define CDEC_GUARD_BEGIN try {
#define CDEC_GUARD_END(fallback) } catch (...) { return fallback; }
#define CDEC_GUARD_END_VOID } catch (...) { return; }

struct cdec_engine {
    cdec::CompiledSchema compiled;
    std::string diagnostics;
    std::unique_ptr<cdec::Vocabulary> vocab;
    std::unique_ptr<cdec::TokenMaskCache> cache;
    int32_t eosTokenId = -1;
};

namespace {

void copyError(char* err, size_t len, const std::string& msg) {
    if (!err || len == 0) return;
    size_t n = msg.size() < len - 1 ? msg.size() : len - 1;
    std::memcpy(err, msg.data(), n);
    err[n] = '\0';
}

}  // namespace

extern "C" {

CDEC_API cdec_engine* cdec_create(const char* schema_json,
                                  int allow_whitespace,
                                  int require_property_order,
                                  char* err,
                                  size_t err_len) {
    CDEC_GUARD_BEGIN
    if (!schema_json) { copyError(err, err_len, "schema_json is null"); return nullptr; }
    cdec::CompileOptions options;
    options.allowWhitespace = allow_whitespace != 0;
    options.requirePropertyOrder = require_property_order != 0;

    auto engine = std::make_unique<cdec_engine>();
    std::string error;
    if (!cdec::compileSchemaText(schema_json, options, engine->compiled, error)) {
        copyError(err, err_len, error);
        return nullptr;
    }
    for (const std::string& d : engine->compiled.diagnostics) {
        engine->diagnostics += d;
        engine->diagnostics += "\n";
    }
    copyError(err, err_len, "");
    return engine.release();
    CDEC_GUARD_END(nullptr)
}

CDEC_API void cdec_destroy(cdec_engine* engine) { delete engine; }

CDEC_API int cdec_set_vocab(cdec_engine* engine,
                            const char* blob,
                            const int32_t* lengths,
                            int32_t count,
                            int32_t eos_token_id) {
    CDEC_GUARD_BEGIN
    if (!engine || !blob || !lengths || count < 0) return -1;
    std::vector<std::string> tokens;
    tokens.reserve(static_cast<size_t>(count));
    size_t offset = 0;
    for (int32_t i = 0; i < count; ++i) {
        if (lengths[i] < 0) return -1;
        tokens.emplace_back(blob + offset, static_cast<size_t>(lengths[i]));
        offset += static_cast<size_t>(lengths[i]);
    }
    engine->vocab = std::make_unique<cdec::Vocabulary>(std::move(tokens));
    engine->cache = std::make_unique<cdec::TokenMaskCache>(engine->compiled.dfa, *engine->vocab);
    engine->eosTokenId = eos_token_id;
    return 0;
    CDEC_GUARD_END(-1)
}

CDEC_API int32_t cdec_start_state(const cdec_engine* engine) {
    return engine ? engine->compiled.dfa.startState : cdec::kDead;
}

CDEC_API int32_t cdec_advance_token(cdec_engine* engine, int32_t state, int32_t token_id) {
    CDEC_GUARD_BEGIN
    if (!engine || !engine->vocab) return cdec::kDead;
    if (token_id < 0 || static_cast<size_t>(token_id) >= engine->vocab->size()) return cdec::kDead;
    return engine->compiled.dfa.run(state, engine->vocab->token(static_cast<size_t>(token_id)));
    CDEC_GUARD_END(cdec::kDead)
}

CDEC_API int32_t cdec_advance_bytes(const cdec_engine* engine, int32_t state, const char* bytes, int32_t len) {
    CDEC_GUARD_BEGIN
    if (!engine || !bytes || len < 0) return cdec::kDead;
    return engine->compiled.dfa.run(state, std::string(bytes, static_cast<size_t>(len)));
    CDEC_GUARD_END(cdec::kDead)
}

CDEC_API int cdec_is_accepting(const cdec_engine* engine, int32_t state) {
    return engine && engine->compiled.dfa.isAccepting(state) ? 1 : 0;
}

CDEC_API int cdec_eos_allowed(const cdec_engine* engine, int32_t state) {
    return cdec_is_accepting(engine, state);
}

CDEC_API int cdec_matches(const cdec_engine* engine, const char* text, int32_t len) {
    CDEC_GUARD_BEGIN
    if (!engine || !text || len < 0) return 0;
    return engine->compiled.dfa.matches(std::string(text, static_cast<size_t>(len))) ? 1 : 0;
    CDEC_GUARD_END(0)
}

CDEC_API int32_t cdec_mask_words(const cdec_engine* engine) {
    if (!engine || !engine->cache) return -1;
    return static_cast<int32_t>(engine->cache->maskWords());
}

CDEC_API int32_t cdec_mask(cdec_engine* engine, int32_t state, uint64_t* words, int32_t word_count) {
    CDEC_GUARD_BEGIN
    if (!engine || !engine->cache || !words) return -1;
    if (word_count < static_cast<int32_t>(engine->cache->maskWords())) return -1;
    const cdec::TokenMask& mask = engine->cache->maskFor(state);
    std::memcpy(words, mask.data(), mask.size() * sizeof(uint64_t));
    return static_cast<int32_t>(cdec::TokenMaskCache::countBits(mask));
    CDEC_GUARD_END(-1)
}

CDEC_API void cdec_apply_mask(const uint64_t* words,
                              int32_t word_count,
                              float* logits,
                              int32_t count,
                              int eos_allowed,
                              int32_t eos_token_id) {
    CDEC_GUARD_BEGIN
    if (!words || !logits || count < 0 || word_count < 0) return;
    // The caller owns `words`; its length is the vocabulary's mask width, which
    // is not derivable from `count` because the logit vector also holds special
    // ids. Trusting `count` here would read past the buffer.
    cdec::TokenMask mask(words, words + static_cast<size_t>(word_count));
    cdec::applyMask(mask, logits, static_cast<size_t>(count), eos_allowed != 0, eos_token_id);
    CDEC_GUARD_END_VOID
}

CDEC_API int32_t cdec_dfa_states(const cdec_engine* engine) {
    return engine ? engine->compiled.dfa.stateCount : -1;
}
CDEC_API int32_t cdec_dfa_states_premin(const cdec_engine* engine) {
    return engine ? engine->compiled.dfaStatesBeforeMinimisation : -1;
}
CDEC_API int32_t cdec_nfa_states(const cdec_engine* engine) {
    return engine ? static_cast<int32_t>(engine->compiled.nfaStates) : -1;
}
CDEC_API int32_t cdec_cached_states(const cdec_engine* engine) {
    return engine && engine->cache ? static_cast<int32_t>(engine->cache->cachedStates()) : -1;
}
CDEC_API int32_t cdec_trie_nodes(const cdec_engine* engine) {
    return engine && engine->vocab ? static_cast<int32_t>(engine->vocab->trieNodeCount()) : -1;
}

CDEC_API int32_t cdec_diagnostics(const cdec_engine* engine, char* buf, int32_t len) {
    CDEC_GUARD_BEGIN
    if (!engine) return -1;
    int32_t needed = static_cast<int32_t>(engine->diagnostics.size()) + 1;
    if (buf && len > 0) {
        size_t n = engine->diagnostics.size() < static_cast<size_t>(len) - 1
                       ? engine->diagnostics.size()
                       : static_cast<size_t>(len) - 1;
        std::memcpy(buf, engine->diagnostics.data(), n);
        buf[n] = '\0';
    }
    return needed;
    CDEC_GUARD_END(-1)
}

CDEC_API void cdec_mask_stats(const cdec_engine* engine,
                              uint64_t* lookups,
                              uint64_t* misses,
                              uint64_t* trie_nodes_visited,
                              uint64_t* compute_nanos) {
    if (!engine || !engine->cache) return;
    const cdec::MaskStats& s = engine->cache->stats();
    if (lookups) *lookups = s.lookups;
    if (misses) *misses = s.misses;
    if (trie_nodes_visited) *trie_nodes_visited = s.trieNodesVisited;
    if (compute_nanos) *compute_nanos = s.computeNanos;
}

CDEC_API void cdec_reset_stats(cdec_engine* engine) {
    if (engine && engine->cache) engine->cache->resetStats();
}

CDEC_API int cdec_valid_json(const char* text, int32_t len) {
    CDEC_GUARD_BEGIN
    if (!text || len < 0) return 0;
    return cdec::isValidJson(std::string(text, static_cast<size_t>(len))) ? 1 : 0;
    CDEC_GUARD_END(0)
}

CDEC_API const char* cdec_version(void) { return "0.1.0"; }

}  // extern "C"
