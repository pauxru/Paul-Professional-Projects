//! The deterministic execution engine.
//!
//! The whole design rests on one idea: a distributed system is a pure function
//! of (initial state, seed). Real time, real threads and real sockets are the
//! things that make concurrency bugs unreproducible, so none of them appear
//! here. Logical time advances only when the event queue says so.

use crate::rng::Rng;
use std::cmp::Ordering;
use std::collections::BinaryHeap;

pub type NodeId = usize;

/// Logical time in microseconds. `u64` rather than `Duration` because it must
/// be totally ordered, cheap to compare, and impossible to accidentally derive
/// from a wall clock.
pub type Time = u64;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Event<M> {
    Deliver { from: NodeId, msg: M },
    Timer { token: u64 },
}

/// An event on the queue. Ordering is `(time, seq)` — the sequence number is
/// what makes ties deterministic. Without it, two events at the same logical
/// instant would be ordered by whatever the heap felt like, and the run would
/// not reproduce.
#[derive(Debug, Clone, PartialEq, Eq)]
struct Scheduled<M> {
    at: Time,
    seq: u64,
    to: NodeId,
    event: Event<M>,
}

impl<M: Eq> Ord for Scheduled<M> {
    fn cmp(&self, other: &Self) -> Ordering {
        // Reversed: BinaryHeap is a max-heap and we want earliest-first.
        other
            .at
            .cmp(&self.at)
            .then_with(|| other.seq.cmp(&self.seq))
    }
}

