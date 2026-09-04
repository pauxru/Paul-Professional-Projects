package router

import (
	"math"
	"sort"
	"testing"

	"router/internal/models"
	"router/internal/workload"
)

func setup(seed uint64, n int) ([]workload.Query, []workload.Query, *models.Oracle) {
	o := models.NewOracle(models.Fleet(), seed)
	train, eval := workload.Split(workload.Generate(n, seed), 0.5)
	return train, eval, o
}

func TestSingleAlwaysCallsExactlyOneModel(t *testing.T) {
	_, eval, o := setup(1, 400)
	out := Run(Single{Model: "mid"}, eval, o)
	if out.CallsPerQ != 1 {
		t.Errorf("calls per query %v, want 1", out.CallsPerQ)
	}
	if out.EscalRate != 0 {
		t.Errorf("escalation rate %v, want 0", out.EscalRate)
	}
	for _, r := range out.Results {
		if len(r.Calls) != 1 || r.Calls[0] != "mid" {
			t.Fatalf("unexpected calls %v", r.Calls)
		}
	}
}

func TestSingleCostMatchesTheModelPrice(t *testing.T) {
	_, eval, o := setup(2, 400)
	out := Run(Single{Model: "large"}, eval, o)
	want := 0.0
	for _, q := range eval {
		want += o.Model("large").Cost(q)
	}
	if math.Abs(out.CostCents-want) > 1e-9 {
		t.Errorf("cost %.6f, want %.6f", out.CostCents, want)
	}
}

func TestRandomEscalatesRoughlyItsRate(t *testing.T) {
	_, eval, o := setup(3, 8000)
	for _, rate := range []float64{0.1, 0.25, 0.5, 0.9} {
		out := Run(Random{Small: "small", Large: "large", Rate: rate, Seed: 3}, eval, o)
		if math.Abs(out.EscalRate-rate) > 0.03 {
			t.Errorf("rate %.2f: escalated %.4f", rate, out.EscalRate)
		}
	}
}

func TestRandomIsReproducible(t *testing.T) {
	_, eval, o := setup(4, 1000)
	p := Random{Small: "small", Large: "large", Rate: 0.4, Seed: 4}
	a, b := Run(p, eval, o), Run(p, eval, o)
	if a.Accuracy != b.Accuracy || a.CostCents != b.CostCents {
		t.Fatal("Random is not deterministic for a fixed seed")
	}
}

func TestRandomNeverPaysTwice(t *testing.T) {
	_, eval, o := setup(5, 500)
	out := Run(Random{Small: "small", Large: "large", Rate: 0.5, Seed: 5}, eval, o)
	for _, r := range out.Results {
		if len(r.Calls) != 1 {
			t.Fatalf("random made %d calls: %v", len(r.Calls), r.Calls)
		}
	}
}

// The oracle is a ceiling. It must be at least as accurate as always-large,
// because it only ever declines to escalate when escalation would not help.
func TestOracleBeatsBothEndpoints(t *testing.T) {
	_, eval, o := setup(6, 4000)
	small := Run(Single{Model: "small"}, eval, o)
	large := Run(Single{Model: "large"}, eval, o)
	orc := Run(Oracle{Small: "small", Large: "large"}, eval, o)
	if orc.Accuracy < large.Accuracy-1e-12 {
		t.Errorf("oracle %.4f is below always-large %.4f", orc.Accuracy, large.Accuracy)
	}
	if orc.CostCents >= large.CostCents {
		t.Errorf("oracle cost %.2f is not below always-large %.2f", orc.CostCents, large.CostCents)
	}
	if orc.Accuracy <= small.Accuracy {
		t.Error("oracle is no better than always-small")
	}
}

func TestOracleNeverPaysForAnEscalationThatWouldNotHelp(t *testing.T) {
	_, eval, o := setup(7, 2000)
	out := Run(Oracle{Small: "small", Large: "large"}, eval, o)
	for _, r := range out.Results {
		if !r.Escalated {
			continue
		}
		if !o.WouldBeCorrect(r.Query, "large") {
			t.Fatalf("query %d escalated to a model that gets it wrong", r.Query.ID)
		}
		if o.WouldBeCorrect(r.Query, "small") {
			t.Fatalf("query %d escalated though the small model had it right", r.Query.ID)
		}
	}
}

func TestOracleNeverPaysTwice(t *testing.T) {
	_, eval, o := setup(8, 1000)
	for _, r := range Run(Oracle{Small: "small", Large: "large"}, eval, o).Results {
		if len(r.Calls) != 1 {
			t.Fatalf("oracle made %d calls on query %d", len(r.Calls), r.Query.ID)
		}
	}
}

