"use strict";

// Map a QueryResult ({columns, rows}) into an array of objects keyed by column name.
function toObjects(result) {
  if (!result || !result.columns) return [];
  return result.rows.map((row) => {
    const o = {};
    result.columns.forEach((c, i) => (o[c] = row[i]));
    return o;
  });
}

async function getJson(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${url} -> ${res.status}`);
  return res.json();
}

function el(tag, attrs, children) {
  const node = document.createElement(tag);
  if (attrs) Object.entries(attrs).forEach(([k, v]) => (k === "style" ? (node.style.cssText = v) : node.setAttribute(k, v)));
  (children || []).forEach((c) => node.append(c));
  return node;
}

async function renderQuality() {
  const host = document.getElementById("dq");
  try {
    const dq = await getJson("/api/dashboard/quality");
    host.innerHTML = "";
    for (const gate of ["silver", "gold"]) {
      const g = dq[gate];
      const cls = !g.available ? "na" : g.blocking || g.failed > 0 ? "fail" : "pass";
      const label = !g.available ? "no data" : g.blocking ? "BLOCKED" : g.failed > 0 ? "WARN" : "PASS";
      const pill = el("div", { class: "dq-pill" }, [
        el("div", { class: "name" }, [`${gate} gate`]),
        el("div", { class: `state ${cls}` }, [label]),
        el("div", { class: "muted" }, [g.available ? `${g.passed} passed · ${g.failed} failed` : "run the pipeline"]),
      ]);
      host.append(pill);
    }
  } catch (e) {
    host.textContent = "Data-quality status unavailable.";
  }
}

async function renderRevenue() {
  const chart = document.getElementById("revenue-chart");
  const summary = document.getElementById("revenue-summary");
  try {
    const rows = toObjects(await getJson("/api/dashboard/revenue-trend"));
    chart.innerHTML = "";
    if (rows.length === 0) {
      summary.textContent = "No revenue data yet.";
      return;
    }
    const values = rows.map((r) => Number(r.revenue_usd) || 0);
    const max = Math.max(...values, 1);
    let total = 0;
    values.forEach((v) => {
      total += v;
      const bar = el("div", { class: "bar", style: `height:${Math.max(1, (v / max) * 100)}%`, title: `${v.toFixed(2)} USD` });
      chart.append(bar);
    });
    summary.textContent = `${rows.length} days · total ${total.toLocaleString(undefined, { maximumFractionDigits: 0 })} USD · peak ${max.toLocaleString(undefined, { maximumFractionDigits: 0 })} USD`;
  } catch (e) {
    summary.textContent = "Revenue trend unavailable.";
  }
}

async function renderFunnel() {
  const host = document.getElementById("funnel");
  try {
    const rows = toObjects(await getJson("/api/dashboard/funnel"));
    host.innerHTML = "";
    if (rows.length === 0) {
      host.textContent = "No funnel data yet.";
      return;
    }
    const top = Math.max(...rows.map((r) => Number(r.sessions) || 0), 1);
    rows.forEach((r) => {
      const n = Number(r.sessions) || 0;
      const pct = ((n / top) * 100).toFixed(1);
      host.append(
        el("div", { class: "step" }, [
          el("div", { class: "label" }, [el("span", null, [r.step]), el("span", null, [`${n} (${pct}%)`])]),
          el("div", { class: "track" }, [el("div", { class: "fill", style: `width:${pct}%` })]),
        ])
      );
    });
  } catch (e) {
    host.textContent = "Funnel unavailable.";
  }
}

async function renderCohort() {
  const host = document.getElementById("cohort");
  try {
    const rows = toObjects(await getJson("/api/dashboard/cohort-retention"));
    if (rows.length === 0) {
      host.textContent = "No cohort data yet.";
      return;
    }
    const cohorts = [...new Set(rows.map((r) => r.cohort_month))].sort();
    const maxMonth = Math.max(...rows.map((r) => Number(r.months_since) || 0));
    const base = {};
    rows.forEach((r) => {
      if (Number(r.months_since) === 0) base[r.cohort_month] = Number(r.customers) || 0;
    });
    const lookup = {};
    rows.forEach((r) => (lookup[`${r.cohort_month}|${r.months_since}`] = Number(r.customers) || 0));

    const table = el("table");
    const head = el("tr", null, [el("th", null, ["cohort"])]);
    for (let m = 0; m <= maxMonth; m++) head.append(el("th", null, [`M${m}`]));
    table.append(head);

    cohorts.forEach((c) => {
      const tr = el("tr", null, [el("th", null, [c])]);
      for (let m = 0; m <= maxMonth; m++) {
        const val = lookup[`${c}|${m}`];
        const denom = base[c] || 0;
        if (val === undefined || denom === 0) {
          tr.append(el("td", null, [""]));
        } else {
          const ratio = val / denom;
          const bg = `rgba(74,163,223,${(0.15 + ratio * 0.85).toFixed(2)})`;
          tr.append(el("td", { style: `background:${bg}` }, [`${Math.round(ratio * 100)}%`]));
        }
      }
      table.append(tr);
    });
    host.innerHTML = "";
    host.append(table);
  } catch (e) {
    host.textContent = "Cohort retention unavailable.";
  }
}

(async function main() {
  await Promise.all([renderQuality(), renderRevenue(), renderFunnel(), renderCohort()]);
})();
