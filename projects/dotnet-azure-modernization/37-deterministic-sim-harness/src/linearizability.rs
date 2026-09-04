//! Linearizability checking for a single register.
//!
//! A history is a set of operations, each with an invocation time and possibly
//! a completion time. The history is linearizable if there exists a total order
//! of the operations that (a) respects real-time precedence — if `a` completed
//! before `b` was invoked, `a` must come first — and (b) is a legal sequential
//! execution of the register.
//!
//! This is the Wing–Gong search with memoisation. It is exponential in the
//! worst case, which is fine and in fact the point: the workload is sized so
//! that the checker is exact rather than heuristic. A checker that can say
//! "probably fine" is not useful for the thing this repository is about.

use std::collections::HashMap;

pub type OpId = usize;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Kind {
    Write(u64),
    /// A read that returned this value.
    Read(u64),
}

#[derive(Debug, Clone)]
pub struct Op {
    pub id: OpId,
    pub client: usize,
    pub invoked: u64,
    /// `None` means the operation never returned — the client crashed, or timed
    /// out. Such an operation may or may not have taken effect, and a correct
    /// checker has to allow both.
    pub returned: Option<u64>,
    pub kind: Kind,
}

#[derive(Debug, Clone)]
pub struct History {
    pub ops: Vec<Op>,
    pub initial: u64,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Verdict {
    Linearizable {
        /// One witnessing order, for the report.
        order: Vec<OpId>,
    },
    NotLinearizable {
        /// The operation whose result cannot be explained under any legal
        /// ordering, chosen as the earliest such by completion time. This is
        /// what a human actually wants to see.
        culprit: OpId,
        explanation: String,
    },
}

impl History {
    pub fn new(initial: u64) -> Self {
        History {
            ops: Vec::new(),
            initial,
        }
    }

    pub fn completed(&self) -> usize {
        self.ops.iter().filter(|o| o.returned.is_some()).count()
    }

    /// Checks the history. Returns a witnessing order on success.
    pub fn check(&self) -> Verdict {
        assert!(
            self.ops.len() <= 60,
            "the bitmask memo holds 60 operations; \
             shrink the workload rather than weakening the checker"
        );
        let n = self.ops.len();
        let required: u64 = self
            .ops
            .iter()
            .enumerate()
            .filter(|(_, o)| o.returned.is_some())
            .map(|(i, _)| 1u64 << i)
            .fold(0, |a, b| a | b);

        let mut memo: HashMap<(u64, u64), bool> = HashMap::new();
        let mut order: Vec<OpId> = Vec::with_capacity(n);
        if self.search(0, self.initial, required, &mut memo, &mut order) {
            return Verdict::Linearizable { order };
        }

        // No legal order exists. Find the earliest-completing read that cannot
        // be explained, which is nearly always the useful one to report.
        let culprit = self.blame();
        let explanation = self.explain(culprit);
        Verdict::NotLinearizable {
            culprit,
            explanation,
        }
    }

    /// `linearized` is a bitmask over `self.ops` indices.
    fn search(
        &self,
        linearized: u64,
        value: u64,
        required: u64,
        memo: &mut HashMap<(u64, u64), bool>,
        order: &mut Vec<OpId>,
    ) -> bool {
        if linearized & required == required {
            return true;
        }
        if let Some(&known) = memo.get(&(linearized, value)) {
            if !known {
                return false;
            }
        }

        for i in 0..self.ops.len() {
            let bit = 1u64 << i;
            if linearized & bit != 0 {
                continue;
            }
            if !self.is_minimal(i, linearized) {
                continue;
            }
            let next = match self.ops[i].kind {
                Kind::Write(v) => v,
                Kind::Read(v) => {
                    if v != value {
                        continue;
                    }
                    value
                }
            };
            order.push(self.ops[i].id);
            if self.search(linearized | bit, next, required, memo, order) {
                return true;
            }
            order.pop();
        }

        memo.insert((linearized, value), false);
        false
    }

