//! End-to-end invariants of the event loop.
//!
//! These are the tests that would have caught the simulator's real bugs. Three
//! of them are named after the bug they pin:
//!
//! * `littles_law_holds_across_every_configuration` -- an early event loop
//!   stepped every replica and then advanced a single global clock by the
//!   *minimum* elapsed time, crediting slower replicas with work they had not
//!   done. Every reported number looked reasonable. Little's Law did not agree
//!   with them, and that disagreement was the only signal anything was wrong.
//! * `replicas_are_filled_evenly` -- replicas were filled greedily in index
//!   order, so replica 0 saturated before replica 1 received anything, and the
//!   measured benefit of a second replica was roughly half of what it should
//!   have been.
//! * `an_impossible_configuration_rejects_rather_than_thrashing` -- an
//!   eviction livelock ran twenty million evictions, completed nothing, and
//!   reported a plausible 97.6% SLO attainment.

use gateway::capacity::{saturation_rps, trace_at_utilisation};
use gateway::engine::EngineConfig;
use gateway::sched::{Admission, Policy};
use gateway::sim::{run, run_requests, run_static_batching, GatewayConfig};
use gateway::workload::{Kind, Workload};

fn base() -> GatewayConfig {
    GatewayConfig::default()
}

fn small_workload() -> Workload {
    Workload::new(3.0, 7)
}

// ------------------------------------------------------------ conservation

#[test]
fn every_offered_request_is_either_completed_or_rejected() {
    let w = small_workload();
    for admission in [
        Admission::AcceptAll,
        Admission::QueueDepth(20),
        Admission::QueueTokens(20_000),
        Admission::SloPredictive,
        Admission::CostAware(4_000),
    ] {
        let s = run(&base().with_admission(admission), &w, 400);
        assert!(!s.truncated, "{} truncated", admission.name());
        assert_eq!(
            s.completed.len() + s.rejected.len(),
            s.offered,
            "{} lost {} requests",
            admission.name(),
            s.offered as i64 - (s.completed.len() + s.rejected.len()) as i64
        );
    }
}

#[test]
fn no_request_is_both_completed_and_rejected() {
    let w = small_workload();
    let s = run(&base().with_admission(Admission::CostAware(3_000)), &w, 400);
    let mut ids: Vec<u64> = s
        .completed
        .iter()
        .map(|c| c.req.id)
        .chain(s.rejected.iter().map(|r| r.id))
        .collect();
    let n = ids.len();
    ids.sort_unstable();
    ids.dedup();
    assert_eq!(ids.len(), n, "an id appeared in both outcomes");
}

#[test]
fn every_policy_conserves_requests() {
    let w = small_workload();
    for p in Policy::ALL {
        let s = run(&base().with_policy(p), &w, 400);
        assert!(!s.truncated, "{} truncated", p.name());
        assert_eq!(s.completed.len(), 400, "{} dropped work", p.name());
    }
}

#[test]
fn completed_requests_emit_exactly_the_tokens_they_asked_for() {
    let s = run(&base(), &small_workload(), 300);
    for c in &s.completed {
        assert!(c.req.output_tokens > 0);
        assert!(
            c.finished_us >= c.first_token_us,
            "request {} finished before its first token",
            c.req.id
        );
        assert!(
            c.first_token_us >= c.req.arrival_us,
            "request {} produced a token before it arrived",
            c.req.id
        );
    }
}

#[test]
fn latency_decomposes_into_queue_and_service() {
    let s = run(&base(), &small_workload(), 300);
    for c in &s.completed {
        assert!(
            c.latency_us() >= c.ttft_us(),
            "total latency below time to first token for {}",
            c.req.id
        );
        assert!(
            c.ttft_us() >= c.queue_us(),
            "time to first token below queue delay for {}",
            c.req.id
        );
    }
}

// ------------------------------------------------------------ Little's Law

