// Package corpus builds pairs of legacy/modern responses with known ground
// truth, so that a ruleset can be scored rather than argued about.
//
// Every pair is (legacy, modern) plus a flag saying whether a real defect was
// injected. Noise is applied to every pair; defects are applied to some. A
// ruleset that reports a difference on a noise-only pair has produced a false
// positive; one that reports nothing on a defective pair has produced a false
// negative — which, in a shadow run, means the bug reaches production wearing a
// green tick.
//
// The defects are deliberately placed both inside and outside the noisy regions
// of the document, because that is the situation ignore rules get wrong. A
// defect in a quiet corner of the response is caught by anything. A defect in
// the same subtree as a genuinely volatile field is caught only by a rule
// precise enough to distinguish them.
package corpus

import (
	"encoding/json"
	"fmt"
)

// Rng is SplitMix64, so a corpus is a pure function of its seed and the numbers
// in docs/results.md reproduce exactly.
type Rng struct{ s uint64 }

func NewRng(seed uint64) *Rng { return &Rng{s: seed} }

func (r *Rng) Next() uint64 {
	r.s += 0x9E3779B97F4A7C15
	z := r.s
	z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9
	z = (z ^ (z >> 27)) * 0x94D049BB133111EB
	return z ^ (z >> 31)
}

func (r *Rng) Below(n int) int       { return int(r.Next() % uint64(n)) }
func (r *Rng) Chance(p float64) bool { return float64(r.Below(1_000_000)) < p*1_000_000 }

// Defect names each injectable bug. They are exported so the results table can
// break recall down per defect, which is far more actionable than an aggregate:
// "your rules miss 30% of defects" is a shrug, "your rules miss every rounding
// error" is a change request.
type Defect string

const (
	None                Defect = ""
	TotalOffByAPenny    Defect = "total_off_by_a_penny"
	StatusCaseChanged   Defect = "status_case_changed"
	DiscountFieldLost   Defect = "discount_field_lost"
	CustomerTierChanged Defect = "customer_tier_changed"
	CurrencyChanged     Defect = "currency_changed"
	LineDropped         Defect = "line_dropped"
	CustomerIDStringly  Defect = "customer_id_became_string"
	TimestampEmptied    Defect = "timestamp_emptied"
	RequestIDNotAUUID   Defect = "request_id_not_a_uuid"
	PlacedAtBecameEpoch Defect = "placed_at_became_epoch"
)

// AllDefects is the injection order; index 0 is "no defect".
var AllDefects = []Defect{
	TotalOffByAPenny,
	StatusCaseChanged,
	DiscountFieldLost,
	CustomerTierChanged,
	CurrencyChanged,
	LineDropped,
	CustomerIDStringly,
	TimestampEmptied,
	RequestIDNotAUUID,
	PlacedAtBecameEpoch,
}

// Pair is one shadow comparison with its ground truth.
type Pair struct {
	Endpoint string
	Legacy   []byte
	Modern   []byte
	Defect   Defect
}

func (p Pair) Defective() bool { return p.Defect != None }

func base(r *Rng) map[string]any {
	nLines := 2 + r.Below(3)
	lines := make([]any, 0, nLines)
	subtotal := 0.0
	for i := 0; i < nLines; i++ {
		qty := float64(1 + r.Below(4))
		price := float64(100+r.Below(9000)) / 100
		disc := float64(r.Below(500)) / 100
		lines = append(lines, map[string]any{
			"sku":      fmt.Sprintf("SKU-%05d", r.Below(100000)),
			"qty":      qty,
			"price":    price,
			"discount": disc,
			"total":    round2(qty*price - disc),
		})
		subtotal += round2(qty*price - disc)
	}
	subtotal = round2(subtotal)
	tax := round2(subtotal * 0.2)
	return map[string]any{
		"order": map[string]any{
			"id":       fmt.Sprintf("ORD-%08d", r.Below(100000000)),
			"status":   []string{"PLACED", "PICKING", "SHIPPED"}[r.Below(3)],
			"currency": "GBP",
			"placedAt": "2024-06-12T09:31:44Z",
			"customer": map[string]any{
				"id":   float64(10000 + r.Below(90000)),
				"name": []string{"A Patel", "B Nkemelu", "C Rossi"}[r.Below(3)],
			},
			"lines":    lines,
			"subtotal": subtotal,
			"tax":      tax,
			"total":    round2(subtotal + tax),
		},
		"meta": map[string]any{
			"requestId":    uuid(r),
			"generatedAt":  "2024-06-12T09:31:45Z",
			"durationMs":   float64(3 + r.Below(40)),
			"cache":        []string{"hit", "miss"}[r.Below(2)],
			"customerTier": []string{"gold", "silver", "standard"}[r.Below(3)],
			"apiVersion":   "2019-05-01",
		},
	}
}

