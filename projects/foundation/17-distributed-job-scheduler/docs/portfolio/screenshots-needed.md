# Portfolio Screenshot and Recording Checklist

Capture only fictional demo data and redact any locally generated JWT values before publishing.

| Capture | What it should show | Why it matters |
| --- | --- | --- |
| **Dashboard overview** | The dashboard at `http://localhost:5017/` with job definitions, upcoming schedules, recent runs, workers, DLQ, and leader summary visible together. | Establishes that this is an operational scheduler surface, not only a code sample. |
| **Job definitions list** | Multiple definitions with handler type, trigger type, queue, enabled status, and owner; include cron/interval/manual examples if present. | Shows the configurable scheduling model and the fixed-handler approach. |
| **Run timeline / detail** | One run progressing through `Pending`, `Claimed`, `Running`, and `Succeeded`, plus attempt count, lease owner, fencing token, correlation ID, and logs where available. | Makes the state machine and traceability concrete. |
| **Worker nodes and heartbeat age** | `w1` and `w2` registered with tags/capabilities, status, slots, and changing heartbeat-age values. | Demonstrates multi-worker visibility and liveness tracking. |
| **Reclaim and fencing recording** | Two worker terminals, the owning worker being force-stopped, and API/dashboard polling that shows the run reclaimed by the surviving worker with a higher fencing token. | Tells the differentiating failure-recovery story: stale writers cannot overwrite a reclaimed run. |
| **Dead-letter queue and replay button** | A dead-lettered `flaky` run, its reason/attempt count, and the replay control before and after activation. | Demonstrates an operator recovery path rather than silently losing poison work. |
| **OpenAPI / Swagger surface** | The OpenAPI document at `http://localhost:5017/openapi/v1.json`, or a Swagger UI only when one is configured for the presentation, showing the API groups and bearer-authenticated operations. | Shows that the scheduler has a documented integration contract. |
| **Passing test terminal** | The verified xUnit summary: **155** unit tests plus **31** integration tests, **186** total, **0 failed**, **0 skipped**. | Provides concrete evidence for correctness work, including the integration coverage. |

## Recording notes

- Use the reclaim walkthrough in `demo-script.md` so the worker failure, lease expiry, token increase, and successful recovery are captured in one continuous clip.
- Leave enough time after force-stopping a worker for lease expiry and the leader reaper; do not edit the wait out of the recording.
- Do not present the authored Dockerfile or `docker-compose.yml` as running evidence: Docker is authored-but-unverified on the build host.

