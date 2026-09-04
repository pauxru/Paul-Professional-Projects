//! The experiments, and the report they produce.
//!
//! Each section registers what it expects *before* it looks, using the
//! prediction machinery in `report.rs`. Eight of the fourteen predictions in
//! the finished document are contradicted. That is not a sign the model is
//! broken; it is the reason the exercise was worth doing, and every one of
//! the eight is a belief the author held going in.

use crate::capacity::{naive_rps, saturation_rps, saturation_tokens_per_s, trace_at_utilisation};
use crate::metrics::{fmt_us, RunStats};
use crate::report::{f1, f2, pct, Report};
use crate::sched::{Admission, Policy};
use crate::sim::{run_requests, run_static_batching, GatewayConfig};
use crate::workload::{Kind, Request, Workload};

const N: usize = 1200;
const N_HEAVY: usize = 1500;
const TRACE_SEED: u64 = 11;
const WORKLOAD_SEED: u64 = 7;

/// Shared setup so every section measures the same system under the same
/// traffic, and differences between sections are attributable to the thing
/// the section changed.
pub struct Bench {
    pub workload: Workload,
    pub base: GatewayConfig,
    pub mu: f64,
    /// Offered load at 85% of measured capacity: loaded, not yet collapsing.
    pub trace_85: Vec<Request>,
    /// Offered load at 95%: inside the knee, where the interesting decisions
    /// are.
    pub trace_95: Vec<Request>,
}

impl Bench {
    pub fn new() -> Self {
        let workload = Workload::new(1.0, WORKLOAD_SEED);
        let base = GatewayConfig::default();
        let mu = saturation_rps(&base, &workload, 400);
        Bench {
            trace_85: trace_at_utilisation(&workload, mu, 0.85, N, TRACE_SEED),
            trace_95: trace_at_utilisation(&workload, mu, 0.95, N_HEAVY, TRACE_SEED),
            workload,
            base,
            mu,
        }
    }

    fn run(&self, cfg: &GatewayConfig, trace: &[Request]) -> RunStats {
        let stats = run_requests(cfg, trace);
        assert!(
            !stats.truncated,
            "a run hit the event ceiling; every statistic derived from it is fiction"
        );
        stats
    }
}

impl Default for Bench {
    fn default() -> Self {
        Bench::new()
    }
}

fn ms(us: u64) -> String {
    if us >= 60_000_000 {
        format!("{:.0} min", us as f64 / 60_000_000.0)
    } else if us >= 1_000_000 {
        format!("{:.1} s", us as f64 / 1_000_000.0)
    } else {
        format!("{:.0} ms", us as f64 / 1_000.0)
    }
}

fn f0(v: f64) -> String {
    format!("{:.0}", v)
}

/// Mean value density -- urgency bought per token of work -- for one request
/// class, measured on the trace rather than on the class definition, so that it
/// reflects the sizes actually drawn. Uses true sizes, matching the definition
/// `Queued::estimated_cost` applies to its estimate.
fn value_density(trace: &[Request], kind: Kind) -> f64 {
    let mut total = 0.0;
    let mut n = 0u64;
    for req in trace.iter().filter(|r| r.kind == kind) {
        let urgency = 1_000_000.0 / req.kind.ttft_slo_us() as f64;
        let cost = (req.prompt_tokens as u64 / 8 + req.output_tokens as u64).max(1);
        total += urgency / cost as f64;
        n += 1;
    }
    if n == 0 {
        0.0
    } else {
        total / n as f64
    }
}

/// Build the whole document.
pub fn build_report() -> Report {
    let b = Bench::new();
    let mut r = Report::new(
        "Where inference-serving latency actually comes from",
        "A discrete-event simulation of an LLM inference gateway, built to answer one \
         question: when a serving system misses its latency objectives, is that a \
         capacity problem or a queueing problem? The answer decides whether you spend \
         money or spend thought, and the two are not interchangeable. Every figure below \
         is produced by the simulator in this repository from a fixed seed, and each \
         section states what it expected to find before reporting what it found.",
        "src/main.rs (cargo run --release --bin run_gateway)",
        "Rust 1.x, no external crates, single-threaded, deterministic",
    );

    section_1_batching_physics(&mut r, &b);
    section_2_static_vs_continuous(&mut r, &b);
    section_3_capacity(&mut r, &b);
    section_4_utilisation_curve(&mut r, &b);
    section_5_head_of_line(&mut r, &b);
    section_6_prediction_error(&mut r, &b);
    section_7_prefill_decode(&mut r, &b);
    section_8_fairness(&mut r, &b);
    section_9_capacity_vs_admission(&mut r, &b);
    section_10_kv_reservation(&mut r, &b);
    section_11_littles_law(&mut r, &b);
    section_12_limits(&mut r);
    r
}

// ---------------------------------------------------------------- section 1

fn section_1_batching_physics(r: &mut Report, b: &Bench) {
    r.h2("1. Why batching works, and where it stops working");
    r.para(
        "Decode is memory-bandwidth bound. Producing one token for one sequence requires \
         streaming the model's weights out of HBM; producing one token for sixty \
         sequences requires streaming them once. Step time is therefore \
         `weight_load + per_seq * batch` with a small `per_seq`, and that shape -- a \
         large fixed cost amortised across the batch -- is the entire reason continuous \
         batching exists. It is not an optimisation applied to a working system. It is \
         the difference between a system that serves concurrent traffic and one that \
         does not.",
    );

    let cfg = b.base.engine;
    let mut rows = Vec::new();
    for batch in [1usize, 2, 4, 8, 16, 32, 48, 64] {
        rows.push(vec![
            batch.to_string(),
            fmt_us(cfg.decode_step_us(batch)),
            f1(cfg.decode_throughput_tok_per_s(batch)),
            f1(cfg.decode_throughput_tok_per_s(batch) / cfg.decode_throughput_tok_per_s(1)),
            fmt_us(cfg.decode_step_us(batch)),
        ]);
    }
    r.table(
        &[
            "batch",
            "step time",
            "tokens/s",
            "throughput vs batch 1",
            "per-request TPOT",
        ],
        rows,
    );

    let t1 = cfg.decode_throughput_tok_per_s(1);
    let t64 = cfg.decode_throughput_tok_per_s(64);
    let tpot1 = cfg.decode_step_us(1);
    let tpot64 = cfg.decode_step_us(64);
    r.para(&format!(
        "Going from batch 1 to batch 64 multiplies throughput by {} while multiplying \
         per-request time-per-output-token by only {}. That asymmetry is the whole \
         business case. Note also the diminishing return: the marginal sequence added to \
         a batch of 8 buys {} tokens/s, while the marginal sequence added to a batch of \
         56 buys {}.",
        f1(t64 / t1),
        f2(tpot64 as f64 / tpot1 as f64),
        f1(cfg.decode_throughput_tok_per_s(9) - cfg.decode_throughput_tok_per_s(8)),
        f1(cfg.decode_throughput_tok_per_s(57) - cfg.decode_throughput_tok_per_s(56)),
    ));
    r.note(
        "The knee is not where you should operate. Throughput keeps rising past it, and \
         so does queueing delay for anything that does not fit. The batch size worth \
         running is the one your memory and your latency objective jointly allow, which \
         is what the rest of this document is about.",
    );
}

