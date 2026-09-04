//! The workload, and why its *shape* decides every conclusion.
//!
//! An inference gateway's behaviour is dominated by two facts about its
//! traffic that a uniform workload hides completely:
//!
//! 1. **Output lengths are heavy-tailed.** A chat turn emits 40 tokens; a
//!    summarisation emits 900. Under FIFO the long one blocks the short ones
//!    behind it, and mean latency stops describing anyone's experience.
//! 2. **Prompt and output lengths are not the same resource.** Prompt length
//!    drives prefill, which is compute-bound and happens once. Output length
//!    drives decode, which is memory-bandwidth-bound and happens per token,
//!    and it is output length that determines how long a KV-cache slot is
//!    held.
//!
//! Every experiment in `docs/results.md` uses the mixed workload below. A
//! homogeneous workload would make head-of-line blocking vanish and would
//! make continuous batching look like a rounding error.

use crate::rng::Rng;

/// What the caller is trying to do. Determines the length distribution and
/// the service class, and both matter.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum Kind {
    /// Short prompt, short output, latency-sensitive. A human is waiting.
    Chat,
    /// Long prompt, short output. Prefill-dominated: the cost is paid before
    /// the first token, so it shows up in TTFT and barely in TPOT.
    Rag,
    /// Short prompt, long output. Decode-dominated, and the source of
    /// head-of-line blocking.
    Summarise,
    /// Long prompt, long output, latency-tolerant. A batch job that should
    /// never be allowed to hurt the interactive classes.
    Batch,
}

