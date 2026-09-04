//! Statistical properties of the generator, the capacity measurement, and the
//! report writer.
//!
//! The distribution tests are deliberately loose on tolerance and strict on
//! shape. A test that pins the mean of a lognormal to three decimal places
//! fails whenever the seed changes and teaches nothing; a test that asserts
//! the median is where it was asked to be, and that the tail is heavier than
//! the head, catches the mistakes that actually happen.

use gateway::capacity::{naive_rps, saturation_rps, saturation_tokens_per_s, trace_at_utilisation};
use gateway::report::{f1, f2, pct, Report};
use gateway::rng::Rng;
use gateway::sim::{run_requests, GatewayConfig};
use gateway::workload::{Estimator, Kind, Workload};

// ------------------------------------------------------------------- RNG

#[test]
fn the_same_seed_gives_the_same_stream() {
    let mut a = Rng::new(12345);
    let mut b = Rng::new(12345);
    for _ in 0..1000 {
        assert_eq!(a.next_u64(), b.next_u64());
    }
}

#[test]
fn different_seeds_give_different_streams() {
    let mut a = Rng::new(1);
    let mut b = Rng::new(2);
    let differ = (0..100).filter(|_| a.next_u64() != b.next_u64()).count();
    assert!(differ > 95, "streams agreed {} times in 100", 100 - differ);
}

#[test]
fn unit_is_strictly_inside_zero_and_one() {
    // Strictly: `exponential` takes a logarithm of this value, so a zero here
    // is an infinity three call frames away.
    let mut r = Rng::new(9);
    for _ in 0..200_000 {
        let u = r.unit();
        assert!(u > 0.0 && u < 1.0, "unit() produced {}", u);
    }
}

#[test]
fn unit_is_roughly_uniform() {
    let mut r = Rng::new(4);
    let mut buckets = [0usize; 10];
    for _ in 0..100_000 {
        buckets[(r.unit() * 10.0) as usize % 10] += 1;
    }
    for (i, b) in buckets.iter().enumerate() {
        assert!(
            (8_000..12_000).contains(b),
            "bucket {} held {} of 100000",
            i,
            b
        );
    }
}

#[test]
fn exponential_has_the_requested_mean() {
    let mut r = Rng::new(77);
    let n = 200_000;
    let sum: f64 = (0..n).map(|_| r.exponential(4.0)).sum();
    let mean = sum / n as f64;
    assert!(
        (mean - 0.25).abs() < 0.01,
        "mean of Exp(4) was {}, expected 0.25",
        mean
    );
}

#[test]
fn exponential_is_always_positive() {
    let mut r = Rng::new(3);
    for _ in 0..100_000 {
        assert!(r.exponential(2.0) > 0.0);
    }
}

#[test]
fn exponential_is_memoryless_enough_to_be_exponential() {
    // P(X > 2m) should be about P(X > m)^2.
    let mut r = Rng::new(21);
    let n = 200_000;
    let mut past_one = 0;
    let mut past_two = 0;
    for _ in 0..n {
        let x = r.exponential(1.0);
        if x > 1.0 {
            past_one += 1;
        }
        if x > 2.0 {
            past_two += 1;
        }
    }
    let p1 = past_one as f64 / n as f64;
    let p2 = past_two as f64 / n as f64;
    assert!(
        (p2 - p1 * p1).abs() < 0.01,
        "P(X>2) = {} but P(X>1)^2 = {}",
        p2,
        p1 * p1
    );
}

#[test]
fn lognormal_is_centred_on_its_median() {
    let mut r = Rng::new(31);
    let n = 100_001;
    let mut v: Vec<f64> = (0..n).map(|_| r.lognormal(200.0, 2.0)).collect();
    v.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let median = v[n / 2];
    assert!(
        (median - 200.0).abs() < 6.0,
        "median was {}, expected 200",
        median
    );
}

#[test]
fn lognormal_is_right_skewed() {
    let mut r = Rng::new(32);
    let n = 100_001;
    let mut v: Vec<f64> = (0..n).map(|_| r.lognormal(200.0, 2.0)).collect();
    v.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let median = v[n / 2];
    let mean: f64 = v.iter().sum::<f64>() / n as f64;
    assert!(
        mean > median * 1.1,
        "mean {} should exceed median {} for a lognormal",
        mean,
        median
    );
    assert!(v[n - 1] > median * 5.0, "the tail is not heavy enough");
}

