//! Measurement, and one invariant that audits the simulator itself.
//!
//! # Little's Law as a test, not a talking point
//!
//! `L = lambda * W`: the mean number of requests in a system equals the
//! arrival rate times the mean time each spends there. It holds for *any*
//! stable queueing system regardless of arrival distribution, service
//! distribution, scheduling policy, or number of servers. It assumes almost
//! nothing, which is exactly what makes it useful here.
//!
//! Because it must hold, it can be checked. This crate measures `L` by
//! time-averaging the number of in-flight requests, measures `lambda` and `W`
//! from the completion records, and asserts they agree. Those three
//! quantities are computed from *different* parts of the simulator: `L` from
//! the event loop's occupancy samples, `W` from per-request timestamps,
//! `lambda` from arrival times. A bookkeeping error in the event loop breaks
//! the identity; nothing else in the crate would notice.
//!
//! This is the same discipline as asserting that Shapley values sum to the
//! total effect: find a quantity your system must satisfy for structural
//! reasons, then compute both sides independently and compare. It is the
//! cheapest real check available on a simulation.

use crate::engine::Completed;
use crate::workload::Kind;

/// Exact percentiles from a sorted sample.
///
/// Exact rather than a t-digest or a reservoir because the sample fits in
/// memory and the report is about tail latency. An approximate p99 in a
/// document arguing that the tail is the story would be self-defeating.
#[derive(Debug, Clone)]
pub struct Latencies {
    sorted_us: Vec<u64>,
}

impl Latencies {
    pub fn from(values: impl IntoIterator<Item = u64>) -> Self {
        let mut sorted_us: Vec<u64> = values.into_iter().collect();
        sorted_us.sort_unstable();
        Latencies { sorted_us }
    }

    pub fn len(&self) -> usize {
        self.sorted_us.len()
    }

    pub fn is_empty(&self) -> bool {
        self.sorted_us.is_empty()
    }

    /// Nearest-rank percentile. `q` in [0, 1].
    pub fn quantile(&self, q: f64) -> u64 {
        if self.sorted_us.is_empty() {
            return 0;
        }
        let q = q.clamp(0.0, 1.0);
        let rank = (q * self.sorted_us.len() as f64).ceil() as usize;
        self.sorted_us[rank.saturating_sub(1).min(self.sorted_us.len() - 1)]
    }

    pub fn mean(&self) -> f64 {
        if self.sorted_us.is_empty() {
            return 0.0;
        }
        self.sorted_us.iter().sum::<u64>() as f64 / self.sorted_us.len() as f64
    }

    pub fn p50(&self) -> u64 {
        self.quantile(0.50)
    }
    pub fn p95(&self) -> u64 {
        self.quantile(0.95)
    }
    pub fn p99(&self) -> u64 {
        self.quantile(0.99)
    }
    pub fn max(&self) -> u64 {
        self.sorted_us.last().copied().unwrap_or(0)
    }
}

/// Evidence for or against Little's Law on one run.
#[derive(Debug, Clone, Copy)]
pub struct Little {
    /// Time-averaged in-flight count, sampled by the event loop.
    pub measured_l: f64,
    /// Arrival rate of *admitted* requests, per second.
    pub lambda: f64,
    /// Mean sojourn time in seconds.
    pub w: f64,
}

impl Little {
    pub fn predicted_l(&self) -> f64 {
        self.lambda * self.w
    }

    /// Relative disagreement between the two independent estimates of L.
    pub fn relative_error(&self) -> f64 {
        let predicted = self.predicted_l();
        if predicted.abs() < 1e-9 {
            return 0.0;
        }
        (self.measured_l - predicted).abs() / predicted
    }

    /// Whether the identity holds to within simulation noise.
    ///
    /// The tolerance is not tight because a finite run has edge effects:
    /// requests in flight when the run ends contribute to `L` but never
    /// contribute a `W`. The report drains the queue before measuring, which
    /// is why 2% is achievable at all.
    pub fn holds(&self) -> bool {
        self.relative_error() < 0.02
    }
}

/// Everything one configuration produced.
#[derive(Debug, Clone)]
pub struct RunStats {
    pub completed: Vec<Completed>,
    pub rejected: Vec<crate::workload::Request>,
    pub offered: usize,
    pub little: Little,
    pub sim_duration_s: f64,
    pub evictions: u32,
    pub mean_batch: f64,
    /// True when the run hit the event ceiling instead of draining.
    ///
    /// Every derived statistic on a truncated run is meaningless -- the
    /// throughput is whatever fraction happened to finish before the ceiling
    /// and Little's Law no longer applies. The flag exists because the first
    /// time this happened the run reported `0.00 requests/s` and a plausible
    /// SLO attainment, and nothing in the output said the number was
    /// fiction. `tests/results_integrity.rs` asserts no reported run is
    /// truncated.
    pub truncated: bool,
    /// Kept so slowdown can be computed after the fact without threading the
    /// engine config through every call site.
    pub engine: crate::engine::EngineConfig,
}

impl RunStats {
    pub fn ttft(&self) -> Latencies {
        Latencies::from(self.completed.iter().map(|c| c.ttft_us()))
    }

    pub fn tpot(&self) -> Latencies {
        Latencies::from(self.completed.iter().map(|c| c.tpot_us()))
    }

