//! The discrete-event loop that ties the pieces together.
//!
//! # Why the clock is per-replica
//!
//! Time advances to the next event, not in fixed ticks: a fixed tick either
//! wastes work or quantises latency, and quantised latency in a report about
//! tail latency is not acceptable.
//!
//! Each replica carries its own clock, because replicas do not step in
//! lockstep -- a replica running a batch of 40 takes 22ms per step while one
//! running a batch of 3 takes 13ms. An earlier draft of this loop stepped
//! every replica and advanced a single global clock by the *minimum* elapsed
//! time, which silently credited the slower replicas with work they had not
//! done and made a two-replica fleet look faster than two independent
//! replicas. The global clock now only ever jumps to the earliest
//! outstanding event: an arrival, or a replica finishing its current step.

use crate::engine::{Completed, EngineConfig, Replica};
use crate::metrics::{Little, RunStats};
use crate::sched::{admit, Admission, Policy, Queued, Scheduler};
use crate::workload::{Estimator, Request, Workload};
use std::collections::HashMap;

#[derive(Debug, Clone)]
pub struct GatewayConfig {
    pub engine: EngineConfig,
    pub policy: Policy,
    pub admission: Admission,
    pub prefill_priority: bool,
    /// Multiplier applied to the estimated peak footprint when reserving KV.
    /// Above 1.0 the gateway reserves headroom and admits fewer sequences;
    /// below 1.0 it over-admits and evicts. Section 8 sweeps it.
    pub kv_reservation_factor: f64,
    /// How many times a request may be preempted before the gateway gives
    /// up and rejects it.
    ///
    /// Preemption cannot fix a systematically insufficient reservation. If
    /// every sequence reserves 15% less memory than it will need, then
    /// evicting one and re-admitting it just moves the shortfall around:
    /// the re-admitted sequence over-grows again, and the gateway evicts
    /// and re-admits forever while completing nothing. A retry cap converts
    /// that unbounded livelock into a bounded, visible failure -- which is
    /// the report's whole thesis about admission control, arriving here as
    /// a consequence rather than as a slogan.
    pub max_restarts: u32,
    /// Log-normal spread of the output-length estimator used for
    /// **scheduling**. 1.0 is an oracle.
    pub estimator_spread: f64,
    /// Log-normal spread of the estimator used for **KV reservation**.
    ///
    /// Deliberately separate from `estimator_spread`. Prediction error
    /// reaches the system through two independent channels -- it degrades
    /// the scheduler's ordering, and it degrades how much memory the
    /// gateway sets aside -- and an experiment that moves both at once
    /// cannot say which one it measured. Section 4 varies them one at a
    /// time, which is how it discovered that the second channel dominates.
    pub reservation_spread: f64,
    pub estimator_seed: u64,
    /// Safety valve: give up rather than loop forever if the model ever
    /// produces a step that does not advance the clock.
    pub max_events: u64,
}

impl Default for GatewayConfig {
    fn default() -> Self {
        GatewayConfig {
            engine: EngineConfig::default(),
            policy: Policy::Fifo,
            admission: Admission::AcceptAll,
            prefill_priority: true,
            kv_reservation_factor: 1.0,
            max_restarts: 4,
            estimator_spread: 1.0,
            reservation_spread: 1.0,
            estimator_seed: 42,
            max_events: 20_000_000,
        }
    }
}

impl GatewayConfig {
    pub fn with_policy(mut self, policy: Policy) -> Self {
        self.policy = policy;
        self
    }
    pub fn with_admission(mut self, admission: Admission) -> Self {
        self.admission = admission;
        self
    }
    pub fn with_replicas(mut self, replicas: usize) -> Self {
        self.engine.replicas = replicas;
        self
    }
    /// Vary only the scheduler's view of job size.
    pub fn with_estimator_spread(mut self, spread: f64) -> Self {
        self.estimator_spread = spread;
        self
    }
    /// Vary only the memory reservation's view of job size.
    pub fn with_reservation_spread(mut self, spread: f64) -> Self {
        self.reservation_spread = spread;
        self
    }
    /// Vary both, which is what a real deployment with one predictor does.
    pub fn with_both_spreads(mut self, spread: f64) -> Self {
        self.estimator_spread = spread;
        self.reservation_spread = spread;
        self
    }
    pub fn with_reservation(mut self, factor: f64) -> Self {
        self.kv_reservation_factor = factor;
        self
    }
    pub fn with_prefill_priority(mut self, on: bool) -> Self {
        self.prefill_priority = on;
        self
    }
    pub fn with_engine(mut self, engine: EngineConfig) -> Self {
        self.engine = engine;
        self
    }
    pub fn with_kv_capacity(mut self, tokens: u32) -> Self {
        self.engine.kv_capacity_tokens = tokens;
        self
    }
}