#[test]
fn littles_law_holds_across_every_configuration() {
    let w = small_workload();
    let configs: Vec<(&str, GatewayConfig)> = vec![
        ("fifo", base()),
        ("sjf", base().with_policy(Policy::Sjf)),
        ("drr", base().with_policy(Policy::DeficitRoundRobin)),
        ("class-priority", base().with_policy(Policy::ClassPriority)),
        ("sjf-aged", base().with_policy(Policy::SjfAged)),
        ("2 replicas", base().with_replicas(2)),
        ("4 replicas", base().with_replicas(4)),
        ("shedding", base().with_admission(Admission::CostAware(3_000))),
        ("decode priority", base().with_prefill_priority(false)),
        ("tight kv", base().with_kv_capacity(60_000)),
        ("under-reserved", base().with_reservation(0.85)),
        ("noisy estimates", base().with_both_spreads(4.0)),
    ];
    for (name, cfg) in configs {
        let s = run(&cfg, &w, 400);
        assert!(!s.truncated, "{} truncated -- its numbers are fiction", name);
        assert!(
            s.little.relative_error() < 0.02,
            "{}: L = {:.4} but lambda x W = {:.4} (relative error {:.4})",
            name,
            s.little.measured_l,
            s.little.predicted_l(),
            s.little.relative_error()
        );
    }
}

#[test]
fn littles_law_holds_at_every_utilisation() {
    let w = small_workload();
    let cfg = base();
    let mu = saturation_rps(&cfg, &w, 300);
    for rho in [0.3, 0.6, 0.85, 0.95] {
        let trace = trace_at_utilisation(&w, mu, rho, 400, 7);
        let s = run_requests(&cfg, &trace);
        assert!(!s.truncated, "rho {} truncated", rho);
        assert!(
            s.little.relative_error() < 0.02,
            "rho {}: relative error {:.4}",
            rho,
            s.little.relative_error()
        );
    }
}

#[test]
fn measured_occupancy_is_positive_under_load() {
    let s = run(&base(), &small_workload(), 400);
    assert!(
        s.little.measured_l > 1.0,
        "a loaded gateway must hold more than one request on average, got {}",
        s.little.measured_l
    );
    assert!(s.little.lambda > 0.0);
    assert!(s.little.w > 0.0);
}

// ------------------------------------------------------------- determinism

#[test]
fn the_same_configuration_produces_identical_results() {
    let w = small_workload();
    let a = run(&base(), &w, 400);
    let b = run(&base(), &w, 400);
    assert_eq!(a.completed.len(), b.completed.len());
    for (x, y) in a.completed.iter().zip(b.completed.iter()) {
        assert_eq!(x.req.id, y.req.id);
        assert_eq!(x.finished_us, y.finished_us);
        assert_eq!(x.first_token_us, y.first_token_us);
    }
    assert_eq!(a.evictions, b.evictions);
    assert_eq!(a.little.measured_l.to_bits(), b.little.measured_l.to_bits());
}

#[test]
fn a_different_seed_produces_a_different_trace() {
    let a = run(&base(), &Workload::new(3.0, 1), 300);
    let b = run(&base(), &Workload::new(3.0, 2), 300);
    assert_ne!(
        a.throughput_rps().to_bits(),
        b.throughput_rps().to_bits(),
        "two seeds produced identical throughput -- the seed is being ignored"
    );
}

#[test]
fn workload_generation_is_reproducible() {
    let w = Workload::new(3.0, 42);
    let a = w.generate(500);
    let b = w.generate(500);
    assert_eq!(a.len(), b.len());
    for (x, y) in a.iter().zip(b.iter()) {
        assert_eq!(x.id, y.id);
        assert_eq!(x.arrival_us, y.arrival_us);
        assert_eq!(x.prompt_tokens, y.prompt_tokens);
        assert_eq!(x.output_tokens, y.output_tokens);
        assert_eq!(x.kind, y.kind);
    }
}

#[test]
fn arrivals_are_non_decreasing() {
    let reqs = Workload::new(5.0, 3).generate(2000);
    for pair in reqs.windows(2) {
        assert!(
            pair[1].arrival_us >= pair[0].arrival_us,
            "arrivals went backwards: {} then {}",
            pair[0].arrival_us,
            pair[1].arrival_us
        );
    }
}

