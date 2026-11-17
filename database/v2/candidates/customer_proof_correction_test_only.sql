-- ISOLATED TEST-ONLY candidate: not a migration and not for a hosted database.
-- Requires 0001/0002, identity_authorization_candidate.sql and the synthetic
-- proof_correction_fixture.sql. It does not provision real proof storage.
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
        RAISE EXCEPTION 'Wrong synthetic candidate target';
    END IF;
END $$;

CREATE POLICY fixture_proof_owner_reservation ON public.reservations
    TO glam_v2_authz_owner USING(true) WITH CHECK(true);
CREATE POLICY fixture_proof_owner_payments ON public.payments
    TO glam_v2_authz_owner USING(true) WITH CHECK(true);
CREATE POLICY fixture_proof_owner_assets ON public.payment_proof_assets
    TO glam_v2_authz_owner USING(true) WITH CHECK(true);
CREATE POLICY fixture_proof_owner_revisions ON public.payment_revisions
    TO glam_v2_authz_owner USING(true) WITH CHECK(true);
GRANT SELECT ON public.reservations,public.payments,public.payment_proof_assets,public.payment_revisions TO glam_v2_authz_owner;
-- PostgreSQL requires an UPDATE privilege for row-locking SELECT.
GRANT UPDATE(reference) ON public.reservations TO glam_v2_authz_owner;
GRANT UPDATE(reservation_id) ON public.payments TO glam_v2_authz_owner;
GRANT UPDATE(id) ON public.payment_proof_assets TO glam_v2_authz_owner;
GRANT INSERT ON public.payment_revisions TO glam_v2_authz_owner;
GRANT USAGE ON SEQUENCE public.payment_revisions_id_seq TO glam_v2_authz_owner;

CREATE FUNCTION v2_authz_api.fixture_correct_customer_proof(
    p_account_id uuid, p_provider_subject uuid, p_role text,
    p_credential_epoch bigint, p_security_generation bigint,
    p_reservation_id bigint, p_expected_revision bigint, p_reference text, p_asset_id uuid
) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE v_customer_id bigint; v_owner bigint; v_status text; v_payment_id bigint;
        v_revision bigint; v_verification text;
BEGIN
    IF p_role IS DISTINCT FROM 'Customer' OR p_reservation_id IS NULL OR p_reservation_id <= 0
       OR p_expected_revision IS NULL OR p_expected_revision < 0 OR p_expected_revision = 9223372036854775807
       OR p_reference IS NULL OR length(btrim(p_reference)) NOT BETWEEN 1 AND 150
       OR p_asset_id IS NULL THEN RETURN false; END IF;
    -- Generation -> Customer -> staff -> identity; locks persist for the transaction.
    IF NOT v2_authz_private.lock_actor(p_account_id,p_provider_subject,p_role,p_credential_epoch,p_security_generation,false)
        THEN RETURN false; END IF;
    SELECT customer_id INTO v_customer_id FROM public.account_identities WHERE id=p_account_id;
    -- Domain order: reservation -> payment -> revision read -> proof asset.
    SELECT customer_id,status INTO v_owner,v_status FROM public.reservations
        WHERE id=p_reservation_id FOR UPDATE;
    IF NOT FOUND OR v_owner IS DISTINCT FROM v_customer_id OR v_status <> 'Pending Payment' THEN RETURN false; END IF;
    SELECT id INTO v_payment_id FROM public.payments WHERE reservation_id=p_reservation_id FOR UPDATE;
    IF NOT FOUND THEN RETURN false; END IF;
    SELECT revision,verification_status INTO v_revision,v_verification FROM public.payment_revisions
        WHERE payment_id=v_payment_id ORDER BY revision DESC LIMIT 1;
    IF NOT FOUND OR v_revision <> p_expected_revision OR v_verification='Verified' THEN RETURN false; END IF;
    PERFORM 1 FROM public.payment_proof_assets WHERE id=p_asset_id
        AND reservation_id=p_reservation_id AND customer_id=v_customer_id AND is_validated FOR SHARE;
    IF NOT FOUND THEN RETURN false; END IF;
    INSERT INTO public.payment_revisions(payment_id,revision,reference,proof_asset_id,verification_status)
    VALUES(v_payment_id,v_revision+1,btrim(p_reference),p_asset_id,'Pending Verification');
    RETURN true;
END $$;
ALTER FUNCTION v2_authz_api.fixture_correct_customer_proof(uuid,uuid,text,bigint,bigint,bigint,bigint,text,uuid)
    OWNER TO glam_v2_authz_owner;
REVOKE ALL ON FUNCTION v2_authz_api.fixture_correct_customer_proof(uuid,uuid,text,bigint,bigint,bigint,bigint,text,uuid)
    FROM PUBLIC,anon,authenticated,service_role,glam_v2_backend;
GRANT EXECUTE ON FUNCTION v2_authz_api.fixture_correct_customer_proof(uuid,uuid,text,bigint,bigint,bigint,bigint,text,uuid)
    TO glam_v2_backend;
COMMIT;
