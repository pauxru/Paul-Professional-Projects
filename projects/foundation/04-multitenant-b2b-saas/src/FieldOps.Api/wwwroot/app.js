const state = { token: null, tenant: null };
const $ = (selector) => document.querySelector(selector);

function toast(message) {
  const element = $("#toast");
  element.textContent = message;
  element.classList.add("visible");
  setTimeout(() => element.classList.remove("visible"), 2600);
}

async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  headers.set("Authorization", `Bearer ${state.token}`);
  headers.set("X-Tenant", state.tenant);
  if (options.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
  const response = await fetch(path, { ...options, headers });
  if (!response.ok) {
    const problem = await response.json().catch(() => ({ title: response.statusText }));
    throw new Error(problem.detail || problem.title || `HTTP ${response.status}`);
  }
  return response.status === 204 ? null : response.json();
}

function rows(target, items, render) {
  const element = $(target);
  element.classList.remove("empty");
  element.innerHTML = items.length ? items.map(render).join("") : "<div class='empty'>No records.</div>";
}

async function loadUsage() {
  const data = await api("/api/v1/usage");
  $("#plan").textContent = data.plan;
  $("#jobUsage").textContent = `${data.usage.jobsCreated} / ${data.usage.jobsHardLimit}`;
  $("#assetLimit").textContent = data.usage.maxAssets.toLocaleString();
  $("#apiLimit").textContent = data.usage.apiRequestsPerMinute.toLocaleString();
}

async function loadJobs() {
  const data = await api("/api/v1/jobs?page=1&pageSize=20&sort=sla");
  rows("#jobs", data.items, job => `
    <div class="row">
      <strong>${escapeHtml(job.title)}</strong>
      <span class="pill">${job.status}</span>
      <small>${job.priority}</small>
      <small>${new Date(job.slaDueAt).toLocaleString()}</small>
    </div>`);
}

async function loadFlags() {
  const data = await api("/api/v1/feature-flags");
  rows("#flags", data, flag => `
    <div class="row">
      <strong>${escapeHtml(flag.key)}</strong>
      <span class="pill">${flag.enabled ? "enabled" : "disabled"}</span>
      <small>${flag.rolloutPercentage}% rollout</small>
      <button class="flag-toggle" data-key="${escapeHtml(flag.key)}"
        data-enabled="${flag.enabled}" data-rollout="${flag.rolloutPercentage}">
        ${flag.enabled ? "Disable" : "Enable"}
      </button>
    </div>`);
}

async function loadAudit() {
  const data = await api("/api/v1/audit?page=1&pageSize=20");
  rows("#audit", data.items, event => `
    <div class="row">
      <strong>${escapeHtml(event.action)}</strong>
      <small>${escapeHtml(event.resource)}</small>
      <small>${escapeHtml(event.actorId)}</small>
      <small>${new Date(event.occurredAt).toLocaleString()}</small>
    </div>`);
}

async function loadTenants() {
  const data = await api("/api/v1/admin/tenants");
  rows("#tenants", data, tenant => `
    <div class="row">
      <strong>${escapeHtml(tenant.name)}</strong>
      <span class="pill">${tenant.status}</span>
      <small>${tenant.plan}</small>
      <small>${escapeHtml(tenant.region)}</small>
    </div>`);
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

async function connect() {
  state.tenant = $("#tenant").value;
  const response = await fetch("/api/v1/auth/token", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email: $("#identity").value, tenantSlug: state.tenant })
  });
  if (!response.ok) throw new Error(`Token request failed (${response.status}).`);
  const session = await response.json();
  state.token = session.accessToken;
  $("#session").textContent = `${session.role} · ${session.tenant.slug}`;
  await Promise.all([loadUsage(), loadJobs(), loadFlags()]);
  try { await loadAudit(); } catch { $("#audit").textContent = "This role cannot read audit events."; }
  if ($("#identity").value === "platform@fieldops.demo") await loadTenants();
}

$("#connect").addEventListener("click", () => connect().catch(error => toast(error.message)));
document.querySelectorAll("[data-refresh]").forEach(button => {
  button.addEventListener("click", () => {
    if (!state.token) return toast("Connect first.");
    const actions = { jobs: loadJobs, flags: loadFlags, audit: loadAudit, tenants: loadTenants };
    actions[button.dataset.refresh]().catch(error => toast(error.message));
  });
});

$("#flags").addEventListener("click", event => {
  const button = event.target.closest(".flag-toggle");
  if (!button) return;
  api(`/api/v1/feature-flags/${encodeURIComponent(button.dataset.key)}`, {
    method: "PUT",
    body: JSON.stringify({
      enabled: button.dataset.enabled !== "true",
      rolloutPercentage: Number(button.dataset.rollout),
      killSwitch: false
    })
  }).then(loadFlags).catch(error => toast(error.message));
});
