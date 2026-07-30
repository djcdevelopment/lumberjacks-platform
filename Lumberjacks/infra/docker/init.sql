--
-- Lumberjacks game database schema — AUTHORITATIVE and IDEMPOTENT.
--
-- This file used to be a raw `pg_dump --schema-only` snapshot mounted only into
-- `/docker-entrypoint-initdb.d/`. That made it a FIRST-INIT-ONLY script: Postgres runs
-- `docker-entrypoint-initdb.d` exactly once, when PGDATA is empty, and silently skips it
-- forever after ("PostgreSQL Database directory appears to contain a database; Skipping
-- initialization"). On P7 the data directory is a persistent bind mount on the state disk,
-- so that single window was missed and the stack came up with NO tables at all — the
-- Gateway's region load and every eventlog INSERT failed on every boot, with nothing but a
-- warning in the log. See infra/gcp/p7/RUNBOOK-schema-repair.md.
--
-- Consequences for editing this file:
--   1. EVERY statement must be re-runnable. Use IF NOT EXISTS, or guard with a DO block
--      that checks pg_catalog. It is applied on every stack start, not just the first.
--   2. Do NOT regenerate it with `pg_dump --schema-only`. A dump is not idempotent and
--      would silently reintroduce the original defect. Hand-edit it instead, and keep it in
--      step with Game.Persistence/GameDbContext.cs (the EF model is the design authority;
--      this file is what actually reaches a database).
--   3. Anything added to the EF model belongs here too. `natural_resources` and
--      `region_profiles` were the standing example of that gap: they existed only in the
--      unapplied EF migration 20260328154322_NatureTwoPointZero, so even a correctly
--      initialized volume was missing them.
--

SET client_min_messages = warning;

-- ---------------------------------------------------------------- events (Game.EventLog)

CREATE TABLE IF NOT EXISTS public.events (
    id serial PRIMARY KEY,
    event_id uuid NOT NULL UNIQUE,
    event_type text NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    world_id text NOT NULL,
    region_id text,
    actor_id text,
    guild_id text,
    source_service text NOT NULL,
    schema_version integer NOT NULL,
    payload jsonb DEFAULT '{}'::jsonb NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_events_type ON public.events USING btree (event_type);
CREATE INDEX IF NOT EXISTS idx_events_actor ON public.events USING btree (actor_id);
CREATE INDEX IF NOT EXISTS idx_events_occurred ON public.events USING btree (occurred_at);

-- --------------------------------------------------------------- regions (Game.Simulation)

CREATE TABLE IF NOT EXISTS public.regions (
    id text PRIMARY KEY,
    name text NOT NULL,
    bounds_min_x double precision DEFAULT 0 NOT NULL,
    bounds_min_y double precision DEFAULT 0 NOT NULL,
    bounds_min_z double precision DEFAULT 0 NOT NULL,
    bounds_max_x double precision DEFAULT 0 NOT NULL,
    bounds_max_y double precision DEFAULT 0 NOT NULL,
    bounds_max_z double precision DEFAULT 0 NOT NULL,
    active boolean DEFAULT true NOT NULL,
    tick_rate double precision DEFAULT 20 NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);

-- ------------------------------------------------------------------------------ progression

CREATE TABLE IF NOT EXISTS public.player_progress (
    player_id text PRIMARY KEY,
    rank integer DEFAULT 0 NOT NULL,
    points integer DEFAULT 0 NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);

CREATE TABLE IF NOT EXISTS public.guild_progress (
    guild_id text PRIMARY KEY,
    points integer DEFAULT 0 NOT NULL,
    challenges_completed integer DEFAULT 0 NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);

CREATE TABLE IF NOT EXISTS public.challenges (
    id text PRIMARY KEY,
    kind text NOT NULL,
    name text NOT NULL,
    version integer DEFAULT 1 NOT NULL,
    trigger_event text NOT NULL,
    trigger_filters jsonb DEFAULT '{}'::jsonb NOT NULL,
    progress_mode text DEFAULT 'sum'::text NOT NULL,
    target integer NOT NULL,
    window_start timestamp with time zone,
    window_end timestamp with time zone,
    rewards jsonb DEFAULT '[]'::jsonb NOT NULL,
    active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_challenges_trigger_event ON public.challenges USING btree (trigger_event);
CREATE INDEX IF NOT EXISTS idx_challenges_active ON public.challenges USING btree (active);

CREATE TABLE IF NOT EXISTS public.challenge_progress (
    id serial PRIMARY KEY,
    challenge_id text NOT NULL,
    guild_id text NOT NULL,
    current_value integer DEFAULT 0 NOT NULL,
    completed boolean DEFAULT false NOT NULL,
    completed_at timestamp with time zone,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT challenge_progress_challenge_id_guild_id_key UNIQUE (challenge_id, guild_id)
);

CREATE INDEX IF NOT EXISTS idx_challenge_progress_guild ON public.challenge_progress USING btree (guild_id);

-- ------------------------------------------------------------------------- world contents

CREATE TABLE IF NOT EXISTS public.structures (
    id text PRIMARY KEY,
    type text NOT NULL,
    position_x double precision DEFAULT 0 NOT NULL,
    position_y double precision DEFAULT 0 NOT NULL,
    position_z double precision DEFAULT 0 NOT NULL,
    rotation double precision DEFAULT 0 NOT NULL,
    owner_id text NOT NULL,
    region_id text NOT NULL,
    placed_at timestamp with time zone DEFAULT now() NOT NULL,
    tags jsonb DEFAULT '[]'::jsonb NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_structures_region_id ON public.structures USING btree (region_id);
CREATE INDEX IF NOT EXISTS idx_structures_owner_id ON public.structures USING btree (owner_id);

CREATE TABLE IF NOT EXISTS public.world_items (
    id text PRIMARY KEY,
    item_type text NOT NULL,
    position_x double precision DEFAULT 0 NOT NULL,
    position_y double precision DEFAULT 0 NOT NULL,
    position_z double precision DEFAULT 0 NOT NULL,
    region_id text NOT NULL,
    quantity integer DEFAULT 1 NOT NULL,
    spawned_at timestamp with time zone DEFAULT now() NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_world_items_region ON public.world_items USING btree (region_id);

CREATE TABLE IF NOT EXISTS public.containers (
    id text PRIMARY KEY,
    structure_id text NOT NULL,
    region_id text NOT NULL
);

CREATE TABLE IF NOT EXISTS public.container_items (
    id serial PRIMARY KEY,
    container_id text NOT NULL,
    item_type text NOT NULL,
    quantity integer DEFAULT 1 NOT NULL,
    stored_at timestamp with time zone DEFAULT now() NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_container_items_container ON public.container_items USING btree (container_id);

CREATE TABLE IF NOT EXISTS public.player_inventories (
    id serial PRIMARY KEY,
    player_id text NOT NULL,
    item_type text NOT NULL,
    quantity integer DEFAULT 1 NOT NULL,
    acquired_at timestamp with time zone DEFAULT now() NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_player_inv_player ON public.player_inventories USING btree (player_id);

-- ------------------------------------------------------- nature 2.0 (EF NatureTwoPointZero)
-- Column set mirrors Migrations/20260328154322_NatureTwoPointZero.cs. That migration has
-- never been applied anywhere (nothing in the repo calls Migrate()), so these tables reach a
-- database only through this file.

CREATE TABLE IF NOT EXISTS public.natural_resources (
    id text PRIMARY KEY,
    type text NOT NULL,
    position_x double precision NOT NULL,
    position_y double precision NOT NULL,
    position_z double precision NOT NULL,
    region_id text NOT NULL,
    health double precision NOT NULL,
    stump_health double precision NOT NULL,
    regrowth_progress double precision NOT NULL,
    lean_x double precision NOT NULL,
    lean_z double precision NOT NULL,
    growth_history jsonb NOT NULL,
    created_at timestamp with time zone NOT NULL,
    last_updated_at timestamp with time zone NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_natural_resources_region_id" ON public.natural_resources USING btree (region_id);

CREATE TABLE IF NOT EXISTS public.region_profiles (
    id text PRIMARY KEY,
    region_id text NOT NULL,
    altitude_grid jsonb NOT NULL,
    humidity_grid jsonb NOT NULL,
    grid_width integer NOT NULL,
    grid_height integer NOT NULL,
    trade_wind_x double precision NOT NULL,
    trade_wind_z double precision NOT NULL,
    geologic_history jsonb NOT NULL,
    created_at timestamp with time zone NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_region_profiles_region_id" ON public.region_profiles USING btree (region_id);

-- ------------------------------------------------------------------- foreign keys (guarded)
-- ADD CONSTRAINT has no IF NOT EXISTS, so each is gated on pg_constraint.

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'challenge_progress_challenge_id_fkey'
    ) THEN
        ALTER TABLE ONLY public.challenge_progress
            ADD CONSTRAINT challenge_progress_challenge_id_fkey
            FOREIGN KEY (challenge_id) REFERENCES public.challenges(id);
    END IF;
END
$$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'container_items_container_id_fkey'
    ) THEN
        ALTER TABLE ONLY public.container_items
            ADD CONSTRAINT container_items_container_id_fkey
            FOREIGN KEY (container_id) REFERENCES public.containers(id);
    END IF;
END
$$;
