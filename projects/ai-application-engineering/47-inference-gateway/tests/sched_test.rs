//! Tests for queue policies and admission control.
//!
//! The DRR fairness tests matter most. Deficit round robin charges tenants by
//! estimated *token* cost rather than by request count, and the difference is
//! not cosmetic: charging by request lets a tenant claim unbounded capacity by
//! sending longer jobs, which is exactly what the analytics tenant in this
//! workload would do.

use gateway::sched::{Admission, Policy, Queued, Scheduler};
use gateway::workload::{Estimate, Estimator, Kind, Request};

fn req(id: u64, tenant: u8, kind: Kind, prompt: u32, output: u32, arrival: u64) -> Request {
    Request {
        id,
        tenant,
        kind,
        arrival_us: arrival,
        prompt_tokens: prompt,
        output_tokens: output,
    }
}

fn queued(r: Request) -> Queued {
    let est = Estimate {
        prompt_tokens: r.prompt_tokens,
        predicted_output_tokens: r.output_tokens,
        kind: r.kind,
    };
    let arrival = r.arrival_us;
    Queued::new(r, est, arrival)
}

/// Drain a scheduler with no capacity limits, returning ids in service order.
///
/// Uses `pop_admissible`, which is where policy lives. `pop_any` is the
/// unordered drain hatch the event loop uses to release a backlog and does not
/// consult the policy at all.
fn drain_at(s: &mut Scheduler, now: u64) -> Vec<u64> {
    let mut order = Vec::new();
    while let Some(q) = s.pop_admissible(now, |_| true) {
        order.push(q.req.id);
    }
    order
}

/// Late enough that every request in these tests is past its arrival time and
/// past any backoff, so `drain` measures ordering rather than readiness.
const LATE: u64 = 1_000_000_000;

fn drain(s: &mut Scheduler) -> Vec<u64> {
    drain_at(s, LATE)
}

// -------------------------------------------------------------- basic queue

#[test]
fn a_new_scheduler_is_empty() {
    for p in Policy::ALL {
        let s = Scheduler::new(p, 2);
        assert!(s.is_empty(), "{} started non-empty", p.name());
        assert_eq!(s.len(), 0);
        assert!(s.peek().is_none());
    }
}

#[test]
fn popping_an_empty_scheduler_returns_none() {
    for p in Policy::ALL {
        let mut s = Scheduler::new(p, 2);
        assert!(s.pop_any().is_none(), "{}", p.name());
    }
}

#[test]
fn every_policy_serves_every_request_exactly_once() {
    for p in Policy::ALL {
        let mut s = Scheduler::new(p, 2);
        for i in 0..40u64 {
            let kind = Kind::ALL[(i % 4) as usize];
            s.push(queued(req(i, (i % 2) as u8, kind, 100 + i as u32, 50, i * 1000)));
        }
        assert_eq!(s.len(), 40);
        let mut order = drain(&mut s);
        assert_eq!(order.len(), 40, "{} lost or duplicated work", p.name());
        order.sort_unstable();
        order.dedup();
        assert_eq!(order.len(), 40, "{} duplicated an id", p.name());
        assert!(s.is_empty());
    }
}

#[test]
fn backlog_tokens_tracks_pushes_and_pops() {
    let mut s = Scheduler::new(Policy::Fifo, 1);
    assert_eq!(s.backlog_tokens(), 0);
    s.push(queued(req(1, 0, Kind::Chat, 800, 100, 0)));
    let after_one = s.backlog_tokens();
    assert!(after_one > 0);
    s.push(queued(req(2, 0, Kind::Batch, 800, 4000, 0)));
    assert!(
        s.backlog_tokens() > after_one,
        "a 4000-token job must add more backlog than a 100-token one"
    );
    s.pop_any();
    s.pop_any();
    assert_eq!(s.backlog_tokens(), 0, "backlog must return to zero when drained");
}

#[test]
fn estimated_cost_weights_prompt_below_output() {
    // Prefill is one pass over the prompt; decode is one pass per token. A
    // cost function that weighted them equally would rank a long-prompt,
    // short-output RAG query as expensive, which is the opposite of true.
    let long_prompt = queued(req(1, 0, Kind::Rag, 4000, 50, 0));
    let long_output = queued(req(2, 0, Kind::Batch, 50, 4000, 0));
    assert!(
        long_output.estimated_cost() > long_prompt.estimated_cost(),
        "output tokens must dominate: {} vs {}",
        long_output.estimated_cost(),
        long_prompt.estimated_cost()
    );
}

