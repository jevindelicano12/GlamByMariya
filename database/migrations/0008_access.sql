-- Nothing in public is reachable through Supabase's public API (roles anon and
-- authenticated). The app reaches the data as its own login role, a member of
-- makeup_backend (created in 0002 and trusted by the RLS policies there). That login
-- carries a password, so the owner creates it on the project, not in a migration.
DO $$
DECLARE r text;
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'makeup_backend') THEN
    CREATE ROLE makeup_backend NOLOGIN NOBYPASSRLS;
  END IF;
  FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
      EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA public FROM %I', r);
      EXECUTE format('REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM %I', r);
      EXECUTE format('REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM %I', r);
      EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM %I', r);
      EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON SEQUENCES FROM %I', r);
      EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON FUNCTIONS FROM %I', r);
    END IF;
  END LOOP;
END $$;
-- Functions are executable by PUBLIC by default, which includes anon and authenticated.
-- The default for future functions is global (not per schema), so it is revoked without
-- IN SCHEMA, for the role that runs migrations. Each later migration grants makeup_backend
-- EXECUTE on the functions the app calls.
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC;
ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO makeup_backend;

-- Tables created after 0002's grant-and-policy loop: the app login (makeup_backend)
-- needs them too. Data Protection keys are read and written by the app at runtime.
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['account_preferences', 'admin_invitations', 'data_protection_keys'] LOOP
    EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON public.%I TO makeup_backend', t);
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE schemaname = 'public' AND tablename = t
                   AND 'makeup_backend' = ANY (roles)) THEN
      EXECUTE format('CREATE POLICY mvc_backend ON public.%I TO makeup_backend USING (true) WITH CHECK (true)', t);
    END IF;
  END LOOP;
END $$;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO makeup_backend;
-- Supabase's helper flagged by the security advisor (SECURITY DEFINER, callable by anon).
DO $$
BEGIN
  IF to_regprocedure('public.rls_auto_enable()') IS NOT NULL THEN
    REVOKE EXECUTE ON FUNCTION public.rls_auto_enable() FROM PUBLIC, anon, authenticated;
  END IF;
END $$;
