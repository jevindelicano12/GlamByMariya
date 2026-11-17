-- Inactive V2 identity/authorization candidate.
--
-- This is not in the numbered migration sequence and must not be applied to a
-- hosted project. It extends a fresh disposable database after 0001 and 0002.
-- A server-side caller must pass evidence from its protected session; this SQL
-- independently checks stored values while holding the shared security locks.
-- Those values are NOT proof of a protected session: a backend caller that knows
-- another actor's values can impersonate them. Do not deploy or grant runtime
-- access until a database-verifiable or otherwise reviewed trust boundary exists.
-- The backend login receives EXECUTE on three narrow operations and no direct
-- table or sequence rights. The NOLOGIN function owner receives only the table
-- rights needed by those operations and is subject to FORCE RLS.

BEGIN;
SET LOCAL lock_timeout = '10s';
SET LOCAL statement_timeout = '120s';
SET LOCAL search_path = pg_catalog, public;

DO $$
DECLARE
    backend_role pg_roles%ROWTYPE;
    owner_role pg_roles%ROWTYPE;
BEGIN
    IF to_regclass('public.customers') IS NULL
       OR to_regclass('public.reservations') IS NULL
       OR to_regclass('public.makeup_artists') IS NULL THEN
        RAISE EXCEPTION 'V2 migrations 0001 and 0002 are required';
    END IF;
    IF (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public' AND c.relname IN ('customers','reservations')
          AND c.relkind='r' AND c.relrowsecurity AND c.relforcerowsecurity) <> 2 THEN
        RAISE EXCEPTION 'Customer and reservation tables must force RLS';
    END IF;
    IF to_regclass('public.admin_users') IS NOT NULL
       OR to_regclass('public.account_identities') IS NOT NULL
       OR to_regclass('public.account_security_generations') IS NOT NULL
       OR to_regnamespace('v2_authz_api') IS NOT NULL
       OR to_regnamespace('v2_authz_private') IS NOT NULL THEN
        RAISE EXCEPTION 'Identity candidate target already contains identity objects';
    END IF;
    IF EXISTS (SELECT 1 FROM public.customers)
       OR EXISTS (SELECT 1 FROM public.reservations) THEN
        RAISE EXCEPTION 'Identity candidate requires an empty customer/reservation foundation';
    END IF;
    SELECT * INTO backend_role FROM pg_roles WHERE rolname = 'glam_v2_backend';
    SELECT * INTO owner_role FROM pg_roles WHERE rolname = 'glam_v2_authz_owner';
    IF backend_role.rolname IS NULL OR NOT backend_role.rolcanlogin
       OR backend_role.rolsuper OR backend_role.rolcreatedb OR backend_role.rolcreaterole
       OR backend_role.rolreplication OR backend_role.rolbypassrls OR backend_role.rolinherit THEN
        RAISE EXCEPTION 'glam_v2_backend must be a least-privilege login role';
    END IF;
    IF owner_role.rolname IS NULL OR owner_role.rolcanlogin OR owner_role.rolsuper
       OR owner_role.rolcreatedb OR owner_role.rolcreaterole
       OR owner_role.rolreplication OR owner_role.rolbypassrls THEN
        RAISE EXCEPTION 'glam_v2_authz_owner must be a non-login, non-bypass role';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_auth_members m
        JOIN pg_roles r ON r.oid = m.roleid
        JOIN pg_roles member ON member.oid = m.member
        WHERE r.rolname IN ('glam_v2_backend','glam_v2_authz_owner')
           OR member.rolname IN ('glam_v2_backend','glam_v2_authz_owner')
    ) THEN
        RAISE EXCEPTION 'V2 authorization roles must have no role memberships';
    END IF;
END $$;

ALTER TABLE public.customers
    ADD COLUMN app_id uuid NOT NULL,
    ADD COLUMN is_active boolean NOT NULL DEFAULT true,
    ADD COLUMN locked_until timestamptz;
ALTER TABLE public.customers ADD CONSTRAINT customers_app_id_key UNIQUE (app_id);
ALTER TABLE public.customers ADD CONSTRAINT customers_app_identity_key UNIQUE (id, app_id);

CREATE TABLE public.admin_users (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    app_id uuid NOT NULL UNIQUE,
    username varchar(254) NOT NULL,
    email varchar(254) NOT NULL,
    role varchar(20) NOT NULL CHECK (role IN ('Admin','SuperAdmin')),
    is_active boolean NOT NULL DEFAULT true,
    locked_until timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT admin_users_app_identity_key UNIQUE (id, app_id)
);

