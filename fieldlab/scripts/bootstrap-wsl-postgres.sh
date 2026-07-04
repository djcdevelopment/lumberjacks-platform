#!/usr/bin/env bash
set -euo pipefail

PORT="5433"
DATABASE="game"
DB_USER="game"
DB_PASSWORD="game"
INIT_SQL="/mnt/c/work/Lumberjacks/infra/docker/init.sql"
LISTEN_ADDRESSES="*"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --port)
      PORT="$2"
      shift 2
      ;;
    --database)
      DATABASE="$2"
      shift 2
      ;;
    --user)
      DB_USER="$2"
      shift 2
      ;;
    --password)
      DB_PASSWORD="$2"
      shift 2
      ;;
    --init-sql)
      INIT_SQL="$2"
      shift 2
      ;;
    --listen-addresses)
      LISTEN_ADDRESSES="$2"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

if ! command -v sudo >/dev/null 2>&1; then
  echo "sudo is required inside WSL." >&2
  exit 1
fi

echo "Installing Postgres packages if needed..."
sudo apt-get update
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y postgresql postgresql-client

echo "Starting Postgres service..."
sudo systemctl start postgresql 2>/dev/null || sudo service postgresql start

CONFIG_FILE="$(sudo -u postgres psql -tAc "SHOW config_file" | xargs)"
if [[ -z "$CONFIG_FILE" || ! -f "$CONFIG_FILE" ]]; then
  echo "Could not locate postgresql.conf." >&2
  exit 1
fi

HBA_FILE="$(sudo -u postgres psql -tAc "SHOW hba_file" | xargs)"
if [[ -z "$HBA_FILE" || ! -f "$HBA_FILE" ]]; then
  echo "Could not locate pg_hba.conf." >&2
  exit 1
fi

echo "Configuring Postgres to listen on ${LISTEN_ADDRESSES}:${PORT}..."
sudo sed -i -E "s/^#?[[:space:]]*port[[:space:]]*=.*/port = ${PORT}/" "$CONFIG_FILE"
if grep -q -E "^#?[[:space:]]*listen_addresses[[:space:]]*=" "$CONFIG_FILE"; then
  sudo sed -i -E "s/^#?[[:space:]]*listen_addresses[[:space:]]*=.*/listen_addresses = '${LISTEN_ADDRESSES}'/" "$CONFIG_FILE"
else
  echo "listen_addresses = '${LISTEN_ADDRESSES}'" | sudo tee -a "$CONFIG_FILE" >/dev/null
fi

HBA_RULE="host all all samenet scram-sha-256"
if ! sudo grep -Fxq "$HBA_RULE" "$HBA_FILE"; then
  echo "$HBA_RULE" | sudo tee -a "$HBA_FILE" >/dev/null
fi

sudo systemctl restart postgresql 2>/dev/null || sudo service postgresql restart

echo "Waiting for Postgres on 127.0.0.1:${PORT}..."
for _ in $(seq 1 30); do
  if pg_isready -h 127.0.0.1 -p "$PORT" >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

if ! pg_isready -h 127.0.0.1 -p "$PORT" >/dev/null 2>&1; then
  echo "Postgres did not become ready on 127.0.0.1:${PORT}." >&2
  exit 1
fi

echo "Creating/updating role and database..."
sudo -u postgres psql -p "$PORT" -v ON_ERROR_STOP=1 <<SQL
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '${DB_USER}') THEN
    CREATE ROLE "${DB_USER}" LOGIN PASSWORD '${DB_PASSWORD}';
  ELSE
    ALTER ROLE "${DB_USER}" WITH LOGIN PASSWORD '${DB_PASSWORD}';
  END IF;
END
\$\$;
SELECT 'CREATE DATABASE "${DATABASE}" OWNER "${DB_USER}"'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = '${DATABASE}')\gexec
GRANT ALL PRIVILEGES ON DATABASE "${DATABASE}" TO "${DB_USER}";
SQL

if [[ -f "$INIT_SQL" ]]; then
  EVENTS_TABLE="$(sudo -u postgres psql -p "$PORT" -d "$DATABASE" -tAc "SELECT to_regclass('public.events')" | xargs)"
  if [[ "$EVENTS_TABLE" != "events" && "$EVENTS_TABLE" != "public.events" ]]; then
    echo "Loading Lumberjacks schema from ${INIT_SQL}..."
    TEMP_SQL="$(mktemp)"
    grep -v -E '^\\(un)?restrict\b' "$INIT_SQL" > "$TEMP_SQL"
    chmod 644 "$TEMP_SQL"
    sudo -u postgres psql -p "$PORT" -d "$DATABASE" -v ON_ERROR_STOP=1 -f "$TEMP_SQL"
    rm -f "$TEMP_SQL"
  else
    echo "Lumberjacks schema already appears to be loaded."
  fi

  sudo -u postgres psql -p "$PORT" -d "$DATABASE" -v ON_ERROR_STOP=1 <<SQL
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO "${DB_USER}";
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO "${DB_USER}";
SQL
else
  echo "Init SQL not found at ${INIT_SQL}; database exists but schema was not loaded."
fi

echo "WSL Postgres bootstrap complete."