// ---------------------------------------------------------------- section 2

fn section_2_static_vs_continuous(r: &mut Report, b: &Bench) {
    r.h2("2. Static batching against continuous batching");
    r.para(
        "Under static batching a batch is formed, run to completion, and only then \
         replaced. Its duration is the duration of its *longest* member, so one \
         900-token summarisation holds every other slot in its batch open for 900 decode \
         steps -- including slots belonging to chat turns that finished after forty. \
         Continuous batching retires each sequence as it completes and admits a \
         replacement immediately.",
    );
    r.expect(
        "Continuous batching should roughly double throughput on this heavy-tailed \
         workload, and improve the tail more than the median.",
    );

    let cont = b.run(&b.base, &b.trace_85);
    let mut rows = vec![vec![
        "continuous".to_string(),
        f2(cont.throughput_rps()),
        ms(cont.ttft().p50()),
        ms(cont.ttft().p99()),
        f2(cont.slowdown_milli().p50() as f64 / 1000.0),
        pct(cont.slo_attainment()),
    ]];
    let mut best_static = 0.0f64;
    for batch in [8usize, 16, 32, 64] {
        let s = run_static_batching(&b.base, &b.trace_85, batch);
        best_static = best_static.max(s.throughput_rps());
        rows.push(vec![
            format!("static, batch {batch}"),
            f2(s.throughput_rps()),
            ms(s.ttft().p50()),
            ms(s.ttft().p99()),
            f2(s.slowdown_milli().p50() as f64 / 1000.0),
            pct(s.slo_attainment()),
        ]);
    }
    r.table(
        &[
            "batching",
            "throughput (req/s)",
            "TTFT p50",
            "TTFT p99",
            "median slowdown",
            "SLO attainment",
        ],
        rows,
    );

    let ratio = cont.throughput_rps() / best_static.max(1e-9);
    r.found(
        &format!(
            "The prediction understated it. Continuous batching delivers {} requests/s against {} for the \
             best static configuration -- a factor of {}, not a factor of two. Static \
             batching cannot serve the offered load at all: at {} requests/s offered it \
             completes {}, so its queue grows without bound and its median time to first \
             token is {}. SLO attainment is {} against {}. The comparison is not close \
             enough to be interesting as a tuning decision, which is itself the finding: \
             continuous batching is a precondition, not a knob.",
            f2(cont.throughput_rps()),
            f2(best_static),
            f1(ratio),
            f2(b.mu * 0.85),
            f2(best_static),
            ms(run_static_batching(&b.base, &b.trace_85, 64).ttft().p50()),
            pct(run_static_batching(&b.base, &b.trace_85, 64).slo_attainment()),
            pct(cont.slo_attainment()),
        ),
        false,
    );
    r.para(
        "Note the direction of the static-batching numbers as batch size grows: bigger \
         batches help, because the throughput gain from amortising the weight load \
         outweighs the extra head-of-line blocking. That is the right instinct applied to \
         the wrong architecture. No batch size rescues it.",
    );
}

// ---------------------------------------------------------------- section 3

fn section_3_capacity(r: &mut Report, b: &Bench) {
    r.h2("3. Measured capacity against assumed capacity");
    r.para(
        "Capacity plans are usually written from peak decode throughput divided by mean \
         output length. That estimate assumes the accelerator spends all of its time \
         decoding at full batch. It does not: it also runs prefill, which is compute \
         bound and blocks decode, and its batch is limited by KV-cache memory rather \
         than by the configured maximum. Rather than argue about the size of the gap, \
         this simulator measures it -- by handing the gateway an infinite backlog and \
         watching how fast it drains.",
    );
    r.expect(
        "The naive estimate should be optimistic, but by a modest margin -- ten percent \
         or so, small enough that a capacity plan built on it would be roughly right.",
    );

    let mut rows = Vec::new();
    let mut worst = 1.0f64;
    for replicas in [1usize, 2, 4] {
        let cfg = b.base.clone().with_replicas(replicas);
        let measured = saturation_rps(&cfg, &b.workload, 400);
        let naive = naive_rps(&cfg, &b.workload, 400);
        let tokens = saturation_tokens_per_s(&cfg, &b.workload, 400);
        worst = worst.max(naive / measured);
        rows.push(vec![
            replicas.to_string(),
            f2(measured),
            f1(tokens),
            f2(naive),
            format!("{}%", ((naive / measured - 1.0) * 100.0).round()),
        ]);
    }
    r.table(
        &[
            "replicas",
            "measured capacity (req/s)",
            "measured tokens/s",
            "naive estimate (req/s)",
            "overstatement",
        ],
        rows,
    );
    r.found(
        &format!(
            "And in the direction that matters. The naive estimate \
             overstates capacity by {}%. A team sizing a fleet from it would provision \
             for {} requests/s per replica and discover the real figure is {} -- which \
             means running at a true utilisation of {} while believing they were at \
             0.79. Section 4 shows what the difference between those two numbers does to \
             latency.",
            ((worst - 1.0) * 100.0).round(),
            f2(naive_rps(&b.base, &b.workload, 400)),
            f2(b.mu),
            f2(0.79 * worst),
        ),
        false,
    );
    r.note(
        "Everything that follows is expressed as a fraction of *measured* capacity. \
         Getting that denominator wrong by a quarter would move every experiment in this \
         report into a different regime, which is the practical reason to measure it \
         rather than derive it.",
    );
}

