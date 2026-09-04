//! An ABD-style quorum-replicated register, and the workload that drives it.
//!
//! The protocol is the textbook one: a write reads the highest timestamp from a
//! majority, picks a strictly greater one, and stores to a majority; a read
//! collects from a majority and takes the highest timestamp it saw.
//!
//! The `read_repair` flag toggles the read's *second phase* — writing the
//! observed value back to a majority before returning. Leaving it out looks
//! like an optimisation, it passes every hand-written test, and it is wrong.
//! Finding that automatically is the point of this repository.

use crate::rng::Rng;
use crate::sim::{Ctx, Event, Node, NodeId, Time};
use crate::linearizability::{Kind, Op};

/// `(counter, node)` — the node id breaks ties so the order is total.
pub type Stamp = (u64, usize);

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Msg {
    Query { rid: u64 },
    QueryResp { rid: u64, ts: Stamp, val: u64 },
    Store { rid: u64, ts: Stamp, val: u64 },
    StoreAck { rid: u64 },
}

#[derive(Debug, Clone)]
pub struct Config {
    pub replicas: usize,
    pub clients: usize,
    pub ops_per_client: usize,
    /// The switch this repository is about.
    pub read_repair: bool,
    pub op_timeout: Time,
    pub think_time: Time,
}

impl Default for Config {
    fn default() -> Self {
        Config {
            replicas: 5,
            clients: 3,
            ops_per_client: 4,
            read_repair: true,
            op_timeout: 60_000,
            think_time: 2_000,
        }
    }
}

impl Config {
    pub fn majority(&self) -> usize {
        self.replicas / 2 + 1
    }

    /// The workload shape empirically most likely to expose the quorum bug,
    /// chosen from the sweep in `docs/results.md` rather than by intuition.
    ///
    /// My intuition said more replicas would help — larger quorums, more room
    /// to disagree. It is the opposite: at 7 replicas the bug is nearly
    /// invisible, because a read quorum of 4 of 7 is very likely to overlap
    /// whatever subset a partial write has reached. Small clusters with many
    /// concurrent clients are where it lives.
    pub fn hunting() -> Self {
        Config {
            replicas: 3,
            clients: 4,
            ops_per_client: 6,
            ..Config::default()
        }
    }
}

pub struct Replica {
    pub ts: Stamp,
    pub val: u64,
}

impl Replica {
    pub fn new() -> Self {
        Replica {
            ts: (0, 0),
            val: 0,
        }
    }
}

impl Default for Replica {
    fn default() -> Self {
        Self::new()
    }
}

#[derive(Debug, Clone)]
enum Phase {
    Idle,
    /// Phase 1 of a read.
    ReadQuery {
        rid: u64,
        best: (Stamp, u64),
        from: Vec<bool>,
        n: usize,
    },
    /// Phase 2 of a read — write-back. Absent when `read_repair` is off.
    ReadRepair {
        rid: u64,
        val: u64,
        from: Vec<bool>,
        n: usize,
    },
    /// Phase 1 of a write.
    WriteQuery {
        rid: u64,
        val: u64,
        best: Stamp,
        from: Vec<bool>,
        n: usize,
    },
    /// Phase 2 of a write.
    WriteStore {
        rid: u64,
        from: Vec<bool>,
        n: usize,
    },
}

pub struct Client {
    id: usize,
    cfg: Config,
    rng: Rng,
    phase: Phase,
    next_rid: u64,
    issued: usize,
    /// The operation currently in flight: (op index in `ops`).
    current: Option<usize>,
    pub ops: Vec<Op>,
    /// Global op ids are handed out by the builder so they are unique across
    /// clients; the checker needs that.
    next_op_id: usize,
    op_id_stride: usize,
}

impl Client {
    pub fn new(id: usize, cfg: Config, seed: u64, first_op_id: usize, stride: usize) -> Self {
        Client {
            id,
            cfg,
            rng: Rng::new(seed),
            phase: Phase::Idle,
            next_rid: 1,
            issued: 0,
            current: None,
            ops: Vec::new(),
            next_op_id: first_op_id,
            op_id_stride: stride,
        }
    }

