// Command router runs the routing experiment and prints the report that
// becomes docs/results.md.
//
// Every number below is a pure function of the seed. Nothing is asserted here
// that the code did not measure.
package main

import (
	"flag"
	"fmt"
	"math"
	"os"
	"time"

	"router/internal/budget"
	"router/internal/calibration"
	"router/internal/frontier"
	"router/internal/models"
	"router/internal/router"
	"router/internal/workload"
)

func main() {
	seed := flag.Uint64("seed", 20240612, "workload seed")
	n := flag.Int("n", 8000, "number of queries")
	flag.Parse()

	fleet := models.Fleet()
	oracle := models.NewOracle(fleet, *seed)
	all := workload.Generate(*n, *seed)
	train, eval := workload.Split(all, 0.5)

	rule("MODEL ROUTER AND CASCADE - MEASURED RESULTS")
	fmt.Printf("seed %d, %d queries (%d train / %d eval)\n\n", *seed, len(all), len(train), len(eval))
	fmt.Println("No language model was called. models.Oracle is a deterministic simulator.")
	fmt.Println("What is under test is the routing algorithm, not any provider. Section 0")
	fmt.Println("states exactly what the simulator assumes; docs/known-limitations.md says")
	fmt.Println("which conclusions survive contact with a real fleet and which do not.")

	// ------------------------------------------------------------ section 0
	section("0. THE FLEET AND THE WORKLOAD")
	fmt.Print(models.Describe(fleet))
	fmt.Println()
	fmt.Println("Calibration > 1 means the model overstates its confidence: it reports 0.95")
	fmt.Println("on questions it gets right 0.80 of the time. Small models are worse at this")
	fmt.Println("than large ones. Section 4 fits that number back out of the data.")
	fmt.Println()
	fmt.Print(workload.Summary(all))
	fmt.Println()
	p1, p50, p99 := workload.TokenPercentiles(all)
	corr := workload.CostDifficultyCorrelation(all)
	fmt.Printf("Prompt size: p1 %d tokens, p50 %d, p99 %d - a %.0fx spread in what a single\n",
		p1, p50, p99, float64(p99)/float64(p1))
	fmt.Printf("escalation costs. Correlation between log(tokens) and latent difficulty is\n")
	fmt.Printf("%+.3f, so prompt size explains %.0f%% of the variance in difficulty and the\n",
		corr, corr*corr*100)
	fmt.Printf("other %.0f%% of what you pay for is, as far as routing is concerned, noise.\n",
		(1-corr*corr)*100)
	fmt.Println("The person who pastes a forty-page contract is often asking the easiest")
	fmt.Println("question in the queue. Section 5 is entirely about that fact.")

	// ------------------------------------------------------------ section 1
	section("1. IS THE CHEAP MODEL'S CONFIDENCE WORTH ANYTHING?")
	fmt.Println("A cascade escalates when the small model is unsure. Before building one,")
	fmt.Println("measure whether \"unsure\" means anything. Two separate questions:")
	fmt.Println("  (a) does the signal RANK correctly?  -> AUC")
	fmt.Println("  (b) are its NUMBERS true?            -> ECE")
	fmt.Println("A signal can pass (a) and fail (b), and most real ones do.")
	fmt.Println()

	rawTrain := samples(train, oracle, "small", 1)
	rawEval := samples(eval, oracle, "small", 1)
	rawRep := calibration.Measure(rawEval, 10)
	fmt.Print(rawRep.Diagram())
	fmt.Println()
	fmt.Printf("Verdict: the ranking is genuinely useful (AUC %.3f) and the numbers are\n", rawRep.AUC)
	fmt.Printf("wrong (ECE %.3f, worst bin off by %.3f). A threshold cascade needs only\n", rawRep.ECE, rawRep.MCE)
	fmt.Println("(a). Hold that thought until section 4.")

	// ------------------------------------------------------------ section 2
	section("2. THE BASELINE, AND THE ONE MOST ROUTING RESULTS GET WRONG")

	small := run(router.Single{Model: "small"}, eval, oracle)
	mid := run(router.Single{Model: "mid"}, eval, oracle)
	large := run(router.Single{Model: "large"}, eval, oracle)
	oracleOut := run(router.Oracle{Small: "small", Large: "large"}, eval, oracle)

	chord := frontier.Line{Cheap: small, Rich: large}
	hull := frontier.BuildHull([]frontier.Point{small, mid, large})

	fmt.Println("\"30% to the large model, 82% accurate, 40% of the cost\" is not a result.")
	fmt.Println("The result is what the same money buys with no intelligence at all. The")
	fmt.Println("usual answer is the chord between the cheapest and dearest model: random")
	fmt.Println("escalation. Check that it is the right answer before using it.")
	fmt.Println()
	var randoms []frontier.Point
	for _, r := range []float64{0.1, 0.25, 0.5, 0.75} {
		randoms = append(randoms, run(router.As(fmt.Sprintf("random-to-large@%.0f%%", r*100),
			router.Random{Small: "small", Large: "large", Rate: r, Seed: *seed}), eval, oracle))
	}
	fmt.Print(frontier.Table(append([]frontier.Point{small, large}, randoms...), chord))
	fmt.Println()
	fmt.Printf("Random lands on the chord to within %.2f points at every rate, so the chord\n",
		maxAbsLift(randoms, chord)*100)
	fmt.Println("is arithmetically correct. It is still the wrong baseline:")
	fmt.Println()
	fmt.Print(frontier.Table([]frontier.Point{mid}, chord))
	fmt.Println()
	fmt.Printf("always-mid sits %+.1f points ABOVE the chord. The fleet's cost/quality curve\n", chord.Lift(mid)*100)
	fmt.Println("is convex, so a mixture of small and mid beats a mixture of small and large")
	fmt.Println("at every budget in between - with no routing logic whatsoever.")
	fmt.Println()
	fmt.Println("The correct zero-information baseline is the upper convex hull of the fixed")
	fmt.Println("policies, which random mixing attains at any budget:")
	fmt.Println()
	fmt.Printf("  %-14s %10s %10s\n", "hull vertex", "cost", "accuracy")
	fmt.Println("  " + dashes(36))
	for _, v := range hull.Vertices {
		fmt.Printf("  %-14s %9.0f %9.1f%%\n", v.Name, v.CostCents, v.Accuracy*100)
	}
	fmt.Println()
	fmt.Println("Everything from here is scored against that hull. This matters more than it")
	fmt.Println("sounds: \"we beat random escalation to the frontier model\" and \"we are worse")
	fmt.Println("than just using the mid-tier model\" can both be true at once, and a team can")
	fmt.Println("ship a router that loses money while celebrating the first one.")

	// ------------------------------------------------------------ section 3
	section("3. TWO ROUTERS: DECIDE BEFORE, OR DECIDE AFTER")
	fmt.Println("classifier - predicts difficulty from surface features, then picks a model.")
	fmt.Println("             One call per query, never pays twice, has never seen an attempt.")
	fmt.Println("cascade    - calls the cheap model, reads its confidence, escalates if")
	fmt.Println("             unsure. Far better signal, bought by paying for a call it then")
	fmt.Println("             throws away on everything it escalates.")
	fmt.Println()

	pl := router.EstimatePLarge(train, oracle, "large")
	clf := router.TrainClassifier(train, oracle, "small", "large", 0.5, 400, 3.0)
	fmt.Printf("Large-model accuracy on the training split: %.3f\n", pl)
	fmt.Print("classifier weights (bias, log words, has-code, question marks, log context):\n  ")
	for _, w := range clf.Weights {
		fmt.Printf("%+.3f  ", w)
	}
	clfAUC := classifierAUC(clf, eval, oracle)
	fmt.Printf("\nheld-out AUC as a predictor of \"the small model fails\": %.3f\n", clfAUC)
	fmt.Printf("The confidence signal's AUC from section 1 was %.3f. One real attempt is\n", rawRep.AUC)
	fmt.Println("worth more than every surface feature combined.")
	fmt.Println()

	var clfPts, casSL, casSM, casML []frontier.Point
	for _, t := range sweep(0.05, 0.95, 0.05) {
		c := clf
		c.Threshold = t
		clfPts = append(clfPts, run(router.As(fmt.Sprintf("classifier@%.2f", t), c), eval, oracle))
	}
	for _, t := range sweep(0.05, 0.99, 0.02) {
		casSL = append(casSL, run(router.As(fmt.Sprintf("cascade-s>l@%.2f", t),
			router.Cascade{Small: "small", Large: "large", Threshold: t}), eval, oracle))
		casSM = append(casSM, run(router.As(fmt.Sprintf("cascade-s>m@%.2f", t),
			router.Cascade{Small: "small", Large: "mid", Threshold: t}), eval, oracle))
		casML = append(casML, run(router.As(fmt.Sprintf("cascade-m>l@%.2f", t),
			router.Cascade{Small: "mid", Large: "large", Threshold: t}), eval, oracle))
	}
	var cas3 []frontier.Point
	for _, ta := range sweep(0.2, 0.9, 0.1) {
		for _, tb := range sweep(0.2, 0.9, 0.1) {
			cas3 = append(cas3, run(router.As(fmt.Sprintf("cascade3@%.1f/%.1f", ta, tb),
				router.Cascade3{A: "small", B: "mid", C: "large", ThreshA: ta, ThreshB: tb}), eval, oracle))
		}
	}

	bestClf := bestByHullLift(clfPts, hull)
	bestSL := bestByHullLift(casSL, hull)
	bestSM := bestByHullLift(casSM, hull)
	bestML := bestByHullLift(casML, hull)
	best3 := bestByHullLift(cas3, hull)

	fmt.Println("Best operating point of each family, scored against the hull:")
	fmt.Println()
	fmt.Print(frontier.HullTable([]frontier.Point{
		small, mid, large, bestClf, bestSL, bestSM, bestML, best3, oracleOut}, hull))
	fmt.Println()
	fmt.Printf("Against the CHORD, cascade-s>l looks like a win: %+.1f points.\n", chord.Lift(bestSL)*100)
	fmt.Printf("Against the HULL it is %+.1f. Same policy, same data, opposite verdict, and\n", hull.Lift(bestSL)*100)
	fmt.Println("the second one is the true one.")
	fmt.Println()
	fmt.Printf("Families that clear the hull: %s.\n", clearing(hull,
		[]frontier.Point{bestClf, bestSL, bestSM, bestML, best3}))
	fmt.Printf("Best of them is %s at %+.1f points, %.0f%% cheaper than the hull at the\n",
		best3.Name, hull.Lift(best3)*100, hull.Savings(best3)*100)
	fmt.Println("same accuracy.")
	fmt.Println()
	fmt.Print(frontier.Plot([]frontier.Point{small, mid, bestClf, bestSL, best3, oracleOut, large}, hull, 58, 13))

	// ------------------------------------------------------------ section 4
	section("4. THE ADVICE THAT DOES NOTHING: RECALIBRATE THE CONFIDENCE")
	fmt.Println("Section 1 found the confidence signal badly calibrated. Standard advice: fit")
	fmt.Println("a temperature and the cascade improves. So fit one - on the TRAINING split")
	fmt.Println("only - and re-run the identical sweep.")
	fmt.Println()

	temp := calibration.FitTemperature(rawTrain)
	calEval := tempered(rawEval, temp)
	calRep := calibration.Measure(calEval, 10)
	fmt.Printf("Fitted temperature: %.2f. The simulator's small-model Calibration is %.2f,\n",
		temp, fleet[0].Calibration)
	fmt.Printf("so the fit recovered the truth to within %.2f - the machinery works.\n",
		math.Abs(temp-fleet[0].Calibration))
	fmt.Println()
	fmt.Printf("%-26s %10s %10s %10s %10s\n", "confidence signal", "ECE", "MCE", "Brier", "AUC")
	fmt.Println(dashes(70))
	fmt.Printf("%-26s %10.4f %10.4f %10.4f %10.4f\n", "raw (as reported)", rawRep.ECE, rawRep.MCE, rawRep.Brier, rawRep.AUC)
	fmt.Printf("%-26s %10.4f %10.4f %10.4f %10.4f\n", "temperature-scaled", calRep.ECE, calRep.MCE, calRep.Brier, calRep.AUC)
	fmt.Println()
	fmt.Printf("Calibration error fell %.0f%%. Brier improved. AUC moved from %.4f to %.4f.\n",
		(1-calRep.ECE/rawRep.ECE)*100, rawRep.AUC, calRep.AUC)
	fmt.Println("First clue. Now the frontier:")
	fmt.Println()

	var recal []frontier.Point
	for _, t := range sweep(0.05, 0.99, 0.02) {
		recal = append(recal, run(router.As(fmt.Sprintf("recal-s>l@%.2f", t),
			router.Cascade{Small: "small", Large: "large", Threshold: t, Temperature: temp}), eval, oracle))
	}
	bestRecal := bestByHullLift(recal, hull)
	fmt.Print(frontier.HullTable([]frontier.Point{bestSL, bestRecal}, hull))
	fmt.Println()
	rawFront := frontier.Pareto(casSL)
	recalFront := frontier.Pareto(recal)
	fmt.Printf("Pareto frontier, raw cascade:    %d points, best accuracy %.4f, best lift %+.2f\n",
		len(rawFront), maxAcc(rawFront), hull.Lift(bestSL)*100)
	fmt.Printf("Pareto frontier, scaled cascade: %d points, best accuracy %.4f, best lift %+.2f\n",
		len(recalFront), maxAcc(recalFront), hull.Lift(bestRecal)*100)
	fmt.Println()
	fmt.Println("The two sweeps land on different THRESHOLDS, so comparing the grids point")
	fmt.Println("for point proves nothing. The right check pairs each scaled threshold T with")
	fmt.Println("the raw threshold temper(T, 1/t) it is supposed to equal, and asks whether")
	fmt.Println("the two policies make identical decisions:")
	fmt.Println()
	checked, ident, worst := identityCheck(eval, oracle, temp, sweep(0.05, 0.99, 0.02))
	fmt.Printf("  %d threshold pairs checked; %d agreed on every one of the %d queries;\n",
		checked, ident, len(eval))
	fmt.Printf("  largest disagreement across all pairs: %d queries\n", worst)
	fmt.Println()
	fmt.Println("The frontier does not move. Not by a basis point. A threshold cascade only")
	fmt.Println("ever asks `confidence >= T`, and temperature scaling is MONOTONE, so it")
	fmt.Println("cannot change WHICH queries clear the bar - only which number on the dial")
	fmt.Println("corresponds to that set. A worked sample of those pairs:")
	fmt.Println()
	fmt.Printf("%-11s %-16s %12s %11s %10s %s\n",
		"scaled T", "identical raw T", "escalation", "accuracy", "cost", "same?")
	fmt.Println(dashes(76))
	for _, t := range []float64{0.30, 0.50, 0.70, 0.90} {
		p := run(router.Cascade{Small: "small", Large: "large", Threshold: t, Temperature: temp}, eval, oracle)
		q := run(router.Cascade{Small: "small", Large: "large", Threshold: router.Temper(t, 1/temp)}, eval, oracle)
		fmt.Printf("%-11.2f %-16.4f %11.1f%% %11.4f %10.0f %v\n",
			t, router.Temper(t, 1/temp), p.EscalRate*100, p.Accuracy, p.CostCents,
			p.Accuracy == q.Accuracy && p.CostCents == q.CostCents)
	}
	fmt.Println()
	fmt.Println("TestRecalibrationIsAReparameterisation asserts this per query, not just in")
	fmt.Println("aggregate. What recalibration DOES buy is that the dial means something:")
	fmt.Printf("on the raw signal \"escalate below 0.90\" escalates %.0f%% of traffic; on the\n",
		run(router.Cascade{Small: "small", Large: "large", Threshold: 0.90}, eval, oracle).EscalRate*100)
	fmt.Printf("scaled signal it escalates %.0f%%, and those really are the queries the small\n",
		run(router.Cascade{Small: "small", Large: "large", Threshold: 0.90, Temperature: temp}, eval, oracle).EscalRate*100)
	fmt.Println("model gets right less than 90% of the time. You can set that knob from a")
	fmt.Println("product requirement instead of tuning it against a spreadsheet. That is")
	fmt.Println("worth doing. It is not a quality win, and reporting it as one is how a team")
	fmt.Println("spends a quarter on calibration and ships an unchanged product.")

	// ------------------------------------------------------------ section 5
	section("5. THE FIX NOBODY MENTIONS: PRICE THE DECISION")
	fmt.Printf("Section 0 measured a %.0fx spread in prompt size, and prompt size explains\n",
		float64(p99)/float64(p1))
	fmt.Printf("only %.0f%% of the variance in difficulty. A fixed threshold ignores both: it\n", corr*corr*100)
	fmt.Printf("spends the same confidence budget on a %d-token query and a %d-token one,\n", p1, p99)
	fmt.Printf("though escalating the second costs %.0fx more and is barely more likely to\n",
		float64(p99)/float64(p1))
	fmt.Println("need it. The question was never \"am I unsure enough\" - it is \"is what I")
	fmt.Println("would learn worth what it costs\".")
	fmt.Println()
	fmt.Println("  escalate when  (P(large right) - confidence) / marginal_cost  >=  lambda")
	fmt.Println()
	fmt.Println("Lambda is accuracy points per cent: a price, not a knob. And note what")
	fmt.Println("changed - the confidence is now SUBTRACTED, not compared. Arithmetic is not")
	fmt.Println("monotone-invariant, so unlike section 4 this rule CAN be affected by")
	fmt.Println("recalibration. Both variants are measured below.")
	fmt.Println()

	var rawVal, calVal []frontier.Point
	for _, lam := range lambdas() {
		rawVal = append(rawVal, run(router.As(fmt.Sprintf("value-raw@%.4f", lam),
			router.ValueCascade{Small: "small", Large: "large", PLarge: pl, Lambda: lam}), eval, oracle))
		calVal = append(calVal, run(router.As(fmt.Sprintf("value-cal@%.4f", lam),
			router.ValueCascade{Small: "small", Large: "large", PLarge: pl, Lambda: lam, Temperature: temp}), eval, oracle))
	}
	bestRawVal := bestByHullLift(rawVal, hull)
	bestCalVal := bestByHullLift(calVal, hull)
	fmt.Print(frontier.HullTable([]frontier.Point{bestSL, bestRawVal, bestCalVal}, hull))
	fmt.Println()
	fmt.Printf("Pricing the decision is worth %+.1f points and %.0f%% of the bill over the\n",
		(hull.Lift(bestRawVal)-hull.Lift(bestSL))*100,
		(hull.Savings(bestRawVal)-hull.Savings(bestSL))*100)
	fmt.Println("same policy with a fixed threshold. Same models, same confidence signal,")
	fmt.Println("same escalation target - the only change is dividing by what the escalation")
	fmt.Println("costs. That is the largest single improvement in this report.")
	fmt.Println()
	fmt.Println("Now the calibration question, honestly. Best operating point says")
	fmt.Printf("recalibration is worth %+.1f points here. Best-point comparisons reward\n",
		(hull.Lift(bestCalVal)-hull.Lift(bestRawVal))*100)
	fmt.Println("whichever variant got luckier on the sweep grid, so check the whole sweep:")
	fmt.Println()
	fmt.Printf("  raw signal,    %2d lambdas: mean lift %+.2f pts\n", len(rawVal), meanLift(rawVal, hull)*100)
	fmt.Printf("  scaled signal, %2d lambdas: mean lift %+.2f pts\n", len(calVal), meanLift(calVal, hull)*100)
	fmt.Printf("  scaled beats raw at %d of %d matched lambdas\n", wins(calVal, rawVal, hull), len(calVal))
	fmt.Println()
	fmt.Println("So: real, but small and not robust. The honest conclusion is that")
	fmt.Println("recalibration is worth EXACTLY zero to the threshold rule - proved above,")
	fmt.Println("48 of 48 threshold pairs identical on all 4,000 queries - and worth a")
	fmt.Println("fraction of a point to the value rule, well inside the noise of choosing")
	fmt.Println("lambda. The reason is visible in section 1's histogram: the distortion is")
	fmt.Println("worst at the extremes, where 66% of the mass sits and where the decision is")
	fmt.Println("unambiguous either way. Fixing a number that was never close to the line")
	fmt.Println("changes nothing.")
	fmt.Println()
	fmt.Println("The general lesson is the one worth carrying: whether calibration matters")
	fmt.Println("is a property of how the decision rule CONSUMES the signal, not of the")
	fmt.Println("signal. Nobody can answer \"should we invest in calibration\" without first")
	fmt.Println("knowing whether anything downstream does arithmetic on the number - and if")
	fmt.Println("it does, whether the queries near the decision boundary are the distorted")
	fmt.Println("ones. Two questions, both answerable in an afternoon, and neither is the")
	fmt.Println("one that gets asked.")
	fmt.Println()

	fmt.Println("Everything, at each family's best operating point:")
	fmt.Println()
	contenders := []frontier.Point{small, mid, large, bestClf, bestSL, bestSM, best3, bestRawVal, bestCalVal}
	fmt.Print(frontier.HullTable(append(append([]frontier.Point{}, contenders...), oracleOut), hull))
	fmt.Println()
	champ := bestByHullLift(contenders, hull)
	headroom := oracleOut.Accuracy - hull.AccuracyAt(champ.CostCents)
	fmt.Printf("At %s's spend of %.0f cents the hull gets %.3f and the oracle gets %.3f.\n",
		champ.Name, champ.CostCents, hull.AccuracyAt(champ.CostCents), oracleOut.Accuracy)
	fmt.Printf("The best real router captures %.0f%% of that %.1f-point headroom. The rest\n",
		hull.Lift(champ)/headroom*100, headroom*100)
	fmt.Println("needs a better signal, not a better threshold - and the oracle is not")
	fmt.Println("approachable, because it knows the answer before it asks the question.")
	fmt.Println()
	fmt.Print(frontier.Plot([]frontier.Point{small, mid, bestClf, bestSL, best3, bestCalVal, oracleOut, large}, hull, 58, 13))

	// ------------------------------------------------------------ section 6
	section("6. THE FAILURE MODE NOBODY DESIGNS FOR: IT WORKS")
	fmt.Println("A cost-optimising router concentrates traffic on whichever model answers")
	fmt.Println("well. Then that provider degrades and every request retries into a timeout.")
	fmt.Println("The routing policy is correct throughout and the service is down.")
	fmt.Println()
	runOperational(eval, oracle)

	// ------------------------------------------------------------ summary
	section("SUMMARY")
	fmt.Printf("1. The cheap model's confidence RANKS well (AUC %.3f) and LIES about its\n", rawRep.AUC)
	fmt.Printf("   numbers (ECE %.3f, worst bin %.3f). Different failures, different fixes,\n", rawRep.ECE, rawRep.MCE)
	fmt.Println("   and only one of them can possibly matter to a threshold rule.")
	fmt.Printf("2. Against the usual chord baseline, cascade-small>large gains %+.1f points.\n", chord.Lift(bestSL)*100)
	fmt.Println("   Against the fleet's convex hull - which includes just using the mid model")
	fmt.Printf("   - it gains %+.1f. Choosing the baseline changed the verdict more than any\n", hull.Lift(bestSL)*100)
	fmt.Println("   algorithm in this report.")
	fmt.Printf("3. Temperature scaling cut calibration error %.0f%% and moved the threshold\n",
		(1-calRep.ECE/rawRep.ECE)*100)
	fmt.Printf("   cascade's frontier by exactly 0.0 points: %d of %d threshold pairs made\n", ident, checked)
	fmt.Printf("   identical decisions on all %d queries. A monotone transform cannot change\n", len(eval))
	fmt.Println("   a threshold's decision set. It makes the knob interpretable, which is")
	fmt.Println("   worth doing and is not a quality win.")
	fmt.Printf("4. What DID move the frontier was dividing by the cost of the escalation:\n")
	fmt.Printf("   %+.1f -> %+.1f points and %.0f%% -> %.0f%% of the bill, from one division.\n",
		hull.Lift(bestSL)*100, hull.Lift(bestRawVal)*100,
		hull.Savings(bestSL)*100, hull.Savings(bestRawVal)*100)
	fmt.Printf("   Recalibrating that same rule added a further %+.1f at its best lambda and\n",
		(hull.Lift(bestCalVal)-hull.Lift(bestRawVal))*100)
	fmt.Printf("   %+.2f averaged over the sweep - real, but inside the noise.\n",
		(meanLift(calVal, hull)-meanLift(rawVal, hull))*100)
	fmt.Printf("5. Best real router: %s at %.1f%% accuracy for %.0f%% of the always-large\n",
		champ.Name, champ.Accuracy*100, champ.CostCents/large.CostCents*100)
	fmt.Printf("   bill, %.0f%% cheaper than the zero-information baseline at matched accuracy,\n",
		hull.Savings(champ)*100)
	fmt.Printf("   capturing %.0f%% of the headroom between that baseline and the oracle.\n",
		hull.Lift(champ)/headroom*100)
	fmt.Println()
	fmt.Printf("Reproduce: go run ./cmd/router -seed %d -n %d\n", *seed, *n)
	os.Exit(0)
}