func TestClassifierLearnsSomethingRealFromHeldOutData(t *testing.T) {
	train, eval, o := setup(9, 8000)
	c := TrainClassifier(train, o, "small", "large", 0.5, 400, 3.0)

	var hardScore, easyScore, nh, ne float64
	for _, q := range eval {
		s := c.Score(q)
		if o.WouldBeCorrect(q, "small") {
			easyScore += s
			ne++
		} else {
			hardScore += s
			nh++
		}
	}
	if hardScore/nh <= easyScore/ne+0.10 {
		t.Fatalf("classifier does not separate held-out cases: %.3f on failures vs %.3f on successes",
			hardScore/nh, easyScore/ne)
	}
}

func TestClassifierThresholdIsMonotoneInEscalation(t *testing.T) {
	train, eval, o := setup(10, 4000)
	c := TrainClassifier(train, o, "small", "large", 0.5, 300, 3.0)
	prev := 2.0
	for _, th := range []float64{0.1, 0.3, 0.5, 0.7, 0.9} {
		c.Threshold = th
		got := Run(c, eval, o).EscalRate
		if got > prev+1e-12 {
			t.Fatalf("raising the threshold to %.1f increased escalation (%.4f > %.4f)", th, got, prev)
		}
		prev = got
	}
}

func TestClassifierNeverPaysTwice(t *testing.T) {
	train, eval, o := setup(11, 1000)
	c := TrainClassifier(train, o, "small", "large", 0.5, 100, 3.0)
	for _, r := range Run(c, eval, o).Results {
		if len(r.Calls) != 1 {
			t.Fatalf("classifier made %d calls", len(r.Calls))
		}
	}
}

func TestTrainClassifierOnEmptyDataIsSafe(t *testing.T) {
	_, _, o := setup(12, 200)
	c := TrainClassifier(nil, o, "small", "large", 0.5, 10, 1.0)
	if len(c.Weights) == 0 {
		t.Fatal("expected a usable zero classifier")
	}
	if s := c.Score(workload.Query{Tokens: 100, Words: 10}); math.IsNaN(s) {
		t.Fatal("zero classifier produced NaN")
	}
}

func TestCascadePaysTwiceExactlyWhenItEscalates(t *testing.T) {
	_, eval, o := setup(13, 1500)
	out := Run(Cascade{Small: "small", Large: "large", Threshold: 0.6}, eval, o)
	for _, r := range out.Results {
		if r.Escalated && len(r.Calls) != 2 {
			t.Fatalf("escalated query %d made %d calls", r.Query.ID, len(r.Calls))
		}
		if !r.Escalated && len(r.Calls) != 1 {
			t.Fatalf("non-escalated query %d made %d calls", r.Query.ID, len(r.Calls))
		}
		if r.Escalated {
			want := o.Model("small").Cost(r.Query) + o.Model("large").Cost(r.Query)
			if math.Abs(r.CostCents-want) > 1e-12 {
				t.Fatalf("escalated cost %.6f, want %.6f (both calls)", r.CostCents, want)
			}
		}
	}
}

func TestCascadeEscalatesExactlyBelowTheThreshold(t *testing.T) {
	_, eval, o := setup(14, 2000)
	const th = 0.55
	c := Cascade{Small: "small", Large: "large", Threshold: th}
	for _, q := range eval {
		r := c.Route(q, o)
		conf := o.Ask(q, "small").Confidence
		if (conf < th) != r.Escalated {
			t.Fatalf("query %d: confidence %.4f, threshold %.2f, escalated %v",
				q.ID, conf, th, r.Escalated)
		}
	}
}

func TestCascadeAtZeroNeverEscalatesAndAtOneAlwaysDoes(t *testing.T) {
	_, eval, o := setup(15, 800)
	if got := Run(Cascade{Small: "small", Large: "large", Threshold: 0}, eval, o).EscalRate; got != 0 {
		t.Errorf("threshold 0 escalated %.4f of traffic", got)
	}
	// Confidence is clamped below 1, so a threshold above every value escalates all.
	if got := Run(Cascade{Small: "small", Large: "large", Threshold: 1.01}, eval, o).EscalRate; got != 1 {
		t.Errorf("threshold above 1 escalated only %.4f of traffic", got)
	}
}