#[test]
fn projected_kv_covers_prompt_and_output() {
    let q = queued(req(1, 0, Kind::Rag, 900, 120, 0));
    assert_eq!(q.projected_kv(), 1020);
}

// --------------------------------------------------------------------- FIFO

#[test]
fn fifo_serves_in_arrival_order_regardless_of_size() {
    let mut s = Scheduler::new(Policy::Fifo, 1);
    s.push(queued(req(1, 0, Kind::Batch, 100, 4000, 0)));
    s.push(queued(req(2, 0, Kind::Chat, 100, 20, 1000)));
    s.push(queued(req(3, 0, Kind::Chat, 100, 20, 2000)));
    assert_eq!(drain(&mut s), vec![1, 2, 3]);
}

#[test]
fn fifo_ignores_the_estimate_entirely() {
    // Pinned because section 6 of the report depends on it: FIFO's numbers
    // moving with estimator spread was the signal that the experiment was
    // confounded by the KV reservation channel.
    let mut honest = Scheduler::new(Policy::Fifo, 1);
    let mut lying = Scheduler::new(Policy::Fifo, 1);
    for i in 0..20u64 {
        let r = req(i, 0, Kind::Chat, 200, 100, i * 1000);
        honest.push(queued(r));
        lying.push(Queued::new(
            r,
            Estimate {
                prompt_tokens: 1,
                predicted_output_tokens: (20 - i) as u32 * 500,
                kind: Kind::Chat,
            },
            r.arrival_us,
        ));
    }
    assert_eq!(drain_at(&mut honest, 30_000), drain_at(&mut lying, 30_000));
}

// ---------------------------------------------------------------------- SJF

#[test]
fn sjf_serves_the_cheapest_first() {
    let mut s = Scheduler::new(Policy::Sjf, 1);
    s.push(queued(req(1, 0, Kind::Batch, 100, 4000, 0)));
    s.push(queued(req(2, 0, Kind::Summarise, 100, 900, 0)));
    s.push(queued(req(3, 0, Kind::Chat, 100, 20, 0)));
    assert_eq!(drain(&mut s), vec![3, 2, 1]);
}

#[test]
fn sjf_ranks_on_the_estimate_not_the_truth() {
    // The scheduler is never given the true output length. If this test ever
    // passes with the *true* ordering, the simulator has granted itself an
    // oracle and every scheduling result in the report is inflated.
    let mut s = Scheduler::new(Policy::Sjf, 1);
    let cheap_truth = req(1, 0, Kind::Chat, 100, 10, 0);
    let expensive_truth = req(2, 0, Kind::Chat, 100, 3000, 0);
    // Estimates deliberately inverted.
    s.push(Queued::new(
        cheap_truth,
        Estimate {
            prompt_tokens: 100,
            predicted_output_tokens: 3000,
            kind: Kind::Chat,
        },
        0,
    ));
    s.push(Queued::new(
        expensive_truth,
        Estimate {
            prompt_tokens: 100,
            predicted_output_tokens: 10,
            kind: Kind::Chat,
        },
        0,
    ));
    assert_eq!(
        drain(&mut s),
        vec![2, 1],
        "SJF must follow the estimate even when the estimate is wrong"
    );
}

#[test]
fn sjf_ordering_survives_multiplicative_noise() {
    // Section 6a's mechanism, isolated: SJF needs the ranking to be roughly
    // right, not the magnitudes. Classes here differ by two orders of
    // magnitude, so 3x noise rarely swaps them.
    let mut est = Estimator::new(3.0, 99);
    let mut s = Scheduler::new(Policy::Sjf, 1);
    let mut chat_ids = Vec::new();
    for i in 0..30u64 {
        let r = if i % 2 == 0 {
            chat_ids.push(i);
            req(i, 0, Kind::Chat, 300, 30, 0)
        } else {
            req(i, 0, Kind::Batch, 300, 3000, 0)
        };
        let e = est.estimate(&r);
        s.push(Queued::new(r, e, 0));
    }
    let order = drain(&mut s);
    let chat_positions: Vec<usize> = chat_ids
        .iter()
        .map(|id| order.iter().position(|o| o == id).unwrap())
        .collect();
    let mean = chat_positions.iter().sum::<usize>() as f64 / chat_positions.len() as f64;
    assert!(
        mean < 10.0,
        "chat should still land in the front half under 3x noise, mean position {}",
        mean
    );
}

