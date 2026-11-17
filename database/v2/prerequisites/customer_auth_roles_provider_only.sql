-- PROPOSAL ONLY: separate owner approval AND a provider administrator are required.
-- NEW glam-by-mariya-v2 only. Never run as a SQL Editor workaround.
-- A real superuser creates the backend without the automatic non-superuser
-- creator membership that unchanged 0004 rejects.
-- No password/credential setup, object grants, migration or runtime activation.
BEGIN;
SET LOCAL statement_timeout='30s';
SET LOCAL search_path=pg_catalog;
DO $$
BEGIN
    IF current_user<>'supabase_admin' OR session_user<>'supabase_admin'
       OR NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user AND rolsuper)
       OR current_setting('server_version_num')::integer<170000 THEN
        RAISE EXCEPTION 'Provider superuser required; postgres CREATEROLE alone is not sufficient';
    END IF;
    IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname IN ('glam_v2_backend','glam_v2_authz_owner'))
       OR NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='postgres' AND NOT rolsuper AND rolcreaterole) THEN
        RAISE EXCEPTION 'Expected absent target roles and a separate non-superuser postgres operator';
    END IF;
END $$;
CREATE ROLE glam_v2_backend LOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS
    NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD NULL;
CREATE ROLE glam_v2_authz_owner NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS
    NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD NULL;
-- Only the definer owner is administrable by the later postgres migration operator.
-- No INHERIT or SET capability is enabled at this prerequisite stage.
GRANT glam_v2_authz_owner TO postgres WITH ADMIN TRUE, INHERIT FALSE, SET FALSE;
DO $$
BEGIN
    IF EXISTS(SELECT 1 FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.member OR r.oid=m.roleid
        WHERE r.rolname='glam_v2_backend') THEN
        RAISE EXCEPTION 'Backend role must have no memberships in either direction';
    END IF;
    IF (SELECT count(*) FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.member OR r.oid=m.roleid
        WHERE r.rolname='glam_v2_authz_owner')<>1
       OR NOT EXISTS(SELECT 1 FROM pg_auth_members m
          JOIN pg_roles target ON target.oid=m.roleid JOIN pg_roles actor ON actor.oid=m.member
          JOIN pg_roles grantor ON grantor.oid=m.grantor
          WHERE target.rolname='glam_v2_authz_owner' AND actor.rolname='postgres'
            AND grantor.rolname='supabase_admin' AND m.admin_option AND NOT m.inherit_option AND NOT m.set_option) THEN
        RAISE EXCEPTION 'Unexpected owner membership options or managed extra grants';
    END IF;
    IF (SELECT count(*) FROM pg_roles WHERE rolname IN ('glam_v2_backend','glam_v2_authz_owner')
      AND NOT rolinherit AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb
      AND NOT rolcreaterole AND NOT rolreplication AND rolcanlogin=(rolname='glam_v2_backend'))<>2 THEN
        RAISE EXCEPTION 'Unexpected resulting role attributes';
    END IF;
END $$;
SELECT rolname,rolcanlogin,rolinherit,rolsuper,rolbypassrls,rolcreatedb,rolcreaterole,rolreplication
FROM pg_roles WHERE rolname IN ('glam_v2_backend','glam_v2_authz_owner') ORDER BY rolname;
SELECT target.rolname AS role,actor.rolname AS member,grantor.rolname AS grantor,
    m.admin_option,m.inherit_option,m.set_option
FROM pg_auth_members m JOIN pg_roles target ON target.oid=m.roleid
JOIN pg_roles actor ON actor.oid=m.member JOIN pg_roles grantor ON grantor.oid=m.grantor
WHERE target.rolname IN ('glam_v2_backend','glam_v2_authz_owner');
COMMIT;
