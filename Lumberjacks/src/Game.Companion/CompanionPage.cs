static class CompanionPage
{
    public const string Html = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Lumberjacks Companion</title>
<style>
:root{color-scheme:dark}*{box-sizing:border-box}body{max-width:1040px;margin:36px auto;padding:0 18px;background:#101319;color:#e8edf4;font:16px/1.45 system-ui,-apple-system,Segoe UI,sans-serif}h1{font-size:2.2rem;color:#43a6ff;margin:0}h2{margin:0 0 10px}p{margin:8px 0}.muted{color:#a7b1c2}.shell{display:flex;justify-content:space-between;gap:18px;align-items:start;margin-bottom:20px}.links a{display:inline-block;margin:0 0 6px 6px}.card{background:#191e27;border:1px solid #303846;border-radius:12px;padding:22px;margin:16px 0}.hero{border-color:#2d69a5;background:linear-gradient(135deg,#172635,#191e27)}.next{font-size:1.1rem;font-weight:700}.parts{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:10px;margin-top:12px}.part{background:#0c0f14;border:1px solid #303846;border-radius:8px;padding:12px}.part strong{display:block;color:#a7b1c2;font-size:.78rem;text-transform:uppercase;letter-spacing:.04em}.part span{display:block;margin-top:5px;font-family:ui-monospace,SFMono-Regular,Consolas,monospace;overflow-wrap:anywhere}.part.ok{border-color:#297a4f}.part.wait{border-color:#8b6c2a}.part.bad{border-color:#8b3a2f}.checks{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:9px;margin:14px 0 2px}.check{display:flex;align-items:center;gap:9px;background:#11161e;border:1px solid #8b6c2a;border-radius:8px;padding:11px 12px;color:#ffcf6b;font-weight:650}.check.ok{border-color:#297a4f;color:#77dc9b}.check input{appearance:none;width:19px;height:19px;margin:0;border:2px solid #d29336;border-radius:4px;background:#241c0d;flex:0 0 auto}.check input:checked{border-color:#55d780;background:#217747}.check input:checked::after{content:'\2713';display:block;color:#fff;font-size:14px;line-height:15px;text-align:center}.check input:disabled{opacity:1}.check.manual{cursor:pointer}.check.manual input{cursor:pointer}.ok{color:#77dc9b}.wait{color:#ffcf6b}.bad{color:#ff9877}button,a.btn{display:inline-block;background:#3479c7;color:#fff;border:0;border-radius:8px;padding:11px 16px;text-decoration:none;font:inherit;font-weight:650;cursor:pointer}button:hover,a.btn:hover{background:#4a8ddd}button:disabled{background:#384455;color:#9eaaba;cursor:not-allowed}.secondary{background:transparent;border:1px solid #586577;color:#dce6f5}.result{margin-top:13px;padding:13px;border-radius:8px;background:#0c0f14;border:1px solid #28303c}.release{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;color:#b8d8ff}.notice{padding:12px 14px;border-radius:8px;background:#123a26;border:1px solid #1f6b41;color:#b8f2ce}details{margin-top:16px}pre{white-space:pre-wrap;overflow:auto;background:#0c0f14;padding:12px;border-radius:8px;font-size:.84rem}@media(max-width:650px){.shell{display:block}.links a{margin:10px 8px 0 0}}
</style>
</head>
<body>
<div class="shell">
  <div>
    <h1>Lumberjacks Companion</h1>
    <p class="muted">Your local alpha updater and live network workbench.</p>
  </div>
  <div class="links">
    <a class="btn" href="/community">Community</a>
    <a class="btn" href="/trace">Live trace</a>
    <a class="btn" href="/roadmap">Roadmap</a>
  </div>
</div>

<section class="card hero">
  <h2 id="headline">Checking local setup...</h2>
  <p id="next" class="next muted">Loading readiness.</p>
</section>

<section class="card">
  <h2>Moving parts</h2>
  <p class="muted">Current local client, release pointer, server heartbeat, and network window.</p>
  <div class="parts">
    <div class="part wait" id="part-companion"><strong>Companion</strong><span>checking...</span></div>
    <div class="part wait" id="part-modpack"><strong>Modpack</strong><span>checking...</span></div>
    <div class="part wait" id="part-gateway"><strong>Gateway</strong><span>checking...</span></div>
    <div class="part wait" id="part-valheim"><strong>Valheim</strong><span>checking...</span></div>
    <div class="part wait" id="part-motion"><strong>Motion</strong><span>checking...</span></div>
    <div class="part wait" id="part-cutover"><strong>Cutover</strong><span>checking...</span></div>
    <div class="part wait" id="part-local"><strong>Local install</strong><span>checking...</span></div>
  </div>
</section>

<section class="card">
  <h2>Ready to update</h2>
  <p class="muted">Your access key stays in your local Valheim config and is never shown here.</p>
  <div class="checks">
    <label class="check" id="l-valheim"><input id="c-valheim" type="checkbox" disabled><span>Valheim folder found</span></label>
    <label class="check" id="l-config"><input id="c-config" type="checkbox" disabled><span>ComfyNetworkSense config found</span></label>
    <label class="check" id="l-profile"><input id="c-profile" type="checkbox" disabled><span>Installed profile linked</span></label>
    <label class="check manual" id="l-game"><input id="c-game" type="checkbox" onchange="paint()"><span>I have closed Valheim</span></label>
  </div>
</section>

<section class="card">
  <h2>Update Valheim mods</h2>
  <p class="muted">1) Check the release. 2) Review it below. 3) Install with Valheim closed. Companion verifies the package hash, preserves your config, and keeps a rollback backup.</p>
  <p>
    <button id="check" onclick="check()">1. Check for updates</button>
    <button id="install" onclick="install()" disabled>2. Install latest</button>
    <button id="rollback" class="secondary" onclick="rollback()" disabled>Rollback last install</button>
  </p>
  <div id="update" class="result">No release check yet.</div>
