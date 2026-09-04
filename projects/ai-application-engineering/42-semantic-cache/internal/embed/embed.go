// Package embed is a real text embedder, not a simulator.
//
// This matters. Project 49 in this portfolio simulates a model fleet, and says
// so. Here a simulator would be worthless: the whole question is whether a
// similarity threshold can separate paraphrases from decisive-token variants,
// and an embedding designed by me would answer whatever I designed it to
// answer. So this is a genuine implementation of two classical embeddings whose
// failure modes are properties of the method, not of my intentions:
//
//	Lexical - TF-IDF over hashed unigrams and bigrams, cosine similarity.
//	Dense   - the same, projected to a fixed low-dimensional dense vector with
//	          a signed random projection (Achlioptas), so that similarity is an
//	          aggregate over smeared token contributions in the way a
//	          mean-pooled neural embedding's is.
//
// Neither is a transformer, and docs/known-limitations.md is explicit about
// that. But the structural result this project reports does not depend on which
// embedder you use, and the reason is arithmetic rather than empirical: any
// embedding that aggregates token contributions into one fixed-length vector
// bounds the similarity drop from changing a single token by that token's share
// of the vector's mass. That bound is computed and reported in section 2.
package embed

import (
	"math"
	"sort"
	"strings"

	"semcache/internal/lexicon"
)

// Vector is an L2-normalised embedding, so Cosine is a dot product.
type Vector []float64

// Cosine similarity. Both arguments are assumed normalised by Embed.
func Cosine(a, b Vector) float64 {
	if len(a) != len(b) {
		return 0
	}
	s := 0.0
	for i := range a {
		s += a[i] * b[i]
	}
	return clamp(s, -1, 1)
}

// Kind selects which embedding a Model produces.
type Kind int

const (
	// Lexical is TF-IDF over hashed n-grams. Sparse, high dimensional.
	Lexical Kind = iota
	// Dense is Lexical put through a signed random projection, which is what
	// makes it behave like a pooled neural embedding for the purposes of the
	// question this project asks.
	Dense
)

func (k Kind) String() string {
	if k == Dense {
		return "dense"
	}
	return "lexical"
}

// Model is a fitted embedder. Fitting means learning IDF weights from a corpus,
// which is the only thing here that touches data.
type Model struct {
	kind Kind
	// hashDim is the size of the sparse hashed feature space.
	hashDim int
	// denseDim is the projected size, used only when kind == Dense.
	denseDim int
	// idf maps a token to its inverse document frequency.
	idf map[string]float64
	// unseenIDF is what an out-of-vocabulary token gets: the maximum observed
	// IDF, because a token nobody has ever written is at least as informative
	// as the rarest one that has been.
	unseenIDF float64
	docs      int
	seed      uint64
	// df is retained so the report can show the document-frequency
	// distribution rather than assert something about it.
	df map[string]int
}

// Fit learns IDF weights. dim is the hashed feature space; denseDim is used
// only by the Dense kind.
func Fit(texts []string, kind Kind, hashDim, denseDim int, seed uint64) *Model {
	m := &Model{kind: kind, hashDim: hashDim, denseDim: denseDim,
		idf: map[string]float64{}, docs: len(texts), seed: seed}

	df := map[string]int{}
	for _, t := range texts {
		seen := map[string]bool{}
		for _, tok := range Tokenize(t) {
			if !seen[tok] {
				seen[tok] = true
				df[tok]++
			}
		}
	}
	m.df = df
	n := float64(len(texts))
	for tok, d := range df {
		// Smoothed IDF, the standard form.
		v := math.Log((n+1)/(float64(d)+1)) + 1
		m.idf[tok] = v
		if v > m.unseenIDF {
			m.unseenIDF = v
		}
	}
	if m.unseenIDF == 0 {
		m.unseenIDF = 1
	}
	return m
}

// IDF returns a token's learned weight, or the unseen-token weight.
func (m *Model) IDF(tok string) float64 {
	if v, ok := m.idf[tok]; ok {
		return v
	}
	return m.unseenIDF
}

// Kind reports which embedding this model produces.
func (m *Model) Kind() Kind { return m.kind }

// Embed produces a normalised vector.
func (m *Model) Embed(text string) Vector {
	sparse := m.sparse(text)
	if m.kind == Lexical {
		return normalise(sparse)
	}
	return normalise(m.project(sparse))
}

// sparse builds the raw hashed TF-IDF vector.
func (m *Model) sparse(text string) []float64 {
	v := make([]float64, m.hashDim)
	toks := Tokenize(text)
	if len(toks) == 0 {
		return v
	}
	tf := map[string]float64{}
	for _, t := range toks {
		tf[t]++
	}
	// Sorted, not map order. Feature hashing deliberately allows collisions, so
	// two tokens can land in the same bucket; float addition is not
	// associative, so accumulating in Go's randomised map order makes the
	// vector differ in its last bits between runs of the same binary. That is
	// invisible until something prints enough digits - here it surfaced as a
	// report cell alternating between "0.000" and "-0.000".
	for _, tok := range sortedKeys(tf) {
		// Sublinear TF, the standard damping: the second occurrence of a word
		// says much less than the first.
		w := (1 + math.Log(tf[tok])) * m.IDF(tok)
		idx, sign := hashIndex(tok, m.hashDim)
		v[idx] += sign * w
	}
	return v
}

