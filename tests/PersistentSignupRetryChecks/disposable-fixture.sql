-- TEST ONLY. Run only after separate approval on a newly initialized local cluster.
-- psql variables marker (32 lowercase UUID hex) and port must be supplied.
\set ON_ERROR_STOP on
BEGIN;
SELECT set_config('glam.retry_marker', :'marker', true), set_config('glam.retry_port', :'port', true);
DO $guard$
DECLARE
    marker text := current_setting('glam.retry_marker');
    runner text := 'glam_retry_runner_' || marker;
BEGIN
    IF marker !~ '^[0-9a-f]{32}$' OR marker = repeat('0',32)
       OR current_database() <> 'glam_retry_disposable_' || marker
       OR current_user <> 'postgres' OR inet_server_addr() IS DISTINCT FROM '127.0.0.1'::inet
       OR inet_client_addr() IS DISTINCT FROM '127.0.0.1'::inet
       OR inet_server_port()::text <> current_setting('glam.retry_port')
       OR EXISTS (SELECT 1 FROM pg_roles WHERE rolname=runner)
       OR EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public')
       OR EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public')
       OR EXISTS (SELECT 1 FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace WHERE n.nspname='public')
    THEN RAISE EXCEPTION 'Unsafe or nonempty disposable fixture target'; END IF;
    EXECUTE format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS',runner);
END $guard$;

CREATE TABLE public.glam_retry_disposable_sentinel (marker uuid PRIMARY KEY);
INSERT INTO public.glam_retry_disposable_sentinel VALUES (current_setting('glam.retry_marker')::uuid);
CREATE TABLE public.customers (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, app_id uuid NOT NULL UNIQUE,
    email text NOT NULL, full_name text NOT NULL, first_name text, last_name text,
    age_at_registration smallint, gender text, address text, password_hash text NOT NULL,
    is_active boolean NOT NULL DEFAULT false, locked_until timestamptz
);
CREATE TABLE public.admin_users (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, app_id uuid NOT NULL UNIQUE,
    email text, username text NOT NULL
);
CREATE TABLE public.account_identities (
    id uuid PRIMARY KEY, customer_id bigint UNIQUE REFERENCES public.customers(id),
    admin_id bigint UNIQUE REFERENCES public.admin_users(id), login text NOT NULL,
    auth_state text NOT NULL DEFAULT 'Unknown' CHECK (auth_state IN ('Unknown','LegacyOnly','Supabase')),
    supabase_user_id uuid, credential_epoch bigint NOT NULL DEFAULT 1 CHECK (credential_epoch>0),
    credential_sync_state text NOT NULL DEFAULT 'Unknown'
        CHECK (credential_sync_state IN ('Unknown','Ready','Pending','ReconciliationRequired')),
    CHECK ((customer_id IS NOT NULL)::int+(admin_id IS NOT NULL)::int=1)
);
CREATE UNIQUE INDEX ux_retry_fixture_login ON public.account_identities(lower(btrim(login)));
CREATE UNIQUE INDEX ux_retry_fixture_subject ON public.account_identities(supabase_user_id) WHERE supabase_user_id IS NOT NULL;
CREATE TABLE public.account_security_generations (
    account_id uuid PRIMARY KEY CHECK (account_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    generation bigint NOT NULL CHECK (generation>0)
);
CREATE TABLE public.auth_mvc_signup_attempts (
    attempt_id uuid PRIMARY KEY,
    browser_secret_hash bytea NOT NULL UNIQUE CHECK (octet_length(browser_secret_hash)=32),
    normalized_email text NOT NULL CHECK (normalized_email=btrim(lower(normalized_email)) AND normalized_email<>''),
    encrypted_profile bytea NOT NULL CHECK (octet_length(encrypted_profile)>0),
    provider_subject uuid,
    state text NOT NULL CHECK (state IN ('Initiated','AwaitingOtp','Verifying','Resending','Consumed','Unconfirmed')),
    operation_id uuid, consumed_operation_id uuid,
    verification_attempts integer NOT NULL DEFAULT 0 CHECK (verification_attempts BETWEEN 0 AND 5),
    next_resend_at timestamptz NOT NULL DEFAULT now()+interval '60 seconds',
    created_at timestamptz NOT NULL DEFAULT now(), expires_at timestamptz NOT NULL CHECK (expires_at>created_at),
    CHECK (state NOT IN ('AwaitingOtp','Consumed','Verifying','Resending') OR provider_subject IS NOT NULL),
    CHECK (state NOT IN ('Verifying','Resending') OR provider_subject <> '00000000-0000-0000-0000-000000000000'::uuid),
    CHECK (operation_id IS NULL OR operation_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CHECK (consumed_operation_id IS NULL OR consumed_operation_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CHECK ((state IN ('Verifying','Resending') AND operation_id IS NOT NULL AND consumed_operation_id IS NULL)
        OR (state='Consumed' AND ((operation_id IS NULL AND consumed_operation_id IS NULL)
            OR (operation_id IS NOT NULL AND consumed_operation_id IS NOT NULL AND operation_id=consumed_operation_id)))
        OR (state NOT IN ('Verifying','Resending','Consumed') AND operation_id IS NULL AND consumed_operation_id IS NULL))
);
CREATE UNIQUE INDEX ux_retry_fixture_operation ON public.auth_mvc_signup_attempts(operation_id) WHERE operation_id IS NOT NULL;
CREATE UNIQUE INDEX ux_retry_fixture_unresolved_email ON public.auth_mvc_signup_attempts(normalized_email)
    WHERE state IN ('Initiated','AwaitingOtp','Verifying','Resending','Unconfirmed');

CREATE FUNCTION public.retry_fixture_customer_sync() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
BEGIN
    INSERT INTO public.account_identities(id,customer_id,login) VALUES(NEW.app_id,NEW.id,btrim(NEW.email));
    RETURN NEW;
END $$;
CREATE TRIGGER retry_fixture_customer_sync AFTER INSERT ON public.customers
    FOR EACH ROW EXECUTE FUNCTION public.retry_fixture_customer_sync();

REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM PUBLIC;
DO $grant$
DECLARE runner text := 'glam_retry_runner_' || current_setting('glam.retry_marker');
BEGIN
    EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC',current_database());
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I',current_database(),runner);
    EXECUTE format('GRANT USAGE ON SCHEMA public TO %I',runner);
    EXECUTE format('GRANT SELECT ON public.glam_retry_disposable_sentinel, public.admin_users TO %I',runner);
    EXECUTE format('GRANT SELECT,INSERT ON public.customers TO %I',runner);
    EXECUTE format('GRANT SELECT,INSERT,UPDATE ON public.account_identities, public.account_security_generations, public.auth_mvc_signup_attempts TO %I',runner);
    EXECUTE format('GRANT USAGE,SELECT ON SEQUENCE public.customers_id_seq TO %I',runner);
END $grant$;
COMMIT;