#[test]
fn a_wider_spread_produces_a_wider_lognormal() {
    let narrow = {
        let mut r = Rng::new(5);
        let mut v: Vec<f64> = (0..20_001).map(|_| r.lognormal(100.0, 1.2)).collect();
        v.sort_by(|a, b| a.partial_cmp(b).unwrap());
        v[19_000] / v[1_000]
    };
    let wide = {
        let mut r = Rng::new(5);
        let mut v: Vec<f64> = (0..20_001).map(|_| r.lognormal(100.0, 4.0)).collect();
        v.sort_by(|a, b| a.partial_cmp(b).unwrap());
        v[19_000] / v[1_000]
    };
    assert!(wide > narrow * 3.0, "{} vs {}", wide, narrow);
}

#[test]
fn lognormal_is_always_positive() {
    let mut r = Rng::new(6);
    for _ in 0..50_000 {
        assert!(r.lognormal(50.0, 3.0) > 0.0);
    }
}

#[test]
fn range_stays_inside_its_bounds_and_covers_them() {
    let mut r = Rng::new(8);
    let mut seen_low = false;
    let mut seen_high = false;
    for _ in 0..10_000 {
        let v = r.range(5, 9);
        assert!((5..=9).contains(&v), "range produced {}", v);
        seen_low |= v == 5;
        seen_high |= v == 9;
    }
    assert!(seen_low && seen_high, "range never reached its endpoints");
}

#[test]
fn chance_matches_its_probability() {
    let mut r = Rng::new(13);
    let hits = (0..100_000).filter(|_| r.chance(0.3)).count();
    let p = hits as f64 / 100_000.0;
    assert!((p - 0.3).abs() < 0.01, "chance(0.3) fired {} of the time", p);
}

#[test]
fn chance_zero_never_fires_and_chance_one_always_does() {
    let mut r = Rng::new(14);
    for _ in 0..10_000 {
        assert!(!r.chance(0.0));
        assert!(r.chance(1.0));
    }
}

// -------------------------------------------------------------- estimator

#[test]
fn the_oracle_estimator_is_exact() {
    let mut e = Estimator::oracle();
    for req in Workload::new(3.0, 5).generate(500) {
        let est = e.estimate(&req);
        assert_eq!(est.predicted_output_tokens, req.output_tokens);
        assert_eq!(est.prompt_tokens, req.prompt_tokens);
    }
}

#[test]
fn the_estimator_never_lies_about_the_prompt() {
    // Prompt length is the one thing a gateway genuinely knows: it is holding
    // the tokens. Only the output length is predicted.
    let mut e = Estimator::new(8.0, 1);
    for req in Workload::new(3.0, 5).generate(500) {
        assert_eq!(e.estimate(&req).prompt_tokens, req.prompt_tokens);
    }
}

#[test]
fn a_wider_spread_produces_worse_estimates() {
    let error = |spread: f64| {
        let mut e = Estimator::new(spread, 3);
        let reqs = Workload::new(3.0, 5).generate(2000);
        let mut total = 0.0;
        for r in &reqs {
            let est = e.estimate(r).predicted_output_tokens as f64;
            let truth = r.output_tokens as f64;
            total += (est / truth).max(truth / est).ln().abs();
        }
        total / reqs.len() as f64
    };
    let tight = error(1.2);
    let loose = error(8.0);
    assert!(
        loose > tight * 1.5,
        "spread 8 produced log-error {:.3} against {:.3} at spread 1.2",
        loose,
        tight
    );
}

#[test]
fn estimates_are_never_zero() {
    // A zero-cost estimate would make SJF serve a request infinitely early and
    // make the KV reservation zero, which is a division by zero waiting to
    // happen in two places.
    let mut e = Estimator::new(20.0, 7);
    for req in Workload::new(3.0, 9).generate(3000) {
        let est = e.estimate(&req);
        assert!(est.predicted_output_tokens > 0, "zero output estimate");
        assert!(est.prompt_tokens > 0, "zero prompt estimate");
    }
}

// --------------------------------------------------------------- workload

#[test]
fn arrival_rate_matches_the_request() {
    for rate in [1.0, 5.0, 20.0] {
        let reqs = Workload::new(rate, 17).generate(20_000);
        let span_s = (reqs.last().unwrap().arrival_us - reqs[0].arrival_us) as f64 / 1e6;
        let measured = (reqs.len() - 1) as f64 / span_s;
        assert!(
            (measured / rate - 1.0).abs() < 0.05,
            "asked for {} req/s, generated {:.2}",
            rate,
            measured
        );
    }
}

