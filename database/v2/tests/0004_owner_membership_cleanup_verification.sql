-- Independently runnable READ-ONLY SELECT after success, rollback or reconnect.
-- No dependency on auth schemas/tables, migration outcome, or another session.
-- Run as the verified postgres operator; all *_ok values must be true.
WITH target_roles AS (
    SELECT * FROM pg_catalog.pg_roles
    WHERE rolname IN ('glam_v2_backend','glam_v2_authz_owner')
), memberships AS (
    SELECT m.*,pg_get_userbyid(m.roleid) AS target_role,
      pg_get_userbyid(m.member) AS member_name,pg_get_userbyid(m.grantor) AS grantor_name
    FROM pg_catalog.pg_auth_members m
    WHERE m.roleid IN (SELECT oid FROM target_roles) OR m.member IN (SELECT oid FROM target_roles)
), checks AS (
    SELECT
      current_user='postgres' AND session_user='postgres'
        AND EXISTS(SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='postgres' AND rolcreaterole AND NOT rolsuper) AS operator_ok,
      (SELECT count(*)=2 AND bool_and(NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb
        AND NOT rolcreaterole AND NOT rolreplication AND NOT rolinherit
        AND rolcanlogin=(rolname='glam_v2_backend')) FROM target_roles) AS restricted_roles_ok,
      EXISTS(SELECT 1 FROM pg_catalog.pg_roles WHERE oid=10 AND rolname='supabase_admin' AND rolsuper) AS bootstrap_ok,
      (SELECT count(*)=2 AND bool_and(member_name='postgres' AND grantor=10
        AND grantor_name='supabase_admin' AND admin_option AND NOT inherit_option AND NOT set_option)
        FROM memberships) AS exactly_bootstrap_rows_ok,
      NOT EXISTS(SELECT 1 FROM memberships WHERE grantor_name='postgres') AS no_temporary_rows_ok,
      NOT pg_has_role('postgres','glam_v2_authz_owner','SET')
        AND NOT pg_has_role('postgres','glam_v2_authz_owner','USAGE')
        AND NOT pg_has_role('postgres','glam_v2_backend','SET')
        AND NOT pg_has_role('postgres','glam_v2_backend','USAGE') AS no_runtime_capability_ok
)
SELECT to_jsonb(c) AS checks,
  (SELECT coalesce(jsonb_agg(to_jsonb(m) ORDER BY target_role,member_name,grantor),'[]'::jsonb) FROM memberships m) AS physical_memberships
FROM checks c;
