#!/usr/bin/env bash
# Install one independently verified CreatorOS Beta 1 server slice and its P7 controls.
set -Eeuo pipefail

archive="${1:?server archive required}"
controls_archive="${2:?platform-controls archive required}"
deployment_manifest="${3:?deployment manifest required}"
activate="${4:-false}"
state_root=/mnt/comfy-p7
environment_file=/etc/comfy-p7/environment
compose_root=/opt/comfy/infra/gcp/p7
compose_target="$compose_root/docker-compose.yml"
unit_target=/etc/systemd/system/comfy-lumberjacks-p7.service
stop_target="$compose_root/scripts/stop-p7-stack.sh"
service_name=comfy-lumberjacks-p7.service

[[ "$activate" == true || "$activate" == false ]] || { echo 'activate must be true or false' >&2; exit 1; }
[[ -f "$archive" && -f "$controls_archive" && -f "$deployment_manifest" ]] || {
  echo 'upload is incomplete' >&2; exit 1;
}
jq -e '.schema == "comfy-p7-creatoros-deploy/v2" and
  .verification.schema == "comfy-p7-creatoros-server-verification/v1" and
  .verification.status == "valid" and (.control_files | length == 3)' \
  "$deployment_manifest" >/dev/null
release_hash="$(jq -r '.verification.release_manifest_sha256' "$deployment_manifest")"
archive_hash="$(jq -r '.archive_sha256' "$deployment_manifest")"
controls_hash="$(jq -r '.controls_archive_sha256' "$deployment_manifest")"
world_uid="$(jq -r '.verification.world_uid' "$deployment_manifest")"
world_pair_hash="$(jq -r '.verification.world_pair_hash' "$deployment_manifest")"
pack_hash="$(jq -r '.verification.pack_content_hash' "$deployment_manifest")"
[[ "$release_hash" =~ ^[0-9a-f]{64}$ && "$archive_hash" =~ ^[0-9a-f]{64}$ &&
   "$controls_hash" =~ ^[0-9a-f]{64}$ && "$world_pair_hash" =~ ^[0-9a-f]{64}$ &&
   "$pack_hash" =~ ^[0-9a-f]{64}$ ]] || { echo 'deployment hashes are invalid' >&2; exit 1; }
[[ "$world_uid" =~ ^-?[1-9][0-9]*$ ]] || { echo 'world UID is invalid' >&2; exit 1; }
[[ "$(sha256sum "$archive" | cut -d' ' -f1)" == "$archive_hash" ]] || {
  echo 'uploaded server archive hash mismatch' >&2; exit 1;
}
[[ "$(sha256sum "$controls_archive" | cut -d' ' -f1)" == "$controls_hash" ]] || {
  echo 'uploaded platform-controls archive hash mismatch' >&2; exit 1;
}

release_parent="$state_root/releases/creatoros-beta1"
release_root="$release_parent/$release_hash"
mkdir -p "$release_parent"
if [[ ! -d "$release_root" ]]; then
  incoming="$(mktemp -d "$release_parent/.incoming-$release_hash-XXXXXX")"
  if ! tar -xzf "$archive" -C "$incoming"; then
    rm -rf -- "$incoming"
    echo 'server archive extraction failed' >&2
    exit 1
  fi
  if [[ ! -d "$incoming/server" ]]; then
    rm -rf -- "$incoming"
    echo 'archive has no server root' >&2
    exit 1
  fi
  cp "$deployment_manifest" "$incoming/deployment-manifest.json"
  mv "$incoming" "$release_root"
fi