#[test]
fn classes_differ_by_orders_of_magnitude_in_cost() {
    // The whole reason scheduling matters on this workload.
    let reqs = Workload::new(3.0, 23).generate(20_000);
    let median_output = |k: Kind| {
        let mut v: Vec<u32> = reqs
            .iter()
            .filter(|r| r.kind == k)
            .map(|r| r.output_tokens)
            .collect();
        v.sort_unstable();
        v[v.len() / 2] as f64
    };
    let chat = median_output(Kind::Chat);
    let batch = median_output(Kind::Batch);
    assert!(
        batch > chat * 10.0,
        "batch median {} against chat median {} -- not heterogeneous enough for \
         head-of-line blocking to be interesting",
        batch,
        chat
    );
}

#[test]
fn tenants_have_genuinely_different_mixes() {
    let reqs = Workload::new(3.0, 29).generate(20_000);
    let interactive_share = |tenant: u8| {
        let of_tenant: Vec<_> = reqs.iter().filter(|r| r.tenant == tenant).collect();
        of_tenant.iter().filter(|r| r.kind.interactive()).count() as f64 / of_tenant.len() as f64
    };
    let a = interactive_share(0);
    let b = interactive_share(1);
    assert!(
        a > b + 0.3,
        "tenant mixes are too similar for fairness to be measurable: {:.2} vs {:.2}",
        a,
        b
    );
}

#[test]
fn every_request_has_positive_work() {
    for r in Workload::new(3.0, 41).generate(20_000) {
        assert!(r.prompt_tokens > 0, "request {} has an empty prompt", r.id);
        assert!(r.output_tokens > 0, "request {} generates nothing", r.id);
    }
}

#[test]
fn request_ids_are_unique_and_dense() {
    let reqs = Workload::new(3.0, 43).generate(5_000);
    for (i, r) in reqs.iter().enumerate() {
        assert_eq!(r.id, i as u64);
    }
}

// --------------------------------------------------------------- capacity

#[test]
fn measured_capacity_is_below_the_naive_estimate() {
    // Section 3's finding as an invariant. The naive estimate assumes the
    // device spends every microsecond decoding at full batch; it never does,
    // because prefill blocks decode and KV limits the batch.
    let w = Workload::new(3.0, 7);
    let cfg = GatewayConfig::default();
    let measured = saturation_rps(&cfg, &w, 400);
    let naive = naive_rps(&cfg, &w, 400);
    assert!(
        naive > measured,
        "the naive estimate {:.2} did not exceed the measured {:.2}",
        naive,
        measured
    );
    assert!(
        naive < measured * 2.0,
        "a 2x gap means the naive estimate is not the estimate anyone would make"
    );
}

#[test]
fn saturation_throughput_is_stable_across_sample_sizes() {
    // If this drifts, every rho in the report is measured against a moving
    // denominator.
    let w = Workload::new(3.0, 7);
    let cfg = GatewayConfig::default();
    let a = saturation_rps(&cfg, &w, 300);
    let b = saturation_rps(&cfg, &w, 900);
    assert!(
        (a / b - 1.0).abs() < 0.15,
        "capacity measured {:.2} at n=300 and {:.2} at n=900",
        a,
        b
    );
}

#[test]
fn saturation_token_throughput_is_positive_and_finite() {
    let t = saturation_tokens_per_s(&GatewayConfig::default(), &Workload::new(3.0, 7), 300);
    assert!(t > 0.0 && t.is_finite(), "{}", t);
}

#[test]
fn a_trace_at_a_given_utilisation_actually_offers_that_load() {
    let w = Workload::new(3.0, 7);
    let cfg = GatewayConfig::default();
    let mu = saturation_rps(&cfg, &w, 300);
    for rho in [0.5, 0.85] {
        let trace = trace_at_utilisation(&w, mu, rho, 4000, 7);
        let span_s = (trace.last().unwrap().arrival_us - trace[0].arrival_us) as f64 / 1e6;
        let offered = (trace.len() - 1) as f64 / span_s;
        assert!(
            (offered / (mu * rho) - 1.0).abs() < 0.06,
            "rho {} asked for {:.2} req/s and offered {:.2}",
            rho,
            mu * rho,
            offered
        );
    }
}

