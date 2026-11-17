-- PROPOSAL ONLY: separately approved NEW glam-by-mariya-v2 role provisioning.
-- Requires locally revised 0004 (revision 2); does NOT execute that migration.
-- No passwords, object grants, API memberships or runtime activation.
BEGIN;
SET LOCAL statement_timeout='30s';
SET LOCAL search_path=pg_catalog;
DO $$
BEGIN
    IF current_user<>'postgres' OR session_user<>'postgres'
       OR NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user AND rolcreaterole AND NOT rolsuper)
       OR NOT EXISTS(SELECT 1 FROM pg_roles WHERE oid=10 AND rolname='supabase_admin' AND rolsuper)
       OR current_setting('server_version_num')::integer<170000 THEN
        RAISE EXCEPTION 'Expected PostgreSQL 17+ postgres operator and genuine Supabase bootstrap grantor';
    END IF;
    IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname IN ('glam_v2_backend','glam_v2_authz_owner')) THEN
        RAISE EXCEPTION 'Target roles already exist; inspect rather than alter or replace';
    END IF;
    IF current_setting('createrole_self_grant')<>'' THEN
        RAISE EXCEPTION 'Creator self-grant setting enables extra capabilities; review without changing it';
    END IF;
    IF EXISTS (
        WITH RECURSIVE reachable(api_role,role_id) AS (
            SELECT oid,oid FROM pg_roles WHERE rolname IN ('anon','authenticated','service_role','authenticator')
            UNION
            SELECT p.api_role,m.roleid FROM reachable p JOIN pg_auth_members m ON m.member=p.role_id
            WHERE m.inherit_option OR m.set_option OR m.admin_option
        )
        SELECT 1 FROM reachable p JOIN pg_roles target ON target.oid=p.role_id WHERE target.rolname='postgres'
    ) OR EXISTS (
        SELECT 1 FROM pg_roles WHERE rolname IN ('anon','authenticated','service_role','authenticator')
          AND (rolsuper OR rolcreaterole)
    ) THEN
        RAISE EXCEPTION 'API can reach or administer postgres; refusing role provisioning';
    END IF;
END $$;
CREATE ROLE glam_v2_backend LOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS
    NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD NULL;
CREATE ROLE glam_v2_authz_owner NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS
    NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD NULL;
DO $$
BEGIN
    IF (SELECT count(*) FROM pg_roles WHERE rolname IN ('glam_v2_backend','glam_v2_authz_owner')
        AND NOT rolinherit AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb
        AND NOT rolcreaterole AND NOT rolreplication AND rolcanlogin=(rolname='glam_v2_backend'))<>2 THEN
        RAISE EXCEPTION 'Unexpected resulting attributes';
    END IF;
    IF (SELECT count(*) FROM pg_auth_members m
        JOIN pg_roles target ON target.oid=m.roleid JOIN pg_roles actor ON actor.oid=m.member
        WHERE target.rolname IN ('glam_v2_backend','glam_v2_authz_owner') AND actor.rolname='postgres'
          AND m.grantor=10 AND m.admin_option AND NOT m.inherit_option AND NOT m.set_option)<>2
       OR (SELECT count(*) FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.member OR r.oid=m.roleid
          WHERE r.rolname IN ('glam_v2_backend','glam_v2_authz_owner'))<>2 THEN
        RAISE EXCEPTION 'Unexpected creator grantor/options or additional membership';
    END IF;
END $$;
SELECT r.rolname,r.rolcanlogin,r.rolinherit,r.rolsuper,r.rolbypassrls,r.rolcreatedb,r.rolcreaterole,r.rolreplication
FROM pg_roles r WHERE r.rolname IN ('glam_v2_backend','glam_v2_authz_owner') ORDER BY r.rolname;
SELECT target.rolname AS role,actor.rolname AS member,grantor.rolname AS grantor,
    m.admin_option,m.inherit_option,m.set_option
FROM pg_auth_members m JOIN pg_roles target ON target.oid=m.roleid
JOIN pg_roles actor ON actor.oid=m.member JOIN pg_roles grantor ON grantor.oid=m.grantor
WHERE target.rolname IN ('glam_v2_backend','glam_v2_authz_owner');
COMMIT;
