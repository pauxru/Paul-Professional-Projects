# Portfolio Demo Script

## Goal

Demonstrate ordering, fault injection, live projection, alerts and idempotent replay in 8–10 minutes.

## 1. Establish credibility

Open the README architecture diagram. State:

> The hard part is not receiving GPS; it is making stateful decisions when the device retries, reorders and disappears in a tunnel.

Point out the cache/DB dedup layers, watermark, vehicle partitions and retained source events.

## 2. Start the working system

```powershell
dotnet run --project src\SavannaLogistics.Api
```

Open `http://localhost:5008`. Show seeded fictional fleet, trip and geofences.

## 3. Inject realistic faults

Click **Run 3-vehicle simulator**, or run:

```powershell
.\scripts\demo.ps1 -VehicleCount 3 -PingsPerVehicle 120 -WatchSeconds 20
```

Show the simulator result fields: generated, dropped, duplicates injected, out-of-order swaps and ingest classification.

## 4. Explain ordered processing

Show `ADR-001` and the explicit watermark tests. Explain that device timestamps advance the bound while sequence numbers establish per-vehicle order. Show the late-arrival sink through `/api/v1/telemetry/stats`.

## 5. Show operational outcomes

Refresh:

- live positions and last-seen;
- current trips/stops;
- speeding/route/geofence alert feed;
- geofence list.

Explain dwell hysteresis and suppression: one noisy boundary or long deviation should not emit hundreds of alerts.

## 6. Replay the incident

Capture alert count, replay the last hour at max speed, and capture it again. Point out that retained pings are not reinserted and unique alert fingerprints prevent duplicates.

Then click **Rebuild projection** and show the latest states return from retained history.

## 7. Close with evidence and scale mapping

Open the benchmark documents:

- grid index: 10,000,000 → 10,510 exact evaluations;
- 10,000-ping API batch: 3.60 s;
- ETA p90: 81.25 s in deterministic synthetic runs.

Finish with the Kafka/Event Hubs and PostGIS mapping in the architecture/ADR documents, clearly labelling it as the production evolution rather than a deployed claim.
