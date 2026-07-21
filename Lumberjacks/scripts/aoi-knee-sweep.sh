#!/usr/bin/env bash
# AoI knee sweep driver (HANDOFF task 4). Scriptable, no human in the loop.
# For a fixed radius config (set on the gateway beforehand), spawn wandering bots at each count,
# sample /tick at steady state (post-warmup, pre-disconnect), and emit one CSV row per count with
# the busiest window seen. Reports variance onset (p99/p50) and failure onset (overruns) as N scales.
#
# Usage: CONFIG_LABEL=default DUR=50 aoi-knee-sweep.sh 50 100 200 400
set -u
GW=${GW:-ws://localhost:4000}
TICK=${TICK:-http://localhost:4000/tick}
DUR=${DUR:-50}
LABEL=${CONFIG_LABEL:-unknown}
COUNTS=("$@")
OUT=${OUT:-/tmp/sweep_${LABEL}.csv}

sample() {  # prints: sent culled near mid far overruns t_p50 t_p99 i_p50 i_p99 i_max
  curl -s --max-time 4 "$TICK" | python -c "
import sys,json
d=json.load(sys.stdin); t=d['tick_timing']; r=t['replication']; p=t['phases']
print(r['sent'],r['culled'],r['near_pop'],r['mid_pop'],r['far_pop'],t['overruns'],
      p['total']['p50_ms'],p['total']['p99_ms'],
      p['interest']['p50_ms'],p['interest']['p99_ms'],p['interest']['max_ms'])
" 2>/dev/null
}

echo "config,bots,changed_per_tick,overruns,total_p50,total_p99,total_p99_p50_ratio,interest_p50,interest_p99,interest_max,sent,culled,near,mid,far" | tee "$OUT"

for N in "${COUNTS[@]}"; do
  BOT_WANDER=1 node scripts/load-test-dual-channel.js "$GW" "$N" "$DUR" >"/tmp/bots_${LABEL}_${N}.out" 2>&1 &
  BOTPID=$!
  sleep 28   # past 15s warmup + settle
  # take 3 samples ~4s apart, keep the busiest (max sent+culled)
  best=""; bestload=-1
  for s in 1 2 3; do
    row=$(sample)
    if [ -n "$row" ]; then
      load=$(echo "$row" | awk '{print $1+$2}')
      if [ "$load" -gt "$bestload" ] 2>/dev/null; then bestload=$load; best="$row"; fi
    fi
    sleep 4
  done
  # emit CSV row
  echo "$best" | awk -v c="$LABEL" -v n="$N" '{
    ratio = ($7>0)? $8/$7 : 0;
    cpt = ($1+$2)/100.0;   # (sent+culled) per ~100-tick window -> per-tick evaluated pairs
    printf "%s,%s,%.1f,%d,%.4f,%.4f,%.2f,%.4f,%.4f,%.4f,%d,%d,%d,%d,%d\n",
      c,n,cpt,$6,$7,$8,ratio,$9,$10,$11,$1,$2,$3,$4,$5
  }' | tee -a "$OUT"
  wait $BOTPID 2>/dev/null
  sleep 6    # drain players before next count
done
echo "=== done $LABEL -> $OUT ==="