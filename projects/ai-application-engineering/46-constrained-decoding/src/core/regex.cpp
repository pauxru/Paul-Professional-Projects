#include "regex.hpp"

#include <vector>

namespace cdec {
namespace {

struct ByteRange {
    int lo;
    int hi;
};

// Bytes that are legal inside a JSON string literal without escaping.
// 0x22 (") and 0x5C (\) are excluded, as are control bytes below 0x20.
std::vector<ByteRange> jsonSafeAny() {
    return {{0x20, 0x21}, {0x23, 0x5B}, {0x5D, 0x7E}};
}

class RegexParser {
public:
    RegexParser(NfaBuilder& builder, const std::string& pattern)
        : b_(builder), p_(pattern) {}

    bool parse(Frag& out) {
        // Implicit anchoring: leading '^' and trailing '$' are accepted and
        // ignored, anything else anchored mid-pattern is rejected.
        if (!p_.empty() && p_[0] == '^') ++i_;
        size_t end = p_.size();
        if (end > i_ && p_[end - 1] == '$') --end;
        end_ = end;

        Frag f;
        if (!parseAlt(f)) return false;
        if (i_ != end_) return fail("unexpected character");
        out = f;
        return true;
    }

    std::string error;

private:
    NfaBuilder& b_;
    const std::string& p_;
    size_t i_ = 0;
    size_t end_ = 0;

    bool fail(const std::string& msg) {
        if (error.empty()) error = "regex: " + msg + " at offset " + std::to_string(i_);
        return false;
    }

    bool eof() const { return i_ >= end_; }
    char peek() const { return p_[i_]; }

    bool parseAlt(Frag& out) {
        std::vector<Frag> options;
        Frag first;
        if (!parseConcat(first)) return false;
        options.push_back(first);
        while (!eof() && peek() == '|') {
            ++i_;
            Frag next;
            if (!parseConcat(next)) return false;
            options.push_back(next);
        }
        out = options.size() == 1 ? options[0] : b_.alt(options);
        return true;
    }

    bool parseConcat(Frag& out) {
        std::vector<Frag> parts;
        while (!eof() && peek() != '|' && peek() != ')') {
            Frag piece;
            if (!parseRepeat(piece)) return false;
            parts.push_back(piece);
        }
        out = parts.empty() ? b_.empty() : b_.concatAll(parts);
        return true;
    }

    // Quantifiers need to rebuild the atom, so the atom source range is
    // captured and re-parsed by the factory.
    bool parseRepeat(Frag& out) {
        size_t atomStart = i_;
        Frag atom;
        if (!parseAtom(atom)) return false;
        size_t atomEnd = i_;

        auto factory = [this, atomStart, atomEnd]() -> Frag {
            RegexParser sub(b_, p_);
            sub.i_ = atomStart;
            sub.end_ = atomEnd;
            Frag f;
            if (!sub.parseAtom(f)) return b_.empty();
            return f;
        };

        if (eof()) { out = atom; return true; }
        char c = peek();
        if (c == '*') { ++i_; out = b_.star(factory); return true; }
        if (c == '+') { ++i_; out = b_.plus(factory); return true; }
        if (c == '?') { ++i_; out = b_.opt(atom); return true; }
        if (c == '{') {
            size_t save = i_;
            ++i_;
            int lo = 0, hi = -1;
            if (!parseInt(lo)) { i_ = save; out = atom; return true; }
            if (!eof() && peek() == ',') {
                ++i_;
                if (!eof() && peek() != '}') { if (!parseInt(hi)) return fail("bad repetition bound"); }
            } else {
                hi = lo;
            }
            if (eof() || peek() != '}') return fail("expected '}'");
            ++i_;
            if (lo < 0 || (hi >= 0 && hi < lo)) return fail("invalid repetition range");
            if ((hi < 0 ? lo : hi) > 1024) return fail("repetition bound too large");
            out = b_.repeat(factory, lo, hi);
            return true;
        }
        out = atom;
        return true;
    }

    bool parseInt(int& out) {
        if (eof() || peek() < '0' || peek() > '9') return false;
        out = 0;
        while (!eof() && peek() >= '0' && peek() <= '9') {
            out = out * 10 + (peek() - '0');
            ++i_;
            if (out > 100000) return false;
        }
        return true;
    }

    Frag fromRanges(const std::vector<ByteRange>& ranges) {
        std::vector<Frag> options;
        options.reserve(ranges.size());
        for (const ByteRange& r : ranges) options.push_back(b_.range(r.lo, r.hi));
        return options.size() == 1 ? options[0] : b_.alt(options);
    }