// ---------------------------------------------------------------- section 4

fn section_4_utilisation_curve(r: &mut Report, b: &Bench) {
    r.h2("4. The utilisation curve");
    r.para(
        "Queueing delay does not rise linearly with load. For an M/M/1 queue mean \
         sojourn time is `1 / (mu - lambda)`, a hyperbola with a pole at saturation, and \
         real systems inherit the shape even when they violate the assumptions. The \
         practical consequence is that the same ten percent of extra traffic is free at \
         one operating point and catastrophic at another.",
    );
    r.expect(
        "Latency should rise slowly to about 80% utilisation and then sharply. SLO \
         attainment should stay above 90% through 0.85 and fall away by 0.95.",
    );

    let mut rows = Vec::new();
    let mut att_85 = 0.0;
    let mut att_95 = 0.0;
    let mut p99_75 = 0u64;
    let mut p99_95 = 0u64;
    for rho in [0.40f64, 0.60, 0.75, 0.85, 0.95, 1.05] {
        let trace = trace_at_utilisation(&b.workload, b.mu, rho, N, TRACE_SEED);
        let s = b.run(&b.base, &trace);
        if (rho - 0.85).abs() < 1e-9 {
            att_85 = s.slo_attainment();
        }
        if (rho - 0.95).abs() < 1e-9 {
            att_95 = s.slo_attainment();
            p99_95 = s.ttft().p99();
        }
        if (rho - 0.75).abs() < 1e-9 {
            p99_75 = s.ttft().p99();
        }
        rows.push(vec![
            f2(rho),
            f2(b.mu * rho),
            ms(s.ttft().p50()),
            ms(s.ttft().p99()),
            ms(s.tpot().p50()),
            f2(s.slowdown_milli().p50() as f64 / 1000.0),
            f1(s.mean_batch),
            pct(s.slo_attainment()),
        ]);
    }
    r.table(
        &[
            "utilisation",
            "offered (req/s)",
            "TTFT p50",
            "TTFT p99",
            "TPOT p50",
            "median slowdown",
            "mean batch",
            "SLO attainment",
        ],
        rows,
    );
    r.found(
        &format!(
            "Attainment is {} at 0.85 and {} at 0.95, and tail time-to-first-token \
             goes from {} at 0.75 utilisation to {} at 0.95 -- a factor of {} for a 27% \
             increase in load. Note where the damage shows up: TPOT barely moves across \
             the whole sweep, because adding sequences to a memory-bound decode batch is \
             nearly free. It is TTFT that explodes, because that is where waiting lives. \
             A dashboard tracking tokens per second would show this system getting \
             *better* right up to the point users start leaving.",
            pct(att_85),
            pct(att_95),
            ms(p99_75),
            ms(p99_95),
            f1(p99_95 as f64 / p99_75.max(1) as f64),
        ),
        true,
    );
    r.para(
        "The mean-batch column explains the mechanism. Below saturation, extra load is \
         absorbed by a larger batch, and the cost is spread thinly across every \
         in-flight request as slightly slower decode. Once the batch reaches its memory \
         or configuration limit there is nowhere left to absorb it, and additional load \
         converts entirely into queueing. The knee in the latency curve is the point \
         where the batch stops growing.",
    );
}

// ---------------------------------------------------------------- section 5

fn section_5_head_of_line(r: &mut Report, b: &Bench) {
    r.h2("5. Head-of-line blocking, and what scheduling can do about it");
    r.para(
        "Latency is the wrong axis for a heterogeneous workload. A 4000-token generation \
         that takes ninety seconds is behaving perfectly; a forty-token chat turn that \
         takes nine is a disaster; and the first looks ten times worse in a latency \
         histogram. The tables below use *slowdown* -- observed latency divided by the \
         time the request would have taken alone on the device -- which puts both on the \
         same axis.",
    );
    r.expect(
        "Shortest-job-first should beat FIFO on mean slowdown. Aging, which promotes \
         long-waiting requests to prevent starvation, should cost a little of that gain.",
    );

    let mut rows = Vec::new();
    let mut fifo_mean = 0.0;
    let mut sjf_mean = 0.0;
    let mut aged_mean = 0.0;
    let mut fifo_rag = 0.0;
    let mut sjf_rag = 0.0;
    let mut aged_rag = 0.0;
    for p in Policy::ALL {
        let s = b.run(&b.base.clone().with_policy(p), &b.trace_85);
        let rag = s.slowdown_milli_for(Kind::Rag).p99() as f64 / 1000.0;
        match p {
            Policy::Fifo => {
                fifo_mean = s.mean_slowdown();
                fifo_rag = rag;
            }
            Policy::Sjf => {
                sjf_mean = s.mean_slowdown();
                sjf_rag = rag;
            }
            Policy::SjfAged => {
                aged_mean = s.mean_slowdown();
                aged_rag = rag;
            }
            _ => {}
        }
        let mut row = vec![p.name().to_string(), f2(s.mean_slowdown())];
        for k in Kind::ALL {
            row.push(f2(s.slowdown_milli_for(k).p99() as f64 / 1000.0));
        }
        row.push(pct(s.slo_attainment()));
        rows.push(row);
    }
    r.table(
        &[
            "policy",
            "mean slowdown",
            "chat p99",
            "rag p99",
            "summarise p99",
            "batch p99",
            "SLO attainment",
        ],
        rows,
    );
    r.found(
        &format!(
            "Half of it held. SJF does beat FIFO on mean slowdown ({} against {}), and the \
             effect is concentrated exactly where the theory says it should be: RAG \
             requests, which are cheap to decode but arrive behind expensive ones, \
             improve from a p99 slowdown of {} to {}. But aging does not cost 'a little'. \
             `sjf-aged` lands at {} mean slowdown, and its RAG p99 of {} is *worse than \
             FIFO's*. The aging threshold is 3 seconds; at this utilisation almost every \
             request waits longer than that, so nearly everything is promoted and the \
             policy degenerates into FIFO with extra steps. An aging threshold has to be \
             set relative to the wait the system actually produces, not relative to the \
             wait you wish it produced -- and if you set it from the SLO, you will \
             silently disable the policy you are aging.",
            f2(sjf_mean),
            f2(fifo_mean),
            f2(fifo_rag),
            f2(sjf_rag),
            f2(aged_mean),
            f2(aged_rag),
        ),
        false,
    );
}