    /// An operation can be linearized next only if nothing that must precede it
    /// is still outstanding: no unlinearized `j` completed before `i` started.
    fn is_minimal(&self, i: usize, linearized: u64) -> bool {
        let inv = self.ops[i].invoked;
        for (j, oj) in self.ops.iter().enumerate() {
            if j == i || linearized & (1u64 << j) != 0 {
                continue;
            }
            if let Some(ret) = oj.returned {
                if ret < inv {
                    return false;
                }
            }
        }
        true
    }

    /// Finds a read that no ordering can justify. We do this by re-running the
    /// search with each completed read excluded in turn; if the history becomes
    /// linearizable without it, that read is the one that broke it.
    ///
    /// Candidates are tried latest-completing first. In the failure mode this
    /// harness exists to find, an earlier read observes a new value and a later
    /// read observes an older one; both removals repair the history, but the
    /// *later* read is the one that went backwards, and that is the one an
    /// engineer needs to look at.
    fn blame(&self) -> OpId {
        let mut candidates: Vec<usize> = (0..self.ops.len())
            .filter(|&i| {
                self.ops[i].returned.is_some() && matches!(self.ops[i].kind, Kind::Read(_))
            })
            .collect();
        candidates.sort_by_key(|&i| std::cmp::Reverse(self.ops[i].returned.unwrap_or(0)));

        for &i in &candidates {
            let required: u64 = self
                .ops
                .iter()
                .enumerate()
                .filter(|(j, o)| *j != i && o.returned.is_some())
                .map(|(j, _)| 1u64 << j)
                .fold(0, |a, b| a | b);
            let mut memo = HashMap::new();
            let mut order = Vec::new();
            // Mark the excluded op as already linearized so `is_minimal` stops
            // treating it as an ordering constraint on everything after it.
            if self.search(1u64 << i, self.initial, required, &mut memo, &mut order) {
                return self.ops[i].id;
            }
        }
        candidates
            .last()
            .map(|&i| self.ops[i].id)
            .unwrap_or(self.ops.first().map(|o| o.id).unwrap_or(0))
    }