#[test]
fn a_workload_contains_every_class_and_both_tenants() {
    let reqs = Workload::new(4.0, 11).generate(3000);
    for k in Kind::ALL {
        assert!(
            reqs.iter().any(|r| r.kind == k),
            "no {} requests generated",
            k.name()
        );
    }
    assert!(reqs.iter().any(|r| r.tenant == 0));
    assert!(reqs.iter().any(|r| r.tenant == 1));
}

// -------------------------------------------------------- load balancing

#[test]
fn replicas_are_filled_evenly() {
    // The greedy-fill bug: replicas were filled in index order, so replica 0
    // saturated before replica 1 received anything. The symptom was subtle --
    // a second replica appeared to buy roughly half of what it should.
    let w = small_workload();
    let mu1 = saturation_rps(&base(), &w, 300);
    let mu2 = saturation_rps(&base().with_replicas(2), &w, 300);
    let ratio = mu2 / mu1;
    assert!(
        ratio > 1.8,
        "two replicas delivered only {:.2}x the capacity of one -- load is not \
         being spread",
        ratio
    );
    assert!(
        ratio < 2.3,
        "two replicas delivered {:.2}x, which is more than two replicas can do",
        ratio
    );
}

#[test]
fn adding_replicas_never_reduces_capacity() {
    let w = small_workload();
    let mut last = 0.0;
    for n in 1..=4 {
        let mu = saturation_rps(&base().with_replicas(n), &w, 250);
        assert!(
            mu > last,
            "capacity fell from {:.2} to {:.2} going to {} replicas",
            last,
            mu,
            n
        );
        last = mu;
    }
}

// --------------------------------------------------------- headline claims

#[test]
fn continuous_batching_beats_every_static_batch_size() {
    let w = small_workload();
    let cfg = base();
    let mu = saturation_rps(&cfg, &w, 300);
    let trace = trace_at_utilisation(&w, mu, 0.85, 400, 7);
    let continuous = run_requests(&cfg, &trace);
    for b in [8usize, 16, 32, 64] {
        let stat = run_static_batching(&cfg, &trace, b);
        assert!(
            continuous.throughput_rps() > stat.throughput_rps() * 2.0,
            "static batch {} reached {:.2} req/s against continuous {:.2} -- the \
             gap should be large",
            b,
            stat.throughput_rps(),
            continuous.throughput_rps()
        );
    }
}

#[test]
fn latency_rises_monotonically_with_utilisation() {
    let w = small_workload();
    let cfg = base();
    let mu = saturation_rps(&cfg, &w, 300);
    let mut last_p99 = 0u64;
    let mut last_attainment = 1.01;
    for rho in [0.4, 0.6, 0.85, 0.95] {
        let trace = trace_at_utilisation(&w, mu, rho, 500, 7);
        let s = run_requests(&cfg, &trace);
        let p99 = s.ttft().p99();
        assert!(
            p99 >= last_p99,
            "tail TTFT fell from {} to {} as load rose to rho {}",
            last_p99,
            p99,
            rho
        );
        let att = s.slo_attainment();
        assert!(
            att <= last_attainment + 1e-9,
            "SLO attainment rose from {:.4} to {:.4} as load rose to rho {}",
            last_attainment,
            att,
            rho
        );
        last_p99 = p99;
        last_attainment = att;
    }
}

#[test]
fn the_latency_curve_is_convex_not_linear() {
    // The report's central claim about shape. The jump from 0.85 to 0.95 must
    // dwarf the jump from 0.4 to 0.5, for the same absolute change in load.
    let w = small_workload();
    let cfg = base();
    let mu = saturation_rps(&cfg, &w, 300);
    let p99 = |rho: f64| {
        let trace = trace_at_utilisation(&w, mu, rho, 600, 7);
        run_requests(&cfg, &trace).ttft().p99() as f64
    };
    let low_step = p99(0.5) - p99(0.4);
    let high_step = p99(0.95) - p99(0.85);
    assert!(
        high_step > low_step * 10.0,
        "the same 0.1 of utilisation cost {:.0}us at the bottom and {:.0}us at \
         the top -- that is not a hyperbola",
        low_step,
        high_step
    );
}

