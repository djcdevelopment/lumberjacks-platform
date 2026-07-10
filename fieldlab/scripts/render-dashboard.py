#!/usr/bin/env python3
"""Render fieldlab/status/program-status.json into the live dashboard HTML.

Usage:  python fieldlab/scripts/render-dashboard.py
Output: fieldlab/status/dashboard.html  (a self-contained fragment; published to the
        claude.ai Artifact URL by the driving session — same URL on every redeploy).

Keep this deterministic: JSON in, HTML out, no network, no timestamps invented here
(the `updated` field in the JSON is the display time).
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]  # fieldlab/
STATUS = ROOT / "status" / "program-status.json"
OUT = ROOT / "status" / "dashboard.html"

# Nordic Glass — dark. Matches the in-game house theme (network/mod/ComfyNetworkSense/UI-DESIGN.md)
# Deliberately single-theme dark: this is the game program's ops console.
TEMPLATE = r"""
<title>Netcode Replacement — Live Program Dashboard</title>
<style>
  :root{
    --bg:#0a0e13; --bg2:#070a0e;
    --glass:rgba(18,27,37,.66); --glass2:rgba(25,36,49,.5);
    --line:rgba(122,162,192,.16); --line-soft:rgba(122,162,192,.08);
    --ink:#e8eef4; --slate:#93a2b3; --faint:#5f6b7a;
    --cyan:#63d3e6; --cyan-deep:#3fa9bd;
    --good:#5ed09a; --amber:#e6b74d; --rust:#e0684a;
    --serif:"Iowan Old Style","Palatino Linotype",Palatino,"Book Antiqua",Georgia,serif;
    --sans:system-ui,-apple-system,"Segoe UI",Roboto,"Helvetica Neue",sans-serif;
    --mono:"Cascadia Code","JetBrains Mono","SF Mono",Consolas,"Liberation Mono",monospace;
  }
  html{background:linear-gradient(180deg,var(--bg) 0%,var(--bg2) 100%);min-height:100%}
  body{margin:0;font-family:var(--sans);color:var(--ink);
    background:
      radial-gradient(120% 80% at 50% -10%,rgba(63,169,189,.10) 0%,transparent 55%),
      radial-gradient(90% 60% at 100% 0%,rgba(94,208,154,.05) 0%,transparent 50%);
  }
  .shell{max-width:1080px;margin:0 auto;padding:28px 20px 64px}
  .kicker{font-family:var(--mono);font-size:11px;letter-spacing:.14em;text-transform:uppercase;color:var(--cyan);margin:0 0 6px}
  h1{font-family:var(--serif);font-weight:600;font-size:clamp(24px,4vw,34px);line-height:1.15;margin:0 0 4px;text-wrap:balance}
  h1 em{font-style:italic;color:var(--cyan)}
  .sub{color:var(--slate);font-size:14px;margin:0 0 22px}
  .sub code{font-family:var(--mono);font-size:12px;color:var(--ink);background:var(--glass2);padding:1px 6px;border-radius:4px}
  .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:12px;margin-bottom:14px}
  .card{background:var(--glass);border:1px solid var(--line);border-radius:10px;padding:14px 16px;backdrop-filter:blur(6px)}
  .card .lbl{font-family:var(--mono);font-size:10.5px;letter-spacing:.12em;text-transform:uppercase;color:var(--faint);margin-bottom:6px}
  .card .big{font-family:var(--serif);font-size:26px;font-variant-numeric:tabular-nums}
  .card .note{color:var(--slate);font-size:12.5px;margin-top:4px;line-height:1.45}
  .derek{border-color:rgba(94,208,154,.4)}
  .derek .big{font-size:17px;font-family:var(--sans);color:var(--good);line-height:1.4}
  .rail{display:flex;gap:8px;overflow-x:auto;padding:4px 0 10px;margin-bottom:8px}
  .ph{min-width:118px;flex:1;background:var(--glass2);border:1px solid var(--line);border-radius:9px;padding:10px 12px;cursor:pointer;position:relative}
  .ph:focus-visible{outline:2px solid var(--cyan);outline-offset:2px}
  .ph .id{font-family:var(--mono);font-size:11px;color:var(--faint)}
  .ph .nm{font-size:12.5px;line-height:1.3;margin:3px 0 8px;color:var(--ink)}
  .ph .bar{height:4px;border-radius:2px;background:var(--line-soft);overflow:hidden}
  .ph .bar i{display:block;height:100%;background:var(--cyan-deep)}
  .ph[data-s=done]{border-color:rgba(94,208,154,.45)} .ph[data-s=done] .bar i{background:var(--good)}
  .ph[data-s=active]{border-color:var(--cyan);box-shadow:0 0 14px rgba(99,211,230,.12)}
  .ph[data-s=active] .id::after{content:"● live";color:var(--cyan);margin-left:6px;font-size:9.5px;letter-spacing:.08em}
  .chip{display:inline-block;font-family:var(--mono);font-size:10px;letter-spacing:.06em;padding:2px 8px;border-radius:999px;border:1px solid var(--line);color:var(--slate)}
  .chip.done{color:var(--good);border-color:rgba(94,208,154,.4)}
  .chip.active{color:var(--cyan);border-color:rgba(99,211,230,.45)}
  .chip.derek{color:var(--amber);border-color:rgba(230,183,77,.4)}
  section{background:var(--glass);border:1px solid var(--line);border-radius:12px;padding:18px 20px;margin:14px 0}
  section>h2{font-family:var(--serif);font-size:19px;font-weight:600;margin:0 0 12px}
  table{width:100%;border-collapse:collapse;font-size:13px}
  .tblwrap{overflow-x:auto}
  th{font-family:var(--mono);font-size:10.5px;letter-spacing:.1em;text-transform:uppercase;color:var(--faint);text-align:left;padding:6px 10px;border-bottom:1px solid var(--line)}
  td{padding:7px 10px;border-bottom:1px solid var(--line-soft);vertical-align:top;color:var(--ink)}
  td.n{font-family:var(--mono);color:var(--faint);font-variant-numeric:tabular-nums;white-space:nowrap}
  td.gate{color:var(--slate);font-size:12px}
  .vlist{display:grid;gap:10px}
  .v{display:grid;grid-template-columns:10px 1fr;gap:12px;align-items:start}
  .v .dot{width:10px;height:10px;border-radius:50%;margin-top:5px}
  .v[data-v=good] .dot{background:var(--good)} .v[data-v=warn] .dot{background:var(--amber)} .v[data-v=bad] .dot{background:var(--rust)}
  .v p{margin:0;font-size:13.5px;line-height:1.55;color:var(--ink)}
  .infra{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:10px}
  .inode{display:grid;grid-template-columns:9px 1fr;gap:10px;align-items:start;background:var(--glass2);border:1px solid var(--line-soft);border-radius:8px;padding:10px 12px}
  .inode .led{width:9px;height:9px;border-radius:50%;margin-top:4px}
  .inode[data-s=up] .led,.inode[data-s=ready] .led{background:var(--good)}
  .inode[data-s=down-expected] .led{background:var(--faint)}
  .inode[data-s=warn] .led{background:var(--amber)}
  .inode b{font-size:13px} .inode span{display:block;color:var(--slate);font-size:12px;line-height:1.45;margin-top:2px}
  details{margin-top:8px} summary{cursor:pointer;color:var(--cyan);font-size:13px}
  summary:focus-visible{outline:2px solid var(--cyan);outline-offset:2px}
  footer{color:var(--faint);font-size:11.5px;font-family:var(--mono);margin-top:26px;line-height:1.6}
  @media (prefers-reduced-motion:no-preference){ .ph{transition:border-color .15s ease} }
