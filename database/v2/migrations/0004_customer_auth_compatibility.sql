-- Customer-auth compatibility; NOT runtime activation or hosted execution approval.
-- Explicit dependencies: 0001 + 0002. 0003 is payment-only and MUST NOT be run here.
-- One-shot, transactional: an occupied/incompatible target is rejected, never reset.
-- Pre-provision restricted glam_v2_backend LOGIN and glam_v2_authz_owner NOLOGIN.
-- Local revision 2: recognize only Supabase's bootstrap ADMIN-only creator row.
BEGIN;
SET LOCAL lock_timeout = '10s';
SET LOCAL statement_timeout = '120s';
SET LOCAL search_path = pg_catalog, public;
SELECT pg_advisory_xact_lock(741104, 4);

DO $$
DECLARE role_record record;
BEGIN
    IF (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public' AND c.relname IN
          ('makeup_artists','services','artist_availability','blocked_slots','customers','reservations')
          AND c.relkind='r' AND c.relrowsecurity AND c.relforcerowsecurity) <> 6
       OR NOT EXISTS (SELECT 1 FROM pg_constraint
          WHERE conrelid='public.reservations'::regclass
            AND conname='reservations_artist_reserved_overlap' AND contype='x') THEN
        RAISE EXCEPTION '0004 requires the forced-RLS 0001/0002 foundation and reservation exclusion';
    END IF;
    IF to_regclass('public.admin_users') IS NOT NULL
       OR to_regclass('public.account_identities') IS NOT NULL
       OR to_regclass('public.account_security_generations') IS NOT NULL
       OR to_regclass('public.auth_mvc_signup_attempts') IS NOT NULL
       OR to_regnamespace('v2_authz_private') IS NOT NULL
       OR to_regnamespace('v2_authz_api') IS NOT NULL
       OR EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public'
                    AND table_name='customers' AND column_name='app_id') THEN
        RAISE EXCEPTION '0004 already applied or conflicting auth objects; do not rerun or overwrite';
    END IF;
    -- Lock before checking occupancy: no concurrent foundation insert can escape preflight.
    LOCK TABLE public.customers IN ACCESS EXCLUSIVE MODE;
    IF EXISTS (SELECT 1 FROM public.customers) THEN
        RAISE EXCEPTION '0004 requires empty foundation customers; existing data needs a separately reviewed mapping migration';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_policy WHERE polrelid='public.customers'::regclass) THEN
        RAISE EXCEPTION 'Unexpected Customer policies require review before 0004';
    END IF;
    IF (SELECT count(*) FROM pg_roles WHERE rolname IN ('anon','authenticated','service_role'))<>3 THEN
        RAISE EXCEPTION 'Supabase API roles are required';
    END IF;
    FOR role_record IN SELECT * FROM pg_roles WHERE rolname IN ('glam_v2_backend','glam_v2_authz_owner') LOOP
        IF role_record.rolsuper OR role_record.rolbypassrls OR role_record.rolcreatedb OR role_record.rolcreaterole OR role_record.rolreplication
           OR role_record.rolinherit OR role_record.rolcanlogin <> (role_record.rolname='glam_v2_backend') THEN
            RAISE EXCEPTION 'Invalid restricted authentication role: %', role_record.rolname;
        END IF;
    END LOOP;
    IF (SELECT count(*) FROM pg_roles WHERE rolname IN ('glam_v2_backend','glam_v2_authz_owner'))<>2
       OR EXISTS (SELECT 1 FROM pg_auth_members m JOIN pg_roles r
         ON r.oid=m.member OR r.oid=m.roleid
          WHERE r.rolname IN ('glam_v2_backend','glam_v2_authz_owner')
            AND NOT coalesce((
              (m.roleid=(SELECT oid FROM pg_roles WHERE rolname='glam_v2_authz_owner')
                AND m.member=(SELECT oid FROM pg_roles WHERE rolname=current_user))
              OR (m.roleid=(SELECT oid FROM pg_roles WHERE rolname='glam_v2_backend')
                AND m.member=(SELECT oid FROM pg_roles WHERE rolname='postgres' AND rolcreaterole AND NOT rolsuper)
                AND m.grantor=(SELECT oid FROM pg_roles WHERE oid=10 AND rolname='supabase_admin' AND rolsuper)
                AND m.admin_option AND NOT m.inherit_option AND NOT m.set_option)),false)) THEN
        RAISE EXCEPTION 'Unexpected auth membership: backend permits only bootstrap ADMIN-only postgres management';
    END IF;
    -- ADMIN can self-grant later: postgres remains a trusted administrator, not
    -- a sandboxed actor. No API role may inherit, SET, or administer that trust.
    IF EXISTS (
        WITH RECURSIVE reachable(api_role,role_id) AS (
            SELECT oid,oid FROM pg_roles WHERE rolname IN ('anon','authenticated','service_role','authenticator')
            UNION
            SELECT p.api_role,m.roleid FROM reachable p JOIN pg_auth_members m ON m.member=p.role_id
            WHERE m.inherit_option OR m.set_option OR m.admin_option
        )
        SELECT 1 FROM reachable p JOIN pg_roles target ON target.oid=p.role_id
        WHERE target.rolname IN ('postgres','glam_v2_backend','glam_v2_authz_owner')
    ) OR EXISTS (
        SELECT 1 FROM pg_roles WHERE rolname IN ('anon','authenticated','service_role','authenticator')
          AND (rolsuper OR rolcreaterole)
    ) THEN
        RAISE EXCEPTION 'API role can reach or administer a trusted auth/migration role';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_class WHERE oid='public.customers'::regclass
        AND relowner IN (SELECT oid FROM pg_roles WHERE rolname IN
          ('glam_v2_backend','glam_v2_authz_owner','anon','authenticated','service_role'))) THEN
        RAISE EXCEPTION 'Private authentication tables require a separate migration owner';
    END IF;
