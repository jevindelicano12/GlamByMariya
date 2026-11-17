-- Local structural compatibility fixture, NOT a hosted migration.
-- The application uses the existing signup adapters and identity candidate.
BEGIN;
DO $$
BEGIN
    IF current_database()<>'glam_v2_customer_auth_test' OR current_user<>'postgres'
       OR inet_server_addr() IS DISTINCT FROM '127.0.0.1'::inet OR inet_server_port()<>55434 THEN
        RAISE EXCEPTION 'Wrong Customer auth fixture target';
    END IF;
END $$;
ALTER TABLE public.customers
    ADD COLUMN first_name varchar(50), ADD COLUMN last_name varchar(50),
    ADD COLUMN age_at_registration smallint, ADD COLUMN gender text,
    ADD COLUMN address text, ADD COLUMN password_hash text NOT NULL DEFAULT '',
    ADD COLUMN security_stamp uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD COLUMN birthday date, ADD COLUMN suffix varchar(10);
ALTER TABLE public.admin_users
    ADD COLUMN password_hash text NOT NULL DEFAULT '',
    ADD COLUMN security_stamp uuid NOT NULL DEFAULT gen_random_uuid();
CREATE UNIQUE INDEX fixture_customer_email ON public.customers(lower(btrim(email)));
CREATE TABLE public.auth_mvc_signup_attempts (
    attempt_id uuid PRIMARY KEY,
    browser_secret_hash bytea NOT NULL UNIQUE CHECK(octet_length(browser_secret_hash)=32),
    normalized_email text NOT NULL CHECK(normalized_email=btrim(lower(normalized_email)) AND normalized_email<>''),
    encrypted_profile bytea NOT NULL CHECK(octet_length(encrypted_profile)>0),
    provider_subject uuid,
    state text NOT NULL CHECK(state IN ('Initiated','AwaitingOtp','Verifying','Resending','Consumed','Unconfirmed')),
    operation_id uuid, consumed_operation_id uuid,
    verification_attempts integer NOT NULL DEFAULT 0 CHECK(verification_attempts BETWEEN 0 AND 5),
    next_resend_at timestamptz NOT NULL DEFAULT now()+interval '60 seconds',
    created_at timestamptz NOT NULL DEFAULT now(), expires_at timestamptz NOT NULL CHECK(expires_at>created_at),
    CHECK(state NOT IN ('AwaitingOtp','Consumed','Verifying','Resending') OR provider_subject IS NOT NULL),
    CHECK((state IN ('Verifying','Resending') AND operation_id IS NOT NULL AND consumed_operation_id IS NULL)
       OR (state='Consumed' AND operation_id IS NOT NULL AND operation_id=consumed_operation_id)
       OR (state NOT IN ('Verifying','Resending','Consumed') AND operation_id IS NULL AND consumed_operation_id IS NULL))
);
CREATE UNIQUE INDEX fixture_signup_operation ON public.auth_mvc_signup_attempts(operation_id) WHERE operation_id IS NOT NULL;
CREATE UNIQUE INDEX fixture_signup_unresolved_email ON public.auth_mvc_signup_attempts(normalized_email)
    WHERE state IN ('Initiated','AwaitingOtp','Verifying','Resending','Unconfirmed');
ALTER TABLE public.auth_mvc_signup_attempts ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.auth_mvc_signup_attempts FORCE ROW LEVEL SECURITY;
CREATE FUNCTION public.fixture_customer_auth_identity() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
BEGIN
    INSERT INTO public.account_identities(id,customer_id,login) VALUES(NEW.app_id,NEW.id,lower(btrim(NEW.email)));
    RETURN NEW;
END $$;
CREATE TRIGGER fixture_customer_auth_identity AFTER INSERT ON public.customers
    FOR EACH ROW EXECUTE FUNCTION public.fixture_customer_auth_identity();
REVOKE ALL ON FUNCTION public.fixture_customer_auth_identity() FROM PUBLIC,anon,authenticated,service_role,glam_v2_backend;
REVOKE ALL ON public.auth_mvc_signup_attempts FROM PUBLIC,anon,authenticated,service_role;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
CREATE POLICY fixture_auth_customers ON public.customers TO glam_v2_backend USING(true) WITH CHECK(true);
CREATE POLICY fixture_auth_admin_read ON public.admin_users TO glam_v2_backend USING(true);
CREATE POLICY fixture_auth_identity ON public.account_identities TO glam_v2_backend USING(true) WITH CHECK(true);
CREATE POLICY fixture_auth_generation ON public.account_security_generations TO glam_v2_backend USING(true) WITH CHECK(true);
CREATE POLICY fixture_auth_attempt ON public.auth_mvc_signup_attempts TO glam_v2_backend USING(true) WITH CHECK(true);
GRANT SELECT,INSERT ON public.customers TO glam_v2_backend;
GRANT UPDATE(password_hash,security_stamp) ON public.customers TO glam_v2_backend;
GRANT SELECT,UPDATE(app_id) ON public.admin_users TO glam_v2_backend;
GRANT SELECT,INSERT ON public.account_identities,public.account_security_generations TO glam_v2_backend;
GRANT UPDATE(auth_state,supabase_user_id,credential_epoch,credential_sync_state) ON public.account_identities TO glam_v2_backend;
GRANT UPDATE(generation) ON public.account_security_generations TO glam_v2_backend;
GRANT SELECT,INSERT,UPDATE ON public.auth_mvc_signup_attempts TO glam_v2_backend;
GRANT USAGE ON SEQUENCE public.customers_id_seq TO glam_v2_backend;
COMMIT;