// ---------------------------------------------------------- class priority

#[test]
fn class_priority_serves_interactive_work_first() {
    let mut s = Scheduler::new(Policy::ClassPriority, 1);
    s.push(queued(req(1, 0, Kind::Batch, 100, 100, 0)));
    s.push(queued(req(2, 0, Kind::Summarise, 100, 100, 0)));
    s.push(queued(req(3, 0, Kind::Chat, 100, 100, 0)));
    let order = drain(&mut s);
    assert_eq!(order[0], 3, "chat must go first, got {:?}", order);
    assert_eq!(*order.last().unwrap(), 1, "batch must go last");
}

// ---------------------------------------------------------------------- DRR

#[test]
fn drr_interleaves_between_tenants() {
    let mut s = Scheduler::new(Policy::DeficitRoundRobin, 2);
    // Tenant 0 arrives first with a long run of work.
    for i in 0..10u64 {
        s.push(queued(req(i, 0, Kind::Chat, 200, 100, 0)));
    }
    for i in 10..20u64 {
        s.push(queued(req(i, 1, Kind::Chat, 200, 100, 0)));
    }
    let order = drain(&mut s);
    // Within the first ten served, tenant 1 must get a meaningful share
    // despite arriving entirely behind tenant 0.
    let t1_in_first_ten = order[..10].iter().filter(|id| **id >= 10).count();
    assert!(
        t1_in_first_ten >= 3,
        "DRR gave the late tenant only {} of the first ten slots: {:?}",
        t1_in_first_ten,
        &order[..10]
    );
}

#[test]
fn drr_charges_by_tokens_not_by_request_count() {
    // Tenant 0 sends many cheap requests; tenant 1 sends few expensive ones
    // totalling similar work. A request-counting scheduler would give tenant 1
    // a small fraction of service; a token-charging one keeps them close.
    let mut s = Scheduler::new(Policy::DeficitRoundRobin, 2);
    for i in 0..30u64 {
        s.push(queued(req(i, 0, Kind::Chat, 100, 50, 0)));
    }
    for i in 30..40u64 {
        s.push(queued(req(i, 1, Kind::Batch, 100, 150, 0)));
    }
    let order = drain(&mut s);
    let first_twenty_t1 = order[..20].iter().filter(|id| **id >= 30).count();
    assert!(
        first_twenty_t1 >= 4,
        "token-charged DRR should give the expensive tenant several early slots, got {}",
        first_twenty_t1
    );
    assert!(
        first_twenty_t1 <= 12,
        "but not let it dominate; got {}",
        first_twenty_t1
    );
}

#[test]
fn drr_with_one_tenant_degenerates_gracefully() {
    let mut s = Scheduler::new(Policy::DeficitRoundRobin, 1);
    for i in 0..15u64 {
        s.push(queued(req(i, 0, Kind::Chat, 100, 50, i * 100)));
    }
    let order = drain(&mut s);
    assert_eq!(order.len(), 15);
}

// -------------------------------------------------------------------- aging

#[test]
fn aged_sjf_promotes_a_request_that_has_waited_past_the_threshold() {
    let mut s = Scheduler::new(Policy::SjfAged, 1);
    // One expensive request that has been waiting a very long time, and a
    // cheap one that just arrived.
    s.push(queued(req(1, 0, Kind::Batch, 100, 4000, 0)));
    s.push(queued(req(2, 0, Kind::Chat, 100, 20, 60_000_000)));
    let first = s
        .pop_admissible(60_000_000, |_| true)
        .expect("something must be admissible");
    assert_eq!(
        first.req.id, 1,
        "a request waiting 60s must be promoted ahead of a cheap newcomer"
    );
}

#[test]
fn aged_sjf_behaves_like_sjf_when_nothing_has_waited() {
    let mut s = Scheduler::new(Policy::SjfAged, 1);
    s.push(queued(req(1, 0, Kind::Batch, 100, 4000, 0)));
    s.push(queued(req(2, 0, Kind::Chat, 100, 20, 0)));
    let first = s.pop_admissible(0, |_| true).unwrap();
    assert_eq!(first.req.id, 2);
}

// ------------------------------------------------------------- admissibility

