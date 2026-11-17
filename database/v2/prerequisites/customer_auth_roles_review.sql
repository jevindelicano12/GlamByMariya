-- REVIEW / ROLLBACK-ONLY: NOT approved for hosted execution.
-- HISTORICAL pre-revision predicate demonstration, not the current provisioner.
-- Revised 0004 has an exact bootstrap exception; use the separately approved
-- customer_auth_roles_sql_editor.sql proposal instead. Keep this ROLLBACK-only.
-- No password, object grant, schema, table, migration or application activation.
BEGIN;
SET LOCAL statement_timeout='30s';
SET LOCAL search_path=pg_catalog;
DO $$
BEGIN
    IF current_user<>'postgres' OR session_user<>'postgres'
       OR NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user AND rolcreaterole AND NOT rolsuper)
       OR current_setting('server_version_num')::integer<170000 THEN
        RAISE EXCEPTION 'Requires the separately approved non-superuser postgres operator on PostgreSQL 17+';
    END IF;
    IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname IN ('glam_v2_backend','glam_v2_authz_owner')) THEN
        RAISE EXCEPTION 'Roles already exist; inspect rather than alter or replace them';
    END IF;
    IF current_setting('createrole_self_grant')<>'' THEN
        RAISE EXCEPTION 'Unexpected creator SET/INHERIT self-grant setting; review without changing it';
    END IF;
END $$;

CREATE ROLE glam_v2_backend LOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS
    NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD NULL;
CREATE ROLE glam_v2_authz_owner NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS
    NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD NULL;

DO $$
BEGIN
    IF (SELECT count(*) FROM pg_roles WHERE rolname IN ('glam_v2_backend','glam_v2_authz_owner')
         AND NOT rolinherit AND NOT rolsuper AND NOT rolbypassrls
         AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication
         AND rolcanlogin=(rolname='glam_v2_backend'))<>2 THEN
        RAISE EXCEPTION 'Unexpected resulting role attributes';
    END IF;
    IF (SELECT count(*) FROM pg_auth_members m
         JOIN pg_roles target ON target.oid=m.roleid
         JOIN pg_roles actor ON actor.oid=m.member
         JOIN pg_roles grantor ON grantor.oid=m.grantor
         WHERE target.rolname IN ('glam_v2_backend','glam_v2_authz_owner')
           AND actor.rolname='postgres' AND grantor.rolname='supabase_admin' AND grantor.rolsuper
           AND m.admin_option AND NOT m.inherit_option AND NOT m.set_option)<>2
       OR (SELECT count(*) FROM pg_auth_members m JOIN pg_roles r
           ON r.oid=m.member OR r.oid=m.roleid
           WHERE r.rolname IN ('glam_v2_backend','glam_v2_authz_owner'))<>2 THEN
        RAISE EXCEPTION 'Unexpected managed creator memberships; stop and inspect, never broaden grants';
    END IF;
END $$;

SELECT rolname,rolcanlogin,rolinherit,rolsuper,rolbypassrls,rolcreatedb,rolcreaterole,rolreplication
FROM pg_roles WHERE rolname IN ('glam_v2_backend','glam_v2_authz_owner') ORDER BY rolname;
SELECT target.rolname AS role,actor.rolname AS member,grantor.rolname AS grantor,
       m.admin_option,m.inherit_option,m.set_option
FROM pg_auth_members m JOIN pg_roles target ON target.oid=m.roleid
JOIN pg_roles actor ON actor.oid=m.member JOIN pg_roles grantor ON grantor.oid=m.grantor
WHERE target.rolname IN ('glam_v2_backend','glam_v2_authz_owner')
   OR actor.rolname IN ('glam_v2_backend','glam_v2_authz_owner');
SELECT
    pg_has_role('postgres','glam_v2_backend','USAGE') AS operator_inherits_backend,
    pg_has_role('postgres','glam_v2_backend','SET') AS operator_can_set_backend,
    pg_has_role('postgres','glam_v2_authz_owner','USAGE') AS operator_inherits_owner,
    pg_has_role('postgres','glam_v2_authz_owner','SET') AS operator_can_set_owner,
    EXISTS (SELECT 1 FROM pg_auth_members m JOIN pg_roles r
      ON r.oid=m.member OR r.oid=m.roleid
      WHERE r.rolname IN ('glam_v2_backend','glam_v2_authz_owner')
        AND NOT (m.roleid=(SELECT oid FROM pg_roles WHERE rolname='glam_v2_authz_owner')
          AND m.member=(SELECT oid FROM pg_roles WHERE rolname=current_user)))
    AS blocked_by_pre_revision_0004_membership_guard;
ROLLBACK;