CREATE TABLE public.account_identities (
    id uuid PRIMARY KEY,
    customer_id bigint UNIQUE,
    admin_id bigint UNIQUE,
    login text NOT NULL,
    auth_state text NOT NULL DEFAULT 'Unknown'
        CHECK (auth_state IN ('Unknown','Supabase')),
    supabase_user_id uuid,
    credential_epoch bigint NOT NULL DEFAULT 1 CHECK (credential_epoch > 0),
    credential_sync_state text NOT NULL DEFAULT 'Unknown'
        CHECK (credential_sync_state IN ('Unknown','Ready','Pending','ReconciliationRequired')),
    CONSTRAINT account_identities_exactly_one_owner CHECK
        (((customer_id IS NOT NULL)::integer + (admin_id IS NOT NULL)::integer) = 1),
    CONSTRAINT account_identities_login_normalized CHECK (login = lower(btrim(login))),
    CONSTRAINT account_identities_customer_owner_fkey
        FOREIGN KEY (customer_id, id) REFERENCES public.customers(id, app_id) ON DELETE RESTRICT,
    CONSTRAINT account_identities_admin_owner_fkey
        FOREIGN KEY (admin_id, id) REFERENCES public.admin_users(id, app_id) ON DELETE RESTRICT
);
CREATE UNIQUE INDEX account_identities_login_key ON public.account_identities(login);
CREATE UNIQUE INDEX account_identities_supabase_subject_key
    ON public.account_identities(supabase_user_id) WHERE supabase_user_id IS NOT NULL;

CREATE TABLE public.account_security_generations (
    account_id uuid PRIMARY KEY REFERENCES public.account_identities(id) ON DELETE RESTRICT,
    generation bigint NOT NULL CHECK (generation > 0)
);

ALTER TABLE public.admin_users ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.admin_users FORCE ROW LEVEL SECURITY;
ALTER TABLE public.account_identities ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.account_identities FORCE ROW LEVEL SECURITY;
ALTER TABLE public.account_security_generations ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.account_security_generations FORCE ROW LEVEL SECURITY;

CREATE POLICY v2_authz_owner_customers ON public.customers
    TO glam_v2_authz_owner USING (true) WITH CHECK (true);
CREATE POLICY v2_authz_owner_admin_users ON public.admin_users
    TO glam_v2_authz_owner USING (true) WITH CHECK (true);
CREATE POLICY v2_authz_owner_identities ON public.account_identities
    TO glam_v2_authz_owner USING (true) WITH CHECK (true);
CREATE POLICY v2_authz_owner_generations ON public.account_security_generations
    TO glam_v2_authz_owner USING (true) WITH CHECK (true);

REVOKE ALL ON TABLE public.customers, public.reservations, public.admin_users,
    public.account_identities, public.account_security_generations
    FROM PUBLIC, anon, authenticated, service_role, glam_v2_backend;
REVOKE ALL ON SEQUENCE public.admin_users_id_seq
    FROM PUBLIC, anon, authenticated, service_role, glam_v2_backend;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM glam_v2_backend;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM glam_v2_backend;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM glam_v2_backend;
REVOKE CREATE ON SCHEMA public FROM glam_v2_backend;
GRANT USAGE ON SCHEMA public TO glam_v2_authz_owner;
GRANT SELECT ON public.customers, public.admin_users, public.account_identities,
    public.account_security_generations TO glam_v2_authz_owner;
GRANT UPDATE (full_name) ON public.customers TO glam_v2_authz_owner;
-- PostgreSQL row-locking SELECT also requires UPDATE on at least one column.
GRANT UPDATE (app_id) ON public.admin_users TO glam_v2_authz_owner;
GRANT UPDATE (id) ON public.account_identities TO glam_v2_authz_owner;
GRANT UPDATE (generation) ON public.account_security_generations TO glam_v2_authz_owner;

CREATE SCHEMA v2_authz_private AUTHORIZATION glam_v2_authz_owner;
CREATE SCHEMA v2_authz_api AUTHORIZATION glam_v2_authz_owner;
REVOKE ALL ON SCHEMA v2_authz_private, v2_authz_api FROM PUBLIC, anon, authenticated, service_role;
GRANT USAGE ON SCHEMA v2_authz_api TO glam_v2_backend;

