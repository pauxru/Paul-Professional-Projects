// A deliberately small regular-expression compiler, used only for JSON Schema
// `pattern` constraints.
//
// The subset is: alternation, concatenation, `*` `+` `?` `{n}` `{n,}` `{n,m}`,
// groups, character classes (with ranges and negation), the escapes
// `\d \D \w \W \s \S` plus escaped literals, and `.`.
//
// Two deliberate restrictions, both because the result is embedded inside a
// JSON string literal:
//   * patterns are implicitly anchored (a JSON Schema `pattern` is not, but an
//     unanchored pattern is meaningless as a generator);
//   * the alphabet excludes bytes that would have to be JSON-escaped, so a
//     matching byte string is always literally emittable between quotes.
#pragma once

#include <string>

#include "automaton.hpp"

namespace cdec {

// Compiles `pattern` into an NFA fragment in `builder`.
// Returns false and sets `error` if the pattern uses unsupported syntax.
bool compileRegex(NfaBuilder& builder, const std::string& pattern, Frag& out, std::string& error);

}  // namespace cdec