impl<M: Eq> PartialOrd for Scheduled<M> {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

/// What a node asks the world to do. Nodes never touch the queue directly;
/// they emit actions and the engine decides what actually happens to them.
#[derive(Debug, Clone)]
pub enum Action<M> {
    Send { to: NodeId, msg: M },
    Timer { after: Time, token: u64 },
    /// Records a point of interest in the run. Cheap, and the only debugging
    /// tool that survives replay intact.
    Trace(String),
}

/// Handed to a node for the duration of one event. It deliberately cannot see
/// the queue, other nodes, or global time — only its own skewed view.
pub struct Ctx<M> {
    pub me: NodeId,
    /// This node's *skewed* view of the clock. Protocol logic must use this and
    /// only this, because a real node cannot see true time.
    pub now: Time,
    /// The engine's true clock. This is an oracle: it exists so the harness can
    /// record a history with a consistent real-time order for the
    /// linearizability checker. Protocol logic reading this would be cheating,
    /// and `tests/no_oracle_in_protocol.rs` enforces that it does not.
    pub global_now: Time,
    pub peers: usize,
    pub actions: Vec<Action<M>>,
}

impl<M> Ctx<M> {
    pub fn send(&mut self, to: NodeId, msg: M) {
        self.actions.push(Action::Send { to, msg });
    }
    pub fn timer(&mut self, after: Time, token: u64) {
        self.actions.push(Action::Timer { after, token });
    }
    pub fn trace(&mut self, s: impl Into<String>) {
        self.actions.push(Action::Trace(s.into()));
    }
}

pub trait Node<M> {
    fn handle(&mut self, ctx: &mut Ctx<M>, ev: Event<M>);
}

/// Network fault model. Every field is a knob the fuzzer turns.
#[derive(Debug, Clone)]
pub struct NetConfig {
    pub min_latency: Time,
    pub max_latency: Time,
    pub drop_prob: f64,
    pub duplicate_prob: f64,
    /// Probability that a message is held back and delivered much later. This
    /// is the fault that actually finds bugs: reordering without loss.
    pub reorder_prob: f64,
    pub reorder_extra: Time,
    /// Probability per partition-tick that the partition set is redrawn.
    pub partition_prob: f64,
    pub partition_interval: Time,
    /// Maximum absolute clock offset per node.
    pub max_clock_skew: Time,
    /// Probability that a given directed link is a straggler for the whole run,
    /// and the factor its latency is multiplied by.
    ///
    /// This was added after the first sweep. Loss, duplication, reordering and
    /// partitions are the faults everyone models, and with only those the
    /// quorum bug showed up in well under 1% of runs. Stragglers are what
    /// actually produce the condition it needs — a write that has reached some
    /// replicas and not others, and *stays* that way long enough for two reads
    /// to disagree. See `docs/portfolio/03-the-fault-that-mattered.md`.
    pub slow_link_prob: f64,
    pub slow_factor: u64,
}

impl Default for NetConfig {
    fn default() -> Self {
        NetConfig {
            min_latency: 500,
            max_latency: 5_000,
            drop_prob: 0.02,
            duplicate_prob: 0.02,
            reorder_prob: 0.10,
            reorder_extra: 40_000,
            partition_prob: 0.15,
            partition_interval: 100_000,
            max_clock_skew: 10_000,
            slow_link_prob: 0.25,
            slow_factor: 12,
        }
    }
}

impl NetConfig {
    /// A network that behaves perfectly. Used to prove the protocol is correct
    /// when nothing goes wrong — if it fails here, the bug is not the network's.
    pub fn perfect() -> Self {
        NetConfig {
            min_latency: 100,
            max_latency: 200,
            drop_prob: 0.0,
            duplicate_prob: 0.0,
            reorder_prob: 0.0,
            reorder_extra: 0,
            partition_prob: 0.0,
            partition_interval: Time::MAX,
            max_clock_skew: 0,
            slow_link_prob: 0.0,
            slow_factor: 1,
        }
    }
}

pub struct Sim<M: Eq + Clone, N: Node<M>> {
    pub rng: Rng,
    pub nodes: Vec<N>,
    pub time: Time,
    seq: u64,
    queue: BinaryHeap<Scheduled<M>>,
    cfg: NetConfig,
    /// `reachable[a * n + b]` — asymmetric on purpose, because real partitions
    /// frequently are, and symmetric-only partitions miss a class of bug.
    reachable: Vec<bool>,
    slow: Vec<bool>,
    skew: Vec<i64>,
    pub traces: Vec<(Time, NodeId, String)>,
    pub delivered: u64,
    pub dropped: u64,
    pub duplicated: u64,
    pub reordered: u64,
    pub straggled: u64,
    pub partition_events: u64,
}

impl<M: Eq + Clone, N: Node<M>> Sim<M, N> {
    pub fn new(seed: u64, nodes: Vec<N>, cfg: NetConfig) -> Self {
        let n = nodes.len();
        let mut rng = Rng::new(seed);
        let skew: Vec<i64> = (0..n)
            .map(|_| {
                if cfg.max_clock_skew == 0 {
                    0
                } else {
                    rng.between(0, 2 * cfg.max_clock_skew) as i64 - cfg.max_clock_skew as i64
                }
            })
            .collect();
        let slow: Vec<bool> = (0..n * n)
            .map(|_| cfg.slow_link_prob > 0.0 && rng.chance(cfg.slow_link_prob))
            .collect();
        let mut sim = Sim {
            rng,
            nodes,
            time: 0,
            seq: 0,
            queue: BinaryHeap::new(),
            cfg,
            reachable: vec![true; n * n],
            slow,
            skew,
            traces: Vec::new(),
            delivered: 0,
            dropped: 0,
            duplicated: 0,
            reordered: 0,
            straggled: 0,
            partition_events: 0,
        };
        if sim.cfg.partition_prob > 0.0 {
            let iv = sim.cfg.partition_interval;
            sim.push(iv, usize::MAX, Event::Timer { token: PARTITION_TICK });
        }
        sim
    }

    pub fn n(&self) -> usize {
        self.nodes.len()
    }

    fn push(&mut self, at: Time, to: NodeId, event: Event<M>) {
        self.seq += 1;
        let seq = self.seq;
        self.queue.push(Scheduled { at, seq, to, event });
    }

    /// Schedules an event on a node from outside — used to kick a run off.
    pub fn schedule(&mut self, after: Time, to: NodeId, token: u64) {
        let at = self.time + after;
        self.push(at, to, Event::Timer { token });
    }