    fn replicas(&self) -> usize {
        self.cfg.replicas
    }

    fn majority(&self) -> usize {
        self.cfg.majority()
    }

    fn fresh_rid(&mut self) -> u64 {
        self.next_rid += 1;
        // Encode the client so two clients never collide.
        self.next_rid * 100 + self.id as u64
    }

    fn broadcast(&self, ctx: &mut Ctx<Msg>, msg: Msg) {
        for r in 0..self.replicas() {
            ctx.send(r, msg.clone());
        }
    }

    fn start_op(&mut self, ctx: &mut Ctx<Msg>) {
        if self.issued >= self.cfg.ops_per_client {
            return;
        }
        self.issued += 1;
        let rid = self.fresh_rid();
        // A write value that identifies its author, so a violation report can
        // say who wrote what.
        let is_write = self.rng.chance(0.4);
        let op_id = self.next_op_id;
        self.next_op_id += self.op_id_stride;
        let kind = if is_write {
            Kind::Write(self.rng.between(1, 9) * 10 + self.id as u64)
        } else {
            // Filled in when the read completes.
            Kind::Read(u64::MAX)
        };
        self.ops.push(Op {
            id: op_id,
            client: self.id,
            invoked: ctx.global_now,
            returned: None,
            kind,
        });
        self.current = Some(self.ops.len() - 1);

        self.phase = if is_write {
            let val = match kind {
                Kind::Write(v) => v,
                _ => unreachable!(),
            };
            Phase::WriteQuery {
                rid,
                val,
                best: (0, 0),
                from: vec![false; self.replicas()],
                n: 0,
            }
        } else {
            Phase::ReadQuery {
                rid,
                best: ((0, 0), 0),
                from: vec![false; self.replicas()],
                n: 0,
            }
        };
        self.broadcast(ctx, Msg::Query { rid });
        ctx.timer(self.cfg.op_timeout, rid);
    }

    fn finish(&mut self, ctx: &mut Ctx<Msg>, result: Option<u64>) {
        if let Some(i) = self.current.take() {
            self.ops[i].returned = Some(ctx.global_now);
            if let (Kind::Read(_), Some(v)) = (self.ops[i].kind, result) {
                self.ops[i].kind = Kind::Read(v);
            }
        }
        self.phase = Phase::Idle;
        let delay = self.cfg.think_time.max(1);
        ctx.timer(delay, START_TOKEN);
    }

    fn abandon(&mut self, ctx: &mut Ctx<Msg>) {
        // The operation stays in the history with `returned: None`. It may or
        // may not have taken effect, and the checker is required to consider
        // both possibilities.
        if let Some(i) = self.current.take() {
            if let Kind::Read(v) = self.ops[i].kind {
                if v == u64::MAX {
                    // A read that never returned tells us nothing at all, so it
                    // must not constrain the checker. Drop it.
                    self.ops.remove(i);
                }
            }
        }
        self.phase = Phase::Idle;
        ctx.timer(self.cfg.think_time.max(1), START_TOKEN);
    }
}

pub const START_TOKEN: u64 = 0;

/// One node in the simulation. Replicas and clients share a `Vec`, so they
/// share a type.
pub enum Actor {
    Replica(Replica),
    Client(Box<Client>),
}

impl Node<Msg> for Actor {
    fn handle(&mut self, ctx: &mut Ctx<Msg>, ev: Event<Msg>) {
        match self {
            Actor::Replica(r) => r.handle(ctx, ev),
            Actor::Client(c) => c.handle(ctx, ev),
        }
    }
}

impl Node<Msg> for Replica {
    fn handle(&mut self, ctx: &mut Ctx<Msg>, ev: Event<Msg>) {
        let Event::Deliver { from, msg } = ev else {
            return;
        };
        match msg {
            Msg::Query { rid } => {
                ctx.send(
                    from,
                    Msg::QueryResp {
                        rid,
                        ts: self.ts,
                        val: self.val,
                    },
                );
            }
            Msg::Store { rid, ts, val } => {
                // Strictly greater: a duplicate Store must be a no-op, and an
                // out-of-order older Store must never win.
                if ts > self.ts {
                    self.ts = ts;
                    self.val = val;
                }
                ctx.send(from, Msg::StoreAck { rid });
            }
            _ => {}
        }
    }
}

