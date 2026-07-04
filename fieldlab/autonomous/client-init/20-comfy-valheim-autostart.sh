#!/usr/bin/env bash
set -euo pipefail

log() {
  printf '[comfy-valheim-autostart] %s\n' "$*"
}

truthy() {
  case "${1:-}" in
    1|true|TRUE|yes|YES|on|ON) return 0 ;;
    *) return 1 ;;
  esac
}

if ! truthy "${COMFY_STEAM_AUTOSTART:-false}"; then
  log "COMFY_STEAM_AUTOSTART is disabled."
  exit 0
fi

run_autostart() {
install_dir="${COMFY_VALHEIM_INSTALL_DIR:-/mnt/games/GameLibrary/Steam/steamapps/common/Valheim}"
server="${COMFY_VALHEIM_SERVER:-valheim-server:2456}"
password="${COMFY_VALHEIM_PASSWORD:-comfytest}"
launch_args="${COMFY_VALHEIM_LAUNCH_ARGS:--console}"
deadline=$((SECONDS + 1800))

candidate_install_dirs="${install_dir} /mnt/games/GameLibrary/Steam/steamapps/common/Valheim /mnt/games/steamapps/common/Valheim"

find_install_dir() {
  for candidate in ${candidate_install_dirs}; do
    if [ -d "${candidate}" ]; then
      printf '%s\n' "${candidate}"
      return 0
    fi
  done
  return 1
}

log "waiting for Valheim install at one of: ${candidate_install_dirs}"
while ! resolved_install_dir="$(find_install_dir)"; do
  if [ "${SECONDS}" -ge "${deadline}" ]; then
    log "Valheim install not found before timeout; leaving Steam desktop running for manual install/login."
    exit 0
  fi
  sleep 10
done
install_dir="${resolved_install_dir}"
log "using Valheim install at ${install_dir}"

mkdir -p \
  "${install_dir}/BepInEx/plugins" \
  "${install_dir}/BepInEx/config" \
  "${install_dir}/BepInEx/config/comfy-network-sense"

if [ -f /mnt/comfy/plugins/ComfyNetworkSense.dll ]; then
  cp -f /mnt/comfy/plugins/ComfyNetworkSense.dll "${install_dir}/BepInEx/plugins/ComfyNetworkSense.dll"
  log "installed ComfyNetworkSense.dll"
else
  log "missing /mnt/comfy/plugins/ComfyNetworkSense.dll"
fi

if [ -f /mnt/comfy/config/djcdevelopment.valheim.comfynetworksense.cfg ]; then
  cp -f /mnt/comfy/config/djcdevelopment.valheim.comfynetworksense.cfg \
    "${install_dir}/BepInEx/config/djcdevelopment.valheim.comfynetworksense.cfg"
  log "installed ComfyNetworkSense config"
fi

if [ -f /mnt/comfy/comfy-network-sense/teleport-route.tsv ]; then
  cp -f /mnt/comfy/comfy-network-sense/teleport-route.tsv \
    "${install_dir}/BepInEx/config/comfy-network-sense/teleport-route.tsv"
  log "installed teleport route"
fi

if pgrep -f '[V]alheim' >/dev/null 2>&1; then
  log "Valheim is already running."
  exit 0
fi

steam_bin="$(command -v steam || command -v steam-runtime || true)"
if [ -z "${steam_bin}" ]; then
  log "Steam executable not found in container."
  exit 0
fi

log "launching Valheim toward ${server}"
nohup "${steam_bin}" -applaunch 892970 ${launch_args} +connect "${server}" +password "${password}" \
  >/tmp/comfy-valheim-autostart.log 2>&1 &
}

run_autostart >/tmp/comfy-valheim-autostart-watcher.log 2>&1 &
log "started background Valheim autostart watcher"
exit 0