impl Kind {
    pub fn name(&self) -> &'static str {
        match self {
            Kind::Chat => "chat",
            Kind::Rag => "rag",
            Kind::Summarise => "summarise",
            Kind::Batch => "batch",
        }
    }

    pub const ALL: [Kind; 4] = [Kind::Chat, Kind::Rag, Kind::Summarise, Kind::Batch];

    /// Whether a human is blocked on this request. Used by admission control
    /// and by the goodput definition: shedding a batch job is a scheduling
    /// decision, shedding a chat turn is an outage.
    pub fn interactive(&self) -> bool {
        matches!(self, Kind::Chat | Kind::Rag)
    }

    /// Time-to-first-token objective, microseconds.
    pub fn ttft_slo_us(&self) -> u64 {
        match self {
            Kind::Chat => 500_000,
            Kind::Rag => 1_000_000,
            Kind::Summarise => 2_000_000,
            Kind::Batch => 30_000_000,
        }
    }

    /// Time-per-output-token objective, microseconds. Below roughly 50ms a
    /// reader cannot tell the difference, which is why TPOT budgets are
    /// generous compared with TTFT.
    pub fn tpot_slo_us(&self) -> u64 {
        match self {
            Kind::Chat => 60_000,
            Kind::Rag => 60_000,
            Kind::Summarise => 100_000,
            Kind::Batch => 1_000_000,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Request {
    pub id: u64,
    pub tenant: u8,
    pub kind: Kind,
    pub arrival_us: u64,
    pub prompt_tokens: u32,
    /// How many tokens this request *will* emit. The gateway does not know
    /// this -- see `Estimate`. Keeping the true value on the request and
    /// forcing every policy to go through an estimator is what stops the
    /// simulation from quietly granting the scheduler an oracle.
    pub output_tokens: u32,
}

impl Request {
    /// Total KV-cache slots this request will occupy at its peak, in tokens.
    pub fn peak_kv_tokens(&self) -> u32 {
        self.prompt_tokens + self.output_tokens
    }
}

/// A tenant's traffic profile. Two tenants sharing a gateway with different
/// mixes is the situation fair-share scheduling exists for.
#[derive(Debug, Clone)]
pub struct Tenant {
    pub id: u8,
    pub name: &'static str,
    pub share: f64,
    pub mix: Vec<(Kind, f64)>,
}

/// The default two-tenant workload used by every experiment.
///
/// `interactive` sends mostly chat and RAG. `analytics` sends mostly
/// summarisation and batch. They are deliberately unbalanced: the whole point
/// of section 6 is that under FIFO the analytics tenant's long jobs degrade
/// the interactive tenant's latency even though the interactive tenant is
/// well within its share.
pub fn default_tenants() -> Vec<Tenant> {
    vec![
        Tenant {
            id: 0,
            name: "interactive",
            share: 0.5,
            mix: vec![(Kind::Chat, 0.70), (Kind::Rag, 0.25), (Kind::Summarise, 0.05)],
        },
        Tenant {
            id: 1,
            name: "analytics",
            share: 0.5,
            mix: vec![(Kind::Summarise, 0.55), (Kind::Batch, 0.35), (Kind::Chat, 0.10)],
        },
    ]
}

#[derive(Debug, Clone)]
pub struct Workload {
    pub tenants: Vec<Tenant>,
    pub arrival_rate_per_s: f64,
    pub seed: u64,
}

impl Workload {
    pub fn new(arrival_rate_per_s: f64, seed: u64) -> Self {
        Workload {
            tenants: default_tenants(),
            arrival_rate_per_s,
            seed,
        }
    }

    /// Generate `n` requests as a Poisson process.
    ///
    /// Arrivals are exponentially spaced rather than evenly spaced, and that
    /// is not a detail. A deterministic arrival process (D/M/1) has
    /// dramatically lower queueing delay than a Poisson one (M/M/1) at the
    /// same utilisation, because bursts are what build queues. Simulating
    /// evenly-spaced arrivals is the single easiest way to produce a
    /// serving benchmark that looks excellent and predicts nothing.
    pub fn generate(&self, n: usize) -> Vec<Request> {
        let mut rng = Rng::new(self.seed);
        let mut now = 0.0f64;
        let mut out = Vec::with_capacity(n);
        let total_share: f64 = self.tenants.iter().map(|t| t.share).sum();

        for id in 0..n as u64 {
            now += rng.exponential(self.arrival_rate_per_s);

            let mut pick = rng.unit() * total_share;
            let tenant = self
                .tenants
                .iter()
                .find(|t| {
                    pick -= t.share;
                    pick <= 0.0
                })
                .unwrap_or_else(|| self.tenants.last().unwrap());

            let mut pick = rng.unit();
            let kind = tenant
                .mix
                .iter()
                .find(|(_, w)| {
                    pick -= w;
                    pick <= 0.0
                })
                .map(|(k, _)| *k)
                .unwrap_or(Kind::Chat);

            let (prompt, output) = match kind {
                Kind::Chat => (rng.lognormal(180.0, 4.0), rng.lognormal(60.0, 4.0)),
                Kind::Rag => (rng.lognormal(1600.0, 3.0), rng.lognormal(90.0, 3.0)),
                Kind::Summarise => (rng.lognormal(700.0, 3.0), rng.lognormal(500.0, 3.0)),
                Kind::Batch => (rng.lognormal(1200.0, 4.0), rng.lognormal(800.0, 4.0)),
            };

            out.push(Request {
                id,
                tenant: tenant.id,
                kind,
                arrival_us: (now * 1_000_000.0) as u64,
                prompt_tokens: (prompt.round() as u32).clamp(8, 32_000),
                output_tokens: (output.round() as u32).clamp(1, 4_000),
            });
        }
        out
    }
}

/// What the scheduler is allowed to know before a request runs.
///
/// Real gateways cannot see `output_tokens`. Every policy in `sched.rs` takes
/// an `Estimate`, never a `Request`, so a shortest-job-first policy has to
/// work from a *prediction* and eat the prediction error. Section 4 measures
/// what that error costs, and the answer is the difference between SJF being
/// a good idea and being a footgun.
#[derive(Debug, Clone, Copy)]
pub struct Estimate {
    pub prompt_tokens: u32,
    pub predicted_output_tokens: u32,
    pub kind: Kind,
}

/// Predicts output length from the request kind alone, with a stated error.
///
/// This is roughly what a production system can do: it knows the endpoint,
/// the model, and the prompt, and it has historical percentiles per class. It
/// does not know how long *this* generation will run, because that depends on
/// when the model decides to emit a stop token.
#[derive(Debug, Clone)]
pub struct Estimator {
    pub error_spread: f64,
    rng: Rng,
}

impl Estimator {
    /// `error_spread` of 1.0 is a perfect oracle; 3.0 means the estimate is
    /// typically within a factor of three, which is optimistic for real
    /// traffic and is stated as such in the report.
    pub fn new(error_spread: f64, seed: u64) -> Self {
        Estimator {
            error_spread,
            rng: Rng::new(seed),
        }
    }

    pub fn oracle() -> Self {
        Estimator::new(1.0, 0)
    }

    pub fn estimate(&mut self, req: &Request) -> Estimate {
        let predicted = if self.error_spread <= 1.0 {
            req.output_tokens as f64
        } else {
            self.rng
                .lognormal(req.output_tokens as f64, self.error_spread)
        };
        Estimate {
            prompt_tokens: req.prompt_tokens,
            predicted_output_tokens: (predicted.round() as u32).clamp(1, 8_000),
            kind: req.kind,
        }
    }
}
