package sim

import (
	"math"
	"math/rand"

	"evolve/locks"
)

// Workload describes a table's steady-state traffic.
type Workload struct {
	// ReadsPerSec and WritesPerSec are arrival rates.
	ReadsPerSec  float64
	WritesPerSec float64
	// ReadSeconds and WriteSeconds are mean statement durations.
	ReadSeconds  float64
	WriteSeconds float64
	// LongQuerySeconds is the duration of the one long-running read that is
	// already in flight when the DDL arrives. This is the ingredient that
	// turns a fast DDL into an outage, and it is almost always present in a
	// real system: an analytics query, a pg_dump, an ORM that opened a
	// transaction and went to lunch.
	LongQuerySeconds float64
	// Duration of the simulated window.
	Duration float64
	Seed     int64
}

// Build generates the request stream for a workload, optionally with a DDL
// statement arriving at ddlAt.
//
// Arrivals are Poisson and durations exponential, which is the standard
// assumption and is wrong in the usual way -- real query durations are
// heavier-tailed. It is used here because the finding being measured is a
// ratio between two scenarios that share the same stream, and a shared
// mis-specification cancels. The absolute blocked-seconds figures should not
// be read as predictions.
func (w Workload) Build(ddl *Request) []Request {
	rng := rand.New(rand.NewSource(w.Seed))
	var out []Request
	id := 0
	next := func() int { id++; return id }

	if w.LongQuerySeconds > 0 {
		out = append(out, Request{
			ID: next(), Arrive: 0, Hold: w.LongQuerySeconds,
			Mode: locks.AccessShare, Label: "long-read",
		})
	}

	gen := func(rate, mean float64, mode locks.Mode, label string) {
		if rate <= 0 {
			return
		}
		t := 0.0
		for {
			t += rng.ExpFloat64() / rate
			if t >= w.Duration {
				return
			}
			out = append(out, Request{
				ID: next(), Arrive: t, Hold: rng.ExpFloat64() * mean,
				Mode: mode, Label: label,
			})
		}
	}
	gen(w.ReadsPerSec, w.ReadSeconds, locks.AccessShare, "read")
	gen(w.WritesPerSec, w.WriteSeconds, locks.RowExclusive, "write")

	if ddl != nil {
		d := *ddl
		d.ID = next()
		out = append(out, d)
	}
	return out
}

// Amplification is the ratio of query-seconds lost to the DDL's own hold
// time: the number that answers "how much did that three-second migration
// actually cost".
type Amplification struct {
	DDLHold        float64
	DDLWait        float64
	BlockedSeconds float64
	Baseline       float64
	Ratio          float64
	MaxQueue       int
	Landed         bool
	Attempts       int
}

// Measure runs the workload twice -- once without the DDL and once with it --
// and reports the difference.
//
// The paired design matters. Queries block each other even with no DDL
// present, because writes conflict with each other on a hot row set, so
// reporting raw blocked-seconds with the DDL in place would attribute
// background contention to the migration.
func Measure(w Workload, ddl Request, label string) Amplification {
	base := Run(w.Build(nil))
	with := Run(w.Build(&ddl))

	baseBlocked := base.QuerySeconds(label)
	withBlocked := with.QuerySeconds(label)

	a := Amplification{
		DDLHold:        ddl.Hold,
		BlockedSeconds: withBlocked - baseBlocked,
		Baseline:       baseBlocked,
		MaxQueue:       with.MaxQueue(),
		Landed:         with.Landed(label),
	}
	for _, r := range with.Results {
		if r.Label == label {
			a.DDLWait = r.Wait()
			a.Attempts = r.Attempts
		}
	}
	if ddl.Hold > 0 {
		a.Ratio = a.BlockedSeconds / ddl.Hold
	}
	if math.IsNaN(a.Ratio) || math.IsInf(a.Ratio, 0) {
		a.Ratio = 0
	}
	return a
}
