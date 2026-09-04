namespace JobScheduler.Api.Dashboard;

/// <summary>
/// Serves a single-page, dependency-free operations dashboard. It obtains a development token from
/// the auth endpoint, then renders job definitions, the upcoming schedule, recent runs, worker
/// nodes with heartbeat age, and the dead-letter queue with a replay action and a "trigger now"
/// button.
/// </summary>
public static class DashboardEndpoint
{
    public static void MapDashboard(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Content(Html, "text/html; charset=utf-8"))
            .WithTags("Dashboard")
            .AllowAnonymous()
            .DisableRateLimiting();
    }

    private const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>Northstar Job Scheduler (fictional)</title>
<style>
  :root { color-scheme: light dark; }
  body { font-family: system-ui, Segoe UI, Roboto, sans-serif; margin: 0; background: #0f1420; color: #e6e9ef; }
  header { padding: 12px 20px; background: #161c2b; border-bottom: 1px solid #263043; display:flex; align-items:center; gap:16px; }
  header h1 { font-size: 16px; margin: 0; font-weight: 600; }
  header .sub { color:#8794ad; font-size:12px; }
  main { padding: 16px 20px; display: grid; gap: 18px; grid-template-columns: 1fr 1fr; }
  section { background:#141a28; border:1px solid #263043; border-radius:8px; padding:12px 14px; overflow:auto; }
  section.wide { grid-column: 1 / -1; }
  h2 { font-size: 13px; text-transform: uppercase; letter-spacing:.06em; color:#8794ad; margin:0 0 10px; }
  table { width:100%; border-collapse: collapse; font-size:12px; }
  th, td { text-align:left; padding:6px 8px; border-bottom:1px solid #212a3d; white-space:nowrap; }
  th { color:#8794ad; font-weight:600; }
  button { background:#2b6cff; color:#fff; border:0; border-radius:5px; padding:5px 9px; cursor:pointer; font-size:12px; }
  button.secondary { background:#33405c; }
  .pill { padding:2px 7px; border-radius:999px; font-size:11px; font-weight:600; }
  .s-Pending{background:#3a3f52;} .s-Claimed{background:#5a4a1f;} .s-Running{background:#1f4d6b;}
  .s-Succeeded{background:#1f5b34;} .s-Failed{background:#6b2020;} .s-Retrying{background:#5a3d1f;}
  .s-TimedOut{background:#6b2020;} .s-Cancelled{background:#444;} .s-DeadLettered{background:#7a1f1f;}
  .muted{color:#8794ad;} .err{color:#ff8a8a;}
  .row{display:flex; gap:8px; align-items:center; margin-bottom:8px; flex-wrap:wrap;}
  select,input{background:#0f1420;color:#e6e9ef;border:1px solid #263043;border-radius:5px;padding:5px;}
</style>
</head>
<body>
<header>
  <h1>Northstar Job Scheduler</h1>
  <span class="sub">fictional operator &middot; distributed lease/fencing demo</span>
  <span id="leader" class="sub"></span>
  <span id="err" class="err"></span>
</header>
<main>
  <section class="wide">
    <h2>Job Definitions</h2>
    <div class="row">
      <select id="triggerSelect"></select>
      <button onclick="triggerSelected()">Trigger now</button>
      <button class="secondary" onclick="refresh()">Refresh</button>
      <span class="muted" id="autorefresh"></span>
    </div>
    <table id="defs"><thead><tr>
      <th>Name</th><th>Handler</th><th>Trigger</th><th>Queue</th><th>Enabled</th><th>Owner</th><th>Last fire</th>
    </tr></thead><tbody></tbody></table>
  </section>

  <section>
    <h2>Upcoming Schedule</h2>
    <table id="upcoming"><thead><tr><th>Job</th><th>Next (UTC)</th><th>Local</th><th>TZ</th></tr></thead><tbody></tbody></table>
  </section>

  <section>
    <h2>Worker Nodes</h2>
    <table id="workers"><thead><tr><th>Node</th><th>Status</th><th>Tags</th><th>Slots</th><th>Heartbeat age</th></tr></thead><tbody></tbody></table>
  </section>

  <section class="wide">
    <h2>Recent Runs</h2>
    <table id="runs"><thead><tr>
      <th>Job</th><th>State</th><th>Attempt</th><th>Node</th><th>Fencing</th><th>Scheduled</th><th>Finished</th><th></th>
    </tr></thead><tbody></tbody></table>
  </section>

  <section class="wide">
    <h2>Dead-Letter Queue</h2>
    <table id="dlq"><thead><tr><th>Job</th><th>Reason</th><th>Attempts</th><th>When</th><th>Replayed</th><th></th></tr></thead><tbody></tbody></table>
  </section>
</main>
<script>
let token = null;
async function getToken(){
  const r = await fetch('/api/v1/auth/token', {method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({})});
  if(!r.ok){ throw new Error('token '+r.status); }
  token = (await r.json()).accessToken;
}
async function api(path, opts){
  opts = opts || {};
  opts.headers = Object.assign({'Authorization':'Bearer '+token, 'Content-Type':'application/json'}, opts.headers||{});
  const r = await fetch(path, opts);
  if(r.status === 401){ await getToken(); return api(path, opts); }
  return r;
}
function pill(s){ return '<span class="pill s-'+s+'">'+s+'</span>'; }
function fmt(t){ return t ? new Date(t).toLocaleString() : '<span class="muted">—</span>'; }

async function refresh(){
  try {
    document.getElementById('err').textContent='';
    if(!token) await getToken();
    const [defs, up, runs, workers, dlq, leader] = await Promise.all([
      api('/api/v1/jobs?pageSize=100').then(r=>r.json()),
      api('/api/v1/schedule/upcoming?count=15').then(r=>r.json()),
      api('/api/v1/runs?pageSize=25').then(r=>r.json()),
      api('/api/v1/workers').then(r=>r.json()),
      api('/api/v1/dlq?includeReplayed=false&pageSize=25').then(r=>r.json()),
      api('/api/v1/leader').then(r=>r.json())
    ]);

    const sel = document.getElementById('triggerSelect');
    sel.innerHTML = defs.items.map(d=>'<option value="'+d.id+'">'+d.name+'</option>').join('');
    document.querySelector('#defs tbody').innerHTML = defs.items.map(d=>
      '<tr><td>'+d.name+'</td><td>'+d.handlerType+'</td><td>'+d.triggerType+'</td><td>'+d.queue+'</td><td>'+(d.enabled?'yes':'no')+'</td><td>'+d.owner+'</td><td>'+fmt(d.lastFireAt)+'</td></tr>').join('');

    document.querySelector('#upcoming tbody').innerHTML = up.map(u=>
      '<tr><td>'+u.jobName+'</td><td>'+fmt(u.nextFireUtc)+'</td><td>'+fmt(u.nextFireLocal)+'</td><td>'+u.timeZoneId+'</td></tr>').join('') || '<tr><td colspan=4 class=muted>none</td></tr>';

    document.querySelector('#runs tbody').innerHTML = runs.items.map(r=>
      '<tr><td>'+r.jobName+'</td><td>'+pill(r.state)+'</td><td>'+r.attemptCount+'/'+r.maxAttempts+'</td><td>'+(r.leaseOwner||'—')+'</td><td>'+r.fencingToken+'</td><td>'+fmt(r.scheduledAt)+'</td><td>'+fmt(r.finishedAt)+'</td>'+
      '<td>'+((r.state==='Running'||r.state==='Claimed'||r.state==='Pending')?'<button class="secondary" onclick="cancelRun(\''+r.id+'\')">Cancel</button>':'')+'</td></tr>').join('');

    const now = Date.now();
    document.querySelector('#workers tbody').innerHTML = workers.map(w=>
      '<tr><td>'+w.nodeId+'</td><td>'+w.status+'</td><td>'+(w.tags.join(', ')||'—')+'</td><td>'+w.maxConcurrency+'</td><td>'+w.heartbeatAgeSeconds.toFixed(1)+'s</td></tr>').join('') || '<tr><td colspan=5 class=muted>no workers registered</td></tr>';

    document.querySelector('#dlq tbody').innerHTML = dlq.items.map(e=>
      '<tr><td>'+e.jobName+'</td><td>'+e.reason+'</td><td>'+e.attemptCount+'</td><td>'+fmt(e.deadLetteredAt)+'</td><td>'+(e.replayed?'yes':'no')+'</td>'+
      '<td>'+(e.replayed?'':'<button onclick="replay(\''+e.id+'\')">Replay</button>')+'</td></tr>').join('') || '<tr><td colspan=6 class=muted>empty</td></tr>';

    document.getElementById('leader').textContent = leader.isHeld ? ('leader: '+leader.owner+' (fence '+leader.fencingToken+')') : 'leader: none';
  } catch(e){ document.getElementById('err').textContent = e.message; }
}
async function triggerSelected(){ const id=document.getElementById('triggerSelect').value; if(id){ await api('/api/v1/jobs/'+id+'/trigger',{method:'POST',body:'{}'}); setTimeout(refresh, 300); } }
async function cancelRun(id){ await api('/api/v1/runs/'+id+'/cancel',{method:'POST'}); setTimeout(refresh, 300); }
async function replay(id){ await api('/api/v1/dlq/'+id+'/replay',{method:'POST'}); setTimeout(refresh, 300); }
document.getElementById('autorefresh').textContent = 'auto-refresh 3s';
refresh();
setInterval(refresh, 3000);
</script>
</body>
</html>
""";
}
