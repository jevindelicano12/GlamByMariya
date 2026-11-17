-- Candidate only: closed customer and reservation baseline, not runtime activation.
-- Requires reviewed 0001, the Supabase API roles and btree_gist in public or
-- extensions. Install/verify the extension separately; this file does not do so.
BEGIN;
SET LOCAL lock_timeout = '10s';
SET LOCAL statement_timeout = '120s';
SET LOCAL search_path = pg_catalog, public, extensions;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace
        WHERE e.extname = 'btree_gist' AND n.nspname IN ('public', 'extensions')
    ) THEN
        RAISE EXCEPTION 'btree_gist in public or extensions is required';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon')
       OR NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated')
       OR NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'service_role') THEN
        RAISE EXCEPTION 'Required Supabase API roles are missing';
    END IF;
END $$;

-- Numeric keys and names follow the existing public adapter's baseline.
-- Authentication linkage is intentionally not created by this migration.
CREATE TABLE public.customers (
    id bigserial PRIMARY KEY,
    full_name varchar(150) NOT NULL,
    phone varchar(20),
    email varchar(254),
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE public.reservations (
    id bigserial PRIMARY KEY,
    reference varchar(30) NOT NULL,
    customer_id bigint NOT NULL REFERENCES public.customers(id),
    service_id bigint NOT NULL REFERENCES public.services(id),
    artist_id bigint NOT NULL REFERENCES public.makeup_artists(id),
    start_at timestamptz NOT NULL,
    end_at timestamptz NOT NULL,
    reserved_until timestamptz NOT NULL,
    location text,
    notes text,
    total_price numeric(10,2) NOT NULL,
    status varchar(20) NOT NULL DEFAULT 'Pending Payment',
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT reservations_reference_key UNIQUE (reference),
    CONSTRAINT reservations_interval_check CHECK (end_at > start_at AND reserved_until >= end_at),
    CONSTRAINT reservations_total_price_check CHECK (total_price >= 0),
    CONSTRAINT reservations_status_check CHECK (status IN
        ('Pending','Pending Payment','Confirmed','InProgress','Completed','Cancelled','Rejected','NoShow','No-show','Incomplete')),
    CONSTRAINT reservations_artist_reserved_overlap EXCLUDE USING gist
        (artist_id WITH =, tstzrange(start_at, reserved_until, '[)') WITH &&)
        WHERE (status IN ('Pending','Pending Payment','Confirmed','InProgress'))
);
CREATE INDEX idx_reservations_artist_time ON public.reservations (artist_id, start_at, end_at);
CREATE INDEX idx_reservations_status ON public.reservations (status);
CREATE INDEX ix_reservations_customer_history ON public.reservations (customer_id, start_at DESC);

ALTER TABLE public.customers ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.customers FORCE ROW LEVEL SECURITY;
ALTER TABLE public.reservations ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.reservations FORCE ROW LEVEL SECURITY;
REVOKE ALL ON TABLE public.customers, public.reservations FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON SEQUENCE public.customers_id_seq, public.reservations_id_seq FROM PUBLIC, anon, authenticated, service_role;
COMMIT;
