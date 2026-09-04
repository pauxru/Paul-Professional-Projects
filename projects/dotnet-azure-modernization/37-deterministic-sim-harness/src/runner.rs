//! Running simulations, checking them, and shrinking the failures.

use crate::abd::{Actor, Client, Config, Replica, START_TOKEN};
use crate::linearizability::{History, Verdict};
use crate::sim::{NetConfig, Sim, Time};

#[derive(Debug, Clone)]
pub struct RunStats {
    pub delivered: u64,
    pub dropped: u64,
    pub duplicated: u64,
    pub reordered: u64,
    pub partition_events: u64,
    pub sim_time: Time,
}

#[derive(Debug, Clone)]
pub struct RunResult {
    pub seed: u64,
    pub verdict: Verdict,
    pub history: History,
    pub stats: RunStats,
    pub traces: Vec<(Time, usize, String)>,
    pub completed: usize,
    pub abandoned: usize,
}

impl RunResult {
    pub fn failed(&self) -> bool {
        matches!(self.verdict, Verdict::NotLinearizable { .. })
    }
}

fn build(cfg: &Config, seed: u64) -> Vec<Actor> {
    let mut actors: Vec<Actor> = (0..cfg.replicas)
        .map(|_| Actor::Replica(Replica::new()))
        .collect();
    for c in 0..cfg.clients {
        actors.push(Actor::Client(Box::new(Client::new(
            c,
            cfg.clone(),
            // Derived from the run seed so the whole run is a function of it.
            seed.wrapping_mul(0x9E37_79B9).wrapping_add(c as u64 + 1),
            c,
            cfg.clients.max(1),
        ))));
    }
    actors
}

/// One complete simulated run: build the cluster, drive the workload, harvest
/// the history, check it.
pub fn run_one(seed: u64, cfg: &Config, net: &NetConfig) -> RunResult {
    let mut sim = Sim::new(seed, build(cfg, seed), net.clone());
    for c in 0..cfg.clients {
        // Stagger the starts by a hair so clients do not move in lockstep.
        sim.schedule(1 + c as Time * 137, cfg.replicas + c, START_TOKEN);
    }
    // Generous: long enough for every client to finish its budget under heavy
    // loss, short enough that a livelocked run terminates.
    let deadline = 40_000_000;
    let sim_time = sim.run_until(deadline);

    let mut history = History::new(0);
    for a in &sim.nodes {
        if let Actor::Client(c) = a {
            history.ops.extend(c.ops.iter().cloned());
        }
    }
    history.ops.sort_by_key(|o| (o.invoked, o.id));

    let completed = history.completed();
    let abandoned = history.ops.len() - completed;
    let verdict = history.check();

    RunResult {
        seed,
        verdict,
        history,
        stats: RunStats {
            delivered: sim.delivered,
            dropped: sim.dropped,
            duplicated: sim.duplicated,
            reordered: sim.reordered,
            partition_events: sim.partition_events,
            sim_time,
        },
        traces: sim.traces,
        completed,
        abandoned,
    }
}

#[derive(Debug, Clone, Default)]
pub struct FuzzSummary {
    pub runs: usize,
    pub failures: Vec<u64>,
    pub total_ops: usize,
    pub total_abandoned: usize,
    pub total_dropped: u64,
    pub total_duplicated: u64,
    pub total_reordered: u64,
}

impl FuzzSummary {
    pub fn failure_rate(&self) -> f64 {
        if self.runs == 0 {
            0.0
        } else {
            self.failures.len() as f64 / self.runs as f64
        }
    }
}

pub fn fuzz(seeds: impl Iterator<Item = u64>, cfg: &Config, net: &NetConfig) -> FuzzSummary {
    let mut s = FuzzSummary::default();
    for seed in seeds {
        let r = run_one(seed, cfg, net);
        s.runs += 1;
        s.total_ops += r.history.ops.len();
        s.total_abandoned += r.abandoned;
        s.total_dropped += r.stats.dropped;
        s.total_duplicated += r.stats.duplicated;
        s.total_reordered += r.stats.reordered;
        if r.failed() {
            s.failures.push(seed);
        }
    }
    s
}

