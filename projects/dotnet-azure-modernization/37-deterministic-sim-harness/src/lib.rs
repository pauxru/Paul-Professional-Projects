//! A FoundationDB-style deterministic simulation harness, and a replicated
//! register to point it at.
//!
//! The claim being tested is narrow and checkable: *any* failure this harness
//! finds can be replayed exactly from its seed. Everything else — the fault
//! model, the protocol, the linearizability checker — exists to make that claim
//! worth something.

pub mod abd;
pub mod linearizability;
pub mod rng;
pub mod runner;
pub mod sim;

pub use runner::{fuzz, report, run_one, shrink, FuzzSummary, RunResult};
