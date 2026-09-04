//! Scheduling and admission control.
//!
//! These are two different decisions and conflating them is the most common
//! design error in this space:
//!
//! * **Scheduling** decides the *order* of work already accepted. It moves
//!   latency between requests. It cannot create capacity.
//! * **Admission control** decides whether to accept work at all. It is the
//!   only mechanism here that can keep a system inside its SLO when demand
//!   exceeds capacity, because it is the only one that reduces load.
//!
//! A team that responds to SLO violations by tuning the scheduler is
//! rearranging who suffers. Section 7 quantifies the difference.

use crate::engine::EngineConfig;
use crate::workload::{Estimate, Kind, Request};
use std::collections::VecDeque;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Policy {
    /// First come first served. The default in every system that has not
    /// thought about it, and the one that maximises head-of-line blocking
    /// on a heavy-tailed workload.
    Fifo,
    /// Shortest estimated job first. Provably minimises mean latency when
    /// the estimates are correct, which is the caveat section 4 is about.
    Sjf,
    /// Strict priority by service class, FIFO within a class.
    ClassPriority,
    /// Deficit round robin across tenants: each tenant gets its configured
    /// share of *tokens*, not of requests. Charging by request lets a tenant
    /// take unbounded capacity by sending longer jobs.
    DeficitRoundRobin,
    /// SJF with aging, so a job that has waited long enough is promoted
    /// regardless of size. The standard answer to SJF's starvation problem.
    SjfAged,
}

impl Policy {
    pub fn name(&self) -> &'static str {
        match self {
            Policy::Fifo => "fifo",
            Policy::Sjf => "sjf",
            Policy::ClassPriority => "class-priority",
            Policy::DeficitRoundRobin => "drr",
            Policy::SjfAged => "sjf-aged",
        }
    }

    pub const ALL: [Policy; 5] = [
        Policy::Fifo,
        Policy::Sjf,
        Policy::ClassPriority,
        Policy::DeficitRoundRobin,
        Policy::SjfAged,
    ];
}

#[derive(Debug, Clone)]
pub struct Queued {
    pub req: Request,
    pub est: Estimate,
    pub enqueued_us: u64,
    /// The memory reservation's view of job size. Usually identical to
    /// `est`; separated so section 4 can move one channel at a time.
    pub kv_est: Estimate,
    /// Earliest time this item may be admitted.
    ///
    /// Only ever set by `requeue`. Without it a preempted request is
    /// re-admitted on the very next tick, immediately re-triggers the memory
    /// pressure that evicted it, and the gateway livelocks -- evicting and
    /// re-admitting forever while completing nothing. That is not a
    /// hypothetical: it is what this simulator did until the backoff was
    /// added, and the only reason the run terminated at all was the event
    /// ceiling in `GatewayConfig::max_events`.
    pub not_before_us: u64,
}

impl Queued {
    pub fn new(req: Request, est: Estimate, enqueued_us: u64) -> Self {
        Queued {
            req,
            est,
            kv_est: est,
            enqueued_us,
            not_before_us: enqueued_us,
        }
    }

    pub fn ready(&self, now_us: u64) -> bool {
        self.not_before_us <= now_us
    }

    /// Estimated total service cost in "token units": prefill tokens plus
    /// projected decode tokens. Used by SJF, and the fact that it is an
    /// estimate rather than a measurement is the entire subject of
    /// section 4.
    pub fn estimated_cost(&self) -> u64 {
        self.est.prompt_tokens as u64 / 8 + self.est.predicted_output_tokens as u64
    }

    /// Projected peak KV footprint, from the *reservation* estimate.
    pub fn projected_kv(&self) -> u32 {
        self.kv_est.prompt_tokens + self.kv_est.predicted_output_tokens
    }
}

/// The pending-work queue plus its ordering policy.
#[derive(Debug, Clone)]
pub struct Scheduler {
    pub policy: Policy,
    queue: VecDeque<Queued>,
    /// Per-tenant deficit counters for DRR, in token units.
    deficits: Vec<i64>,
    quantum: i64,
    /// Requests wait at most this long before aging promotes them.
    pub aging_threshold_us: u64,
}

impl Scheduler {
    pub fn new(policy: Policy, tenants: usize) -> Self {
        Scheduler {
            policy,
            queue: VecDeque::new(),
            deficits: vec![0; tenants.max(1)],
            quantum: 2_000,
            aging_threshold_us: 3_000_000,
        }
    }

    pub fn len(&self) -> usize {
        self.queue.len()
    }

    pub fn is_empty(&self) -> bool {
        self.queue.is_empty()
    }

    pub fn push(&mut self, item: Queued) {
        self.queue.push_back(item);
    }

