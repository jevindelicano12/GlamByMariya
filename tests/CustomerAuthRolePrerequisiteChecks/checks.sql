-- LOCAL ONLY. The PowerShell runner initializes and independently guards a fresh cluster.
\set ON_ERROR_STOP on
DO $$
BEGIN
    IF current_database()<>'glam_v2_role_prerequisite_test'
       OR current_user<>'postgres' OR session_user<>'postgres'
       OR inet_server_addr() IS DISTINCT FROM '127.0.0.1'::inet
       OR inet_server_port()<>55435
       OR current_setting('server_version_num')::integer NOT BETWEEN 180000 AND 189999
       OR NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='postgres' AND rolcreaterole AND NOT rolsuper) THEN
        RAISE EXCEPTION 'Refusing unexpected role-test target';
    END IF;
END $$;
\ir ../../database/v2/prerequisites/customer_auth_roles_review.sql
DO $$
BEGIN
    IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname IN ('glam_v2_backend','glam_v2_authz_owner')) THEN
        RAISE EXCEPTION 'Review script did not roll back its roles';
    END IF;
END $$;
SELECT 'PASS 01 exact review script executes as non-superuser and leaves no roles';

BEGIN;
CREATE ROLE glam_v2_backend LOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS
    NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD NULL;
CREATE ROLE glam_v2_authz_owner NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS
    NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD NULL;
DO $$
BEGIN
    IF (SELECT count(*) FROM pg_roles WHERE rolname IN ('glam_v2_backend','glam_v2_authz_owner')
      AND NOT rolinherit AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb
      AND NOT rolcreaterole AND NOT rolreplication AND rolcanlogin=(rolname='glam_v2_backend'))<>2 THEN
        RAISE EXCEPTION 'Role attributes wrong';
    END IF;
    IF pg_has_role('postgres','glam_v2_backend','SET') OR pg_has_role('postgres','glam_v2_backend','USAGE')
       OR pg_has_role('postgres','glam_v2_authz_owner','SET') OR pg_has_role('postgres','glam_v2_authz_owner','USAGE') THEN
        RAISE EXCEPTION 'Creator has unintended SET/INHERIT access';
    END IF;
END $$;
SELECT 'PASS 02 restricted role attributes; ADMIN-only does not grant SET/INHERIT';

REVOKE glam_v2_backend FROM postgres;
DO $$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.roleid
      JOIN pg_roles g ON g.oid=m.grantor WHERE r.rolname='glam_v2_backend'
      AND g.rolname='supabase_admin' AND m.admin_option AND NOT m.set_option AND NOT m.inherit_option) THEN
        RAISE EXCEPTION 'Expected bootstrap creator grant to survive ordinary REVOKE';
    END IF;
    BEGIN
        REVOKE glam_v2_backend FROM postgres GRANTED BY supabase_admin;
        RAISE EXCEPTION 'Non-superuser unexpectedly revoked bootstrap grant';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
END $$;
SELECT 'PASS 03 ordinary REVOKE leaves bootstrap grant; explicit grantor impersonation is denied';

DO $$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.member OR r.oid=m.roleid
      WHERE r.rolname IN ('glam_v2_backend','glam_v2_authz_owner')
        AND NOT (m.roleid=(SELECT oid FROM pg_roles WHERE rolname='glam_v2_authz_owner')
          AND m.member=(SELECT oid FROM pg_roles WHERE rolname=current_user))) THEN
        RAISE EXCEPTION 'Expected pre-revision predicate to reject the backend creator membership';
    END IF;
END $$;
SELECT 'PASS 04 historical pre-revision membership predicate rejects creator rows';

CREATE SCHEMA role_prereq_probe;
ALTER DEFAULT PRIVILEGES IN SCHEMA role_prereq_probe GRANT EXECUTE ON FUNCTIONS TO anon,authenticated,service_role;
CREATE FUNCTION role_prereq_probe.owner_probe() RETURNS integer LANGUAGE sql AS 'SELECT 1';
DO $$
BEGIN
    BEGIN
        ALTER FUNCTION role_prereq_probe.owner_probe() OWNER TO glam_v2_authz_owner;
        RAISE EXCEPTION 'Ownership transfer without SET capability unexpectedly succeeded';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
END $$;
SELECT 'PASS 05 ownership transfer needs SET capability, not only ADMIN';

