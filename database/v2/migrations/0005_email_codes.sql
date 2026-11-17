-- 0005: one-time email codes (Brevo OTP) stored in PostgreSQL instead of process memory,
-- so registration and password-reset codes survive Render restarts and sleeps.
-- Used by PostgresEmailCodeStore (Backend/GlamByMariya.Infrastructure/Services/EmailCodeStore.cs).
-- NOT yet executed anywhere. Phase 2 decides where this runs alongside the other migrations.
-- Safe to re-run: every statement is idempotent.
BEGIN;
SET LOCAL lock_timeout = '10s';
SET LOCAL statement_timeout = '60s';
SET LOCAL search_path = pg_catalog, public;

-- One live code per (purpose, email). Codes are SHA-256 hashes, never plain codes.
-- purpose: 'registration', 'reset', 'reset-ok', or 'account:<user id>'.
-- payload: pending registration profile as JSON, holding only a password HASH.
CREATE TABLE IF NOT EXISTS public.email_codes (
    purpose     text        NOT NULL CHECK (length(purpose) BETWEEN 1 AND 80),
    email       text        NOT NULL CHECK (email = lower(btrim(email)) AND length(email) BETWEEN 3 AND 254),
    code_hash   text        NOT NULL CHECK (length(code_hash) <= 128),
    expires_at  timestamptz NOT NULL,
    attempts    integer     NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 100),
    payload     text        CHECK (payload IS NULL OR length(payload) <= 8000),
    created_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (purpose, email)
);
CREATE INDEX IF NOT EXISTS ix_email_codes_expires ON public.email_codes (expires_at);

-- Resend cooldowns, separate per scope so a reset request never blocks registration.
CREATE TABLE IF NOT EXISTS public.email_code_cooldowns (
    scope     text        NOT NULL CHECK (length(scope) BETWEEN 1 AND 40),
    email     text        NOT NULL CHECK (email = lower(btrim(email)) AND length(email) BETWEEN 3 AND 254),
    until_at  timestamptz NOT NULL,
    PRIMARY KEY (scope, email)
);

-- Server-only tables: no browser (anon/authenticated) access.
ALTER TABLE public.email_codes ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.email_codes FORCE ROW LEVEL SECURITY;
ALTER TABLE public.email_code_cooldowns ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.email_code_cooldowns FORCE ROW LEVEL SECURITY;
REVOKE ALL ON public.email_codes, public.email_code_cooldowns FROM PUBLIC;
DO $$
DECLARE backend_role text;
BEGIN
    FOREACH backend_role IN ARRAY ARRAY['makeup_backend', 'glam_v2_backend'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = backend_role) THEN
            EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON public.email_codes, public.email_code_cooldowns TO %I', backend_role);
            EXECUTE format('DROP POLICY IF EXISTS %I ON public.email_codes', 'email_codes_' || backend_role);
            EXECUTE format('CREATE POLICY %I ON public.email_codes TO %I USING (true) WITH CHECK (true)', 'email_codes_' || backend_role, backend_role);
            EXECUTE format('DROP POLICY IF EXISTS %I ON public.email_code_cooldowns', 'email_code_cooldowns_' || backend_role);
            EXECUTE format('CREATE POLICY %I ON public.email_code_cooldowns TO %I USING (true) WITH CHECK (true)', 'email_code_cooldowns_' || backend_role, backend_role);
        END IF;
    END LOOP;
END $$;

-- Housekeeping: expired rows are harmless (they are ignored), but should be removed.
-- Phase 2 schedules this with pg_cron, e.g. every 15 minutes.
CREATE OR REPLACE FUNCTION public.purge_expired_email_codes() RETURNS integer
LANGUAGE sql SECURITY INVOKER SET search_path = pg_catalog, public AS $$
    WITH codes AS (DELETE FROM public.email_codes WHERE expires_at < now() - interval '1 hour' RETURNING 1),
         cooldowns AS (DELETE FROM public.email_code_cooldowns WHERE until_at < now() RETURNING 1)
    SELECT (SELECT count(*) FROM codes)::integer + (SELECT count(*) FROM cooldowns)::integer
$$;
REVOKE ALL ON FUNCTION public.purge_expired_email_codes() FROM PUBLIC;

COMMIT;
