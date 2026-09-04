# Deterministic simulation results

All numbers below are produced by `cargo run --release`. Every run is a
pure function of its seed, so every line reproduces.

## Does the fault model find the bug?

`read_repair = false` removes the write-back phase of the ABD read. It is a
plausible optimisation and it is not linearizable.

| network | read-repair | runs | violations | rate |
|---|---|---|---|---|
| perfect | on | 2000 | 0 | 0.00% |
| perfect | off | 2000 | 0 | 0.00% |
| faulty | on | 2000 | 0 | 0.00% |
| faulty | off | 2000 | 46 | 2.30% |

The row that matters is `perfect / off`: the broken protocol is invisible
on a healthy network. That is precisely why this class of bug reaches
production and survives there.

## What the fault injector actually did

| network | read-repair | messages | dropped | duplicated | reordered | abandoned ops |
|---|---|---|---|---|---|---|
| perfect | on | 48000 | 0 | 0 | 0 | 0 |
| perfect | off | 48000 | 0 | 0 | 0 | 0 |
| faulty | on | 44887 | 15923 | 10785 | 53759 | 2088 |
| faulty | off | 46471 | 10051 | 7577 | 38473 | 1929 |

## Which fault is load-bearing?

Each row disables one fault and re-runs the broken protocol. If the
violation rate collapses, that fault was the one exposing the bug.

| faults disabled | runs | violations | rate |
|---|---|---|---|
| none (full fault model) | 2000 | 46 | 2.30% |
| message loss | 2000 | 40 | 2.00% |
| duplication | 2000 | 49 | 2.45% |
| reordering | 2000 | 28 | 1.40% |
| partitions | 2000 | 45 | 2.25% |
| clock skew | 2000 | 51 | 2.55% |
| straggler links | 2000 | 25 | 1.25% |

## Replay and shrinking

First failing seed: 19. Two independent runs produce byte-identical reports.

Shrunk from 5r/4c/8ops to 5r/4c/4ops, still failing.

```
seed 19
  network: 213 delivered, 7 dropped, 4 duplicated, 20 reordered, 56 partition changes
  history: 16 operations (14 completed, 2 abandoned)
  NOT LINEARIZABLE (operation 8)
  read by client 0 returned 62 at t=118049, but by the time it was invoked (t=71321) the completed writes were [12, 62]; no total order over the remaining operations makes that value current
  history:
     op0   client0          1..16611      Read(0)
     op1   client1        138..85041      Write(51)
     op2   client2        275..35022      Write(12)
     op3   client3        412..pending    Write(53)
     op4   client0      18611..69321      Read(0)
     op6   client2      37022..52155      Write(62)
     op10  client2      54155..64365      Read(53)
     op14  client2      66365..75327      Read(53)
  >> op8   client0      71321..118049     Read(62)
     op5   client1      87041..125774     Read(53)
     op7   client3     102061..201430     Write(93)
     op12  client0     120049..167249     Read(53)
     op9   client1     127774..163512     Read(53)
     op13  client1     165512..191303     Read(53)
     op11  client3     203430..pending    Write(63)
     op15  client3     325075..349160     Read(63)
```

## Where the bug lives: sweeping the workload shape

Each cell is 500 seeds against the broken protocol. My prior was that
larger clusters would expose it more readily. They do the opposite.

| replicas | clients | ops/client | runs | violations | rate |
|---|---|---|---|---|---|
| 3 | 2 | 2 | 500 | 0 | 0.0% |
| 3 | 2 | 4 | 500 | 2 | 0.4% |
| 3 | 2 | 6 | 500 | 2 | 0.4% |
| 3 | 3 | 2 | 500 | 0 | 0.0% |
| 3 | 3 | 4 | 500 | 1 | 0.2% |
| 3 | 3 | 6 | 500 | 5 | 1.0% |
| 3 | 4 | 2 | 500 | 1 | 0.2% |
| 3 | 4 | 4 | 500 | 3 | 0.6% |
| 3 | 4 | 6 | 500 | 13 | 2.6% |
| 5 | 2 | 2 | 500 | 0 | 0.0% |
| 5 | 2 | 4 | 500 | 0 | 0.0% |
| 5 | 2 | 6 | 500 | 0 | 0.0% |
| 5 | 3 | 2 | 500 | 1 | 0.2% |
| 5 | 3 | 4 | 500 | 4 | 0.8% |
| 5 | 3 | 6 | 500 | 6 | 1.2% |
| 5 | 4 | 2 | 500 | 1 | 0.2% |
| 5 | 4 | 4 | 500 | 5 | 1.0% |
| 5 | 4 | 6 | 500 | 4 | 0.8% |
| 7 | 2 | 2 | 500 | 0 | 0.0% |
| 7 | 2 | 4 | 500 | 0 | 0.0% |
| 7 | 2 | 6 | 500 | 0 | 0.0% |
| 7 | 3 | 2 | 500 | 0 | 0.0% |
| 7 | 3 | 4 | 500 | 0 | 0.0% |
| 7 | 3 | 6 | 500 | 0 | 0.0% |
| 7 | 4 | 2 | 500 | 0 | 0.0% |
| 7 | 4 | 4 | 500 | 1 | 0.2% |
| 7 | 4 | 6 | 500 | 0 | 0.0% |

## Cost of exhaustive checking

The linearizability checker is exponential in the worst case. Sizing the
workload so that it stays exact is a deliberate trade: a sampled checker
would let violations through, and a harness you cannot trust is worse
than no harness.

| ops in history | runs | wall time | ms/run |
|---|---|---|---|
| 7 | 200 | 0.01s | 0.03 |
| 15 | 200 | 0.01s | 0.06 |
| 22 | 200 | 0.01s | 0.07 |
| 29 | 200 | 0.02s | 0.10 |
