#!/usr/bin/env bash
# AoI BASELINE HEALTH HARNESS (HANDOFF task 4, re-scoped 2026-07-21).
#
# Goal is NOT a precise knee number — this harness can't drive enough load for that, and the
# absolute figure isn't the point. The goal is a CONSISTENT, re-runnable reference: the same coarse
# grid of configs x loads, capturing a broad set of health signals, so any future change (hysteresis,
# a send-path fix, a new policy) can be diffed against this committed baseline.
#
# Signals per cell: observed tick rate & drop (overruns, scheduler slip), where time goes
# (total/interest/send p50/p99), backpressure (degraded ticks, deadline aborts), load shape
# (sent/culled + near/mid/far band pop), the CPU knee (gateway container CPU/mem), and errors
# (client-side + gateway log). Fixed duration/warmup/sampling so runs are comparable.
set -u
COMPOSE=infra/docker/docker-compose.yml
GW=ws://localhost:4000
TICK=http://localhost:4000/tick
DUR=${DUR:-45}
COUNTS=(${COUNTS:-50 100 200})
OUT=${OUT:-/tmp/aoi_baseline.csv}
CONTAINER=docker-gateway-1

# configs: label|policy|near|mid|mti
CONFIGS=(
  "default_100_300|tiered|100|300|4"
  "aggressive_30_64|tiered|30|64|4"
  "full_no_aoi|full|0|0|4"
)

snap() { curl -s --max-time 4 "$TICK"; }

echo "config,policy,near,mid,bots,obs_tick_hz,overruns,interval_p99,total_p50,total_p99,interest_p99,send_p99,degraded_ticks,deadline_aborts,sent,culled,near_pop,mid_pop,far_pop,gw_cpu_pct,gw_mem_mb,bot_disc,bot_err,gw_log_err" | tee "$OUT"

for CFG in "${CONFIGS[@]}"; do
  IFS='|' read -r LABEL POL NEAR MID MTI <<< "$CFG"
  cat > /tmp/gw-ov.yml <<EOF
services:
  gateway:
    environment:
      Replication__Policy: "$POL"
      Replication__NearRadius: "$NEAR"
      Replication__MidRadius: "$MID"
      Replication__MidTickInterval: "$MTI"
      Replication__AdaptiveDegrade: "false"
EOF
  docker compose -f "$COMPOSE" -f /tmp/gw-ov.yml up -d --force-recreate gateway >/dev/null 2>&1
  for w in $(seq 1 15); do curl -s --max-time 3 "$TICK" >/dev/null 2>&1 && break; sleep 1; done
  sleep 4
  for N in "${COUNTS[@]}"; do
    START=$(date +%s)
    BOT_WANDER=1 node scripts/load-test-dual-channel.js "$GW" "$N" "$DUR" >"/tmp/b_${LABEL}_${N}.out" 2>&1 &
    BOTPID=$!
    sleep 26
    # observed tick rate over a 3s span
    c0=$(snap | python -c "import sys,json;print(json.load(sys.stdin)['current_tick'])" 2>/dev/null); t0=$(date +%s.%N)
    sleep 3
    c1=$(snap | python -c "import sys,json;print(json.load(sys.stdin)['current_tick'])" 2>/dev/null); t1=$(date +%s.%N)
    hz=$(python -c "print(f'{($c1-$c0)/($t1-$t0):.2f}')" 2>/dev/null || echo 0)
    # busiest /tick window of 2 samples
    S=$(snap); sleep 3; S2=$(snap)
    metrics=$(python -c "
import json
best=None
for s in ['''$S''','''$S2''']:
    try:
        d=json.loads(s); t=d['tick_timing']; r=t['replication']; p=t['phases']
        load=r['sent']+r['culled']
        row=[t['overruns'],p['interval']['p99_ms'],p['total']['p50_ms'],p['total']['p99_ms'],
             p['interest']['p99_ms'],p['send']['p99_ms'],r['degraded_ticks'],r['deadline_aborts'],
             r['sent'],r['culled'],r['near_pop'],r['mid_pop'],r['far_pop']]
        if best is None or load>best[0]: best=(load,row)
    except: pass
print(' '.join(str(x) for x in best[1]) if best else '')
" 2>/dev/null)
    # gateway CPU / mem point sample under load
    stats=$(docker stats --no-stream --format '{{.CPUPerc}} {{.MemUsage}}' "$CONTAINER" 2>/dev/null | awk '{gsub("%","",$1); print $1, $2}')
    cpu=$(echo "$stats" | awk '{print $1}'); mem=$(echo "$stats" | awk '{print $2}' | sed 's/[A-Za-z]*//g')
    wait $BOTPID 2>/dev/null
    disc=$(grep -oE "Disconnects: *[0-9]+" "/tmp/b_${LABEL}_${N}.out" | grep -oE "[0-9]+" | tail -1)
    berr=$(grep -oE "Errors: *[0-9]+" "/tmp/b_${LABEL}_${N}.out" | grep -oE "[0-9]+" | tail -1)
    gerr=$(docker logs --since "$START" "$CONTAINER" 2>&1 | grep -ciE "error|exception|unhandled" )
    echo "$metrics" | awk -v c="$LABEL" -v pol="$POL" -v nr="$NEAR" -v mr="$MID" -v n="$N" -v hz="$hz" \
        -v cpu="${cpu:-0}" -v mem="${mem:-0}" -v d="${disc:-0}" -v be="${berr:-0}" -v ge="${gerr:-0}" '{
      printf "%s,%s,%s,%s,%s,%s,%d,%.2f,%.4f,%.4f,%.4f,%.4f,%d,%d,%d,%d,%d,%d,%d,%s,%s,%s,%s,%s\n",
        c,pol,nr,mr,n,hz,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,cpu,mem,d,be,ge
    }' | tee -a "$OUT"
    sleep 6
  done
done
echo "=== baseline done -> $OUT ==="