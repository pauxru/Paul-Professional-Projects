// A deliberately narrow C ABI.
//
// Everything crossing this boundary is a primitive or a pointer to caller-owned
// memory. No C++ types, no exceptions, no ownership transfer except through
// cdec_create / cdec_destroy. That is what makes it callable from ctypes
// without a compiler, and stable across compiler versions.
#ifndef CDEC_CAPI_H
#define CDEC_CAPI_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(CDEC_BUILD_SHARED)
#    define CDEC_API __declspec(dllexport)
#  else
#    define CDEC_API
#  endif
#else
#  define CDEC_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct cdec_engine cdec_engine;

/* Compiles `schema_json` into a DFA. Returns NULL on failure and writes a
 * NUL-terminated message into `err`. */
CDEC_API cdec_engine* cdec_create(const char* schema_json,
                                  int allow_whitespace,
                                  int require_property_order,
                                  char* err,
                                  size_t err_len);

CDEC_API void cdec_destroy(cdec_engine* engine);

/* Installs the tokenizer vocabulary. Tokens are supplied as one concatenated
 * blob plus per-token byte lengths, which avoids marshalling an array of
 * pointers. Returns 0 on success. */
CDEC_API int cdec_set_vocab(cdec_engine* engine,
                            const char* blob,
                            const int32_t* lengths,
                            int32_t count,
                            int32_t eos_token_id);

CDEC_API int32_t cdec_start_state(const cdec_engine* engine);
CDEC_API int32_t cdec_advance_token(cdec_engine* engine, int32_t state, int32_t token_id);
CDEC_API int32_t cdec_advance_bytes(const cdec_engine* engine, int32_t state, const char* bytes, int32_t len);
CDEC_API int cdec_is_accepting(const cdec_engine* engine, int32_t state);
CDEC_API int cdec_eos_allowed(const cdec_engine* engine, int32_t state);
CDEC_API int cdec_matches(const cdec_engine* engine, const char* text, int32_t len);

/* Writes the allow-mask for `state` into `words` (which must hold
 * cdec_mask_words entries). Returns the number of allowed tokens, or -1. */
CDEC_API int32_t cdec_mask(cdec_engine* engine, int32_t state, uint64_t* words, int32_t word_count);
CDEC_API int32_t cdec_mask_words(const cdec_engine* engine);

/* Applies a previously fetched mask to a logit array in place. `word_count` is
 * the length of `words`, which is NOT derivable from `count`: the logit vector
 * also contains special ids (EOS, padding) that the vocabulary does not. */
CDEC_API void cdec_apply_mask(const uint64_t* words,
                              int32_t word_count,
                              float* logits,
                              int32_t count,
                              int eos_allowed,
                              int32_t eos_token_id);

CDEC_API int32_t cdec_dfa_states(const cdec_engine* engine);
CDEC_API int32_t cdec_dfa_states_premin(const cdec_engine* engine);
CDEC_API int32_t cdec_nfa_states(const cdec_engine* engine);
CDEC_API int32_t cdec_cached_states(const cdec_engine* engine);
CDEC_API int32_t cdec_trie_nodes(const cdec_engine* engine);

/* Copies newline-separated compiler diagnostics into `buf`; returns the number
 * of bytes that would be needed. */
CDEC_API int32_t cdec_diagnostics(const cdec_engine* engine, char* buf, int32_t len);

CDEC_API void cdec_mask_stats(const cdec_engine* engine,
                              uint64_t* lookups,
                              uint64_t* misses,
                              uint64_t* trie_nodes_visited,
                              uint64_t* compute_nanos);
CDEC_API void cdec_reset_stats(cdec_engine* engine);

/* Independent validity oracle: is `text` well-formed JSON? */
CDEC_API int cdec_valid_json(const char* text, int32_t len);

CDEC_API const char* cdec_version(void);

#ifdef __cplusplus
}
#endif

#endif /* CDEC_CAPI_H */
