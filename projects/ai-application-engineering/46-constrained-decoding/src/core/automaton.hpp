// Byte-level finite automata.
//
// A schema is compiled to an NFA by Thompson construction over byte ranges,
// determinised by subset construction, and then minimised. Working at the byte
// level (rather than over Unicode code points) is deliberate: LLM tokenizers
// emit byte strings, so aligning the automaton to the tokenizer is only exact
// if the automaton's alphabet is also bytes. UTF-8 well-formedness is itself a
// regular language, so nothing is lost by doing this.
#pragma once

#include <cstdint>
#include <functional>
#include <string>
#include <vector>

namespace cdec {

constexpr int kDead = -1;
constexpr int kAlphabet = 256;

struct NfaEdge {
    int lo;      // inclusive
    int hi;      // inclusive
    int target;
};

struct NfaState {
    std::vector<NfaEdge> edges;
    std::vector<int> eps;
};

// A fragment is a sub-automaton with a single entry and a single exit. Because
// fragments share the builder's state pool they must never be reused after
// being combined; helpers that need repetition take a factory instead.
struct Frag {
    int start;
    int accept;
};

class NfaBuilder {
public:
    using Factory = std::function<Frag()>;

    int newState();
    Frag empty();                       // matches the empty string
    Frag range(int lo, int hi);         // single byte in [lo, hi]
    Frag byte(int value) { return range(value, value); }
    Frag literal(const std::string& text);
    Frag concat(Frag a, Frag b);
    Frag concatAll(const std::vector<Frag>& parts);
    Frag alt(const std::vector<Frag>& options);
    Frag opt(Frag f);
    Frag star(const Factory& make);
    Frag plus(const Factory& make);
    // Repetition with an inclusive lower bound and an optional upper bound.
    // `maxCount < 0` means unbounded.
    Frag repeat(const Factory& make, int minCount, int maxCount);

    const std::vector<NfaState>& states() const { return states_; }
    size_t stateCount() const { return states_.size(); }

    // Raw edge insertion. Used only by `embed`, which needs to reproduce an
    // arbitrary transition table rather than a Thompson-shaped fragment.
    void addEdge(int from, int lo, int hi, int target);
    void addEpsilon(int from, int target);

private:
    std::vector<NfaState> states_;
};

struct Dfa {
    int startState = 0;
    int stateCount = 0;
    std::vector<int32_t> transitions;  // stateCount * 256, kDead when absent
    std::vector<uint8_t> accepting;

    int step(int state, unsigned char byte) const {
        if (state < 0) return kDead;
        return transitions[static_cast<size_t>(state) * kAlphabet + byte];
    }

    // Runs every byte of `text`, returning kDead as soon as the input leaves
    // the language prefix set.
    int run(int state, const std::string& text) const {
        for (unsigned char c : text) {
            state = step(state, c);
            if (state == kDead) return kDead;
        }
        return state;
    }

    bool isAccepting(int state) const {
        return state >= 0 && accepting[static_cast<size_t>(state)] != 0;
    }

    bool matches(const std::string& text) const { return isAccepting(run(startState, text)); }
};

// Subset construction. `nfaAccept` is the accepting state of the whole machine.
Dfa determinize(const NfaBuilder& builder, Frag whole);

// Moore partition refinement. Also drops states that cannot reach an accepting
// state, which matters here: unreachable/dead states would otherwise inflate
// the per-state mask cache.
Dfa minimize(const Dfa& dfa);

Dfa compileNfa(const NfaBuilder& builder, Frag whole);

// Product construction: the language accepted by both machines. Needed because
// JSON Schema conjoins constraints (`pattern` AND `maxLength`) while Thompson
// construction only offers union and concatenation. Silently honouring one and
// dropping the other would make the automaton accept documents the schema
// rejects, which is the one failure mode this design exists to prevent.
Dfa intersect(const Dfa& a, const Dfa& b);

// Splices a DFA back into a builder as a single-entry, single-exit fragment, so
// the result of an intersection can be composed with the rest of the schema.
Frag embed(NfaBuilder& builder, const Dfa& dfa);

}  // namespace cdec