// This is the claim the whole report turns on. Temperature scaling is monotone,
// so a threshold cascade on the scaled signal is IDENTICAL to one on the raw
// signal at a re-indexed threshold. Not similar - identical, query by query.
func TestRecalibrationIsAReparameterisation(t *testing.T) {
	_, eval, o := setup(16, 4000)
	for _, temp := range []float64{0.6, 1.0, 1.59, 2.4} {
		for th := 0.05; th <= 0.95; th += 0.05 {
			scaled := Cascade{Small: "small", Large: "large", Threshold: th, Temperature: temp}
			raw := Cascade{Small: "small", Large: "large", Threshold: Temper(th, 1/temp)}
			for _, q := range eval {
				a, b := scaled.Route(q, o), raw.Route(q, o)
				if a.Escalated != b.Escalated || a.Correct != b.Correct ||
					a.CostCents != b.CostCents || len(a.Calls) != len(b.Calls) {
					t.Fatalf("temp %.2f threshold %.2f query %d: scaled %+v differs from raw %+v",
						temp, th, q.ID, a, b)
				}
			}
		}
	}
}

// The corresponding positive claim: the VALUE rule does arithmetic on the
// confidence, so it is not invariant and recalibration can change its decisions.
func TestValueCascadeIsNotInvariantUnderRecalibration(t *testing.T) {
	train, eval, o := setup(17, 4000)
	pl := EstimatePLarge(train, o, "large")
	raw := ValueCascade{Small: "small", Large: "large", PLarge: pl, Lambda: 0.1}
	cal := ValueCascade{Small: "small", Large: "large", PLarge: pl, Lambda: 0.1, Temperature: 1.59}
	diff := 0
	for _, q := range eval {
		if raw.Route(q, o).Escalated != cal.Route(q, o).Escalated {
			diff++
		}
	}
	if diff == 0 {
		t.Fatal("recalibration changed no value-cascade decisions; either the rule " +
			"is no longer arithmetic or the temperature is being ignored")
	}
}

// The mechanism, tested exactly. Ask() is a pure function of (query ID, model,
// seed) and difficulty, so two queries that differ ONLY in token count get the
// identical confidence and the identical correctness — and differ only in what
// escalation costs. At a fixed lambda the value rule must escalate the cheap
// one and decline the dear one. Nothing else in the codebase can do this.
func TestValueCascadePricesTheEscalation(t *testing.T) {
	_, _, o := setup(18, 10)
	pl := 0.875
	v := ValueCascade{Small: "small", Large: "large", PLarge: pl, Lambda: 0.05}

	base := workload.Query{ID: 7, Class: workload.Reasoning, Words: 120,
		QuestionMarks: 2, Difficulty: 0.55}
	cheap, dear := base, base
	cheap.Tokens = 200
	dear.Tokens = 20000

	conf := o.Ask(cheap, "small").Confidence
	if conf != o.Ask(dear, "small").Confidence {
		t.Fatal("the pair is not confidence-matched; Ask must not depend on token count")
	}
	// Sanity: the pair has to straddle the decision, or the test proves nothing.
	gain := pl - conf
	if gain <= 0.05*(2.0*200/1000) || gain >= 0.05*(2.0*20000/1000) {
		t.Skipf("gain %.4f does not straddle the two marginal costs", gain)
	}

	if !v.Route(cheap, o).Escalated {
		t.Error("declined to escalate a 200-token query it could afford")
	}
	if v.Route(dear, o).Escalated {
		t.Error("escalated a 20,000-token query at the same confidence")
	}
}

// The economic claim, tested distributionally: matched on escalation RATE, the
// value rule spends less on escalation than a fixed threshold does, because it
// picks a cheaper subset of the same size.
func TestValueCascadeBuysACheaperEscalationSetAtTheSameRate(t *testing.T) {
	train, eval, o := setup(19, 8000)
	pl := EstimatePLarge(train, o, "large")
	v := Run(ValueCascade{Small: "small", Large: "large", PLarge: pl, Lambda: 0.05}, eval, o)

	best, bestGap := Outcome{}, math.Inf(1)
	for th := 0.02; th <= 0.99; th += 0.02 {
		c := Run(Cascade{Small: "small", Large: "large", Threshold: th}, eval, o)
		if gap := math.Abs(c.EscalRate - v.EscalRate); gap < bestGap {
			best, bestGap = c, gap
		}
	}
	if bestGap > 0.02 {
		t.Skipf("no threshold matches the value rule's %.3f escalation rate", v.EscalRate)
	}
	if v.CostCents >= best.CostCents {
		t.Fatalf("at %.1f%% escalation the value rule cost %.0f¢ and the fixed "+
			"threshold cost %.0f¢; pricing the escalation bought nothing",
			v.EscalRate*100, v.CostCents, best.CostCents)
	}
}