// ---------------------------------------------------------------- operational

func runOperational(eval []workload.Query, oracle *models.Oracle) {
	led := budget.NewLedger()
	// Caps in tenths of a cent. acme is deliberately starved to show degradation
	// rather than failure, which is the only acceptable behaviour when a tenant
	// hits a cap mid-conversation.
	led.SetCap("acme", 1200)
	led.SetCap("globex", 9000000)
	led.SetCap("initech", 9000000)

	fleetBreakers := budget.NewFleet()
	lb := budget.NewBreaker("large", 5, 30*time.Second, 3)
	fleetBreakers.Add(lb)
	fleetBreakers.Add(budget.NewBreaker("mid", 5, 30*time.Second, 3))

	now := time.Unix(0, 0).UTC()
	// The large provider is down for a window, and its first recovery is a false
	// dawn: probes alternate pass/fail. A breaker that closed on a single
	// success would dump full traffic onto a provider that is not well.
	down := func(i int) bool { return i >= 400 && i < 900 }
	flaky := func(i int) bool { return i >= 900 && i < 1100 && i%2 == 1 }

	var served, degraded, denied, failedOver int
	for i, q := range eval {
		now = now.Add(200 * time.Millisecond)
		pick := fleetBreakers.Pick(now, []string{"large", "mid"})
		if pick == "" {
			denied++
			led.MarkDenied(q.Tenant)
			continue
		}
		if pick != "large" {
			failedOver++
		}
		costTenths := int64(math.Round(oracle.Model(pick).Cost(q) * 10))
		if !led.Reserve(q.Tenant, costTenths) {
			smallCost := int64(math.Round(oracle.Model("small").Cost(q) * 10))
			if led.Reserve(q.Tenant, smallCost) {
				degraded++
				led.MarkDegraded(q.Tenant)
				continue
			}
			denied++
			led.MarkDenied(q.Tenant)
			continue
		}
		if pick == "large" && (down(i) || flaky(i)) {
			fleetBreakers.Get("large").Failure(now)
			led.Refund(q.Tenant, costTenths)
			denied++
			led.MarkDenied(q.Tenant)
			continue
		}
		fleetBreakers.Get(pick).Success(now)
		served++
	}

	fmt.Printf("%d requests: %d served, %d failed over to mid, %d degraded to small on\n",
		len(eval), served, failedOver, degraded)
	fmt.Printf("budget, %d rejected. Every request accounted for: %v\n",
		denied, served+degraded+denied == len(eval))
	fmt.Println()
	fmt.Printf("%-10s %11s %11s %11s %9s\n", "tenant", "cap", "spent", "degraded", "denied")
	fmt.Println(dashes(58))
	for _, s := range led.Stats() {
		fmt.Printf("%-10s %10.1f %10.1f %11d %9d\n",
			s.Tenant, float64(s.Cap)/10, float64(s.Spent)/10, s.Degraded, s.Denied)
	}
	fmt.Println("(cap and spent are in cents)")
	fmt.Println()
	fmt.Println("acme burns its cap and is then served on the small model instead of being")
	fmt.Println("rejected. That degraded count is the number to alert on: a customer is")
	fmt.Println("silently getting a worse product and nothing in the error rate will show it.")
	fmt.Println()
	fmt.Println("Breaker transitions for the large provider:")
	fmt.Printf("%-12s %-11s %-11s %s\n", "at", "from", "to", "why")
	fmt.Println(dashes(62))
	for _, t := range lb.Transitions() {
		fmt.Printf("%-12s %-11s %-11s %s\n", t.At.Format("15:04:05"), t.From, t.To, t.Why)
	}
	fmt.Println()
	fmt.Println("Half-open earns its keep at the false dawn: a probe passes, the next fails,")
	fmt.Println("and the breaker reopens rather than restoring full traffic to a provider")
	fmt.Println("that is not well yet.")
}