    static std::vector<ByteRange> classFor(char escape, bool& ok) {
        ok = true;
        switch (escape) {
            case 'd': return {{'0', '9'}};
            case 'w': return {{'0', '9'}, {'A', 'Z'}, {'_', '_'}, {'a', 'z'}};
            case 's': return {{' ', ' '}};  // only space survives JSON-safe filtering
            case 'D': return {{0x20, 0x2F}, {0x3A, 0x5B}, {0x5D, 0x7E}};
            case 'W': return {{0x20, 0x2F}, {0x3A, 0x40}, {0x5B, 0x5B}, {0x5D, 0x5E}, {0x60, 0x60}, {0x7B, 0x7E}};
            case 'S': return {{0x21, 0x21}, {0x23, 0x5B}, {0x5D, 0x7E}};
            default: ok = false; return {};
        }
    }

    bool parseAtom(Frag& out) {
        if (eof()) { out = b_.empty(); return true; }
        char c = peek();
        if (c == '(') {
            ++i_;
            // Non-capturing groups are accepted; capture semantics are
            // irrelevant to a recogniser.
            if (i_ + 2 < end_ && p_[i_] == '?' && p_[i_ + 1] == ':') i_ += 2;
            Frag inner;
            if (!parseAlt(inner)) return false;
            if (eof() || peek() != ')') return fail("expected ')'");
            ++i_;
            out = inner;
            return true;
        }
        if (c == '[') return parseClass(out);
        if (c == '.') { ++i_; out = fromRanges(jsonSafeAny()); return true; }
        if (c == '\\') {
            ++i_;
            if (eof()) return fail("trailing backslash");
            char e = p_[i_++];
            bool ok = false;
            std::vector<ByteRange> cls = classFor(e, ok);
            if (ok) { out = fromRanges(cls); return true; }
            unsigned char lit = static_cast<unsigned char>(e);
            if (lit == '"' || lit == '\\' || lit < 0x20) return fail("pattern may not match a byte requiring JSON escaping");
            out = b_.byte(lit);
            return true;
        }
        if (c == ')' || c == '|' || c == '*' || c == '+' || c == '?') return fail("unexpected metacharacter");
        unsigned char lit = static_cast<unsigned char>(c);
        if (lit == '"' || lit == '\\' || lit < 0x20) return fail("pattern may not match a byte requiring JSON escaping");
        ++i_;
        out = b_.byte(lit);
        return true;
    }

    bool parseClass(Frag& out) {
        ++i_;  // '['
        bool negate = false;
        if (!eof() && peek() == '^') { negate = true; ++i_; }
        std::vector<ByteRange> ranges;
        bool first = true;
        while (!eof() && (peek() != ']' || first)) {
            first = false;
            if (peek() == '\\') {
                ++i_;
                if (eof()) return fail("trailing backslash in class");
                char e = p_[i_++];
                bool ok = false;
                std::vector<ByteRange> cls = classFor(e, ok);
                if (ok) { for (const ByteRange& r : cls) ranges.push_back(r); continue; }
                ranges.push_back({static_cast<unsigned char>(e), static_cast<unsigned char>(e)});
                continue;
            }
            unsigned char lo = static_cast<unsigned char>(p_[i_++]);
            if (!eof() && peek() == '-' && i_ + 1 < end_ && p_[i_ + 1] != ']') {
                ++i_;
                unsigned char hi = static_cast<unsigned char>(p_[i_++]);
                if (hi < lo) return fail("inverted range in class");
                ranges.push_back({lo, hi});
            } else {
                ranges.push_back({lo, lo});
            }
        }
        if (eof() || peek() != ']') return fail("expected ']'");
        ++i_;
        if (ranges.empty()) return fail("empty character class");

        // Intersect (or subtract) against the JSON-safe alphabet so the result
        // is always emittable inside a string literal.
        bool allowed[256] = {false};
        for (const ByteRange& r : jsonSafeAny()) {
            for (int v = r.lo; v <= r.hi; ++v) allowed[v] = true;
        }
        bool selected[256] = {false};
        for (const ByteRange& r : ranges) {
            for (int v = r.lo; v <= r.hi && v < 256; ++v) selected[v] = true;
        }
        std::vector<ByteRange> finalRanges;
        int runStart = -1;
        for (int v = 0; v < 256; ++v) {
            bool take = allowed[v] && (negate ? !selected[v] : selected[v]);
            if (take && runStart < 0) runStart = v;
            if (!take && runStart >= 0) { finalRanges.push_back({runStart, v - 1}); runStart = -1; }
        }
        if (runStart >= 0) finalRanges.push_back({runStart, 255});
        if (finalRanges.empty()) return fail("character class matches nothing that is JSON-safe");
        out = fromRanges(finalRanges);
        return true;
    }
};

}  // namespace

bool compileRegex(NfaBuilder& builder, const std::string& pattern, Frag& out, std::string& error) {
    RegexParser parser(builder, pattern);
    if (!parser.parse(out)) { error = parser.error; return false; }
    return true;
}

}  // namespace cdec
