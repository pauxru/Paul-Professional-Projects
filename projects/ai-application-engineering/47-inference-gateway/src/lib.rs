//! A discrete-event simulator for an LLM inference gateway.
//!
//! The argument this crate exists to make: **serving latency is a queueing
//! problem before it is a capacity problem.** Teams reach for more replicas
//! because latency is bad, but at high utilisation the relationship between
//! load and delay is hyperbolic, not linear -- so the marginal replica buys
//! progressively less, while the marginal *rejected* request buys a great
//! deal. Section 7 of the report quantifies that: at 95% utilisation,
//! deliberately shedding 5% of offered load beats doubling the fleet.
//!
//! No dependencies. The RNG, the event loop, the percentile estimator and
//! the report writer are all in here, because a benchmark you cannot read
//! end to end is a benchmark you cannot trust.

pub mod capacity;
pub mod engine;
pub mod experiments;
pub mod metrics;
pub mod report;
pub mod rng;
pub mod sched;
pub mod sim;
pub mod workload;

pub use engine::{Completed, EngineConfig, Replica};
pub use experiments::report_main;
pub use metrics::{fmt_us, Latencies, Little, RunStats};
pub use rng::Rng;
pub use sched::{Admission, Policy, Queued, Scheduler};
pub use sim::{run, run_requests, run_static_batching, GatewayConfig};
pub use workload::{Estimate, Estimator, Kind, Request, Tenant, Workload};