END $$;

ALTER TABLE public.customers
    ALTER COLUMN email SET NOT NULL,
    ADD COLUMN app_id uuid NOT NULL CHECK(app_id<>'00000000-0000-0000-0000-000000000000'),
    ADD COLUMN is_active boolean NOT NULL DEFAULT true,
    ADD COLUMN locked_until timestamptz,
    ADD COLUMN first_name varchar(50) NOT NULL CHECK(btrim(first_name)<>''),
    ADD COLUMN last_name varchar(50) NOT NULL CHECK(btrim(last_name)<>''),
    ADD COLUMN age_at_registration smallint NOT NULL CHECK(age_at_registration>=0),
    ADD COLUMN gender text NOT NULL CHECK(gender IN ('Male','Female','Others')),
    ADD COLUMN address varchar(500) NOT NULL CHECK(char_length(btrim(address)) BETWEEN 8 AND 500),
    ADD COLUMN password_hash text NOT NULL CHECK(password_hash<>''),
    ADD COLUMN security_stamp uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD COLUMN birthday date NOT NULL CHECK(birthday>=DATE '0001-01-01'
        AND birthday<=(statement_timestamp() AT TIME ZONE 'Asia/Manila')::date),
    ADD COLUMN suffix varchar(10) CHECK(suffix IN ('','Jr.','Sr.','II','III','IV')),
    ADD CONSTRAINT customers_auth_email_check CHECK(email=btrim(email) AND email<>''),
    ADD CONSTRAINT customers_app_id_key UNIQUE(app_id),
    ADD CONSTRAINT customers_app_identity_key UNIQUE(id,app_id);
CREATE UNIQUE INDEX customers_auth_email_key ON public.customers(lower(btrim(email)));

