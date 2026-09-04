package workload

import (
	"testing"

	"semcache/internal/corpus"
)

func TestGenerateIsDeterministicForASeed(t *testing.T) {
	cfg := Default()
	cfg.N = 2000
	a, b := Generate(cfg), Generate(cfg)
	if len(a) != len(b) {
		t.Fatalf("lengths differ: %d vs %d", len(a), len(b))
	}
	for i := range a {
		if a[i] != b[i] {
			t.Fatalf("request %d differs between identical configs: %+v vs %+v", i, a[i], b[i])
		}
	}
}

func TestDifferentSeedsProduceDifferentStreams(t *testing.T) {
	cfg := Default()
	cfg.N = 2000
	a := Generate(cfg)
	cfg.Seed++
	b := Generate(cfg)
	same := 0
	for i := range a {
		if a[i].Text == b[i].Text {
			same++
		}
	}
	if same == len(a) {
		t.Error("changing the seed changed nothing; the generator is ignoring it")
	}
}

func TestGenerateRespectsN(t *testing.T) {
	for _, n := range []int{0, 1, 10, 5000} {
		cfg := Default()
		cfg.N = n
		if got := len(Generate(cfg)); got != n {
			t.Errorf("N=%d produced %d requests", n, got)
		}
	}
}

// The gateway is a discrete-event simulation. If arrivals were not sorted it
// would process a later request before an earlier one and every coalescing
// window would be wrong.
func TestArrivalsAreSortedAndNonNegative(t *testing.T) {
	cfg := Default()
	cfg.N = 5000
	reqs := Generate(cfg)
	var prev int64 = -1
	for i, r := range reqs {
		if r.ArriveUs < 0 {
			t.Fatalf("request %d arrives at %d", i, r.ArriveUs)
		}
		if r.ArriveUs < prev {
			t.Fatalf("request %d arrives at %d, before its predecessor at %d",
				i, r.ArriveUs, prev)
		}
		prev = r.ArriveUs
	}
}

// Every request must be a real, labelled corpus utterance. A generator that
// invented text or labels would make every measured rate unverifiable.
//
// The one legitimate exception is a SHARED intent: "how do I reset my
// password" belongs to every tenant, so the generator re-tenants it. Intents
// that are not shared must keep the tenant the corpus assigned.
func TestEveryRequestIsALabelledCorpusQuery(t *testing.T) {
	byText := map[string]map[string]string{} // text -> tenant -> intent
	anyIntent := map[string]map[string]bool{}
	for _, q := range corpus.All() {
		if byText[q.Text] == nil {
			byText[q.Text] = map[string]string{}
			anyIntent[q.Text] = map[string]bool{}
		}
		byText[q.Text][q.Tenant] = q.Intent
		anyIntent[q.Text][q.Intent] = true
	}
	cfg := Default()
	cfg.N = 3000
	for i, r := range Generate(cfg) {
		perTenant, ok := byText[r.Text]
		if !ok {
			t.Fatalf("request %d has text %q, which is not in the corpus; its intent "+
				"label would be unverifiable", i, r.Text)
		}
		if !anyIntent[r.Text][r.Intent] {
			t.Fatalf("request %d labels %q as %q, an intent the corpus never gives it",
				i, r.Text, r.Intent)
		}
		if got, ok := perTenant[r.Tenant]; ok {
			if got != r.Intent {
				t.Fatalf("request %d labels %q for tenant %q as %q, but the corpus "+
					"says %q", i, r.Text, r.Tenant, r.Intent, got)
			}
			continue
		}
		// A tenant the corpus never paired with this text is only allowed when
		// the intent is shared across all tenants.
		if !corpus.Shared(r.Intent) {
			t.Fatalf("request %d gives %q (intent %q) to tenant %q, but that intent is "+
				"not shared and the corpus assigns it elsewhere",
				i, r.Text, r.Intent, r.Tenant)
		}
	}
}

func TestZipfSkewConcentratesTraffic(t *testing.T) {
	count := func(z float64) float64 {
		cfg := Default()
		cfg.N = 8000
		cfg.Zipf = z
		return Describe(Generate(cfg), 100_000).TopIntentShare
	}
	flat, skewed := count(0), count(1.2)
	if skewed <= flat {
		t.Errorf("Zipf 1.2 gave a top-intent share of %.3f, no more than the uniform "+
			"stream's %.3f; the skew parameter does nothing", skewed, flat)
	}
}

// The lever section 3 depends on. A benign stream must contain essentially no
// confusable pairs, or the "benign vs trap-heavy" comparison is meaningless.
func TestOneIntentPerFamilyRemovesConfusablePairs(t *testing.T) {
	cfg := Default()
	cfg.N = 5000
	cfg.OneIntentPerFamily = true
	reqs := Generate(cfg)

	// tenant -> family -> set of intents seen
	seen := map[string]map[string]map[string]bool{}
	for _, r := range reqs {
		fam := corpus.Family(r.Intent)
		if fam == "" {
			continue
		}
		if seen[r.Tenant] == nil {
			seen[r.Tenant] = map[string]map[string]bool{}
		}
		if seen[r.Tenant][fam] == nil {
			seen[r.Tenant][fam] = map[string]bool{}
		}
		seen[r.Tenant][fam][r.Intent] = true
	}
	for tenant, fams := range seen {
		for fam, intents := range fams {
			if len(intents) > 1 {
				t.Errorf("tenant %q family %q has %d distinct intents in a "+
					"OneIntentPerFamily stream; section 3's benign arm still contains traps",
					tenant, fam, len(intents))
			}
		}
	}
}

