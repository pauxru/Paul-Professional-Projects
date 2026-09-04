"use strict";

// Utilitarian review console. In Development the API exposes /api/v1/dev/token which mints a JWT
// with all reviewer permissions; the console uses it as a bearer token for every call.
const state = { token: null, taskId: null, documentId: null };

async function getToken() {
  if (state.token) return state.token;
  const res = await fetch("/api/v1/dev/token", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ subject: "review-console", permissions: null })
  });
  if (!res.ok) throw new Error("Unable to obtain a dev token (is the API in Development?).");
  const data = await res.json();
  state.token = data.access_token;
  return state.token;
}

async function api(path, options = {}) {
  const token = await getToken();
  const headers = Object.assign(
    { "Authorization": "Bearer " + token }, options.headers || {});
  if (options.body && !headers["Content-Type"]) headers["Content-Type"] = "application/json";
  const res = await fetch(path, Object.assign({}, options, { headers }));
  if (res.status === 204) return null;
  const text = await res.text();
  const payload = text ? JSON.parse(text) : null;
  if (!res.ok) {
    const detail = payload && (payload.detail || payload.message) || res.statusText;
    throw new Error(detail);
  }
  return payload;
}

function confClass(c) { return c >= 0.85 ? "high" : c >= 0.6 ? "mid" : "low"; }
function fmtConf(c) { return (c * 100).toFixed(0) + "%"; }
function setStatus(msg, ok) {
  const el = document.getElementById("status");
  el.textContent = msg || "";
  el.className = "status " + (ok ? "ok" : msg ? "err" : "");
}

async function loadMetrics() {
  try {
    const m = await api("/api/v1/metrics/stp");
    document.getElementById("metrics").innerHTML =
      chip("STP rate", (m.straightThroughRate * 100).toFixed(1) + "%") +
      chip("Processed", m.processed) +
      chip("Auto-approved", m.autoApproved) +
      chip("In review", m.inReview) +
      chip("Exported", m.exported) +
      chip("Queue depth", m.reviewQueueDepth);
  } catch (e) { setStatus(e.message, false); }
}
function chip(label, value) {
  return '<span class="chip">' + label + ': <b>' + value + "</b></span>";
}

async function loadQueue() {
  const body = document.getElementById("queue-body");
  try {
    const items = await api("/api/v1/review/queue?limit=100");
    if (!items.length) { body.innerHTML = '<tr><td colspan="7">Queue is empty.</td></tr>'; return; }
    body.innerHTML = items.map(function (t) {
      return '<tr data-task="' + t.taskId + '" data-doc="' + t.documentId + '">' +
        "<td>" + t.priority + "</td>" +
        "<td>" + escapeHtml(t.fileName) + "</td>" +
        "<td>" + t.documentType + "</td>" +
        "<td>" + (t.documentValue != null ? t.documentValue : "-") + "</td>" +
        '<td><span class="conf ' + confClass(t.documentConfidence) + '">' +
          fmtConf(t.documentConfidence) + "</span></td>" +
        "<td>" + t.ageMinutes + "</td>" +
        "<td>" + (t.overdue ? "OVERDUE" : "ok") + "</td>" +
      "</tr>";
    }).join("");
    Array.prototype.forEach.call(body.querySelectorAll("tr"), function (row) {
      row.addEventListener("click", function () {
        openDocument(row.getAttribute("data-task"), row.getAttribute("data-doc"));
      });
    });
  } catch (e) { body.innerHTML = '<tr><td colspan="7">' + escapeHtml(e.message) + "</td></tr>"; }
}