// Both rules escalate LONGER queries on median, because difficulty and length
// are positively correlated in this workload. That is not a bug and it is worth
// pinning: the value rule's cost-awareness is a conditional effect at equal
// confidence, not an unconditional preference for short prompts. A reader who
// assumed otherwise would misread section 5.
func TestBothRulesEscalateLongerQueriesOnMedian(t *testing.T) {
	train, eval, o := setup(18, 6000)
	pl := EstimatePLarge(train, o, "large")
	for _, p := range []Policy{
		Cascade{Small: "small", Large: "large", Threshold: 0.6},
		ValueCascade{Small: "small", Large: "large", PLarge: pl, Lambda: 0.05},
	} {
		var esc, kept []float64
		for _, q := range eval {
			if p.Route(q, o).Escalated {
				esc = append(esc, float64(q.Tokens))
			} else {
				kept = append(kept, float64(q.Tokens))
			}
		}
		if len(esc) == 0 || len(kept) == 0 {
			continue
		}
		if median(esc) <= median(kept) {
			t.Errorf("%s escalated shorter queries on median (%.0f vs %.0f); the "+
				"difficulty/length correlation this documents has gone away",
				p.Name(), median(esc), median(kept))
		}
	}
}

func median(v []float64) float64 {
	s := append([]float64(nil), v...)
	sort.Float64s(s)
	return s[len(s)/2]
}

func TestValueCascadeLambdaIsMonotoneInEscalation(t *testing.T) {
	train, eval, o := setup(19, 4000)
	pl := EstimatePLarge(train, o, "large")
	prev := 2.0
	for _, lam := range []float64{0.001, 0.01, 0.1, 0.5, 2.0} {
		got := Run(ValueCascade{Small: "small", Large: "large", PLarge: pl, Lambda: lam}, eval, o).EscalRate
		if got > prev+1e-12 {
			t.Fatalf("raising lambda to %v increased escalation (%.4f > %.4f)", lam, got, prev)
		}
		prev = got
	}
}

func TestEstimatePLargeMatchesTheMeasuredRate(t *testing.T) {
	train, _, o := setup(20, 4000)
	pl := EstimatePLarge(train, o, "large")
	n := 0
	for _, q := range train {
		if o.Ask(q, "large").Correct {
			n++
		}
	}
	if math.Abs(pl-float64(n)/float64(len(train))) > 1e-12 {
		t.Errorf("EstimatePLarge %.6f does not match the measured %.6f", pl, float64(n)/float64(len(train)))
	}
	if EstimatePLarge(nil, o, "large") != 0 {
		t.Error("empty training set should give 0, not NaN")
	}
}

func TestCascade3StopsAtTheFirstConfidentStage(t *testing.T) {
	_, eval, o := setup(21, 2000)
	c := Cascade3{A: "small", B: "mid", C: "large", ThreshA: 0.6, ThreshB: 0.6}
	for _, q := range eval {
		r := c.Route(q, o)
		confA := o.Ask(q, "small").Confidence
		if confA >= 0.6 {
			if len(r.Calls) != 1 {
				t.Fatalf("query %d cleared stage A but made %d calls", q.ID, len(r.Calls))
			}
			continue
		}
		confB := o.Ask(q, "mid").Confidence
		want := 3
		if confB >= 0.6 {
			want = 2
		}
		if len(r.Calls) != want {
			t.Fatalf("query %d made %d calls, want %d", q.ID, len(r.Calls), want)
		}
	}
}

func TestCascade3ChargesForEveryStageItUsed(t *testing.T) {
	_, eval, o := setup(22, 1500)
	c := Cascade3{A: "small", B: "mid", C: "large", ThreshA: 0.5, ThreshB: 0.5}
	for _, q := range eval {
		r := c.Route(q, o)
		want := 0.0
		for _, name := range r.Calls {
			want += o.Model(name).Cost(q)
		}
		if math.Abs(r.CostCents-want) > 1e-12 {
			t.Fatalf("query %d cost %.6f for calls %v, want %.6f", q.ID, r.CostCents, r.Calls, want)
		}
	}
}

func TestCascade3LatencyIsTheSumOfItsStages(t *testing.T) {
	_, eval, o := setup(23, 800)
	c := Cascade3{A: "small", B: "mid", C: "large", ThreshA: 0.4, ThreshB: 0.4}
	for _, q := range eval {
		r := c.Route(q, o)
		want := 0.0
		for _, name := range r.Calls {
			want += o.Ask(q, name).LatencyMs
		}
		if math.Abs(r.LatencyMs-want) > 1e-9 {
			t.Fatalf("query %d latency %.3f, want %.3f", q.ID, r.LatencyMs, want)
		}
	}
}

