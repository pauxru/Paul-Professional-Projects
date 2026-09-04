/**
 * Builds `docs/dashboard.html` from the panel output.
 *
 * Zero dependencies, run by Node's native TypeScript stripping. The point of the page is
 * the thing the project claims is hard: making a failure that throws no exception
 * *visible*. Each scenario gets one row per detector, and each row is a sparkline of that
 * detector's score normalised by its own threshold, so the horizontal line at y=1 means
 * the same thing on every chart regardless of whether the underlying statistic lives in
 * the hundredths or the millionths.
 *
 * Three vertical markers per chart: onset (grey, when the degradation starts), material
 * (amber, when quality actually drops below 0.95) and alert (red or green, when the
 * detector fired). The visual gap between amber and the alert marker is the number the
 * whole project is about.
 */

import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";

type Series = {
  detector: string;
  notes: string;
  callsPerDay: number;
  normalised: number[];
  alertDay: number;
  delayDays: number | null;
};

type Scenario = {
  key: string;
  title: string;
  story: string;
  symptom: string;
  isRegression: boolean;
  onsetDay: number;
  materialDay: number;
  quality: number[];
  detectors: Series[];
};

type Payload = {
  days: number;
  referenceDays: number;
  persistence: number;
  alpha: number;
  requestsPerDay: number;
  scenarios: Scenario[];
  coveringSet: string[];
  falseAlarmDays: Record<string, number>;
  inputShiftFalsePositive: Record<string, boolean>;
};

const W = 620;
const H = 54;
const PAD = 4;

