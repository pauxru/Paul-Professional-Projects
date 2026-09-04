//! Tests for the measurement layer.
//!
//! The percentile estimator gets more attention here than it looks like it
//! deserves. Every headline in the report is a tail number, so a percentile
//! that is off by one index is a report that is quietly wrong -- and unlike a
//! crash, it produces plausible output. These tests pin the exact definition
//! (nearest-rank on the sorted sample) rather than asserting a range.

use gateway::metrics::{fmt_us, mm1_sojourn_s, Latencies, Little};

// ---------------------------------------------------------------- percentiles

#[test]
fn empty_sample_has_no_percentiles() {
    let l = Latencies::from(Vec::<u64>::new());
    assert!(l.is_empty());
    assert_eq!(l.len(), 0);
    assert_eq!(l.p50(), 0);
    assert_eq!(l.p99(), 0);
    assert_eq!(l.max(), 0);
    assert_eq!(l.mean(), 0.0);
}

#[test]
fn single_value_is_every_percentile() {
    let l = Latencies::from(vec![42]);
    for q in [0.0, 0.01, 0.5, 0.9, 0.99, 1.0] {
        assert_eq!(l.quantile(q), 42, "q={}", q);
    }
    assert_eq!(l.mean(), 42.0);
}

#[test]
fn percentiles_are_exact_not_interpolated() {
    // 1..=100. Nearest-rank p50 is the 50th value; p99 the 99th.
    let l = Latencies::from(1u64..=100);
    assert_eq!(l.p50(), 50);
    assert_eq!(l.p95(), 95);
    assert_eq!(l.p99(), 99);
    assert_eq!(l.max(), 100);
}

#[test]
fn quantile_zero_is_the_minimum() {
    let l = Latencies::from(vec![9, 3, 7, 1, 5]);
    assert_eq!(l.quantile(0.0), 1);
}

#[test]
fn quantile_one_is_the_maximum() {
    let l = Latencies::from(vec![9, 3, 7, 1, 5]);
    assert_eq!(l.quantile(1.0), 9);
    assert_eq!(l.max(), 9);
}

#[test]
fn quantile_clamps_out_of_range_inputs() {
    let l = Latencies::from(1u64..=10);
    assert_eq!(l.quantile(-5.0), 1);
    assert_eq!(l.quantile(17.0), 10);
}

#[test]
fn input_order_does_not_change_percentiles() {
    let ascending = Latencies::from(1u64..=50);
    let descending = Latencies::from((1u64..=50).rev());
    assert_eq!(ascending.p50(), descending.p50());
    assert_eq!(ascending.p99(), descending.p99());
    assert_eq!(ascending.mean(), descending.mean());
}

#[test]
fn duplicates_are_kept_not_deduplicated() {
    // A sample of 99 zeros and 1 large value must report p99 = 0 and max =
    // large. Deduplicating would report p99 = large, which is the classic way
    // to make a system look worse than it is.
    let mut v = vec![0u64; 99];
    v.push(1_000_000);
    let l = Latencies::from(v);
    assert_eq!(l.len(), 100);
    assert_eq!(l.p99(), 0);
    assert_eq!(l.max(), 1_000_000);
}

#[test]
fn two_element_percentiles() {
    let l = Latencies::from(vec![10, 20]);
    assert_eq!(l.quantile(0.0), 10);
    assert_eq!(l.p50(), 10);
    assert_eq!(l.quantile(1.0), 20);
}

#[test]
fn mean_is_not_the_median() {
    // Heavy tail: the distinction is the reason the report never quotes a
    // mean latency on its own.
    let mut v = vec![1u64; 999];
    v.push(1_000_000);
    let l = Latencies::from(v);
    assert_eq!(l.p50(), 1);
    assert!(l.mean() > 999.0, "mean was {}", l.mean());
}

#[test]
fn percentiles_are_monotone_in_q() {
    let l = Latencies::from((0u64..1000).map(|i| i * i));
    let mut last = 0;
    let mut q = 0.0;
    while q <= 1.0 {
        let v = l.quantile(q);
        assert!(v >= last, "quantile decreased at q={}", q);
        last = v;
        q += 0.01;
    }
}

// ------------------------------------------------------------- Little's Law

#[test]
fn little_holds_for_a_consistent_triple() {
    let l = Little {
        measured_l: 10.0,
        lambda: 2.0,
        w: 5.0,
    };
    assert_eq!(l.predicted_l(), 10.0);
    assert!(l.relative_error() < 1e-12);
    assert!(l.holds());
}

#[test]
fn little_detects_an_inconsistent_triple() {
    let l = Little {
        measured_l: 10.0,
        lambda: 2.0,
        w: 8.0,
    };
    // |10 - 16| / 16
    assert!((l.relative_error() - 0.375).abs() < 1e-12, "{}", l.relative_error());
    assert!(!l.holds());
}

#[test]
fn little_relative_error_is_symmetric_in_sign() {
    let over = Little {
        measured_l: 11.0,
        lambda: 2.0,
        w: 5.0,
    };
    let under = Little {
        measured_l: 9.0,
        lambda: 2.0,
        w: 5.0,
    };
    assert!((over.relative_error() - under.relative_error()).abs() < 1e-12);
}

#[test]
fn little_on_an_empty_system_does_not_divide_by_zero() {
    let l = Little {
        measured_l: 0.0,
        lambda: 0.0,
        w: 0.0,
    };
    assert!(l.relative_error().is_finite());
    assert!(l.holds());
}

// ---------------------------------------------------------------- M/M/1

#[test]
fn mm1_is_undefined_at_and_above_saturation() {
    assert!(mm1_sojourn_s(5.0, 5.0).is_none());
    assert!(mm1_sojourn_s(6.0, 5.0).is_none());
}

#[test]
fn mm1_sojourn_matches_closed_form() {
    let w = mm1_sojourn_s(4.0, 5.0).unwrap();
    assert!((w - 1.0).abs() < 1e-12, "w = {}", w);
}

#[test]
fn mm1_sojourn_diverges_as_load_approaches_capacity() {
    let a = mm1_sojourn_s(4.0, 5.0).unwrap();
    let b = mm1_sojourn_s(4.9, 5.0).unwrap();
    let c = mm1_sojourn_s(4.99, 5.0).unwrap();
    assert!(b > a * 5.0);
    assert!(c > b * 5.0);
}

// ---------------------------------------------------------------- formatting

#[test]
fn fmt_us_picks_a_readable_unit() {
    assert!(fmt_us(500).contains("us") || fmt_us(500).contains("\u{b5}s"));
    assert!(fmt_us(1_500).contains("ms"));
    assert!(fmt_us(2_500_000).contains('s'));
}

#[test]
fn fmt_us_is_never_empty() {
    for v in [0u64, 1, 999, 1_000, 1_000_000, u64::MAX / 2] {
        assert!(!fmt_us(v).is_empty(), "empty for {}", v);
    }
}
