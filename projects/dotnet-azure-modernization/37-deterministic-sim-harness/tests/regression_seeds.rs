//! Seeds that are known to expose the quorum bug, recorded so they keep doing
//! so.
//!
//! These are the closest thing this repository has to a golden file. A change
//! to the RNG, the event ordering, the fault model or the protocol will move
//! them, and that is exactly what should be noticed: the entire value of the
//! harness is that a seed recorded today still reproduces tomorrow. If this
//! test fails after a refactor, the refactor broke replayability even if
//! everything else still passes.

use detsim::abd::Config;
use detsim::runner::{report, run_one};
use detsim::sim::NetConfig;

const KNOWN_FAILING: &[u64] = &[89, 134, 167, 204, 206, 302, 319, 339, 358, 404];

fn broken() -> Config {
    Config {
        read_repair: false,
        ..Config::hunting()
    }
}

#[test]
fn recorded_seeds_still_expose_the_bug() {
    for &seed in KNOWN_FAILING {
        let r = run_one(seed, &broken(), &NetConfig::default());
        assert!(
            r.failed(),
            "seed {seed} was recorded as a counterexample and no longer reproduces"
        );
    }
}

#[test]
fn recorded_seeds_are_clean_once_read_repair_is_restored() {
    let fixed = Config {
        read_repair: true,
        ..Config::hunting()
    };
    for &seed in KNOWN_FAILING {
        let r = run_one(seed, &fixed, &NetConfig::default());
        assert!(
            !r.failed(),
            "seed {seed} still violates linearizability with the fix applied"
        );
    }
}

#[test]
fn a_replay_is_byte_identical() {
    for &seed in KNOWN_FAILING {
        let a = report(&run_one(seed, &broken(), &NetConfig::default()));
        let b = report(&run_one(seed, &broken(), &NetConfig::default()));
        assert_eq!(a, b, "seed {seed} did not replay identically");
    }
}

#[test]
fn every_counterexample_names_a_read_that_went_backwards() {
    use detsim::linearizability::{Kind, Verdict};
    for &seed in KNOWN_FAILING {
        let r = run_one(seed, &broken(), &NetConfig::default());
        let Verdict::NotLinearizable { culprit, .. } = &r.verdict else {
            panic!("seed {seed} should fail");
        };
        let op = r
            .history
            .ops
            .iter()
            .find(|o| o.id == *culprit)
            .unwrap_or_else(|| panic!("seed {seed}: culprit {culprit} not in history"));
        assert!(
            matches!(op.kind, Kind::Read(_)),
            "seed {seed}: blamed a write, which this bug never produces"
        );
        assert!(
            op.returned.is_some(),
            "seed {seed}: blamed an operation that never returned"
        );
    }
}

/// The failure rate is a property of the harness, not a coincidence. If it
/// drifts far from what is documented, `docs/results.md` is stale.
#[test]
fn the_documented_failure_rate_still_holds() {
    let s = detsim::fuzz(0..2000, &broken(), &NetConfig::default());
    let rate = 100.0 * s.failure_rate();
    assert!(
        (1.5..3.5).contains(&rate),
        "documented rate is 2.30%; measured {rate:.2}% — update docs/results.md"
    );
}