-- No bootstrap, seed, staff provisioning API, or runtime staff INSERT grant.
CREATE TABLE public.admin_users (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    app_id uuid NOT NULL UNIQUE CHECK(app_id<>'00000000-0000-0000-0000-000000000000'),
    username varchar(254) NOT NULL CHECK(btrim(username)<>''),
    email varchar(254) NOT NULL CHECK(email=btrim(email) AND email<>''),
    role varchar(20) NOT NULL CHECK(role IN ('Admin','SuperAdmin')),
    is_active boolean NOT NULL DEFAULT false,
    locked_until timestamptz,
    password_hash text NOT NULL DEFAULT '',
    security_stamp uuid NOT NULL DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT admin_users_app_identity_key UNIQUE(id,app_id)
);
CREATE UNIQUE INDEX admin_users_auth_email_key ON public.admin_users(lower(btrim(email)));
CREATE TABLE public.account_identities (
    id uuid PRIMARY KEY,
    customer_id bigint UNIQUE,
    admin_id bigint UNIQUE,
    login text NOT NULL CHECK(login=lower(btrim(login)) AND login<>''),
    auth_state text NOT NULL DEFAULT 'Unknown' CHECK(auth_state IN ('Unknown','Supabase')),
    supabase_user_id uuid CHECK(supabase_user_id<>'00000000-0000-0000-0000-000000000000'),
    credential_epoch bigint NOT NULL DEFAULT 1 CHECK(credential_epoch>0),
    credential_sync_state text NOT NULL DEFAULT 'Unknown'
        CHECK(credential_sync_state IN ('Unknown','Ready','Pending','ReconciliationRequired')),
    CONSTRAINT account_identities_exactly_one_owner CHECK(num_nonnulls(customer_id,admin_id)=1),
    CONSTRAINT account_identities_auth_consistency CHECK(
        (auth_state='Unknown' AND supabase_user_id IS NULL AND credential_sync_state='Unknown' AND credential_epoch=1)
        OR (auth_state='Supabase' AND supabase_user_id IS NOT NULL AND credential_sync_state<>'Unknown')),
    CONSTRAINT account_identities_customer_owner_fkey FOREIGN KEY(customer_id,id)
        REFERENCES public.customers(id,app_id) ON DELETE RESTRICT,
    CONSTRAINT account_identities_admin_owner_fkey FOREIGN KEY(admin_id,id)
        REFERENCES public.admin_users(id,app_id) ON DELETE RESTRICT
);
CREATE UNIQUE INDEX account_identities_login_key ON public.account_identities(login);
CREATE UNIQUE INDEX account_identities_supabase_subject_key ON public.account_identities(supabase_user_id)
    WHERE supabase_user_id IS NOT NULL;
CREATE TABLE public.account_security_generations (
    account_id uuid PRIMARY KEY REFERENCES public.account_identities(id) ON DELETE RESTRICT,
    generation bigint NOT NULL CHECK(generation>0)
);
CREATE TABLE public.auth_mvc_signup_attempts (
    attempt_id uuid PRIMARY KEY CHECK(attempt_id<>'00000000-0000-0000-0000-000000000000'),
    browser_secret_hash bytea NOT NULL UNIQUE CHECK(octet_length(browser_secret_hash)=32),
    normalized_email varchar(254) NOT NULL CHECK(normalized_email=lower(btrim(normalized_email)) AND normalized_email<>''),
    encrypted_profile bytea NOT NULL CHECK(octet_length(encrypted_profile)>0),
    provider_subject uuid CHECK(provider_subject<>'00000000-0000-0000-0000-000000000000'),
    state text NOT NULL CHECK(state IN ('Initiated','AwaitingOtp','Verifying','Resending','Consumed','Unconfirmed')),
    operation_id uuid CHECK(operation_id<>'00000000-0000-0000-0000-000000000000'),
    consumed_operation_id uuid,
    verification_attempts integer NOT NULL DEFAULT 0 CHECK(verification_attempts BETWEEN 0 AND 5),
    next_resend_at timestamptz NOT NULL DEFAULT now()+interval '60 seconds',
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL CHECK(expires_at>created_at AND expires_at<=created_at+interval '30 minutes'),
    CHECK(state NOT IN ('AwaitingOtp','Verifying','Resending','Consumed') OR provider_subject IS NOT NULL),
    CHECK((state IN ('Verifying','Resending') AND operation_id IS NOT NULL AND consumed_operation_id IS NULL)
       OR (state='Consumed' AND operation_id IS NOT NULL AND operation_id=consumed_operation_id)
       OR (state NOT IN ('Verifying','Resending','Consumed') AND operation_id IS NULL AND consumed_operation_id IS NULL))
);
CREATE UNIQUE INDEX auth_signup_operation_key ON public.auth_mvc_signup_attempts(operation_id) WHERE operation_id IS NOT NULL;
-- Expired uncertain attempts intentionally still block replacement until reconciliation.
CREATE UNIQUE INDEX auth_signup_unresolved_email_key ON public.auth_mvc_signup_attempts(normalized_email)
    WHERE state IN ('Initiated','AwaitingOtp','Verifying','Resending','Unconfirmed');

ALTER TABLE public.admin_users ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.admin_users FORCE ROW LEVEL SECURITY;
ALTER TABLE public.account_identities ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.account_identities FORCE ROW LEVEL SECURITY;
ALTER TABLE public.account_security_generations ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.account_security_generations FORCE ROW LEVEL SECURITY;
ALTER TABLE public.auth_mvc_signup_attempts ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.auth_mvc_signup_attempts FORCE ROW LEVEL SECURITY;

