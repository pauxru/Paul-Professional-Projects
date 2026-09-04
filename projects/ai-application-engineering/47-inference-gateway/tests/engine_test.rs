//! Tests for the engine model: batching physics, KV accounting, admission
//! headroom, preemption.
//!
//! Two of these correspond directly to bugs that were in the simulator and
//! produced believable output while wrong: `charge_is_max_not_sum` (KV was
//! double-counted, so the batch was capped at roughly a quarter of its real
//! size) and `admission_reserves_headroom_for_the_existing_batch` (the
//! gateway admitted into memory that its own decode step was about to
//! consume, producing an eviction livelock).

use gateway::engine::{EngineConfig, Replica, Running};
use gateway::workload::{Kind, Request};

fn req(id: u64, kind: Kind, prompt: u32, output: u32) -> Request {
    Request {
        id,
        tenant: 0,
        kind,
        arrival_us: 0,
        prompt_tokens: prompt,
        output_tokens: output,
    }
}

fn cfg() -> EngineConfig {
    EngineConfig::default()
}

// ------------------------------------------------------- batching physics

#[test]
fn decode_step_grows_sublinearly_in_batch() {
    let c = cfg();
    let one = c.decode_step_us(1);
    let sixty_four = c.decode_step_us(64);
    assert!(sixty_four > one, "step time must grow with batch");
    assert!(
        sixty_four < one * 64,
        "step time must grow far slower than batch: {} vs {}",
        sixty_four,
        one * 64
    );
}

#[test]
fn decode_throughput_is_monotone_increasing_in_batch() {
    let c = cfg();
    let mut last = 0.0;
    for b in 1..=c.max_batch {
        let t = c.decode_throughput_tok_per_s(b);
        assert!(t > last, "throughput fell at batch {}: {} <= {}", b, t, last);
        last = t;
    }
}

#[test]
fn marginal_throughput_gain_diminishes() {
    let c = cfg();
    let early = c.decode_throughput_tok_per_s(8) - c.decode_throughput_tok_per_s(7);
    let late = c.decode_throughput_tok_per_s(56) - c.decode_throughput_tok_per_s(55);
    assert!(
        early > late,
        "the marginal sequence must buy less as the batch grows: {} vs {}",
        early,
        late
    );
}

#[test]
fn batch_of_zero_costs_nothing() {
    assert_eq!(cfg().decode_step_us(0), 0);
}

#[test]
fn prefill_is_linear_in_prompt_tokens() {
    let c = cfg();
    let a = c.prefill_us(1000);
    let b = c.prefill_us(2000);
    let ratio = b as f64 / a as f64;
    assert!(
        (ratio - 2.0).abs() < 0.05,
        "prefill should be linear in tokens, ratio was {}",
        ratio
    );
}

#[test]
fn prefill_of_an_empty_prompt_is_free() {
    assert_eq!(cfg().prefill_us(0), 0);
}

#[test]
fn knee_batch_is_within_the_configured_maximum() {
    let c = cfg();
    let k = c.knee_batch(0.02);
    assert!(k >= 1 && k <= c.max_batch, "knee at {}", k);
}

#[test]
fn a_tighter_epsilon_moves_the_knee_later() {
    let c = cfg();
    assert!(c.knee_batch(0.005) >= c.knee_batch(0.05));
}

// ---------------------------------------------------------- KV accounting

#[test]
fn charge_is_max_not_sum() {
    // The bug this pins: charging `kv_held + reserved_kv` double-counts, so a
    // sequence that has reserved 1000 tokens and grown into 300 of them was
    // billed 1300. Batch size collapsed and the cause was invisible, because
    // every individual number looked reasonable.
    let c = cfg();
    let r = Running::new(req(1, Kind::Chat, 500, 200), 0, 1000, &c);
    assert_eq!(
        r.charge(),
        1000,
        "a sequence inside its reservation is charged the reservation"
    );
}

#[test]
fn charge_follows_actual_use_once_the_reservation_is_exceeded() {
    let c = cfg();
    let mut r = Running::new(req(1, Kind::Chat, 500, 200), 0, 400, &c);
    // Under-reserved: 500 prompt tokens already exceed the 400 requested, and
    // `Running::new` raises the reservation to cover what is actually held.
    assert_eq!(r.charge(), 500);
    r.prefill_remaining_us = 0;
    let before = r.charge();
    for _ in 0..50 {
        r.emit_token();
    }
    assert_eq!(
        r.charge(),
        before + 50,
        "charge must grow one-for-one with tokens emitted past the reservation"
    );
}

#[test]
fn charge_never_falls_below_the_reservation() {
    let c = cfg();
    let mut r = Running::new(req(1, Kind::Chat, 10, 400), 0, 5000, &c);
    r.prefill_remaining_us = 0;
    for _ in 0..100 {
        r.emit_token();
        assert!(r.charge() >= 5000, "charge dipped to {}", r.charge());
    }
}

