namespace SavannaLogistics.Api;

public static class Dashboard
{
    private const string Html = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
        <title>Savanna Logistics Ltd (fictional)</title>
        <style>
        :root{color-scheme:dark}body{font:14px system-ui;margin:0;background:#111827;color:#e5e7eb}
        header{padding:18px 24px;background:#064e3b;display:flex;justify-content:space-between;align-items:center}
        main{padding:20px;display:grid;grid-template-columns:repeat(auto-fit,minmax(380px,1fr));gap:16px}
        section{background:#1f2937;border:1px solid #374151;border-radius:8px;padding:14px;overflow:auto}
        table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:7px;border-bottom:1px solid #374151}
        button,input,select{padding:8px;background:#111827;color:#e5e7eb;border:1px solid #4b5563;border-radius:4px}
        button{cursor:pointer;background:#047857}.badge{padding:2px 6px;border-radius:9px;background:#374151}
        .wide{grid-column:1/-1}.muted{color:#9ca3af}.error{color:#fca5a5}
        </style></head>
        <body><header><div><strong>Savanna Logistics Ltd (fictional)</strong><div class="muted">Event-driven fleet operations</div></div>
        <div><span id="status" class="badge">connecting</span> <button onclick="refresh()">Refresh</button></div></header>
        <main>
        <section><h2>Live vehicles</h2><table><thead><tr><th>Vehicle</th><th>Status</th><th>Position</th><th>Speed</th><th>Last seen</th></tr></thead><tbody id="vehicles"></tbody></table></section>
        <section><h2>Active trips</h2><table><thead><tr><th>Vehicle</th><th>Status</th><th>Stop</th><th>SLA due</th></tr></thead><tbody id="trips"></tbody></table></section>
        <section><h2>Alerts feed</h2><div id="alerts"></div></section>
        <section><h2>Geofences</h2><div id="geofences"></div></section>
        <section class="wide"><h2>Incident controls</h2>
        <button onclick="simulate()">Run 3-vehicle simulator</button>
        <select id="speed"><option value="0">max</option><option value="10">10x</option><option value="1">1x</option></select>
        <button onclick="replay()">Replay last hour</button> <button onclick="rebuild()">Rebuild projection</button>
        <pre id="operation" class="muted"></pre></section></main>
        <script>
        let token='';
        const api=async(path,options={})=>{options.headers={...(options.headers||{}),Authorization:`Bearer ${token}`,'Content-Type':'application/json'};const r=await fetch(path,options);if(!r.ok)throw new Error(`${r.status} ${await r.text()}`);return r.json()};
        const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
        async function login(){const r=await fetch('/api/v1/auth/token',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({clientId:'operator'})});token=(await r.json()).accessToken}
        async function refresh(){try{if(!token)await login();const [fleet,states,trips,alerts,gfs]=await Promise.all([api('/api/v1/vehicles?pageSize=200'),api('/api/v1/vehicles/states'),api('/api/v1/trips?pageSize=100'),api('/api/v1/alerts?pageSize=30'),api('/api/v1/geofences?pageSize=100')]);const byId=Object.fromEntries(fleet.items.map(v=>[v.id,v]));
        document.querySelector('#vehicles').innerHTML=states.map(s=>`<tr><td>${esc(byId[s.vehicleId]?.registration||s.vehicleId.slice(0,8))}</td><td>${esc(s.status)}</td><td>${s.latitude.toFixed(5)}, ${s.longitude.toFixed(5)}</td><td>${s.speedKph.toFixed(1)} km/h</td><td>${new Date(s.lastSeenAt).toLocaleTimeString()}</td></tr>`).join('')||'<tr><td colspan=5>No telemetry yet — run the simulator.</td></tr>';
        document.querySelector('#trips').innerHTML=trips.items.filter(t=>!['Completed','Aborted'].includes(t.status)).map(t=>`<tr><td>${esc(byId[t.vehicleId]?.registration||t.vehicleId.slice(0,8))}</td><td>${esc(t.status)}</td><td>${t.currentStopSequence}</td><td>${new Date(t.slaDueAt).toLocaleTimeString()}</td></tr>`).join('');
        document.querySelector('#alerts').innerHTML=alerts.items.map(a=>`<p><span class=badge>${esc(a.type)}</span> ${esc(a.message)}<br><span class=muted>${new Date(a.occurredAt).toLocaleString()}</span></p>`).join('')||'No alerts.';
        document.querySelector('#geofences').innerHTML=gfs.items.map(g=>`<p><b>${esc(g.name)}</b> <span class=badge>${esc(g.shape)}</span></p>`).join('');
        document.querySelector('#status').textContent='live'}catch(e){document.querySelector('#status').textContent='error';document.querySelector('#operation').textContent=e}}
        async function simulate(){document.querySelector('#operation').textContent='running simulator...';try{const r=await api('/api/v1/telemetry/simulate',{method:'POST',body:JSON.stringify({vehicleCount:3,pingsPerVehicle:80,pingsPerSecond:20,seed:808,duplicateRate:.04,outOfOrderRate:.08,dropoutRate:.05,gpsJitterMetres:12,clockSkewSeconds:4,realTime:false})});document.querySelector('#operation').textContent=JSON.stringify(r,null,2);setTimeout(refresh,600)}catch(e){document.querySelector('#operation').textContent=e}}
        async function replay(){const to=new Date(),from=new Date(to-3600000);try{const r=await api('/api/v1/replay',{method:'POST',body:JSON.stringify({from:from.toISOString(),to:to.toISOString(),speed:Number(document.querySelector('#speed').value),vehicleId:null})});document.querySelector('#operation').textContent=JSON.stringify(r,null,2);refresh()}catch(e){document.querySelector('#operation').textContent=e}}
        async function rebuild(){try{document.querySelector('#operation').textContent='rebuilding...';const r=await api('/api/v1/replay/rebuild-projection',{method:'POST',body:'{}'});document.querySelector('#operation').textContent=JSON.stringify(r,null,2);refresh()}catch(e){document.querySelector('#operation').textContent=e}}
        login().then(refresh);setInterval(refresh,5000);
        </script></body></html>
        """;

    public static IEndpointRouteBuilder MapDashboard(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", () => Results.Content(Html, "text/html")).AllowAnonymous();
        return endpoints;
    }
}