CREATE SCHEMA v2_authz_private AUTHORIZATION glam_v2_authz_owner;
CREATE SCHEMA v2_authz_api AUTHORIZATION glam_v2_authz_owner;
REVOKE ALL ON SCHEMA v2_authz_private,v2_authz_api FROM PUBLIC,anon,authenticated,service_role,glam_v2_backend;
REVOKE CREATE ON SCHEMA public FROM PUBLIC,anon,authenticated,service_role,glam_v2_backend;
GRANT USAGE ON SCHEMA public TO glam_v2_backend,glam_v2_authz_owner;
GRANT USAGE ON SCHEMA v2_authz_api TO glam_v2_backend;

-- The server must see all owners to detect collisions; API roles get no policies.
CREATE POLICY auth_backend_customer_read ON public.customers FOR SELECT TO glam_v2_backend USING(true);
CREATE POLICY auth_backend_customer_insert ON public.customers FOR INSERT TO glam_v2_backend WITH CHECK(
    is_active AND locked_until IS NULL AND EXISTS (
      SELECT 1 FROM public.auth_mvc_signup_attempts a WHERE a.attempt_id=app_id
        AND a.normalized_email=lower(btrim(email)) AND a.state='Consumed'
        AND a.expires_at>statement_timestamp()));
CREATE POLICY auth_backend_customer_credentials ON public.customers FOR UPDATE TO glam_v2_backend
    USING(true) WITH CHECK(true);
CREATE POLICY auth_backend_admin_read ON public.admin_users FOR SELECT TO glam_v2_backend USING(true);
CREATE POLICY auth_backend_admin_lock ON public.admin_users FOR UPDATE TO glam_v2_backend USING(true) WITH CHECK(true);
CREATE POLICY auth_backend_identity_read ON public.account_identities FOR SELECT TO glam_v2_backend USING(true);
CREATE POLICY auth_backend_identity_security ON public.account_identities FOR UPDATE TO glam_v2_backend
    USING(customer_id IS NOT NULL) WITH CHECK(customer_id IS NOT NULL);
CREATE POLICY auth_backend_generation_read ON public.account_security_generations FOR SELECT TO glam_v2_backend USING(true);
CREATE POLICY auth_backend_generation_insert ON public.account_security_generations FOR INSERT TO glam_v2_backend
    WITH CHECK(generation=1 AND EXISTS(SELECT 1 FROM public.account_identities i
      WHERE i.id=account_id AND i.customer_id IS NOT NULL AND i.auth_state='Supabase' AND i.credential_sync_state='Ready'));
CREATE POLICY auth_backend_generation_lock ON public.account_security_generations FOR UPDATE TO glam_v2_backend
    USING(true) WITH CHECK(true);
CREATE POLICY auth_backend_attempt_read ON public.auth_mvc_signup_attempts FOR SELECT TO glam_v2_backend USING(true);
CREATE POLICY auth_backend_attempt_insert ON public.auth_mvc_signup_attempts FOR INSERT TO glam_v2_backend
    WITH CHECK(state='Initiated' AND provider_subject IS NULL AND operation_id IS NULL AND consumed_operation_id IS NULL
      AND verification_attempts=0);
CREATE POLICY auth_backend_attempt_update ON public.auth_mvc_signup_attempts FOR UPDATE TO glam_v2_backend
    USING(true) WITH CHECK(true);
CREATE POLICY auth_owner_customers ON public.customers TO glam_v2_authz_owner USING(true) WITH CHECK(true);
CREATE POLICY auth_owner_admins ON public.admin_users TO glam_v2_authz_owner USING(true) WITH CHECK(true);
CREATE POLICY auth_owner_identities ON public.account_identities TO glam_v2_authz_owner USING(true) WITH CHECK(true);
CREATE POLICY auth_owner_generations ON public.account_security_generations TO glam_v2_authz_owner USING(true) WITH CHECK(true);

CREATE FUNCTION v2_authz_private.initialize_identity() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
    INSERT INTO public.account_identities(id,customer_id,admin_id,login)
    VALUES(NEW.app_id,CASE WHEN TG_TABLE_NAME='customers' THEN NEW.id END,
        CASE WHEN TG_TABLE_NAME='admin_users' THEN NEW.id END,lower(btrim(NEW.email)));
    RETURN NEW;