struct Slot {
    replica: Replica,
    /// Time at which this replica finishes its current step and can make a
    /// new decision. Never behind the global clock while it has work.
    clock_us: u64,
}

/// Run one configuration against one workload to completion.
pub fn run(cfg: &GatewayConfig, workload: &Workload, n: usize) -> RunStats {
    let requests = workload.generate(n);
    run_requests(cfg, &requests)
}

/// Run one configuration against a fixed request trace.
///
/// The loop **drains**: it does not stop at a wall-clock horizon, it stops
/// when every admitted request has finished. Truncating at a horizon would
/// leave in-flight requests contributing to occupancy but never contributing
/// a completion time, which breaks Little's Law for a reason that has
/// nothing to do with the system under test -- and would make the invariant
/// worthless as a self-check.
pub fn run_requests(cfg: &GatewayConfig, requests: &[Request]) -> RunStats {
    let mut estimator = Estimator::new(cfg.estimator_spread, cfg.estimator_seed);
    let mut kv_estimator = Estimator::new(cfg.reservation_spread, cfg.estimator_seed ^ 0x5EED);
    let n_tenants = requests.iter().map(|r| r.tenant as usize).max().unwrap_or(0) + 1;
    let mut sched = Scheduler::new(cfg.policy, n_tenants);
    let mut slots: Vec<Slot> = (0..cfg.engine.replicas.max(1))
        .map(|_| Slot {
            replica: Replica::new(cfg.engine),
            clock_us: 0,
        })
        .collect();

    let mut now_us: u64 = 0;
    let mut next_arrival = 0usize;
    let mut completed: Vec<Completed> = Vec::with_capacity(requests.len());
    let mut rejected: Vec<Request> = Vec::new();
    let mut evictions: u32 = 0;
    let mut restarts: HashMap<u64, u32> = HashMap::new();

    // Occupancy integral for Little's Law: sum of (in-system count * dt),
    // accumulated at event boundaries. "In system" means queued *or*
    // running, matching the sojourn time W measured from arrival.
    let mut occupancy_us: f64 = 0.0;
    let mut batch_integral: f64 = 0.0;
    let mut busy_us: f64 = 0.0;
    let mut events: u64 = 0;
    let mut truncated = false;

    // Observed fleet drain rate in tokens/s, seeded from the hardware model
    // and then corrected by measurement. The predictive admission policy
    // needs some estimate of service capacity and is not allowed a
    // privileged one.
    let mut served_tokens: f64 = 0.0;
    let mut served_us: f64 = 0.0;
    let nominal_rate = cfg
        .engine
        .decode_throughput_tok_per_s(cfg.engine.max_batch / 2)
        * cfg.engine.replicas.max(1) as f64;

    loop {
        events += 1;
        if events > cfg.max_events {
            truncated = true;
            break;
        }

        // 1. Admit every arrival whose time has come.
        while next_arrival < requests.len() && requests[next_arrival].arrival_us <= now_us {
            let req = requests[next_arrival];
            next_arrival += 1;
            let est = estimator.estimate(&req);
            let kv_est = kv_estimator.estimate(&req);
            let mut item = Queued::new(req, est, now_us);
            item.kv_est = kv_est;
            let rate = if served_us > 0.0 {
                served_tokens / (served_us / 1_000_000.0)
            } else {
                nominal_rate
            };
            if admit(cfg.admission, &item, &sched, rate, &cfg.engine) {
                sched.push(item);
            } else {
                rejected.push(req);
            }
        }

        // 2. Fill replicas from the queue, always offering the next request
        //    to the *least loaded* ready replica.
        //
        //    An earlier draft filled replicas greedily in index order, which
        //    packed replica 0 to its batch limit before replica 1 received
        //    anything. That is not how any load balancer behaves, and it
        //    made the multi-replica experiments in section 7 measure a
        //    scheduling artefact rather than the effect of adding capacity.
        for slot in slots.iter_mut() {
            if slot.clock_us <= now_us {
                slot.clock_us = now_us;
            }
        }
        let mut exhausted = vec![false; slots.len()];
        loop {
            let target = slots
                .iter()
                .enumerate()
                .filter(|(i, s)| s.clock_us <= now_us && !exhausted[*i])
                .min_by_key(|(i, s)| (s.replica.batch(), *i))
                .map(|(i, _)| i);
            let Some(i) = target else { break };
            let factor = cfg.kv_reservation_factor;
            let cap = slots[i].replica.cfg.kv_capacity_tokens;
            let replica = &slots[i].replica;
            let picked = sched.pop_admissible(now_us, |q| {
                replica.can_admit(reservation(q, factor, cap))
            });
            match picked {
                Some(item) => {
                    let reserve = reservation(&item, factor, cap);
                    slots[i].replica.admit(item.req, now_us, reserve);
                }
                // Nothing in the queue fits this replica. Another replica
                // may still have room for a different shape of request, so
                // retire this one and keep going rather than stopping the
                // whole fill pass.
                None => exhausted[i] = true,
            }
        }

        let in_flight: usize = slots.iter().map(|s| s.replica.batch()).sum();
        let queued = sched.len();

        // 3. Terminate when nothing is anywhere.
        if in_flight == 0 && queued == 0 {
            if next_arrival >= requests.len() {
                break;
            }
            // Idle. Jump straight to the next arrival: this is the entire
            // reason for a discrete-event loop rather than a ticked one.
            now_us = requests[next_arrival].arrival_us;
            for slot in slots.iter_mut() {
                slot.clock_us = now_us;
            }
            continue;
        }

        // 4. Step every replica sitting exactly at the global clock.
        for slot in slots.iter_mut() {
            if slot.clock_us > now_us || slot.replica.batch() == 0 {
                continue;
            }
            let (elapsed, finished) = slot.replica.step(now_us, cfg.prefill_priority);
            slot.clock_us = now_us + elapsed;
            for c in finished {
                let mut c = c;
                c.restarts = restarts.get(&c.req.id).copied().unwrap_or(0);
                completed.push(c);
            }
        }

        // 5. Preempt only where the next step genuinely cannot proceed.
        for slot in slots.iter_mut() {
            let step_us = cfg
                .engine
                .decode_step_us(slot.replica.batch().max(1));
            while slot.replica.over_subscribed() && slot.replica.batch() > 1 {
                match slot.replica.evict_one() {
                    Some(ev) => {
                        evictions += 1;
                        let n = restarts.entry(ev.req.id).or_insert(0);
                        *n += 1;
                        let n = *n;
                        if n > cfg.max_restarts {
                            // Out of retries. Reject rather than thrash: a
                            // request that cannot be made to fit is a
                            // capacity failure, and reporting it as one is
                            // more useful than hiding it in the tail.
                            rejected.push(ev.req);
                            continue;
                        }
                        let mut est = estimator.estimate(&ev.req);
                        let mut kv_est = kv_estimator.estimate(&ev.req);
                        // The gateway now knows this request emitted at
                        // least `ev.emitted` tokens. Re-admitting it on the
                        // original under-estimate would reproduce exactly
                        // the memory pressure that just evicted it.
                        est.predicted_output_tokens =
                            est.predicted_output_tokens.max(ev.emitted + 1);
                        kv_est.predicted_output_tokens =
                            kv_est.predicted_output_tokens.max(ev.emitted + 1);
                        let mut q = Queued::new(ev.req, est, now_us);
                        q.kv_est = kv_est;
                        // Exponential backoff. Linear backoff still lets a
                        // pathological configuration spend the entire run
                        // evicting; doubling makes the thrash decay.
                        q.not_before_us = now_us + step_us * (1u64 << n.min(20));
                        sched.requeue(q);
                    }
                    None => break,
                }
            }
        }

        // 6. Advance to the earliest outstanding event: an arrival, or a
        //    replica reaching its next decision point. A replica that just
        //    emptied counts too -- its clock marks the moment occupancy
        //    dropped, and skipping it would inflate the occupancy integral
        //    and break Little's Law for a bookkeeping reason.
        let mut next_us = u64::MAX;
        if next_arrival < requests.len() {
            next_us = next_us.min(requests[next_arrival].arrival_us.max(now_us + 1));
        }
        for slot in slots.iter() {
            if slot.clock_us > now_us {
                next_us = next_us.min(slot.clock_us);
            }
        }
        if let Some(ready) = sched.next_ready_us() {
            if ready > now_us {
                next_us = next_us.min(ready);
            }
        }
        if next_us == u64::MAX {
            if in_flight == 0 && queued == 0 {
                break;
            }
            next_us = now_us + 1;
        }
        let dt = next_us - now_us;

        occupancy_us += (in_flight + queued) as f64 * dt as f64;
        batch_integral += in_flight as f64 * dt as f64;
        if in_flight > 0 {
            busy_us += dt as f64;
            served_us += dt as f64;
            served_tokens += in_flight as f64 * dt as f64
                / cfg.engine.decode_step_us(in_flight.max(1)) as f64;
        }

        now_us = next_us;
    }

    let sim_duration_s = now_us as f64 / 1_000_000.0;
    let n_done = completed.len();
    let lambda = if sim_duration_s > 0.0 {
        n_done as f64 / sim_duration_s
    } else {
        0.0
    };
    let w = if n_done > 0 {
        completed.iter().map(|c| c.latency_us() as f64).sum::<f64>() / n_done as f64 / 1_000_000.0
    } else {
        0.0
    };
    let measured_l = if now_us > 0 {
        occupancy_us / now_us as f64
    } else {
        0.0
    };

    RunStats {
        completed,
        rejected,
        offered: requests.len(),
        little: Little {
            measured_l,
            lambda,
            w,
        },
        sim_duration_s,
        evictions,
        engine: cfg.engine,
        truncated,
        mean_batch: if busy_us > 0.0 {
            batch_integral / busy_us
        } else {
            0.0
        },
    }
}

