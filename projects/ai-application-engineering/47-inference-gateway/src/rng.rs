//! Deterministic pseudo-randomness.
//!
//! Every distribution in this crate is derived from a seeded generator, so a
//! run is reproducible byte for byte. `docs/results.md` is a build artefact
//! and a test asserts it matches the code that produced it; that is only
//! possible if nothing here consults a clock or the OS entropy pool.

/// xorshift64*, chosen because it is four lines, has no state-initialisation
/// hazards for non-zero seeds, and passes the statistical properties this
/// crate actually depends on (uniformity of `unit()` over the ranges used to
/// drive inverse-transform sampling).
///
/// It is not cryptographic and nothing here needs it to be.
#[derive(Debug, Clone)]
pub struct Rng {
    state: u64,
}

impl Rng {
    pub fn new(seed: u64) -> Self {
        // A zero state is a fixed point of xorshift and would emit zeros
        // forever. Mixing rather than rejecting keeps `new` total.
        Rng {
            state: seed.wrapping_mul(0x9E37_79B9_7F4A_7C15) | 1,
        }
    }

    pub fn next_u64(&mut self) -> u64 {
        let mut x = self.state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        self.state = x;
        x.wrapping_mul(0x2545_F491_4F6C_DD1D)
    }

    /// Uniform on the half-open interval (0, 1).
    ///
    /// Excluding zero matters: `exponential` takes a logarithm of this value,
    /// and `ln(0)` is negative infinity. A generator that can return exactly
    /// zero produces an infinite inter-arrival time roughly once every 2^53
    /// draws, which is the kind of defect that survives every test run and
    /// then hangs a long simulation.
    pub fn unit(&mut self) -> f64 {
        let bits = self.next_u64() >> 11;
        (bits as f64 + 0.5) / ((1u64 << 53) as f64)
    }

    /// Inverse-transform sample from Exp(rate).
    pub fn exponential(&mut self, rate: f64) -> f64 {
        debug_assert!(rate > 0.0, "exponential rate must be positive");
        -self.unit().ln() / rate
    }

    /// Uniform integer in `[low, high]`, inclusive at both ends.
    pub fn range(&mut self, low: u64, high: u64) -> u64 {
        debug_assert!(low <= high);
        low + self.next_u64() % (high - low + 1)
    }

    /// Bernoulli trial.
    pub fn chance(&mut self, p: f64) -> bool {
        self.unit() < p
    }

    /// Log-normal, parameterised by the median and the multiplicative spread
    /// rather than by mu and sigma, because the callers reason in terms of
    /// "the typical request is 400 tokens and the long tail is about 8x".
    pub fn lognormal(&mut self, median: f64, spread: f64) -> f64 {
        let sigma = spread.ln() / 1.2816; // 90th percentile at median*spread
        median * (sigma * self.normal()).exp()
    }

    /// Box-Muller, keeping only one of the two variates. Wasting a normal per
    /// call is cheaper than carrying the cached-variate state, and carrying
    /// it would make the generator's output depend on call history in a way
    /// that makes tests harder to reason about.
    fn normal(&mut self) -> f64 {
        let u1 = self.unit();
        let u2 = self.unit();
        (-2.0 * u1.ln()).sqrt() * (std::f64::consts::TAU * u2).cos()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn same_seed_same_sequence() {
        let a: Vec<u64> = (0..64).map(|_| Rng::new(7).next_u64()).collect();
        let mut r = Rng::new(7);
        let b: Vec<u64> = (0..64).map(|_| r.next_u64()).collect();
        assert_eq!(a[0], b[0]);
    }

    #[test]
    fn different_seeds_diverge() {
        assert_ne!(Rng::new(1).next_u64(), Rng::new(2).next_u64());
    }

    #[test]
    fn zero_seed_does_not_stick() {
        let mut r = Rng::new(0);
        let first = r.next_u64();
        assert_ne!(first, 0);
        assert_ne!(r.next_u64(), first);
    }

    #[test]
    fn unit_is_strictly_inside_zero_and_one() {
        let mut r = Rng::new(11);
        for _ in 0..200_000 {
            let u = r.unit();
            assert!(u > 0.0 && u < 1.0, "unit() produced {u}");
        }
    }

    #[test]
    fn exponential_mean_matches_the_rate() {
        let mut r = Rng::new(3);
        let n = 200_000;
        let total: f64 = (0..n).map(|_| r.exponential(4.0)).sum();
        let mean = total / n as f64;
        assert!((mean - 0.25).abs() < 0.005, "mean was {mean}");
    }

    #[test]
    fn exponential_is_memoryless_enough() {
        // P(X > 2m | X > m) should equal P(X > m) for an exponential.
        let mut r = Rng::new(5);
        let samples: Vec<f64> = (0..200_000).map(|_| r.exponential(1.0)).collect();
        let past_one = samples.iter().filter(|&&x| x > 1.0).count() as f64;
        let past_two = samples.iter().filter(|&&x| x > 2.0).count() as f64;
        let conditional = past_two / past_one;
        let unconditional = past_one / samples.len() as f64;
        assert!((conditional - unconditional).abs() < 0.01);
    }

    #[test]
    fn range_covers_both_endpoints() {
        let mut r = Rng::new(9);
        let mut low = false;
        let mut high = false;
        for _ in 0..10_000 {
            match r.range(3, 5) {
                3 => low = true,
                5 => high = true,
                4 => {}
                other => panic!("out of range: {other}"),
            }
        }
        assert!(low && high);
    }

    #[test]
    fn lognormal_median_is_the_median() {
        let mut r = Rng::new(13);
        let mut samples: Vec<f64> = (0..40_000).map(|_| r.lognormal(400.0, 8.0)).collect();
        samples.sort_by(|a, b| a.partial_cmp(b).unwrap());
        let median = samples[samples.len() / 2];
        assert!((median - 400.0).abs() < 12.0, "median was {median}");
    }

    #[test]
    fn lognormal_is_right_skewed() {
        let mut r = Rng::new(17);
        let samples: Vec<f64> = (0..40_000).map(|_| r.lognormal(400.0, 8.0)).collect();
        let mean = samples.iter().sum::<f64>() / samples.len() as f64;
        assert!(mean > 400.0, "log-normal mean must exceed its median");
    }

    #[test]
    fn chance_is_calibrated() {
        let mut r = Rng::new(19);
        let hits = (0..100_000).filter(|_| r.chance(0.3)).count();
        assert!((hits as f64 / 100_000.0 - 0.3).abs() < 0.01);
    }
}