async function openDocument(taskId, documentId) {
  state.taskId = taskId;
  state.documentId = documentId;
  try {
    const d = await api("/api/v1/documents/" + documentId);
    document.getElementById("detail-empty").hidden = true;
    document.getElementById("detail").hidden = false;
    document.getElementById("detail-state").textContent = d.state;
    document.getElementById("detail-meta").innerHTML =
      "<b>" + escapeHtml(d.fileName) + "</b> · " + d.documentType +
      " · classified " + fmtConf(d.classificationConfidence) +
      " · document confidence " + fmtConf(d.documentConfidence) +
      (d.currency ? " · " + d.currency : "") +
      (d.supplierNameRaw ? " · supplier: " + escapeHtml(d.supplierNameRaw) : "");

    document.getElementById("fields-body").innerHTML = d.fields.map(function (f) {
      const box = f.box
        ? ("[p" + f.box.page + " x" + f.box.x.toFixed(0) + " y" + f.box.y.toFixed(0) + "]")
        : "";
      const evidence = f.evidence ? escapeHtml(f.evidence) : "";
      return "<tr>" +
        "<td>" + f.fieldKey + (f.isRequired ? " *" : "") + "</td>" +
        '<td><input class="field-edit" data-field="' + f.fieldKey + '" value="' +
          escapeAttr(f.value != null ? f.value : "") + '" /></td>' +
        '<td><span class="conf ' + confClass(f.confidence) + '">' + fmtConf(f.confidence) + "</span></td>" +
        "<td>" + f.strategy + "</td>" +
        '<td class="evidence">' + evidence + " " + box + "</td>" +
      "</tr>";
    }).join("");

    document.getElementById("lines-body").innerHTML = d.lineItems.map(function (l) {
      return "<tr><td>" + l.lineNumber + "</td><td>" + escapeHtml(l.description || "") + "</td>" +
        "<td>" + n(l.quantity) + "</td><td>" + n(l.unitPrice) + "</td>" +
        "<td>" + n(l.lineTotal) + "</td><td>" + n(l.taxRate) + "</td></tr>";
    }).join("");

    document.getElementById("validations").innerHTML = d.validations.map(function (v) {
      return '<li class="v-' + v.outcome + '">' + escapeHtml(v.ruleName) + ": " +
        escapeHtml(v.message) + "</li>";
    }).join("");
    setStatus("Loaded " + d.fileName, true);
  } catch (e) { setStatus(e.message, false); }
}

function reviewer() { return document.getElementById("reviewer").value || "reviewer-1"; }

async function claim() {
  try {
    await api("/api/v1/review/" + state.taskId + "/claim", {
      method: "POST", body: JSON.stringify({ reviewer: reviewer() })
    });
    setStatus("Task claimed.", true);
  } catch (e) { setStatus(e.message, false); }
}

async function saveCorrections() {
  const inputs = document.querySelectorAll("#fields-body input.field-edit");
  const corrections = [];
  Array.prototype.forEach.call(inputs, function (i) {
    corrections.push({ fieldKey: i.getAttribute("data-field"), newValue: i.value, reason: "console edit" });
  });
  try {
    await api("/api/v1/review/" + state.taskId + "/correct", {
      method: "POST", body: JSON.stringify({ reviewer: reviewer(), corrections: corrections })
    });
    setStatus("Corrections saved and fed to the per-supplier learning loop.", true);
    await openDocument(state.taskId, state.documentId);
  } catch (e) { setStatus(e.message, false); }
}

async function approve() {
  try {
    const r = await api("/api/v1/review/" + state.taskId + "/approve", {
      method: "POST", body: JSON.stringify({ reviewer: reviewer() })
    });
    setStatus("Approved. Export status: " + (r && r.exportStatus), true);
    await refreshAll();
  } catch (e) { setStatus(e.message, false); }
}

async function reject() {
  try {
    await api("/api/v1/review/" + state.taskId + "/reject", {
      method: "POST", body: JSON.stringify({ reviewer: reviewer(), reason: "rejected in console" })
    });
    setStatus("Rejected.", true);
    await refreshAll();
  } catch (e) { setStatus(e.message, false); }
}

async function refreshAll() { await Promise.all([loadMetrics(), loadQueue()]); }

function n(v) { return v == null ? "-" : v; }
function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, function (c) {
    return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
  });
}
function escapeAttr(s) { return escapeHtml(s).replace(/"/g, "&quot;"); }

document.getElementById("refresh-btn").addEventListener("click", refreshAll);
document.getElementById("claim-btn").addEventListener("click", claim);
document.getElementById("save-btn").addEventListener("click", saveCorrections);
document.getElementById("approve-btn").addEventListener("click", approve);
document.getElementById("reject-btn").addEventListener("click", reject);

refreshAll();