func TestOneIntentPerFamilyStillUsesManyIntents(t *testing.T) {
	cfg := Default()
	cfg.N = 5000
	cfg.OneIntentPerFamily = true
	if d := Describe(Generate(cfg), 100_000); d.DistinctIntents < 15 {
		t.Errorf("only %d distinct intents survive OneIntentPerFamily; the benign "+
			"stream is too degenerate to compare against", d.DistinctIntents)
	}
}

func TestAdversarialBoostRaisesTheAdversarialShare(t *testing.T) {
	share := func(b float64) float64 {
		cfg := Default()
		cfg.N = 6000
		cfg.AdversarialBoost = b
		return Describe(Generate(cfg), 100_000).AdversarialShare
	}
	lo, hi := share(0), share(4)
	if hi <= lo {
		t.Errorf("AdversarialBoost=4 gave share %.3f, no more than 0's %.3f", hi, lo)
	}
}

func TestBurstFractionIncreasesOverlap(t *testing.T) {
	overlap := func(f float64) int {
		cfg := Default()
		cfg.N = 4000
		cfg.BurstFraction = f
		return Describe(Generate(cfg), 100_000).MaxOverlap
	}
	if hi, lo := overlap(0.8), overlap(0.0); hi <= lo {
		t.Errorf("burst fraction 0.8 gave max overlap %d, no more than 0.0's %d; "+
			"there would be no coalescing window to measure", hi, lo)
	}
}

func TestDescribeCountsAgreeWithTheStream(t *testing.T) {
	cfg := Default()
	cfg.N = 3000
	reqs := Generate(cfg)
	d := Describe(reqs, 100_000)

	if d.N != len(reqs) {
		t.Errorf("Describe says N=%d for %d requests", d.N, len(reqs))
	}
	texts, intents, adv := map[string]bool{}, map[string]bool{}, 0
	advText := map[string]bool{}
	for _, q := range corpus.Adversarial() {
		advText[q.Text] = true
	}
	for _, r := range reqs {
		texts[r.Text] = true
		intents[r.Intent] = true
		if advText[r.Text] {
			adv++
		}
	}
	if d.DistinctTexts != len(texts) {
		t.Errorf("DistinctTexts = %d, counted %d", d.DistinctTexts, len(texts))
	}
	if d.DistinctIntents != len(intents) {
		t.Errorf("DistinctIntents = %d, counted %d", d.DistinctIntents, len(intents))
	}
	want := float64(adv) / float64(len(reqs))
	if diff := d.AdversarialShare - want; diff > 1e-9 || diff < -1e-9 {
		t.Errorf("AdversarialShare = %v, counted %v", d.AdversarialShare, want)
	}
}

func TestDescribeHandlesAnEmptyStream(t *testing.T) {
	d := Describe(nil, 100_000)
	if d.N != 0 || d.TopIntentShare != 0 || d.MaxOverlap != 0 {
		t.Errorf("Describe(nil) returned %+v", d)
	}
}

func TestSiblingRateProducesAdjacentConfusablePairs(t *testing.T) {
	cfg := Default()
	cfg.N = 4000
	cfg.SiblingRate = 0.9
	cfg.OneIntentPerFamily = false
	reqs := Generate(cfg)
	adjacent := 0
	for i := 1; i < len(reqs); i++ {
		if reqs[i].Tenant == reqs[i-1].Tenant &&
			reqs[i].Intent != reqs[i-1].Intent &&
			corpus.ConfusableIntents(reqs[i].Intent, reqs[i-1].Intent) {
			adjacent++
		}
	}
	if adjacent == 0 {
		t.Error("SiblingRate 0.9 produced no adjacent confusable pairs; the trap-heavy " +
			"stream contains no traps")
	}
}

func TestSpanUsBoundsTheStream(t *testing.T) {
	cfg := Default()
	cfg.N = 2000
	cfg.SpanUs = 500_000
	reqs := Generate(cfg)
	last := reqs[len(reqs)-1].ArriveUs
	if last > cfg.SpanUs+cfg.BurstSpreadUs {
		t.Errorf("last arrival at %d exceeds the span %d (+burst spread %d)",
			last, cfg.SpanUs, cfg.BurstSpreadUs)
	}
}

func TestTenantsAreAllRepresented(t *testing.T) {
	cfg := Default()
	cfg.N = 5000
	seen := map[string]bool{}
	for _, r := range Generate(cfg) {
		seen[r.Tenant] = true
	}
	for _, want := range corpus.Tenants() {
		if !seen[want] {
			t.Errorf("tenant %q never appears; section 5 cannot measure isolation", want)
		}
	}
}
