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
:root{color-scheme:dark}*{box-sizing:border-box}body{max-width:1040px;margin:36px auto;padding:0 18px;background:#101319;color:#e8edf4;font:16px/1.45 system-ui,-apple-system,Segoe UI,sans-serif}h1{font-size:2.2rem;color:#43a6ff;margin:0}h2{margin:0 0 10px}p{margin:8px 0}.muted{color:#a7b1c2}.shell{display:flex;justify-content:space-between;gap:18px;align-items:start;margin-bottom:20px}.links a{display:inline-block;margin:0 0 6px 6px}.card{background:#191e27;border:1px solid #303846;border-radius:12px;padding:22px;margin:16px 0}.hero{border-color:#2d69a5;background:linear-gradient(135deg,#172635,#191e27)}.next{font-size:1.1rem;font-weight:700}.parts{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:10px;margin-top:12px}.part{background:#0c0f14;border:1px solid #303846;border-radius:8px;padding:12px}.part strong{display:block;color:#a7b1c2;font-size:.78rem;text-transform:uppercase;letter-spacing:.04em}.part span{display:block;margin-top:5px;font-family:ui-monospace,SFMono-Regular,Consolas,monospace;overflow-wrap:anywhere}.part.ok{border-color:#297a4f}.part.wait{border-color:#8b6c2a}.part.bad{border-color:#8b3a2f}.readout{display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:9px;margin-top:13px}.metric{background:#0c0f14;border:1px solid #28303c;border-radius:8px;padding:10px}.metric strong{display:block;color:#a7b1c2;font-size:.72rem;text-transform:uppercase;letter-spacing:.05em}.metric span{display:block;margin-top:4px;font:700 1rem ui-monospace,SFMono-Regular,Consolas,monospace;color:#e8edf4;overflow-wrap:anywhere}.metric.ok{border-color:#297a4f}.metric.wait{border-color:#8b6c2a}.metric.bad{border-color:#8b3a2f}.checks{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:9px;margin:14px 0 2px}.check{display:flex;align-items:center;gap:9px;background:#11161e;border:1px solid #8b6c2a;border-radius:8px;padding:11px 12px;color:#ffcf6b;font-weight:650}.check.ok{border-color:#297a4f;color:#77dc9b}.check input{appearance:none;width:19px;height:19px;margin:0;border:2px solid #d29336;border-radius:4px;background:#241c0d;flex:0 0 auto}.check input:checked{border-color:#55d780;background:#217747}.check input:checked::after{content:'\2713';display:block;color:#fff;font-size:14px;line-height:15px;text-align:center}.check input:disabled{opacity:1}.check.manual{cursor:pointer}.check.manual input{cursor:pointer}.ok{color:#77dc9b}.wait{color:#ffcf6b}.bad{color:#ff9877}button,a.btn{display:inline-block;background:#3479c7;color:#fff;border:0;border-radius:8px;padding:11px 16px;text-decoration:none;font:inherit;font-weight:650;cursor:pointer}button:hover,a.btn:hover{background:#4a8ddd}button:disabled{background:#384455;color:#9eaaba;cursor:not-allowed}.secondary{background:transparent;border:1px solid #586577;color:#dce6f5}.result{margin-top:13px;padding:13px;border-radius:8px;background:#0c0f14;border:1px solid #28303c}.result.ok{border-color:#297a4f}.result.wait{border-color:#8b6c2a}.result.bad{border-color:#8b3a2f}.release{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;color:#b8d8ff}.notice{padding:12px 14px;border-radius:8px;background:#123a26;border:1px solid #1f6b41;color:#b8f2ce}details{margin-top:16px}pre{white-space:pre-wrap;overflow:auto;background:#0c0f14;padding:12px;border-radius:8px;font-size:.84rem}@media(max-width:650px){.shell{display:block}.links a{margin:10px 8px 0 0}}
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
  <div class="readout" id="live-readout">
    <div class="metric wait" id="metric-peers"><strong>Peers</strong><span>?</span></div>
    <div class="metric wait" id="metric-players"><strong>Players</strong><span>none</span></div>
    <div class="metric wait" id="metric-motion"><strong>LJ motion</strong><span>0 / 0</span></div>
    <div class="metric wait" id="metric-cutover"><strong>Cutover</strong><span>unknown</span></div>
    <div class="metric wait" id="metric-queue"><strong>Queue</strong><span>pending ?</span></div>
    <div class="metric wait" id="metric-apply"><strong>Ack / apply</strong><span>? / ?</span></div>
  </div>
  <div id="evidence" class="result wait">Current read: waiting for live telemetry.</div>
</section>

<section class="card">
  <h2>Live signal stream</h2>
  <p class="muted">A compact rolling log of changes from the public telemetry path. If nothing is moving, this stays quiet.</p>
  <div id="signal-stream" class="result">Waiting for the first sample...</div>
</section>

<section class="card">
  <h2>Wave 0 live gate</h2>
  <p class="muted">Operator-minimal checklist for the two-client apply/observe proof. The browser does not move characters; it tells you when the existing automation is safe to run.</p>
  <div id="wave0-status" class="result wait">Checking Wave 0 status...</div>
  <div class="checks">
    <label class="check" id="w-local"><input id="wc-local" type="checkbox" disabled><span>Local profile and config ready</span></label>
    <label class="check" id="w-p7"><input id="wc-p7" type="checkbox" disabled><span>P7 telemetry readable</span></label>
    <label class="check" id="w-peers"><input id="wc-peers" type="checkbox" disabled><span>Two real clients joined</span></label>
    <label class="check" id="w-capture"><input id="wc-capture" type="checkbox" disabled><span>Recent evidence capture exists</span></label>
  </div>
  <div id="wave0-command" class="result">Waiting for the next command.</div>
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
  <h2>Capture transport evidence</h2>
  <p class="muted">Start this before a two-client movement test. Companion records Gateway, Valheim, cutover, and motion counters into local JSONL evidence. Use burst mode for sprint/stutter-step tests.</p>
  <p>
    <button id="capture-smoke" onclick="captureTransport(15,5,'smoke')">15s smoke</button>
    <button id="capture-burst" onclick="captureTransport(30,1,'movement-burst')">30s burst / 1s</button>
    <button id="capture" onclick="captureTransport(60,5,'movement')">60s movement</button>
    <button id="capture-long" onclick="captureTransport(180,5,'session')">180s session</button>
  </p>
  <div id="capture-result" class="result">No capture yet.</div>
  <div id="capture-history" class="result">Loading recent captures...</div>
</section>

<section class="card">
  <h2>Companion application</h2>
  <p class="muted">Local updater version: <span id="companion-version" class="release">Checking...</span></p>
  <p class="notice" id="companion-note">Checking Companion release status...</p>
  <p><a class="btn secondary" href="/api/v0/companion/diagnostics" download="lumberjacks-companion-diagnostics.json">Download redacted diagnostics</a></p>
</section>

<details><summary>Technical details</summary><pre id="technical">Loading...</pre></details>

<script>
let state=null,manifest=null,lastSignal=null,signalRows=[];
const q=s=>document.querySelector(s);

function esc(v){return String(v??'').replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]))}
async function get(u){let r=await fetch(u,{cache:'no-store'}),d=await r.json();if(!r.ok)throw d;return d}
function confirmation(){return {headers:{'Content-Type':'application/json'},body:JSON.stringify({game_closed_confirmed:true})}}

