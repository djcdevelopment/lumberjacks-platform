#!/usr/bin/env bash
# Gracefully save the selected Valheim world before the rest of P7 stops.
set -uo pipefail

compose_root=/opt/comfy/infra/gcp/p7
environment_file=/etc/comfy-p7/environment
evidence_root=/mnt/comfy-p7/evidence/shutdown
world_root=/mnt/comfy-p7/valheim/config/worlds_local
timeout_seconds="${P7_VALHEIM_STOP_TIMEOUT_SECONDS:-70}"

cd "$compose_root" || exit 1
if [[ ! -r "$environment_file" ]]; then
  echo "P7 environment is unreadable: $environment_file" >&2
  exit 1
fi
read_setting() {
  local key="$1"
  local fallback="$2"
  local value
  value="$(sed -n "s/^${key}=//p" "$environment_file" | tail -n 1)"
  value="${value%\"}"
  value="${value#\"}"
  [[ -n "$value" ]] && printf '%s' "$value" || printf '%s' "$fallback"
}
world_name="$(read_setting VALHEIM_WORLD_NAME ComfyEra16)"
timeout_seconds="$(read_setting P7_VALHEIM_STOP_TIMEOUT_SECONDS "$timeout_seconds")"
if [[ ! "$world_name" =~ ^[A-Za-z0-9_-]{1,64}$ ]]; then
  echo "Unsafe VALHEIM_WORLD_NAME: $world_name" >&2
  exit 1
fi
if [[ ! "$timeout_seconds" =~ ^[0-9]+$ ]] || (( timeout_seconds < 30 || timeout_seconds > 80 )); then
  echo "P7_VALHEIM_STOP_TIMEOUT_SECONDS must be 30..80" >&2
  exit 1
fi

mkdir -p "$evidence_root"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
started_utc="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
receipt="$evidence_root/$stamp-$world_name.json"
container="$(docker compose --env-file "$environment_file" ps -q valheim-server 2>/dev/null || true)"
was_running=false
save_logged=false
stop_exit=0
if [[ -n "$container" ]] && [[ "$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null || true)" == true ]]; then
  was_running=true
  docker compose --env-file "$environment_file" stop -t "$timeout_seconds" valheim-server || stop_exit=$?
  if docker logs --since "$started_utc" "$container" 2>&1 | grep -Fq 'World saved'; then
    save_logged=true
  fi
fi

db="$world_root/$world_name.db"
fwl="$world_root/$world_name.fwl"
db_bytes=0
fwl_bytes=0
[[ -f "$db" ]] && db_bytes="$(stat -c '%s' "$db")"
[[ -f "$fwl" ]] && fwl_bytes="$(stat -c '%s' "$fwl")"
new_files=0
for candidate in "$db.new" "$fwl.new"; do
  [[ -e "$candidate" ]] && new_files=$((new_files + 1))
done

status=passed
detail=already_stopped_world_pair_intact
if [[ "$was_running" == true ]]; then
  detail=graceful_stop_world_saved
  if (( stop_exit != 0 )) || [[ "$save_logged" != true ]]; then
    status=failed
    detail=graceful_stop_missing_world_saved_marker
  fi
fi
if (( db_bytes <= 0 || fwl_bytes <= 0 || new_files != 0 )); then
  status=failed
  detail=world_pair_missing_empty_or_interrupted
fi

stack_stop_exit=0
if [[ "$status" == passed ]]; then
  # Valheim is already stopped and saved. Give the stateless/control services only a short tail,
  # but do not report a safe VM shutdown while any part of the stack refused to stop.
  docker compose --env-file "$environment_file" stop -t 8 || stack_stop_exit=$?
  if (( stack_stop_exit != 0 )); then
    status=failed
    detail=remaining_stack_stop_failed
  fi
fi

jq -n \
  --arg schema 'comfy-p7-graceful-shutdown/v1' \
  --arg status "$status" \
  --arg detail "$detail" \
  --arg completed_utc "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  --arg world_name "$world_name" \
  --argjson was_running "$was_running" \
  --argjson save_logged "$save_logged" \
  --argjson db_bytes "$db_bytes" \
  --argjson fwl_bytes "$fwl_bytes" \
  --argjson interrupted_temp_files "$new_files" \
  --argjson stack_stop_exit "$stack_stop_exit" \
  '{schema:$schema,status:$status,detail:$detail,completed_utc:$completed_utc,
    world_name:$world_name,was_running:$was_running,world_saved_log_seen:$save_logged,
    db_bytes:$db_bytes,fwl_bytes:$fwl_bytes,interrupted_temp_files:$interrupted_temp_files,
    stack_stop_exit:$stack_stop_exit}' \
  > "$receipt.tmp"
mv "$receipt.tmp" "$receipt"

if [[ "$status" != passed ]]; then
  echo "P7 graceful shutdown gate failed; receipt: $receipt" >&2
  exit 1
fi

echo "$receipt"