    pub fn peek(&self) -> Option<&Queued> {
        self.queue.front()
    }

    /// Remove the oldest item regardless of policy. Used only to break a
    /// deadlock, never as a scheduling decision.
    pub fn pop_any(&mut self) -> Option<Queued> {
        self.queue.pop_front()
    }

    /// Backlog measured in token-units of projected work rather than in
    /// requests -- the unit `Admission::QueueTokens` bounds.
    pub fn backlog_tokens(&self) -> u64 {
        self.queue.iter().map(|q| q.estimated_cost()).sum()
    }

    /// Earliest time any queued item becomes admissible. With backoff in
    /// play this is a genuine simulation event: skipping it would let the
    /// clock jump past the moment a preempted request became eligible, or
    /// leave the loop spinning while every item was still held back.
    pub fn next_ready_us(&self) -> Option<u64> {
        self.queue.iter().map(|q| q.not_before_us).min()
    }

    /// Put an evicted request back at the front of its class. It has already
    /// waited once and already had work discarded; sending it to the back is
    /// how eviction turns into starvation.
    pub fn requeue(&mut self, item: Queued) {
        self.queue.push_front(item);
    }

    pub fn iter(&self) -> impl Iterator<Item = &Queued> {
        self.queue.iter()
    }

    /// Choose the next request to admit, if the predicate accepts it.
    ///
    /// The predicate is how memory pressure enters scheduling: the caller
    /// passes "does this fit in KV right now", and the scheduler will skip
    /// over a large job to find a small one that fits rather than blocking.
    /// That skipping is itself a policy decision -- it improves utilisation
    /// and it is how a large request starves. `SjfAged` exists to bound it.
    pub fn pop_admissible(
        &mut self,
        now_us: u64,
        fits: impl Fn(&Queued) -> bool,
    ) -> Option<Queued> {
        // Backoff is applied here rather than inside each policy so that no
        // policy can forget it.
        let fits = |q: &Queued| q.ready(now_us) && fits(q);
        let idx = match self.policy {
            Policy::Fifo => self.queue.iter().position(&fits),
            Policy::Sjf => self.pick_min(|q| q.estimated_cost(), &fits),
            Policy::ClassPriority => self.pick_min(
                |q| {
                    let rank = match q.req.kind {
                        Kind::Chat => 0u64,
                        Kind::Rag => 1,
                        Kind::Summarise => 2,
                        Kind::Batch => 3,
                    };
                    rank * 1_000_000_000 + q.req.id
                },
                &fits,
            ),
            Policy::SjfAged => {
                let aged = self.queue.iter().position(|q| {
                    now_us.saturating_sub(q.enqueued_us) > self.aging_threshold_us && fits(q)
                });
                aged.or_else(|| self.pick_min(|q| q.estimated_cost(), &fits))
            }
            Policy::DeficitRoundRobin => self.pick_drr(&fits),
        }?;

        let item = self.queue.remove(idx)?;
        if self.policy == Policy::DeficitRoundRobin {
            let t = item.req.tenant as usize;
            if t < self.deficits.len() {
                self.deficits[t] -= item.estimated_cost() as i64;
            }
        }
        Some(item)
    }

    fn pick_min<K: Ord>(
        &self,
        key: impl Fn(&Queued) -> K,
        fits: &impl Fn(&Queued) -> bool,
    ) -> Option<usize> {
        self.queue
            .iter()
            .enumerate()
            .filter(|(_, q)| fits(q))
            .min_by_key(|(_, q)| key(q))
            .map(|(i, _)| i)
    }

