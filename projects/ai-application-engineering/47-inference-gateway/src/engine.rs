//! The model server, modelled at the level that determines queueing
//! behaviour and no finer.
//!
//! # What is modelled, and why
//!
//! Three facts about transformer inference drive everything a gateway does.
//! The model below captures exactly these and nothing else.
//!
//! **1. Decode is memory-bandwidth bound, so batching is nearly free.**
//! Generating one token for one sequence requires streaming the entire
//! weight matrix from HBM. Generating one token for *sixty* sequences
//! requires streaming it once. Step time is therefore
//! `weight_load + per_seq * batch`, with `per_seq` small — the shape that
//! makes throughput rise almost linearly with batch size while per-request
//! latency barely moves. If you take one thing from this module, it is that
//! the arithmetic intensity of decode is what makes continuous batching
//! worth building.
//!
//! **2. Prefill is compute bound and competes with decode for the device.**
//! Processing a 4000-token prompt is a large matmul. While it runs, no
//! decode step runs, so every *already-admitted* request stalls. This is the
//! prefill/decode conflict and it is why TTFT and TPOT trade against each
//! other rather than improving together.
//!
//! **3. KV cache is the binding constraint, not compute.** Each active
//! sequence holds `(prompt + generated) * bytes_per_token` of HBM for its
//! entire lifetime. The server runs out of memory long before it runs out of
//! FLOPs, and admission is therefore a *memory* decision. A gateway that
//! admits on request count rather than on projected KV footprint will
//! either under-fill the device or start evicting under load.
//!
//! # What is deliberately not modelled
//!
//! Tensor/pipeline parallelism, paged-attention fragmentation, speculative
//! decoding, quantisation, and the difference between chunked and
//! non-chunked prefill. Each would change the constants. None would change
//! the queueing conclusions, which are the subject of the report -- and
//! `docs/known-limitations.md` says so explicitly rather than leaving the
//! reader to guess.

use crate::workload::Request;

/// Hardware and model constants. Defaults are in the region of a 7B model on
/// a single 80GB accelerator; the report sweeps the ones that matter rather
/// than defending any particular value.
#[derive(Debug, Clone, Copy)]
pub struct EngineConfig {
    /// Fixed cost of a decode step: streaming weights from HBM. Paid once
    /// per step regardless of batch size, which is the entire reason
    /// batching works.
    pub decode_weight_load_us: u64,
    /// Marginal cost per sequence in a decode step.
    pub decode_per_seq_us: u64,
    /// Prefill throughput, tokens per second. A 7B model needs roughly
    /// 14 GFLOP per prompt token; an 80GB-class accelerator at realistic
    /// utilisation lands near this figure.
    pub prefill_tokens_per_s: f64,
    /// KV cache capacity in tokens across all sequences. At roughly 0.5MB
    /// per token for a 7B model, this is the usable share of an 80GB card.
    pub kv_capacity_tokens: u32,
    /// Hard cap on concurrently decoding sequences, independent of memory.
    pub max_batch: usize,
    /// Number of independent replicas behind the gateway.
    pub replicas: usize,
}

impl Default for EngineConfig {
    fn default() -> Self {
        EngineConfig {
            decode_weight_load_us: 12_000,
            decode_per_seq_us: 200,
            prefill_tokens_per_s: 14_000.0,
            kv_capacity_tokens: 160_000,
            max_batch: 64,
            replicas: 1,
        }
    }
}

impl EngineConfig {
    /// Duration of one decode step at the given batch size.
    pub fn decode_step_us(&self, batch: usize) -> u64 {
        if batch == 0 {
            return 0;
        }
        self.decode_weight_load_us + self.decode_per_seq_us * batch as u64
    }

    /// Duration of prefill for a prompt of `tokens` tokens.
    pub fn prefill_us(&self, tokens: u32) -> u64 {
        ((tokens as f64 / self.prefill_tokens_per_s) * 1_000_000.0) as u64
    }

    /// Tokens emitted per second across the whole batch, at a given batch
    /// size. Rises with batch and asymptotes at
    /// `1e6 / decode_per_seq_us` -- the point where the weight load is fully
    /// amortised and the device is saturated.
    pub fn decode_throughput_tok_per_s(&self, batch: usize) -> f64 {
        if batch == 0 {
            return 0.0;
        }
        batch as f64 * 1_000_000.0 / self.decode_step_us(batch) as f64
    }

