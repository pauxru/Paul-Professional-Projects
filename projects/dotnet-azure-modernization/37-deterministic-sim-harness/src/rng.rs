//! Deterministic pseudo-random number generation.
//!
//! Everything nondeterministic in the simulator is funnelled through this type.
//! There is exactly one `Rng` per simulation run, seeded once. If any component
//! reaches for `HashMap` iteration order, `SystemTime`, or a thread, determinism
//! is lost — see `docs/adr/0001-determinism-boundary.md`.

/// SplitMix64. Chosen because it is a pure function of its state, has no
/// hidden global, and is trivially reproducible across platforms.
#[derive(Debug, Clone)]
pub struct Rng {
    state: u64,
}

impl Rng {
    pub fn new(seed: u64) -> Self {
        Rng { state: seed }
    }

    pub fn next_u64(&mut self) -> u64 {
        self.state = self.state.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.state;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    /// Uniform in `[0, n)`. Rejection-sampled so the distribution is exact; a
    /// modulo would bias low values, and a biased scheduler is one that quietly
    /// never explores certain interleavings.
    pub fn below(&mut self, n: u64) -> u64 {
        assert!(n > 0, "below(0) is undefined");
        let zone = u64::MAX - (u64::MAX % n) - 1;
        loop {
            let v = self.next_u64();
            if v <= zone {
                return v % n;
            }
        }
    }

    /// Inclusive range.
    pub fn between(&mut self, lo: u64, hi: u64) -> u64 {
        assert!(lo <= hi);
        lo + self.below(hi - lo + 1)
    }

    /// True with probability `p` (clamped to `[0,1]`).
    pub fn chance(&mut self, p: f64) -> bool {
        let p = p.clamp(0.0, 1.0);
        // 2^53 keeps the ratio exactly representable as f64.
        let scale = 1u64 << 53;
        (self.below(scale) as f64) < p * (scale as f64)
    }

    pub fn choose<'a, T>(&mut self, xs: &'a [T]) -> Option<&'a T> {
        if xs.is_empty() {
            None
        } else {
            xs.get(self.below(xs.len() as u64) as usize)
        }
    }

    pub fn shuffle<T>(&mut self, xs: &mut [T]) {
        for i in (1..xs.len()).rev() {
            let j = self.below(i as u64 + 1) as usize;
            xs.swap(i, j);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn same_seed_gives_same_stream() {
        let mut a = Rng::new(42);
        let mut b = Rng::new(42);
        for _ in 0..1000 {
            assert_eq!(a.next_u64(), b.next_u64());
        }
    }

    #[test]
    fn different_seeds_diverge() {
        let mut a = Rng::new(1);
        let mut b = Rng::new(2);
        let da: Vec<u64> = (0..8).map(|_| a.next_u64()).collect();
        let db: Vec<u64> = (0..8).map(|_| b.next_u64()).collect();
        assert_ne!(da, db);
    }

    /// A known-answer test. If a refactor silently changes the stream, every
    /// recorded failing seed in this repository stops reproducing — a worse
    /// bug than whatever the refactor was fixing.
    #[test]
    fn splitmix64_matches_reference_vector() {
        let mut r = Rng::new(0);
        assert_eq!(r.next_u64(), 0xE220_A839_7B1D_CDAF);
        assert_eq!(r.next_u64(), 0x6E78_9E6A_A1B9_65F4);
        assert_eq!(r.next_u64(), 0x06C4_5D18_8009_454F);
    }

    #[test]
    fn below_stays_in_range_and_covers_it() {
        let mut r = Rng::new(7);
        let mut seen = [false; 5];
        for _ in 0..500 {
            let v = r.below(5);
            assert!(v < 5);
            seen[v as usize] = true;
        }
        assert!(seen.iter().all(|&s| s));
    }

    #[test]
    fn chance_zero_and_one_are_absolute() {
        let mut r = Rng::new(3);
        for _ in 0..200 {
            assert!(!r.chance(0.0));
            assert!(r.chance(1.0));
        }
    }

    #[test]
    fn shuffle_is_a_permutation() {
        let mut r = Rng::new(9);
        let mut xs: Vec<u32> = (0..64).collect();
        r.shuffle(&mut xs);
        let mut sorted = xs.clone();
        sorted.sort();
        assert_eq!(sorted, (0..64).collect::<Vec<u32>>());
        assert_ne!(xs, sorted, "a shuffle that changes nothing is suspicious");
    }
}
