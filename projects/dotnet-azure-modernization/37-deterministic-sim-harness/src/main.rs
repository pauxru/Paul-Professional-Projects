//! CLI: fuzz, replay, shrink, and the experiment that writes the results table.

use detsim::abd::Config;
use detsim::runner::{fuzz, report, run_one, shrink};
use detsim::sim::NetConfig;
use std::env;

fn main() {
    let args: Vec<String> = env::args().collect();
    let cmd = args.get(1).map(|s| s.as_str()).unwrap_or("experiment");
    match cmd {
        "fuzz" => {
            let n: u64 = args.get(2).and_then(|s| s.parse().ok()).unwrap_or(500);
            let repair = args.get(3).map(|s| s != "broken").unwrap_or(true);
            let cfg = Config {
                read_repair: repair,
                ..Config::hunting()
            };
            let s = fuzz(0..n, &cfg, &NetConfig::default());
            println!(
                "{} runs, {} failures ({:.1}%)",
                s.runs,
                s.failures.len(),
                100.0 * s.failure_rate()
            );
            for seed in s.failures.iter().take(10) {
                println!("  failing seed {seed}");
            }
        }
        "replay" => {
            let seed: u64 = args.get(2).and_then(|s| s.parse().ok()).unwrap_or(0);
            let repair = args.get(3).map(|s| s != "broken").unwrap_or(false);
            let cfg = Config {
                read_repair: repair,
                ..Config::hunting()
            };
            print!("{}", report(&run_one(seed, &cfg, &NetConfig::default())));
        }
        "shrink" => {
            let seed: u64 = args.get(2).and_then(|s| s.parse().ok()).unwrap_or(0);
            let cfg = Config {
                read_repair: false,
                ..Config::hunting()
            };
            let net = NetConfig::default();
            let small = shrink(seed, &cfg, &net);
            println!(
                "shrank to {} replicas, {} clients, {} ops/client",
                small.replicas, small.clients, small.ops_per_client
            );
            print!("{}", report(&run_one(seed, &small, &net)));
        }
        "sweep" => sweep(),
        _ => experiment(),
    }
}

/// Sweeps the workload shape. Included because the result was not what I
/// expected and it changes how you should configure this kind of harness.
fn sweep() {
    sweep_table(500);
}

fn sweep_table(runs: u64) {
    let net = NetConfig::default();
    println!("| replicas | clients | ops/client | runs | violations | rate |");
    println!("|---|---|---|---|---|---|");
    for replicas in [3usize, 5, 7] {
        for clients in [2usize, 3, 4] {
            for ops in [2usize, 4, 6] {
                let cfg = Config {
                    replicas,
                    clients,
                    ops_per_client: ops,
                    read_repair: false,
                    ..Config::default()
                };
                let s = fuzz(0..runs, &cfg, &net);
                println!(
                    "| {replicas} | {clients} | {ops} | {} | {} | {:.1}% |",
                    s.runs,
                    s.failures.len(),
                    100.0 * s.failure_rate()
                );
            }
        }
    }
}

