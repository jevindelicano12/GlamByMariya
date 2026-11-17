-- EF migration payload; EF owns the transaction. Local preparation only, not
-- evidence of deployment or application.
-- Additive metadata only. Existing identities receive the safe NotApplicable default.
-- Prerequisites are provided by PublicIntegration, RolesAndInvitations, and
-- 20261019160000_AuthIdentityTracking in migration-ID order.
SET LOCAL lock_timeout='10s';
SET LOCAL statement_timeout='120s';

DO $$
BEGIN
 IF to_regclass('public.account_identities') IS NULL
    OR to_regclass('public.admin_invitations') IS NULL THEN
  RAISE EXCEPTION 'Admin invitation provisioning requires public.account_identities and public.admin_invitations';
 END IF;
 IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='account_identities' AND column_name='admin_id')
    OR NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='account_identities' AND column_name='supabase_user_id')
    OR NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='account_identities' AND column_name='auth_state')
    OR NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='admin_invitations' AND column_name='id') THEN
  RAISE EXCEPTION 'Admin invitation provisioning prerequisites are incomplete; apply and verify earlier migrations first';
 END IF;
END $$;

ALTER TABLE public.account_identities
 ADD COLUMN IF NOT EXISTS admin_provisioning_state varchar(20) NOT NULL DEFAULT 'NotApplicable',
 ADD COLUMN IF NOT EXISTS admin_invitation_id uuid;

DO $$
BEGIN
 IF NOT EXISTS (
  SELECT 1 FROM information_schema.columns
  WHERE table_schema='public' AND table_name='account_identities'
    AND column_name='admin_provisioning_state' AND data_type='character varying'
    AND character_maximum_length=20 AND is_nullable='NO'
    AND column_default LIKE '%NotApplicable%'
 ) OR NOT EXISTS (
  SELECT 1 FROM information_schema.columns
  WHERE table_schema='public' AND table_name='account_identities'
    AND column_name='admin_invitation_id' AND data_type='uuid' AND is_nullable='YES'
 ) THEN
  RAISE EXCEPTION 'Admin invitation provisioning columns do not match the reviewed definitions';
 END IF;
END $$;

DO $$
BEGIN
 IF NOT EXISTS (
  SELECT 1 FROM pg_constraint
  WHERE conrelid='public.account_identities'::regclass
    AND conname='account_identities_admin_invitation_fkey'
 ) THEN
  ALTER TABLE public.account_identities
   ADD CONSTRAINT account_identities_admin_invitation_fkey
   FOREIGN KEY (admin_invitation_id) REFERENCES public.admin_invitations(id)
   ON DELETE RESTRICT NOT VALID;
 END IF;
END $$;
ALTER TABLE public.account_identities VALIDATE CONSTRAINT account_identities_admin_invitation_fkey;

DO $$
BEGIN
 IF NOT EXISTS (
  SELECT 1 FROM pg_constraint
  WHERE conrelid='public.account_identities'::regclass
    AND conname='account_identities_admin_provisioning_state_check'
 ) THEN
  ALTER TABLE public.account_identities
   ADD CONSTRAINT account_identities_admin_provisioning_state_check
   CHECK (admin_provisioning_state IN
    ('NotApplicable','Pending','NotAttempted','Rejected','Unknown','Confirmed')) NOT VALID;
 END IF;
END $$;
ALTER TABLE public.account_identities VALIDATE CONSTRAINT account_identities_admin_provisioning_state_check;

DO $$
BEGIN
 IF NOT EXISTS (
  SELECT 1 FROM pg_constraint
  WHERE conrelid='public.account_identities'::regclass
    AND conname='account_identities_admin_provisioning_correlation_check'
 ) THEN
  ALTER TABLE public.account_identities
   ADD CONSTRAINT account_identities_admin_provisioning_correlation_check
   CHECK (
    (admin_provisioning_state='NotApplicable' AND admin_invitation_id IS NULL)
    OR (admin_provisioning_state<>'NotApplicable' AND admin_id IS NOT NULL
        AND admin_invitation_id IS NOT NULL)
   ) NOT VALID;
 END IF;
END $$;
ALTER TABLE public.account_identities VALIDATE CONSTRAINT account_identities_admin_provisioning_correlation_check;

DO $$
BEGIN
 IF NOT EXISTS (
  SELECT 1 FROM pg_constraint
  WHERE conrelid='public.account_identities'::regclass
    AND conname='account_identities_admin_confirmed_link_check'
 ) THEN
  ALTER TABLE public.account_identities
   ADD CONSTRAINT account_identities_admin_confirmed_link_check
   CHECK (admin_provisioning_state<>'Confirmed'
       OR (supabase_user_id IS NOT NULL AND auth_state='Supabase')) NOT VALID;
 END IF;
END $$;
ALTER TABLE public.account_identities VALIDATE CONSTRAINT account_identities_admin_confirmed_link_check;

-- One consumed invitation may correlate to at most one local identity.
-- The provider-subject unique index already belongs to AuthIdentityTracking.sql;
-- do not recreate it here.
CREATE UNIQUE INDEX IF NOT EXISTS ux_account_identities_admin_invitation
 ON public.account_identities(admin_invitation_id)
 WHERE admin_invitation_id IS NOT NULL;