function setCheck(id,ok){
  q('#'+id).checked=ok;
  q('#l-'+id.slice(2)).className='check '+(ok?'ok':'wait');
}

function setWaveCheck(labelId,checkId,ok,level){
  q('#'+checkId).checked=ok;
  q('#'+labelId).className='check '+(ok?'ok':(level||'wait'));
}

function part(id,text,level='ok'){
  const el=q('#part-'+id);
  el.className='part '+level;
  el.querySelector('span').textContent=text;
}

function metric(id,text,level='wait'){
  const el=q('#metric-'+id);
  if(!el)return;
  el.className='metric '+level;
  el.querySelector('span').textContent=text;
}

function evidence(text,level='wait'){
  const el=q('#evidence');
  el.className='result '+level;
  el.textContent='Current read: '+text;
}

function stamp(){return new Date().toLocaleTimeString([], {hour:'2-digit',minute:'2-digit',second:'2-digit'})}

function emitSignal(text,level='wait'){
  signalRows.unshift({at:stamp(),text,level});
  signalRows=signalRows.slice(0,18);
  q('#signal-stream').innerHTML=signalRows.map(row=>'<div class="'+row.level+'"><span class="release">'+esc(row.at)+'</span> '+esc(row.text)+'</div>').join('');
}

function playerNames(valheim){
  const players=valheim?.heartbeat?.players||valheim?.players||[];
  return players.map(p=>p.name||p.player_name||p.character_name||p.steam_name||p.id||'unknown').join(', ');
}