    /// Deficit round robin over tenants, charging by estimated token cost.
    ///
    /// Charging by *request* instead would let a tenant claim unbounded
    /// capacity simply by sending longer requests, which is not a
    /// hypothetical -- it is exactly what the analytics tenant in
    /// `workload.rs` does.
    fn pick_drr(&mut self, fits: &impl Fn(&Queued) -> bool) -> Option<usize> {
        for _round in 0..self.deficits.len() * 2 + 2 {
            let best = self
                .queue
                .iter()
                .enumerate()
                .filter(|(_, q)| fits(q))
                .filter(|(_, q)| {
                    let t = q.req.tenant as usize;
                    t < self.deficits.len() && self.deficits[t] >= q.estimated_cost() as i64
                })
                .min_by_key(|(_, q)| (std::cmp::Reverse(self.deficits[q.req.tenant as usize]), q.req.id))
                .map(|(i, _)| i);
            if best.is_some() {
                return best;
            }
            if !self.queue.iter().any(fits) {
                return None;
            }
            for d in self.deficits.iter_mut() {
                *d += self.quantum;
            }
        }
        self.queue.iter().position(fits)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Admission {
    /// Accept everything. Under overload this converts a capacity problem
    /// into a latency problem affecting every request equally, which is the
    /// worst available outcome and the most common default.
    AcceptAll,
    /// Bound the queue by depth. Simple, and wrong in a specific way: a
    /// queue of ten summarisations and a queue of ten chat turns represent
    /// wildly different amounts of work.
    QueueDepth(usize),
    /// Bound the queue by projected *work* -- token-units of backlog. The
    /// correct unit, and the report shows it is worth measuring.
    QueueTokens(u64),
    /// Reject a request when its predicted wait already exceeds its TTFT
    /// objective. Shedding a request that was going to miss anyway costs
    /// nothing and returns its capacity to requests that can still succeed.
    SloPredictive,
    /// Shed by *value density* once the backlog exceeds a token threshold:
    /// drop the expensive, latency-tolerant work first and keep the cheap,
    /// latency-sensitive work.
    ///
    /// This exists because the report's section 7 found that
    /// `SloPredictive` sheds the wrong requests. It rejects whatever is
    /// arriving when the queue happens to be long, and during a burst that
    /// is disproportionately interactive traffic -- the traffic with the
    /// tightest objective and the smallest cost, which is exactly the
    /// traffic worth keeping. Shedding one batch job returns as much
    /// capacity as shedding twenty chat turns.
    CostAware(u64),
}

impl Admission {
    pub fn name(&self) -> &'static str {
        match self {
            Admission::AcceptAll => "accept-all",
            Admission::QueueDepth(_) => "queue-depth",
            Admission::QueueTokens(_) => "queue-tokens",
            Admission::SloPredictive => "slo-predictive",
            Admission::CostAware(_) => "cost-aware",
        }
    }
}

/// Decide whether to admit `candidate` given the current backlog.
///
/// `service_rate_tok_per_s` is the gateway's running estimate of how fast the
/// fleet drains token-units. Estimating it from observed completions rather
/// than from a configured constant is what lets admission control keep
/// working when a replica degrades.
pub fn admit(
    policy: Admission,
    candidate: &Queued,
    backlog: &Scheduler,
    service_rate_tok_per_s: f64,
    cfg: &EngineConfig,
) -> bool {
    match policy {
        Admission::AcceptAll => true,
        Admission::QueueDepth(max) => backlog.len() < max,
        Admission::QueueTokens(max) => {
            backlog.iter().map(|q| q.estimated_cost()).sum::<u64>() < max
        }
        Admission::SloPredictive => {
            if service_rate_tok_per_s <= 0.0 {
                return true;
            }
            // Requests ahead of this one under the current policy. For a
            // size-ordered policy that is only the smaller ones; for FIFO it
            // is all of them. Using the policy's own ordering here is what
            // keeps the prediction honest.
            let ahead: u64 = match backlog.policy {
                Policy::Sjf | Policy::SjfAged => backlog
                    .iter()
                    .filter(|q| q.estimated_cost() <= candidate.estimated_cost())
                    .map(|q| q.estimated_cost())
                    .sum(),
                _ => backlog.iter().map(|q| q.estimated_cost()).sum(),
            };
            let predicted_wait_us = (ahead as f64 / service_rate_tok_per_s * 1_000_000.0) as u64;
            let prefill_us = cfg.prefill_us(candidate.est.prompt_tokens);
            predicted_wait_us + prefill_us <= candidate.req.kind.ttft_slo_us()
        }
        Admission::CostAware(threshold) => {
            let backlog = backlog.backlog_tokens();
            if backlog < threshold {
                return true;
            }
            // Past the threshold, keep only work that is both cheap and
            // urgent. `pressure` rises as the backlog grows, so the bar
            // rises with the severity of the overload instead of being a
            // cliff -- a cliff turns a small burst into a mass rejection.
            let pressure = backlog as f64 / threshold.max(1) as f64;
            let cost = candidate.estimated_cost().max(1) as f64;
            let urgency = 1_000_000.0 / candidate.req.kind.ttft_slo_us() as f64;
            // Value density: urgency bought per token of work. On the
            // default workload chat scores ~0.024, rag ~0.0035, summarise
            // ~0.0009 and batch ~0.00004 -- nearly three orders of
            // magnitude, which is why shedding by class is so much more
            // effective than shedding by arrival time.
            let density = urgency / cost;
            // The bar rises linearly with overload rather than as a step,
            // so classes drop out one at a time as pressure builds. A step
            // turns a momentary burst into a mass rejection.
            density >= 0.0005 * pressure
        }
    }
}
