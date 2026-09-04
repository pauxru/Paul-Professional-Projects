package embed

import (
	"math"
	"sort"
	"strings"
	"testing"
)

func fitCorpus() *Model {
	return Fit([]string{
		"how long does shipping take to germany",
		"how long does shipping take to france",
		"when will my order arrive in germany",
		"how many requests do I get on free",
		"how many requests do I get on pro",
		"disable 2FA on my account",
		"enable 2FA on my account",
		"I want a refund for my order",
		"how do I get my money back",
		"what is your returns policy",
	}, Lexical, 1024, 64, 7)
}

func TestVectorsAreUnitLength(t *testing.T) {
	m := fitCorpus()
	for _, kind := range []Kind{Lexical, Dense} {
		mm := Fit([]string{"a b c", "b c d", "c d e"}, kind, 256, 32, 1)
		v := mm.Embed("b c d")
		n := 0.0
		for _, x := range v {
			n += x * x
		}
		if math.Abs(math.Sqrt(n)-1) > 1e-9 {
			t.Errorf("%v: |v| = %v, want 1", kind, math.Sqrt(n))
		}
	}
	// A text with no in-vocabulary tokens must still produce a usable vector
	// rather than NaNs, or one empty query poisons every comparison.
	v := m.Embed("")
	for i, x := range v {
		if math.IsNaN(x) || math.IsInf(x, 0) {
			t.Fatalf("empty text produced a non-finite component at %d: %v", i, x)
		}
	}
}

func TestEmbeddingIsDeterministic(t *testing.T) {
	m := fitCorpus()
	a := m.Embed("how long does shipping take to germany")
	b := m.Embed("how long does shipping take to germany")
	for i := range a {
		if a[i] != b[i] {
			t.Fatalf("component %d differs between calls: %v vs %v", i, a[i], b[i])
		}
	}
}

// Determinism WITHIN a process is easy; determinism ACROSS processes is what
// the report's "reproduces byte for byte" claim needs, and it is a different
// property. Go randomises map iteration order per process, and float addition
// is not associative, so any sum accumulated in map order varies between runs
// of the same binary. Feature hashing makes this reachable: two tokens can
// collide into one bucket, and then the bucket's value depends on which was
// added first.
//
// Building many independent models and comparing exact bit patterns exercises
// many map orderings in one process. A reproducibility check in test.ps1
// covers the cross-process case; this is the fast version that fails locally.
func TestEmbeddingIsBitIdenticalAcrossIndependentFits(t *testing.T) {
	ref := fitCorpus()
	texts := []string{
		"how long does shipping take to germany",
		"how many requests do I get on free",
		"disable 2FA on my account",
		"I want a refund for my order",
		"",
	}
	want := make([]Vector, len(texts))
	for i, txt := range texts {
		want[i] = ref.Embed(txt)
	}
	for trial := 0; trial < 60; trial++ {
		m := fitCorpus()
		for i, txt := range texts {
			got := m.Embed(txt)
			for j := range got {
				if got[j] != want[i][j] {
					t.Fatalf("trial %d, text %q, component %d: %v != %v. A sum is "+
						"being accumulated in map iteration order.",
						trial, txt, j, got[j], want[i][j])
				}
			}
		}
	}
}

func TestTokenWeightsAreBitIdenticalAcrossFits(t *testing.T) {
	ref := fitCorpus().TokenWeights("how long does shipping take to germany")
	for trial := 0; trial < 60; trial++ {
		got := fitCorpus().TokenWeights("how long does shipping take to germany")
		for k, v := range ref {
			if got[k] != v {
				t.Fatalf("trial %d: weight for %q is %v, want %v", trial, k, got[k], v)
			}
		}
	}
}

func TestFittingIsDeterministic(t *testing.T) {
	a, b := fitCorpus(), fitCorpus()
	if a.VocabSize() != b.VocabSize() {
		t.Fatalf("vocab sizes differ: %d vs %d", a.VocabSize(), b.VocabSize())
	}
	va, vb := a.Embed("disable 2FA on my account"), b.Embed("disable 2FA on my account")
	for i := range va {
		if va[i] != vb[i] {
			t.Fatalf("two fits of the same corpus disagree at %d", i)
		}
	}
}