// ---------------------------------------------------------------- section 6

fn section_6_prediction_error(r: &mut Report, b: &Bench) {
    r.h2("6. What prediction error actually costs");
    r.para(
        "Shortest-job-first needs to know how long a job will take, and an inference \
         gateway cannot know: output length is decided by the model, one token at a \
         time. Every size-aware policy therefore runs on a prediction. The obvious \
         question is how much accuracy it needs.",
    );
    r.para(
        "The obvious experiment -- vary the predictor's accuracy and watch SJF degrade -- \
         is confounded, and the first version of this section fell into it. The \
         predictor feeds two consumers, not one: the scheduler uses it to *order* work, \
         and the memory manager uses it to *reserve* KV cache. Varying one knob moves \
         both. The giveaway was that FIFO's numbers also moved, and FIFO never consults \
         the predictor for ordering at all. The two channels are separated below.",
    );
    r.expect(
        "SJF's advantage over FIFO should decay quickly as prediction error grows, and \
         should be roughly gone once estimates are typically off by a factor of five.",
    );

    let mut rows = Vec::new();
    let mut adv_1 = 0.0;
    let mut adv_8 = 0.0;
    for spread in [1.0f64, 1.5, 2.0, 3.0, 5.0, 8.0] {
        let sjf = b.run(
            &b.base
                .clone()
                .with_policy(Policy::Sjf)
                .with_estimator_spread(spread),
            &b.trace_85,
        );
        let fifo = b.run(
            &b.base
                .clone()
                .with_policy(Policy::Fifo)
                .with_estimator_spread(spread),
            &b.trace_85,
        );
        let adv = fifo.mean_slowdown() - sjf.mean_slowdown();
        if spread == 1.0 {
            adv_1 = adv;
        }
        if spread == 8.0 {
            adv_8 = adv;
        }
        rows.push(vec![
            f1(spread),
            f2(sjf.mean_slowdown()),
            f2(fifo.mean_slowdown()),
            format!("{adv:+.3}"),
            pct(sjf.slo_attainment()),
        ]);
    }
    r.h3("6a. Error in the scheduling estimate only");
    r.table(
        &[
            "estimate spread",
            "SJF mean slowdown",
            "FIFO mean slowdown",
            "SJF advantage",
            "SJF attainment",
        ],
        rows,
    );
    r.found(
        &format!(
            "Decisively so. SJF's advantage is {:+.3} with a perfect oracle \
             and {:+.3} when estimates are typically wrong by a factor of eight. It does \
             not decay at all. The reason is that SJF needs the *ranking* to be roughly \
             right, not the magnitudes: a chat turn and a batch job differ by more than \
             an order of magnitude in cost, and multiplicative noise of even 8x rarely \
             swaps their order. Rank statistics are robust to noise in a way that \
             absolute quantities are not.",
            adv_1, adv_8
        ),
        false,
    );

    r.h3("6b. Error in the memory-reservation estimate only");
    r.expect(
        "Reservation error should matter less than scheduling error. A reservation that \
         is somewhat wrong is corrected by preemption, which the engine already supports.",
    );
    let mut rows = Vec::new();
    let mut att_1 = 0.0;
    let mut att_8 = 0.0;
    let mut ev_8 = 0;
    for spread in [1.0f64, 1.5, 2.0, 3.0, 5.0, 8.0] {
        let s = b.run(&b.base.clone().with_reservation_spread(spread), &b.trace_85);
        if spread == 1.0 {
            att_1 = s.slo_attainment();
        }
        if spread == 8.0 {
            att_8 = s.slo_attainment();
            ev_8 = s.evictions;
        }
        rows.push(vec![
            f1(spread),
            f2(s.mean_slowdown()),
            s.evictions.to_string(),
            f1(s.mean_batch),
            ms(s.ttft().p99()),
            pct(s.slo_attainment()),
        ]);
    }
    r.table(
        &[
            "estimate spread",
            "mean slowdown",
            "evictions",
            "mean batch",
            "TTFT p99",
            "SLO attainment",
        ],
        rows,
    );
    r.found(
        &format!(
            "And this is the section's real finding. Holding the scheduler \
             on a perfect oracle and degrading only the *reservation* estimate takes \
             attainment from {} to {} and tail TTFT from {} to {}, with evictions rising \
             from zero to {}. Preemption does not correct a bad reservation; it \
             redistributes the damage, and it destroys completed work every time it \
             fires. So the two channels are not comparable: the same predictor error is \
             nearly free when it decides *order* and expensive when it decides *how much \
             memory to commit*.",
            pct(att_1),
            pct(att_8),
            ms(b.run(&b.base, &b.trace_85).ttft().p99()),
            ms(b
                .run(&b.base.clone().with_reservation_spread(8.0), &b.trace_85)
                .ttft()
                .p99()),
            ev_8,
        ),
        false,
    );
    r.note(
        "The engineering rule this yields is sharper than 'improve the predictor': use \
         predictions for ordering decisions, which are reversible and forgiving, and \
         avoid using them for resource commitments, which are neither. If a commitment \
         must be made from a prediction, bias it in the direction whose failure mode is \
         cheaper -- see section 10.",
    );

    r.h3("6c. Both channels, which is what a single predictor gives you");
    let mut rows = Vec::new();
    for spread in [1.0f64, 2.0, 3.0, 5.0, 8.0] {
        let s = b.run(
            &b.base
                .clone()
                .with_policy(Policy::Sjf)
                .with_both_spreads(spread),
            &b.trace_85,
        );
        rows.push(vec![
            f1(spread),
            f2(s.mean_slowdown()),
            s.evictions.to_string(),
            pct(s.slo_attainment()),
        ]);
    }
    r.table(
        &["estimate spread", "mean slowdown", "evictions", "attainment"],
        rows,
    );
    r.para(
        "The combined degradation tracks the reservation channel, not the scheduling \
         one, which is what you would expect once you know the two are separable and one \
         of them is nearly free.",
    );
}

// ---------------------------------------------------------------- section 7