    /// Slowdown distribution, scaled by 1000 so it can share the integer
    /// percentile machinery. Divide by 1000 to read as a ratio.
    pub fn slowdown_milli(&self) -> Latencies {
        Latencies::from(
            self.completed
                .iter()
                .map(|c| (c.slowdown(&self.engine) * 1000.0) as u64),
        )
    }

    pub fn slowdown_milli_for(&self, kind: Kind) -> Latencies {
        Latencies::from(
            self.completed
                .iter()
                .filter(|c| c.req.kind == kind)
                .map(|c| (c.slowdown(&self.engine) * 1000.0) as u64),
        )
    }

    pub fn ttft_for(&self, kind: Kind) -> Latencies {
        Latencies::from(
            self.completed
                .iter()
                .filter(|c| c.req.kind == kind)
                .map(|c| c.ttft_us()),
        )
    }

    pub fn ttft_for_tenant(&self, tenant: u8) -> Latencies {
        Latencies::from(
            self.completed
                .iter()
                .filter(|c| c.req.tenant == tenant)
                .map(|c| c.ttft_us()),
        )
    }

    pub fn mean_slowdown(&self) -> f64 {
        if self.completed.is_empty() {
            return 0.0;
        }
        self.completed
            .iter()
            .map(|c| c.slowdown(&self.engine))
            .sum::<f64>()
            / self.completed.len() as f64
    }

    pub fn latency(&self) -> Latencies {
        Latencies::from(self.completed.iter().map(|c| c.latency_us()))
    }

    pub fn queue(&self) -> Latencies {
        Latencies::from(self.completed.iter().map(|c| c.queue_us()))
    }

    pub fn for_kind(&self, kind: Kind) -> Latencies {
        Latencies::from(
            self.completed
                .iter()
                .filter(|c| c.req.kind == kind)
                .map(|c| c.latency_us()),
        )
    }

    pub fn for_tenant(&self, tenant: u8) -> Latencies {
        Latencies::from(
            self.completed
                .iter()
                .filter(|c| c.req.tenant == tenant)
                .map(|c| c.latency_us()),
        )
    }

    /// Requests completed per second. What a capacity plan is written in,
    /// and on its own a misleading number -- see `goodput`.
    pub fn throughput_rps(&self) -> f64 {
        if self.sim_duration_s <= 0.0 {
            return 0.0;
        }
        self.completed.len() as f64 / self.sim_duration_s
    }

    /// Output tokens emitted per second across the fleet.
    pub fn token_throughput(&self) -> f64 {
        if self.sim_duration_s <= 0.0 {
            return 0.0;
        }
        self.completed
            .iter()
            .map(|c| c.req.output_tokens as f64)
            .sum::<f64>()
            / self.sim_duration_s
    }

    /// Requests per second that completed **within their SLO**.
    ///
    /// The distinction from throughput is the report's central practical
    /// point. A system serving 40 requests per second where none meets its
    /// objective has a throughput of 40 and a goodput of zero, and the
    /// capacity plan built on the first number will buy hardware that does
    /// not fix anything.
    pub fn goodput_rps(&self) -> f64 {
        if self.sim_duration_s <= 0.0 {
            return 0.0;
        }
        self.completed.iter().filter(|c| c.met_slo()).count() as f64 / self.sim_duration_s
    }

    /// Share of *offered* load that succeeded, counting rejections as
    /// failures. Rejections are not free, and a metric that ignores them
    /// rewards shedding everything.
    pub fn offered_success_rate(&self) -> f64 {
        if self.offered == 0 {
            return 0.0;
        }
        self.completed.iter().filter(|c| c.met_slo()).count() as f64 / self.offered as f64
    }

    pub fn rejection_rate(&self) -> f64 {
        if self.offered == 0 {
            return 0.0;
        }
        self.rejected.len() as f64 / self.offered as f64
    }

    /// Share of completed requests meeting their SLO.
    pub fn slo_attainment(&self) -> f64 {
        if self.completed.is_empty() {
            return 0.0;
        }
        self.completed.iter().filter(|c| c.met_slo()).count() as f64 / self.completed.len() as f64
    }

    pub fn attainment_for(&self, kind: Kind) -> f64 {
        let members: Vec<_> = self.completed.iter().filter(|c| c.req.kind == kind).collect();
        if members.is_empty() {
            return 0.0;
        }
        members.iter().filter(|c| c.met_slo()).count() as f64 / members.len() as f64
    }

    pub fn interactive_attainment(&self) -> f64 {
        let members: Vec<_> = self
            .completed
            .iter()
            .filter(|c| c.req.kind.interactive())
            .collect();
        if members.is_empty() {
            return 0.0;
        }
        members.iter().filter(|c| c.met_slo()).count() as f64 / members.len() as f64
    }
}

/// Format microseconds as milliseconds with one decimal, or as seconds when
/// large. Report tables are read by humans deciding whether a number is
/// alarming, and `1843211us` does not communicate that.
pub fn fmt_us(us: u64) -> String {
    if us >= 10_000_000 {
        format!("{:.1}s", us as f64 / 1_000_000.0)
    } else if us >= 1_000 {
        format!("{:.0}ms", us as f64 / 1_000.0)
    } else {
        format!("{us}us")
    }
}

/// M/M/1 mean sojourn time, used in the report to show how closely a
/// heavy-tailed simulated system tracks the textbook curve -- and where it
/// diverges, which is the interesting part.
pub fn mm1_sojourn_s(lambda: f64, mu: f64) -> Option<f64> {
    if lambda >= mu {
        return None;
    }
    Some(1.0 / (mu - lambda))
}
