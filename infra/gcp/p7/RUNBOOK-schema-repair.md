# P7 Postgres schema repair — the Gateway database has no tables

**Status:** STAGED, not executed. Recorded 2026-07-30.
**Blocking condition:** the P7 VM is STOPPED and stays stopped until the operator decides to
start it. Nothing in this runbook has been run against the VM. Everything in the *Diagnosis*
section below was established from the repository alone; the *Confirm on the VM* step exists
because one link in the causal chain cannot be read from the repo.

## Symptom

`comfy-lumberjacks-p7-postgres-1` logs, Postgres 16.14:

```text
2026-07-30 00:55:08 UTC [38]   ERROR: relation "regions" does not exist at character 159
  STATEMENT: SELECT r.id, r.active, r.bounds_max_x, ... FROM regions AS r
2026-07-30 01:10:29 UTC [1357] ERROR: relation "events" does not exist at character 13
  STATEMENT: INSERT INTO events (actor_id, created_at, event_id, event_type, guild_id,
             occurred_at, payload, region_id, schema_version, source_service, world_id) ...
```

The database is not partially migrated. It is **empty** — the `game` database has no
application tables at all.

Attribution of the two statements, since it changes where you look:

| Statement | Emitting service | Source |
|---|---|---|
| `SELECT ... FROM regions` | **gateway** (hosts the simulation) | [`RegionLoader.cs:24`](../../../Lumberjacks/src/Game.Simulation/Startup/RegionLoader.cs#L24), called from [`Program.cs`](../../../Lumberjacks/src/Game.Gateway/Program.cs) at startup |
| `INSERT INTO events` | **eventlog**, not the gateway | [`EventEndpoints.cs:34`](../../../Lumberjacks/src/Game.EventLog/Endpoints/EventEndpoints.cs#L34) |

The Gateway forwards gameplay events to `http://eventlog:4002` (`ServiceUrls__EventLog`), so
the INSERT failure surfaces in the **eventlog** container's logs, not the Gateway's.

## Impact

- `/community` **Gameplay Feed** and **Quests Completed** can never populate. Every event
  POST reaches eventlog and dies at `SaveChangesAsync`. A modded player in-world changes
  nothing.
- During the 2026-07-29 demo this was attributed to having 0 players connected. That was
  wrong, or at best incomplete: with 0 players there is nothing to show *and* nothing could
  have been shown if there had been players.
- `RegionProfileLoader`, `NaturalResourceLoader` and `StructureLoader` never ran at all. They
  sat behind `RegionLoader` in a single `try` block, so the first exception skipped the rest.

### `/api/v0/telemetry/regions` returning "Spawn Island" is a false green

Confirmed, and it answers the open question: that endpoint never touches the database.
[`TelemetryV0Endpoints.cs:41`](../../../Lumberjacks/src/Game.Simulation/Endpoints/TelemetryV0Endpoints.cs#L41)
reads the in-memory `WorldState`, and the `WorldState` constructor
([`WorldState.cs:27-40`](../../../Lumberjacks/src/Game.Simulation/World/WorldState.cs#L27))
hard-seeds `region-spawn` / `"Spawn Island"`. It returns that region whether or not Postgres
has a single table. **Do not use it as a database health signal.** The database-backed regions
would have been *added* to that seed by `RegionLoader`; a schema-less DB is indistinguishable
from an empty `regions` table at that endpoint.

## Diagnosis

### Where the schema is defined

Two places, and until now neither was reliably applied:

1. [`Lumberjacks/infra/docker/init.sql`](../../../Lumberjacks/infra/docker/init.sql) — the only
   thing that has ever created tables anywhere. Mounted into
   `/docker-entrypoint-initdb.d/init.sql`.
2. `Lumberjacks/src/Game.Persistence/Migrations/20260328154322_NatureTwoPointZero.cs` — the
   single EF Core migration. **Never applied to any environment.** `Migrate()`,
   `MigrateAsync()` and `EnsureCreated()` appear nowhere in the repo, and no
   `__EFMigrationsHistory` table is referenced anywhere. It creates `natural_resources` and
   `region_profiles`, which the pre-repair `init.sql` did not contain — so even a *correctly*
   initialized P7 volume was missing two tables.

### Why it did not run

Four findings, all verifiable in the repo:

1. **`docker-entrypoint-initdb.d` is a first-init-only hook.** Postgres runs it exactly once,
   when `PGDATA` is empty, and otherwise logs *"PostgreSQL Database directory appears to
   contain a database; Skipping initialization"* — which is what the P7 log shows. The P7 data
   directory is `/mnt/comfy-p7/lumberjacks/postgres`, a bind mount on the **state disk**. It
   survives every `compose down`, every image re-pin, every promotion drill and every VM
   stop/start. So the schema window opens **once per disk**, and there was no reconcile path
   if it was missed.

2. **The window that mattered was 2026-07-24, and the mount path was already stale by then.**
   [`RECONCILE-GAP.md`](RECONCILE-GAP.md) §4 records that the state disk was **replaced** on
   2026-07-24 (the old 150 GB `comfy-lumberjacks-p7-state` was deleted; the current 32 GB
   `-state-v2` is unmanaged). So the cluster now on disk is a *fresh* cluster initialized on or
   after 2026-07-24 — its recorded clean shutdown is 2026-07-26 — meaning it passed through its
   one and only initdb window four days ago and came out with no schema.

   Meanwhile commit `f6b2793` (**2026-07-20**, "Land Lumberjacks whole under Lumberjacks/")
   moved `init.sql` from `<root>/infra/docker/init.sql` to
   `<root>/Lumberjacks/infra/docker/init.sql`. The compose file still mounted the
   pre-unification path, `${LUMBERJACKS_ROOT:-/opt/lumberjacks}/infra/docker/init.sql` — which
   names no file in a baseline checkout. The repo-layout change landed **four days before** the
   only boot that could have applied the schema.

3. **A missing bind source fails silently in exactly the wrong way.** Docker materializes a
   missing bind-mount source as an empty **directory**. On an already-initialized volume the
   initdb loop never runs, so an empty directory mounted at
   `/docker-entrypoint-initdb.d/init.sql` produces no error, no warning, and no schema. There
   is nothing in a healthy-looking `docker compose ps` that distinguishes it.

4. **No deploy path ever applied the schema explicitly.** `build-release-bundle.ps1` stages
   only the manifest, the mod DLL, `Dockerfile`, `Directory.*.props` and the mod `.csproj` — it
   has never shipped `init.sql`. And before this change, no document under `infra/gcp/p7/`
   mentioned `psql`, `init.sql` or the word *schema* at all. There was no step to skip, which is
   why nobody noticed skipping it.

5. **The application swallowed the evidence.** `Program.cs` wrapped all four world-state
   loaders in one `try` that logged a single **Warning** and continued "with in-memory defaults
   only". A schema-less database therefore produced one quiet line at startup and an
   apparently-healthy Gateway.

### What is *not* established from the repo

Which of these was true at the 2026-07-24 boot: `LUMBERJACKS_ROOT` unset (defaulting to
`/opt/lumberjacks`, which `bootstrap.sh.tftpl:49` creates as an empty directory), or pointing at
`/opt/lumberjacks-<sha>`, or at the `/opt/comfy` baseline checkout. All three resolve to a
non-existent `infra/docker/init.sql` in the post-unification layout, so the outcome is the same
— but the specific value is only readable on the VM. Step 1 below settles it.

## Fix (landed in this repo, not yet deployed)

**Tier 1 — no image build, no promotion.** Ships by git bundle + `docker compose up -d`.

- `Lumberjacks/infra/docker/init.sql` rewritten to be **idempotent** (`IF NOT EXISTS`
  throughout, `pg_constraint`-guarded `DO` blocks for the two foreign keys) and **complete**
  (all 13 `GameDbContext` tables, including the previously-missing `natural_resources` and
  `region_profiles`).
- `docker-compose.yml` gained a one-shot **`dbschema`** service that runs
  `psql --set=ON_ERROR_STOP=1 --file=/schema.sql` on **every** stack start. `gateway`,
  `eventlog`, `progression` and `operatorapi` now additionally depend on it with
  `condition: service_completed_successfully`, so a schema failure is a loud startup failure
  instead of a warning and an empty dashboard panel. It reuses the already-pinned
  `postgres:16-alpine` image, so there is no new pull and no build.
- Both schema mounts are now **relative to the compose file** (`../../../Lumberjacks/...`)
  rather than to `LUMBERJACKS_ROOT`. `LUMBERJACKS_ROOT` is no longer consumed by
  `docker-compose.yml` at all; it remains in `environment.example` and `README.md`, which
  should be revisited separately.

**Tier 2 — requires a Gateway image build and promotion** (`New-GatewayReleaseCut` +
`Promote-GatewayImage`), so it can ship later without delaying the repair:

- `Game.Gateway/Program.cs` now isolates each of the four startup loaders and logs failures at
  **Error** naming the loader, so one missing relation no longer masks the three behind it.

### Verified locally (no VM involved)

- `docker compose config` resolves both mounts to the real
  `Lumberjacks/infra/docker/init.sql`, and all four services carry the `dbschema` gate.
- The schema applied to a throwaway `postgres:16-alpine` **three times consecutively**, exit 0
  each time; 13 tables present.
- Both statements from the P7 log were replayed against it: the `regions` SELECT returned
  `(0 rows)` instead of erroring, and the `events` INSERT returned `INSERT 0 1`.
- `Game.Gateway` builds Release with 0 warnings / 0 errors; `Game.Gateway.Tests` 207/207 pass.

## Repair procedure — requires operator approval to start the VM

Costs money and the standing policy is that P7 stays stopped. Steps 1–2 are the forensic
capture; if you only want the repair, they are still worth the 60 seconds because they convert
an inference into a receipt.

### 0. Ship the code to the VM

Deliver the current baseline to `/opt/comfy` by the usual git-bundle path (no credentials on
the box). Confirm the file the compose file now points at actually exists:

```bash
test -f /opt/comfy/Lumberjacks/infra/docker/init.sql && echo SCHEMA_PRESENT
```

### 1. Capture the receipt for the root cause (before changing anything)

```bash
cd /opt/comfy/infra/gcp/p7
sudo grep -n LUMBERJACKS_ROOT /etc/comfy-p7/environment
sudo docker compose --env-file /etc/comfy-p7/environment config | grep -A2 'initdb'
```

Then look at what Docker actually mounted for the old path. **A directory here is the proof:**

```bash
ls -la "$(sudo grep -oP '(?<=^LUMBERJACKS_ROOT=).*' /etc/comfy-p7/environment)/infra/docker/" 2>&1 | head
ls -ld /opt/lumberjacks/infra/docker/init.sql 2>&1
```

### 2. Confirm the database really is empty

```bash
sudo docker compose exec -T postgres \
  psql -U game -d game -c "\dt"
```

Expect `Did not find any relations.` If instead you find a *partial* schema, stop and read the
table list before applying anything — this runbook assumes empty-or-consistent, and the
idempotent script will not repair a table that exists with the wrong columns.

### 3. Apply the schema

The stack does this for you now — starting it is the fix:

```bash
cd /opt/comfy/infra/gcp/p7
sudo systemctl restart comfy-lumberjacks-p7
```

Or, without restarting the world, apply just the schema against the running database:

```bash
cd /opt/comfy/infra/gcp/p7
sudo docker compose --env-file /etc/comfy-p7/environment up dbschema
```

`dbschema` must exit 0. If it does not, the .NET services will refuse to start rather than run
schema-less — that is the intended behaviour.

### 4. Verify — the schema exists

```bash
sudo docker compose exec -T postgres psql -U game -d game -c "\dt"
```

Expect 13 tables: `challenge_progress`, `challenges`, `container_items`, `containers`,
`events`, `guild_progress`, `natural_resources`, `player_inventories`, `player_progress`,
`region_profiles`, `regions`, `structures`, `world_items`.

### 5. Verify — the two failing statements now succeed

This is the real acceptance check: it exercises the exact SQL from the incident, and needs no
player in-world.

```bash
sudo docker compose exec -T postgres psql -U game -d game -v ON_ERROR_STOP=1 <<'SQL'
SELECT r.id, r.active, r.bounds_max_x FROM regions AS r;
INSERT INTO events (actor_id, created_at, event_id, event_type, guild_id,
                    occurred_at, payload, region_id, schema_version, source_service, world_id)
VALUES ('schema-repair-probe', now(), gen_random_uuid(), 'diagnostic.probe', NULL,
        now(), '{"source":"RUNBOOK-schema-repair"}', 'region-spawn', 1, 'runbook', 'ComfyEra16')
RETURNING id, event_type;
SQL
```

Both must succeed. Then remove the probe row so it never reaches a public feed:

```bash
sudo docker compose exec -T postgres \
  psql -U game -d game -c "DELETE FROM events WHERE actor_id = 'schema-repair-probe';"
```

### 6. Verify — the Gateway's own startup path is clean

```bash
sudo docker compose logs gateway  | grep -iE 'region|loader|persisted' | tail -20
sudo docker compose logs eventlog | grep -iE 'error|does not exist'     | tail -20
```

Expect `Loaded 0 regions from database (+ default spawn)` from `RegionLoader` and **no**
`relation ... does not exist`. `0 regions` is correct and expected: the `regions` table is
genuinely empty, and the in-memory `region-spawn` seed is what serves
`/api/v0/telemetry/regions`. If the Tier 2 image is deployed, all four loaders report
individually.

### 7. Verify — end to end, with a player

Only this step proves the dashboard. Everything above proves the database. Per the standing
networking-lane hold there are no human Steam tests scheduled, so treat this as gated on the
operator, not as part of the repair:

- A modded client generates a gameplay event → `/community` **Gameplay Feed** shows a row.
- `SELECT count(*) FROM events;` climbs.

## Follow-ups this exposed (not fixed here)

1. **EF migrations are dead weight in their current form.** One migration, never applied, and
   it is not a baseline — it assumes the core tables already exist. If migrations are to become
   the real path, that needs a generated baseline migration plus a deliberate fake-apply
   against databases that already have the tables. Until then `init.sql` is the mechanism and
   the EF model is only the design authority, and the two can drift again exactly as they did
   with `natural_resources` / `region_profiles`.
2. ~~**`LUMBERJACKS_ROOT` is still documented as required** in `README.md` and
   `environment.example` while nothing consumes it.~~ **CLOSED 2026-07-30:** retired from both,
   with a note against reintroducing it. Remaining mentions in this repo are historical prose.
   **Still to do on the box:** remove the line from `/etc/comfy-p7/environment` — but read it
   first, it is the forensic evidence in step 1.
3. **`bootstrap.sh.tftpl:49` creates `/opt/lumberjacks` empty**, which is what made the old
   compose default resolve to a plausible-looking but wrong path. Left alone deliberately: that
   file is `metadata_startup_script`, and per [`RECONCILE-GAP.md`](RECONCILE-GAP.md) §1 it is
   already drifted and force-replaces the VM on apply. Fold it into the reconcile effort, not
   into a docs cleanup.
4. **No boot-time schema assertion outside compose.** The `dbschema` gate covers
   `compose up`; it does not cover someone starting a single service by hand.
5. **The state disk is still unmanaged in Terraform** — [`RECONCILE-GAP.md`](RECONCILE-GAP.md)
   §4. The disk replacement that triggered this incident is itself the open drift item. A
   future disk replacement re-opens the same initdb window; the `dbschema` service is what
   makes that survivable.
