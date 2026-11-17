-- Synthetic fixture only; no storage object is accepted by this candidate.
BEGIN;
SET LOCAL lock_timeout='10s';
SET LOCAL statement_timeout='120s';
DO $$
BEGIN
    IF current_database()<>'glam_v2_identity_boundary_test'
       OR current_user<>'postgres' OR session_user<>'postgres'
       OR inet_server_addr() IS DISTINCT FROM '127.0.0.1'::inet
       OR inet_server_port()<>55433
       OR to_regclass('public.payment_proof_assets') IS NULL THEN
        RAISE EXCEPTION 'Wrong synthetic staging target';
    END IF;
END $$;

CREATE TABLE public.fixture_proof_staging (
    id uuid PRIMARY KEY,
    customer_id bigint NOT NULL REFERENCES public.customers(id),
    reservation_id bigint NOT NULL REFERENCES public.reservations(id),
    payment_id bigint NOT NULL REFERENCES public.payments(id),
    expected_revision bigint NOT NULL CHECK (expected_revision >= 0),
    content_sha256 bytea NOT NULL CHECK (octet_length(content_sha256) = 32),
    extension text NOT NULL CHECK (extension IN ('.png','.jpg')),
    object_key text NOT NULL UNIQUE,
    state text NOT NULL DEFAULT 'pending'
        CHECK (state IN ('pending','verified','consumed','reconciliation_required','retired')),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
ALTER TABLE public.fixture_proof_staging ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.fixture_proof_staging FORCE ROW LEVEL SECURITY;
REVOKE ALL ON public.fixture_proof_staging FROM PUBLIC,anon,authenticated,service_role,glam_v2_backend;
CREATE POLICY fixture_staging_owner ON public.fixture_proof_staging
    TO glam_v2_authz_owner USING(true) WITH CHECK(true);
GRANT SELECT,INSERT,UPDATE(state) ON public.fixture_proof_staging TO glam_v2_authz_owner;

-- No acceptance or deletion transition is enabled until storage immutability
-- and deletion/consumption coordination are proven.
CREATE FUNCTION v2_authz_private.fixture_guard_stage_transition() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    IF OLD.state <> 'pending' OR NEW.state <> 'reconciliation_required'
       OR (to_jsonb(OLD)-'state') IS DISTINCT FROM (to_jsonb(NEW)-'state') THEN
        RAISE EXCEPTION 'Unapproved proof staging transition';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER fixture_guard_stage_transition BEFORE UPDATE ON public.fixture_proof_staging
FOR EACH ROW EXECUTE FUNCTION v2_authz_private.fixture_guard_stage_transition();
REVOKE ALL ON FUNCTION v2_authz_private.fixture_guard_stage_transition()
    FROM PUBLIC,anon,authenticated,service_role,glam_v2_backend;

-- p_id and the digest are supplied by the trusted server, never by a browser.
-- A retry can reuse p_id; a conflicting retry never changes the existing row.
CREATE FUNCTION v2_authz_api.fixture_begin_proof_stage(
    p_account uuid,p_subject uuid,p_role text,p_epoch bigint,p_generation bigint,
    p_reservation bigint,p_revision bigint,p_id uuid,p_digest bytea,p_extension text
) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE v_customer bigint; v_owner bigint; v_status text; v_payment bigint;
        v_revision bigint; v_verification text; v_existing public.fixture_proof_staging%ROWTYPE;
BEGIN
    IF p_role IS DISTINCT FROM 'Customer' OR p_id IS NULL OR p_id='00000000-0000-0000-0000-000000000000'::uuid
       OR p_reservation IS NULL OR p_reservation<=0 OR p_revision IS NULL OR p_revision<0
       OR p_revision=9223372036854775807 OR p_digest IS NULL OR octet_length(p_digest)<>32
       OR p_extension IS NULL OR p_extension NOT IN ('.png','.jpg') THEN RETURN NULL; END IF;
    IF NOT v2_authz_private.lock_actor(p_account,p_subject,p_role,p_epoch,p_generation,false) THEN RETURN NULL; END IF;
    SELECT customer_id INTO v_customer FROM public.account_identities WHERE id=p_account;
    SELECT customer_id,status INTO v_owner,v_status FROM public.reservations WHERE id=p_reservation FOR UPDATE;
    IF NOT FOUND OR v_owner IS DISTINCT FROM v_customer OR v_status IS DISTINCT FROM 'Pending Payment' THEN RETURN NULL; END IF;
    SELECT id INTO v_payment FROM public.payments WHERE reservation_id=p_reservation FOR UPDATE;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT revision,verification_status INTO v_revision,v_verification FROM public.payment_revisions
        WHERE payment_id=v_payment ORDER BY revision DESC LIMIT 1;
    IF NOT FOUND OR v_revision<>p_revision OR v_verification='Verified' THEN RETURN NULL; END IF;
    INSERT INTO public.fixture_proof_staging(id,customer_id,reservation_id,payment_id,expected_revision,content_sha256,extension,object_key)
    VALUES(p_id,v_customer,p_reservation,v_payment,p_revision,p_digest,p_extension,'ProofStaging/'||replace(p_id::text,'-','')||p_extension)
    ON CONFLICT DO NOTHING;
    SELECT * INTO v_existing FROM public.fixture_proof_staging WHERE id=p_id;
    IF NOT FOUND OR v_existing.customer_id<>v_customer OR v_existing.reservation_id<>p_reservation
       OR v_existing.payment_id<>v_payment OR v_existing.expected_revision<>p_revision
       OR v_existing.content_sha256<>p_digest OR v_existing.extension<>p_extension
       OR v_existing.state<>'pending' THEN RETURN NULL; END IF;
    RETURN v_existing.id;
END $$;
ALTER FUNCTION v2_authz_api.fixture_begin_proof_stage(uuid,uuid,text,bigint,bigint,bigint,bigint,uuid,bytea,text)
    OWNER TO glam_v2_authz_owner;
REVOKE ALL ON FUNCTION v2_authz_api.fixture_begin_proof_stage(uuid,uuid,text,bigint,bigint,bigint,bigint,uuid,bytea,text)
    FROM PUBLIC,anon,authenticated,service_role,glam_v2_backend;
GRANT EXECUTE ON FUNCTION v2_authz_api.fixture_begin_proof_stage(uuid,uuid,text,bigint,bigint,bigint,bigint,uuid,bytea,text)
    TO glam_v2_backend;

-- Trusted backend calls this only when the object outcome is unknown. Quarantine
-- cannot make an asset eligible, change its binding, or cause a storage delete.
CREATE FUNCTION v2_authz_api.fixture_quarantine_proof_stage(
    p_account uuid,p_subject uuid,p_role text,p_epoch bigint,p_generation bigint,
    p_reservation bigint,p_id uuid
) RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE v_customer bigint; v_owner bigint; v_payment bigint; v_stage public.fixture_proof_staging%ROWTYPE;
BEGIN
    IF p_role IS DISTINCT FROM 'Customer' OR p_reservation IS NULL OR p_id IS NULL
       OR NOT v2_authz_private.lock_actor(p_account,p_subject,p_role,p_epoch,p_generation,false)
       THEN RETURN false; END IF;
    SELECT customer_id INTO v_customer FROM public.account_identities WHERE id=p_account;
    SELECT customer_id INTO v_owner FROM public.reservations WHERE id=p_reservation FOR UPDATE;
    IF NOT FOUND OR v_owner IS DISTINCT FROM v_customer THEN RETURN false; END IF;
    SELECT id INTO v_payment FROM public.payments WHERE reservation_id=p_reservation FOR UPDATE;
    IF NOT FOUND THEN RETURN false; END IF;
    SELECT * INTO v_stage FROM public.fixture_proof_staging WHERE id=p_id FOR UPDATE;
    IF NOT FOUND OR v_stage.customer_id IS DISTINCT FROM v_customer
       OR v_stage.reservation_id IS DISTINCT FROM p_reservation
       OR v_stage.payment_id IS DISTINCT FROM v_payment THEN RETURN false; END IF;
    IF v_stage.state='reconciliation_required' THEN RETURN true; END IF;
    IF v_stage.state<>'pending' THEN RETURN false; END IF;
    UPDATE public.fixture_proof_staging SET state='reconciliation_required' WHERE id=p_id;
    RETURN true;
END $$;
ALTER FUNCTION v2_authz_api.fixture_quarantine_proof_stage(uuid,uuid,text,bigint,bigint,bigint,uuid)
    OWNER TO glam_v2_authz_owner;
REVOKE ALL ON FUNCTION v2_authz_api.fixture_quarantine_proof_stage(uuid,uuid,text,bigint,bigint,bigint,uuid)
    FROM PUBLIC,anon,authenticated,service_role,glam_v2_backend;
GRANT EXECUTE ON FUNCTION v2_authz_api.fixture_quarantine_proof_stage(uuid,uuid,text,bigint,bigint,bigint,uuid)
    TO glam_v2_backend;
COMMIT;