    fn explain(&self, culprit: OpId) -> String {
        let Some(op) = self.ops.iter().find(|o| o.id == culprit) else {
            return "no witnessing order exists".into();
        };
        let read_value = match op.kind {
            Kind::Read(v) => v,
            Kind::Write(v) => {
                return format!("write({v}) by client {} cannot be placed", op.client)
            }
        };
        // Which writes had already completed when this read was invoked?
        let mut settled: Vec<u64> = self
            .ops
            .iter()
            .filter(|o| matches!(o.kind, Kind::Write(_)))
            .filter(|o| o.returned.map(|r| r < op.invoked).unwrap_or(false))
            .map(|o| match o.kind {
                Kind::Write(v) => v,
                _ => unreachable!(),
            })
            .collect();
        settled.sort_unstable();
        format!(
            "read by client {} returned {read_value} at t={}, but by the time it was \
             invoked (t={}) the completed writes were {:?}; no total order over the \
             remaining operations makes that value current",
            op.client,
            op.returned.unwrap_or(0),
            op.invoked,
            settled
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn h(initial: u64, ops: Vec<(usize, u64, Option<u64>, Kind)>) -> History {
        History {
            initial,
            ops: ops
                .into_iter()
                .enumerate()
                .map(|(id, (client, invoked, returned, kind))| Op {
                    id,
                    client,
                    invoked,
                    returned,
                    kind,
                })
                .collect(),
        }
    }

    #[test]
    fn empty_history_is_linearizable() {
        assert!(matches!(
            h(0, vec![]).check(),
            Verdict::Linearizable { .. }
        ));
    }

    #[test]
    fn sequential_write_then_read_is_fine() {
        let hist = h(
            0,
            vec![
                (0, 10, Some(20), Kind::Write(7)),
                (0, 30, Some(40), Kind::Read(7)),
            ],
        );
        assert!(matches!(hist.check(), Verdict::Linearizable { .. }));
    }

    #[test]
    fn reading_a_stale_value_after_a_completed_write_is_a_violation() {
        let hist = h(
            0,
            vec![
                (0, 10, Some(20), Kind::Write(7)),
                (1, 30, Some(40), Kind::Read(0)),
            ],
        );
        match hist.check() {
            Verdict::NotLinearizable { culprit, .. } => assert_eq!(culprit, 1),
            v => panic!("expected a violation, got {v:?}"),
        }
    }

    /// The bug this whole repository exists to catch: two reads that overlap a
    /// write, where the second read goes backwards in time.
    #[test]
    fn non_monotonic_concurrent_reads_are_a_violation() {
        let hist = h(
            0,
            vec![
                (0, 10, Some(100), Kind::Write(7)), // concurrent with both reads
                (1, 20, Some(40), Kind::Read(7)),   // sees the new value
                (2, 50, Some(70), Kind::Read(0)),   // sees the old value, later
            ],
        );
        match hist.check() {
            Verdict::NotLinearizable { culprit, .. } => assert_eq!(culprit, 2),
            v => panic!("expected a violation, got {v:?}"),
        }
    }

    #[test]
    fn concurrent_reads_in_the_other_order_are_fine() {
        let hist = h(
            0,
            vec![
                (0, 10, Some(100), Kind::Write(7)),
                (1, 20, Some(40), Kind::Read(0)),
                (2, 50, Some(70), Kind::Read(7)),
            ],
        );
        assert!(matches!(hist.check(), Verdict::Linearizable { .. }));
    }

    #[test]
    fn a_pending_write_may_be_treated_as_never_having_happened() {
        let hist = h(
            0,
            vec![
                (0, 10, None, Kind::Write(7)), // never returned
                (1, 20, Some(30), Kind::Read(0)),
            ],
        );
        assert!(matches!(hist.check(), Verdict::Linearizable { .. }));
    }

    #[test]
    fn a_pending_write_may_also_be_treated_as_having_happened() {
        let hist = h(
            0,
            vec![
                (0, 10, None, Kind::Write(7)),
                (1, 20, Some(30), Kind::Read(7)),
            ],
        );
        assert!(matches!(hist.check(), Verdict::Linearizable { .. }));
    }

    #[test]
    fn overlapping_writes_permit_either_outcome() {
        for observed in [7u64, 9] {
            let hist = h(
                0,
                vec![
                    (0, 10, Some(50), Kind::Write(7)),
                    (1, 20, Some(60), Kind::Write(9)),
                    (2, 70, Some(80), Kind::Read(observed)),
                ],
            );
            assert!(
                matches!(hist.check(), Verdict::Linearizable { .. }),
                "observed {observed} should be explainable"
            );
        }
    }

    #[test]
    fn a_value_nobody_ever_wrote_is_a_violation() {
        let hist = h(
            0,
            vec![
                (0, 10, Some(20), Kind::Write(7)),
                (1, 30, Some(40), Kind::Read(42)),
            ],
        );
        assert!(matches!(hist.check(), Verdict::NotLinearizable { .. }));
    }

    #[test]
    fn the_witness_order_respects_real_time_precedence() {
        let hist = h(
            0,
            vec![
                (0, 10, Some(20), Kind::Write(1)),
                (0, 30, Some(40), Kind::Write(2)),
                (0, 50, Some(60), Kind::Read(2)),
            ],
        );
        match hist.check() {
            Verdict::Linearizable { order } => assert_eq!(order, vec![0, 1, 2]),
            v => panic!("expected linearizable, got {v:?}"),
        }
    }

    #[test]
    fn explanation_names_the_reader_and_the_value() {
        let hist = h(
            0,
            vec![
                (0, 10, Some(20), Kind::Write(7)),
                (3, 30, Some(40), Kind::Read(0)),
            ],
        );
        match hist.check() {
            Verdict::NotLinearizable { explanation, .. } => {
                assert!(explanation.contains("client 3"), "{explanation}");
                assert!(explanation.contains("returned 0"), "{explanation}");
            }
            v => panic!("expected a violation, got {v:?}"),
        }
    }
}