CREATE FUNCTION v2_authz_private.lock_actor(
    p_account_id uuid,
    p_provider_subject uuid,
    p_role text,
    p_credential_epoch bigint,
    p_security_generation bigint,
    p_exclusive boolean
) RETURNS boolean
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $$
DECLARE
    v_generation bigint;
    v_customer_id bigint;
    v_customer_active boolean;
    v_customer_locked_until timestamptz;
    v_customer_email text;
    v_admin_id bigint;
    v_admin_active boolean;
    v_admin_locked_until timestamptz;
    v_admin_email text;
    v_admin_role text;
    v_identity_customer_id bigint;
    v_identity_admin_id bigint;
    v_login text;
    v_auth_state text;
    v_subject uuid;
    v_epoch bigint;
    v_sync_state text;
    v_owner_count bigint;
BEGIN
    IF session_user <> 'glam_v2_backend'
       OR p_account_id IS NULL OR p_account_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_provider_subject IS NULL OR p_provider_subject = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_role IS NULL OR p_role NOT IN ('Customer','Admin','SuperAdmin')
       OR p_credential_epoch IS NULL OR p_credential_epoch <= 0
       OR p_security_generation IS NULL OR p_security_generation <= 0
       OR p_exclusive IS NULL THEN
        RETURN false;
    END IF;

    -- Shared order for every protected operation and security writer:
    -- generation -> customers row -> admin_users row -> identity row -> domain row.
    IF p_exclusive THEN
        SELECT generation INTO v_generation
        FROM public.account_security_generations
        WHERE account_id = p_account_id FOR UPDATE;
    ELSE
        SELECT generation INTO v_generation
        FROM public.account_security_generations
        WHERE account_id = p_account_id FOR SHARE;
    END IF;
    IF NOT FOUND OR v_generation <> p_security_generation THEN RETURN false; END IF;

    IF p_exclusive THEN
        SELECT id, is_active, locked_until, email INTO v_customer_id, v_customer_active,
            v_customer_locked_until, v_customer_email
        FROM public.customers WHERE app_id = p_account_id FOR UPDATE;
    ELSE
        SELECT id, is_active, locked_until, email INTO v_customer_id, v_customer_active,
            v_customer_locked_until, v_customer_email
        FROM public.customers WHERE app_id = p_account_id FOR SHARE;
    END IF;
    IF p_exclusive THEN
        SELECT id, is_active, locked_until, email, role INTO v_admin_id, v_admin_active,
            v_admin_locked_until, v_admin_email, v_admin_role
        FROM public.admin_users WHERE app_id = p_account_id FOR UPDATE;
    ELSE
        SELECT id, is_active, locked_until, email, role INTO v_admin_id, v_admin_active,
            v_admin_locked_until, v_admin_email, v_admin_role
        FROM public.admin_users WHERE app_id = p_account_id FOR SHARE;
    END IF;
    SELECT count(*) INTO v_owner_count FROM (
        SELECT 1 FROM public.customers WHERE app_id = p_account_id
        UNION ALL
        SELECT 1 FROM public.admin_users WHERE app_id = p_account_id
    ) owners;
    IF v_owner_count <> 1 THEN RETURN false; END IF;

    IF p_exclusive THEN
        SELECT customer_id, admin_id, login, auth_state, supabase_user_id,
            credential_epoch, credential_sync_state
        INTO v_identity_customer_id, v_identity_admin_id, v_login, v_auth_state,
            v_subject, v_epoch, v_sync_state
        FROM public.account_identities WHERE id = p_account_id FOR UPDATE;
    ELSE
        SELECT customer_id, admin_id, login, auth_state, supabase_user_id,
            credential_epoch, credential_sync_state
        INTO v_identity_customer_id, v_identity_admin_id, v_login, v_auth_state,
            v_subject, v_epoch, v_sync_state
        FROM public.account_identities WHERE id = p_account_id FOR SHARE;
    END IF;
    IF NOT FOUND OR ((v_identity_customer_id IS NOT NULL)::integer
        + (v_identity_admin_id IS NOT NULL)::integer) <> 1 THEN RETURN false; END IF;

    IF v_customer_id IS NOT NULL THEN
        IF v_admin_id IS NOT NULL OR v_identity_customer_id <> v_customer_id
           OR v_identity_admin_id IS NOT NULL OR p_role <> 'Customer'
           OR v_customer_active IS DISTINCT FROM true
           OR (v_customer_locked_until IS NOT NULL AND v_customer_locked_until > statement_timestamp())
           OR v_customer_email IS NULL OR v_login IS DISTINCT FROM lower(btrim(v_customer_email)) THEN RETURN false; END IF;
    ELSE
        IF v_admin_id IS NULL OR v_identity_admin_id <> v_admin_id
           OR v_identity_customer_id IS NOT NULL OR v_admin_role <> p_role
           OR v_admin_active IS DISTINCT FROM true
           OR (v_admin_locked_until IS NOT NULL AND v_admin_locked_until > statement_timestamp())
           OR v_admin_email IS NULL OR v_login IS DISTINCT FROM lower(btrim(v_admin_email)) THEN RETURN false; END IF;
    END IF;

    IF v_auth_state IS DISTINCT FROM 'Supabase' OR v_subject IS DISTINCT FROM p_provider_subject
       OR v_epoch IS DISTINCT FROM p_credential_epoch OR v_sync_state IS DISTINCT FROM 'Ready' THEN RETURN false; END IF;
    RETURN true;