while IFS=$'\t' read -r expected expected_bytes relative; do
  [[ "$relative" =~ ^[A-Za-z0-9._/-]+$ && "$relative" != /* && "$relative" != *..* ]] || {
    echo "unsafe server artifact path: $relative" >&2; exit 1;
  }
  candidate="$release_root/server/$relative"
  [[ -f "$candidate" ]] || { echo "server artifact missing: $relative" >&2; exit 1; }
  [[ "$(sha256sum "$candidate" | cut -d' ' -f1)" == "$expected" ]] || {
    echo "server artifact hash mismatch: $relative" >&2; exit 1;
  }
  [[ "$(stat -c '%s' "$candidate")" == "$expected_bytes" ]] || {
    echo "server artifact size mismatch: $relative" >&2; exit 1;
  }
done < <(jq -r '.verification.server_files[] | [.sha256,.bytes,.path] | @tsv' "$deployment_manifest")

controls_parent="$state_root/releases/creatoros-beta1-controls"
controls_root="$controls_parent/$controls_hash"
mkdir -p "$controls_parent"
if [[ ! -d "$controls_root" ]]; then
  controls_incoming="$(mktemp -d "$controls_parent/.incoming-$controls_hash-XXXXXX")"
  if ! tar -xzf "$controls_archive" -C "$controls_incoming"; then
    rm -rf -- "$controls_incoming"
    echo 'platform-controls archive extraction failed' >&2
    exit 1
  fi
  if [[ ! -d "$controls_incoming/controls" ]]; then
    rm -rf -- "$controls_incoming"
    echo 'platform-controls archive has no controls root' >&2
    exit 1
  fi
  mv "$controls_incoming" "$controls_root"
fi

control_field_for() {
  local relative="$1"
  local field="$2"
  jq -r --arg path "$relative" --arg field "$field" \
    '.control_files[] | select(.path == $path) | .[$field]' "$deployment_manifest"
}
verify_control() {
  local relative="$1"
  local target="$2"
  local mode="$3"
  local source="$controls_root/$relative"
  local expected_hash expected_bytes
  expected_hash="$(control_field_for "$relative" sha256)"
  expected_bytes="$(control_field_for "$relative" bytes)"
  [[ "$(control_field_for "$relative" target)" == "$target" &&
     "$(control_field_for "$relative" mode)" == "$mode" &&
     "$expected_hash" =~ ^[0-9a-f]{64}$ && "$expected_bytes" =~ ^[0-9]+$ ]] || {
    echo "platform-control declaration drifted: $relative" >&2; exit 1;
  }
  [[ -f "$source" && "$(sha256sum "$source" | cut -d' ' -f1)" == "$expected_hash" &&
     "$(stat -c '%s' "$source")" == "$expected_bytes" ]] || {
    echo "platform-control bytes drifted: $relative" >&2; exit 1;
  }
}
verify_control controls/docker-compose.yml "$compose_target" 0644
verify_control controls/comfy-lumberjacks-p7.service "$unit_target" 0644
verify_control controls/scripts/stop-p7-stack.sh "$stop_target" 0755
actual_controls="$(find "$controls_root/controls" -type f -printf '%P\n' | LC_ALL=C sort)"
expected_controls=$'comfy-lumberjacks-p7.service\ndocker-compose.yml\nscripts/stop-p7-stack.sh'
[[ "$actual_controls" == "$expected_controls" ]] || {
  echo 'platform-controls archive file set drifted' >&2; exit 1;
}

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
backup_root="$state_root/backups/creatoros-beta1/$stamp-$release_hash"
mkdir -p "$backup_root"
backup_if_present() {
  local target="$1"
  local label="$2"
  mkdir -p "$backup_root/$(dirname "$label")"
  if [[ -f "$target" ]]; then
    cp -a "$target" "$backup_root/$label"
    printf 'present\n' > "$backup_root/$label.state"
  else
    printf 'absent\n' > "$backup_root/$label.state"
  fi
}
restore_backup() {
  local target="$1"
  local label="$2"
  local state_file="$backup_root/$label.state"
  [[ -f "$state_file" ]] || return 0
  if [[ "$(cat "$state_file")" == present ]]; then
    local temporary="$target.creatoros-restore-${release_hash:0:12}"
    mkdir -p "$(dirname "$target")"
    cp -a "$backup_root/$label" "$temporary"
    mv -f "$temporary" "$target"
  else
    rm -f -- "$target"
  fi
}
install_atomic() {
  local source="$1"
  local target="$2"
  local expected="$3"
  local owner="${4:-1000:1000}"
  local mode="${5:-0644}"
  local temporary="$target.creatoros-new-${release_hash:0:12}"
  [[ -f "$source" ]] || { echo "install source missing: $source" >&2; false; }
  mkdir -p "$(dirname "$target")"
  [[ ! -e "$temporary" ]] || { echo "stale install temporary exists: $temporary" >&2; false; }
  cp "$source" "$temporary"
  chown "$owner" "$temporary"
  chmod "$mode" "$temporary"
  [[ "$(sha256sum "$temporary" | cut -d' ' -f1)" == "$expected" ]] || {
    echo "staged install hash mismatch: $target" >&2; false;
  }
  mv -f "$temporary" "$target"
}
manifest_hash_for() {
  local relative="$1"
  jq -r --arg path "$relative" '.verification.server_files[] | select(.path == $path) | .sha256' "$deployment_manifest"
}

transaction_started=false
transaction_committed=false
service_was_active=false
service_stopped_cleanly=false
receipt=''
rollback_on_error() {
  local failure=$?
  trap - ERR
  set +e
  if [[ "$transaction_started" == true && "$transaction_committed" != true ]]; then
    echo 'CreatorOS activation failed; attempting bounded rollback.' >&2
    systemctl stop "$service_name" >/dev/null 2>&1 || true
    local active_container
    active_container="$(cd "$compose_root" 2>/dev/null && docker compose --env-file "$environment_file" ps -q valheim-server 2>/dev/null || true)"
    if [[ -n "$active_container" && "$(docker inspect -f '{{.State.Running}}' "$active_container" 2>/dev/null || true)" == true ]]; then
      echo 'Rollback blocked: Valheim is still running; installed bytes were left intact.' >&2
    else
      world_root="$state_root/valheim/config/worlds_local"
      bepinex_root="$state_root/valheim/config/bepinex"
      creator_root="$state_root/valheim/config/creatoros-beta1"
      restore_backup "$world_root/CreatorOSBeta1.db" 'worlds_local/CreatorOSBeta1.db'
      restore_backup "$world_root/CreatorOSBeta1.fwl" 'worlds_local/CreatorOSBeta1.fwl'
      restore_backup "$bepinex_root/plugins/ComfyNetworkSense.dll" 'bepinex/plugins/ComfyNetworkSense.dll'
      restore_backup "$bepinex_root/djcdevelopment.valheim.comfynetworksense.cfg" 'bepinex/djcdevelopment.valheim.comfynetworksense.cfg'
      restore_backup "$bepinex_root/comfy-network-sense/quest-view.json" 'bepinex/comfy-network-sense/quest-view.json'
      restore_backup "$creator_root/venue.json" 'creatoros-beta1/venue.json'
      restore_backup "$creator_root/campaign.json" 'creatoros-beta1/campaign.json'
      restore_backup "$creator_root/platform-manifest.json" 'creatoros-beta1/platform-manifest.json'
      restore_backup "$environment_file" environment
      restore_backup "$compose_target" controls/docker-compose.yml
      restore_backup "$unit_target" controls/comfy-lumberjacks-p7.service
      restore_backup "$stop_target" controls/scripts/stop-p7-stack.sh
      systemctl daemon-reload
      if [[ "$service_was_active" == true && "$service_stopped_cleanly" == true ]]; then
        systemctl reset-failed "$service_name" >/dev/null 2>&1 || true
        systemctl start "$service_name" || echo 'Rollback restored bytes but could not restart the prior stack.' >&2
      fi
      mkdir -p "$state_root/evidence/creatoros-beta1"
      jq -n --arg schema 'comfy-p7-creatoros-rollback/v1' --arg status rolled_back \
        --arg completed_utc "$(date -u +%Y-%m-%dT%H:%M:%SZ)" --arg release_manifest_sha256 "$release_hash" \
        --arg backup_root "$backup_root" --argjson failed_exit "$failure" \
        '{schema:$schema,status:$status,completed_utc:$completed_utc,
          release_manifest_sha256:$release_manifest_sha256,backup_root:$backup_root,failed_exit:$failed_exit}' \
        > "$state_root/evidence/creatoros-beta1/rollback-$stamp-$release_hash.json"
      echo "CreatorOS activation rolled back to $backup_root." >&2
    fi
  fi
  exit "$failure"
}
trap rollback_on_error ERR

# Back up the live controls and secrets before the first mutation. World bytes are copied only
# after the bounded save gate has stopped Valheim, so the rollback pair is internally consistent.
backup_if_present "$environment_file" environment
backup_if_present "$compose_target" controls/docker-compose.yml
backup_if_present "$unit_target" controls/comfy-lumberjacks-p7.service
backup_if_present "$stop_target" controls/scripts/stop-p7-stack.sh
if systemctl is-active --quiet "$service_name"; then
  service_was_active=true
fi
transaction_started=true

install_atomic "$controls_root/controls/docker-compose.yml" "$compose_target" \
  "$(control_field_for controls/docker-compose.yml sha256)" root:root 0644
install_atomic "$controls_root/controls/comfy-lumberjacks-p7.service" "$unit_target" \
  "$(control_field_for controls/comfy-lumberjacks-p7.service sha256)" root:root 0644
install_atomic "$controls_root/controls/scripts/stop-p7-stack.sh" "$stop_target" \
  "$(control_field_for controls/scripts/stop-p7-stack.sh sha256)" root:root 0755
systemd-analyze verify "$unit_target"
systemctl daemon-reload

if [[ "$service_was_active" == true ]]; then
  systemctl stop "$service_name"
fi
service_stopped_cleanly=true
container="$(cd "$compose_root" && docker compose --env-file "$environment_file" ps -q valheim-server 2>/dev/null || true)"
if [[ -n "$container" && "$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null || true)" == true ]]; then
  echo 'refusing to install while Valheim runs outside the stopped P7 unit' >&2
  false
fi

world_root="$state_root/valheim/config/worlds_local"
bepinex_root="$state_root/valheim/config/bepinex"
creator_root="$state_root/valheim/config/creatoros-beta1"
for name in CreatorOSBeta1.db CreatorOSBeta1.fwl; do
  backup_if_present "$world_root/$name" "worlds_local/$name"
  install_atomic "$release_root/server/worlds_local/$name" "$world_root/$name" "$(manifest_hash_for "worlds_local/$name")"
done
backup_if_present "$bepinex_root/plugins/ComfyNetworkSense.dll" 'bepinex/plugins/ComfyNetworkSense.dll'
install_atomic "$release_root/server/BepInEx/plugins/ComfyNetworkSense.dll" \
  "$bepinex_root/plugins/ComfyNetworkSense.dll" "$(manifest_hash_for 'BepInEx/plugins/ComfyNetworkSense.dll')"
backup_if_present "$bepinex_root/djcdevelopment.valheim.comfynetworksense.cfg" 'bepinex/djcdevelopment.valheim.comfynetworksense.cfg'
install_atomic "$release_root/server/BepInEx/config/djcdevelopment.valheim.comfynetworksense.cfg" \
  "$bepinex_root/djcdevelopment.valheim.comfynetworksense.cfg" \
  "$(manifest_hash_for 'BepInEx/config/djcdevelopment.valheim.comfynetworksense.cfg')"
backup_if_present "$bepinex_root/comfy-network-sense/quest-view.json" 'bepinex/comfy-network-sense/quest-view.json'
install_atomic "$release_root/server/BepInEx/config/comfy-network-sense/quest-view.json" \
  "$bepinex_root/comfy-network-sense/quest-view.json" \
  "$(manifest_hash_for 'BepInEx/config/comfy-network-sense/quest-view.json')"
for name in venue.json campaign.json; do
  backup_if_present "$creator_root/$name" "creatoros-beta1/$name"
  install_atomic "$release_root/server/creatoros/$name" "$creator_root/$name" "$(manifest_hash_for "creatoros/$name")"
done
backup_if_present "$creator_root/platform-manifest.json" 'creatoros-beta1/platform-manifest.json'
install_atomic "$release_root/server/platform-manifest.json" "$creator_root/platform-manifest.json" \
  "$(manifest_hash_for 'platform-manifest.json')"

set_environment() {
  local key="$1"
  local value="$2"
  local temporary
  temporary="$(mktemp /etc/comfy-p7/environment.creatoros.XXXXXX)"
  awk -v key="$key" 'index($0, key "=") != 1 { print }' "$environment_file" > "$temporary"
  printf '%s=%s\n' "$key" "$value" >> "$temporary"
  chown root:root "$temporary"
  chmod 0600 "$temporary"
  mv "$temporary" "$environment_file"
}
set_environment VALHEIM_SERVER_NAME '"CreatorOS Beta 1"'
set_environment VALHEIM_WORLD_NAME CreatorOSBeta1
set_environment COMFY_LUMBERJACKS_CUTOVER_MODE native
set_environment LUMBERJACKS_AUTHORITATIVE_WINDOW_ID creatoros-beta1
set_environment LUMBERJACKS_STRICT_ROSTER_ENABLED true
set_environment LUMBERJACKS_ALPHA_SEAT_GATE disabled
set_environment VALHEIM_HANDSHAKE_SEAT_CAPACITY 0
set_environment P7_VALHEIM_STOP_TIMEOUT_SECONDS 70

mkdir -p "$state_root/evidence/creatoros-beta1"
receipt="$state_root/evidence/creatoros-beta1/install-$stamp-$release_hash.json"
jq -n \
  --arg schema 'comfy-p7-creatoros-install/v1' \
  --arg status installed \
  --arg completed_utc "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  --arg release_manifest_sha256 "$release_hash" \
  --arg controls_archive_sha256 "$controls_hash" \
  --arg world_uid "$world_uid" \
  --arg world_pair_hash "$world_pair_hash" \
  --arg pack_content_hash "$pack_hash" \
  --arg backup_root "$backup_root" \
  '{schema:$schema,status:$status,completed_utc:$completed_utc,release_manifest_sha256:$release_manifest_sha256,
    controls_archive_sha256:$controls_archive_sha256,world_name:"CreatorOSBeta1",world_uid:$world_uid,
    world_pair_hash:$world_pair_hash,pack_content_hash:$pack_content_hash,server_mode:"native-valheim",
    strict_roster:true,handshake_fail_closed:true,platform_controls_verified:true,backup_root:$backup_root}' \
  > "$receipt.tmp"
mv "$receipt.tmp" "$receipt"

if [[ "$activate" == true ]]; then
  systemctl start "$service_name"
  systemctl is-active --quiet "$service_name"
  container="$(cd "$compose_root" && docker compose --env-file "$environment_file" ps -q valheim-server)"
  [[ -n "$container" ]] || { echo 'Valheim container did not start' >&2; false; }
  inspect_env="$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$container")"
  grep -Fxq 'WORLD_NAME=CreatorOSBeta1' <<<"$inspect_env" || {
    echo 'running container world identity drifted' >&2; false;
  }
  expected_dll="$(manifest_hash_for 'BepInEx/plugins/ComfyNetworkSense.dll')"
  runtime_dll="$(docker exec "$container" sha256sum /opt/valheim/bepinex/BepInEx/plugins/ComfyNetworkSense.dll | cut -d' ' -f1)"
  [[ "$runtime_dll" == "$expected_dll" ]] || { echo 'running NetworkSense hash drifted' >&2; false; }
  handshake="$(curl -fsS http://127.0.0.1:4000/valheim/handshake/status/creatoros-beta1)"
  jq -e '.window_id == "creatoros-beta1" and .seat_capacity == 0 and .strict_roster_enabled == true' <<<"$handshake" >/dev/null || {
    echo 'Gateway did not arm the durable strict CreatorOS roster window' >&2; false;
  }
  curl -fsS https://comfy-p7.duckdns.org/health >/dev/null
  jq --arg status active --arg hash "$runtime_dll" \
    '.status=$status | .activated_utc=(now | todateiso8601) | .tls_health=true | .runtime_networksense_sha256=$hash' \
    "$receipt" > "$receipt.active"
  mv "$receipt.active" "$receipt"
fi

transaction_committed=true
trap - ERR
echo "$receipt"
