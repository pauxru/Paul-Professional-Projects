#include "mask.hpp"

#include <algorithm>
#include <chrono>
#include <limits>

namespace cdec {

Vocabulary::Vocabulary(std::vector<std::string> tokens) : tokens_(std::move(tokens)) {
    trie_.emplace_back();  // root
    for (size_t id = 0; id < tokens_.size(); ++id) {
        const std::string& t = tokens_[id];
        int node = 0;
        for (unsigned char c : t) {
            auto& kids = trie_[static_cast<size_t>(node)].children;
            auto it = std::lower_bound(kids.begin(), kids.end(), c,
                                       [](const std::pair<uint8_t, int>& p, unsigned char v) { return p.first < v; });
            if (it != kids.end() && it->first == c) {
                node = it->second;
            } else {
                int created = static_cast<int>(trie_.size());
                trie_.emplace_back();
                // `kids` may dangle after the emplace_back above reallocates.
                auto& kidsAfter = trie_[static_cast<size_t>(node)].children;
                auto pos = std::lower_bound(kidsAfter.begin(), kidsAfter.end(), c,
                                            [](const std::pair<uint8_t, int>& p, unsigned char v) { return p.first < v; });
                kidsAfter.insert(pos, {c, created});
                node = created;
            }
        }
        // An empty token would make the root itself a token end, which would
        // permit infinite loops during decoding; such tokens are dropped.
        if (!t.empty()) trie_[static_cast<size_t>(node)].tokenId = static_cast<int>(id);
    }
}

TokenMaskCache::TokenMaskCache(const Dfa& dfa, const Vocabulary& vocab)
    : dfa_(dfa), vocab_(vocab), words_((vocab.size() + 63) / 64) {}

size_t TokenMaskCache::countBits(const TokenMask& mask) {
    size_t total = 0;
    for (uint64_t w : mask) {
        while (w) { w &= w - 1; ++total; }
    }
    return total;
}

TokenMask TokenMaskCache::compute(int state) {
    TokenMask mask(words_, 0ULL);
    const std::vector<Vocabulary::TrieNode>& trie = vocab_.trie();

    // Explicit stack rather than recursion: vocabularies contain long tokens
    // and this runs on the decode hot path.
    struct Item { int node; int dfaState; };
    std::vector<Item> stack;
    stack.push_back({0, state});

    while (!stack.empty()) {
        Item item = stack.back();
        stack.pop_back();
        ++stats_.trieNodesVisited;

        const Vocabulary::TrieNode& node = trie[static_cast<size_t>(item.node)];
        if (node.tokenId >= 0) {
            size_t id = static_cast<size_t>(node.tokenId);
            mask[id >> 6] |= 1ULL << (id & 63);
        }
        for (const auto& [byte, child] : node.children) {
            int next = dfa_.step(item.dfaState, byte);
            if (next == kDead) continue;  // prunes the whole subtree
            stack.push_back({child, next});
        }
    }
    return mask;
}

const TokenMask& TokenMaskCache::maskFor(int state) {
    ++stats_.lookups;
    auto it = cache_.find(state);
    if (it != cache_.end()) return it->second;

    ++stats_.misses;
    auto start = std::chrono::steady_clock::now();
    TokenMask mask = compute(state);
    stats_.computeNanos += static_cast<uint64_t>(
        std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - start).count());
    return cache_.emplace(state, std::move(mask)).first->second;
}

void applyMask(const TokenMask& mask, float* logits, size_t count, bool eosAllowed, int eosTokenId) {
    const float negInf = -std::numeric_limits<float>::infinity();
    // The logit vector is longer than the vocabulary whenever the model has
    // special ids (EOS, padding) that the tokenizer trie does not contain.
    // Anything past the last mask bit is disallowed by construction; reading
    // past the mask instead would be an out-of-bounds read whose likelihood
    // depends on whether the vocabulary size is a multiple of 64.
    const size_t bits = mask.size() * 64;
    for (size_t i = 0; i < count; ++i) {
        bool allowed = i < bits && ((mask[i >> 6] >> (i & 63)) & 1ULL);
        if (static_cast<int>(i) == eosTokenId) allowed = eosAllowed;
        if (!allowed) logits[i] = negInf;
    }
}

}  // namespace cdec