END $$;
CREATE TRIGGER customers_auth_initialize AFTER INSERT ON public.customers
    FOR EACH ROW EXECUTE FUNCTION v2_authz_private.initialize_identity();
CREATE TRIGGER admins_auth_initialize AFTER INSERT ON public.admin_users
    FOR EACH ROW EXECUTE FUNCTION v2_authz_private.initialize_identity();

CREATE FUNCTION v2_authz_private.guard_owner() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.app_id IS DISTINCT FROM OLD.app_id
       OR NEW.email IS DISTINCT FROM OLD.email THEN
        RAISE EXCEPTION 'Authentication owner UUID and login are immutable' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER customers_auth_owner BEFORE UPDATE ON public.customers
    FOR EACH ROW EXECUTE FUNCTION v2_authz_private.guard_owner();
CREATE TRIGGER admins_auth_owner BEFORE UPDATE ON public.admin_users
    FOR EACH ROW EXECUTE FUNCTION v2_authz_private.guard_owner();

CREATE FUNCTION v2_authz_private.guard_identity_security() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    IF ROW(NEW.id,NEW.customer_id,NEW.admin_id,NEW.login)
       IS DISTINCT FROM ROW(OLD.id,OLD.customer_id,OLD.admin_id,OLD.login) THEN
        RAISE EXCEPTION 'Authentication identity ownership is immutable' USING ERRCODE='23514';
    END IF;
    IF OLD.auth_state='Unknown' AND NEW.auth_state='Supabase' AND NEW.credential_sync_state='Ready'
       AND NEW.credential_epoch=1 THEN RETURN NEW; END IF;
    IF ROW(NEW.auth_state,NEW.supabase_user_id) IS DISTINCT FROM ROW(OLD.auth_state,OLD.supabase_user_id) THEN
        RAISE EXCEPTION 'Provider binding is immutable' USING ERRCODE='23514';
    END IF;
    IF NEW.credential_epoch=OLD.credential_epoch AND NEW.credential_sync_state=OLD.credential_sync_state THEN RETURN NEW; END IF;
    IF OLD.credential_sync_state='Ready' AND NEW.credential_sync_state='Pending'
       AND OLD.credential_epoch<9223372036854775807 AND NEW.credential_epoch=OLD.credential_epoch+1 THEN RETURN NEW; END IF;
    IF OLD.credential_sync_state='Pending' AND
       ((NEW.credential_epoch=OLD.credential_epoch AND NEW.credential_sync_state IN ('Ready','ReconciliationRequired'))
        OR (OLD.credential_epoch<9223372036854775807 AND NEW.credential_epoch=OLD.credential_epoch+1
            AND NEW.credential_sync_state='ReconciliationRequired')) THEN RETURN NEW; END IF;
    RAISE EXCEPTION 'Invalid credential-security transition' USING ERRCODE='23514';
END $$;
CREATE TRIGGER identities_auth_security BEFORE UPDATE ON public.account_identities
    FOR EACH ROW EXECUTE FUNCTION v2_authz_private.guard_identity_security();

CREATE FUNCTION v2_authz_private.guard_generation() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    IF TG_OP='INSERT' THEN
        IF NEW.generation=1 THEN RETURN NEW; END IF;
    ELSIF NEW.account_id=OLD.account_id AND current_user='glam_v2_authz_owner'
       AND OLD.generation<9223372036854775807 AND NEW.generation=OLD.generation+1 THEN RETURN NEW;
    END IF;
    RAISE EXCEPTION 'Generation must initialize once at one and advance through revocation' USING ERRCODE='23514';
END $$;
CREATE TRIGGER generations_auth_security BEFORE INSERT OR UPDATE ON public.account_security_generations
    FOR EACH ROW EXECUTE FUNCTION v2_authz_private.guard_generation();

