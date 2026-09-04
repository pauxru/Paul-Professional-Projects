namespace CloudCostObservability.Api.Endpoints;

public static class DashboardEndpoints
{
    public static WebApplication MapDashboard(this WebApplication app)
    {
        app.MapGet("/", () => Results.Content(Html, "text/html; charset=utf-8")).AllowAnonymous();
        app.MapGet("/favicon.ico", () => Results.NoContent()).AllowAnonymous();
        app.MapGet("/docs", () => Results.Content("""
            <!doctype html><title>Northstar FinOps API</title>
            <h1>Northstar FinOps API</h1>
            <p>OpenAPI document: <a href="/openapi/v1.json">/openapi/v1.json</a></p>
            <p>The local dashboard is available at <a href="/">/</a>.</p>
            """, "text/html; charset=utf-8")).AllowAnonymous();
        return app;
    }

    private const string Html = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Northstar Group (fictional) — FinOps</title>
        <style>
        :root{color-scheme:dark;font-family:Inter,Segoe UI,sans-serif;background:#0b1220;color:#e6edf6}
        body{margin:0;padding:24px;max-width:1500px;margin:auto}.top{display:flex;justify-content:space-between;align-items:end;border-bottom:1px solid #28354a;padding-bottom:16px}
        h1{margin:0;font-size:24px}h2{font-size:15px;margin:0 0 12px;color:#b9c8dc}.muted{color:#8ba0b9;font-size:12px}.grid{display:grid;grid-template-columns:repeat(12,1fr);gap:14px;margin-top:16px}
        .card{background:#111c2e;border:1px solid #263752;border-radius:7px;padding:15px;min-height:120px}.wide{grid-column:span 8}.four{grid-column:span 4}.six{grid-column:span 6}
        svg{display:block;width:100%;height:205px}.value{font-size:25px;font-weight:600}.list{font-size:12px;line-height:1.55;max-height:210px;overflow:auto}.badge{padding:3px 7px;border-radius:20px;background:#21395f;color:#9ac4ff;font-size:11px}
        .row{display:flex;justify-content:space-between;border-bottom:1px solid #1e2c42;padding:5px 0}.warn{color:#ffbd59}.critical{color:#ff6d6d}.good{color:#72d39b}
        </style></head><body>
        <div class="top"><div><h1>Northstar Group <span class="muted">(fictional)</span> · Cloud Cost Observatory</h1><div class="muted">Synthetic local FinOps reference implementation · USD reporting basis</div></div><span class="badge" id="status">loading local data</span></div>
        <main class="grid">
          <section class="card wide"><h2>Spend trend · forecast band</h2><svg id="spend"></svg></section>
          <section class="card four"><h2>Budget burn-down</h2><div id="budgets" class="list"></div></section>
          <section class="card four"><h2>Spend by service</h2><svg id="service"></svg></section>
          <section class="card four"><h2>Spend by team</h2><svg id="team"></svg></section>
          <section class="card four"><h2>Spend by environment</h2><svg id="environment"></svg></section>
          <section class="card six"><h2>Anomaly feed</h2><div id="anomalies" class="list"></div></section>
          <section class="card six"><h2>Recommendation backlog · projected monthly savings</h2><div id="recommendations" class="list"></div></section>
          <section class="card six"><h2>Tag coverage</h2><div id="tags" class="list"></div></section>
          <section class="card six"><h2>Unit-economics trend</h2><svg id="economics"></svg></section>
        </main>
        <script>
        const $=id=>document.getElementById(id), money=n=>new Intl.NumberFormat('en-US',{style:'currency',currency:'USD',maximumFractionDigits:0}).format(n||0);
        function svg(el,content){el.setAttribute('viewBox','0 0 600 205');el.innerHTML=content}
        function line(id, values, color='#62b5ff', band){
          const el=$(id), w=580,h=170,p=10, max=Math.max(...values,1), min=Math.min(...values,0), range=Math.max(max-min,1);
          const xy=(v,i)=>`${p+i*(w/(Math.max(values.length-1,1)))},${p+h-(v-min)/range*h}`;
          const path=values.map((v,i)=>(i?'L':'M')+xy(v,i)).join(' ');
          let extra='<line x1="10" y1="180" x2="590" y2="180" stroke="#334866"/><text x="10" y="198" fill="#8ba0b9" font-size="11">recent synthetic period</text>';
          if(band){const [lo,hi]=band;extra+=`<rect x="492" y="22" width="80" height="130" fill="#62b5ff" opacity=".12"/><text x="496" y="38" fill="#8ba0b9" font-size="10">forecast ${money(lo)}–${money(hi)}</text>`}
          svg(el,extra+`<path d="${path}" fill="none" stroke="${color}" stroke-width="3"/><text x="12" y="18" fill="#8ba0b9" font-size="11">${money(max)} high</text>`)
        }
        function bars(id, items){
          const max=Math.max(...items.map(x=>x.value),1), colors=['#62b5ff','#72d39b','#ffbd59','#c49cff','#ff7f9f','#5ed3c6'];
          const body=items.slice(0,6).map((x,i)=>{const y=12+i*30,w=520*x.value/max;return `<text x="5" y="${y+13}" fill="#b9c8dc" font-size="11">${x.key.slice(0,18)}</text><rect x="150" y="${y}" width="${w}" height="17" rx="3" fill="${colors[i]}"/><text x="${155+w}" y="${y+13}" fill="#8ba0b9" font-size="10">${money(x.value)}</text>`}).join('');
          svg($(id),body)
        }
        function rows(id, items, render){$(id).innerHTML=items.length?items.map(render).join(''):'<span class="muted">No data loaded.</span>'}
        async function api(path,token){const r=await fetch(path,{headers:{Authorization:'Bearer '+token}});if(!r.ok)throw new Error(path+' '+r.status);return r.json()}
        (async()=>{
          try{
            const t=await fetch('/api/v1/auth/token',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({subject:'local-dashboard',scope:'finops:read finops:manage finops:admin'})}).then(r=>r.json());
            const q='?pageSize=1000';
            const [daily,service,team,environment,budget,forecast,anomaly,recommendation,tags,economics]=await Promise.all([
              api('/api/v1/costs?groupBy=day'+q,t.accessToken),api('/api/v1/costs?groupBy=service'+q,t.accessToken),api('/api/v1/costs?groupBy=team'+q,t.accessToken),
              api('/api/v1/costs?groupBy=environment'+q,t.accessToken),api('/api/v1/budgets/status',t.accessToken),api('/api/v1/forecasts',t.accessToken),
              api('/api/v1/anomalies?includeSuppressed=false',t.accessToken),api('/api/v1/recommendations',t.accessToken),api('/api/v1/tags/coverage',t.accessToken),api('/api/v1/unit-economics',t.accessToken)
            ]);
            const chosen=forecast.find(x=>x.isDefault)||forecast[0], band=chosen?[chosen.lowerBound,chosen.upperBound]:null;
            line('spend',daily.results.items.sort((a,b)=>a.key.localeCompare(b.key)).map(x=>x.amortizedCost),'#62b5ff',band);
            bars('service',service.results.items.map(x=>({key:x.key,value:x.amortizedCost})));bars('team',team.results.items.map(x=>({key:x.key,value:x.amortizedCost})));bars('environment',environment.results.items.map(x=>({key:x.key,value:x.amortizedCost})));
            rows('budgets',budget,x=>`<div class="row"><span>${x.budgetId}<br><span class="muted">${money(x.spend)} / ${money(x.budgetAmount)}</span></span><span class="${x.percentUsed>=100?'critical':x.percentUsed>=80?'warn':'good'}">${x.percentUsed}%</span></div>`);
            rows('anomalies',anomaly.slice(0,12),x=>`<div class="row"><span><b>${x.severity}</b> · ${x.dimension}<br><span class="muted">${x.explanation}</span></span><span class="critical">${x.severityScore}</span></div>`);
            rows('recommendations',recommendation.filter(x=>x.lifecycle==='Open').slice(0,12),x=>`<div class="row"><span>${x.type}<br><span class="muted">${x.resourceId} · ${x.confidence} confidence</span></span><span class="good">${money(x.projectedMonthlySavings)}</span></div>`);
            $('tags').innerHTML=`<div class="value ${tags.coveragePercent>=90?'good':'warn'}">${tags.coveragePercent}%</div><div class="muted">${tags.fullyCompliantResources}/${tags.totalResources} resources fully compliant</div>`+Object.entries(tags.missingByTag).map(([k,v])=>`<div class="row"><span>${k}</span><span>${v} missing</span></div>`).join('');
            line('economics',economics.sort((a,b)=>a.date.localeCompare(b.date)).map(x=>x.costPerOrder),'#72d39b');
            $('status').textContent='live synthetic data · '+(chosen?`${chosen.method} forecast`: 'forecast unavailable');
          }catch(error){$('status').textContent='dashboard error';console.error(error)}
        })();
        </script></body></html>
        """;
}