#[test]
fn pop_admissible_skips_what_does_not_fit() {
    let mut s = Scheduler::new(Policy::Fifo, 1);
    s.push(queued(req(1, 0, Kind::Batch, 8000, 4000, 0)));
    s.push(queued(req(2, 0, Kind::Chat, 50, 20, 0)));
    let got = s
        .pop_admissible(0, |q| q.projected_kv() < 1000)
        .expect("the small request must be reachable");
    assert_eq!(got.req.id, 2);
    assert_eq!(s.len(), 1, "the skipped request must remain queued");
}

#[test]
fn pop_admissible_returns_none_when_nothing_fits() {
    let mut s = Scheduler::new(Policy::Fifo, 1);
    for i in 0..5u64 {
        s.push(queued(req(i, 0, Kind::Batch, 8000, 4000, 0)));
    }
    assert!(s.pop_admissible(0, |_| false).is_none());
    assert_eq!(s.len(), 5, "a failed pop must not consume the queue");
}

#[test]
fn a_backed_off_request_is_not_ready_until_its_deadline() {
    let mut q = queued(req(1, 0, Kind::Chat, 100, 50, 0));
    q.not_before_us = 5_000_000;
    assert!(!q.ready(1_000_000));
    assert!(q.ready(5_000_000));
    assert!(q.ready(9_000_000));
}

#[test]
fn next_ready_us_reports_the_earliest_backoff_deadline() {
    let mut s = Scheduler::new(Policy::Fifo, 1);
    for (i, t) in [9_000_000u64, 3_000_000, 7_000_000].iter().enumerate() {
        let mut q = queued(req(i as u64, 0, Kind::Chat, 100, 50, 0));
        q.not_before_us = *t;
        s.push(q);
    }
    assert_eq!(s.next_ready_us(), Some(3_000_000));
}

#[test]
fn pop_admissible_respects_backoff() {
    let mut s = Scheduler::new(Policy::Fifo, 1);
    let mut q = queued(req(1, 0, Kind::Chat, 100, 50, 0));
    q.not_before_us = 5_000_000;
    s.push(q);
    assert!(
        s.pop_admissible(1_000_000, |_| true).is_none(),
        "a backed-off request must not be served early"
    );
    assert!(s.pop_admissible(5_000_000, |_| true).is_some());
}

// -------------------------------------------------------------- admission

fn accepts(a: Admission, backlog: &Scheduler, candidate: &Queued, _now: u64) -> bool {
    let cfg = gateway::engine::EngineConfig::default();
    let rate = cfg.decode_throughput_tok_per_s(cfg.max_batch);
    gateway::sched::admit(a, candidate, backlog, rate, &cfg)
}

#[test]
fn accept_all_never_refuses() {
    let s = Scheduler::new(Policy::Fifo, 1);
    let q = queued(req(1, 0, Kind::Batch, 8000, 4000, 0));
    assert!(accepts(Admission::AcceptAll, &s, &q, 0));
}

#[test]
fn queue_depth_refuses_past_its_limit() {
    let mut s = Scheduler::new(Policy::Fifo, 1);
    let q = queued(req(999, 0, Kind::Chat, 100, 50, 0));
    assert!(accepts(Admission::QueueDepth(10), &s, &q, 0));
    for i in 0..20u64 {
        s.push(queued(req(i, 0, Kind::Chat, 100, 50, 0)));
    }
    assert!(!accepts(Admission::QueueDepth(10), &s, &q, 0));
}

#[test]
fn queue_tokens_refuses_on_work_not_on_count() {
    let mut s = Scheduler::new(Policy::Fifo, 1);
    let q = queued(req(999, 0, Kind::Chat, 100, 50, 0));
    // Three requests, but an enormous amount of work.
    for i in 0..3u64 {
        s.push(queued(req(i, 0, Kind::Batch, 100, 8000, 0)));
    }
    assert!(
        accepts(Admission::QueueDepth(10), &s, &q, 0),
        "depth-based admission cannot see the work"
    );
    assert!(
        !accepts(Admission::QueueTokens(5_000), &s, &q, 0),
        "token-based admission can"
    );
}

#[test]
fn cost_aware_accepts_everything_below_its_threshold() {
    let mut s = Scheduler::new(Policy::Fifo, 1);
    s.push(queued(req(0, 0, Kind::Chat, 100, 50, 0)));
    for kind in Kind::ALL {
        let q = queued(req(999, 0, kind, 100, 50, 0));
        assert!(
            accepts(Admission::CostAware(100_000), &s, &q, 0),
            "{} refused below threshold",
            kind.name()
        );
    }
}