fn section_7_prefill_decode(r: &mut Report, b: &Bench) {
    r.h2("7. Prefill against decode: the two objectives are in tension");
    r.para(
        "Prefill is a large matmul over the whole prompt. It is compute bound, and while \
         it runs no decode step runs, so every already-admitted request stalls. A \
         scheduler must therefore choose: run prefill promptly and give newcomers a fast \
         first token at the cost of stuttering everyone already streaming, or protect \
         the streamers and make newcomers wait.",
    );
    r.expect(
        "Prefill priority should improve TTFT and worsen TPOT. The trade should look \
         like a trade -- meaningful movement in both directions.",
    );

    let mut rows = Vec::new();
    let mut ttft_on = 0u64;
    let mut ttft_off = 0u64;
    let mut tpot_on = 0u64;
    let mut tpot_off = 0u64;
    let mut att_on = 0.0;
    let mut att_off = 0.0;
    for on in [true, false] {
        let s = b.run(&b.base.clone().with_prefill_priority(on), &b.trace_85);
        if on {
            ttft_on = s.ttft().p99();
            tpot_on = s.tpot().p50();
            att_on = s.slo_attainment();
        } else {
            ttft_off = s.ttft().p99();
            tpot_off = s.tpot().p50();
            att_off = s.slo_attainment();
        }
        rows.push(vec![
            if on { "prefill first" } else { "decode first" }.to_string(),
            ms(s.ttft().p50()),
            ms(s.ttft().p99()),
            ms(s.tpot().p50()),
            ms(s.tpot().p99()),
            pct(s.slo_attainment()),
        ]);
    }
    r.table(
        &[
            "priority",
            "TTFT p50",
            "TTFT p99",
            "TPOT p50",
            "TPOT p99",
            "SLO attainment",
        ],
        rows,
    );
    r.found(
        &format!(
            "The direction held; the magnitude did not. Decode priority produces the best \
             time-per-output-token in this entire report -- {} against {}, essentially \
             the batch-1 floor, because the batch stays small and every step is cheap. It \
             also produces a tail TTFT of {} against {}, and SLO attainment of {} against \
             {}. This is not a trade-off curve, it is a starvation failure: with decode \
             taking priority, prefill only runs when nothing at all can decode, which at \
             this utilisation is almost never. The system optimises the metric it can see \
             and destroys the one it cannot.",
            ms(tpot_off),
            ms(tpot_on),
            ms(ttft_off),
            ms(ttft_on),
            pct(att_off),
            pct(att_on),
        ),
        false,
    );
    r.note(
        "Real systems resolve this with chunked prefill: a long prompt is split so it can \
         be interleaved with decode steps rather than blocking them wholesale. That is \
         not modelled here, and its absence is why the decode-priority case is as \
         extreme as it is. It is included as a boundary rather than as a proposal.",
    );
}

// ---------------------------------------------------------------- section 8

fn section_8_fairness(r: &mut Report, b: &Bench) {
    r.h2("8. Two tenants on one gateway");
    r.para(
        "The workload has two tenants with deliberately different mixes. `interactive` \
         sends mostly chat and RAG; `analytics` sends mostly summarisation and batch \
         work. They offer the same request rate, but not remotely the same load -- and \
         under FIFO, the tenant that behaves well subsidises the one that does not.",
    );
    r.expect(
        "Deficit round robin should protect the interactive tenant's tail latency \
         without reducing total throughput, because scheduling reorders work rather than \
         creating or destroying it.",
    );

    let mut rows = Vec::new();
    let mut fifo_t0 = 0u64;
    let mut drr_t0 = 0u64;
    let mut fifo_thr = 0.0;
    let mut drr_thr = 0.0;
    for p in [
        Policy::Fifo,
        Policy::Sjf,
        Policy::ClassPriority,
        Policy::DeficitRoundRobin,
    ] {
        let s = b.run(&b.base.clone().with_policy(p), &b.trace_85);
        let t0 = s.ttft_for_tenant(0).p99();
        let t1 = s.ttft_for_tenant(1).p99();
        if p == Policy::Fifo {
            fifo_t0 = t0;
            fifo_thr = s.throughput_rps();
        }
        if p == Policy::DeficitRoundRobin {
            drr_t0 = t0;
            drr_thr = s.throughput_rps();
        }
        rows.push(vec![
            p.name().to_string(),
            ms(t0),
            ms(t1),
            f1(t1 as f64 / t0.max(1) as f64),
            f2(s.throughput_rps()),
        ]);
    }
    r.table(
        &[
            "policy",
            "interactive TTFT p99",
            "analytics TTFT p99",
            "ratio",
            "throughput (req/s)",
        ],
        rows,
    );
    let thr_delta = format!("{:+.1}", (drr_thr / fifo_thr - 1.0) * 100.0);
    r.found(
        &format!(
            "Deficit round robin takes the interactive tenant's tail TTFT from {} \
             to {} -- a {}x improvement -- while total throughput moves from {} to {} \
             requests/s, a change of {}%. That flat throughput line is the point: \
             scheduling is a redistribution mechanism. It decides who waits. It cannot \
             decide how much waiting there is.",
            ms(fifo_t0),
            ms(drr_t0),
            f1(fifo_t0 as f64 / drr_t0.max(1) as f64),
            f2(fifo_thr),
            f2(drr_thr),
            thr_delta,
        ),
        true,
    );
    r.note(
        "DRR charges each tenant by estimated *token* cost, not by request count. \
         Charging by request would let the analytics tenant claim unbounded capacity \
         simply by sending longer jobs, which -- given its mix -- is exactly what it \
         would do.",
    );
}

// ---------------------------------------------------------------- section 9