</section>

<section class="card">
  <h2>Companion application</h2>
  <p class="muted">Local updater version: <span id="companion-version" class="release">Checking...</span></p>
  <p class="notice" id="companion-note">Checking Companion release status...</p>
</section>

<details><summary>Technical details</summary><pre id="technical">Loading...</pre></details>

<script>
let state=null,manifest=null;
const q=s=>document.querySelector(s);

function esc(v){return String(v??'').replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]))}
async function get(u){let r=await fetch(u,{cache:'no-store'}),d=await r.json();if(!r.ok)throw d;return d}
function confirmation(){return {headers:{'Content-Type':'application/json'},body:JSON.stringify({game_closed_confirmed:true})}}

function setCheck(id,ok){
  q('#'+id).checked=ok;
  q('#l-'+id.slice(2)).className='check '+(ok?'ok':'wait');
}

function part(id,text,level='ok'){
  const el=q('#part-'+id);
  el.className='part '+level;
  el.querySelector('span').textContent=text;
}

function paint(){
  let v=state.valheim,p=state.profile;
  setCheck('c-valheim',v.found);
  setCheck('c-config',v.config_found);
  setCheck('c-profile',p.linked);
  let game=q('#c-game');
  game.disabled=v.running;
  q('#l-game').className='check manual '+(!v.running&&game.checked?'ok':'wait');
  let ready=v.found&&v.config_found&&p.linked&&!v.running&&game.checked;
  q('#install').disabled=!(ready&&manifest);
  q('#rollback').disabled=!state.installed||!ready;
  if(ready){
    q('#headline').textContent='Ready to install the checked release';
    q('#next').textContent=manifest?'Install is enabled.':'Next: check the current alpha release.';
  }else{
    q('#headline').textContent='Finish the amber checks before updating';
    q('#next').textContent=v.running?'Close Valheim, then confirm it here.':'Check "I have closed Valheim" after closing the game.';
  }
  part('companion','v'+state.companion_version+' via '+state.gateway_url,'ok');
  part('local',state.installed?('installed '+(state.installed.release||state.installed.mod_release||'unknown')):(v.found?'no install receipt yet':'Valheim path not mounted'),'wait');
  q('#technical').textContent=JSON.stringify(state,null,2);
}