#[test]
fn cost_aware_sheds_the_expensive_latency_tolerant_end_first() {
    // The finding in section 9b: refusing one batch job returns as much
    // capacity as refusing twenty chat turns, so a policy that sheds by
    // arrival time rather than by value density throws away the wrong work.
    let mut s = Scheduler::new(Policy::Fifo, 1);
    for i in 0..60u64 {
        s.push(queued(req(i, 0, Kind::Batch, 400, 3000, 0)));
    }
    let chat = queued(req(900, 0, Kind::Chat, 100, 30, 0));
    let batch = queued(req(901, 0, Kind::Batch, 400, 3000, 0));
    let policy = Admission::CostAware(2_000);
    assert!(
        !accepts(policy, &s, &batch, 0),
        "an expensive, latency-tolerant request must be shed under pressure"
    );
    assert!(
        accepts(policy, &s, &chat, 0),
        "a cheap, urgent request must be kept under the same pressure"
    );
}

#[test]
fn cost_aware_raises_its_bar_as_pressure_grows() {
    let mut s = Scheduler::new(Policy::Fifo, 1);
    let rag = queued(req(900, 0, Kind::Rag, 900, 120, 0));
    let policy = Admission::CostAware(2_000);
    let mut refused_at = None;
    for i in 0..400u64 {
        s.push(queued(req(i, 0, Kind::Batch, 400, 3000, 0)));
        if refused_at.is_none() && !accepts(policy, &s, &rag, 0) {
            refused_at = Some(i);
        }
    }
    assert!(
        refused_at.is_some(),
        "a graduated policy must eventually refuse mid-value work"
    );
    assert!(
        refused_at.unwrap() > 0,
        "but not immediately -- a cliff turns a small burst into a mass rejection"
    );
}

#[test]
fn slo_predictive_sheds_on_projected_wait() {
    let mut s = Scheduler::new(Policy::Fifo, 1);
    let chat = queued(req(900, 0, Kind::Chat, 100, 30, 0));
    assert!(accepts(Admission::SloPredictive, &s, &chat, 0));
    for i in 0..300u64 {
        s.push(queued(req(i, 0, Kind::Batch, 400, 3000, 0)));
    }
    assert!(
        !accepts(Admission::SloPredictive, &s, &chat, 0),
        "a request whose projected wait exceeds its objective must be refused"
    );
}

#[test]
fn slo_predictive_sheds_the_tightest_deadlines_first() {
    // Why the 'obvious' policy is the worst one measured: as the queue grows
    // it refuses interactive traffic *before* bulk traffic, because
    // interactive traffic is what has a deadline to breach. Rather than pick a
    // backlog by hand, grow one and record where each class is first refused.
    let mut s = Scheduler::new(Policy::Fifo, 1);
    let chat = queued(req(900, 0, Kind::Chat, 100, 30, 0));
    let batch = queued(req(901, 0, Kind::Batch, 400, 3000, 0));
    let mut chat_refused_at = None;
    let mut batch_refused_at = None;
    for i in 0..400u64 {
        s.push(queued(req(i, 0, Kind::Batch, 400, 3000, 0)));
        if chat_refused_at.is_none() && !accepts(Admission::SloPredictive, &s, &chat, 0) {
            chat_refused_at = Some(i);
        }
        if batch_refused_at.is_none() && !accepts(Admission::SloPredictive, &s, &batch, 0) {
            batch_refused_at = Some(i);
        }
    }
    let chat_at = chat_refused_at.expect("chat must eventually be refused");
    let batch_at = batch_refused_at.expect("batch must eventually be refused");
    assert!(
        chat_at < batch_at,
        "SLO-predictive sheds chat at backlog {} and batch at {} -- the cheap urgent \
         request is dropped first, which is precisely backwards and the reason \
         cost-aware shedding beats it",
        chat_at,
        batch_at
    );
}

#[test]
fn every_admission_policy_has_a_name() {
    for a in [
        Admission::AcceptAll,
        Admission::QueueDepth(1),
        Admission::QueueTokens(1),
        Admission::SloPredictive,
        Admission::CostAware(1),
    ] {
        assert!(!a.name().is_empty());
    }
}

#[test]
fn every_policy_has_a_distinct_name() {
    let mut names: Vec<&str> = Policy::ALL.iter().map(|p| p.name()).collect();
    let count = names.len();
    names.sort_unstable();
    names.dedup();
    assert_eq!(names.len(), count);
}
