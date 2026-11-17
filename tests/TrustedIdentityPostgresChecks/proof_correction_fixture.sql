-- Synthetic-only structural fixture. This is not migration 0003 and must never
-- be run against a hosted database or an existing developer database.
BEGIN;
DO $$
BEGIN
    IF current_database()<>'glam_v2_identity_boundary_test'
       OR current_user<>'postgres' OR session_user<>'postgres'
       OR inet_server_addr() IS DISTINCT FROM '127.0.0.1'::inet
       OR inet_server_port()<>55433 THEN
        RAISE EXCEPTION 'Wrong synthetic fixture target';
    END IF;
    IF to_regclass('public.payments') IS NOT NULL
       OR to_regclass('public.payment_revisions') IS NOT NULL
       OR to_regclass('public.payment_proof_assets') IS NOT NULL THEN
        RAISE EXCEPTION 'Synthetic fixture objects already exist';
    END IF;
END $$;
CREATE TABLE public.payments (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    reservation_id bigint NOT NULL UNIQUE REFERENCES public.reservations(id)
);
CREATE TABLE public.payment_proof_assets (
    id uuid PRIMARY KEY,
    reservation_id bigint NOT NULL REFERENCES public.reservations(id),
    customer_id bigint NOT NULL REFERENCES public.customers(id),
    is_validated boolean NOT NULL
);
CREATE TABLE public.payment_revisions (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    payment_id bigint NOT NULL REFERENCES public.payments(id),
    revision bigint NOT NULL CHECK(revision >= 0),
    reference varchar(150) NOT NULL CHECK(length(btrim(reference)) > 0),
    proof_asset_id uuid REFERENCES public.payment_proof_assets(id),
    verification_status varchar(20) NOT NULL CHECK(verification_status IN ('Pending Verification','Correction Requested','Verified')),
    UNIQUE(payment_id,revision)
);
CREATE FUNCTION public.fixture_revision_immutable() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN RAISE EXCEPTION 'Synthetic history is append-only'; END $$;
CREATE TRIGGER fixture_revision_immutable BEFORE UPDATE OR DELETE ON public.payment_revisions
FOR EACH ROW EXECUTE FUNCTION public.fixture_revision_immutable();
ALTER TABLE public.payments ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.payments FORCE ROW LEVEL SECURITY;
ALTER TABLE public.payment_proof_assets ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.payment_proof_assets FORCE ROW LEVEL SECURITY;
ALTER TABLE public.payment_revisions ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.payment_revisions FORCE ROW LEVEL SECURITY;
REVOKE ALL ON public.payments,public.payment_revisions,public.payment_proof_assets
    FROM PUBLIC,anon,authenticated,service_role,glam_v2_backend;
REVOKE ALL ON SEQUENCE public.payments_id_seq,public.payment_revisions_id_seq
    FROM PUBLIC,anon,authenticated,service_role,glam_v2_backend;
REVOKE ALL ON FUNCTION public.fixture_revision_immutable()
    FROM PUBLIC,anon,authenticated,service_role,glam_v2_backend;
COMMIT;