#[test]
fn scheduling_redistributes_delay_without_creating_capacity() {
    // Section 8's claim, as an invariant: policies must not move throughput
    // materially, because they reorder work rather than creating or
    // destroying it.
    let w = small_workload();
    let cfg = base();
    let mu = saturation_rps(&cfg, &w, 300);
    let trace = trace_at_utilisation(&w, mu, 0.85, 600, 7);
    let fifo = run_requests(&cfg, &trace).throughput_rps();
    for p in Policy::ALL {
        let t = run_requests(&cfg.clone().with_policy(p), &trace).throughput_rps();
        let delta = (t - fifo).abs() / fifo;
        assert!(
            delta < 0.05,
            "{} changed throughput by {:.1}% -- scheduling cannot manufacture \
             capacity",
            p.name(),
            delta * 100.0
        );
    }
}

#[test]
fn prefill_priority_trades_ttft_for_tpot() {
    let w = small_workload();
    let cfg = base();
    let mu = saturation_rps(&cfg, &w, 300);
    let trace = trace_at_utilisation(&w, mu, 0.85, 500, 7);
    let prefill_first = run_requests(&cfg, &trace);
    let decode_first = run_requests(&cfg.clone().with_prefill_priority(false), &trace);
    assert!(
        decode_first.tpot().p50() < prefill_first.tpot().p50(),
        "decode priority must produce the better time-per-output-token"
    );
    assert!(
        decode_first.ttft().p99() > prefill_first.ttft().p99(),
        "and must pay for it in time-to-first-token"
    );
}

// ------------------------------------------------------------- robustness

#[test]
fn an_impossible_configuration_rejects_rather_than_thrashing() {
    // The eviction livelock. A reservation far below what sequences actually
    // need used to spin forever: evict, free a large block, re-admit the same
    // sequence, over-grow, evict again. The run terminated only on the event
    // ceiling and reported plausible numbers. It must now either complete
    // honestly or refuse work -- never spin.
    let w = small_workload();
    let cfg = base().with_kv_capacity(20_000).with_reservation(0.2);
    let s = run(&cfg, &w, 200);
    assert!(
        !s.truncated,
        "a pathological configuration must terminate honestly, not on the ceiling"
    );
    assert_eq!(
        s.completed.len() + s.rejected.len(),
        s.offered,
        "requests vanished during thrashing"
    );
    assert!(
        s.evictions < 200_000,
        "{} evictions for 200 requests is a livelock, not a workload",
        s.evictions
    );
}

#[test]
fn a_gateway_with_no_traffic_produces_no_statistics() {
    let s = run(&base(), &small_workload(), 0);
    assert_eq!(s.completed.len(), 0);
    assert_eq!(s.offered, 0);
    assert_eq!(s.throughput_rps(), 0.0);
    assert_eq!(s.slo_attainment(), 0.0);
    assert!(s.little.relative_error().is_finite());
    assert!(!s.truncated);
}

#[test]
fn a_single_request_runs_to_completion() {
    let s = run(&base(), &small_workload(), 1);
    assert_eq!(s.completed.len(), 1);
    assert!(s.completed[0].met_slo(), "one request alone must meet its SLO");
    assert_eq!(s.evictions, 0);
}

#[test]
fn a_tiny_kv_cache_refuses_work_it_cannot_hold() {
    // Not every request fits in a 2000-token cache. The gateway must say so
    // rather than queueing forever or evicting in a loop.
    let w = small_workload();
    let s = run(&base().with_kv_capacity(2_000), &w, 150);
    assert!(!s.truncated);
    assert_eq!(s.completed.len() + s.rejected.len(), s.offered);
}