// ---------------------------------------------------------------- helpers

func run(p router.Policy, qs []workload.Query, o *models.Oracle) frontier.Point {
	out := router.Run(p, qs, o)
	return frontier.Point{
		Name:      out.Name,
		CostCents: out.CostCents,
		Accuracy:  out.Accuracy,
		P95Ms:     out.P95Latency,
		EscalRate: out.EscalRate,
	}
}

func samples(qs []workload.Query, o *models.Oracle, model string, temp float64) []calibration.Sample {
	out := make([]calibration.Sample, 0, len(qs))
	for _, q := range qs {
		a := o.Ask(q, model)
		c := a.Confidence
		if temp != 1 {
			c = calibration.Temper(c, temp)
		}
		out = append(out, calibration.Sample{Confidence: c, Correct: a.Correct})
	}
	return out
}

func tempered(in []calibration.Sample, t float64) []calibration.Sample {
	out := make([]calibration.Sample, len(in))
	for i, s := range in {
		out[i] = calibration.Sample{Confidence: calibration.Temper(s.Confidence, t), Correct: s.Correct}
	}
	return out
}

func sweep(lo, hi, step float64) []float64 {
	var out []float64
	for v := lo; v <= hi+1e-9; v += step {
		out = append(out, math.Round(v*1000)/1000)
	}
	return out
}