END $$;

CREATE FUNCTION v2_authz_api.validate_session(
    p_account_id uuid, p_provider_subject uuid, p_role text,
    p_credential_epoch bigint, p_security_generation bigint
) RETURNS boolean
LANGUAGE sql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $$
    SELECT v2_authz_private.lock_actor(
        p_account_id, p_provider_subject, p_role, p_credential_epoch,
        p_security_generation, false)
$$;

CREATE FUNCTION v2_authz_api.update_customer_full_name(
    p_account_id uuid, p_provider_subject uuid, p_role text,
    p_credential_epoch bigint, p_security_generation bigint, p_full_name text
) RETURNS boolean
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $$
BEGIN
    IF p_role <> 'Customer' OR p_full_name IS NULL
       OR btrim(p_full_name) = '' OR char_length(p_full_name) > 150
       OR NOT v2_authz_private.lock_actor(
           p_account_id, p_provider_subject, p_role, p_credential_epoch,
           p_security_generation, true) THEN
        RETURN false;
    END IF;
    UPDATE public.customers c SET full_name = btrim(p_full_name)
    FROM public.account_identities i
    WHERE i.id = p_account_id AND i.customer_id = c.id AND i.admin_id IS NULL
      AND c.app_id = p_account_id;
    RETURN FOUND;
END $$;

CREATE FUNCTION v2_authz_api.revoke_my_sessions(
    p_account_id uuid, p_provider_subject uuid, p_role text,
    p_credential_epoch bigint, p_security_generation bigint
) RETURNS bigint
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $$
DECLARE
    v_generation bigint;
BEGIN
    IF NOT v2_authz_private.lock_actor(
        p_account_id, p_provider_subject, p_role, p_credential_epoch,
        p_security_generation, true) THEN
        RETURN NULL;
    END IF;
    UPDATE public.account_security_generations
    SET generation = generation + 1
    WHERE account_id = p_account_id
      AND generation = p_security_generation
      AND generation < 9223372036854775807
    RETURNING generation INTO v_generation;
    RETURN v_generation;
END $$;

ALTER FUNCTION v2_authz_private.lock_actor(uuid,uuid,text,bigint,bigint,boolean)
    OWNER TO glam_v2_authz_owner;
ALTER FUNCTION v2_authz_api.validate_session(uuid,uuid,text,bigint,bigint)
    OWNER TO glam_v2_authz_owner;
ALTER FUNCTION v2_authz_api.update_customer_full_name(uuid,uuid,text,bigint,bigint,text)
    OWNER TO glam_v2_authz_owner;
ALTER FUNCTION v2_authz_api.revoke_my_sessions(uuid,uuid,text,bigint,bigint)
    OWNER TO glam_v2_authz_owner;

REVOKE ALL ON FUNCTION v2_authz_private.lock_actor(uuid,uuid,text,bigint,bigint,boolean)
    FROM PUBLIC, anon, authenticated, service_role, glam_v2_backend;
REVOKE ALL ON FUNCTION v2_authz_api.validate_session(uuid,uuid,text,bigint,bigint)
    FROM PUBLIC, anon, authenticated, service_role, glam_v2_backend;
REVOKE ALL ON FUNCTION v2_authz_api.update_customer_full_name(uuid,uuid,text,bigint,bigint,text)
    FROM PUBLIC, anon, authenticated, service_role, glam_v2_backend;
REVOKE ALL ON FUNCTION v2_authz_api.revoke_my_sessions(uuid,uuid,text,bigint,bigint)
    FROM PUBLIC, anon, authenticated, service_role, glam_v2_backend;
GRANT EXECUTE ON FUNCTION v2_authz_api.validate_session(uuid,uuid,text,bigint,bigint),
    v2_authz_api.update_customer_full_name(uuid,uuid,text,bigint,bigint,text),
    v2_authz_api.revoke_my_sessions(uuid,uuid,text,bigint,bigint)
    TO glam_v2_backend;

COMMIT;