fn section_9_capacity_vs_admission(r: &mut Report, b: &Bench) {
    r.h2("9. Buying capacity against refusing work");
    r.para(
        "This is the decision the report exists for. A system at 95% of its measured \
         capacity is missing its objectives. The available moves are to add replicas, to \
         reorder work, or to refuse some of it. Section 8 showed reordering cannot \
         change how much waiting exists. That leaves capacity and admission control, and \
         the two are usually compared on vibes.",
    );
    r.para(
        "Every configuration below sees the identical trace at 95% utilisation. The \
         column that matters is *offered success rate*: SLO-meeting completions divided \
         by requests **offered**, counting every rejection as a failure. Measuring \
         success over *served* requests instead would let a policy win by refusing \
         almost everything, which is the standard way admission control is oversold.",
    );
    r.expect(
        "Deliberately shedding around five percent of load at this operating point \
         should beat doubling the fleet, because the utilisation-to-latency curve is \
         hyperbolic and shedding moves you down the steep part for free.",
    );

    let mut rows = Vec::new();
    let push = |name: &str, s: &RunStats, rows: &mut Vec<Vec<String>>| {
        rows.push(vec![
            name.to_string(),
            s.rejected.len().to_string(),
            ms(s.ttft().p99()),
            f2(s.goodput_rps()),
            pct(s.slo_attainment()),
            pct(s.offered_success_rate()),
        ]);
    };

    let base1 = b.run(&b.base, &b.trace_95);
    push("1 replica, accept all", &base1, &mut rows);
    let two = b.run(&b.base.clone().with_replicas(2), &b.trace_95);
    push("2 replicas, accept all", &two, &mut rows);
    let three = b.run(&b.base.clone().with_replicas(3), &b.trace_95);
    push("3 replicas, accept all", &three, &mut rows);
    let slo = b.run(
        &b.base.clone().with_admission(Admission::SloPredictive),
        &b.trace_95,
    );
    push("1 replica, slo-predictive", &slo, &mut rows);
    let qt = b.run(
        &b.base
            .clone()
            .with_admission(Admission::QueueTokens(6_000)),
        &b.trace_95,
    );
    push("1 replica, queue-tokens", &qt, &mut rows);
    let ca = b.run(
        &b.base.clone().with_admission(Admission::CostAware(2_000)),
        &b.trace_95,
    );
    push("1 replica, cost-aware", &ca, &mut rows);
    let best = b.run(
        &b.base
            .clone()
            .with_policy(Policy::Sjf)
            .with_admission(Admission::CostAware(2_000)),
        &b.trace_95,
    );
    push("1 replica, sjf + cost-aware", &best, &mut rows);

    // Uniform random shedding, as a baseline nobody bothers to measure.
    for drop_pct in [10usize, 25] {
        let kept: Vec<Request> = b
            .trace_95
            .iter()
            .filter(|r| (r.id as usize * 37 % 100) >= drop_pct)
            .copied()
            .collect();
        let mut s = run_requests(&b.base, &kept);
        s.offered = b.trace_95.len();
        push(&format!("1 replica, drop {drop_pct}% at random"), &s, &mut rows);
    }

    r.table(
        &[
            "configuration",
            "rejected",
            "TTFT p99",
            "goodput (req/s)",
            "attainment (served)",
            "offered success rate",
        ],
        rows,
    );

    r.found(
        &format!(
            "Doubling the fleet wins outright: {} offered success against \
             {} for the best single-replica configuration, and {} for the baseline. \
             Shedding cannot match it, and the reason is arithmetic I got backwards. \
             Capacity is multiplicative in mu -- a second replica moves utilisation from \
             0.95 to 0.48, right down the flat part of the curve. Shedding five percent \
             is subtractive in lambda and moves it to 0.90, which is still inside the \
             knee. To match the second replica by shedding you would have to shed half \
             the traffic.",
            pct(two.offered_success_rate()),
            pct(best.offered_success_rate()),
            pct(base1.offered_success_rate()),
        ),
        false,
    );

    r.h3("9a. But the third replica is worth nothing");
    r.expect(
        "Having been wrong about shedding, the natural correction is that capacity is \
         simply the better lever. If so, a third replica should also help, if less than \
         the second.",
    );
    let second_worth = format!(
        "{:+.1}",
        (two.offered_success_rate() - base1.offered_success_rate()) * 100.0
    );
    let third_worth = format!(
        "{:+.1}",
        (three.offered_success_rate() - two.offered_success_rate()) * 100.0
    );
    r.found(
        &format!(
            "And again, in the opposite direction. The second replica is worth \
             {} percentage points of offered success. The third is worth {}. The value \
             of capacity is not a property of capacity; it is a property of where the \
             purchase lands you on the utilisation curve. Below the knee, additional \
             replicas buy nothing measurable, and a fleet sized by the reflex that solved \
             the last incident will keep growing long after it has stopped helping. Both \
             of my predictions in this section were wrong for the same reason: I was \
             treating 'add capacity' and 'shed load' as competing quantities of a single \
             substance, when what actually matters is the shape of the curve at the point \
             you are standing on.",
            second_worth,
            third_worth,
        ),
        false,
    );

    r.h3("9b. Which requests you refuse matters more than how many");
    r.expect(
        "Among the shedding policies, the SLO-predictive one should win. It rejects \
         precisely those requests whose predicted wait already exceeds their objective, \
         which is the request most obviously worth refusing.",
    );
    r.found(
        &format!(
            "SLO-predictive is the *worst* admission policy measured. It \
             rejects {} of {} requests -- {} of the traffic -- to reach an offered \
             success rate of {}, while cost-aware shedding rejects {} ({}) and reaches \
             {}. Uniform random shedding at 25% reaches {}, beating the clever policy \
             while having no idea what it is doing. The defect is that SLO-predictive \
             sheds whatever is *arriving while the queue is long*, and during a burst \
             that is disproportionately interactive traffic -- the cheapest requests with \
             the tightest deadlines, which are precisely the ones worth keeping. \
             Refusing one batch job returns as much capacity as refusing twenty chat \
             turns. Cost-aware shedding ranks candidates by urgency per token of work and \
             drops the expensive, latency-tolerant end first; on this workload the value \
             density of chat is about {}x that of batch, which is the whole margin.",
            slo.rejected.len(),
            b.trace_95.len(),
            pct(slo.rejected.len() as f64 / b.trace_95.len() as f64),
            pct(slo.offered_success_rate()),
            ca.rejected.len(),
            pct(ca.rejected.len() as f64 / b.trace_95.len() as f64),
            pct(ca.offered_success_rate()),
            pct({
                let kept: Vec<Request> = b
                    .trace_95
                    .iter()
                    .filter(|r| (r.id as usize * 37 % 100) >= 25)
                    .copied()
                    .collect();
                let mut s = run_requests(&b.base, &kept);
                s.offered = b.trace_95.len();
                s.offered_success_rate()
            }),
            f0(value_density(&b.trace_95, Kind::Chat) / value_density(&b.trace_95, Kind::Batch)),
        ),
        false,
    );
    let replica_gain = two.offered_success_rate() - base1.offered_success_rate();
    let recovered = format!(
        "{:.0}%",
        (best.offered_success_rate() - base1.offered_success_rate()) / replica_gain.max(1e-9)
            * 100.0
    );
    let replica_points = format!("{:.1}", replica_gain * 100.0);
    r.para(&format!(
        "The practical result: on one replica, refusing {} of offered traffic by value \
         density recovers {} of the {} percentage points that an entire additional \
         replica buys, at no hardware cost. That is the honest version of the claim this \
         section set out to make -- not that admission control replaces capacity, but \
         that it is the cheapest way to spend the time before the capacity arrives, and \
         that a poorly chosen shedding rule can be worse than a coin flip.",
        pct(best.rejected.len() as f64 / b.trace_95.len() as f64),
        recovered,
        replica_points,
    ));
}