</style>
<div class="shell">
  <p class="kicker">Valheim × Lumberjacks · Live Program Dashboard</p>
  <h1>Netcode replacement, <em>one proven invariant at a time.</em></h1>
  <p class="sub" id="sub"></p>

  <div class="cards" id="cards"></div>
  <div class="rail" id="rail" role="tablist" aria-label="Program phases"></div>
  <section><h2 id="phTitle"></h2><div class="tblwrap"><table id="phTable"></table></div></section>

  <section><h2>Audit verdicts — the trust reset</h2><div class="vlist" id="verdicts"></div>
    <details><summary>What the numbers mean</summary>
      <p style="color:var(--slate);font-size:13px;line-height:1.6" id="trustNote"></p>
    </details>
  </section>

  <section><h2>Infrastructure</h2><div class="infra" id="infra"></div></section>

  <footer id="foot"></footer>
</div>
<script>
const S = /*__STATUS_JSON__*/ null;
const $ = (id) => document.getElementById(id);
const esc = (s) => String(s).replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));

const phases = S.phases;
const doneSteps = p => p.steps.filter(s => s.s === 'done').length;
const totSteps  = phases.reduce((a,p)=>a+p.steps.length,0);
const totDone   = phases.reduce((a,p)=>a+doneSteps(p),0);
const active    = phases.find(p=>p.status==='active') || phases.find(p=>p.status!=='done') || phases[0];

