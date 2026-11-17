-- Additive Phase 5A preparation. No data backfill and no authentication policy activation.
-- Existing accounts remain Unknown until their identity is explicitly verified.
SET LOCAL lock_timeout='10s';
SET LOCAL statement_timeout='120s';

ALTER TABLE public.account_identities
 ADD COLUMN IF NOT EXISTS supabase_user_id uuid,
 ADD COLUMN IF NOT EXISTS auth_state varchar(20) NOT NULL DEFAULT 'Unknown';
ALTER TABLE public.account_identities DROP CONSTRAINT IF EXISTS account_identities_auth_state_check;
ALTER TABLE public.account_identities ADD CONSTRAINT account_identities_auth_state_check
 CHECK (auth_state IN ('Unknown','LegacyOnly','Supabase'));
CREATE UNIQUE INDEX IF NOT EXISTS ux_account_identities_supabase_user
 ON public.account_identities(supabase_user_id) WHERE supabase_user_id IS NOT NULL;