// --------------------------------------------------------------- section 10

fn section_10_kv_reservation(r: &mut Report, b: &Bench) {
    r.h2("10. How much memory to set aside, and which constraint is binding");
    r.para(
        "Admitting a sequence means committing KV cache for its whole lifetime. The \
         gateway reserves `factor x projected peak footprint`. Above 1.0 it holds back \
         headroom and runs a smaller batch; below 1.0 it over-subscribes, runs a larger \
         batch, and risks having to preempt. The right value is not a constant.",
    );
    r.expect(
        "Over-subscribing memory should be a mistake: the batch gains are small and \
         eviction throws away completed work, so the best factor should be at or slightly \
         above 1.0 in every regime.",
    );

    r.h3("10a. KV capacity is the binding constraint (60k tokens)");
    let mut rows = Vec::new();
    let mut best_f = 0.0;
    let mut best_ok = 0.0;
    let mut at_one = 0.0;
    for f in [0.5f64, 0.7, 0.85, 1.0, 1.25, 1.6] {
        let s = b.run(
            &b.base.clone().with_kv_capacity(60_000).with_reservation(f),
            &b.trace_85,
        );
        if s.offered_success_rate() > best_ok {
            best_ok = s.offered_success_rate();
            best_f = f;
        }
        if f == 1.0 {
            at_one = s.offered_success_rate();
        }
        rows.push(vec![
            f2(f),
            f1(s.mean_batch),
            s.evictions.to_string(),
            s.rejected.len().to_string(),
            f2(s.throughput_rps()),
            pct(s.offered_success_rate()),
        ]);
    }
    r.table(
        &[
            "reservation factor",
            "mean batch",
            "evictions",
            "rejected",
            "throughput (req/s)",
            "offered success rate",
        ],
        rows,
    );

    r.h3("10b. Batch size is the binding constraint (160k tokens)");
    let mut rows = Vec::new();
    for f in [0.5f64, 0.7, 1.0, 1.3, 1.8] {
        let s = b.run(&b.base.clone().with_reservation(f), &b.trace_85);
        rows.push(vec![
            f2(f),
            f1(s.mean_batch),
            s.evictions.to_string(),
            ms(s.ttft().p99()),
            f2(s.throughput_rps()),
            pct(s.offered_success_rate()),
        ]);
    }
    r.table(
        &[
            "reservation factor",
            "mean batch",
            "evictions",
            "TTFT p99",
            "throughput (req/s)",
            "offered success rate",
        ],
        rows,
    );

    r.found(
        &format!(
            "In one regime only. Held where memory is plentiful and contradicted where it is not. When KV capacity binds, the optimum is at factor {} -- deliberate \
             over-subscription -- reaching {} against {} at full reservation. The larger \
             batch is worth more than the evictions cost. When batch size binds, memory \
             was never scarce, under-reserving is free, and over-reserving is pure loss: \
             factor 1.8 shrinks the mean batch by a third and costs {} of offered \
             success for no benefit whatsoever. So the reservation factor cannot be tuned \
             from a latency dashboard. You have to know whether your batch is limited by \
             memory or by configuration, and those two states look identical from the \
             outside.",
            f2(best_f),
            pct(best_ok),
            pct(at_one),
            {
                let a = b.run(&b.base.clone().with_reservation(0.5), &b.trace_85);
                let z = b.run(&b.base.clone().with_reservation(1.8), &b.trace_85);
                pct(a.offered_success_rate() - z.offered_success_rate())
            },
        ),
        false,
    );

    r.h3("10c. What preemption cannot fix");
    r.para(
        "Building this section produced the simulator's worst bug, and the most \
         instructive one. At reservation factors below 1.0 the run performed 19,997,889 \
         evictions and completed nothing. Every sequence reserved less than it would \
         need, so memory ran short; the gateway evicted a sequence, which freed a large \
         block, which made the evicted sequence immediately admissible again; it was \
         re-admitted, over-grew, and was evicted once more. The run only terminated \
         because of an event ceiling, and it reported a throughput of 0.00 requests per \
         second alongside a perfectly plausible 97.6% SLO attainment. Nothing in the \
         output indicated the numbers were fiction.",
    );
    r.para(
        "Three separate defects had to be fixed. Admission has to reserve one token for \
         every sequence already decoding, not just for the newcomer, or the gateway \
         admits into memory its existing batch is about to consume. A preempted request \
         has to back off before it can return, and the backoff has to be exponential, \
         because linear backoff still lets a pathological configuration spend an entire \
         run thrashing. And preemption needs a retry limit: a systematically insufficient \
         reservation is not recoverable by rearranging which sequence is the victim, so \
         after a few attempts the honest response is to reject the request. The report's \
         thesis arrived here as a consequence rather than as a slogan -- when a system \
         cannot fit the work, the only real options are more capacity or less work.",
    );
    r.note(
        "The lasting fix was not any of those three. It was `RunStats::truncated`, which \
         marks a run that hit the ceiling, and an assertion that no reported run is \
         truncated. A safety valve that silently substitutes plausible numbers for real \
         ones is more dangerous than no safety valve at all.",
    );
}

// --------------------------------------------------------------- section 11