fn experiment() {
    let runs = 2_000u64;
    println!("# Deterministic simulation results\n");
    println!("All numbers below are produced by `cargo run --release`. Every run is a");
    println!("pure function of its seed, so every line reproduces.\n");

    println!("## Does the fault model find the bug?\n");
    println!("`read_repair = false` removes the write-back phase of the ABD read. It is a");
    println!("plausible optimisation and it is not linearizable.\n");
    println!("| network | read-repair | runs | violations | rate |");
    println!("|---|---|---|---|---|");

    let mut rows = Vec::new();
    for (netname, net) in [
        ("perfect", NetConfig::perfect()),
        ("faulty", NetConfig::default()),
    ] {
        for repair in [true, false] {
            let cfg = Config {
                read_repair: repair,
                ..Config::hunting()
            };
            let s = fuzz(0..runs, &cfg, &net);
            println!(
                "| {netname} | {} | {} | {} | {:.2}% |",
                if repair { "on" } else { "off" },
                s.runs,
                s.failures.len(),
                100.0 * s.failure_rate()
            );
            rows.push((netname, repair, s));
        }
    }

    println!("\nThe row that matters is `perfect / off`: the broken protocol is invisible");
    println!("on a healthy network. That is precisely why this class of bug reaches");
    println!("production and survives there.\n");

    println!("## What the fault injector actually did\n");
    println!("| network | read-repair | messages | dropped | duplicated | reordered | abandoned ops |");
    println!("|---|---|---|---|---|---|---|");
    for (netname, repair, s) in &rows {
        println!(
            "| {netname} | {} | {} | {} | {} | {} | {} |",
            if *repair { "on" } else { "off" },
            s.total_ops,
            s.total_dropped,
            s.total_duplicated,
            s.total_reordered,
            s.total_abandoned
        );
    }

    println!("\n## Which fault is load-bearing?\n");
    println!("Each row disables one fault and re-runs the broken protocol. If the");
    println!("violation rate collapses, that fault was the one exposing the bug.\n");
    println!("| faults disabled | runs | violations | rate |");
    println!("|---|---|---|---|");
    let cfg = Config {
        read_repair: false,
        ..Config::hunting()
    };
    let variants: Vec<(&str, NetConfig)> = vec![
        ("none (full fault model)", NetConfig::default()),
        (
            "message loss",
            NetConfig {
                drop_prob: 0.0,
                ..NetConfig::default()
            },
        ),
        (
            "duplication",
            NetConfig {
                duplicate_prob: 0.0,
                ..NetConfig::default()
            },
        ),
        (
            "reordering",
            NetConfig {
                reorder_prob: 0.0,
                ..NetConfig::default()
            },
        ),
        (
            "partitions",
            NetConfig {
                partition_prob: 0.0,
                ..NetConfig::default()
            },
        ),
        (
            "clock skew",
            NetConfig {
                max_clock_skew: 0,
                ..NetConfig::default()
            },
        ),
        (
            "straggler links",
            NetConfig {
                slow_link_prob: 0.0,
                ..NetConfig::default()
            },
        ),
    ];
    for (name, net) in &variants {
        let s = fuzz(0..runs, &cfg, net);
        println!(
            "| {name} | {} | {} | {:.2}% |",
            s.runs,
            s.failures.len(),
            100.0 * s.failure_rate()
        );
    }

    println!("\n## Replay and shrinking\n");
    let net = NetConfig::default();
    let big = Config {
        read_repair: false,
        replicas: 5,
        clients: 4,
        ops_per_client: 8,
        ..Config::default()
    };
    let s = fuzz(0..runs, &big, &net);
    if let Some(&seed) = s.failures.first() {
        let a = run_one(seed, &big, &net);
        let b = run_one(seed, &big, &net);
        println!(
            "First failing seed: {seed}. Two independent runs produce {} reports.\n",
            if report(&a) == report(&b) {
                "byte-identical"
            } else {
                "DIFFERENT"
            }
        );
        let small = shrink(seed, &big, &net);
        println!(
            "Shrunk from {}r/{}c/{}ops to {}r/{}c/{}ops, still failing.\n",
            big.replicas,
            big.clients,
            big.ops_per_client,
            small.replicas,
            small.clients,
            small.ops_per_client
        );
        println!("```");
        print!("{}", report(&run_one(seed, &small, &net)));
        println!("```");
    } else {
        println!("No failure found in {runs} runs at the large configuration.");
    }

    println!("\n## Where the bug lives: sweeping the workload shape\n");
    println!("Each cell is 500 seeds against the broken protocol. My prior was that");
    println!("larger clusters would expose it more readily. They do the opposite.\n");
    sweep_table(500);

    println!("\n## Cost of exhaustive checking\n");    println!("The linearizability checker is exponential in the worst case. Sizing the");    println!("workload so that it stays exact is a deliberate trade: a sampled checker");
    println!("would let violations through, and a harness you cannot trust is worse");
    println!("than no harness.\n");
    println!("| ops in history | runs | wall time | ms/run |");
    println!("|---|---|---|---|");
    for ops in [2usize, 4, 6, 8] {
        let c = Config {
            read_repair: true,
            ops_per_client: ops,
            ..Config::hunting()
        };
        let t0 = std::time::Instant::now();
        let n = 200u64;
        let s = fuzz(0..n, &c, &net);
        let el = t0.elapsed();
        println!(
            "| {} | {} | {:.2}s | {:.2} |",
            s.total_ops / s.runs.max(1),
            s.runs,
            el.as_secs_f64(),
            el.as_secs_f64() * 1000.0 / n as f64
        );
    }
}
