#include "automaton.hpp"

#include <algorithm>
#include <map>
#include <queue>
#include <set>
#include <unordered_map>

namespace cdec {

int NfaBuilder::newState() {
    states_.emplace_back();
    return static_cast<int>(states_.size()) - 1;
}

Frag NfaBuilder::empty() {
    int s = newState();
    int a = newState();
    states_[s].eps.push_back(a);
    return {s, a};
}

Frag NfaBuilder::range(int lo, int hi) {
    int s = newState();
    int a = newState();
    states_[s].edges.push_back({lo, hi, a});
    return {s, a};
}

Frag NfaBuilder::literal(const std::string& text) {
    int s = newState();
    int cur = s;
    for (unsigned char c : text) {
        int nxt = newState();
        states_[cur].edges.push_back({c, c, nxt});
        cur = nxt;
    }
    return {s, cur};
}

Frag NfaBuilder::concat(Frag a, Frag b) {
    states_[a.accept].eps.push_back(b.start);
    return {a.start, b.accept};
}

Frag NfaBuilder::concatAll(const std::vector<Frag>& parts) {
    if (parts.empty()) return empty();
    Frag acc = parts[0];
    for (size_t i = 1; i < parts.size(); ++i) acc = concat(acc, parts[i]);
    return acc;
}

Frag NfaBuilder::alt(const std::vector<Frag>& options) {
    if (options.empty()) {
        // The empty language: a start state with no way out.
        int s = newState();
        int a = newState();
        return {s, a};
    }
    int s = newState();
    int a = newState();
    for (const Frag& f : options) {
        states_[s].eps.push_back(f.start);
        states_[f.accept].eps.push_back(a);
    }
    return {s, a};
}

Frag NfaBuilder::opt(Frag f) {
    int s = newState();
    int a = newState();
    states_[s].eps.push_back(f.start);
    states_[s].eps.push_back(a);
    states_[f.accept].eps.push_back(a);
    return {s, a};
}

Frag NfaBuilder::star(const Factory& make) {
    int s = newState();
    int a = newState();
    Frag f = make();
    states_[s].eps.push_back(f.start);
    states_[s].eps.push_back(a);
    states_[f.accept].eps.push_back(f.start);
    states_[f.accept].eps.push_back(a);
    return {s, a};
}

Frag NfaBuilder::plus(const Factory& make) {
    Frag first = make();
    Frag rest = star(make);
    return concat(first, rest);
}

Frag NfaBuilder::repeat(const Factory& make, int minCount, int maxCount) {
    std::vector<Frag> parts;
    for (int i = 0; i < minCount; ++i) parts.push_back(make());
    if (maxCount < 0) {
        parts.push_back(star(make));
    } else {
        // Nested options so that stopping early is always legal:
        // (x (x (x)?)?)?
        int optional = maxCount - minCount;
        if (optional > 0) {
            std::vector<Frag> chain;
            for (int i = 0; i < optional; ++i) chain.push_back(make());
            Frag acc = opt(chain[optional - 1]);
            for (int i = optional - 2; i >= 0; --i) acc = opt(concat(chain[i], acc));
            parts.push_back(acc);
        }
    }
    if (parts.empty()) return empty();
    return concatAll(parts);
}

namespace {

using StateSet = std::vector<int>;

void closureInto(const std::vector<NfaState>& states, StateSet& set) {
    std::vector<int> stack(set.begin(), set.end());
    std::set<int> seen(set.begin(), set.end());
    while (!stack.empty()) {
        int s = stack.back();
        stack.pop_back();
        for (int t : states[static_cast<size_t>(s)].eps) {
            if (seen.insert(t).second) stack.push_back(t);
        }
    }
    set.assign(seen.begin(), seen.end());
}

}  // namespace

Dfa determinize(const NfaBuilder& builder, Frag whole) {
    const std::vector<NfaState>& nfa = builder.states();

    StateSet startSet{whole.start};
    closureInto(nfa, startSet);

    std::map<StateSet, int> index;
    std::vector<StateSet> subsets;
    std::queue<int> work;

    index[startSet] = 0;
    subsets.push_back(startSet);
    work.push(0);

    std::vector<std::vector<int32_t>> trans;
    std::vector<uint8_t> accept;

    while (!work.empty()) {
        int id = work.front();
        work.pop();
        if (static_cast<int>(trans.size()) <= id) {
            trans.resize(static_cast<size_t>(id) + 1);
            accept.resize(static_cast<size_t>(id) + 1, 0);
        }
        trans[static_cast<size_t>(id)].assign(kAlphabet, kDead);

        const StateSet current = subsets[static_cast<size_t>(id)];
        accept[static_cast<size_t>(id)] =
            std::find(current.begin(), current.end(), whole.accept) != current.end() ? 1 : 0;

        for (int b = 0; b < kAlphabet; ++b) {
            StateSet move;
            for (int s : current) {
                for (const NfaEdge& e : nfa[static_cast<size_t>(s)].edges) {
                    if (b >= e.lo && b <= e.hi) move.push_back(e.target);
                }
            }
            if (move.empty()) continue;
            std::sort(move.begin(), move.end());
            move.erase(std::unique(move.begin(), move.end()), move.end());
            closureInto(nfa, move);

            auto it = index.find(move);
            int target;
            if (it == index.end()) {
                target = static_cast<int>(subsets.size());
                index[move] = target;
                subsets.push_back(move);
                work.push(target);
            } else {
                target = it->second;
            }
            trans[static_cast<size_t>(id)][static_cast<size_t>(b)] = target;
        }
    }

    Dfa dfa;
    dfa.startState = 0;
    dfa.stateCount = static_cast<int>(trans.size());
    dfa.accepting = accept;
    dfa.transitions.resize(static_cast<size_t>(dfa.stateCount) * kAlphabet, kDead);
    for (int s = 0; s < dfa.stateCount; ++s) {
        for (int b = 0; b < kAlphabet; ++b) {
            dfa.transitions[static_cast<size_t>(s) * kAlphabet + b] = trans[static_cast<size_t>(s)][static_cast<size_t>(b)];
        }
    }
    return dfa;
}

Dfa minimize(const Dfa& input) {
    const int n = input.stateCount;
    if (n == 0) return input;

    // Drop states from which no accepting state is reachable. Without this the
    // mask cache would hold entries for states that can never produce output.
    std::vector<std::vector<int>> reverse(static_cast<size_t>(n));
    for (int s = 0; s < n; ++s) {
        for (int b = 0; b < kAlphabet; ++b) {
            int t = input.transitions[static_cast<size_t>(s) * kAlphabet + b];
            if (t != kDead) reverse[static_cast<size_t>(t)].push_back(s);
        }
    }
    std::vector<uint8_t> productive(static_cast<size_t>(n), 0);
    std::vector<int> stack;
    for (int s = 0; s < n; ++s) {
        if (input.accepting[static_cast<size_t>(s)]) { productive[static_cast<size_t>(s)] = 1; stack.push_back(s); }
    }
    while (!stack.empty()) {
        int s = stack.back();
        stack.pop_back();
        for (int p : reverse[static_cast<size_t>(s)]) {
            if (!productive[static_cast<size_t>(p)]) { productive[static_cast<size_t>(p)] = 1; stack.push_back(p); }
        }
    }

    // A virtual dead class (index n) makes the transition function total, which
    // keeps the refinement loop simple.
    const int dead = n;
    std::vector<int> partition(static_cast<size_t>(n) + 1);
    for (int s = 0; s < n; ++s) {
        if (!productive[static_cast<size_t>(s)]) partition[static_cast<size_t>(s)] = 2;
        else partition[static_cast<size_t>(s)] = input.accepting[static_cast<size_t>(s)] ? 0 : 1;
    }
    partition[static_cast<size_t>(dead)] = 2;

    auto target = [&](int s, int b) -> int {
        if (s == dead) return dead;
        int t = input.transitions[static_cast<size_t>(s) * kAlphabet + b];
        if (t == kDead || !productive[static_cast<size_t>(t)]) return dead;
        return t;
    };

    bool changed = true;
    while (changed) {
        changed = false;
        std::map<std::pair<int, std::vector<int>>, int> signatures;
        std::vector<int> next(static_cast<size_t>(n) + 1);
        int nextId = 0;
        for (int s = 0; s <= n; ++s) {
            std::vector<int> sig(kAlphabet);
            for (int b = 0; b < kAlphabet; ++b) sig[static_cast<size_t>(b)] = partition[static_cast<size_t>(target(s, b))];
            auto key = std::make_pair(partition[static_cast<size_t>(s)], sig);
            auto it = signatures.find(key);
            if (it == signatures.end()) {
                signatures[key] = nextId;
                next[static_cast<size_t>(s)] = nextId;
                ++nextId;
            } else {
                next[static_cast<size_t>(s)] = it->second;
            }
        }
        if (next != partition) { partition = next; changed = true; }
    }

    const int deadClass = partition[static_cast<size_t>(dead)];
    std::unordered_map<int, int> remap;
    int count = 0;
    // The start state is emitted first so that startState is always 0.
    int startClass = partition[static_cast<size_t>(input.startState)];
    if (startClass != deadClass) remap[startClass] = count++;
    for (int s = 0; s < n; ++s) {
        int c = partition[static_cast<size_t>(s)];
        if (c == deadClass) continue;
        if (!remap.count(c)) remap[c] = count++;
    }

    Dfa out;
    out.stateCount = count;
    out.startState = remap.count(startClass) ? remap[startClass] : 0;
    out.transitions.assign(static_cast<size_t>(count) * kAlphabet, kDead);
    out.accepting.assign(static_cast<size_t>(count), 0);
    if (count == 0) { out.stateCount = 1; out.transitions.assign(kAlphabet, kDead); out.accepting.assign(1, 0); return out; }

    for (int s = 0; s < n; ++s) {
        int c = partition[static_cast<size_t>(s)];
        if (c == deadClass) continue;
        int mapped = remap[c];
        if (input.accepting[static_cast<size_t>(s)]) out.accepting[static_cast<size_t>(mapped)] = 1;
        for (int b = 0; b < kAlphabet; ++b) {
            int t = target(s, b);
            if (t == dead) continue;
            int tc = partition[static_cast<size_t>(t)];
            if (tc == deadClass) continue;
            out.transitions[static_cast<size_t>(mapped) * kAlphabet + b] = remap[tc];
        }
    }
    return out;
}

Dfa compileNfa(const NfaBuilder& builder, Frag whole) {
    return minimize(determinize(builder, whole));
}

void NfaBuilder::addEdge(int from, int lo, int hi, int target) {
    states_[static_cast<size_t>(from)].edges.push_back(NfaEdge{lo, hi, target});
}

void NfaBuilder::addEpsilon(int from, int target) {
    states_[static_cast<size_t>(from)].eps.push_back(target);
}

Dfa intersect(const Dfa& a, const Dfa& b) {
    // Standard product construction, explored lazily from the start pair so the
    // result contains only reachable products rather than |A| * |B| states.
    std::map<std::pair<int, int>, int> index;
    std::vector<std::pair<int, int>> order;
    auto idOf = [&](int x, int y) {
        auto key = std::make_pair(x, y);
        auto it = index.find(key);
        if (it != index.end()) return it->second;
        int id = static_cast<int>(order.size());
        index.emplace(key, id);
        order.push_back(key);
        return id;
    };

    Dfa out;
    out.startState = idOf(a.startState, b.startState);
    std::vector<std::vector<int32_t>> rows;
    std::vector<uint8_t> accept;
    for (size_t i = 0; i < order.size(); ++i) {
        auto [x, y] = order[i];
        std::vector<int32_t> row(kAlphabet, kDead);
        for (int c = 0; c < kAlphabet; ++c) {
            int nx = a.step(x, static_cast<unsigned char>(c));
            if (nx == kDead) continue;
            int ny = b.step(y, static_cast<unsigned char>(c));
            if (ny == kDead) continue;
            row[static_cast<size_t>(c)] = idOf(nx, ny);
        }
        rows.push_back(std::move(row));
        accept.push_back(a.isAccepting(x) && b.isAccepting(y) ? 1 : 0);
    }

    out.stateCount = static_cast<int>(rows.size());
    out.transitions.assign(static_cast<size_t>(out.stateCount) * kAlphabet, kDead);
    out.accepting = std::move(accept);
    for (size_t i = 0; i < rows.size(); ++i) {
        std::copy(rows[i].begin(), rows[i].end(),
                  out.transitions.begin() + static_cast<ptrdiff_t>(i * kAlphabet));
    }
    // Minimising here also removes states from which nothing is accepted, which
    // an intersection produces in abundance.
    return minimize(out);
}

Frag embed(NfaBuilder& builder, const Dfa& dfa) {
    std::vector<int> states(static_cast<size_t>(dfa.stateCount));
    for (int i = 0; i < dfa.stateCount; ++i) states[static_cast<size_t>(i)] = builder.newState();
    int accept = builder.newState();
    for (int s = 0; s < dfa.stateCount; ++s) {
        // Coalesce consecutive bytes with the same target into one range edge;
        // 256 singleton edges per state would make epsilon closure needlessly
        // expensive during the outer determinisation.
        int runStart = -1;
        int runTarget = kDead;
        for (int c = 0; c <= kAlphabet; ++c) {
            int target = c < kAlphabet ? dfa.step(s, static_cast<unsigned char>(c)) : kDead;
            if (target != runTarget) {
                if (runTarget != kDead) {
                    builder.addEdge(states[static_cast<size_t>(s)], runStart, c - 1,
                                    states[static_cast<size_t>(runTarget)]);
                }
                runStart = c;
                runTarget = target;
            }
        }
        if (dfa.isAccepting(s)) builder.addEpsilon(states[static_cast<size_t>(s)], accept);
    }
    return Frag{states[static_cast<size_t>(dfa.startState)], accept};
}

}  // namespace cdec
