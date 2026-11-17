-- REVIEW ONLY. Separate approval is required for this exact file and target.
-- Execute only in a new, empty, disposable local database owned by glam_authz_test.
\set ON_ERROR_STOP on
\if :{?test_database}
\else
\set test_database glam_authz_sprint3
\endif
BEGIN;
SELECT pg_catalog.set_config('glam.test_database', :'test_database', true);
DO $$
BEGIN
  IF current_setting('glam.test_database', true) NOT IN ('glam_authz_sprint3','glam_authz_sprint3_retry')
     OR current_database() <> current_setting('glam.test_database', true)
     OR current_user <> 'glam_authz_test'
     OR session_user <> 'glam_authz_test'
     OR inet_server_addr() IS DISTINCT FROM '127.0.0.1'::inet
     OR inet_client_addr() IS DISTINCT FROM '127.0.0.1'::inet OR inet_server_port() <> 5432
     OR NOT EXISTS(SELECT 1 FROM pg_database d JOIN pg_roles r ON r.oid=d.datdba
                   WHERE d.datname=current_database() AND r.rolname=current_user)
     OR NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user AND rolcanlogin
                   AND NOT rolsuper AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls)
     OR EXISTS(SELECT 1 FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.member WHERE r.rolname=current_user)
     OR EXISTS(SELECT 1 FROM pg_namespace WHERE nspname NOT IN ('public','information_schema') AND nspname NOT LIKE 'pg\_%' ESCAPE '\')
     OR EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public')
     OR EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public')
     OR EXISTS(SELECT 1 FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace WHERE n.nspname='public')
     OR EXISTS(SELECT 1 FROM pg_collation x JOIN pg_namespace n ON n.oid=x.collnamespace WHERE n.nspname='public')
     OR EXISTS(SELECT 1 FROM pg_conversion x JOIN pg_namespace n ON n.oid=x.connamespace WHERE n.nspname='public')
     OR EXISTS(SELECT 1 FROM pg_operator x JOIN pg_namespace n ON n.oid=x.oprnamespace WHERE n.nspname='public')
     OR EXISTS(SELECT 1 FROM pg_opclass x JOIN pg_namespace n ON n.oid=x.opcnamespace WHERE n.nspname='public')
     OR EXISTS(SELECT 1 FROM pg_opfamily x JOIN pg_namespace n ON n.oid=x.opfnamespace WHERE n.nspname='public')
     OR EXISTS(SELECT 1 FROM pg_ts_config x JOIN pg_namespace n ON n.oid=x.cfgnamespace WHERE n.nspname='public')
     OR EXISTS(SELECT 1 FROM pg_ts_dict x JOIN pg_namespace n ON n.oid=x.dictnamespace WHERE n.nspname='public')
     OR EXISTS(SELECT 1 FROM pg_ts_parser x JOIN pg_namespace n ON n.oid=x.prsnamespace WHERE n.nspname='public')
     OR EXISTS(SELECT 1 FROM pg_ts_template x JOIN pg_namespace n ON n.oid=x.tmplnamespace WHERE n.nspname='public')
     OR EXISTS(SELECT 1 FROM pg_extension WHERE extname<>'plpgsql')
     OR EXISTS(SELECT 1 FROM pg_event_trigger)
     OR EXISTS(SELECT 1 FROM pg_publication)
     OR EXISTS(SELECT 1 FROM pg_foreign_server)
     OR EXISTS(SELECT 1 FROM pg_default_acl)
  THEN RAISE EXCEPTION 'Unsafe or nonempty test target'; END IF;
END $$;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
CREATE TABLE public.authz_sprint3_sentinel(name text PRIMARY KEY CHECK(name='synthetic-sprint3-only'));
INSERT INTO public.authz_sprint3_sentinel VALUES ('synthetic-sprint3-only');
CREATE TABLE public.customers (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, app_id uuid NOT NULL UNIQUE,
  email text NOT NULL, is_active boolean NOT NULL, locked_until timestamptz,
  password_hash text NOT NULL
);
CREATE TABLE public.admin_users (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, app_id uuid NOT NULL UNIQUE,
  username text NOT NULL, email text NOT NULL, role text NOT NULL CHECK(role IN ('Admin','SuperAdmin')),
  is_active boolean NOT NULL, locked_until timestamptz, password_hash text NOT NULL
);
CREATE TABLE public.account_identities (
  id uuid PRIMARY KEY, customer_id bigint UNIQUE REFERENCES public.customers(id),
  admin_id bigint UNIQUE REFERENCES public.admin_users(id), login text NOT NULL,
  auth_state text NOT NULL CHECK(auth_state IN ('Unknown','LegacyOnly','Supabase')),
  supabase_user_id uuid, credential_epoch bigint NOT NULL CHECK(credential_epoch>0),
  credential_sync_state text NOT NULL CHECK(credential_sync_state IN ('Unknown','Ready','Pending','ReconciliationRequired')),
  CHECK ((customer_id IS NOT NULL)::int+(admin_id IS NOT NULL)::int=1)
);
CREATE UNIQUE INDEX authz_sprint3_login ON public.account_identities(lower(btrim(login)));
CREATE UNIQUE INDEX authz_sprint3_subject ON public.account_identities(supabase_user_id) WHERE supabase_user_id IS NOT NULL;
CREATE TABLE public.account_security_generations (
  account_id uuid PRIMARY KEY CHECK(account_id <> '00000000-0000-0000-0000-000000000000'::uuid),
  generation bigint NOT NULL CHECK(generation > 0)
);
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM PUBLIC;
COMMIT;