    /// The batch size at which marginal throughput gain falls below
    /// `epsilon` tokens/s per additional sequence. Beyond this point extra
    /// batching buys throughput you cannot measure and costs latency you
    /// can.
    pub fn knee_batch(&self, epsilon: f64) -> usize {
        for b in 1..self.max_batch {
            let gain = self.decode_throughput_tok_per_s(b + 1)
                - self.decode_throughput_tok_per_s(b);
            if gain < epsilon {
                return b;
            }
        }
        self.max_batch
    }
}

/// A request that has been admitted and is executing.
#[derive(Debug, Clone)]
pub struct Running {
    pub req: Request,
    pub admitted_us: u64,
    pub first_token_us: Option<u64>,
    pub emitted: u32,
    pub prefill_remaining_us: u64,
    /// KV tokens actually occupied: prompt plus whatever has been generated.
    pub kv_held: u32,
    /// KV tokens reserved at admission against this sequence's *projected*
    /// peak footprint.
    ///
    /// Held separately from `kv_held` because the two diverge, and the
    /// divergence is the phenomenon section 8 is about. A sequence's charge
    /// against capacity is `max(kv_held, reserved_kv)`: while it is still
    /// inside its reservation it costs what was set aside for it, and once
    /// it outgrows the reservation it costs what it actually uses. Charging
    /// the sum of both would double-count and make the server look full at
    /// half occupancy; charging only `kv_held` would make reservation a
    /// no-op and delete the entire point of admitting on a projection.
    pub reserved_kv: u32,
    /// How many times this request has been evicted and restarted. Every
    /// eviction throws away completed work, which is why the report treats
    /// eviction as a cost rather than as a free safety valve.
    pub restarts: u32,
}

impl Running {
    pub fn new(req: Request, now_us: u64, reserved_kv: u32, cfg: &EngineConfig) -> Self {
        Running {
            prefill_remaining_us: cfg.prefill_us(req.prompt_tokens),
            kv_held: req.prompt_tokens,
            reserved_kv: reserved_kv.max(req.prompt_tokens),
            admitted_us: now_us,
            first_token_us: None,
            emitted: 0,
            restarts: 0,
            req,
        }
    }

    pub fn prefilling(&self) -> bool {
        self.prefill_remaining_us > 0
    }

    pub fn done(&self) -> bool {
        self.emitted >= self.req.output_tokens
    }

    /// What this sequence currently charges against KV capacity.
    pub fn charge(&self) -> u32 {
        self.kv_held.max(self.reserved_kv)
    }

    /// Whether the next generated token consumes *fresh* capacity rather
    /// than eating into an existing reservation.
    pub fn grows_next_token(&self) -> bool {
        self.kv_held >= self.reserved_kv
    }

    /// Record one generated token, returning the fresh capacity consumed
    /// (0 while still inside the reservation, 1 once past it).
    ///
    /// The two assertions encode invariants the event loop already respects
    /// but which nothing previously enforced: a sequence cannot emit while
    /// its prompt is still being prefilled, and cannot emit after it has
    /// produced everything it was asked for. Both were reachable once this
    /// method became public, and the first one silently spins `kv_held` to
    /// integer overflow rather than failing anywhere near the mistake.
    pub fn emit_token(&mut self) -> u32 {
        debug_assert!(
            !self.prefilling(),
            "emit_token called on a sequence still in prefill"
        );
        debug_assert!(!self.done(), "emit_token called on a finished sequence");
        let fresh = if self.grows_next_token() { 1 } else { 0 };
        self.kv_held += 1;
        self.emitted += 1;
        fresh
    }
}

/// Outcome of a completed request, in the units an SLO is written in.
#[derive(Debug, Clone, Copy)]
pub struct Completed {
    pub req: Request,
    pub admitted_us: u64,
    pub first_token_us: u64,
    pub finished_us: u64,
    pub restarts: u32,
}

impl Completed {
    /// Queue delay: how long the request waited before the engine touched
    /// it. Distinct from TTFT, which also includes prefill.
    pub fn queue_us(&self) -> u64 {
        self.admitted_us.saturating_sub(self.req.arrival_us)
    }