CREATE FUNCTION v2_authz_private.guard_signup_attempt() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    IF OLD.state='Consumed' OR
       ROW(NEW.attempt_id,NEW.browser_secret_hash,NEW.normalized_email,NEW.encrypted_profile,NEW.created_at,NEW.expires_at)
       IS DISTINCT FROM ROW(OLD.attempt_id,OLD.browser_secret_hash,OLD.normalized_email,OLD.encrypted_profile,OLD.created_at,OLD.expires_at)
       OR (OLD.provider_subject IS NOT NULL AND NEW.provider_subject IS DISTINCT FROM OLD.provider_subject)
       OR NEW.verification_attempts<OLD.verification_attempts THEN
        RAISE EXCEPTION 'Signup ownership, provider binding and consumed attempts are immutable' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER signup_auth_security BEFORE UPDATE ON public.auth_mvc_signup_attempts
    FOR EACH ROW EXECUTE FUNCTION v2_authz_private.guard_signup_attempt();

-- SQL evidence is trusted only from the protected server request, never from API claims.
CREATE FUNCTION v2_authz_api.revoke_my_sessions(
    p_account_id uuid,p_provider_subject uuid,p_role text,p_credential_epoch bigint,p_security_generation bigint
) RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE g bigint; c public.customers%ROWTYPE; i public.account_identities%ROWTYPE;
BEGIN
    IF session_user<>'glam_v2_backend' OR p_role IS DISTINCT FROM 'Customer'
       OR p_account_id IS NULL OR p_provider_subject IS NULL
       OR p_credential_epoch IS NULL OR p_credential_epoch<1
       OR p_security_generation IS NULL OR p_security_generation<1 THEN RETURN NULL; END IF;
    SELECT generation INTO g FROM public.account_security_generations WHERE account_id=p_account_id FOR UPDATE;
    IF NOT FOUND OR g<>p_security_generation OR g=9223372036854775807 THEN RETURN NULL; END IF;
    SELECT * INTO c FROM public.customers WHERE app_id=p_account_id FOR UPDATE;
    IF NOT FOUND THEN RETURN NULL; END IF;
    PERFORM id FROM public.admin_users WHERE app_id=p_account_id FOR UPDATE;
    IF FOUND THEN RETURN NULL; END IF;
    SELECT * INTO i FROM public.account_identities WHERE id=p_account_id FOR UPDATE;
    IF NOT FOUND OR i.customer_id IS DISTINCT FROM c.id OR i.admin_id IS NOT NULL
       OR i.supabase_user_id IS DISTINCT FROM p_provider_subject OR i.auth_state<>'Supabase'
       OR i.credential_epoch<>p_credential_epoch OR i.credential_sync_state<>'Ready'
       OR i.login<>lower(btrim(c.email)) OR NOT c.is_active
       OR c.locked_until>statement_timestamp() THEN RETURN NULL; END IF;
    UPDATE public.account_security_generations SET generation=g+1 WHERE account_id=p_account_id;
    RETURN g+1;
END $$;

-- Explicitly clear column ACLs too; a table REVOKE alone does not remove them.
DO $$
DECLARE t text; cols text;
BEGIN
    FOREACH t IN ARRAY ARRAY['customers','admin_users','account_identities','account_security_generations','auth_mvc_signup_attempts'] LOOP
        EXECUTE format('REVOKE ALL ON TABLE public.%I FROM PUBLIC,anon,authenticated,service_role,glam_v2_backend,glam_v2_authz_owner',t);
        SELECT string_agg(quote_ident(attname),',') INTO cols FROM pg_attribute
          WHERE attrelid=('public.'||t)::regclass AND attnum>0 AND NOT attisdropped;
        EXECUTE format('REVOKE ALL (%s) ON TABLE public.%I FROM PUBLIC,anon,authenticated,service_role,glam_v2_backend,glam_v2_authz_owner',cols,t);
    END LOOP;
END $$;
REVOKE ALL ON SEQUENCE public.customers_id_seq,public.admin_users_id_seq
    FROM PUBLIC,anon,authenticated,service_role,glam_v2_backend,glam_v2_authz_owner;
GRANT SELECT ON public.customers,public.admin_users,public.account_identities,
    public.account_security_generations,public.auth_mvc_signup_attempts TO glam_v2_backend;
GRANT INSERT(app_id,email,first_name,last_name,full_name,age_at_registration,gender,address,password_hash,is_active,suffix,birthday)
    ON public.customers TO glam_v2_backend;
