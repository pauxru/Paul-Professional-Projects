// Package calibration measures whether a confidence signal means what it says.
//
// This is the load-bearing measurement of the whole project. A cascade escalates
// when the cheap model is unsure. If the cheap model's "0.9" actually means "I
// am right about 70% of the time", every threshold is mis-set, and the cascade
// keeps answers it should have escalated while paying twice for the ones it does.
//
// Accuracy tells you how often a model is right. Calibration tells you whether
// you can act on its opinion about that. They are different numbers and only
// one of them determines whether a cascade works.
package calibration

import (
	"fmt"
	"math"
	"sort"
)

// Sample is one prediction with its outcome.
type Sample struct {
	Confidence float64
	Correct    bool
}

// Report is the calibration of a signal.
type Report struct {
	N int
	// Accuracy is the fraction actually correct.
	Accuracy float64
	// MeanConfidence is what the signal claimed on average. The gap between
	// this and Accuracy is the overconfidence, in plain terms.
	MeanConfidence float64
	// ECE is expected calibration error: the average gap between claimed
	// confidence and observed accuracy, weighted by bin population.
	ECE float64
	// MCE is the worst single bin, which is what bites at a threshold.
	MCE float64
	// Brier is the mean squared error of the probability, which decomposes into
	// calibration and refinement. It is included because ECE alone can be
	// gamed by a signal that always says 0.5.
	Brier float64
	// AUC is the signal's ranking quality: the probability that a correct
	// answer is scored above an incorrect one. A signal can be badly calibrated
	// and still rank perfectly, which is why both numbers are reported.
	AUC  float64
	Bins []Bin
}

// Bin is one confidence bucket.
type Bin struct {
	Lo, Hi   float64
	N        int
	MeanConf float64
	Accuracy float64
}

// Measure computes the calibration of a set of samples using equal-width bins.
func Measure(samples []Sample, nbins int) Report {
	rep := Report{N: len(samples)}
	if len(samples) == 0 {
		return rep
	}
	correct, conf, brier := 0, 0.0, 0.0
	for _, s := range samples {
		if s.Correct {
			correct++
		}
		conf += s.Confidence
		y := 0.0
		if s.Correct {
			y = 1
		}
		brier += (s.Confidence - y) * (s.Confidence - y)
	}
	n := float64(len(samples))
	rep.Accuracy = float64(correct) / n
	rep.MeanConfidence = conf / n
	rep.Brier = brier / n

	bins := make([]Bin, nbins)
	for i := range bins {
		bins[i].Lo = float64(i) / float64(nbins)
		bins[i].Hi = float64(i+1) / float64(nbins)
	}
	sums := make([]float64, nbins)
	hits := make([]int, nbins)
	for _, s := range samples {
		i := int(s.Confidence * float64(nbins))
		if i >= nbins {
			i = nbins - 1
		}
		if i < 0 {
			i = 0
		}
		bins[i].N++
		sums[i] += s.Confidence
		if s.Correct {
			hits[i]++
		}
	}
	for i := range bins {
		if bins[i].N == 0 {
			continue
		}
		bins[i].MeanConf = sums[i] / float64(bins[i].N)
		bins[i].Accuracy = float64(hits[i]) / float64(bins[i].N)
		gap := math.Abs(bins[i].MeanConf - bins[i].Accuracy)
		rep.ECE += gap * float64(bins[i].N) / n
		if gap > rep.MCE {
			rep.MCE = gap
		}
	}
	rep.Bins = bins
	rep.AUC = auc(samples)
	return rep
}

// auc is the Mann-Whitney U statistic: the probability that a randomly chosen
// correct answer scores above a randomly chosen incorrect one, with ties at 0.5.
func auc(samples []Sample) float64 {
	type sc struct {
		conf float64
		pos  bool
	}
	xs := make([]sc, len(samples))
	pos, neg := 0, 0
	for i, s := range samples {
		xs[i] = sc{s.Confidence, s.Correct}
		if s.Correct {
			pos++
		} else {
			neg++
		}
	}
	if pos == 0 || neg == 0 {
		return 0.5
	}
	sort.Slice(xs, func(i, j int) bool { return xs[i].conf < xs[j].conf })

	// Average ranks over ties, which matters because a distorted signal
	// saturates and produces a lot of them.
	ranks := make([]float64, len(xs))
	i := 0
	for i < len(xs) {
		j := i
		for j+1 < len(xs) && xs[j+1].conf == xs[i].conf {
			j++
		}
		avg := float64(i+j)/2 + 1
		for k := i; k <= j; k++ {
			ranks[k] = avg
		}
		i = j + 1
	}
	sumPos := 0.0
	for k, x := range xs {
		if x.pos {
			sumPos += ranks[k]
		}
	}
	return (sumPos - float64(pos)*float64(pos+1)/2) / (float64(pos) * float64(neg))
}

// FitTemperature finds the temperature that minimises ECE on a held-out set.
// This is temperature scaling: one parameter, fitted on data the router did not
// train on, and it is all a cascade needs to become useful.
func FitTemperature(samples []Sample) float64 {
	best, bestECE := 1.0, math.Inf(1)
	for t := 0.20; t <= 5.0; t += 0.01 {
		scaled := make([]Sample, len(samples))
		for i, s := range samples {
			scaled[i] = Sample{Confidence: Temper(s.Confidence, t), Correct: s.Correct}
		}
		if e := Measure(scaled, 12).ECE; e < bestECE {
			best, bestECE = t, e
		}
	}
	return best
}

// Temper pulls a probability towards 0.5 for t > 1.
func Temper(p, t float64) float64 {
	if t == 1 {
		return p
	}
	a := math.Pow(p, 1/t)
	b := math.Pow(1-p, 1/t)
	return a / (a + b)
}

// Diagram renders a reliability diagram as text. A perfectly calibrated signal
// puts every row's observed accuracy on its claimed confidence.
func (r Report) Diagram() string {
	s := fmt.Sprintf("%-14s %8s %10s %10s %9s\n",
		"confidence", "n", "claimed", "observed", "gap")
	s += repeat("-", 55) + "\n"
	for _, b := range r.Bins {
		if b.N == 0 {
			continue
		}
		s += fmt.Sprintf("[%.2f, %.2f) %8d %10.3f %10.3f %+9.3f\n",
			b.Lo, b.Hi, b.N, b.MeanConf, b.Accuracy, b.Accuracy-b.MeanConf)
	}
	s += fmt.Sprintf("\nmean claimed %.3f vs actual accuracy %.3f. The aggregate gap is small\n",
		r.MeanConfidence, r.Accuracy)
	s += "and the aggregate gap is a lie: the signal is too EXTREME at both ends,\n"
	s += "so the errors cancel when you average them. That is what MCE is for.\n"
	s += fmt.Sprintf("ECE %.4f   MCE %.4f   Brier %.4f   AUC %.4f\n",
		r.ECE, r.MCE, r.Brier, r.AUC)
	return s
}

func repeat(s string, n int) string {
	out := make([]byte, 0, n*len(s))
	for i := 0; i < n; i++ {
		out = append(out, s...)
	}
	return string(out)
}
