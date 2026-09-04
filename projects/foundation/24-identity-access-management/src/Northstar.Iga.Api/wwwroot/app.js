let token = "";

async function connect() {
  const response = await fetch("/api/v1/auth/token", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ subject: "admin-ui", scopes: ["iga.read", "iga.admin", "iga.approve"] })
  });
  if (!response.ok) throw new Error("Local development token endpoint is unavailable.");
  token = (await response.json()).accessToken;
  const connection = document.querySelector("#connection");
  connection.textContent = "Local API connected";
  connection.classList.add("connected");
}

async function api(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}`, ...(options.headers || {}) }
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => ({ title: response.statusText }));
    throw new Error(`${problem.title || "Request failed"}: ${problem.detail || response.status}`);
  }
  if (response.status === 204) return null;
  return response.json();
}

function activate(panel) {
  document.querySelectorAll(".panel").forEach(x => x.classList.toggle("active", x.id === panel));
}

async function loadUsers() {
  const data = await api("/api/v1/users?page=1&pageSize=50");
  document.querySelector("#users-body").innerHTML = data.items.map(user => `
    <tr>
      <td>${user.displayName}<br><small>${user.email}</small></td>
      <td>${user.department}</td><td>${user.jobTitle}</td><td>${user.status}</td>
      <td><button class="profile-button" data-user="${user.id}">Why access?</button></td>
    </tr>`).join("");
  document.querySelectorAll(".profile-button").forEach(button =>
    button.addEventListener("click", () => loadProfile(button.dataset.user)));
}

async function loadProfile(userId) {
  activate("profile");
  const profile = await api(`/api/v1/reports/users/${userId}/access-profile`);
  document.querySelector("#profile-help").textContent = `${profile.displayName} · ${profile.access.length} effective entitlements`;
  document.querySelector("#profile-content").innerHTML = profile.access.map(access => `
    <article class="card">
      <h3>${access.entitlementKey}</h3>
      <p><code>${access.permission}</code> · <span class="risk-${access.risk}">${access.risk}</span></p>
      ${access.derivationPaths.map(path => `<div class="path">${path.join(" → ")}</div>`).join("")}
    </article>`).join("") || "<p>No effective access.</p>";
}

async function loadRequests() {
  const requests = await api("/api/v1/requests");
  const pending = requests.filter(x => x.status === "Pending");
  document.querySelector("#requests-content").innerHTML = (await Promise.all(pending.map(async request => {
    const steps = await api(`/api/v1/requests/${request.id}/steps`);
    return `<article class="card"><h3>${request.targetType} request</h3>
      <p>${request.justification}</p><p>Risk: <strong>${request.risk}</strong> · stage ${request.currentStage}</p>
      ${steps.map(step => `<div class="path">${step.stage}. ${step.kind} — ${step.status} — approver ${step.approverId}</div>`).join("")}
    </article>`;
  }))).join("") || "<p>No requests awaiting approval.</p>";
}

async function loadCampaigns() {
  const campaigns = await api("/api/v1/campaigns");
  document.querySelector("#campaign-content").innerHTML = (await Promise.all(campaigns.map(async campaign => {
    const progress = await api(`/api/v1/campaigns/${campaign.id}/progress`);
    return `<article class="card"><h3>${campaign.name}</h3><p>${campaign.scopeType}: ${campaign.scopeValue}</p>
      <p>${progress.completionPercent}% complete · ${progress.pending}/${progress.total} pending · deadline ${campaign.deadline}</p></article>`;
  }))).join("") || "<p>No campaigns.</p>";
}

async function loadSod() {
  const violations = await api("/api/v1/sod/violations");
  document.querySelector("#sod-content").innerHTML = violations.map(item => `
    <article class="card violation"><h3>${item.ruleName}</h3>
      <p>User ${item.userId} · ${item.severity} · exception ${item.hasActiveException ? `until ${item.exceptionExpiresAt}` : "none"}</p>
    </article>`).join("") || "<p>No toxic combinations detected.</p>";
}

async function simulate(event) {
  event.preventDefault();
  const permission = document.querySelector("#sim-permission").value;
  const department = document.querySelector("#sim-department").value;
  const networkZone = document.querySelector("#sim-network").value;
  const effect = document.querySelector("#sim-effect").value;
  const result = await api("/api/v1/policies/simulate", {
    method: "POST",
    body: JSON.stringify({
      proposedPolicy: {
        name: "Admin UI what-if policy",
        description: "Unsaved policy evaluated by the simulator.",
        effect,
        permissionPattern: permission,
        priority: 500,
        conditionsJson: JSON.stringify({ subject: { department }, environment: { networkZone } }),
        enabled: true
      },
      permission,
      resource: {},
      environment: { networkZone, mfaLevel: 2, deviceTrust: "Trusted" }
    })
  });
  document.querySelector("#simulation-output").textContent = JSON.stringify(result, null, 2);
}

document.querySelectorAll("nav button").forEach(button =>
  button.addEventListener("click", () => activate(button.dataset.panel)));
document.querySelector("#refresh-users").addEventListener("click", loadUsers);
document.querySelector("#refresh-requests").addEventListener("click", loadRequests);
document.querySelector("#refresh-campaigns").addEventListener("click", loadCampaigns);
document.querySelector("#refresh-sod").addEventListener("click", loadSod);
document.querySelector("#simulation-form").addEventListener("submit", simulate);

connect()
  .then(() => Promise.all([loadUsers(), loadRequests(), loadCampaigns(), loadSod()]))
  .catch(error => {
    document.querySelector("#connection").textContent = error.message;
    document.querySelector("#connection").classList.add("violation");
  });