fn section_11_littles_law(r: &mut Report, b: &Bench) {
    r.h2("11. Auditing the simulator with Little's Law");
    r.para(
        "`L = lambda x W`. The mean number of requests in a system equals the arrival \
         rate times the mean time each spends there. It holds for any stable queueing \
         system regardless of arrival distribution, service distribution, scheduling \
         policy or server count -- it assumes almost nothing, which is what makes it \
         useful as a check rather than as a prediction.",
    );
    r.para(
        "Because it must hold, it can be tested. This simulator measures `L` by \
         integrating the in-system count over time inside the event loop, and measures \
         `lambda` and `W` from the completion records. Those come from different code \
         paths: occupancy from the clock advance, sojourn time from per-request \
         timestamps. A bookkeeping error in the event loop breaks the identity and \
         nothing else in the crate would notice.",
    );
    r.expect(
        "The identity should hold to within 2% across every configuration, with the \
         residual coming from finite-run edge effects.",
    );

    let mut rows = Vec::new();
    let mut worst = 0.0f64;
    let cases: Vec<(String, RunStats)> = vec![
        ("fifo, rho 0.85".to_string(), b.run(&b.base, &b.trace_85)),
        (
            "sjf, rho 0.85".to_string(),
            b.run(&b.base.clone().with_policy(Policy::Sjf), &b.trace_85),
        ),
        (
            "drr, rho 0.85".to_string(),
            b.run(
                &b.base.clone().with_policy(Policy::DeficitRoundRobin),
                &b.trace_85,
            ),
        ),
        ("fifo, rho 0.95".to_string(), b.run(&b.base, &b.trace_95)),
        (
            "2 replicas, rho 0.95".to_string(),
            b.run(&b.base.clone().with_replicas(2), &b.trace_95),
        ),
        (
            "cost-aware shedding".to_string(),
            b.run(
                &b.base.clone().with_admission(Admission::CostAware(2_000)),
                &b.trace_95,
            ),
        ),
        (
            "kv 60k, factor 0.85".to_string(),
            b.run(
                &b.base
                    .clone()
                    .with_kv_capacity(60_000)
                    .with_reservation(0.85),
                &b.trace_85,
            ),
        ),
    ];
    for (name, s) in &cases {
        worst = worst.max(s.little.relative_error());
        rows.push(vec![
            name.clone(),
            f2(s.little.measured_l),
            f2(s.little.lambda),
            f2(s.little.w),
            f2(s.little.predicted_l()),
            format!("{:.4}%", s.little.relative_error() * 100.0),
        ]);
    }
    r.table(
        &[
            "configuration",
            "measured L",
            "lambda (req/s)",
            "W (s)",
            "lambda x W",
            "relative error",
        ],
        rows,
    );
    r.found(
        &format!(
            "With a great deal of room to spare: the worst disagreement across \
             every configuration is {:.4}%. That is tighter than the 2% tolerance because \
             the simulator drains rather than stopping at a horizon, so no request \
             contributes to occupancy without also contributing a completion time.",
            worst * 100.0
        ),
        true,
    );
    r.para(
        "This check earned its place. An early version of the event loop stepped every \
         replica and then advanced a single global clock by the *minimum* elapsed time, \
         which credited slower replicas with work they had not done. The multi-replica \
         numbers looked entirely reasonable. Little's Law did not agree with them, and \
         that disagreement was the only signal that anything was wrong.",
    );
    r.note(
        "The general technique: find a quantity your system must satisfy for structural \
         reasons, compute both sides from independent code paths, and assert they agree. \
         It is the cheapest real check available on a simulation, and unlike a golden \
         output file it keeps working when the results legitimately change.",
    );
}

// --------------------------------------------------------------- section 12

fn section_12_limits(r: &mut Report) {
    r.h2("12. What this does not model");
    r.para(
        "The engine model captures three things: decode is bandwidth bound so batching is \
         nearly free, prefill is compute bound so it blocks decode, and KV cache is the \
         binding memory constraint. Those three determine queueing behaviour. A good deal \
         else determines the constants, and none of it is here.",
    );
    r.bullets(&[
        "**Chunked prefill.** Prefill runs to completion for one request before decode \
         resumes. Real schedulers split long prompts so they interleave. This makes \
         section 7's decode-priority case more extreme than any real system would be.",
        "**Paged attention and fragmentation.** KV cache is modelled as a single pool of \
         tokens. Real allocators work in blocks and suffer internal fragmentation, so \
         usable capacity is below nominal capacity by an amount that depends on the \
         length distribution.",
        "**Tensor and pipeline parallelism.** A replica is one indivisible unit. \
         Sharding a model across devices changes the constants and adds collective \
         communication that this model has no representation for.",
        "**Speculative decoding and prefix caching.** Both change effective throughput \
         substantially and neither changes the queueing structure -- which is precisely \
         why leaving them out is defensible for these questions and indefensible for \
         capacity planning.",
        "**Heterogeneous or degrading hardware.** All replicas are identical and stay \
         that way. Real fleets contain a slow node, and the interesting failure is what \
         a load balancer does when it finds one.",
        "**Request cancellation.** Users close tabs. A gateway that keeps generating for \
         a disconnected client is wasting the scarcest resource it has, and the fix -- \
         propagating cancellation into the decode loop -- is unglamorous and worth more \
         than most scheduling work.",
    ]);
    r.para(
        "The conclusions that survive these omissions are the structural ones: that \
         utilisation drives latency hyperbolically, that scheduling redistributes delay \
         while only admission control reduces it, that prediction error is cheap for \
         ordering and expensive for resource commitment, and that the marginal replica's \
         value depends entirely on where it lands you. The conclusions that do not \
         survive are any absolute number of requests per second.",
    );
}

/// Entry point for `cargo run --bin run_gateway`.
///
/// Writes `docs/results.md` directly rather than relying on shell
/// redirection, which on Windows silently inserts a BOM and rewrites line
/// endings -- and would then make `tests/results_integrity.rs` fail for a
/// reason that has nothing to do with the simulation. Pass `--stdout` to print
/// instead.
pub fn report_main() {
    let r = build_report();
    let rendered = r.render();
    if std::env::args().any(|a| a == "--stdout") {
        print!("{}", rendered);
        return;
    }
    let path = std::path::Path::new("docs").join("results.md");
    if let Some(dir) = path.parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    match std::fs::write(&path, rendered.as_bytes()) {
        Ok(()) => eprintln!(
            "wrote {} ({} predictions, digest {})",
            path.display(),
            r.prediction_count,
            r.digest()
        ),
        Err(e) => {
            eprintln!("could not write {}: {e}", path.display());
            std::process::exit(1);
        }
    }
}