func sortedKeys(m map[string]float64) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

// project applies a signed random projection. Each sparse coordinate is spread
// over the dense space with deterministic +/-1 signs, which is the standard
// database-friendly Johnson-Lindenstrauss construction and preserves inner
// products in expectation.
func (m *Model) project(sparse []float64) []float64 {
	out := make([]float64, m.denseDim)
	scale := 1 / math.Sqrt(float64(m.denseDim))
	for i, v := range sparse {
		if v == 0 {
			continue
		}
		h := mix(m.seed, uint64(i))
		for j := 0; j < m.denseDim; j++ {
			h = mix(h, uint64(j))
			if h&1 == 0 {
				out[j] += v * scale
			} else {
				out[j] -= v * scale
			}
		}
	}
	return out
}

// TokenWeights returns each distinct token's share of the vector's L2 mass,
// which is the quantity that bounds how much changing one token can move
// cosine similarity. Section 2 of the report uses it directly.
func (m *Model) TokenWeights(text string) map[string]float64 {
	toks := Tokenize(text)
	tf := map[string]float64{}
	for _, t := range toks {
		tf[t]++
	}
	w := map[string]float64{}
	total := 0.0
	// Sorted for the same reason as sparse: the running total must not depend
	// on map iteration order, or the shares differ in their last bits and
	// section 1's exactness claim becomes run-dependent.
	for _, tok := range sortedKeys(tf) {
		v := (1 + math.Log(tf[tok])) * m.IDF(tok)
		w[tok] = v * v
		total += v * v
	}
	if total == 0 {
		return w
	}
	for k := range w {
		w[k] /= total
	}
	return w
}

// RareTokens returns the tokens whose IDF is at or above minIDF, sorted.
//
// These are the decisive tokens: "germany", "france", "pro", "free", "import",
// "export", "401". A cosine similarity averages them away by construction. The
// guard in package cache compares them directly instead, which is the fix that
// section 4 measures.
func (m *Model) RareTokens(text string, minIDF float64) []string {
	seen := map[string]bool{}
	var out []string
	for _, tok := range Tokenize(text) {
		if seen[tok] || strings.Contains(tok, " ") {
			continue
		}
		if m.IDF(tok) >= minIDF {
			seen[tok] = true
			out = append(out, tok)
		}
	}
	sort.Strings(out)
	return out
}

// TopIDFTokens returns the k highest-IDF unigrams of a text, sorted for
// determinism.
//
// This exists because RareTokens is the wrong tool on a small corpus, and
// section 4 of the report shows exactly why. Document frequency over 170
// documents is degenerate: most of the vocabulary occurs once, so every
// percentile of the IDF distribution lands on the same value and an
// "IDF >= cut" rule admits nearly every content word. The decisive set then
// grows with sentence length, which reintroduces the dilution the guard was
// built to escape.
//
// Bounding the SIZE of the set fixes that. Whatever the corpus, the k most
// informative words of a query are the ones that carry its identity, and two
// queries about different countries cannot agree on them.
//
// Ties are broken lexicographically, not by map order, because a guard whose
// verdict depends on Go's map iteration would be unreproducible.
func (m *Model) TopIDFTokens(text string, k int) []string {
	if k <= 0 {
		return nil
	}
	seen := map[string]bool{}
	var toks []string
	for _, tok := range Tokenize(text) {
		if seen[tok] || strings.Contains(tok, " ") {
			continue
		}
		seen[tok] = true
		toks = append(toks, tok)
	}
	sort.Slice(toks, func(i, j int) bool {
		a, b := m.IDF(toks[i]), m.IDF(toks[j])
		if a != b {
			return a > b
		}
		return toks[i] < toks[j]
	})
	if len(toks) > k {
		toks = toks[:k]
	}
	sort.Strings(toks)
	return toks
}

// IDFHistogram reports how many vocabulary entries fall in each document
// frequency, lowest first. It is the evidence for the degeneracy claim above:
// if the first bucket holds most of the vocabulary, an IDF threshold cannot
// discriminate, and no amount of tuning the cut will help.
func (m *Model) IDFHistogram() []int {
	counts := map[int]int{}
	maxDF := 0
	for _, df := range m.df {
		counts[df]++
		if df > maxDF {
			maxDF = df
		}
	}
	out := make([]int, maxDF+1)
	for df, n := range counts {
		out[df] = n
	}
	return out
}

