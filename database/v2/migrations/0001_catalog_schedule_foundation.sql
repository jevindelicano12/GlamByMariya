-- V2 foundation candidate: empty catalog and artist scheduling tables only.
-- Apply only after the prerequisites and disposable-database checks in README.md.
-- Intentionally no data, policies, runtime grants, credentials, or cloud configuration.
BEGIN;
SET LOCAL lock_timeout = '10s';
SET LOCAL statement_timeout = '120s';

-- Supabase API roles must exist so the explicit revocations cannot silently fail.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon')
       OR NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated')
       OR NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'service_role') THEN
        RAISE EXCEPTION 'Required Supabase API roles anon, authenticated and service_role are missing';
    END IF;
END $$;

-- The initial shapes are the empty, non-user-data subset of
-- tests/PublicChecks/BaselinePublic.sql, not the makeup-schema initializer.
CREATE TABLE public.makeup_artists (
    id bigserial PRIMARY KEY,
    full_name varchar(150) NOT NULL,
    phone varchar(20),
    is_active boolean NOT NULL DEFAULT true
);
CREATE TABLE public.services (
    id bigserial PRIMARY KEY,
    name varchar(100) NOT NULL,
    description text,
    price numeric(10,2) NOT NULL,
    duration_minutes integer NOT NULL,
    is_active boolean NOT NULL DEFAULT true,
    CONSTRAINT services_duration_minutes_check CHECK (duration_minutes > 0),
    CONSTRAINT services_price_check CHECK (price >= 0)
);
CREATE TABLE public.artist_availability (
    id bigserial PRIMARY KEY,
    artist_id bigint NOT NULL REFERENCES public.makeup_artists(id),
    weekday integer NOT NULL,
    start_time time NOT NULL,
    end_time time NOT NULL,
    CONSTRAINT artist_availability_check CHECK (end_time > start_time),
    CONSTRAINT artist_availability_weekday_check CHECK (weekday BETWEEN 0 AND 6)
);
CREATE TABLE public.blocked_slots (
    id bigserial PRIMARY KEY,
    artist_id bigint NOT NULL REFERENCES public.makeup_artists(id),
    start_at timestamptz NOT NULL,
    end_at timestamptz NOT NULL,
    reason text,
    CONSTRAINT blocked_slots_check CHECK (end_at > start_at)
);

-- RLS with no policies is default-deny for non-bypass roles. Explicit revocations
-- also override grants inherited from PUBLIC or Supabase default privileges.
ALTER TABLE public.makeup_artists ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.makeup_artists FORCE ROW LEVEL SECURITY;
ALTER TABLE public.services ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.services FORCE ROW LEVEL SECURITY;
ALTER TABLE public.artist_availability ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.artist_availability FORCE ROW LEVEL SECURITY;
ALTER TABLE public.blocked_slots ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.blocked_slots FORCE ROW LEVEL SECURITY;
REVOKE ALL ON TABLE public.makeup_artists, public.services, public.artist_availability, public.blocked_slots FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON SEQUENCE public.makeup_artists_id_seq, public.services_id_seq, public.artist_availability_id_seq, public.blocked_slots_id_seq FROM PUBLIC, anon, authenticated, service_role;
COMMIT;