function peerCount(valheim){
  return valheim?.peers??valheim?.peer_count??valheim?.heartbeat?.peer_count??0;
}

function valheimStatus(valheim){
  return valheim?.status??valheim?.server_state??valheim?.heartbeat?.server_state??'unknown';
}

function signalFrom(live){
  return {
    gateway: live.gateway_version||'unknown',
    peers: live.peers||0,
    players: live.players||'none',
    motion_received: live.motion_received,
    motion_relayed: live.motion_relayed,
    motion_state: live.motion_state,
    cutover: live.cutover||'unknown',
    pending: live.cutover_pending,
    active_consumers: live.active_consumers,
    acknowledged: live.acknowledged,
    applied: live.applied
  };
}

function paintReadout(live){
  const motionReceived=live.motion_received??0;
  const motionRelayed=live.motion_relayed??0;
  metric('peers',String(live.peers??0),(live.peers??0)>0?'ok':'wait');
  metric('players',live.players||'none',(live.peers??0)>0?'ok':'wait');
  const motionState=live.motion_state||'unknown';
  metric('motion',motionState+' / '+motionReceived+' recv / '+motionRelayed+' relay',motionReceived>0?'ok':((live.peers??0)>0?'wait':'wait'));
  metric('cutover',live.cutover||'unknown',live.gateway?'ok':'bad');
  metric('queue','pending '+(live.cutover_pending??'?')+' / consumers '+(live.active_consumers??'?'),(live.cutover_pending??0)>0?'wait':'ok');
  metric('apply',(live.acknowledged??'?')+' ack / '+(live.applied??'?')+' applied',live.gateway?'ok':'bad');
}

function diffSignal(next){
  if(!lastSignal){
    lastSignal=next;
    emitSignal('baseline: gateway '+next.gateway+'; peers '+next.peers+' ('+next.players+'); cutover '+next.cutover+'; motion recv '+next.motion_received+'; pending '+next.pending,'wait');
    return;
  }
  const changes=[];
  for(const key of Object.keys(next)){
    if(next[key]!==lastSignal[key])changes.push(key+' '+lastSignal[key]+' → '+next[key]);
  }
  if(changes.length>0){
    const level=changes.some(c=>c.startsWith('motion_received')||c.startsWith('motion_relayed'))?'ok':'wait';
    emitSignal(changes.join('; '),level);
    lastSignal=next;
  }
}

function counterLine(ranges){
  if(!ranges)return '';
  const parts=[];
  for(const key of ['peers','motion_received','motion_relayed','pending','active_consumers','acknowledged','applied']){
    const r=ranges[key];
    if(r)parts.push(key+' '+r.first+'→'+r.last+' ('+(r.delta>=0?'+':'')+r.delta+')');
  }
  return parts.join(' · ');
}

function identityLine(identity){
  if(!identity)return 'identity unknown';
  const parts=[];
  if(identity.gateway_version)parts.push('Gateway '+identity.gateway_version);
  if(identity.valheim_mod_version)parts.push('mod '+identity.valheim_mod_version);
  if(identity.valheim_instance_id)parts.push('server '+identity.valheim_instance_id);
  if(identity.cutover_mode)parts.push('cutover '+identity.cutover_mode);
  if(identity.bootstrap_release)parts.push('Companion '+identity.bootstrap_release);
  return parts.join(' · ')||'identity unknown';
}

function levelClass(level){
  return ['ok','wait','bad'].includes(level)?level:'wait';
}

function interpretationBlock(i){
  if(!i)return '';
  const level=levelClass(i.level);
  return '<p><strong class="'+level+'">'+esc(i.headline||'No interpretation recorded.')+'</strong><br>'+esc(i.next_action||'')+'<br><span class="muted">'+esc(i.evidence||'')+'</span></p>';
}