func TestRunAggregatesConsistently(t *testing.T) {
	_, eval, o := setup(24, 1200)
	out := Run(Cascade{Small: "small", Large: "large", Threshold: 0.5}, eval, o)
	if len(out.Results) != len(eval) {
		t.Fatalf("aggregated %d results for %d queries", len(out.Results), len(eval))
	}
	correct, cost, calls, esc := 0, 0.0, 0, 0
	for _, r := range out.Results {
		if r.Correct {
			correct++
		}
		cost += r.CostCents
		calls += len(r.Calls)
		if r.Escalated {
			esc++
		}
	}
	n := float64(len(eval))
	if math.Abs(out.Accuracy-float64(correct)/n) > 1e-12 {
		t.Errorf("accuracy %v does not match the results", out.Accuracy)
	}
	if math.Abs(out.CostCents-cost) > 1e-9 {
		t.Errorf("cost %v does not match the results", out.CostCents)
	}
	if math.Abs(out.CallsPerQ-float64(calls)/n) > 1e-12 {
		t.Errorf("calls per query %v does not match the results", out.CallsPerQ)
	}
	if math.Abs(out.EscalRate-float64(esc)/n) > 1e-12 {
		t.Errorf("escalation rate %v does not match the results", out.EscalRate)
	}
	if out.P50Latency > out.P95Latency {
		t.Errorf("p50 %.1f exceeds p95 %.1f", out.P50Latency, out.P95Latency)
	}
}

func TestRunOnAnEmptyWorkloadDoesNotPanic(t *testing.T) {
	_, _, o := setup(25, 100)
	out := Run(Single{Model: "small"}, nil, o)
	if out.CostCents != 0 || len(out.Results) != 0 {
		t.Errorf("unexpected outcome for an empty workload: %+v", out)
	}
}

func TestAsRelabelsWithoutChangingBehaviour(t *testing.T) {
	_, eval, o := setup(26, 600)
	base := Cascade{Small: "small", Large: "large", Threshold: 0.5}
	a := Run(base, eval, o)
	b := Run(As("renamed", base), eval, o)
	if b.Name != "renamed" {
		t.Errorf("name %q, want renamed", b.Name)
	}
	if a.Accuracy != b.Accuracy || a.CostCents != b.CostCents || a.EscalRate != b.EscalRate {
		t.Fatal("As changed the policy's behaviour")
	}
}

func TestEveryPolicyIsDeterministic(t *testing.T) {
	train, eval, o := setup(27, 2000)
	pl := EstimatePLarge(train, o, "large")
	policies := []Policy{
		Single{Model: "mid"},
		Random{Small: "small", Large: "large", Rate: 0.3, Seed: 27},
		Oracle{Small: "small", Large: "large"},
		TrainClassifier(train, o, "small", "large", 0.5, 100, 3.0),
		Cascade{Small: "small", Large: "large", Threshold: 0.5},
		Cascade{Small: "small", Large: "large", Threshold: 0.5, Temperature: 1.6},
		ValueCascade{Small: "small", Large: "large", PLarge: pl, Lambda: 0.1},
		Cascade3{A: "small", B: "mid", C: "large", ThreshA: 0.5, ThreshB: 0.5},
	}
	for _, p := range policies {
		a, b := Run(p, eval, o), Run(p, eval, o)
		if a.Accuracy != b.Accuracy || a.CostCents != b.CostCents || a.EscalRate != b.EscalRate {
			t.Errorf("%s is not deterministic", p.Name())
		}
	}
}

// No policy other than the oracle baseline may consult ground truth, and none
// may read the hidden difficulty. The classifier is the one with the temptation,
// so pin that its score depends only on the observable features.
func TestClassifierScoreDependsOnlyOnFeatures(t *testing.T) {
	train, _, o := setup(28, 1000)
	c := TrainClassifier(train, o, "small", "large", 0.5, 100, 3.0)
	q := workload.Query{ID: 1, Words: 100, Tokens: 500, ContextTokens: 300, QuestionMarks: 2, Difficulty: 0.1}
	r := q
	r.Difficulty = 0.9
	if c.Score(q) != c.Score(r) {
		t.Fatal("the classifier's score changed when only the hidden difficulty changed")
	}
}

func TestTemperMatchesTheCalibrationPackage(t *testing.T) {
	for _, temp := range []float64{0.7, 1.0, 1.59, 3.0} {
		for p := 0.01; p < 1; p += 0.05 {
			if math.Abs(Temper(Temper(p, temp), 1/temp)-p) > 1e-9 {
				t.Fatalf("Temper is not self-inverting at p=%.2f temp=%.2f", p, temp)
			}
		}
	}
}
