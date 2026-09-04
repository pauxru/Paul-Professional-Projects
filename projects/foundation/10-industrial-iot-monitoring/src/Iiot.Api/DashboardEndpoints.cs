namespace Iiot.Api;

public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => Results.Content(Html, "text/html; charset=utf-8")).AllowAnonymous();
        app.MapPost("/api/v1/demo/network", (NetworkToggleRequest request, GatewayNetworkControl control) =>
        {
            control.SetOnline(request.Online);
            return Results.Ok(new { online = control.IsOnline, bufferDepth = GatewayNetworkControl.CurrentBufferDepth });
        }).RequireAuthorization(Policies.Operator);
        app.MapGet("/api/v1/demo/network", (GatewayNetworkControl control) =>
            Results.Ok(new { online = control.IsOnline, bufferDepth = GatewayNetworkControl.CurrentBufferDepth }))
            .RequireAuthorization(Policies.Operator);
    }

    private const string Html = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><title>Jua Kali Manufacturing Ltd — IoT monitoring</title>
        <style>
          body{font-family:system-ui,sans-serif;margin:0;background:#f4f5f6;color:#17212b}header{background:#17324d;color:#fff;padding:16px 24px}
          main{padding:20px;display:grid;gap:18px;grid-template-columns:2fr 1fr}.card{background:#fff;border:1px solid #d7dce0;padding:16px}
          table{width:100%;border-collapse:collapse}td,th{padding:8px;border-bottom:1px solid #e6e9eb;text-align:left}button{padding:8px 12px;cursor:pointer}
          .online{color:#087f23}.offline{color:#b42318}.alert{color:#b42318;font-weight:600}svg{width:100%;height:80px;background:#fafafa}
        </style></head><body><header><strong>Jua Kali Manufacturing Ltd (fictional)</strong> · Industrial IoT Monitoring</header>
        <main><section class="card"><h2>Devices</h2><p id="network"></p><button id="cut">Kill network</button> <button id="restore">Restore network</button>
        <table><thead><tr><th>Device</th><th>Asset</th><th>Status</th><th>Latest °C</th><th>Firmware</th><th>Temperature sparkline</th></tr></thead><tbody id="devices"></tbody></table></section>
        <aside><section class="card"><h2>Active alerts</h2><div id="alerts">Loading…</div></section>
        <section class="card"><h2>Command console</h2><select id="commandDevice"></select><button id="restart">Queue restart</button><pre id="commandResult"></pre></section></aside></main>
        <script>
        let token='';
        async function api(path, options={}) {
          options.headers={...(options.headers||{}),Authorization:'Bearer '+token};
          const response=await fetch(path,options); if(!response.ok) throw new Error(await response.text()); return response.json();
        }
        function spark(points){if(!points.length)return '<svg></svg>';const values=points.map(p=>p.average);const lo=Math.min(...values),hi=Math.max(...values),range=hi-lo||1;
          const poly=values.map((v,i)=>`${i*100/(values.length-1||1)},${76-(v-lo)*70/range}`).join(' ');return `<svg viewBox="0 0 100 80" preserveAspectRatio="none"><polyline fill="none" stroke="#1677b8" stroke-width="2" points="${poly}"/></svg>`}
        function esc(value){return String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]))}
        async function refresh(){
          const devices=await api('/api/v1/devices');const body=document.getElementById('devices'),pick=document.getElementById('commandDevice');body.innerHTML='';pick.innerHTML='';
          for(const d of devices){const rolls=await api(`/api/v1/telemetry/${encodeURIComponent(d.deviceId)}/rollups?metric=TemperatureC&resolution=minute`);const raw=await api(`/api/v1/telemetry/${encodeURIComponent(d.deviceId)}`);const latest=raw.at(-1);
            body.insertAdjacentHTML('beforeend',`<tr><td>${esc(d.deviceId)}</td><td>${esc(d.asset)}</td><td>${esc(d.status)}</td><td>${latest?latest.values.temperatureC.toFixed(1):'—'}</td><td>${esc(d.firmwareVersion)}</td><td>${spark(rolls)}</td></tr>`);
            pick.insertAdjacentHTML('beforeend',`<option value="${esc(d.deviceId)}">${esc(d.deviceId)}</option>`);}
          const alerts=await api('/api/v1/alerts?activeOnly=true');document.getElementById('alerts').innerHTML=alerts.length?alerts.map(a=>`<p class="alert">${esc(a.deviceId)}: ${esc(a.reason)}</p>`).join(''):'No active alerts';const networkState=await api('/api/v1/demo/network');showNetwork(networkState);
        }
        function showNetwork(status){document.getElementById('network').innerHTML=`Gateway cloud link: <strong class="${status.online?'online':'offline'}">${status.online?'ONLINE':'OFFLINE'}</strong>; durable buffer: ${status.bufferDepth}`;}
        async function network(online){const status=await api('/api/v1/demo/network',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({online})});
          showNetwork(status);}
        async function start(){const auth=await fetch('/api/v1/auth/token',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({subject:'dashboard',scope:'admin operator'})});token=(await auth.json()).accessToken;
          document.getElementById('cut').onclick=()=>network(false);document.getElementById('restore').onclick=()=>network(true);
          document.getElementById('restart').onclick=async()=>{try{const r=await api('/api/v1/commands',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({deviceId:document.getElementById('commandDevice').value,commandType:'restart',parameters:{}})});document.getElementById('commandResult').textContent='Queued '+r.commandId}catch(e){document.getElementById('commandResult').textContent=e}};await network(true);await refresh();setInterval(refresh,5000)}
        start().catch(e=>document.body.insertAdjacentText('beforeend',e));
        </script></body></html>
        """;
}