    /// Time to first token, measured from arrival -- not from admission.
    /// Measuring from admission is the most common way a serving dashboard
    /// hides queueing: it reports the part of the latency the engine is
    /// responsible for and omits the part the *gateway* is responsible for,
    /// which is the part that explodes under load.
    pub fn ttft_us(&self) -> u64 {
        self.first_token_us.saturating_sub(self.req.arrival_us)
    }

    /// Mean time per output token after the first.
    pub fn tpot_us(&self) -> u64 {
        let tokens = self.req.output_tokens.saturating_sub(1).max(1) as u64;
        self.finished_us.saturating_sub(self.first_token_us) / tokens
    }

    pub fn latency_us(&self) -> u64 {
        self.finished_us.saturating_sub(self.req.arrival_us)
    }

    /// How long this request would have taken with the device entirely to
    /// itself: prefill plus decode at batch size one.
    ///
    /// Note this is *not* the fastest possible completion. Batching changes
    /// per-token cost only slightly (decode is bandwidth bound), so a
    /// request in a batch of sixty is barely slower per token than one
    /// alone. That is precisely why the ratio below is informative.
    pub fn ideal_us(&self, cfg: &EngineConfig) -> u64 {
        cfg.prefill_us(self.req.prompt_tokens)
            + cfg.decode_step_us(1) * self.req.output_tokens as u64
    }

    /// Latency divided by ideal service time.
    ///
    /// Raw latency is close to useless on a heterogeneous workload: a
    /// 4000-token generation that takes 90 seconds is behaving perfectly
    /// and a 40-token chat turn that takes 9 seconds is a disaster, yet the
    /// first looks ten times worse in a latency histogram. Slowdown --
    /// standard in the scheduling literature, sometimes called stretch --
    /// puts both on the same axis, and it is the metric that makes
    /// head-of-line blocking visible instead of averaging it away.
    pub fn slowdown(&self, cfg: &EngineConfig) -> f64 {
        let ideal = self.ideal_us(cfg).max(1);
        self.latency_us() as f64 / ideal as f64
    }

    /// Whether this request met both objectives. A request that streams its
    /// first token promptly and then stalls has met neither the user's
    /// expectation nor this predicate.
    pub fn met_slo(&self) -> bool {
        self.ttft_us() <= self.req.kind.ttft_slo_us()
            && self.tpot_us() <= self.req.kind.tpot_slo_us()
    }
}

/// A sequence that was preempted, along with what it had achieved.
///
/// The `emitted` count matters on requeue: the gateway now *knows* this
/// request produces at least that many tokens, and re-admitting it on the
/// original under-estimate is how a preemption loop becomes a livelock.
#[derive(Debug, Clone, Copy)]
pub struct Evicted {
    pub req: Request,
    pub emitted: u32,
    pub kv_held: u32,
}

/// One replica's execution state.
#[derive(Debug, Clone)]
pub struct Replica {
    pub cfg: EngineConfig,
    pub running: Vec<Running>,
    pub kv_used: u32,
}

impl Replica {
    pub fn new(cfg: EngineConfig) -> Self {
        Replica {
            cfg,
            running: Vec::new(),
            kv_used: 0,
        }
    }

    pub fn batch(&self) -> usize {
        self.running.len()
    }

    pub fn kv_free(&self) -> u32 {
        self.cfg.kv_capacity_tokens.saturating_sub(self.kv_used)
    }

    /// Whether this replica can accept a request whose projected peak KV
    /// footprint is `projected_kv` tokens.
    ///
    /// Two things are reserved, and leaving out the second causes a
    /// livelock. The first is the newcomer's own *projection*, not its
    /// prompt: admitting on prompt size alone is how a server ends up
    /// evicting, because the sequence fits when it starts and does not fit
    /// by the time it is 600 tokens into its output. The second is one
    /// token for every sequence already decoding, so that the next step is
    /// always affordable for the work already accepted.
    ///
    /// Without that headroom term the gateway will happily admit into
    /// memory that its existing batch is about to consume. It then evicts
    /// to make room, which frees a large block, which makes the newcomer
    /// admissible again -- and the cycle repeats without anything
    /// completing. This simulator ran 19,997,889 evictions and produced
    /// zero completions before the headroom term existed.
    pub fn can_admit(&self, projected_kv: u32) -> bool {
        self.batch() < self.cfg.max_batch.max(1)
            && self.kv_free() >= projected_kv.saturating_add(self.needs_fresh_kv())
    }

