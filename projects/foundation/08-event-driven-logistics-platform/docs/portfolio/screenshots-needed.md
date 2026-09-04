# Screenshots Needed

Capture only synthetic data and crop out local usernames/paths if publishing.

1. **Dashboard overview** — vehicle state, active trips, alerts and geofences after simulator run.
2. **Simulator result** — JSON showing generated/dropped/duplicate/out-of-order counts.
3. **Replay result** — max-speed replay response beside unchanged alert count.
4. **Projection rebuild** — response plus restored current-state rows.
5. **OpenAPI surface** — `/openapi/v1.json` or an imported API viewer showing grouped endpoints.
6. **Architecture diagram** — rendered event pipeline Mermaid.
7. **Ping-to-alert sequence** — rendered sequence diagram.
8. **Trip lifecycle** — rendered state diagram.
9. **Test result** — console showing 44 unit and 14 integration tests passing.
10. **Benchmarks** — detailed console output for geofence, ETA and 10,000-ping tests.
11. **Late/dead stats** — `/api/v1/telemetry/stats` after an intentional late ping.
12. **Security evidence** — 401 without token and 403 with viewer profile on a write.

Do not present Docker output because Docker was unavailable and the container configuration was not verified.