#[test]
fn a_trace_below_saturation_keeps_up() {
    // 'Keeps up' means the queue does not grow without bound, which is a
    // statement about waiting rather than about throughput. Throughput
    // measured over the whole run is diluted by the drain tail after the last
    // arrival -- at n=600 that tail is several percent of the run, so
    // comparing it to the offered rate measures the tail, not the queue.
    let w = Workload::new(3.0, 7);
    let cfg = GatewayConfig::default();
    let mu = saturation_rps(&cfg, &w, 300);
    let trace = trace_at_utilisation(&w, mu, 0.5, 600, 7);
    let s = run_requests(&cfg, &trace);
    assert_eq!(s.completed.len(), trace.len(), "requests were left unserved");

    // The queue delay of the last decile must not exceed that of the first:
    // a system falling behind accumulates delay monotonically.
    let tenth = trace.len() / 10;
    let mean_queue = |slice: &[gateway::engine::Completed]| {
        slice.iter().map(|c| c.queue_us() as f64).sum::<f64>() / slice.len() as f64
    };
    let mut by_arrival = s.completed.clone();
    by_arrival.sort_by_key(|c| c.req.arrival_us);
    let early = mean_queue(&by_arrival[..tenth]);
    let late = mean_queue(&by_arrival[by_arrival.len() - tenth..]);
    assert!(
        late < early.max(1.0) * 5.0 + 1_000_000.0,
        "queue delay grew from {:.0}us early to {:.0}us late -- the system is \
         falling behind at half its measured capacity",
        early,
        late
    );
}

// ----------------------------------------------------------------- report

#[test]
fn a_report_renders_its_sections_in_order() {
    let mut r = Report::new("T", "intro", "gen", "env");
    r.h2("First");
    r.para("alpha");
    r.h2("Second");
    r.para("beta");
    let out = r.render();
    assert!(out.find("First").unwrap() < out.find("Second").unwrap());
    assert!(out.find("alpha").unwrap() < out.find("beta").unwrap());
    assert!(out.starts_with("# T"));
}

#[test]
fn a_report_counts_held_and_contradicted_predictions() {
    let mut r = Report::new("T", "i", "g", "e");
    r.expect("a");
    r.found("yes", true);
    r.expect("b");
    r.found("no", false);
    r.expect("c");
    r.found("no", false);
    let out = r.render();
    assert!(out.contains("held: 1"), "{}", out);
    assert!(out.contains("contradicted: 2"), "{}", out);
}

#[test]
#[should_panic(expected = "open prediction")]
fn a_report_refuses_to_render_an_unanswered_prediction() {
    // The property that makes the prediction discipline real rather than
    // decorative: you cannot register a question and quietly not answer it.
    let mut r = Report::new("T", "i", "g", "e");
    r.expect("something");
    let _ = r.render();
}

#[test]
#[should_panic]
fn a_report_refuses_a_finding_with_no_prediction() {
    let mut r = Report::new("T", "i", "g", "e");
    r.found("an answer to nothing", true);
}

#[test]
fn a_table_renders_a_markdown_header_rule() {
    let mut r = Report::new("T", "i", "g", "e");
    r.table(
        &["a", "b"],
        vec![vec!["1".into(), "2".into()], vec!["3".into(), "4".into()]],
    );
    let out = r.render();
    assert!(out.contains("| a | b |"));
    assert!(out.contains("|---|---|"));
    assert!(out.contains("| 1 | 2 |"));
}

#[test]
fn the_digest_changes_when_the_content_changes() {
    let build = |para: &str| {
        let mut r = Report::new("T", "i", "g", "e");
        r.para(para);
        r.digest()
    };
    assert_eq!(build("same"), build("same"));
    assert_ne!(build("one"), build("two"));
}

#[test]
fn the_digest_is_stable_across_renders() {
    let mut r = Report::new("T", "i", "g", "e");
    r.para("content");
    let a = r.digest();
    let _ = r.render();
    assert_eq!(a, r.digest());
}

#[test]
fn number_helpers_format_predictably() {
    assert_eq!(f1(1.24159), "1.2");
    assert_eq!(f2(1.24159), "1.24");
    assert_eq!(pct(0.9137), "91.4%");
    assert_eq!(pct(0.0), "0.0%");
    assert_eq!(pct(1.0), "100.0%");
}
