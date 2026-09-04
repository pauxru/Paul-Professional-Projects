namespace FeatureFlags.Api.Ui;

public static class AdminUi
{
    public const string Html = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Feature Flags Admin</title><style>
body{font:14px system-ui;margin:2rem;max-width:1200px;color:#18212f}h1,h2{margin-top:1.6rem}button,input,select,textarea{padding:.45rem;margin:.2rem}textarea{width:100%;min-height:190px;font:12px ui-monospace,Consolas}table{border-collapse:collapse;width:100%}th,td{border:1px solid #ccd3dd;padding:.45rem;text-align:left}.row{display:flex;gap:1rem;align-items:center;flex-wrap:wrap}.status{background:#edf4ff;padding:.7rem;white-space:pre-wrap}small{color:#52606d}</style></head><body>
<h1>Feature Flags</h1><p>Utilitarian administration surface for the <b>Acme Manufacturing (fictional)</b> demo project.</p>
<div class="row"><label>Environment <select id="env"><option>dev</option><option>staging</option><option>production</option></select></label><button onclick="loadFlags()">Load flags</button><button onclick="token()">Get dev admin token</button></div><pre id="status" class="status">Ready. Get a development token, then load an environment.</pre>
<h2>Flag list and environment toggles</h2><table><thead><tr><th>Key</th><th>Type</th><th>Lifecycle</th><th>On</th><th>Client side</th><th>Actions</th></tr></thead><tbody id="flags"></tbody></table>
<h2>Rule editor (JSON)</h2><small>Paste a complete flag definition from the API model, then save it. Production saves create an approval request instead.</small><textarea id="editor">{
  "key": "new-checkout", "name": "New checkout", "valueType": "Boolean", "isOn": true, "clientSide": true,
  "offVariation": 0, "fallthroughVariation": 0, "salt": "checkout-v1", "variations": [{"index":0,"name":"off","value":false},{"index":1,"name":"on","value":true}],
  "rollout": {"variations":[{"variationIndex":1,"weightBps":2500}]}
}</textarea><div><button onclick="saveFlag(false)">Save flag</button><button onclick="saveFlag(true)">Request production approval</button></div>
<h2>Targeting preview</h2><textarea id="context">{"flagKey":"new-checkout","contextKey":"demo-user","kind":"user","attributes":{"country":"KE"}}</textarea><button onclick="preview()">Evaluate and explain</button><pre id="preview" class="status"></pre>
<h2>Change history and one-click revert</h2><button onclick="history()">Load history</button><div id="history"></div>
<h2>Stale-flag technical-debt report</h2><button onclick="stale()">Load report</button><pre id="stale" class="status"></pre>
<h2>Production approvals queue</h2><button onclick="approvals()">Load approvals</button><div id="approvals"></div>
<script>
let auth='';const project='acme';const $=id=>document.getElementById(id);const set=x=>$('status').textContent=x;
async function token(){let r=await fetch('/api/v1/auth/token',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({actor:'ui-admin',scopes:['flags:read','flags:write','flags:approve']})});let d=await r.json();auth=d.accessToken;set('Development token acquired.');}
function headers(){return {'Authorization':'Bearer '+auth,'Content-Type':'application/json'};}function path(){return '/api/v1/projects/'+project+'/environments/'+$('env').value;}
async function api(url,opt={}){let r=await fetch(url,{...opt,headers:{...headers(),...(opt.headers||{})}});if(!r.ok){throw new Error(await r.text())}return r.status===204?null:r.json();}
async function loadFlags(){try{let d=await api(path()+'/flags');$('flags').innerHTML=d.map(f=>`<tr><td>${f.key}</td><td>${f.valueType}</td><td>${f.lifecycleStatus}</td><td>${f.isOn}</td><td>${f.clientSide}</td><td><button onclick='kill("${f.key}",false)'>Kill</button><button onclick='edit(${JSON.stringify(JSON.stringify(f))})'>Edit</button></td></tr>`).join('');set('Loaded '+d.length+' flags from '+$('env').value+'.');}catch(e){set(e.message)}}
function edit(json){$('editor').value=JSON.parse(json)}
async function kill(key,enabled){try{await api(path()+'/flags/'+key+'/kill-switch',{method:'POST',body:JSON.stringify({enabled,comment:'UI emergency action'})});await loadFlags();}catch(e){set(e.message)}}
async function saveFlag(approval){try{let flag=JSON.parse($('editor').value);let endpoint=approval?path()+'/approvals':path()+'/flags/'+flag.key;let method=approval?'POST':'PUT';let d=await api(endpoint,{method,body:JSON.stringify({flag,comment:'UI change'})});set(approval?'Approval requested: '+d.id:'Flag saved.');await loadFlags();}catch(e){set(e.message)}}
async function preview(){try{$('preview').textContent=JSON.stringify(await api(path()+'/evaluate',{method:'POST',body:$('context').value}),null,2)}catch(e){set(e.message)}}
async function history(){try{let d=await api(path()+'/audit');$('history').innerHTML=d.map(x=>`<p><b>${x.action}</b> ${x.resource} by ${x.actor} <button onclick="revert('${x.id}')">Revert to before this</button><br><small>${x.occurredAt}</small></p>`).join('')||'No changes yet.'}catch(e){set(e.message)}}
async function revert(id){try{await api(path()+'/audit/'+id+'/revert',{method:'POST'});await history();await loadFlags()}catch(e){set(e.message)}}
async function stale(){try{$('stale').textContent=JSON.stringify(await api(path()+'/stale-flags?days=30'),null,2)}catch(e){set(e.message)}}
async function approvals(){try{let d=await api(path()+'/approvals');$('approvals').innerHTML=d.map(x=>`<p>${x.status}: ${x.resource} requested by ${x.requestedBy} <button onclick="review('${x.id}',true)">Approve</button> <button onclick="review('${x.id}',false)">Reject</button> <button onclick="apply('${x.id}')">Apply</button></p>`).join('')||'No approvals.'}catch(e){set(e.message)}}
async function review(id,approve){try{await api(path()+'/approvals/'+id+'/review',{method:'POST',body:JSON.stringify({approve,comment:'UI review'})});approvals()}catch(e){set(e.message)}}async function apply(id){try{await api(path()+'/approvals/'+id+'/apply',{method:'POST'});approvals();loadFlags()}catch(e){set(e.message)}}
</script></body></html>
""";
}