$('sub').innerHTML = `Updated <code>${esc(S.updated)}</code> · state: <code>${esc(S.canonical_docs.state)}</code> · plan: <code>${esc(S.canonical_docs.plan)}</code>`;

$('cards').innerHTML = `
  <div class="card derek"><div class="lbl">Needs Derek right now</div>
    <div class="big">${S.needs_derek.length ? S.needs_derek.map(esc).join('<br>') : 'Nothing.'}</div>
    <div class="note">${esc(S.next_derek_touchpoint)}</div></div>
  <div class="card"><div class="lbl">Program</div><div class="big">${phases.filter(p=>p.status==='done').length}<span style="color:var(--faint)">/${phases.length}</span> <span style="font-size:15px;color:var(--slate)">phases</span></div>
    <div class="note">${totDone}/${totSteps} steps gated green · active: <b>${esc(active.id)} ${esc(active.title)}</b></div></div>
  <div class="card"><div class="lbl">Trust ledger</div><div class="big" style="color:var(--good)">${S.trust.confirmed}<span style="font-size:15px;color:var(--slate)"> confirmed</span></div>
    <div class="note">${S.trust.refuted} refuted · ${S.trust.uncorroborated} uncorroborated · ${S.trust.red_herrings_catalogued} dead theories catalogued</div></div>`;

$('rail').innerHTML = phases.map(p=>{
  const pct = Math.round(100*doneSteps(p)/p.steps.length);
  return `<div class="ph" role="tab" tabindex="0" data-s="${p.status}" data-id="${p.id}" aria-label="${esc(p.id)} ${esc(p.title)}, ${pct}% done">
    <div class="id">${esc(p.id)} · ${esc(p.ladder)}</div><div class="nm">${esc(p.title)}</div>
    <div class="bar"><i style="width:${pct}%"></i></div></div>`;
}).join('');

function showPhase(p){
  $('phTitle').innerHTML = `${esc(p.id)} — ${esc(p.title)} <span class="chip ${p.status==='done'?'done':p.status==='active'?'active':''}">${esc(p.status)}</span> <span class="chip derek">Derek: ~${p.human_minutes} min</span>`;
  $('phTable').innerHTML = `<tr><th style="width:34px">#</th><th>Step</th><th style="width:90px">Status</th><th>Gate</th></tr>` +
    p.steps.map(s=>`<tr><td class="n">${s.n}</td><td>${s.o==='derek'?'<span class="chip derek">DEREK</span> ':''}${esc(s.t)}</td>
      <td><span class="chip ${s.s==='done'?'done':s.s==='active'?'active':''}">${esc(s.s)}</span></td><td class="gate">${esc(s.g||'')}</td></tr>`).join('');
}
showPhase(active);
document.querySelectorAll('.ph').forEach(el=>{
  const open = ()=> showPhase(phases.find(p=>p.id===el.dataset.id));
  el.addEventListener('click', open);
  el.addEventListener('keydown', e=>{ if(e.key==='Enter'||e.key===' '){e.preventDefault();open();} });
});

$('verdicts').innerHTML = S.trust.headlines.map(h=>`<div class="v" data-v="${h.verdict}"><div class="dot"></div><p>${esc(h.text)}</p></div>`).join('');
$('trustNote').textContent = `${S.trust.claims_total} claims audited across 6 independent lanes (mod code, git forensics, run evidence, doc conflicts, live infra, session transcripts) + adversarial verification. Rule: ${S.trust.rule}`;
$('infra').innerHTML = S.infra.map(i=>`<div class="inode" data-s="${i.state}"><div class="led"></div><div><b>${esc(i.name)}</b><span>${esc(i.detail)}</span></div></div>`).join('');
$('foot').innerHTML = `generated from <b>fieldlab/status/program-status.json</b> by render-dashboard.py · every gate that passes updates the JSON and this page in the same slice · full state: ${esc(S.canonical_docs.state)}`;
</script>
"""


def main() -> int:
    status = json.loads(STATUS.read_text(encoding="utf-8"))
    html = TEMPLATE.replace("/*__STATUS_JSON__*/ null", json.dumps(status, ensure_ascii=False))
    OUT.write_text(html.strip() + "\n", encoding="utf-8")
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
