"use strict";

// Minimal, dependency-free console for the Agent Platform API.
const state = { token: null, tenant: "tenant-alpha", currentRun: null };

const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => Array.from(document.querySelectorAll(sel));

async function api(path, options = {}) {
  const headers = Object.assign({ "Content-Type": "application/json" }, options.headers || {});
  if (state.token) headers["Authorization"] = "Bearer " + state.token;
  const res = await fetch(path, Object.assign({}, options, { headers }));
  const text = await res.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = text; }
  if (!res.ok) throw { status: res.status, body };
  return body;
}

// ---- auth -------------------------------------------------------------------
async function connect() {
  state.tenant = $("#tenant").value.trim() || "tenant-alpha";
  const body = await api("/api/v1/dev/token", {
    method: "POST",
    body: JSON.stringify({ subject: "console-user", tenant: state.tenant, scopes: ["agents:run", "agents:approve", "agents:admin"] })
  });
  state.token = body.access_token;
  $("#who").textContent = `connected as console-user @ ${state.tenant}`;
  await Promise.all([loadWorkflows(), loadRuns()]);
}

// ---- workflows / runs -------------------------------------------------------
async function loadWorkflows() {
  const wfs = await api("/api/v1/workflows");
  const sel = $("#wf");
  sel.innerHTML = "";
  wfs.forEach((w) => {
    const o = document.createElement("option");
    o.value = JSON.stringify({ name: w.name, version: w.version });
    o.textContent = `${w.name} v${w.version}`;
    sel.appendChild(o);
  });
}

async function startRun() {
  let inputs;
  try { inputs = JSON.parse($("#inputs").value); }
  catch { alert("Inputs must be valid JSON."); return; }
  const { name, version } = JSON.parse($("#wf").value);
  try {
    const run = await api("/api/v1/runs", { method: "POST", body: JSON.stringify({ workflowName: name, version, inputs }) });
    await loadRuns();
    await showTrace(run.id);
  } catch (e) { alert("Start failed: " + JSON.stringify(e.body || e)); }
}

async function loadRuns() {
  const runs = await api("/api/v1/runs");
  const tb = $("#runsTable tbody");
  tb.innerHTML = "";
  runs.forEach((r) => {
    const tr = document.createElement("tr");
    tr.className = "clickable";
    tr.innerHTML = `<td><code>${r.id.slice(0, 8)}</code></td><td>${r.workflow}</td>
      <td><span class="badge ${r.status}">${r.status}</span></td>
      <td>${r.outcome ? `<span class="badge ${r.outcome}">${r.outcome}</span>` : "—"}</td>`;
    tr.onclick = () => showTrace(r.id);
    tb.appendChild(tr);
  });
}

async function showTrace(runId) {
  state.currentRun = runId;
  $("#traceRun").textContent = runId.slice(0, 8);
  const data = await api(`/api/v1/runs/${runId}/trace`);
  const el = $("#trace");
  el.innerHTML = "";
  data.events.forEach((ev) => {
    const div = document.createElement("div");
    div.className = "ev " + ev.type;
    const meta = [];
    if (ev.tool) meta.push("tool=" + ev.tool);
    if (ev.promptVersion) meta.push("prompt=" + ev.promptVersion);
    if (ev.completionTokens) meta.push(ev.promptTokens + "→" + ev.completionTokens + "tok");
    if (ev.durationMs) meta.push(ev.durationMs + "ms");
    if (!ev.success) meta.push("FAILED");
    div.innerHTML = `<div class="top"><span class="type">#${ev.ordinal} ${ev.type}</span>
      <span class="muted">${ev.stepId || ""} ${meta.join(" · ")}</span></div>
      <pre>${escapeHtml(JSON.stringify(ev.data, null, 2))}</pre>`;
    el.appendChild(div);
  });
}

// ---- approvals --------------------------------------------------------------
async function loadApprovals() {
  const list = await api("/api/v1/approvals");
  const el = $("#approvalsList");
  el.innerHTML = list.length ? "" : '<p class="muted">No pending approvals.</p>';
  list.forEach((a) => {
    const div = document.createElement("div");
    div.className = "card";
    div.innerHTML = `<div><strong>${a.title}</strong> <span class="badge ${a.status}">${a.status}</span>
      <span class="muted">risk=${a.riskLevel}</span></div>
      <pre>${escapeHtml(JSON.stringify(a.proposedAction, null, 2))}</pre>
      <div class="muted">${escapeHtml(a.reasoningTrace || "")}</div>
      <div class="actions">
        <button class="ok" data-a="${a.id}" data-d="approve">Approve</button>
        <button class="danger" data-a="${a.id}" data-d="reject">Reject</button>
      </div>`;
    el.appendChild(div);
  });
  $$("#approvalsList button").forEach((b) => b.onclick = () => decide(b.dataset.a, b.dataset.d));
}

async function decide(id, decision) {
  try {
    await api(`/api/v1/approvals/${id}/${decision}`, { method: "POST", body: JSON.stringify({ notes: "via console" }) });
    await loadApprovals();
    if (state.currentRun) await showTrace(state.currentRun);
    await loadRuns();
  } catch (e) { alert("Decision failed: " + JSON.stringify(e.body || e)); }
}