function commandBlock(title,command){
  if(!command)return '';
  return '<p><strong>'+esc(title)+'</strong></p><pre>'+esc(command)+'</pre>';
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
  }else if(!v.found){
    q('#headline').textContent='Read-only dashboard: Valheim is not visible';
    q('#next').textContent='Updates need the Valheim folder mounted. Re-run the Companion bootstrap, or on i5 use Start-I5Companion.ps1 so /valheim is mounted.';
  }else if(!v.config_found){
    q('#headline').textContent='Valheim found, Lumberjacks config missing';
    q('#next').textContent='Install or repair the ComfyNetworkSense config before using client-pull updates. Existing access keys are never shown here.';
  }else if(!p.linked){
    q('#headline').textContent='Local profile not linked yet';
    q('#next').textContent='Claim the installed profile or complete Steam enrollment before installing updates.';
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
    const latest=r.latest_bootstrap;
    q('#companion-version').textContent='app '+(r.companion_version||'unknown')+' / bootstrap '+(r.bootstrap_release||'unknown');
    if(r.update_available&&latest?.downloads?.package){
      q('#companion-note').innerHTML='<strong>New Companion bootstrap available.</strong><br>Local: <span class="release">'+esc(r.bootstrap_release||'unknown')+'</span><br>Latest: <span class="release">'+esc(latest.release)+'</span><br><a class="btn secondary" href="'+esc(latest.downloads.package)+'">Download latest bootstrap</a> <a href="'+esc(latest.downloads.manifest||'#')+'">manifest</a>';
    }else if(latest){
      q('#companion-note').innerHTML='<strong>Companion bootstrap current.</strong><br><span class="release">'+esc(latest.release)+'</span> · sha256 '+esc((latest.package?.sha256||'').slice(0,12))+'...';
    }else{
      q('#companion-note').textContent=r.error?'Could not check public Companion bootstrap: '+r.error:r.note;
    }
  }catch(e){
    q('#companion-version').textContent='Unknown';
    q('#companion-note').textContent='Could not check Companion release status.';
  }
}

async function movingParts(){
  const live={gateway:false,gateway_version:null,valheim:false,peers:0,players:'none',motion_received:null,motion_relayed:null,cutover:'unknown',cutover_pending:null,active_consumers:null,acknowledged:null,applied:null};
  try{
    const m=await get('/api/v0/companion/update/check');
    manifest=manifest||m;
    part('modpack',(m.release||'unknown')+' / '+(m.package?.size_bytes||0)+' bytes','ok');
  }catch(e){part('modpack','manifest unavailable','bad')}

  try{
    const d=await get('/api/v0/telemetry/deployment');
    live.gateway=true;
    live.gateway_version=d.lumberjacks_version||'unknown';
    part('gateway',(d.environment||'gateway')+' / '+(d.lumberjacks_version||'unknown'),'ok');
  }catch(e){part('gateway','deployment telemetry unavailable','bad')}

  try{
    const v=await get('/api/v0/telemetry/valheim');
    live.valheim=!v.stale;
    live.peers=peerCount(v);
    live.players=playerNames(v)||'none';
    live.motion_state=v.heartbeat?.motion_state||'unknown';
    live.motion_ws=v.heartbeat?.motion_websocket_connected;
    live.motion_udp=v.heartbeat?.motion_udp_ready;
    live.motion_error=v.heartbeat?.motion_last_error||'';
    const text=valheimStatus(v)+' / '+live.peers+' peers';
    part('valheim',text,v.stale?'wait':'ok');
  }catch(e){part('valheim','heartbeat unavailable','bad')}

  try{
    const c=await get('/api/v0/telemetry/cutover');
    const a=c.authoritative_window||{};
    live.cutover=c.mode||c.state||'unknown';
    live.cutover_pending=a.pending??a.consumer_pending??0;
    live.active_consumers=a.active_consumers??0;
    live.acknowledged=a.consumer_acknowledged??a.acknowledged??0;
    live.applied=a.applied??0;
    const text=(c.mode||c.state||'unknown')+' / pending '+(a.pending??a.consumer_pending??0)+' / active '+(a.active_consumers??0);
    part('cutover',text,c.stale?'wait':'ok');
  }catch(e){part('cutover','cutover telemetry unavailable','bad')}

  try{
    const m=await get('/live/valheim-motion');
    const received=(m.received||0), relayed=(m.relayed_udp||0)+(m.relayed_websocket||0);
    live.motion_received=received;
    live.motion_relayed=relayed;
    const state=live.motion_state||'unknown';
    const readiness=value=>value===true?'up':value===false?'down':'unknown';
    const controls='client '+state+' / WS '+readiness(live.motion_ws)+' / UDP '+readiness(live.motion_udp);
    const details='recv '+received+' (UDP '+(m.received_udp||0)+' / WS '+(m.received_websocket||0)+') / relay '+relayed;
    const error=live.motion_error?' / error '+live.motion_error:'';
    const text=received>0?'LJ motion observed / '+controls+' / '+details+error:controls+' / '+details+error;
    part('motion',text,received>0?'ok':(state==='error'?'bad':'wait'));
  }catch(e){part('motion','motion telemetry unavailable','bad')}

  paintReadout(live);

  if(!live.gateway){
    evidence('Gateway telemetry is unavailable; local update checks may still work, but live network evidence is not trustworthy.','bad');
  }else if(live.motion_received>0){
    evidence('Lumberjacks motion frames are arriving. Compare in-game movement against the Motion tile and trace before judging interpolation.','ok');
  }else if(live.motion_received===0&&live.peers>0){
    evidence('Valheim has '+live.peers+' peer(s), but Lumberjacks motion counters are zero. Visible player movement is still native Valheim for this run.','wait');
  }else if(live.motion_received===null){
    evidence('Motion telemetry is unavailable; use the in-game strip and trace before interpreting player movement.','bad');
  }else if(live.valheim){
    evidence('P7 is up with no active peers. Join two clients, then watch Valheim peers and Motion counters change together.','wait');
  }else{
    evidence('Waiting for Valheim heartbeat before interpreting the transport path.','wait');
  }
  diffSignal(signalFrom(live));
}