async function status(){
  state=await get('/api/v0/companion/status');
  paint();
}

async function companionRelease(){
  try{
    let r=await get('/api/v0/companion/release/check');
    q('#companion-version').textContent=r.installed_version;
    q('#companion-note').textContent=r.note;
  }catch(e){
    q('#companion-version').textContent='Unknown';
    q('#companion-note').textContent='Could not check Companion release status.';
  }
}

async function movingParts(){
  try{
    const m=await get('/api/v0/companion/update/check');
    manifest=manifest||m;
    part('modpack',(m.release||'unknown')+' / '+(m.package?.size_bytes||0)+' bytes','ok');
  }catch(e){part('modpack','manifest unavailable','bad')}

  try{
    const d=await get('/api/v0/telemetry/deployment');
    part('gateway',(d.environment||'gateway')+' / '+(d.lumberjacks_version||'unknown'),'ok');
  }catch(e){part('gateway','deployment telemetry unavailable','bad')}

  try{
    const v=await get('/api/v0/telemetry/valheim');
    const text=(v.status||'unknown')+' / '+(v.peers??0)+' peers';
    part('valheim',text,v.stale?'wait':'ok');
  }catch(e){part('valheim','heartbeat unavailable','bad')}

  try{
    const c=await get('/api/v0/telemetry/cutover');
    const a=c.authoritative_window||{};
    const text=(c.mode||c.state||'unknown')+' / pending '+(a.pending??a.consumer_pending??0)+' / active '+(a.active_consumers??0);
    part('cutover',text,c.stale?'wait':'ok');
  }catch(e){part('cutover','cutover telemetry unavailable','bad')}

  try{
    const m=await get('/live/valheim-motion');
    const received=(m.received||0), relayed=(m.relayed_udp||0)+(m.relayed_websocket||0);
    const details='recv '+received+' (UDP '+(m.received_udp||0)+' / WS '+(m.received_websocket||0)+') / relay '+relayed;
    const text=received>0?'LJ motion observed / '+details:'native motion only / '+details;
    part('motion',text,received>0?'ok':'wait');
  }catch(e){part('motion','motion telemetry unavailable','bad')}
}

async function check(){
  q('#update').textContent='Checking the current alpha release...';
  try{
    manifest=await get('/api/v0/companion/update/check');
    q('#update').innerHTML='<strong class="ok">Release available</strong><p><span class="release">'+esc(manifest.release)+'</span> &middot; mod <span class="release">'+esc(manifest.mod_release)+'</span></p><p class="muted">Package: '+esc(manifest.package.size_bytes)+' bytes &middot; SHA-256 '+esc(manifest.package.sha256.slice(0,12))+'...</p>';
    part('modpack',(manifest.release||'unknown')+' / '+manifest.package.size_bytes+' bytes','ok');
    paint();
  }catch(e){
    q('#update').textContent='Could not check the release: '+JSON.stringify(e);
  }
}

async function install(){
  if(!confirm('Install the checked package into Valheim? Your current mod files will be backed up and your config will be preserved.'))return;
  q('#update').textContent='Downloading, verifying, and installing...';
  let r=await fetch('/api/v0/companion/update/install',{method:'POST',...confirmation()}),d=await r.json();
  q('#update').innerHTML=r.ok?'<strong class="ok">Installed.</strong><p>Launch Valheim to use the updated files. A rollback backup is available while the game is closed.</p>':'<strong class="bad">Install did not run.</strong><p>'+esc(d.result)+(d.detail?': '+esc(d.detail):'')+'</p>';
  manifest=null;
  await status();
}

async function rollback(){
  if(!confirm('Restore the files saved before the last Companion install?'))return;
  let r=await fetch('/api/v0/companion/update/rollback',{method:'POST',...confirmation()}),d=await r.json();
  q('#update').innerHTML=r.ok?'<strong class="ok">Rollback complete.</strong>':'<strong class="bad">Rollback did not run.</strong><p>'+esc(d.result)+'</p>';
  await status();
}

status();
companionRelease();
movingParts();
setInterval(movingParts,5000);
</script>
</body>
</html>
""";
}