func TestCosineIsBoundedAndSelfSimilarityIsOne(t *testing.T) {
	m := fitCorpus()
	texts := []string{"how long does shipping take to germany", "enable 2FA on my account",
		"I want a refund for my order"}
	for _, a := range texts {
		va := m.Embed(a)
		if s := Cosine(va, va); math.Abs(s-1) > 1e-9 {
			t.Errorf("Cosine(v,v) = %v, want 1", s)
		}
		for _, b := range texts {
			s := Cosine(va, m.Embed(b))
			if s < -1.0000001 || s > 1.0000001 {
				t.Errorf("Cosine(%q,%q) = %v, out of [-1,1]", a, b, s)
			}
		}
	}
}

func TestCosineIsSymmetric(t *testing.T) {
	m := fitCorpus()
	a, b := m.Embed("enable 2FA on my account"), m.Embed("disable 2FA on my account")
	if math.Abs(Cosine(a, b)-Cosine(b, a)) > 1e-12 {
		t.Error("Cosine is not symmetric")
	}
}

func TestTokenizeProducesBigrams(t *testing.T) {
	toks := Tokenize("shipping to germany")
	found := false
	for _, tk := range toks {
		if strings.Contains(tk, " ") {
			found = true
		}
	}
	if !found {
		t.Errorf("no bigram in %v; without bigrams word order is invisible", toks)
	}
}

// "on", "off" and "not" carry the entire meaning of the autorenew and 2FA
// families. A stopword list that drops them would make those intents
// indistinguishable, and the failure would look like an embedding weakness.
func TestPolarityWordsSurviveTokenisation(t *testing.T) {
	for _, w := range []string{"on", "off", "not"} {
		toks := Tokenize("turn autorenew " + w)
		found := false
		for _, tk := range toks {
			if tk == w || strings.Contains(tk, w+" ") || strings.Contains(tk, " "+w) {
				found = true
			}
		}
		if !found {
			t.Errorf("%q was dropped by tokenisation: %v", w, toks)
		}
	}
}

func TestStopwordsAreActuallyRemoved(t *testing.T) {
	toks := Tokenize("what is the refund policy")
	for _, tk := range toks {
		if tk == "the" || tk == "is" {
			t.Errorf("stopword %q survived: %v", tk, toks)
		}
	}
}

func TestTokenWeightsSumToOne(t *testing.T) {
	m := fitCorpus()
	for _, text := range []string{
		"how long does shipping take to germany",
		"enable 2FA on my account",
		"I want a refund for my order",
	} {
		w := m.TokenWeights(text)
		sum := 0.0
		for _, x := range w {
			sum += x
		}
		if math.Abs(sum-1) > 1e-9 {
			t.Errorf("TokenWeights(%q) sums to %v, want 1; section 2's mass-share "+
				"argument depends on this being a proper share", text, sum)
		}
	}
}

// The claim section 2 rests on, tested directly: for unit vectors the cosine is
// the shared mass, so the drop from changing features equals their share.
func TestSimilarityDropEqualsDifferingMassShare(t *testing.T) {
	m := fitCorpus()
	a := "how long does shipping take to germany"
	b := "how long does shipping take to france"
	wa, wb := m.TokenWeights(a), m.TokenWeights(b)
	shared := 0.0
	for tok, sa := range wa {
		if sb, ok := wb[tok]; ok {
			shared += math.Sqrt(sa * sb)
		}
	}
	predicted := 1 - shared
	measured := 1 - Cosine(m.Embed(a), m.Embed(b))
	if math.Abs(predicted-measured) > 1e-6 {
		t.Errorf("predicted drop %v, measured %v; the mass-share identity does not hold",
			predicted, measured)
	}
}

func TestTopIDFTokensRespectsTheBudget(t *testing.T) {
	m := fitCorpus()
	for k := 1; k <= 5; k++ {
		got := m.TopIDFTokens("how long does shipping take to germany", k)
		if len(got) > k {
			t.Errorf("TopIDFTokens(k=%d) returned %d tokens: %v", k, len(got), got)
		}
	}
	if got := m.TopIDFTokens("anything", 0); got != nil {
		t.Errorf("k=0 must return nil, got %v", got)
	}
}