async function wave0Status(){
  try{
    const w=await get('/api/v0/companion/wave0/status');
    const level=levelClass(w.level||'wait');
    q('#wave0-status').className='result '+level;
    const players=(w.p7?.players||[]).join(', ')||'none';
    const cap=w.latest_capture;
    q('#wave0-status').innerHTML='<strong>'+esc(w.verdict)+'</strong><p>'+esc(w.next_action||'')+'</p><p>P7 peers: '+esc(w.p7?.peer_count??0)+' · players: '+esc(players)+' · motion received: '+esc(w.p7?.motion_received??0)+' · relayed: '+esc(w.p7?.motion_relayed??0)+'</p>';
    setWaveCheck('w-local','wc-local',!!(w.local?.valheim_found&&w.local?.config_found&&w.local?.profile_linked),'bad');
    setWaveCheck('w-p7','wc-p7',!!(w.p7?.gateway_ready&&w.p7?.valheim_ready&&w.p7?.motion_ready),'bad');
    setWaveCheck('w-peers','wc-peers',(w.p7?.peer_count??0)>=2,'wait');
    setWaveCheck('w-capture','wc-capture',!!cap,'wait');
    let command=w.commands?.prelive||'Run Test-Wave0Prelive.ps1 from OMEN.';
    let title='Recommended command';
    let chain='';
    if(w.verdict==='ready_for_live_gate'||w.verdict==='motion_evidence_present'){
      command=w.commands?.live_omen_applies||command;
      title='First live-gate command';
      chain=[
        commandBlock('1. First live gate: OMEN applies',w.commands?.live_omen_applies),
        commandBlock('2. Annotate first visual pass',w.commands?.annotate_omen_applies),
        commandBlock('3. Role reversal: i5 applies',w.commands?.live_i5_applies),
        commandBlock('4. Annotate reversal',w.commands?.annotate_i5_applies),
        commandBlock('5. Seal visual evidence',w.commands?.seal_visual_evidence)
      ].join('');
    }
    q('#wave0-command').innerHTML='<strong>'+esc(title)+'</strong><pre>'+esc(command)+'</pre>'+chain+(cap?'<p>Latest capture: <span class="release">'+esc(cap.run_id)+'</span> · '+esc(cap.verdict)+' · max peers '+esc(cap.max_peers)+'</p>':'<p class="muted">No recent capture surfaced yet.</p>');
  }catch(e){
    q('#wave0-status').className='result bad';
    q('#wave0-status').textContent='Could not read Wave 0 status: '+JSON.stringify(e);
  }
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

function setCaptureButtons(disabled){
  for(const id of ['capture-smoke','capture-burst','capture','capture-long'])q('#'+id).disabled=disabled;
}

function captureMotionLine(c){
  const states=(c.observed_motion_states||[]).join(', ')||'unknown';
  const readiness=value=>value===true?'up':value===false?'down':'unknown';
  const error=c.final_motion_last_error?' / error '+c.final_motion_last_error:'';
  return 'client '+states+' / WS '+readiness(c.final_motion_websocket_connected)+' / UDP '+readiness(c.final_motion_udp_ready)+error;
}

async function captureTransport(seconds,interval,label){
  setCaptureButtons(true);
  q('#capture-result').textContent='Capturing for '+seconds+' seconds at '+interval+'s intervals ('+label+'). Move both clients now; leave this browser tab open.';
  try{
    let r=await fetch('/api/v0/companion/transport-capture',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({duration_seconds:seconds,interval_seconds:interval,label:'companion-ui-'+label})});
    let d=await r.json();
    if(!r.ok)throw d;
    const level=levelClass(d.interpretation?.level||(d.bad_sample_count>0?'wait':(d.motion_received_delta>0?'ok':'wait')));
    q('#capture-result').className='result '+level;
    const base='/api/v0/companion/transport-capture/'+encodeURIComponent(d.run_id)+'/';
    const players=(d.observed_players||[]).join(', ')||'none';
    const counters=counterLine(d.counter_ranges)||'no counters';
    q('#capture-result').innerHTML='<strong>Capture complete: '+esc(d.verdict||'unknown')+'</strong><p>'+esc(d.final_current_read?.text||'No final read recorded.')+'</p><p>Run <span class="release">'+esc(d.run_id)+'</span></p><p>'+esc(identityLine(d.capture_identity))+'</p><p>Players: '+esc(players)+'</p><p>Samples: '+esc(d.sample_count)+' · max peers: '+esc(d.max_peers)+' · '+esc(counters)+'</p><p><a class="btn secondary" href="'+base+'summary.json">Download summary</a> <a class="btn secondary" href="'+base+'samples.jsonl">Download samples</a></p>';
    q('#capture-result').insertAdjacentHTML('beforeend','<p><a class="btn secondary" href="'+base+'bundle.zip">Download evidence bundle</a></p>');
    q('#capture-result').insertAdjacentHTML('afterbegin','<p>'+esc(captureMotionLine(d))+'</p>');
    if(d.interpretation)q('#capture-result').insertAdjacentHTML('afterbegin',interpretationBlock(d.interpretation));
    await captureHistory();
  }catch(e){
    q('#capture-result').className='result bad';
    q('#capture-result').textContent='Capture failed: '+JSON.stringify(e);
  }finally{
    setCaptureButtons(false);
  }
}