#[test]
fn a_sequence_inside_its_reservation_does_not_need_fresh_memory() {
    let c = cfg();
    let r = Running::new(req(1, Kind::Chat, 100, 200), 0, 4000, &c);
    assert!(
        !r.grows_next_token(),
        "a sequence with slack must not claim new memory for its next token"
    );
}

#[test]
fn a_sequence_past_its_reservation_needs_fresh_memory() {
    let c = cfg();
    let mut r = Running::new(req(1, Kind::Chat, 100, 400), 0, 101, &c);
    r.prefill_remaining_us = 0;
    for _ in 0..10 {
        r.emit_token();
    }
    assert!(
        r.grows_next_token(),
        "a sequence past its reservation must claim new memory"
    );
}

#[test]
fn emit_token_is_rejected_during_prefill() {
    // Reachable only because `emit_token` is public for these tests, but the
    // failure mode is nasty enough to pin: it spins `kv_held` upward with no
    // termination condition, and the eventual panic is an integer overflow
    // hundreds of thousands of iterations from the actual mistake.
    let c = cfg();
    let mut r = Running::new(req(1, Kind::Chat, 500, 200), 0, 700, &c);
    assert!(r.prefilling());
    let panicked = std::panic::catch_unwind(std::panic::AssertUnwindSafe(move || {
        r.emit_token();
    }))
    .is_err();
    assert!(
        panicked || !cfg!(debug_assertions),
        "a debug build must refuse to emit during prefill"
    );
}

#[test]
fn emit_token_is_rejected_after_completion() {
    let c = cfg();
    let mut r = Running::new(req(1, Kind::Chat, 100, 2), 0, 200, &c);
    r.prefill_remaining_us = 0;
    r.emit_token();
    r.emit_token();
    assert!(r.done());
    let panicked = std::panic::catch_unwind(std::panic::AssertUnwindSafe(move || {
        r.emit_token();
    }))
    .is_err();
    assert!(
        panicked || !cfg!(debug_assertions),
        "a debug build must refuse to emit past the requested output length"
    );
}

// -------------------------------------------------------------- admission

#[test]
fn an_empty_replica_admits_anything_that_fits() {
    let c = cfg();
    let r = Replica::new(c);
    assert!(r.can_admit(100));
    assert!(!r.can_admit(c.kv_capacity_tokens + 1));
}

#[test]
fn admission_reserves_headroom_for_the_existing_batch() {
    // The livelock bug. `can_admit` must account for the memory every
    // already-decoding sequence will claim on its *next* step, not just for
    // the newcomer. Without this the gateway admits into memory its own batch
    // is about to consume, evicts to make room, and repeats -- 19,997,889
    // times in the run that first exposed it, while reporting a plausible
    // 97.6% SLO attainment.
    let mut c = cfg();
    c.kv_capacity_tokens = 40_000;
    c.max_batch = 64;
    let mut r = Replica::new(c);
    // Reserve exactly the prompt length, so every sequence exhausts its
    // reservation on its very first generated token.
    for i in 0..20u64 {
        assert!(r.can_admit(200));
        r.admit(req(i, Kind::Chat, 200, 4000), 0, 200);
    }
    // Drive the batch out of prefill and into decode.
    let mut now = 0u64;
    for _ in 0..64 {
        let (elapsed, _) = r.step(now, true);
        now += elapsed;
    }
    let growth = r.needs_fresh_kv();
    assert_eq!(
        growth,
        r.batch() as u32,
        "every decoding sequence with no slack wants exactly one fresh token"
    );
    let free = r.kv_free();
    assert!(
        !r.can_admit(free),
        "admitting all {} free tokens while {} sequences each need one more is the livelock",
        free,
        growth
    );
    assert!(
        r.can_admit(free.saturating_sub(growth)),
        "admission must still accept a request that fits alongside the headroom"
    );
}

#[test]
fn admission_respects_the_batch_limit() {
    let mut c = cfg();
    c.max_batch = 4;
    c.kv_capacity_tokens = 1_000_000;
    let mut r = Replica::new(c);
    for i in 0..10u64 {
        if r.can_admit(100) {
            r.admit(req(i, Kind::Chat, 100, 50), 0, 100);
        }
    }
    assert_eq!(r.batch(), 4, "batch limit must bind before memory does");
}

#[test]
fn kv_free_is_never_negative_and_never_exceeds_capacity() {
    let c = cfg();
    let mut r = Replica::new(c);
    assert_eq!(r.kv_free(), c.kv_capacity_tokens);
    for i in 0..30u64 {
        if r.can_admit(300) {
            r.admit(req(i, Kind::Chat, 300, 200), 0, 300);
        }
        assert!(r.kv_free() <= c.kv_capacity_tokens);
    }
    for _ in 0..200 {
        r.step(0, true);
        assert!(r.kv_free() <= c.kv_capacity_tokens);
    }
}

// ------------------------------------------------------------- preemption