GRANT glam_v2_authz_owner TO postgres WITH ADMIN FALSE, INHERIT TRUE, SET TRUE;
GRANT USAGE,CREATE ON SCHEMA role_prereq_probe TO glam_v2_authz_owner;
ALTER FUNCTION role_prereq_probe.owner_probe() OWNER TO glam_v2_authz_owner;
DO $$
BEGIN
    IF NOT pg_has_role('postgres','glam_v2_authz_owner','SET')
       OR NOT pg_has_role('postgres','glam_v2_authz_owner','USAGE')
       OR (SELECT pg_get_userbyid(proowner) FROM pg_proc
           WHERE oid='role_prereq_probe.owner_probe()'::regprocedure)<>'glam_v2_authz_owner'
       OR NOT has_function_privilege('authenticated','role_prereq_probe.owner_probe()','EXECUTE')
       OR NOT EXISTS(SELECT 1 FROM pg_proc p,
         LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
         WHERE p.oid='role_prereq_probe.owner_probe()'::regprocedure AND a.grantee=0 AND a.privilege_type='EXECUTE') THEN
        RAISE EXCEPTION 'Scoped owner transfer failed';
    END IF;
END $$;
REVOKE ALL ON FUNCTION role_prereq_probe.owner_probe() FROM PUBLIC,anon,authenticated,service_role;
DO $$
BEGIN
    IF has_function_privilege('authenticated','role_prereq_probe.owner_probe()','EXECUTE') THEN
        RAISE EXCEPTION 'Post-transfer function ACL revocation failed';
    END IF;
END $$;
SELECT 'PASS 06 owner transfer preserves creator/PUBLIC ACLs until explicit function revocation';

REVOKE glam_v2_authz_owner FROM postgres GRANTED BY postgres;
DO $$
BEGIN
    IF pg_has_role('postgres','glam_v2_authz_owner','SET')
       OR pg_has_role('postgres','glam_v2_authz_owner','USAGE')
       OR (SELECT count(*) FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.roleid
          WHERE r.rolname='glam_v2_authz_owner')<>1 THEN
        RAISE EXCEPTION 'Temporary grant cleanup failed or bootstrap row disappeared';
    END IF;
END $$;
SELECT 'PASS 07 cleanup removes only operator grant; bootstrap ADMIN-only row remains';

ALTER DEFAULT PRIVILEGES IN SCHEMA role_prereq_probe GRANT ALL ON TABLES TO anon,authenticated,service_role;
CREATE TABLE role_prereq_probe.default_acl_probe(id integer);
DO $$
BEGIN
    IF NOT has_table_privilege('authenticated','role_prereq_probe.default_acl_probe','SELECT') THEN
        RAISE EXCEPTION 'Creator-default simulation failed';
    END IF;
END $$;
SELECT 'PASS 08 new objects receive their creator defaults, not their future owner defaults';

REVOKE ALL ON TABLE role_prereq_probe.default_acl_probe FROM anon,authenticated,service_role,PUBLIC;
DO $$
BEGIN
    IF has_table_privilege('authenticated','role_prereq_probe.default_acl_probe','SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,MAINTAIN')
       OR has_table_privilege('service_role','role_prereq_probe.default_acl_probe','SELECT') THEN
        RAISE EXCEPTION 'Object-specific revocation did not clear API ACLs';
    END IF;
END $$;
SELECT 'PASS 09 object-specific revocation clears API grants without changing managed defaults';

GRANT SELECT ON role_prereq_probe.default_acl_probe TO PUBLIC;
DO $$
BEGIN
    IF NOT has_table_privilege('glam_v2_backend','role_prereq_probe.default_acl_probe','SELECT') THEN
        RAISE EXCEPTION 'PUBLIC privilege inheritance simulation failed';
    END IF;
END $$;
SELECT 'PASS 10 NOINHERIT does not cancel PUBLIC object grants';

DO $$
BEGIN
    IF EXISTS(SELECT 1 FROM pg_auth_members m JOIN pg_roles t ON t.oid=m.roleid
        JOIN pg_roles a ON a.oid=m.member
        WHERE (t.rolname IN ('glam_v2_backend','glam_v2_authz_owner')
               AND a.rolname IN ('anon','authenticated','service_role','authenticator'))
          OR (a.rolname IN ('glam_v2_backend','glam_v2_authz_owner'))) THEN
        RAISE EXCEPTION 'Runtime/API memberships must not be introduced';
    END IF;
END $$;
SELECT 'PASS 11 no API role receives target membership and target roles join no other roles';
ROLLBACK;
DO $$
BEGIN
    IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname IN ('glam_v2_backend','glam_v2_authz_owner'))
       OR to_regnamespace('role_prereq_probe') IS NOT NULL THEN
        RAISE EXCEPTION 'Local test rollback incomplete';
    END IF;
END $$;
SELECT 'PASS 12 role/object/default-ACL tests roll back without applying 0004 or 0003';