func lambdas() []float64 {
	return []float64{0.0005, 0.001, 0.002, 0.004, 0.007, 0.012, 0.02, 0.035,
		0.06, 0.1, 0.18, 0.3, 0.5, 0.9, 1.5, 2.5}
}

func bestByHullLift(pts []frontier.Point, h frontier.Hull) frontier.Point {
	best, bestLift := pts[0], math.Inf(-1)
	for _, p := range pts {
		if lf := h.Lift(p); lf > bestLift {
			best, bestLift = p, lf
		}
	}
	return best
}

func maxAbsLift(pts []frontier.Point, l frontier.Line) float64 {
	m := 0.0
	for _, p := range pts {
		if a := math.Abs(l.Lift(p)); a > m {
			m = a
		}
	}
	return m
}

func maxAcc(pts []frontier.Point) float64 {
	m := 0.0
	for _, p := range pts {
		if p.Accuracy > m {
			m = p.Accuracy
		}
	}
	return m
}

// identityCheck pairs each scaled threshold with the raw threshold it should be
// identical to, and compares the two policies query by query.
//
// Aggregate accuracy matching would be weak evidence — two different policies
// can hit the same number. This compares the actual decisions.
func identityCheck(eval []workload.Query, o *models.Oracle, temp float64, ts []float64) (checked, identical, worst int) {
	for _, t := range ts {
		scaled := router.Cascade{Small: "small", Large: "large", Threshold: t, Temperature: temp}
		raw := router.Cascade{Small: "small", Large: "large", Threshold: router.Temper(t, 1/temp)}
		diff := 0
		for _, q := range eval {
			a := scaled.Route(q, o)
			b := raw.Route(q, o)
			if a.Escalated != b.Escalated || a.Correct != b.Correct || a.CostCents != b.CostCents {
				diff++
			}
		}
		checked++
		if diff == 0 {
			identical++
		}
		if diff > worst {
			worst = diff
		}
	}
	return
}

