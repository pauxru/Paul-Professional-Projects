// Tokenizer alignment: turning "which byte strings are legal here" into
// "which token ids are legal here".
//
// This is the part naive implementations get wrong. A language model does not
// emit bytes, it emits tokens, and a token is an arbitrary multi-byte string.
// A token is legal in DFA state s exactly when running its bytes from s never
// enters the dead state. Doing that per token is O(sum of token lengths) for
// every step; doing it over a trie shares work across common prefixes and
// prunes whole subtrees the moment the automaton dies.
//
// The second observation is that the mask depends *only* on the DFA state, not
// on the history that reached it, so it can be cached. Both effects are
// measured in bench/.
#pragma once

#include <cstdint>
#include <string>
#include <unordered_map>
#include <vector>

#include "automaton.hpp"

namespace cdec {

class Vocabulary {
public:
    explicit Vocabulary(std::vector<std::string> tokens);

    size_t size() const { return tokens_.size(); }
    const std::string& token(size_t id) const { return tokens_[id]; }
    const std::vector<std::string>& tokens() const { return tokens_; }

    struct TrieNode {
        // Sorted (byte, childIndex) pairs. A dense 256-way node would cost a
        // kilobyte per node, which is prohibitive for a realistic vocabulary.
        std::vector<std::pair<uint8_t, int>> children;
        int tokenId = -1;
    };

    const std::vector<TrieNode>& trie() const { return trie_; }
    size_t trieNodeCount() const { return trie_.size(); }

private:
    std::vector<std::string> tokens_;
    std::vector<TrieNode> trie_;
};

struct MaskStats {
    uint64_t lookups = 0;
    uint64_t misses = 0;        // states whose mask had to be computed
    uint64_t trieNodesVisited = 0;
    uint64_t computeNanos = 0;
};

// A bitset over token ids. Bit i set means token i is legal.
using TokenMask = std::vector<uint64_t>;

class TokenMaskCache {
public:
    TokenMaskCache(const Dfa& dfa, const Vocabulary& vocab);

    // Legal tokens in `state`. Cached per DFA state.
    const TokenMask& maskFor(int state);

    // The end-of-sequence token may only be emitted from an accepting state,
    // otherwise the model could stop halfway through a valid document.
    bool eosAllowed(int state) const { return dfa_.isAccepting(state); }

    // Advances the automaton by a token, returning kDead if it is illegal.
    int advance(int state, size_t tokenId) const {
        return dfa_.run(state, vocab_.token(tokenId));
    }

    size_t maskWords() const { return words_; }
    const MaskStats& stats() const { return stats_; }
    void resetStats() { stats_ = MaskStats(); }
    size_t cachedStates() const { return cache_.size(); }

    static bool testBit(const TokenMask& mask, size_t id) {
        return (mask[id >> 6] >> (id & 63)) & 1ULL;
    }
    static size_t countBits(const TokenMask& mask);

private:
    TokenMask compute(int state);

    const Dfa& dfa_;
    const Vocabulary& vocab_;
    size_t words_;
    std::unordered_map<int, TokenMask> cache_;
    MaskStats stats_;
};

// Applies a mask to a logit vector in place, setting illegal entries to
// negative infinity. Separated from the cache so it can be benchmarked alone.
void applyMask(const TokenMask& mask, float* logits, size_t count, bool eosAllowed, int eosTokenId);

}  // namespace cdec
