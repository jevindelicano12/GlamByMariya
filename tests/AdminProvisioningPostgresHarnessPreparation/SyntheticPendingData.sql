-- Synthetic-only follow-up fixture; intended only after both prerequisite
-- migrations and AdminInvitationProvisioning.sql in a separately approved,
-- freshly isolated database. Not executed in this phase.
DO $$
DECLARE changed integer;
BEGIN
  UPDATE public.account_identities
  SET admin_invitation_id='f217f000-0000-4000-8000-000000000101',
      admin_provisioning_state='Pending'
  WHERE id='f217f000-0000-4000-8000-000000000004'
    AND admin_id IS NOT NULL
    AND admin_provisioning_state='NotApplicable'
    AND supabase_user_id IS NULL
    AND auth_state='Unknown';
  GET DIAGNOSTICS changed = ROW_COUNT;
  IF changed <> 1 THEN
    RAISE EXCEPTION 'Synthetic Pending fixture must update exactly one expected identity';
  END IF;
END $$;
