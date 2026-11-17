-- Roles (SuperAdmin) + administrator invitations. Additive only; no drops, no rewrites.
-- Safe to run repeatedly (IF NOT EXISTS / OR REPLACE).

-- 1. Third role in the read projection. UUID ...003 is new; ...001/002 unchanged.
CREATE OR REPLACE VIEW public.mvc_roles WITH(security_invoker=true) AS
 SELECT '10000000-0000-0000-0000-000000000001'::uuid AS id,'Admin'::text AS name UNION ALL
 SELECT '10000000-0000-0000-0000-000000000002'::uuid,'Customer'::text UNION ALL
 SELECT '10000000-0000-0000-0000-000000000003'::uuid,'SuperAdmin'::text;

-- 2. Map admin_users.role to the matching role id. Existing 'Admin' rows unchanged.
CREATE OR REPLACE VIEW public.mvc_users WITH(security_invoker=true) AS
 SELECT c.app_id AS id,'10000000-0000-0000-0000-000000000002'::uuid AS role_id,coalesce(c.first_name,c.full_name)::text AS first_name,
 coalesce(c.last_name,'')::text AS last_name,coalesce(c.age_at_registration,0)::smallint AS age_at_registration,
 coalesce(c.gender,'')::text AS gender,coalesce(c.address,'')::text AS address,coalesce(c.email,'')::text AS email,
 coalesce(c.password_hash,'')::text AS password_hash,c.is_active,c.security_stamp,c.locked_until FROM public.customers c
 UNION ALL SELECT u.app_id,
 CASE WHEN u.role='SuperAdmin' THEN '10000000-0000-0000-0000-000000000003'::uuid ELSE '10000000-0000-0000-0000-000000000001'::uuid END,
 coalesce(u.first_name,u.username)::text,coalesce(u.last_name,'')::text,
 coalesce(u.age_at_registration,0)::smallint,coalesce(u.gender,'')::text,coalesce(u.address,'')::text,coalesce(u.email,u.username)::text,
 u.password_hash,u.is_active,u.security_stamp,u.locked_until FROM public.admin_users u;

-- 3. SuperAdmin satisfies Admin-gated triggers (superset). New explicit SuperAdmin branch.
CREATE OR REPLACE FUNCTION public.mvc_assert_role(who uuid,wanted text) RETURNS void LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
BEGIN
 IF wanted='SuperAdmin' AND EXISTS(SELECT 1 FROM public.admin_users WHERE app_id=who AND role='SuperAdmin' AND is_active) THEN RETURN; END IF;
 IF wanted='Admin' AND EXISTS(SELECT 1 FROM public.admin_users WHERE app_id=who AND is_active) THEN RETURN; END IF;
 IF wanted='Customer' AND EXISTS(SELECT 1 FROM public.customers WHERE app_id=who AND is_active) THEN RETURN; END IF;
 RAISE EXCEPTION 'An active % account is required',wanted;
END $$;

-- 4. Administrator invitations. Tokens are SHA-256 hashes; plaintext lives only in email.
CREATE TABLE IF NOT EXISTS public.admin_invitations(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 email text NOT NULL,
 token_hash text NOT NULL UNIQUE,
 invited_by uuid REFERENCES public.account_identities(id),
 expires_at timestamptz NOT NULL,
 accepted_at timestamptz,
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now());
CREATE INDEX IF NOT EXISTS ix_mvc_invitations_email ON public.admin_invitations(lower(btrim(email)));
ALTER TABLE public.admin_invitations ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON public.admin_invitations FROM anon, authenticated;
DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename='admin_invitations' AND policyname='trusted_mvc_backend') THEN
  CREATE POLICY trusted_mvc_backend ON public.admin_invitations TO makeup_backend USING(true) WITH CHECK(true);
 END IF;
END $$;
DROP TRIGGER IF EXISTS mvc_touch ON public.admin_invitations;
CREATE TRIGGER mvc_touch BEFORE UPDATE ON public.admin_invitations FOR EACH ROW EXECUTE FUNCTION public.mvc_touch();
DROP TRIGGER IF EXISTS mvc_audit ON public.admin_invitations;
CREATE TRIGGER mvc_audit AFTER INSERT OR UPDATE OR DELETE ON public.admin_invitations FOR EACH ROW EXECUTE FUNCTION public.mvc_audit();
