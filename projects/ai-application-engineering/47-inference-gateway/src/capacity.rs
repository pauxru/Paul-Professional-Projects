//! Capacity measurement.
//!
//! # Why utilisation is measured rather than assumed
//!
//! Every claim in the report is expressed as a function of utilisation
//! `rho = lambda / mu`, because that is the variable latency actually
//! depends on. Getting `mu` wrong by 20% moves an experiment from
//! "comfortably loaded" to "unstable" and the report would be describing a
//! different system than it claims to.
//!
//! `mu` is therefore not computed from the hardware constants. It is
//! *measured*, by handing the gateway an infinite backlog and seeing how
//! fast it drains. This automatically accounts for prefill stealing time
//! from decode, for the batch size the scheduler actually achieves rather
//! than the configured maximum, and for KV-capacity limits -- none of which
//! a closed-form estimate from `EngineConfig` would capture.
//!
//! The difference is not academic. The naive estimate from
//! `decode_throughput_tok_per_s(max_batch)` divided by mean output length
//! overstates capacity substantially, because it assumes the device spends
//! all its time decoding at full batch. Section 1 of the report shows the
//! measured figure and the naive one side by side.

use crate::sim::{run_requests, GatewayConfig};
use crate::workload::{Request, Workload};

/// Measured saturation throughput in requests per second.
///
/// Presents the gateway with `n` requests all arriving at t=0, so the queue
/// is never the limiting factor, and reports the drain rate. Because the
/// simulator drains to completion, the tail of the run has a shrinking batch
/// and therefore lower throughput; `warmup` and `cooldown` trim the ends so
/// the figure describes steady state rather than the ramp.
pub fn saturation_rps(cfg: &GatewayConfig, workload: &Workload, n: usize) -> f64 {
    let mut requests = workload.generate(n);
    for r in requests.iter_mut() {
        r.arrival_us = 0;
    }
    let stats = run_requests(cfg, &requests);
    if stats.completed.is_empty() {
        return 0.0;
    }

    let mut finish: Vec<u64> = stats.completed.iter().map(|c| c.finished_us).collect();
    finish.sort_unstable();
    let lo = finish.len() / 5;
    let hi = finish.len() - finish.len() / 5;
    if hi <= lo + 1 {
        return stats.throughput_rps();
    }
    let span_us = finish[hi - 1].saturating_sub(finish[lo]);
    if span_us == 0 {
        return stats.throughput_rps();
    }
    (hi - lo) as f64 / (span_us as f64 / 1_000_000.0)
}

/// Saturation throughput in output tokens per second, which is the figure a
/// capacity plan should actually be written in -- requests per second is
/// only meaningful for a fixed length distribution.
pub fn saturation_tokens_per_s(cfg: &GatewayConfig, workload: &Workload, n: usize) -> f64 {
    let mut requests = workload.generate(n);
    for r in requests.iter_mut() {
        r.arrival_us = 0;
    }
    let stats = run_requests(cfg, &requests);
    if stats.sim_duration_s <= 0.0 {
        return 0.0;
    }
    stats.token_throughput()
}

/// The estimate a capacity plan usually starts from: peak decode throughput
/// divided by mean output length. Kept so the report can show how far it is
/// from the measured value, and why.
pub fn naive_rps(cfg: &GatewayConfig, workload: &Workload, n: usize) -> f64 {
    let requests = workload.generate(n);
    let mean_output = requests.iter().map(|r| r.output_tokens as f64).sum::<f64>()
        / requests.len().max(1) as f64;
    let peak = cfg
        .engine
        .decode_throughput_tok_per_s(cfg.engine.max_batch)
        * cfg.engine.replicas.max(1) as f64;
    peak / mean_output
}

/// Build a trace offering load at a target utilisation of measured capacity.
pub fn trace_at_utilisation(
    workload: &Workload,
    mu_rps: f64,
    rho: f64,
    n: usize,
    seed: u64,
) -> Vec<Request> {
    let w = Workload {
        tenants: workload.tenants.clone(),
        arrival_rate_per_s: (mu_rps * rho).max(1e-6),
        seed,
    };
    w.generate(n)
}
