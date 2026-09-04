// Compiles a JSON Schema subset into a byte-level DFA.
//
// The central claim of this project is that a JSON Schema with statically
// bounded nesting describes a *regular* language, so a finite automaton can
// recognise it exactly. Unbounded recursion (a `$ref` back to an ancestor)
// escapes that class - it is context-free, not regular - and is therefore
// rejected rather than silently approximated. See docs/decisions/ADR-001.
#pragma once

#include <string>
#include <vector>

#include "automaton.hpp"
#include "json.hpp"

namespace cdec {

struct CompileOptions {
    // Permit insignificant whitespace between structural tokens. Off by
    // default: it materially enlarges the automaton and models never need it.
    bool allowWhitespace = false;

    // When false, object keys may appear in any order, which is what JSON
    // actually permits. The expansion is sum over subsets S of |S|!, so it is
    // capped; on overflow the compiler falls back to declaration order and
    // records a diagnostic.
    bool requirePropertyOrder = false;
    size_t maxObjectAlternatives = 720;

    // JSON strings, arrays and numbers are unbounded in general. A finite
    // automaton needs bounds, so these defaults apply when the schema does not
    // give one. They are recorded in diagnostics so the caller knows the
    // language was narrowed.
    int defaultMaxStringLength = 64;
    int defaultMaxItems = 8;
    int defaultMaxIntegerDigits = 15;
    int defaultMaxFractionDigits = 6;
    int maxNestingDepth = 16;
};

struct CompiledSchema {
    Dfa dfa;
    size_t nfaStates = 0;
    int dfaStatesBeforeMinimisation = 0;
    std::vector<std::string> diagnostics;
};

bool compileSchema(const JsonPtr& schema,
                   const CompileOptions& options,
                   CompiledSchema& out,
                   std::string& error);

// Convenience wrapper that parses the schema text first.
bool compileSchemaText(const std::string& schemaText,
                       const CompileOptions& options,
                       CompiledSchema& out,
                       std::string& error);

}  // namespace cdec