// IDFPercentile returns the IDF value at the given percentile of the fitted
// vocabulary, so the rare-token threshold can be expressed as "the top 30% most
// informative tokens" rather than as an unexplainable constant.
func (m *Model) IDFPercentile(p float64) float64 {
	if len(m.idf) == 0 {
		return 0
	}
	vals := make([]float64, 0, len(m.idf))
	for _, v := range m.idf {
		vals = append(vals, v)
	}
	sort.Float64s(vals)
	i := int(p * float64(len(vals)-1))
	return vals[i]
}

// VocabSize is the number of distinct tokens seen during fitting.
func (m *Model) VocabSize() int { return len(m.idf) }

// Tokenize lowercases, splits on non-alphanumerics, drops stopwords, maps
// surface forms to concepts via package lexicon, and adds adjacent bigrams.
//
// The lexicon step is what makes this a *semantic* similarity rather than a
// lexical one, and package lexicon's doc comment explains at length why it is
// hand-authored and what that does and does not license. In short: it supplies
// synonymy, which is the one thing a neural sentence embedder has that a bag of
// words does not, and it contains no information about which tokens decide an
// answer.
//
// Bigrams are here for a specific reason: without them "how do I turn on auto
// renew" and "how do I turn off auto renew" differ in exactly one unigram out
// of five. Bigrams give the difference more surface ("turn on"/"turn off",
// "on auto"/"off auto"), which is the standard mitigation. Section 2 measures
// how much it helps, and the answer is: some, and nowhere near enough.
func Tokenize(text string) []string {
	var words []string
	cur := make([]rune, 0, 16)
	flush := func() {
		if len(cur) > 0 {
			words = append(words, string(cur))
			cur = cur[:0]
		}
	}
	for _, r := range strings.ToLower(text) {
		switch {
		case r >= 'a' && r <= 'z', r >= '0' && r <= '9':
			cur = append(cur, r)
		default:
			flush()
		}
	}
	flush()

	// Phrase-level synonyms first, because "money back" must become "refund"
	// before "money" and "back" are considered separately.
	rewritten := make([]string, 0, len(words))
	for i := 0; i < len(words); i++ {
		if i+1 < len(words) {
			if repl, ok := lexicon.CanonPhrase(words[i], words[i+1]); ok {
				rewritten = append(rewritten, repl...)
				i++
				continue
			}
		}
		rewritten = append(rewritten, words[i])
	}

	kept := make([]string, 0, len(rewritten))
	for _, w := range rewritten {
		if stopwords[w] {
			continue
		}
		kept = append(kept, lexicon.Canon(w))
	}
	out := make([]string, 0, len(kept)*2)
	out = append(out, kept...)
	for i := 0; i+1 < len(kept); i++ {
		out = append(out, kept[i]+" "+kept[i+1])
	}
	return out
}

// A deliberately short stopword list. "no", "not", "off" and "on" are NOT
// stopwords, because dropping them is how a cache comes to treat "how do I turn
// off 2FA" and "how do I turn on 2FA" as the same question.
var stopwords = map[string]bool{
	"a": true, "an": true, "the": true, "is": true, "are": true, "was": true,
	"were": true, "be": true, "been": true, "am": true, "i": true, "me": true,
	"my": true, "we": true, "our": true, "you": true, "your": true,
	"it": true, "its": true, "of": true, "to": true, "in": true, "for": true,
	"and": true, "or": true, "at": true, "by": true, "with": true, "as": true,
	"that": true, "this": true, "these": true, "those": true, "do": true,
	"does": true, "did": true, "have": true, "has": true, "had": true,
	"can": true, "could": true, "would": true, "should": true, "will": true,
	"what": true, "how": true, "when": true, "where": true, "which": true,
	"there": true, "here": true, "get": true, "got": true,
}

func normalise(v []float64) Vector {
	var n float64
	for _, x := range v {
		n += x * x
	}
	if n == 0 {
		return Vector(v)
	}
	n = math.Sqrt(n)
	for i := range v {
		v[i] /= n
	}
	return Vector(v)
}

// hashIndex is the feature-hashing trick: a token maps to one bucket and one
// sign. The sign is what keeps collisions unbiased rather than systematically
// inflating similarity.
func hashIndex(tok string, dim int) (int, float64) {
	h := fnv(tok)
	idx := int(h % uint64(dim))
	if (h>>63)&1 == 1 {
		return idx, -1
	}
	return idx, 1
}

func fnv(s string) uint64 {
	var h uint64 = 1469598103934665603
	for i := 0; i < len(s); i++ {
		h ^= uint64(s[i])
		h *= 1099511628211
	}
	return h
}

func mix(a, b uint64) uint64 {
	h := a ^ (b * 0x9E3779B97F4A7C15)
	h ^= h >> 33
	h *= 0xFF51AFD7ED558CCD
	h ^= h >> 33
	h *= 0xC4CEB9FE1A85EC53
	h ^= h >> 33
	return h
}

func clamp(v, lo, hi float64) float64 {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}
