#!/usr/bin/env bash
set -euo pipefail

environment_file="/etc/comfy-p7/environment"
compose_root="/opt/comfy/infra/gcp/p7"

grep -q '^LUMBERJACKS_ADMIN_KEY=' "$environment_file" ||
  printf 'LUMBERJACKS_ADMIN_KEY=%s\n' "$(head -c 32 /dev/urandom | xxd -p -c 64)" >> "$environment_file"

sed -i \
  -e '/^LUMBERJACKS_PLAYER_GATEWAY_URL=/d' \
  -e '/^LUMBERJACKS_ENROLLMENT_PUBLIC_URL=/d' \
  -e '/^LUMBERJACKS_PLAYER_PORT=/d' \
  "$environment_file"

printf '%s\n' \
  'LUMBERJACKS_PLAYER_GATEWAY_URL=http://8.231.129.249:42317' \
  'LUMBERJACKS_ENROLLMENT_PUBLIC_URL=http://8.231.129.249:42317' \
  'LUMBERJACKS_PLAYER_PORT=42317' >> "$environment_file"

set -a
# shellcheck disable=SC1090
source "$environment_file"
set +a

cd "$compose_root"
docker compose config >/dev/null
docker compose build gateway
docker compose up -d --no-deps gateway