async function captureHistory(){
  try{
    const d=await get('/api/v0/companion/transport-capture');
    const captures=d.captures||[];
    if(captures.length===0){
      q('#capture-history').textContent='No saved captures yet.';
      return;
    }
    q('#capture-history').innerHTML='<strong>Recent captures</strong>'+captures.map(c=>{
      const base='/api/v0/companion/transport-capture/'+encodeURIComponent(c.run_id)+'/';
      const players=(c.observed_players||[]).join(', ')||'none';
      const counters=counterLine(c.counter_ranges)||('peers '+c.max_peers+' · motion delta '+c.motion_received_delta);
      return '<p><strong>'+esc(c.verdict||'unknown')+'</strong> · <span class="release">'+esc(c.run_id)+'</span><br>'+esc(c.final_current_read?.text||'No final read recorded.')+'<br>'+esc(identityLine(c.capture_identity))+'<br>players '+esc(players)+'<br>'+esc(counters)+' · samples '+esc(c.sample_count)+'<br><a href="'+base+'summary.json">summary</a> · <a href="'+base+'samples.jsonl">samples</a></p>';
    }).join('');
    const bundles=captures.map(c=>{
      const base='/api/v0/companion/transport-capture/'+encodeURIComponent(c.run_id)+'/';
      return '<p><span class="release">'+esc(c.run_id)+'</span> <a class="btn secondary" href="'+base+'bundle.zip">Download evidence bundle</a></p>';
    }).join('');
    if(bundles)q('#capture-history').insertAdjacentHTML('beforeend','<strong>Evidence bundles</strong>'+bundles);
    const readiness=captures.map(c=>'<p><span class="release">'+esc(c.run_id)+'</span> '+esc(captureMotionLine(c))+'</p>').join('');
    if(readiness)q('#capture-history').insertAdjacentHTML('beforeend','<strong>Motion readiness</strong>'+readiness);
    const interpreted=captures.filter(c=>c.interpretation).map(c=>'<p><span class="release">'+esc(c.run_id)+'</span>'+interpretationBlock(c.interpretation)+'</p>').join('');
    if(interpreted)q('#capture-history').insertAdjacentHTML('beforeend','<strong>Recent interpretations</strong>'+interpreted);
  }catch(e){
    q('#capture-history').className='result bad';
    q('#capture-history').textContent='Could not load recent captures: '+JSON.stringify(e);
  }
}

status();
companionRelease();
movingParts();
wave0Status();
captureHistory();
setInterval(movingParts,5000);
setInterval(wave0Status,5000);
</script>
</body>
</html>
""";
}