/// KV tokens to set aside for a queued request.
///
/// Reserved against the *projected peak*, never against the prompt. Admitting
/// on prompt size is how a server ends up evicting: the sequence fits when it
/// starts and does not fit once it is 600 tokens into its output.
fn reservation(q: &Queued, factor: f64, cap: u32) -> u32 {
    let projected = (q.projected_kv() as f64 * factor).round();
    (projected.max(q.req.prompt_tokens as f64) as u32).min(cap)
}

/// Static batching: collect up to `batch` requests, run them together, and
/// admit nothing new until **every** sequence in the batch has finished.
///
/// This is the baseline continuous batching is measured against, and the
/// difference is not an optimisation, it is a change of behaviour class.
/// Under static batching a batch's duration is the duration of its longest
/// member, so a single 900-token summarisation holds every slot in its batch
/// open through 900 decode steps -- including the slots of chat turns that
/// finished after 40. That is head-of-line blocking at the *batch* level, and
/// on a heavy-tailed workload it dominates everything else the system does.
pub fn run_static_batching(cfg: &GatewayConfig, requests: &[Request], batch: usize) -> RunStats {
    let mut now_us: u64 = 0;
    let mut next_arrival = 0usize;
    let mut waiting: Vec<Request> = Vec::new();
    let mut completed: Vec<Completed> = Vec::new();
    let mut occupancy_us: f64 = 0.0;
    let mut batch_integral: f64 = 0.0;
    let mut busy_us: f64 = 0.0;

    loop {
        while next_arrival < requests.len() && requests[next_arrival].arrival_us <= now_us {
            waiting.push(requests[next_arrival]);
            next_arrival += 1;
        }
        if waiting.is_empty() {
            if next_arrival >= requests.len() {
                break;
            }
            now_us = requests[next_arrival].arrival_us;
            continue;
        }

        let take = waiting.len().min(batch.max(1));
        let group: Vec<Request> = waiting.drain(..take).collect();
        let started = now_us;

        let prefill: u64 = group
            .iter()
            .map(|r| cfg.engine.prefill_us(r.prompt_tokens))
            .sum();
        let longest = group.iter().map(|r| r.output_tokens).max().unwrap_or(1);
        let decode = cfg.engine.decode_step_us(group.len()) * longest as u64;
        let batch_end = started + prefill + decode;

        for r in &group {
            // Every member is released only when the whole batch retires.
            // That is what static batching means, and it is the cost.
            completed.push(Completed {
                req: *r,
                admitted_us: started,
                first_token_us: started + prefill,
                finished_us: batch_end,
                restarts: 0,
            });
        }

        let dt = (batch_end - started) as f64;
        occupancy_us += (group.len() + waiting.len()) as f64 * dt;
        batch_integral += group.len() as f64 * dt;
        busy_us += dt;
        now_us = batch_end;
    }

    let sim_duration_s = now_us as f64 / 1_000_000.0;
    let lambda = if sim_duration_s > 0.0 {
        completed.len() as f64 / sim_duration_s
    } else {
        0.0
    };
    let w = if !completed.is_empty() {
        completed.iter().map(|c| c.latency_us() as f64).sum::<f64>()
            / completed.len() as f64
            / 1_000_000.0
    } else {
        0.0
    };

    RunStats {
        completed,
        rejected: Vec::new(),
        offered: requests.len(),
        little: Little {
            measured_l: if now_us > 0 {
                occupancy_us / now_us as f64
            } else {
                0.0
            },
            lambda,
            w,
        },
        sim_duration_s,
        evictions: 0,
        engine: cfg.engine,
        truncated: false,
        mean_batch: if busy_us > 0.0 {
            batch_integral / busy_us
        } else {
            0.0
        },
    }
}