// ---- tools / prompts --------------------------------------------------------
async function loadTools() {
  const tools = await api("/api/v1/tools");
  const tb = $("#toolsTable tbody");
  tb.innerHTML = "";
  tools.forEach((t) => {
    const tr = document.createElement("tr");
    tr.innerHTML = `<td><code>${t.name}</code> <span class="muted">v${t.version}</span></td>
      <td>${t.sideEffect}</td><td>${t.riskLevel}</td>
      <td class="muted">${(t.requiredScopes || []).join(", ") || "—"}</td>
      <td>${t.requiresApproval ? "yes" : "no"}</td>`;
    tb.appendChild(tr);
  });
}

async function loadPrompts() {
  const prompts = await api("/api/v1/prompts");
  const el = $("#promptsList");
  el.innerHTML = "";
  prompts.forEach((p) => {
    const div = document.createElement("div");
    div.className = "card";
    div.innerHTML = `<div><strong>${p.name}</strong> <span class="muted">v${p.version} — ${escapeHtml(p.description || "")}</span></div>
      <div class="muted">variables: ${(p.variables || []).map((v) => `<code>${v}</code>`).join(" ") || "none"}</div>
      <pre>${escapeHtml(p.body)}</pre>`;
    el.appendChild(div);
  });
}

// ---- evals ------------------------------------------------------------------
async function runEvals() {
  $("#evalStatus").textContent = "running…";
  try {
    const report = await api("/api/v1/evals/run", { method: "POST", body: JSON.stringify({}) });
    $("#evalStatus").textContent = `done at ${new Date(report.generatedAt).toLocaleTimeString()}`;
    renderEvals(report);
  } catch (e) { $("#evalStatus").textContent = "failed: " + JSON.stringify(e.body || e); }
}

function renderEvals(r) {
  const pct = (x) => (x * 100).toFixed(1) + "%";
  const el = $("#evalResults");
  el.innerHTML = `<div class="scoregrid">
    ${metric("Scenarios", r.totalScenarios)}
    ${metric("Passed", `${r.totalPassed}/${r.totalScenarios}`)}
    ${metric("Task success", pct(r.overallTaskSuccess))}
    ${metric("Tool accuracy", pct(r.overallToolSelectionAccuracy))}
    ${metric("Unauth. handling", pct(r.overallUnauthorisedHandling))}
    ${metric("Approval correct", pct(r.overallApprovalCorrectness))}
    ${metric("Budget adherence", pct(r.overallBudgetAdherence))}
    ${metric("Unauth. blocked", r.totalUnauthorisedAttemptsBlocked)}
    ${metric("Total tokens", r.totalTokens)}
    ${metric("Total cost", "$" + Number(r.totalCostUsd).toFixed(4))}
    ${metric("Mean latency", r.meanLatencyMs.toFixed(1) + "ms")}
  </div>
  <table><thead><tr><th>Workflow</th><th>Scenarios</th><th>Passed</th><th>Task</th><th>Tools</th><th>Unauth</th><th>Approval</th><th>Budget</th></tr></thead><tbody>
  ${r.workflows.map((w) => `<tr><td>${w.workflowName}</td><td>${w.scenarioCount}</td><td>${w.passed}</td>
    <td>${pct(w.taskSuccess)}</td><td>${pct(w.toolSelectionAccuracy)}</td><td>${pct(w.unauthorisedHandling)}</td>
    <td>${pct(w.approvalCorrectness)}</td><td>${pct(w.budgetAdherence)}</td></tr>`).join("")}
  </tbody></table>`;
}

const metric = (l, n) => `<div class="metric"><div class="n">${n}</div><div class="l">${l}</div></div>`;

// ---- metrics ----------------------------------------------------------------
async function loadMetrics() {
  const m = await api("/api/v1/metrics");
  const tb = $("#metricsTable tbody");
  tb.innerHTML = "";
  Object.keys(m).sort().forEach((k) => {
    const tr = document.createElement("tr");
    tr.innerHTML = `<td><code>${k}</code></td><td>${m[k]}</td>`;
    tb.appendChild(tr);
  });
  if (!Object.keys(m).length) tb.innerHTML = '<tr><td colspan="2" class="muted">No metrics yet — start a run.</td></tr>';
}

// ---- misc -------------------------------------------------------------------
function escapeHtml(s) {
  return String(s == null ? "" : s).replace(/[&<>]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]));
}

function switchTab(name) {
  $$("nav button").forEach((b) => b.classList.toggle("active", b.dataset.tab === name));
  $$(".tab").forEach((t) => t.classList.toggle("active", t.id === name));
  if (!state.token) return;
  if (name === "approvals") loadApprovals();
  if (name === "tools") loadTools();
  if (name === "prompts") loadPrompts();
  if (name === "metrics") loadMetrics();
}

// ---- wire up ----------------------------------------------------------------
$("#connect").onclick = () => connect().catch((e) => alert("Connect failed: " + JSON.stringify(e.body || e)));
$("#start").onclick = startRun;
$("#refreshRuns").onclick = loadRuns;
$("#refreshApprovals").onclick = loadApprovals;
$("#refreshTools").onclick = loadTools;
$("#refreshPrompts").onclick = loadPrompts;
$("#refreshMetrics").onclick = loadMetrics;
$("#runEvals").onclick = runEvals;
$$("nav button").forEach((b) => b.onclick = () => switchTab(b.dataset.tab));

// auto-connect for convenience in dev
connect().catch(() => { $("#who").textContent = "click Connect to mint a dev token"; });