func TestTopIDFTokensIsSortedAndDeterministic(t *testing.T) {
	m := fitCorpus()
	for i := 0; i < 20; i++ {
		got := m.TopIDFTokens("how many requests do I get on free", 3)
		if !sort.StringsAreSorted(got) {
			t.Fatalf("not sorted: %v", got)
		}
		want := m.TopIDFTokens("how many requests do I get on free", 3)
		if strings.Join(got, ",") != strings.Join(want, ",") {
			t.Fatalf("run %d differs: %v vs %v", i, got, want)
		}
	}
}

// The guard only works if the top-K of two confusable queries actually differ.
// If this fails, section 4's result is luck.
func TestTopIDFTokensSeparatesConfusablePairs(t *testing.T) {
	m := fitCorpus()
	pairs := [][2]string{
		{"how long does shipping take to germany", "how long does shipping take to france"},
		{"how many requests do I get on free", "how many requests do I get on pro"},
		{"disable 2FA on my account", "enable 2FA on my account"},
	}
	for _, p := range pairs {
		a := m.TopIDFTokens(p[0], 3)
		b := m.TopIDFTokens(p[1], 3)
		if strings.Join(a, ",") == strings.Join(b, ",") {
			t.Errorf("top-3 identical for %q and %q: %v", p[0], p[1], a)
		}
	}
}

func TestTopIDFTokensExcludesBigrams(t *testing.T) {
	m := fitCorpus()
	for _, tok := range m.TopIDFTokens("how long does shipping take to germany", 5) {
		if strings.Contains(tok, " ") {
			t.Errorf("bigram %q in the decisive set; the guard compares words, not phrases", tok)
		}
	}
}

func TestIDFHistogramAccountsForTheWholeVocabulary(t *testing.T) {
	m := fitCorpus()
	sum := 0
	for _, n := range m.IDFHistogram() {
		sum += n
	}
	if sum != m.VocabSize() {
		t.Errorf("histogram sums to %d, vocabulary is %d", sum, m.VocabSize())
	}
	if h := m.IDFHistogram(); len(h) > 0 && h[0] != 0 {
		t.Errorf("%d tokens with document frequency 0, which is impossible", h[0])
	}
}

func TestUnseenTokensGetTheMaximumIDF(t *testing.T) {
	m := fitCorpus()
	maxSeen := 0.0
	for tok := range m.idf {
		if v := m.IDF(tok); v > maxSeen {
			maxSeen = v
		}
	}
	if got := m.IDF("zzzzneverseen"); got < maxSeen {
		t.Errorf("unseen token IDF %v < max seen %v; a word nobody has written is at "+
			"least as informative as the rarest one that has been", got, maxSeen)
	}
}

func TestRareTokensRespectsTheCut(t *testing.T) {
	m := fitCorpus()
	cut := m.IDFPercentile(0.5)
	for _, tok := range m.RareTokens("how long does shipping take to germany", cut) {
		if m.IDF(tok) < cut {
			t.Errorf("%q has IDF %v, below the cut %v", tok, m.IDF(tok), cut)
		}
	}
	// A cut above every observed value must admit nothing.
	if got := m.RareTokens("how long does shipping take to germany", 1e9); len(got) != 0 {
		t.Errorf("an impossible cut admitted %v", got)
	}
}

func TestIDFPercentileIsMonotone(t *testing.T) {
	m := fitCorpus()
	prev := -1.0
	for _, p := range []float64{0, 0.1, 0.25, 0.5, 0.75, 0.9, 1.0} {
		v := m.IDFPercentile(p)
		if v < prev {
			t.Errorf("IDFPercentile is not monotone: p=%v gave %v after %v", p, v, prev)
		}
		prev = v
	}
}

// The lexicon is supposed to make paraphrases similar. If it does not, the
// similarity is lexical and the project's framing is wrong.
func TestSynonymsRaiseSimilarity(t *testing.T) {
	m := fitCorpus()
	withSyn := Cosine(m.Embed("I want a refund for my order"),
		m.Embed("how do I get my money back"))
	if withSyn <= 0 {
		t.Errorf("two paraphrases scored %v; the lexicon is not supplying synonymy", withSyn)
	}
}

func TestKindStringsAreDistinct(t *testing.T) {
	if Lexical.String() == Dense.String() {
		t.Error("Kind.String() is ambiguous")
	}
}