function escapeHtml(s: string): string {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

/**
 * Sparkline for one detector on one scenario.
 *
 * The y axis is clamped at 3x threshold. Without the clamp a detector whose score runs
 * away to 40x threshold flattens every other line on the page into the baseline, and the
 * chart stops answering the question it exists to answer -- which is *when* the line
 * crossed, not how far past it went afterwards.
 */
function sparkline(series: Series, scenario: Scenario, days: number, referenceDays: number): string {
  const ceiling = 3;
  const x = (d: number) => PAD + (d / Math.max(1, days - 1)) * (W - 2 * PAD);
  // Clamped at both ends. The ceiling stops one runaway detector from flattening every
  // other line on the page; the floor matters because two detectors legitimately score
  // negative -- APM's p95 sits below its 600ms reference, and the canary's inverted score
  // goes negative on a day the model beats its own baseline -- and without it those lines
  // are drawn below the plot rectangle.
  const y = (v: number) => H - PAD - (Math.min(Math.max(v, 0), ceiling) / ceiling) * (H - 2 * PAD);

  const points = series.normalised.map((v, d) => `${x(d).toFixed(1)},${y(v).toFixed(1)}`).join(" ");
  const parts: string[] = [];

  parts.push(`<rect x="0" y="0" width="${W}" height="${H}" class="plot"/>`);
  // Reference window: no detector may alert inside it.
  parts.push(
    `<rect x="${PAD}" y="${PAD}" width="${(x(referenceDays) - PAD).toFixed(1)}" height="${H - 2 * PAD}" class="refwin"/>`,
  );
  parts.push(`<line x1="${PAD}" y1="${y(1).toFixed(1)}" x2="${W - PAD}" y2="${y(1).toFixed(1)}" class="thresh"/>`);

  if (scenario.onsetDay >= 0) {
    parts.push(`<line x1="${x(scenario.onsetDay).toFixed(1)}" y1="0" x2="${x(scenario.onsetDay).toFixed(1)}" y2="${H}" class="onset"/>`);
  }
  if (scenario.materialDay >= 0) {
    parts.push(`<line x1="${x(scenario.materialDay).toFixed(1)}" y1="0" x2="${x(scenario.materialDay).toFixed(1)}" y2="${H}" class="material"/>`);
  }
  if (series.alertDay >= 0) {
    const cls = scenario.isRegression ? "alert-good" : "alert-bad";
    parts.push(`<line x1="${x(series.alertDay).toFixed(1)}" y1="0" x2="${x(series.alertDay).toFixed(1)}" y2="${H}" class="${cls}"/>`);
  }
  parts.push(`<polyline points="${points}" class="line"/>`);

  return `<svg viewBox="0 0 ${W} ${H}" width="${W}" height="${H}" role="img" aria-label="${escapeHtml(series.detector)}">${parts.join("")}</svg>`;
}

function qualityLine(scenario: Scenario, days: number): string {
  const x = (d: number) => PAD + (d / Math.max(1, days - 1)) * (W - 2 * PAD);
  const y = (v: number) => H - PAD - ((v - 0.5) / 0.5) * (H - 2 * PAD);
  const points = scenario.quality
    .map((v, d) => `${x(d).toFixed(1)},${y(Math.max(0.5, v)).toFixed(1)}`)
    .join(" ");
  return (
    `<svg viewBox="0 0 ${W} ${H}" width="${W}" height="${H}" role="img" aria-label="true quality">` +
    `<rect x="0" y="0" width="${W}" height="${H}" class="plot"/>` +
    `<line x1="${PAD}" y1="${y(0.95).toFixed(1)}" x2="${W - PAD}" y2="${y(0.95).toFixed(1)}" class="thresh"/>` +
    `<polyline points="${points}" class="qline"/></svg>`
  );
}

function verdict(series: Series, scenario: Scenario): string {
  if (series.alertDay < 0) {
    return scenario.isRegression
      ? `<span class="v-miss">missed</span>`
      : `<span class="v-ok">correctly silent</span>`;
  }
  if (!scenario.isRegression) {
    return `<span class="v-fp">false positive, day ${series.alertDay}</span>`;
  }
  const d = series.delayDays ?? 0;
  if (d < 0) return `<span class="v-early">${-d}d early</span>`;
  if (d === 0) return `<span class="v-early">same day</span>`;
  return `<span class="v-late">${d}d late</span>`;
}

function render(p: Payload): string {
  const rows: string[] = [];

  for (const s of p.scenarios) {
    const kind = s.isRegression
      ? `<span class="tag tag-reg">regression</span>`
      : `<span class="tag tag-ctl">control &mdash; alerting here is wrong</span>`;
    rows.push(`<section>
  <h2>${escapeHtml(s.title)} ${kind}</h2>
  <p class="story">${escapeHtml(s.story)}</p>
  <p class="meta">Symptom: <em>${escapeHtml(s.symptom)}</em>${
      s.onsetDay >= 0 ? ` &middot; onset day ${s.onsetDay}` : ""
    }${s.materialDay >= 0 ? ` &middot; quality materially degraded from day ${s.materialDay}` : " &middot; quality never degrades"}</p>
  <div class="chart"><div class="label">True quality <span class="sub">ground truth &mdash; no detector may look at this</span></div>${qualityLine(s, p.days)}<div class="verdict"></div></div>
  ${s.detectors
    .map(
      (d) => `<div class="chart">
    <div class="label">${escapeHtml(d.detector)}<span class="sub">${escapeHtml(d.notes)}${
        d.callsPerDay ? ` &middot; ${d.callsPerDay} model calls/day` : " &middot; free"
      }</span></div>${sparkline(d, s, p.days, p.referenceDays)}<div class="verdict">${verdict(d, s)}</div></div>`,
    )
    .join("\n  ")}
</section>`);
  }

  return `<!doctype html>
<html lang="en"><head><meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>Silent failure detection panel</title>
<style>
:root { color-scheme: light dark; }
body { font: 14px/1.55 ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif; margin: 0 auto; max-width: 1080px; padding: 2rem 1.25rem 4rem; }
h1 { font-size: 1.6rem; margin-bottom: .25rem; }
h2 { font-size: 1.05rem; margin: 2.4rem 0 .3rem; }
p.lede { color: #666; max-width: 62ch; }
p.story { max-width: 78ch; margin: .2rem 0 .4rem; }
p.meta { color: #777; font-size: .82rem; margin: 0 0 .7rem; }
.chart { display: grid; grid-template-columns: 250px ${W}px 1fr; align-items: center; gap: .8rem; padding: .18rem 0; }
.label { font-size: .8rem; font-weight: 600; }
.label .sub { display: block; font-weight: 400; color: #888; font-size: .72rem; }
.verdict { font-size: .78rem; }
.plot { fill: #fafafa; stroke: #e3e3e3; }
.refwin { fill: #f0f0f0; }
.line { fill: none; stroke: #2563eb; stroke-width: 1.3; }
.qline { fill: none; stroke: #111; stroke-width: 1.3; }
.thresh { stroke: #bbb; stroke-width: 1; stroke-dasharray: 3 3; }
.onset { stroke: #999; stroke-width: 1; stroke-dasharray: 2 3; }
.material { stroke: #d97706; stroke-width: 1.2; }
.alert-good { stroke: #16a34a; stroke-width: 1.6; }
.alert-bad { stroke: #dc2626; stroke-width: 1.6; }
.tag { font-size: .68rem; padding: .1rem .45rem; border-radius: 3px; vertical-align: middle; font-weight: 600; }
.tag-reg { background: #fee2e2; color: #991b1b; }
.tag-ctl { background: #dbeafe; color: #1e40af; }
.v-early { color: #15803d; font-weight: 600; }
.v-late { color: #b45309; }
.v-miss { color: #991b1b; }
.v-fp { color: #dc2626; font-weight: 600; }
.v-ok { color: #15803d; }
.legend { font-size: .78rem; color: #666; border: 1px solid #e3e3e3; border-radius: 5px; padding: .7rem .9rem; margin: 1.2rem 0; }
code { background: #f2f2f2; padding: .05rem .25rem; border-radius: 3px; }
@media (prefers-color-scheme: dark) {
  body { background: #111; color: #e6e6e6; }
  .plot { fill: #1a1a1a; stroke: #333; } .refwin { fill: #222; }
  .qline { stroke: #eee; } .thresh, .onset { stroke: #555; }
  .legend { border-color: #333; } code { background: #222; }
  p.lede, p.meta, .label .sub { color: #999; }
}
</style></head><body>
<h1>Detecting failures that don't throw</h1>
<p class="lede">${p.days} days of a support assistant, ${p.requestsPerDay} requests a day.
Every request in every scenario below returns <code>200 OK</code> with a normal latency.
Five of the six streams contain something an operator would want to know about; one does
not, and is here to catch detectors that cannot tell the difference.</p>

<div class="legend">
Each sparkline is a detector's score <strong>divided by its own alert threshold</strong>, so the
dashed line at 1.0 means the same thing on every chart. Thresholds are calibrated to a
${(p.alpha * 100).toFixed(0)}% per-day false positive rate and an alert needs
${p.persistence} consecutive days over the line. The shaded band is the
${p.referenceDays}-day reference window, inside which nothing may alert.
Markers: <span class="v-late">amber</span> = the day quality genuinely dropped;
<span class="v-early">green</span> = a correct alert; <span class="v-fp">red</span> = an
alert on the control. The distance from amber to green is what this project measures.
<br/><br/>
Cheapest set of detectors covering every regression:
<strong>${p.coveringSet.map(escapeHtml).join(" + ")}</strong>.
</div>

${rows.join("\n")}
<p class="meta" style="margin-top:2.5rem">Generated by <code>dashboard/build.ts</code> from
<code>docs/dashboard-data.json</code>. No JavaScript runs on this page and nothing is fetched from a network.</p>
</body></html>
`;
}

function main(): void {
  const root = join(import.meta.dirname, "..");
  const input = join(root, "docs", "dashboard-data.json");
  const output = join(root, "docs", "dashboard.html");
  const payload = JSON.parse(readFileSync(input, "utf8")) as Payload;
  mkdirSync(dirname(output), { recursive: true });
  writeFileSync(output, render(payload), "utf8");

  const charts = payload.scenarios.reduce((n, s) => n + s.detectors.length + 1, 0);
  console.log(`dashboard: ${payload.scenarios.length} scenarios, ${charts} charts -> ${output}`);
}

main();
