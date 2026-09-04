# Screenshots needed for portfolio site

None of these have been captured yet; this is a checklist for when we deploy locally.

1. **Swagger UI** at `https://localhost:5023/swagger` — full endpoint list.
2. **Operational dashboard homepage** — today's schedule per site.
3. **Clinic board** — patient journey stages with wait times.
4. **Access-anomaly report** — showing a break-glass event, a VIP read, and a
   no-care-relationship read.
5. **Slot search response JSON** — pretty-printed showing candidate slots for a day.
6. **`dotnet test -c Release` terminal output** — 62 passed, 0 failed.
7. **ER diagram** — captured from the rendered Mermaid in `docs/database-schema.md`.
8. **Break-glass sequence diagram** — captured from the rendered Mermaid in `README.md`.
9. **Concurrent-booking test source** — snippet of `ConcurrentBookingTests.cs`.
10. **Audit table row** — a `PatientDataRead` row with `BreakGlass = true`.
