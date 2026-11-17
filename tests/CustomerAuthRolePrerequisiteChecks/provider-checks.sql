\set ON_ERROR_STOP on
DO $$
BEGIN
    IF current_database()<>'glam_v2_role_prerequisite_test' OR current_user<>'postgres'
       OR session_user<>'postgres' OR inet_server_port()<>55435
       OR inet_server_addr() IS DISTINCT FROM '127.0.0.1'::inet THEN
        RAISE EXCEPTION 'Wrong local provider-role test target';
    END IF;
    IF EXISTS(SELECT 1 FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.member OR r.oid=m.roleid
      WHERE r.rolname IN ('glam_v2_backend','glam_v2_authz_owner')
        AND NOT (m.roleid=(SELECT oid FROM pg_roles WHERE rolname='glam_v2_authz_owner')
          AND m.member=(SELECT oid FROM pg_roles WHERE rolname=current_user))) THEN
        RAISE EXCEPTION 'Provider-provisioned roles do not satisfy the zero-backend-membership alternative';
    END IF;
END $$;
SELECT 'PASS 13 provider-only proposal retains its zero-backend-membership alternative';

DO $$
BEGIN
    IF EXISTS(SELECT 1 FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.member OR r.oid=m.roleid
      WHERE r.rolname='glam_v2_backend') THEN
        RAISE EXCEPTION 'Provider backend must have zero memberships';
    END IF;
    IF pg_has_role('postgres','glam_v2_authz_owner','SET') OR pg_has_role('postgres','glam_v2_authz_owner','USAGE') THEN
        RAISE EXCEPTION 'Prerequisite unexpectedly granted runtime owner capabilities';
    END IF;
    IF EXISTS(SELECT 1 FROM pg_roles api CROSS JOIN pg_roles target
      WHERE api.rolname IN ('anon','authenticated','service_role','authenticator')
        AND target.rolname IN ('glam_v2_backend','glam_v2_authz_owner')
        AND (pg_has_role(api.oid,target.oid,'SET') OR pg_has_role(api.oid,target.oid,'USAGE'))) THEN
        RAISE EXCEPTION 'API can act as a private role';
    END IF;
END $$;
SELECT 'PASS 14 no backend membership or API/operator SET/INHERIT escalation at prerequisite stage';

BEGIN;
GRANT glam_v2_authz_owner TO postgres WITH ADMIN FALSE, INHERIT TRUE, SET TRUE;
CREATE SCHEMA provider_role_probe AUTHORIZATION glam_v2_authz_owner;
CREATE FUNCTION provider_role_probe.owner_probe() RETURNS integer LANGUAGE sql AS 'SELECT 1';
ALTER FUNCTION provider_role_probe.owner_probe() OWNER TO glam_v2_authz_owner;
DO $$
BEGIN
    IF (SELECT pg_get_userbyid(proowner) FROM pg_proc
      WHERE oid='provider_role_probe.owner_probe()'::regprocedure)<>'glam_v2_authz_owner' THEN
        RAISE EXCEPTION 'Provider-created owner role cannot receive function ownership';
    END IF;
END $$;
SELECT 'PASS 15 postgres ADMIN-only membership permits a separate temporary INHERIT/SET grant and ownership transfer';

REVOKE glam_v2_authz_owner FROM postgres GRANTED BY postgres;
DO $$
BEGIN
    IF pg_has_role('postgres','glam_v2_authz_owner','SET') OR pg_has_role('postgres','glam_v2_authz_owner','USAGE')
       OR (SELECT count(*) FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.roleid
         WHERE r.rolname='glam_v2_authz_owner')<>1 THEN
        RAISE EXCEPTION 'Scoped cleanup failed';
    END IF;
END $$;
SELECT 'PASS 16 grantor-specific cleanup removes only the temporary capability, not provider ADMIN-only management';
ROLLBACK;
DO $$
BEGIN
    IF to_regnamespace('provider_role_probe') IS NOT NULL
       OR EXISTS(SELECT 1 FROM pg_class WHERE relnamespace='public'::regnamespace AND relkind='r')
       OR EXISTS(SELECT 1 FROM pg_default_acl WHERE defaclrole=(SELECT oid FROM pg_roles WHERE rolname='postgres')) THEN
        RAISE EXCEPTION 'Unexpected local application object or default-ACL change remains';
    END IF;
END $$;
SELECT 'PASS 17 provider proposal leaves only its two roles and the one ADMIN-only owner membership; no migrations applied';