impl Node<Msg> for Client {
    fn handle(&mut self, ctx: &mut Ctx<Msg>, ev: Event<Msg>) {
        match ev {
            Event::Timer { token } => {
                if token == START_TOKEN {
                    if matches!(self.phase, Phase::Idle) {
                        self.start_op(ctx);
                    }
                    return;
                }
                // An operation timeout. Only meaningful if that operation is
                // still the current one.
                let live = match &self.phase {
                    Phase::Idle => None,
                    Phase::ReadQuery { rid, .. }
                    | Phase::ReadRepair { rid, .. }
                    | Phase::WriteQuery { rid, .. }
                    | Phase::WriteStore { rid, .. } => Some(*rid),
                };
                if live == Some(token) {
                    ctx.trace(format!("client {} abandoned rid {}", self.id, token));
                    self.abandon(ctx);
                }
            }
            Event::Deliver { from, msg } => self.on_msg(ctx, from, msg),
        }
    }
}

impl Client {
    fn on_msg(&mut self, ctx: &mut Ctx<Msg>, from: NodeId, msg: Msg) {
        // Take the phase so the borrow checker lets us mutate `self` freely,
        // then put back whatever we decide the new phase is.
        let phase = std::mem::replace(&mut self.phase, Phase::Idle);
        match (phase, msg) {
            (
                Phase::ReadQuery {
                    rid,
                    mut best,
                    from: mut seen,
                    mut n,
                },
                Msg::QueryResp {
                    rid: r,
                    ts,
                    val,
                },
            ) if r == rid => {
                // The network duplicates messages. Counting a duplicate towards
                // a quorum would let one replica form a "majority" on its own.
                if from < seen.len() && !seen[from] {
                    seen[from] = true;
                    n += 1;
                    if ts > best.0 {
                        best = (ts, val);
                    }
                }
                if n >= self.majority() {
                    if self.cfg.read_repair {
                        let rid2 = self.fresh_rid();
                        self.phase = Phase::ReadRepair {
                            rid: rid2,
                            val: best.1,
                            from: vec![false; self.replicas()],
                            n: 0,
                        };
                        self.broadcast(
                            ctx,
                            Msg::Store {
                                rid: rid2,
                                ts: best.0,
                                val: best.1,
                            },
                        );
                        ctx.timer(self.cfg.op_timeout, rid2);
                    } else {
                        // The bug. Returning here is fast, obvious, and unsafe.
                        self.finish(ctx, Some(best.1));
                    }
                } else {
                    self.phase = Phase::ReadQuery {
                        rid,
                        best,
                        from: seen,
                        n,
                    };
                }
            }

            (
                Phase::ReadRepair {
                    rid,
                    val,
                    from: mut seen,
                    mut n,
                },
                Msg::StoreAck { rid: r },
            ) if r == rid => {
                if from < seen.len() && !seen[from] {
                    seen[from] = true;
                    n += 1;
                }
                if n >= self.majority() {
                    self.finish(ctx, Some(val));
                } else {
                    self.phase = Phase::ReadRepair {
                        rid,
                        val,
                        from: seen,
                        n,
                    };
                }
            }

            (
                Phase::WriteQuery {
                    rid,
                    val,
                    mut best,
                    from: mut seen,
                    mut n,
                },
                Msg::QueryResp { rid: r, ts, .. },
            ) if r == rid => {
                if from < seen.len() && !seen[from] {
                    seen[from] = true;
                    n += 1;
                    if ts > best {
                        best = ts;
                    }
                }
                if n >= self.majority() {
                    let newts = (best.0 + 1, self.id);
                    let rid2 = self.fresh_rid();
                    self.phase = Phase::WriteStore {
                        rid: rid2,
                        from: vec![false; self.replicas()],
                        n: 0,
                    };
                    self.broadcast(
                        ctx,
                        Msg::Store {
                            rid: rid2,
                            ts: newts,
                            val,
                        },
                    );
                    ctx.timer(self.cfg.op_timeout, rid2);
                } else {
                    self.phase = Phase::WriteQuery {
                        rid,
                        val,
                        best,
                        from: seen,
                        n,
                    };
                }
            }

            (
                Phase::WriteStore {
                    rid,
                    from: mut seen,
                    mut n,
                },
                Msg::StoreAck { rid: r },
            ) if r == rid => {
                if from < seen.len() && !seen[from] {
                    seen[from] = true;
                    n += 1;
                }
                if n >= self.majority() {
                    self.finish(ctx, None);
                } else {
                    self.phase = Phase::WriteStore {
                        rid,
                        from: seen,
                        n,
                    };
                }
            }

            // A stale response for an abandoned or already-satisfied phase.
            (p, _) => self.phase = p,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::sim::{NetConfig, Sim};

    fn build(cfg: Config, seed: u64) -> Vec<Actor> {
        let mut actors: Vec<Actor> = (0..cfg.replicas)
            .map(|_| Actor::Replica(Replica::new()))
            .collect();
        for c in 0..cfg.clients {
            actors.push(Actor::Client(Box::new(Client::new(
                c,
                cfg.clone(),
                seed ^ (0xC1E0 + c as u64),
                c,
                cfg.clients,
            ))));
        }
        actors
    }

    #[test]
    fn a_replica_ignores_an_older_store() {
        let mut r = Replica::new();
        let mut ctx = Ctx {
            me: 0,
            now: 0,
            global_now: 0,
            peers: 1,
            actions: vec![],
        };
        r.handle(
            &mut ctx,
            Event::Deliver {
                from: 1,
                msg: Msg::Store {
                    rid: 1,
                    ts: (5, 0),
                    val: 50,
                },
            },
        );
        r.handle(
            &mut ctx,
            Event::Deliver {
                from: 1,
                msg: Msg::Store {
                    rid: 2,
                    ts: (3, 0),
                    val: 30,
                },
            },
        );
        assert_eq!(r.val, 50);
        assert_eq!(r.ts, (5, 0));
    }

    #[test]
    fn a_duplicate_store_is_idempotent() {
        let mut r = Replica::new();
        let mut ctx = Ctx {
            me: 0,
            now: 0,
            global_now: 0,
            peers: 1,
            actions: vec![],
        };
        let m = Msg::Store {
            rid: 1,
            ts: (4, 1),
            val: 41,
        };
        r.handle(&mut ctx, Event::Deliver { from: 1, msg: m.clone() });
        let after_first = (r.ts, r.val);
        r.handle(&mut ctx, Event::Deliver { from: 1, msg: m });
        assert_eq!((r.ts, r.val), after_first);
    }

    #[test]
    fn the_run_is_reproducible() {
        let cfg = Config::default();
        let go = || {
            let mut sim = Sim::new(1234, build(cfg.clone(), 1234), NetConfig::default());
            for c in 0..cfg.clients {
                sim.schedule(1 + c as Time, cfg.replicas + c, START_TOKEN);
            }
            sim.run_until(5_000_000);
            (sim.delivered, sim.dropped, sim.traces.len())
        };
        assert_eq!(go(), go());
    }

    #[test]
    fn on_a_perfect_network_every_operation_completes() {
        let cfg = Config {
            read_repair: true,
            ..Config::default()
        };
        let mut sim = Sim::new(9, build(cfg.clone(), 9), NetConfig::perfect());
        for c in 0..cfg.clients {
            sim.schedule(1 + c as Time, cfg.replicas + c, START_TOKEN);
        }
        sim.run_until(50_000_000);
        let mut total = 0;
        for a in &sim.nodes {
            if let Actor::Client(c) = a {
                total += c.ops.len();
                assert!(
                    c.ops.iter().all(|o| o.returned.is_some()),
                    "a perfect network should not strand operations"
                );
            }
        }
        assert_eq!(total, cfg.clients * cfg.ops_per_client);
    }
}
