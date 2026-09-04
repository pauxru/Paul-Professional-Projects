# Screenshots Needed

The repository is code-first and does not require a UI. If you want visuals for a portfolio site,
here is the list of shots that best convey what this project *is*:

1. **Terminal running the API** — showing `Now listening on: http://localhost:5015` and structured
   log output with a correlation id.

2. **`GET /api/v1/rulesets` response** — the JSON that shows an active `v1.0.0` and a shadow
   `v1.1.0-challenger`, side-by-side.

3. **A `ScoreResponse` JSON** — one that fires multiple rules (score in the 400-700 range so you
   can see `Decision: "Review"` and a non-empty `rulesFired` array).

4. **The Mermaid diagrams from the README** rendered as SVG for slide use:
   - Container view.
   - Scoring sequence.
   - Case lifecycle.

5. **A screenshot of the passing test run** — `dotnet test -c Release` output showing `Passed: 68`.

6. **`docs/detection-performance.snapshot.json`** — the actual measured numbers file. Screenshot
   both the file and the terminal that produced it, so nobody can accuse you of hand-editing.

7. **A section of the code**:
   - `WindowedFeatureAggregator.Aggregate` — the ring-buffer sum, with the boundary comment.
   - `PartitionedTransactionBus.PartitionOf` — the FNV-1a hash.
   - `ScoringService.Aggregate` — the allow-list / deny-list precedence and budget-degradation.
   - `Case.ProposeDisposition` — the four-eyes check.

If a portfolio site is required, these seven screenshots plus the README render more than cover
the ground.