func round2(f float64) float64 {
	return float64(int64(f*100+0.5)) / 100
}

func uuid(r *Rng) string {
	b := make([]byte, 16)
	for i := range b {
		b[i] = byte(r.Below(256))
	}
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

// applyNoise makes the modern response differ from the legacy one in every way
// that does *not* matter. This is not pessimism about the corpus; it is what
// two independent implementations of the same endpoint actually produce.
func applyNoise(doc map[string]any, r *Rng) {
	meta := doc["meta"].(map[string]any)
	meta["requestId"] = uuid(r)
	meta["generatedAt"] = "2024-06-12T09:31:46Z"
	meta["durationMs"] = float64(3 + r.Below(40))
	meta["cache"] = []string{"hit", "miss"}[r.Below(2)]

	ord := doc["order"].(map[string]any)
	lines := ord["lines"].([]any)
	// A different query plan returns the same rows in a different order.
	if len(lines) > 1 && r.Chance(0.7) {
		i := r.Below(len(lines))
		j := r.Below(len(lines))
		lines[i], lines[j] = lines[j], lines[i]
	}
	// Float accumulation in a different order lands a few ULPs away.
	ord["subtotal"] = ord["subtotal"].(float64) + 1e-12
}

func applyDefect(doc map[string]any, d Defect, r *Rng) {
	ord := doc["order"].(map[string]any)
	meta := doc["meta"].(map[string]any)
	lines := ord["lines"].([]any)
	switch d {
	case TotalOffByAPenny:
		ord["total"] = round2(ord["total"].(float64) - 0.01)
	case StatusCaseChanged:
		ord["status"] = lower(ord["status"].(string))
	case DiscountFieldLost:
		i := r.Below(len(lines))
		delete(lines[i].(map[string]any), "discount")
	case CustomerTierChanged:
		// Downgrade, never a no-op. An earlier version assigned "standard"
		// unconditionally, which silently produced pairs labelled defective
		// whose bytes were identical. Every ruleset then had a recall ceiling
		// below 1.0 that looked like a diff-engine weakness and was a corpus
		// bug. See docs/adr/0004.
		meta["customerTier"] = downgrade(meta["customerTier"].(string))
	case CurrencyChanged:
		ord["currency"] = "USD"
	case LineDropped:
		if len(lines) > 1 {
			ord["lines"] = lines[:len(lines)-1]
		}
	case CustomerIDStringly:
		c := ord["customer"].(map[string]any)
		c["id"] = fmt.Sprintf("%v", c["id"])
	case TimestampEmptied:
		meta["generatedAt"] = ""
	case RequestIDNotAUUID:
		meta["requestId"] = "null"
	case PlacedAtBecameEpoch:
		ord["placedAt"] = float64(1718184704)
	}
}

func lower(s string) string {
	b := []byte(s)
	for i := range b {
		if b[i] >= 'A' && b[i] <= 'Z' {
			b[i] += 32
		}
	}
	return string(b)
}

// downgrade returns a tier that is never the one passed in, so the defect it
// backs always changes the document.
func downgrade(tier string) string {
	switch tier {
	case "gold":
		return "silver"
	case "silver":
		return "standard"
	default:
		return "gold"
	}
}

// Generate builds n pairs. defectRate is the fraction that carry a real bug;
// which bug is chosen round-robin so every defect gets comparable coverage.
func Generate(n int, seed uint64, defectRate float64) []Pair {
	r := NewRng(seed)
	out := make([]Pair, 0, n)
	next := 0
	for i := 0; i < n; i++ {
		legacy := base(r)
		modern := clone(legacy)
		applyNoise(modern, r)
		d := None
		if r.Chance(defectRate) {
			d = AllDefects[next%len(AllDefects)]
			next++
			applyDefect(modern, d, r)
		}
		lb, _ := json.Marshal(legacy)
		mb, _ := json.Marshal(modern)
		out = append(out, Pair{Endpoint: "/orders/{id}", Legacy: lb, Modern: mb, Defect: d})
	}
	return out
}

// GenerateEach builds exactly one pair per defect plus one clean pair, for
// per-defect reporting.
func GenerateEach(seed uint64, repeats int) []Pair {
	r := NewRng(seed)
	var out []Pair
	for i := 0; i < repeats; i++ {
		for _, d := range append([]Defect{None}, AllDefects...) {
			legacy := base(r)
			modern := clone(legacy)
			applyNoise(modern, r)
			if d != None {
				applyDefect(modern, d, r)
			}
			lb, _ := json.Marshal(legacy)
			mb, _ := json.Marshal(modern)
			out = append(out, Pair{Endpoint: "/orders/{id}", Legacy: lb, Modern: mb, Defect: d})
		}
	}
	return out
}

func clone(v map[string]any) map[string]any {
	b, _ := json.Marshal(v)
	var out map[string]any
	_ = json.Unmarshal(b, &out)
	return out
}
