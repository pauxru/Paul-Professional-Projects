package lexicon

import (
	"strings"
	"testing"
)

func TestCanonIsIdempotent(t *testing.T) {
	// Canonicalising a canonical form must be a no-op, or tokenisation would
	// depend on how many times it happened to run.
	for surface := range table {
		c := Canon(surface)
		if got := Canon(c); got != c {
			t.Errorf("Canon(%q) = %q, but Canon(%q) = %q; the table has a chain",
				surface, c, c, got)
		}
	}
}

func TestCanonIsTotalAndNeverEmpty(t *testing.T) {
	for _, tok := range []string{"refund", "zzzznotaword", "germany", "", "401"} {
		if tok == "" {
			continue
		}
		if Canon(tok) == "" {
			t.Errorf("Canon(%q) returned empty", tok)
		}
	}
}

func TestUnknownTokensPassThroughUnchanged(t *testing.T) {
	const tok = "quixotically"
	if got := Canon(tok); got != tok {
		t.Errorf("Canon(%q) = %q; unknown tokens must pass through so the lexicon can only ADD synonymy",
			tok, got)
	}
}

// This is the integrity test for the entire method.
//
// The lexicon exists to supply synonymy - to make "money back" and "refund"
// the same concept - so that the similarity function measures meaning rather
// than string overlap. It must not smuggle in the answer.
//
// Writing this test forced the property to be stated precisely, and the first
// attempt was wrong. Banning every decisive token outright also bans
// "activate" -> "enable", which is legitimate synonymy: the decisive token is
// the CANONICAL form there, not a hint about which words matter. The property
// that actually matters splits in two.
//
// Class A - tokens with no legitimate synonym at all. A proper noun, a plan
// name and an HTTP status code mean themselves and nothing else. Any entry
// touching them, on either side, is the author telling the embedder the answer.
func TestNoLexiconEntryTouchesAnIrreducibleToken(t *testing.T) {
	irreducible := []string{
		"germany", "france", "us", "usa", "uk", "ireland", // entities
		"free", "pro", "enterprise", "basic", "premium", // plan names
		"401", "429", "403", "500", // status codes
		"acme", "globex", "initech", // tenants
		"on", "off", "not", // polarity particles
	}
	for _, tok := range irreducible {
		if v, ok := table[tok]; ok {
			t.Errorf("irreducible token %q is a lexicon KEY (-> %q); it has no synonyms "+
				"and any entry for it encodes the answer in the instrument", tok, v)
		}
		for k, v := range table {
			if v == tok {
				t.Errorf("irreducible token %q is a lexicon VALUE (%q -> %q); same problem",
					tok, k, v)
			}
		}
	}
}

// Class B - tokens that MAY be canonical forms, because they have real
// synonyms, but must never collapse with a sibling from their own confusable
// family. "activate" -> "enable" is synonymy. "remove" -> "disable" was a bug:
// removing a teammate and disabling 2FA are different intents in different
// families, and collapsing the verbs manufactures the very confusion this
// project measures. This test found it.
func TestFamilySiblingsNeverCollapseToTheSameConcept(t *testing.T) {
	families := [][]string{
		{"enable", "disable"},
		{"import", "export"},
		{"upgrade", "downgrade"},
		{"add", "remove"},
		{"increase", "decrease"},
		{"germany", "france", "us"},
		{"free", "pro", "enterprise"},
		{"cancel", "enable"},
		{"remove", "disable"},
		{"delete", "export"},
		{"password", "key"},
	}
	for _, fam := range families {
		for i := 0; i < len(fam); i++ {
			for j := i + 1; j < len(fam); j++ {
				if Canon(fam[i]) == Canon(fam[j]) {
					t.Errorf("%q and %q both canonicalise to %q, but they distinguish "+
						"two intents the cache must not confuse",
						fam[i], fam[j], Canon(fam[i]))
				}
			}
		}
	}
}

// Phrases are the same hazard with more room to hide. Matched on whole words:
// "sub processors" legitimately contains the letters of "pro".
func TestNoPhraseRuleTouchesAnIrreducibleToken(t *testing.T) {
	irreducible := map[string]bool{
		"germany": true, "france": true, "free": true, "pro": true,
		"enterprise": true, "401": true, "429": true, "on": true, "off": true,
	}
	for k, v := range phrases {
		for _, word := range append(strings.Fields(k), strings.Fields(v)...) {
			if irreducible[word] {
				t.Errorf("phrase rule %q -> %q uses the irreducible token %q", k, v, word)
			}
		}
	}
}

// The synonymy the lexicon DOES supply has to actually work, or the embedding
// is a lexical matcher wearing a hat.
func TestGenuineSynonymsCollapse(t *testing.T) {
	pairs := [][2]string{
		{"refund", "refund"},
		{"cancel", "cancel"},
	}
	for _, p := range pairs {
		if Canon(p[0]) != Canon(p[1]) {
			t.Errorf("%q and %q should share a concept", p[0], p[1])
		}
	}
	// At least some entries must be non-trivial, or the table is decoration.
	nontrivial := 0
	for k, v := range table {
		if k != v {
			nontrivial++
		}
	}
	if nontrivial < 50 {
		t.Errorf("only %d non-trivial synonym mappings; the lexicon is not doing enough work "+
			"to justify calling the similarity semantic", nontrivial)
	}
}

func TestCanonPhraseOnlyFiresOnKnownPhrases(t *testing.T) {
	if _, ok := CanonPhrase("zzz", "qqq"); ok {
		t.Error("CanonPhrase fired on an unknown pair")
	}
	fired := 0
	for k := range phrases {
		parts := strings.SplitN(k, " ", 2)
		if len(parts) != 2 {
			continue
		}
		if _, ok := CanonPhrase(parts[0], parts[1]); ok {
			fired++
		}
	}
	if fired == 0 {
		t.Error("no phrase rule fires; the phrase table is unreachable")
	}
}

func TestSizeCountsBothTables(t *testing.T) {
	if Size() != len(table)+len(phrases) {
		t.Errorf("Size() = %d, want %d", Size(), len(table)+len(phrases))
	}
	if Size() < 100 {
		t.Errorf("lexicon has only %d entries", Size())
	}
}
