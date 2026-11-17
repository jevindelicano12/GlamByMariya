-- Review candidate only. This closed ledger does not activate payment workflows.
-- Requires 0001 and 0002. Authentication, authoritative price snapshots,
-- proof ownership/storage, and trusted Customer/Admin commands remain blocked.
BEGIN;
SET LOCAL lock_timeout = '10s';
SET LOCAL statement_timeout = '120s';
SET LOCAL search_path = pg_catalog, public;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='anon')
       OR NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='authenticated')
       OR NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='service_role') THEN
        RAISE EXCEPTION 'Required Supabase API roles are missing';
    END IF;
END $$;

-- An immutable claim and its append-only revisions; no payment data is seeded.
-- The minimum is a supplied snapshot, NOT a verified business configuration.
CREATE TABLE public.payments (
    id bigserial PRIMARY KEY,
    app_id uuid NOT NULL DEFAULT gen_random_uuid() UNIQUE,
    reservation_id bigint NOT NULL UNIQUE REFERENCES public.reservations(id),
    amount numeric(14,2) NOT NULL CHECK (amount > 0),
    minimum_snapshot numeric(14,2) NOT NULL CHECK (minimum_snapshot > 0),
    total_snapshot numeric(14,2) NOT NULL CHECK (total_snapshot > 0),
    method varchar(30) NOT NULL CHECK (method IN ('GCash','Maya','Bank Transfer')),
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (minimum_snapshot <= amount AND amount <= total_snapshot)
);
CREATE TABLE public.payment_revisions (
    id bigserial PRIMARY KEY,
    payment_id bigint NOT NULL REFERENCES public.payments(id),
    revision bigint NOT NULL CHECK (revision >= 0),
    reference varchar(150) NOT NULL CHECK (length(btrim(reference)) > 0),
    proof_asset_id uuid,
    verification_status varchar(20) NOT NULL CHECK
        (verification_status IN ('Pending Verification','Correction Requested','Verified')),
    review_note text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(payment_id,revision),
    CHECK (verification_status <> 'Verified' OR proof_asset_id IS NOT NULL),
    CHECK (verification_status <> 'Correction Requested' OR length(btrim(review_note)) > 0)
);

-- No role is authorized by these triggers. They enforce local consistency only.
CREATE FUNCTION public.v2_payment_claim_guard() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
DECLARE r public.reservations%ROWTYPE;
BEGIN
    SELECT * INTO STRICT r FROM public.reservations WHERE id=NEW.reservation_id FOR UPDATE;
    IF r.status NOT IN ('Pending','Pending Payment') OR r.total_price <> NEW.total_snapshot THEN
        RAISE EXCEPTION 'Claim requires a pending reservation and matching total snapshot';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER v2_payment_claim_guard BEFORE INSERT ON public.payments
FOR EACH ROW EXECUTE FUNCTION public.v2_payment_claim_guard();

CREATE FUNCTION public.v2_payment_revision_guard() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
DECLARE p public.payments%ROWTYPE; r public.reservations%ROWTYPE;
        previous public.payment_revisions%ROWTYPE; expected bigint;
BEGIN
    SELECT * INTO STRICT p FROM public.payments WHERE id=NEW.payment_id;
    SELECT * INTO STRICT r FROM public.reservations WHERE id=p.reservation_id FOR UPDATE;
    PERFORM 1 FROM public.payments WHERE id=NEW.payment_id FOR UPDATE;
    IF r.status NOT IN ('Pending','Pending Payment') OR r.total_price <> p.total_snapshot THEN
        RAISE EXCEPTION 'Claim requires an unchanged pending reservation';
    END IF;
    SELECT * INTO previous FROM public.payment_revisions
        WHERE payment_id=NEW.payment_id ORDER BY revision DESC LIMIT 1;
    expected := CASE WHEN FOUND THEN previous.revision + 1 ELSE 0 END;
    IF NEW.revision <> expected THEN
        RAISE EXCEPTION 'Stale payment revision';
    END IF;
    IF expected=0 AND NEW.verification_status <> 'Pending Verification' THEN
        RAISE EXCEPTION 'The initial claim must await verification';
    END IF;
    IF expected>0 AND previous.verification_status='Verified' THEN
        RAISE EXCEPTION 'Verified evidence is immutable';
    END IF;
    IF NEW.verification_status='Verified' AND
       (NEW.reference IS DISTINCT FROM previous.reference OR NEW.proof_asset_id IS DISTINCT FROM previous.proof_asset_id) THEN
        RAISE EXCEPTION 'Verification must use the existing evidence';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER v2_payment_revision_guard BEFORE INSERT ON public.payment_revisions
FOR EACH ROW EXECUTE FUNCTION public.v2_payment_revision_guard();

CREATE FUNCTION public.v2_payment_immutable() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    RAISE EXCEPTION 'Payment ledger is append-only';
END $$;
CREATE TRIGGER v2_payment_immutable BEFORE UPDATE OR DELETE ON public.payments
FOR EACH ROW EXECUTE FUNCTION public.v2_payment_immutable();
CREATE TRIGGER v2_payment_revision_immutable BEFORE UPDATE OR DELETE ON public.payment_revisions
FOR EACH ROW EXECUTE FUNCTION public.v2_payment_immutable();

ALTER TABLE public.payments ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.payments FORCE ROW LEVEL SECURITY;
ALTER TABLE public.payment_revisions ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.payment_revisions FORCE ROW LEVEL SECURITY;
REVOKE ALL ON public.payments, public.payment_revisions FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON SEQUENCE public.payments_id_seq, public.payment_revisions_id_seq FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION public.v2_payment_claim_guard(), public.v2_payment_revision_guard(), public.v2_payment_immutable() FROM PUBLIC, anon, authenticated, service_role;
COMMIT;
