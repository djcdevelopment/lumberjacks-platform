#!/usr/bin/env bash
# AoI knee MATRIX driver (HANDOFF task 4). Sweeps the (NearRadius x bot-count) plane, recreating
# the gateway per radius (MidRadius pinned, say which). Emits one CSV row per cell with the busiest
# steady-state /tick window. This is the surface the brief asks for + the grid the model predicts.
set -u
COMPOSE=infra/docker/docker-compose.yml
GW=ws://localhost:4000
TICK=http://localhost:4000/tick
MID=${MID:-300}          # MidRadius pinned for the range sweep
MTI=${MTI:-4}
DUR=${DUR:-45}
RADII=(${RADII:-30 60 100 150 200})
COUNTS=(${COUNTS:-100 200})
OUT=${OUT:-/tmp/aoi_matrix.csv}

sample() {
  curl -s --max-time 4 "$TICK" | python -c "
import sys,json
d=json.load(sys.stdin); t=d['tick_timing']; r=t['replication']; p=t['phases']
print(r['sent'],r['culled'],r['near_pop'],r['mid_pop'],r['far_pop'],t['overruns'],
      p['total']['p50_ms'],p['total']['p99_ms'],p['interest']['p50_ms'],p['interest']['p99_ms'],
      p['send']['p50_ms'],p['send']['p99_ms'])
" 2>/dev/null
}

echo "near_radius,mid_radius,bots,overruns,total_p50,total_p99,ratio,interest_p99,send_p99,sent,culled,near_pop,mid_pop,far_pop,dgram_ups_per_s" | tee "$OUT"

for NEAR in "${RADII[@]}"; do
  cat > /tmp/gw-ov.yml <<EOF
services:
  gateway:
    environment:
      Replication__NearRadius: "$NEAR"
      Replication__MidRadius: "$MID"
      Replication__MidTickInterval: "$MTI"
      Replication__AdaptiveDegrade: "false"
EOF
  docker compose -f "$COMPOSE" -f /tmp/gw-ov.yml up -d --force-recreate gateway >/dev/null 2>&1
  # wait for tick loop (window populated)
  for w in $(seq 1 15); do curl -s --max-time 3 "$TICK" >/dev/null 2>&1 && break; sleep 1; done
  sleep 4
  for N in "${COUNTS[@]}"; do
    BOT_WANDER=1 node scripts/load-test-dual-channel.js "$GW" "$N" "$DUR" >"/tmp/m_${NEAR}_${N}.out" 2>&1 &
    BOTPID=$!
    sleep 26
    best=""; bestload=-1
    for s in 1 2 3; do
      row=$(sample)
      if [ -n "$row" ]; then
        load=$(echo "$row" | awk '{print $1+$2}')
        [ "$load" -gt "$bestload" ] 2>/dev/null && { bestload=$load; best="$row"; }
      fi
      sleep 4
    done
    echo "$best" | awk -v nr="$NEAR" -v mr="$MID" -v n="$N" -v dur="$DUR" '{
      ratio=($7>0)?$8/$7:0;
      # datagram updates/sec = sent over the ~100-tick (~5s) window; report per second
      ups=$1/5.0;
      printf "%s,%s,%s,%d,%.4f,%.4f,%.2f,%.4f,%.4f,%d,%d,%d,%d,%d,%.0f\n",
        nr,mr,n,$6,$7,$8,ratio,$10,$12,$1,$2,$3,$4,$5,ups
    }' | tee -a "$OUT"
    wait $BOTPID 2>/dev/null
    sleep 6
  done
done
echo "=== matrix done -> $OUT ==="