#[test]
fn evicting_from_an_empty_replica_returns_none() {
    let mut r = Replica::new(cfg());
    assert!(r.evict_one().is_none());
}

#[test]
fn eviction_frees_memory_and_shrinks_the_batch() {
    let c = cfg();
    let mut r = Replica::new(c);
    for i in 0..8u64 {
        r.admit(req(i, Kind::Chat, 400, 200), 0, 600);
    }
    let batch_before = r.batch();
    let free_before = r.kv_free();
    let victim = r.evict_one();
    assert!(victim.is_some());
    assert_eq!(r.batch(), batch_before - 1);
    assert!(
        r.kv_free() > free_before,
        "eviction must actually return memory"
    );
}

#[test]
fn repeated_eviction_empties_the_replica_and_then_stops() {
    let mut r = Replica::new(cfg());
    for i in 0..10u64 {
        r.admit(req(i, Kind::Chat, 200, 100), 0, 300);
    }
    let mut evicted = 0;
    while r.evict_one().is_some() {
        evicted += 1;
        assert!(evicted <= 10, "evicted more than were admitted");
    }
    assert_eq!(evicted, 10);
    assert_eq!(r.batch(), 0);
    assert_eq!(r.kv_free(), cfg().kv_capacity_tokens);
}

// ------------------------------------------------------------- completion

#[test]
fn a_request_completes_after_exactly_its_output_tokens() {
    let c = cfg();
    let mut r = Replica::new(c);
    r.admit(req(1, Kind::Chat, 100, 30), 0, 200);
    let mut now = 0u64;
    let mut done = Vec::new();
    for _ in 0..500 {
        let (elapsed, finished) = r.step(now, true);
        now += elapsed;
        done.extend(finished);
        if !done.is_empty() {
            break;
        }
    }
    assert_eq!(done.len(), 1);
    assert_eq!(done[0].req.output_tokens, 30);
    assert!(done[0].ttft_us() > 0);
    assert!(done[0].latency_us() >= done[0].ttft_us());
}

#[test]
fn a_single_token_request_still_reports_a_tpot() {
    let c = cfg();
    let mut r = Replica::new(c);
    r.admit(req(1, Kind::Chat, 50, 1), 0, 100);
    let mut now = 0u64;
    loop {
        let (elapsed, finished) = r.step(now, true);
        now += elapsed;
        if let Some(done) = finished.into_iter().next() {
            // No inter-token gap exists; the value must be defined, not a
            // division by zero.
            assert!(done.tpot_us() < u64::MAX);
            return;
        }
        assert!(now < 100_000_000, "never completed");
    }
}

#[test]
fn slowdown_of_a_solo_request_is_close_to_one() {
    let c = cfg();
    let mut r = Replica::new(c);
    r.admit(req(1, Kind::Chat, 100, 40), 0, 200);
    let mut now = 0u64;
    loop {
        let (elapsed, finished) = r.step(now, true);
        now += elapsed;
        if let Some(done) = finished.into_iter().next() {
            let s = done.slowdown(&c);
            assert!(
                (0.95..1.6).contains(&s),
                "a request alone on the device should have slowdown near 1, got {}",
                s
            );
            return;
        }
    }
}

#[test]
fn ideal_time_ignores_contention() {
    let c = cfg();
    let mut r = Replica::new(c);
    for i in 0..40u64 {
        r.admit(req(i, Kind::Chat, 100, 40), 0, 200);
    }
    let mut now = 0u64;
    loop {
        let (elapsed, finished) = r.step(now, true);
        now += elapsed;
        if let Some(done) = finished.into_iter().next() {
            let solo_step = c.decode_step_us(1);
            let ideal = done.ideal_us(&c);
            assert!(
                ideal <= c.prefill_us(100) + solo_step * 40 + 1,
                "ideal time must be computed at batch 1"
            );
            assert!(
                done.slowdown(&c) > 1.0,
                "40 concurrent sequences must produce slowdown above 1"
            );
            return;
        }
    }
}

#[test]
fn slo_is_evaluated_against_the_requests_own_class() {
    let chat = Kind::Chat.ttft_slo_us();
    let batch = Kind::Batch.ttft_slo_us();
    assert!(
        chat < batch,
        "an interactive class must have a tighter objective than a bulk one"
    );
    assert!(Kind::Chat.interactive());
    assert!(!Kind::Batch.interactive());
}

#[test]
fn every_kind_has_a_positive_objective() {
    for k in Kind::ALL {
        assert!(k.ttft_slo_us() > 0, "{} has no TTFT objective", k.name());
        assert!(k.tpot_slo_us() > 0, "{} has no TPOT objective", k.name());
        assert!(!k.name().is_empty());
    }
}

#[test]
fn peak_kv_is_prompt_plus_output() {
    let r = req(1, Kind::Rag, 900, 120);
    assert_eq!(r.peak_kv_tokens(), 1020);
}