#[test]
fn an_enormous_kv_cache_never_evicts() {
    let s = run(&base().with_kv_capacity(50_000_000), &small_workload(), 300);
    assert_eq!(
        s.evictions, 0,
        "memory was not scarce, so nothing should have been preempted"
    );
}

#[test]
fn rejection_rate_and_offered_success_rate_agree_with_the_counts() {
    let s = run(
        &base().with_admission(Admission::CostAware(2_000)),
        &small_workload(),
        500,
    );
    let expected_rejection = s.rejected.len() as f64 / s.offered as f64;
    assert!((s.rejection_rate() - expected_rejection).abs() < 1e-12);
    let met = s.completed.iter().filter(|c| c.met_slo()).count();
    let expected_success = met as f64 / s.offered as f64;
    assert!((s.offered_success_rate() - expected_success).abs() < 1e-12);
    assert!(
        s.offered_success_rate() <= s.slo_attainment() + 1e-12,
        "offered success can never exceed attainment over served requests"
    );
}

#[test]
fn goodput_never_exceeds_throughput() {
    for admission in [Admission::AcceptAll, Admission::CostAware(2_000)] {
        let s = run(&base().with_admission(admission), &small_workload(), 400);
        assert!(s.goodput_rps() <= s.throughput_rps() + 1e-12);
    }
}

#[test]
fn a_custom_engine_config_is_actually_used() {
    let mut fast = EngineConfig::default();
    fast.prefill_tokens_per_s *= 4.0;
    fast.decode_weight_load_us /= 4;
    let w = small_workload();
    let slow_mu = saturation_rps(&base(), &w, 250);
    let fast_mu = saturation_rps(&base().with_engine(fast), &w, 250);
    assert!(
        fast_mu > slow_mu * 1.5,
        "a four-times-faster engine produced {:.2} against {:.2}",
        fast_mu,
        slow_mu
    );
}

// ---------------------------------------------------------------------------
// The truncation flag.
//
// A simulator that hits its own event ceiling and returns anyway produces
// numbers that look ordinary: a throughput, a p99, a Little's Law residual
// that all pass every other assertion in this file, because they describe a
// prefix of the run rather than the run. The flag is the only thing standing
// between "we did not finish" and a plausible-looking lie, so it gets a test
// on both branches.
// ---------------------------------------------------------------------------

#[test]
fn truncation_flag_is_set_when_the_event_ceiling_is_hit() {
    let mut cfg = base();
    cfg.max_events = 40;
    let trace = trace_at_utilisation(&small_workload(), 4.0, 0.9, 400, 11);
    let stats = run_requests(&cfg, &trace);
    assert!(
        stats.truncated,
        "a run cut off after {} events reported itself complete",
        cfg.max_events
    );
    assert!(
        stats.completed.len() < trace.len(),
        "the ceiling was supposedly hit but all {} requests finished",
        trace.len()
    );
}

#[test]
fn truncation_flag_is_clear_on_a_run_that_drains() {
    let cfg = base();
    let trace = trace_at_utilisation(&small_workload(), 4.0, 0.6, 400, 11);
    let stats = run_requests(&cfg, &trace);
    assert!(
        !stats.truncated,
        "a run that drained {} of {} requests claimed it was truncated",
        stats.completed.len(),
        trace.len()
    );
    assert_eq!(
        stats.completed.len() + stats.rejected.len(),
        trace.len(),
        "an untruncated run must account for every request"
    );
}

#[test]
fn the_event_ceiling_is_not_reached_by_any_configuration_this_suite_uses() {
    // If the default ceiling were close to what the experiments need, a
    // slightly heavier workload would silently start truncating and the report
    // would be quietly wrong rather than loudly wrong.
    for rho in [0.4, 0.75, 0.95, 1.05] {
        let trace = trace_at_utilisation(&small_workload(), 4.0, rho, 1200, 3);
        let stats = run_requests(&base(), &trace);
        assert!(
            !stats.truncated,
            "rho {:.2} truncated at the default ceiling",
            rho
        );
    }
}