GRANT UPDATE(password_hash,security_stamp) ON public.customers TO glam_v2_backend;
GRANT UPDATE(app_id) ON public.admin_users TO glam_v2_backend;
GRANT UPDATE(auth_state,supabase_user_id,credential_epoch,credential_sync_state) ON public.account_identities TO glam_v2_backend;
GRANT INSERT(account_id,generation),UPDATE(generation) ON public.account_security_generations TO glam_v2_backend;
GRANT INSERT(attempt_id,browser_secret_hash,normalized_email,encrypted_profile,state,expires_at),
    UPDATE(state,provider_subject,next_resend_at,operation_id,consumed_operation_id,verification_attempts)
    ON public.auth_mvc_signup_attempts TO glam_v2_backend;
GRANT USAGE ON SEQUENCE public.customers_id_seq TO glam_v2_backend;

GRANT SELECT ON public.customers,public.admin_users,public.account_identities,public.account_security_generations TO glam_v2_authz_owner;
GRANT INSERT(id,customer_id,admin_id,login),UPDATE(id) ON public.account_identities TO glam_v2_authz_owner;
-- Row locking requires UPDATE on at least one column; immutable guards prevent key edits.
GRANT UPDATE(security_stamp) ON public.customers TO glam_v2_authz_owner;
GRANT UPDATE(app_id) ON public.admin_users TO glam_v2_authz_owner;
GRANT UPDATE(generation) ON public.account_security_generations TO glam_v2_authz_owner;

ALTER FUNCTION v2_authz_private.initialize_identity() OWNER TO glam_v2_authz_owner;
ALTER FUNCTION v2_authz_private.guard_owner() OWNER TO glam_v2_authz_owner;
ALTER FUNCTION v2_authz_private.guard_identity_security() OWNER TO glam_v2_authz_owner;
ALTER FUNCTION v2_authz_private.guard_generation() OWNER TO glam_v2_authz_owner;
ALTER FUNCTION v2_authz_private.guard_signup_attempt() OWNER TO glam_v2_authz_owner;
ALTER FUNCTION v2_authz_api.revoke_my_sessions(uuid,uuid,text,bigint,bigint) OWNER TO glam_v2_authz_owner;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA v2_authz_private,v2_authz_api
    FROM PUBLIC,anon,authenticated,service_role,glam_v2_backend;
GRANT EXECUTE ON FUNCTION v2_authz_api.revoke_my_sessions(uuid,uuid,text,bigint,bigint) TO glam_v2_backend;

-- Reject effective inherited/default/owner privileges; never repair role memberships silently.
DO $$
DECLARE r text; effective_role record; t text;
BEGIN
    FOR r IN SELECT rolname FROM pg_roles WHERE rolname IN ('anon','authenticated','service_role','authenticator') LOOP
      -- NOINHERIT alone is insufficient: a membership may still permit SET ROLE.
      FOR effective_role IN SELECT rolname FROM pg_roles WHERE pg_has_role(r,rolname,'SET') LOOP
        FOREACH t IN ARRAY ARRAY['customers','admin_users','account_identities','account_security_generations','auth_mvc_signup_attempts'] LOOP
            IF has_table_privilege(effective_role.rolname,'public.'||t,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,MAINTAIN')
               OR has_any_column_privilege(effective_role.rolname,'public.'||t,'SELECT,INSERT,UPDATE,REFERENCES') THEN
                RAISE EXCEPTION 'Unexpected effective private auth privilege: % via % on %',r,effective_role.rolname,t;
            END IF;
        END LOOP;
        IF has_sequence_privilege(effective_role.rolname,'public.customers_id_seq','USAGE,SELECT,UPDATE')
           OR has_sequence_privilege(effective_role.rolname,'public.admin_users_id_seq','USAGE,SELECT,UPDATE')
           OR has_schema_privilege(effective_role.rolname,'v2_authz_private','USAGE,CREATE')
           OR has_schema_privilege(effective_role.rolname,'v2_authz_api','USAGE,CREATE')
           OR has_function_privilege(effective_role.rolname,'v2_authz_api.revoke_my_sessions(uuid,uuid,text,bigint,bigint)','EXECUTE') THEN
            RAISE EXCEPTION 'Unexpected effective authentication API privilege: % via %',r,effective_role.rolname;
        END IF;
      END LOOP;
    END LOOP;
    IF has_schema_privilege('glam_v2_backend','public','CREATE')
       OR has_schema_privilege('glam_v2_backend','v2_authz_private','USAGE,CREATE') THEN
        RAISE EXCEPTION 'Backend must not have schema DDL or private function access';
    END IF;
END $$;
COMMIT;
