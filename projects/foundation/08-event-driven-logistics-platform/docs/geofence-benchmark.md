# Geofence Spatial Index Benchmark

## Result

Real command executed on the Windows build host (16 cores, 64 GB RAM, .NET 10):

```powershell
dotnet test tests\SavannaLogistics.UnitTests -c Release `
  --filter "Category=Benchmark" --logger "console;verbosity=detailed"
```

Measured output on 2026-09-03:

```text
GEOFENCE_BENCHMARK geofences=2000 points=5000 bruteEvaluations=10000000 indexedEvaluations=10510 bruteMs=1919.25 indexedMs=12.75 matches=1527
```

| Metric | Brute force | Grid index |
|---|---:|---:|
| Geofences | 2,000 | 2,000 |
| Query points | 5,000 | 5,000 |
| Exact containment evaluations | 10,000,000 | 10,510 |
| Wall-clock time | 1,919.25 ms | 12.75 ms |
| Matches | 1,527 | 1,527 |

The measured wall-clock ratio was approximately **150.5×**, and the candidate index avoided approximately **99.895%** of exact evaluations. Both paths returned exactly the same matches.

## Setup

- Seed: `20260903`.
- Geofences: 2,000 synthetic circles over a one-degree Nairobi-region box.
- Radius: 0.1–1.3 km.
- Points: 5,000 random positions over the same box.
- Cell size: 0.02 degrees.
- Exact predicate: the same haversine circle containment used by the processor.
- Warm-up is not separated; this is a practical repository regression benchmark, not a BenchmarkDotNet microbenchmark.

## Why candidate indexing matters

Without a spatial index, each ping evaluates every fleet geofence: `O(M)`. The grid inserts a geofence ID into every cell touched by its bounding box. A ping performs an `O(1)` cell lookup and exact calculations only for that cell's candidates. The exact predicate remains authoritative, so bounding-box false positives are harmless.

## Caveats

Timing varies with host load and data distribution. Large geofences span more cells; dense urban clusters produce more candidates; polygons cost more than circles. Production should use PostGIS GiST/SP-GiST indexes and `ST_DWithin`/`ST_Covers`, then benchmark with actual shape distributions. These numbers are synthetic and are not a production throughput claim.