    fn can_reach(&self, a: NodeId, b: NodeId) -> bool {
        self.reachable[a * self.nodes.len() + b]
    }

    fn redraw_partitions(&mut self) {
        let n = self.nodes.len();
        self.partition_events += 1;
        // Heal first, then maybe cut. Permanent partitions produce runs where
        // nothing can make progress, which is true but uninteresting.
        for x in self.reachable.iter_mut() {
            *x = true;
        }
        if !self.rng.chance(0.5) {
            return;
        }
        // Split into two groups; cut every edge across the cut in one or both
        // directions.
        let mut group = vec![false; n];
        let size = self.rng.between(1, (n as u64).saturating_sub(1).max(1)) as usize;
        let mut idx: Vec<usize> = (0..n).collect();
        self.rng.shuffle(&mut idx);
        for &i in idx.iter().take(size) {
            group[i] = true;
        }
        let one_way = self.rng.chance(0.3);
        for a in 0..n {
            for b in 0..n {
                if group[a] != group[b] {
                    if one_way && group[a] {
                        continue;
                    }
                    self.reachable[a * n + b] = false;
                }
            }
        }
    }

    /// Runs until the queue drains or `deadline` is passed. Returns the final
    /// logical time.
    pub fn run_until(&mut self, deadline: Time) -> Time {
        while let Some(top) = self.queue.peek() {
            if top.at > deadline {
                break;
            }
            let ev = self.queue.pop().expect("peeked");
            self.time = ev.at;

            if ev.to == usize::MAX {
                // Engine-internal event.
                if let Event::Timer { token: PARTITION_TICK } = ev.event {
                    if self.rng.chance(self.cfg.partition_prob) {
                        self.redraw_partitions();
                    }
                    let next = self.time + self.cfg.partition_interval;
                    if next <= deadline {
                        self.push(next, usize::MAX, Event::Timer { token: PARTITION_TICK });
                    }
                }
                continue;
            }

            let skewed = (self.time as i64 + self.skew[ev.to]).max(0) as Time;
            let mut ctx = Ctx {
                me: ev.to,
                now: skewed,
                global_now: self.time,
                peers: self.nodes.len(),
                actions: Vec::new(),
            };
            self.nodes[ev.to].handle(&mut ctx, ev.event);
            let actions = std::mem::take(&mut ctx.actions);
            self.apply(ev.to, actions);
        }
        self.time
    }