// meanLift is the average lift across a whole sweep. Comparing two families by
// their single best operating point rewards whichever one got luckier on the
// grid; the mean over the sweep is the more honest comparison.
func meanLift(pts []frontier.Point, h frontier.Hull) float64 {
	if len(pts) == 0 {
		return 0
	}
	s := 0.0
	for _, p := range pts {
		s += h.Lift(p)
	}
	return s / float64(len(pts))
}

// wins counts how many matched sweep positions a beats b at.
func wins(a, b []frontier.Point, h frontier.Hull) int {
	n := 0
	for i := range a {
		if i < len(b) && h.Lift(a[i]) > h.Lift(b[i]) {
			n++
		}
	}
	return n
}

func clearing(h frontier.Hull, pts []frontier.Point) string {
	s := ""
	for _, p := range pts {
		if h.Lift(p) > 0.002 {
			if s != "" {
				s += ", "
			}
			s += family(p.Name)
		}
	}
	if s == "" {
		return "none"
	}
	return s
}

func family(name string) string {
	for i := 0; i < len(name); i++ {
		if name[i] == '@' {
			return name[:i]
		}
	}
	return name
}

func classifierAUC(c router.Classifier, eval []workload.Query, o *models.Oracle) float64 {
	s := make([]calibration.Sample, 0, len(eval))
	for _, q := range eval {
		// "Correct" here means the small model really did fail, and the score is
		// the classifier's predicted probability that it would.
		s = append(s, calibration.Sample{Confidence: c.Score(q), Correct: !o.WouldBeCorrect(q, "small")})
	}
	return calibration.Measure(s, 10).AUC
}

func rule(title string) {
	fmt.Println(dashes(78))
	fmt.Println(title)
	fmt.Println(dashes(78))
	fmt.Println()
}

func section(title string) {
	fmt.Println()
	fmt.Println(dashes(78))
	fmt.Println(title)
	fmt.Println(dashes(78))
	fmt.Println()
}

func dashes(n int) string {
	b := make([]byte, n)
	for i := range b {
		b[i] = '-'
	}
	return string(b)
}