    pub fn admit(&mut self, req: Request, now_us: u64, reserved_kv: u32) {
        let r = Running::new(req, now_us, reserved_kv, &self.cfg);
        self.kv_used += r.charge();
        self.running.push(r);
    }

    /// How many sequences will consume *fresh* KV on the next decode step.
    ///
    /// Sequences still inside their reservation consume nothing new, so a
    /// fully reserved replica can be at `kv_free() == 0` and still run
    /// happily forever. Confusing "no free capacity" with "cannot proceed"
    /// would make the gateway evict sequences that were never in trouble.
    pub fn needs_fresh_kv(&self) -> u32 {
        self.running
            .iter()
            .filter(|r| !r.prefilling() && !r.done() && r.grows_next_token())
            .count() as u32
    }

    /// True when the next decode step would need more capacity than exists.
    /// This is the only honest trigger for preemption.
    pub fn over_subscribed(&self) -> bool {
        self.needs_fresh_kv() > self.kv_free()
    }

    /// Advance this replica by one scheduling step and return anything that
    /// finished.
    ///
    /// Prefill is done to completion for one request before decode resumes,
    /// which models a non-chunked scheduler. That choice is the source of
    /// section 5's result and is called out there.
    pub fn step(&mut self, now_us: u64, prefill_priority: bool) -> (u64, Vec<Completed>) {
        if self.running.is_empty() {
            return (0, Vec::new());
        }

        let prefill_idx = if prefill_priority {
            self.running.iter().position(|r| r.prefilling())
        } else {
            // Decode-priority: only prefill when nothing can decode.
            if self.running.iter().any(|r| !r.prefilling() && !r.done()) {
                None
            } else {
                self.running.iter().position(|r| r.prefilling())
            }
        };

        if let Some(idx) = prefill_idx {
            let elapsed = self.running[idx].prefill_remaining_us;
            self.running[idx].prefill_remaining_us = 0;
            self.running[idx].first_token_us = Some(now_us + elapsed);
            self.kv_used += self.running[idx].emit_token();
            let finished = self.harvest(now_us + elapsed);
            return (elapsed, finished);
        }

        let decoding: Vec<usize> = self
            .running
            .iter()
            .enumerate()
            .filter(|(_, r)| !r.prefilling() && !r.done())
            .map(|(i, _)| i)
            .collect();

        if decoding.is_empty() {
            let finished = self.harvest(now_us);
            return (0, finished);
        }

        let elapsed = self.cfg.decode_step_us(decoding.len());
        for i in decoding {
            self.kv_used += self.running[i].emit_token();
        }
        let finished = self.harvest(now_us + elapsed);
        (elapsed, finished)
    }

    fn harvest(&mut self, now_us: u64) -> Vec<Completed> {
        let mut out = Vec::new();
        let mut i = 0;
        while i < self.running.len() {
            if self.running[i].done() {
                let r = self.running.remove(i);
                self.kv_used = self.kv_used.saturating_sub(r.charge());
                out.push(Completed {
                    req: r.req,
                    admitted_us: r.admitted_us,
                    first_token_us: r.first_token_us.unwrap_or(now_us),
                    finished_us: now_us,
                    restarts: r.restarts,
                });
            } else {
                i += 1;
            }
        }
        out
    }

    /// Evict the sequence that has generated the least, returning it to the
    /// queue. Least-progress-first minimises discarded work, which is the
    /// only defensible victim policy when eviction throws away everything.
    pub fn evict_one(&mut self) -> Option<Evicted> {
        let victim = self
            .running
            .iter()
            .enumerate()
            .filter(|(_, r)| !r.done())
            .min_by_key(|(_, r)| r.emitted)
            .map(|(i, _)| i)?;
        let r = self.running.remove(victim);
        self.kv_used = self.kv_used.saturating_sub(r.charge());
        Some(Evicted {
            req: r.req,
            emitted: r.emitted,
            kv_held: r.kv_held,
        })
    }
}
