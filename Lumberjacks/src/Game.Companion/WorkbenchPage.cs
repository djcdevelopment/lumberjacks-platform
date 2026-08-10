namespace Lumberjacks.Companion;

static class WorkbenchPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
  <title>Lumberjacks Workbench</title>
  <style>
    :root{color-scheme:dark}body{max-width:1180px;margin:28px auto;padding:0 18px;background:#0f1319;color:#e8edf4;font:15px system-ui,sans-serif}h1{color:#43a6ff;margin-bottom:5px}h2{margin:0 0 10px}h3{margin:0 0 6px;color:#b8d8ff}.muted{color:#9aa8ba}.links{display:flex;gap:8px;flex-wrap:wrap;margin:14px 0}.btn{display:inline-block;background:#3479c7;color:#fff;border:0;border-radius:7px;padding:9px 13px;text-decoration:none;font-weight:650;cursor:pointer}.btn.secondary{background:transparent;border:1px solid #586577}.card{background:#191e27;border:1px solid #303846;border-radius:12px;padding:18px;margin:14px 0}.hero{border-color:#3479c7}.source{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;color:#b8d8ff}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(250px,1fr));gap:10px}.tile{background:#10151d;border:1px solid #303846;border-radius:9px;padding:12px}.tile.ok{border-color:#297a4f}.tile.wait{border-color:#8b6c2a}.tile.bad{border-color:#8b3a2f}.tag{display:inline-block;padding:2px 7px;border-radius:999px;background:#293546;color:#dce6f5;font-size:.82rem}.tag.ok{background:#123a26;color:#b8f2ce}.tag.wait{background:#453719;color:#ffdb91}.tag.bad{background:#481f1c;color:#ffc1b5}code,pre{font-family:ui-monospace,SFMono-Regular,Consolas,monospace}pre{white-space:pre-wrap;overflow:auto;background:#0b0e13;border-radius:8px;padding:12px}.check{display:flex;gap:8px;align-items:center;margin:7px 0}.check input{accent-color:#45ce83;width:17px;height:17px}.step{display:grid;grid-template-columns:34px 1fr;gap:10px;padding:10px 0;border-top:1px solid #303846}.step:first-child{border-top:0}.num{background:#3479c7;border-radius:50%;width:26px;height:26px;display:grid;place-items:center;font-weight:700}.small{font-size:.88rem}@media(max-width:650px){body{margin:16px auto}.step{grid-template-columns:28px 1fr}}
  </style>
</head>
<body>
  <h1>Lumberjacks Workbench</h1>
  <p class="muted">The local Docker workbench: source position, milestone hierarchy, live lanes, and recoverable evidence.</p>
  <div class="links"><a class="btn" href="/">Local status</a><a class="btn" href="/quest-studio">Quest Studio</a><a class="btn" href="/trace">Live trace</a><a class="btn" href="/community">Community</a><a class="btn" href="/roadmap">Roadmap</a></div>

  <section class="card hero"><h2 id="goal">Loading active goal...</h2><p id="objective" class="muted">Loading source and objective.</p><p class="source" id="source">Source unknown</p></section>

  <section class="card"><h2>Workbench checklist</h2><p class="muted">This is a compact map, not a second roadmap. Each item points back to its source document or receipt family.</p><div id="milestones" class="grid"></div></section>

  <section class="card"><h2>Execution lanes</h2><div id="lanes" class="grid"></div></section>

  <section class="card"><h2>Reconstruct after reset</h2><p id="principle" class="muted"></p><div id="reconstruct"></div><p><button class="btn" onclick="snapshot()">Capture redacted workbench snapshot</button> <a class="btn secondary" href="/api/v0/companion/workbench/snapshot/latest">Download latest snapshot</a></p><div id="snapshot" class="small muted">No snapshot captured in this session.</div></section>

  <section class="card"><h2>Local boundary checks</h2><label class="check"><input id="local" type="checkbox" disabled><span>Local workbench is loopback-only</span></label><label class="check"><input id="source-ready" type="checkbox" disabled><span>Image/source identity is known</span></label><label class="check"><input id="gateway" type="checkbox" disabled><span>Configured Gateway responds</span></label><label class="check"><input id="valheim" type="checkbox" disabled><span>Valheim mount and config are visible</span></label><label class="check"><input id="profile" type="checkbox" disabled><span>Local profile is linked (redacted)</span></label><label class="check"><input id="capture" type="checkbox" disabled><span>Evidence capture history exists</span></label></section>

  <details class="card"><summary>Raw workbench packet</summary><pre id="raw">Loading...</pre></details>
<script>
let packet=null;
const q=s=>document.querySelector(s);
const esc=v=>String(v??'').replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));
async function get(u,options){let r=await fetch(u,{cache:'no-store',...(options||{})});let d=await r.json();if(!r.ok)throw d;return d}
function tag(status){let c=status==='complete'||status==='active'||status==='primary'||status==='live_edge'||status==='ready_for_bounded_window'?'ok':(status==='gated'||status==='queued'||status==='seed_gated'?'wait':'');return '<span class="tag '+c+'">'+esc(status)+'</span>'}
function list(items){return (items||[]).map(x=>'<div class="small">'+esc(x)+'</div>').join('')}
function paint(p){
  packet=p;const g=p.catalog.active_goal;q('#goal').textContent=g.title+' · '+g.status;q('#objective').textContent=g.objective;
  q('#source').textContent='branch '+(p.source.source_branch||'unknown')+' · revision '+(p.source.source_revision||'unknown')+' · image '+(p.source.image||'unknown')+' · dirty '+p.source.source_dirty;
  q('#milestones').innerHTML=p.catalog.milestones.map(m=>'<article class="tile '+(m.status==='active'||m.status==='complete'?'ok':(m.status==='gated'||m.status==='queued'?'wait':'bad'))+'"><h3>'+esc(m.id)+' · '+esc(m.title)+'</h3><p>'+tag(m.status)+' <span class="small">last '+esc(m.last_updated)+'</span></p><p>'+esc(m.focus)+'</p><div class="muted small">'+list(m.source_points)+'</div></article>').join('');
  q('#lanes').innerHTML=p.catalog.execution_lanes.map(l=>'<article class="tile"><h3>'+esc(l.title)+'</h3><p>'+tag(l.status)+'</p><p>'+esc(l.surface)+'</p><div>'+list(l.owns)+'</div>'+(l.human_touch?'<p class="muted small">Human touch: '+esc(l.human_touch)+'</p>':'')+(l.guardrail?'<p class="muted small">Guardrail: '+esc(l.guardrail)+'</p>':'')+'</article>').join('');
  q('#principle').textContent=p.catalog.reconstruct.principle;
  q('#reconstruct').innerHTML=p.catalog.reconstruct.steps.map(s=>'<div class="step"><div class="num">'+esc(s.order)+'</div><div><h3>'+esc(s.title)+'</h3><code>'+esc(s.command)+'</code><p class="muted small">'+esc(s.result)+'</p></div></div>').join('');
  q('#raw').textContent=JSON.stringify(p,null,2);
  q('#local').checked=true;q('#source-ready').checked=p.source.source_revision&&p.source.source_revision!=='unknown';
}
async function refresh(){try{const p=await get('/api/v0/companion/workbench');paint(p);const s=p.local_status||{};q('#gateway').checked=!!p.gateway_reachable;q('#valheim').checked=!!(s.valheim?.found&&s.valheim?.config_found);q('#profile').checked=!!s.profile?.linked;q('#capture').checked=!!p.recent_capture_exists}catch(e){q('#raw').textContent='Workbench packet unavailable: '+JSON.stringify(e)}}
async function snapshot(){q('#snapshot').textContent='Writing redacted snapshot...';try{const d=await get('/api/v0/companion/workbench/snapshot',{method:'POST'});q('#snapshot').textContent='Saved '+d.snapshot_name+' · '+d.generated_utc}catch(e){q('#snapshot').textContent='Snapshot failed: '+JSON.stringify(e)}}
refresh();setInterval(refresh,10000);
</script>
</body></html>
""";
}