/// Shrinks a failing configuration while keeping the same seed.
///
/// The seed is held fixed, so a smaller configuration is a *different* run, not
/// a subsequence of the original — it may simply not fail. That is why this
/// keeps only reductions that still fail, and why it is honest to call the
/// result "the smallest configuration that still fails at this seed" rather
/// than "the minimal counterexample".
pub fn shrink(seed: u64, cfg: &Config, net: &NetConfig) -> Config {
    let mut best = cfg.clone();
    loop {
        let mut improved = false;
        let mut candidates = Vec::new();
        if best.ops_per_client > 1 {
            let mut c = best.clone();
            c.ops_per_client -= 1;
            candidates.push(c);
        }
        if best.clients > 2 {
            let mut c = best.clone();
            c.clients -= 1;
            candidates.push(c);
        }
        if best.replicas > 3 {
            let mut c = best.clone();
            c.replicas -= 2; // keep it odd
            candidates.push(c);
        }
        for c in candidates {
            if run_one(seed, &c, net).failed() {
                best = c;
                improved = true;
                break;
            }
        }
        if !improved {
            return best;
        }
    }
}

/// Renders a failure the way a human wants to read it.
pub fn report(r: &RunResult) -> String {
    let mut out = String::new();
    out.push_str(&format!("seed {}\n", r.seed));
    out.push_str(&format!(
        "  network: {} delivered, {} dropped, {} duplicated, {} reordered, {} partition changes\n",
        r.stats.delivered,
        r.stats.dropped,
        r.stats.duplicated,
        r.stats.reordered,
        r.stats.partition_events
    ));
    out.push_str(&format!(
        "  history: {} operations ({} completed, {} abandoned)\n",
        r.history.ops.len(),
        r.completed,
        r.abandoned
    ));
    match &r.verdict {
        Verdict::Linearizable { order } => {
            out.push_str(&format!("  LINEARIZABLE, witness order {order:?}\n"));
        }
        Verdict::NotLinearizable {
            culprit,
            explanation,
        } => {
            out.push_str(&format!("  NOT LINEARIZABLE (operation {culprit})\n"));
            out.push_str(&format!("  {explanation}\n"));
            out.push_str("  history:\n");
            for o in &r.history.ops {
                let mark = if o.id == *culprit { ">>" } else { "  " };
                out.push_str(&format!(
                    "  {mark} op{:<3} client{} {:>10}..{:<10} {:?}\n",
                    o.id,
                    o.client,
                    o.invoked,
                    o.returned
                        .map(|x| x.to_string())
                        .unwrap_or_else(|| "pending".into()),
                    o.kind
                ));
            }
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_correct_protocol_survives_a_hostile_network() {
        let cfg = Config {
            read_repair: true,
            ..Config::default()
        };
        let net = NetConfig::default();
        let s = fuzz(0..200, &cfg, &net);
        assert_eq!(
            s.failures,
            Vec::<u64>::new(),
            "read-repair ABD should be linearizable at every seed"
        );
        assert!(s.total_dropped > 0, "the network was not actually hostile");
        assert!(s.total_reordered > 0);
    }

    #[test]
    fn removing_read_repair_is_caught() {
        let cfg = Config {
            read_repair: false,
            ..Config::default()
        };
        let net = NetConfig::default();
        let s = fuzz(0..200, &cfg, &net);
        assert!(
            !s.failures.is_empty(),
            "dropping the read write-back must produce a violation"
        );
    }

    #[test]
    fn a_failure_reproduces_exactly() {
        let cfg = Config {
            read_repair: false,
            ..Config::default()
        };
        let net = NetConfig::default();
        let s = fuzz(0..300, &cfg, &net);
        let seed = *s.failures.first().expect("expected at least one failure");
        let a = run_one(seed, &cfg, &net);
        let b = run_one(seed, &cfg, &net);
        assert!(a.failed() && b.failed());
        assert_eq!(report(&a), report(&b), "a failure must replay identically");
    }

    #[test]
    fn shrinking_produces_a_configuration_that_still_fails() {
        let cfg = Config {
            read_repair: false,
            clients: 4,
            ops_per_client: 8,
            replicas: 5,
            ..Config::default()
        };
        let net = NetConfig::default();
        let s = fuzz(0..1000, &cfg, &net);
        let seed = *s.failures.first().expect("expected at least one failure");
        let small = shrink(seed, &cfg, &net);
        assert!(run_one(seed, &small, &net).failed());
        assert!(
            small.ops_per_client <= cfg.ops_per_client && small.clients <= cfg.clients,
            "shrinking must not grow the configuration"
        );
    }

    #[test]
    fn a_perfect_network_hides_the_bug() {
        // The whole argument for fault injection: the broken protocol passes
        // when nothing goes wrong. This is why it ships.
        let cfg = Config {
            read_repair: false,
            ..Config::hunting()
        };
        let s = fuzz(0..1000, &cfg, &NetConfig::perfect());
        assert!(
            s.failures.is_empty(),
            "on a perfect network the bug should be invisible; \
             if this fails the fault model is not the thing finding it"
        );
    }
}
