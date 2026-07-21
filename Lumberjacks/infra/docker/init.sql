--
-- PostgreSQL database dump
--

\restrict amHmSDBaybL5uzvq9yFt23WPLvRspVduWnQma5AS1ugu2v5uBbjrDW9lfngLCpG

-- Dumped from database version 17.9 (Debian 17.9-1.pgdg13+1)
-- Dumped by pg_dump version 17.9 (Debian 17.9-1.pgdg13+1)

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: challenge_progress; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.challenge_progress (
    id integer NOT NULL,
    challenge_id text NOT NULL,
    guild_id text NOT NULL,
    current_value integer DEFAULT 0 NOT NULL,
    completed boolean DEFAULT false NOT NULL,
    completed_at timestamp with time zone,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: challenge_progress_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.challenge_progress_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: challenge_progress_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.challenge_progress_id_seq OWNED BY public.challenge_progress.id;


--
-- Name: challenges; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.challenges (
    id text NOT NULL,
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


--
-- Name: container_items; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.container_items (
    id integer NOT NULL,
    container_id text NOT NULL,
    item_type text NOT NULL,
    quantity integer DEFAULT 1 NOT NULL,
    stored_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: container_items_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.container_items_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: container_items_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.container_items_id_seq OWNED BY public.container_items.id;


--
-- Name: containers; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.containers (
    id text NOT NULL,
    structure_id text NOT NULL,
    region_id text NOT NULL
);


--
-- Name: events; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.events (
    id integer NOT NULL,
    event_id uuid NOT NULL,
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


--
-- Name: events_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.events_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: events_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.events_id_seq OWNED BY public.events.id;


--
-- Name: guild_progress; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.guild_progress (
    guild_id text NOT NULL,
    points integer DEFAULT 0 NOT NULL,
    challenges_completed integer DEFAULT 0 NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: player_inventories; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.player_inventories (
    id integer NOT NULL,
    player_id text NOT NULL,
    item_type text NOT NULL,
    quantity integer DEFAULT 1 NOT NULL,
    acquired_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: player_inventories_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.player_inventories_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: player_inventories_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.player_inventories_id_seq OWNED BY public.player_inventories.id;


--
-- Name: player_progress; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.player_progress (
    player_id text NOT NULL,
    rank integer DEFAULT 0 NOT NULL,
    points integer DEFAULT 0 NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: structures; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.structures (
    id text NOT NULL,
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


--
-- Name: world_items; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.regions (
    id text NOT NULL,
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

ALTER TABLE ONLY public.regions ADD CONSTRAINT regions_pkey PRIMARY KEY (id);

CREATE TABLE public.world_items (
    id text NOT NULL,
    item_type text NOT NULL,
    position_x double precision DEFAULT 0 NOT NULL,
    position_y double precision DEFAULT 0 NOT NULL,
    position_z double precision DEFAULT 0 NOT NULL,
    region_id text NOT NULL,
    quantity integer DEFAULT 1 NOT NULL,
    spawned_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: challenge_progress id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.challenge_progress ALTER COLUMN id SET DEFAULT nextval('public.challenge_progress_id_seq'::regclass);


--
-- Name: container_items id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.container_items ALTER COLUMN id SET DEFAULT nextval('public.container_items_id_seq'::regclass);


--
-- Name: events id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.events ALTER COLUMN id SET DEFAULT nextval('public.events_id_seq'::regclass);


--
-- Name: player_inventories id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.player_inventories ALTER COLUMN id SET DEFAULT nextval('public.player_inventories_id_seq'::regclass);


--
-- Name: challenge_progress challenge_progress_challenge_id_guild_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.challenge_progress
    ADD CONSTRAINT challenge_progress_challenge_id_guild_id_key UNIQUE (challenge_id, guild_id);


--
-- Name: challenge_progress challenge_progress_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.challenge_progress
    ADD CONSTRAINT challenge_progress_pkey PRIMARY KEY (id);


--
-- Name: challenges challenges_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.challenges
    ADD CONSTRAINT challenges_pkey PRIMARY KEY (id);


--
-- Name: container_items container_items_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.container_items
    ADD CONSTRAINT container_items_pkey PRIMARY KEY (id);


--
-- Name: containers containers_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.containers
    ADD CONSTRAINT containers_pkey PRIMARY KEY (id);


--
-- Name: events events_event_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.events
    ADD CONSTRAINT events_event_id_key UNIQUE (event_id);


--
-- Name: events events_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.events
    ADD CONSTRAINT events_pkey PRIMARY KEY (id);


--
-- Name: guild_progress guild_progress_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.guild_progress
    ADD CONSTRAINT guild_progress_pkey PRIMARY KEY (guild_id);


--
-- Name: player_inventories player_inventories_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.player_inventories
    ADD CONSTRAINT player_inventories_pkey PRIMARY KEY (id);


--
-- Name: player_progress player_progress_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.player_progress
    ADD CONSTRAINT player_progress_pkey PRIMARY KEY (player_id);


--
-- Name: structures structures_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.structures
    ADD CONSTRAINT structures_pkey PRIMARY KEY (id);


--
-- Name: world_items world_items_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.world_items
    ADD CONSTRAINT world_items_pkey PRIMARY KEY (id);


--
-- Name: idx_challenge_progress_guild; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_challenge_progress_guild ON public.challenge_progress USING btree (guild_id);


--
-- Name: idx_container_items_container; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_container_items_container ON public.container_items USING btree (container_id);


--
-- Name: idx_events_actor; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_events_actor ON public.events USING btree (actor_id);


--
-- Name: idx_events_occurred; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_events_occurred ON public.events USING btree (occurred_at);


--
-- Name: idx_events_type; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_events_type ON public.events USING btree (event_type);


--
-- Name: idx_player_inv_player; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_player_inv_player ON public.player_inventories USING btree (player_id);


--
-- Name: idx_structures_owner_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_structures_owner_id ON public.structures USING btree (owner_id);


--
-- Name: idx_structures_region_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_structures_region_id ON public.structures USING btree (region_id);


--
-- Name: idx_world_items_region; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_world_items_region ON public.world_items USING btree (region_id);


--
-- Name: challenge_progress challenge_progress_challenge_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.challenge_progress
    ADD CONSTRAINT challenge_progress_challenge_id_fkey FOREIGN KEY (challenge_id) REFERENCES public.challenges(id);


--
-- Name: container_items container_items_container_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.container_items
    ADD CONSTRAINT container_items_container_id_fkey FOREIGN KEY (container_id) REFERENCES public.containers(id);


--
-- PostgreSQL database dump complete
--

\unrestrict amHmSDBaybL5uzvq9yFt23WPLvRspVduWnQma5AS1ugu2v5uBbjrDW9lfngLCpG

