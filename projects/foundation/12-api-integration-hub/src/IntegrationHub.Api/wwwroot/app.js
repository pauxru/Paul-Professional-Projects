const api = async (path, options = {}) => {
  const token = localStorage.getItem("hub-token");
  const headers = {"Content-Type":"application/json", ...(options.headers || {})};
  if (token) headers.Authorization = `Bearer ${token}`;
  const response = await fetch(path, {...options, headers});
  if (!response.ok) throw new Error(`${response.status}: ${await response.text()}`);
  return response.status === 204 ? null : response.json();
};
const escapeHtml = value => String(value ?? "").replace(/[&<>"']/g, x => ({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"}[x]));
const table = (headers, rows) => `<table><thead><tr>${headers.map(x=>`<th>${escapeHtml(x)}</th>`).join("")}</tr></thead><tbody>${rows.join("")}</tbody></table>`;
const content = document.querySelector("#content");
const fail = error => { if(content) content.innerHTML = `<p class="bad">${escapeHtml(error.message)}</p>`; };
const page = document.body.dataset.page;

if (page === "home") {
  document.querySelector("#login-form").addEventListener("submit", async event => {
    event.preventDefault();
    try {
      const result = await api("/api/v1/auth/token", {method:"POST",body:JSON.stringify({
        clientId:document.querySelector("#client-id").value,
        clientSecret:document.querySelector("#client-secret").value,
        scopes:["hub.read","hub.write","hub.admin"]
      })});
      localStorage.setItem("hub-token", result.access_token);
      document.querySelector("#login-status").innerHTML = '<span class="ok">Authenticated for one hour.</span>';
    } catch(error) { document.querySelector("#login-status").textContent = error.message; }
  });
}
if (page === "connectors") {
  const load = async () => { try {
    const items = await api("/api/v1/connectors");
    content.innerHTML = table(["Connector","Version","Auth","Pagination","Operations"], items.map(x =>
      `<tr><td><strong>${escapeHtml(x.displayName)}</strong><br>${escapeHtml(x.id)}</td><td>${escapeHtml(x.version)}</td><td>${escapeHtml(x.authKind)}</td><td>${escapeHtml(x.paginationStyle)}</td><td>${x.operations.map(o=>escapeHtml(o.name)).join("<br>")}</td></tr>`));
  } catch(error){ fail(error); } };
  document.querySelector("#refresh").onclick=load; load();
}
if (page === "flows") {
  const load = async () => { try {
    const items = await api("/api/v1/flows");
    content.innerHTML = table(["Name","Active","Versions","Actions"], items.map(x =>
      `<tr><td>${escapeHtml(x.name)}</td><td>${escapeHtml(x.activeVersion ?? "inactive")}</td><td>${x.versions.length}</td><td><button data-run="${x.id}">Run</button> <button data-rollback="${x.id}">Rollback</button></td></tr>`));
    document.querySelectorAll("[data-run]").forEach(button => button.onclick = async () => { await api(`/api/v1/flows/${button.dataset.run}/runs`,{method:"POST",body:'{"payload":{}}'}); await load(); });
    document.querySelectorAll("[data-rollback]").forEach(button => button.onclick = async () => { await api(`/api/v1/flows/${button.dataset.rollback}/rollback`,{method:"POST",body:"{}"}); await load(); });
  } catch(error){ fail(error); } };
  document.querySelector("#refresh").onclick=load; load();
}
if (page === "runs") {
  const load = async () => { try {
    const status = document.querySelector("#status").value;
    const items = await api(`/api/v1/runs?page=1&pageSize=100${status?`&status=${encodeURIComponent(status)}`:""}`);
    content.innerHTML = table(["Started","Status","Records","Correlation","Details"], items.map(x =>
      `<tr><td>${escapeHtml(x.startedAt)}</td><td>${escapeHtml(x.status)}</td><td>${x.recordsProcessed}</td><td>${escapeHtml(x.correlationId)}</td><td><button data-details="${x.id}">View timeline</button></td></tr>`));
    document.querySelectorAll("[data-details]").forEach(button => button.onclick = async () => {
      document.querySelector("#details").textContent = JSON.stringify(await api(`/api/v1/runs/${button.dataset.details}`), null, 2);
    });
  } catch(error){ fail(error); } };
  document.querySelector("#refresh").onclick=load; load();
}
if (page === "deadletters") {
  const load = async () => { try {
    const items = await api("/api/v1/dead-letters?status=Pending");
    content.innerHTML = table(["Created","Step","Record","Error","Action"], items.map(x =>
      `<tr><td>${escapeHtml(x.createdAt)}</td><td>${escapeHtml(x.stepId)}</td><td>${escapeHtml(x.recordKey)}</td><td>${escapeHtml(x.error)}</td><td><button data-replay="${x.id}">Replay</button></td></tr>`));
    document.querySelectorAll("[data-replay]").forEach(button => button.onclick = async () => {
      await api("/api/v1/dead-letters/replay",{method:"POST",body:JSON.stringify({itemId:button.dataset.replay})}); await load();
    });
  } catch(error){ fail(error); } };
  document.querySelector("#refresh").onclick=load; load();
}
if (page === "mapping") {
  document.querySelector("#run-mapping").onclick = async () => { try {
    const result = await api("/api/v1/mappings/test",{method:"POST",body:JSON.stringify({
      input:JSON.parse(document.querySelector("#input").value),
      mappings:JSON.parse(document.querySelector("#mappings").value)
    })});
    content.textContent=JSON.stringify(result,null,2);
  } catch(error){ fail(error); } };
}