    fn apply(&mut self, from: NodeId, actions: Vec<Action<M>>) {
        for a in actions {
            match a {
                Action::Trace(s) => {
                    let t = self.time;
                    self.traces.push((t, from, s));
                }
                Action::Timer { after, token } => {
                    let at = self.time + after.max(1);
                    self.push(at, from, Event::Timer { token });
                }
                Action::Send { to, msg } => {
                    if !self.can_reach(from, to) {
                        self.dropped += 1;
                        continue;
                    }
                    if self.rng.chance(self.cfg.drop_prob) {
                        self.dropped += 1;
                        continue;
                    }
                    let mut delay = self
                        .rng
                        .between(self.cfg.min_latency, self.cfg.max_latency);
                    if self.slow[from * self.nodes.len() + to] {
                        delay = delay.saturating_mul(self.cfg.slow_factor);
                        self.straggled += 1;
                    }
                    if self.rng.chance(self.cfg.reorder_prob) {
                        delay += self.rng.between(0, self.cfg.reorder_extra);
                        self.reordered += 1;
                    }
                    let at = self.time + delay;
                    self.delivered += 1;
                    self.push(
                        at,
                        to,
                        Event::Deliver {
                            from,
                            msg: msg.clone(),
                        },
                    );
                    if self.rng.chance(self.cfg.duplicate_prob) {
                        let extra = self.rng.between(1, self.cfg.max_latency);
                        self.duplicated += 1;
                        self.push(at + extra, to, Event::Deliver { from, msg });
                    }
                }
            }
        }
    }
}

const PARTITION_TICK: u64 = u64::MAX;

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Debug, Clone, PartialEq, Eq)]
    struct Ping(u64);

    struct Echo {
        seen: Vec<u64>,
    }

    impl Node<Ping> for Echo {
        fn handle(&mut self, ctx: &mut Ctx<Ping>, ev: Event<Ping>) {
            match ev {
                Event::Timer { token } => {
                    for p in 0..ctx.peers {
                        if p != ctx.me {
                            ctx.send(p, Ping(token));
                        }
                    }
                }
                Event::Deliver { msg, .. } => {
                    self.seen.push(msg.0);
                }
            }
        }
    }

    fn run(seed: u64, cfg: NetConfig) -> (Vec<Vec<u64>>, Time) {
        let nodes: Vec<Echo> = (0..4).map(|_| Echo { seen: vec![] }).collect();
        let mut sim = Sim::new(seed, nodes, cfg);
        for i in 0..4 {
            sim.schedule(1000 * (i as Time + 1), i, i as u64);
        }
        let t = sim.run_until(1_000_000);
        (sim.nodes.into_iter().map(|n| n.seen).collect(), t)
    }

    #[test]
    fn identical_seeds_produce_identical_runs() {
        for seed in [1u64, 2, 99, 123456] {
            let a = run(seed, NetConfig::default());
            let b = run(seed, NetConfig::default());
            assert_eq!(a, b, "seed {seed} did not reproduce");
        }
    }

    #[test]
    fn different_seeds_produce_different_runs() {
        let a = run(1, NetConfig::default());
        let b = run(2, NetConfig::default());
        assert_ne!(a, b);
    }

    #[test]
    fn perfect_network_delivers_everything_exactly_once() {
        let (seen, _) = run(7, NetConfig::perfect());
        let total: usize = seen.iter().map(|s| s.len()).sum();
        assert_eq!(total, 4 * 3, "4 senders x 3 peers");
    }

    #[test]
    fn lossy_network_actually_loses_things() {
        let nodes: Vec<Echo> = (0..4).map(|_| Echo { seen: vec![] }).collect();
        let mut cfg = NetConfig::default();
        cfg.drop_prob = 0.5;
        let mut sim = Sim::new(5, nodes, cfg);
        for i in 0..4 {
            sim.schedule(1000, i, 0);
        }
        sim.run_until(1_000_000);
        assert!(sim.dropped > 0, "a 50% drop rate dropped nothing");
    }

    #[test]
    fn time_never_goes_backwards() {
        struct Chain {
            times: Vec<Time>,
        }
        impl Node<Ping> for Chain {
            fn handle(&mut self, ctx: &mut Ctx<Ping>, ev: Event<Ping>) {
                self.times.push(ctx.now);
                if let Event::Timer { token } = ev {
                    if token < 50 {
                        ctx.timer(10, token + 1);
                        ctx.send((ctx.me + 1) % ctx.peers, Ping(token));
                    }
                }
            }
        }
        let nodes: Vec<Chain> = (0..3).map(|_| Chain { times: vec![] }).collect();
        let mut sim = Sim::new(11, nodes, NetConfig::default());
        sim.schedule(1, 0, 0);
        let mut last = 0;
        // Step the deadline forward and check monotonicity of the engine clock.
        for d in (0..200_000).step_by(1000) {
            let t = sim.run_until(d);
            assert!(t >= last);
            last = t;
        }
    }

    #[test]
    fn partitions_are_drawn_and_healed() {
        let nodes: Vec<Echo> = (0..5).map(|_| Echo { seen: vec![] }).collect();
        let mut cfg = NetConfig::default();
        cfg.partition_prob = 1.0;
        cfg.partition_interval = 10_000;
        let mut sim = Sim::new(3, nodes, cfg);
        for i in 0..5 {
            sim.schedule(1, i, 0);
        }
        sim.run_until(500_000);
        assert!(sim.partition_events > 10);
    }
}
