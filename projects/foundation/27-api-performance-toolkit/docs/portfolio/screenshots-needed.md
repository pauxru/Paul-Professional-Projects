# Screenshots needed

These are the images that would go into a portfolio site or a PDF write-up. They are
*not* checked in as image files — the toolkit runs offline and produces the report data
that the images render, so you can regenerate them any time.

## 1. CLI summary of a case-study run

**Source:** `dotnet loadrun.dll run scenarios\case-study-baseline.json` in Release mode.
**Content:** the printed summary block. Terminal, ~500 × 300 px.

## 2. HTML report — case-study baseline

**Source:** `results/case-study-baseline-<runId>.html` (open in a browser, take a full
screenshot of the top of the report).
**Content:** header + summary table + first two SVG charts (RPS over time; latency
percentile distribution). ~1200 × 900 px.

## 3. HTML report — comparison overlay

**Source:** `results/compare-<baseline>-vs-<candidate>.html`.
**Content:** the p95 overlay chart with baseline (red) and candidate (green) lines.
Below it, the significance verdict block. ~1200 × 800 px.

## 4. Markdown comparison as it would look in a PR comment

**Source:** `results/compare-<baseline>-vs-<candidate>.md`.
**Content:** the whole file, rendered by GitHub's markdown renderer if possible; otherwise
by a static tool. Emphasise the "Overall verdict: Improved" line. ~1000 × 700 px.

## 5. Architecture diagram

**Source:** `README.md` "Architecture Diagram" section → render the Mermaid diagram in
a viewer that supports it. Export as PNG at ~1600 × 900 px.

## 6. Test-run output

**Source:** `docs/test-results.md` — a terminal screenshot of `dotnet test -c Release`
showing the passing count and duration. ~1000 × 500 px.

## 7. Sample scenario JSON

**Source:** `scenarios/case-study-baseline.json`. Syntax-highlighted, ~600 × 700 px.

## 8. Sample API "pathology switches" endpoint

**Source:** browser at `http://127.0.0.1:5027/admin/pathology` after a POST to enable a
mode. Shows the JSON reply confirming the switch state.

## Notes for whoever takes the screenshots

- Use a dark terminal theme with a monospace font at 14–16 pt.
- Crop tightly; don't include your taskbar or IDE chrome.
- Redact anything that looks like a real identifier (there shouldn't be any, but eyes
  play tricks in demos).
