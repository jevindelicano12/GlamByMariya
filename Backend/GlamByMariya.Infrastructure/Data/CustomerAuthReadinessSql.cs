namespace GlamByMariya.Infrastructure.Data;

// Shared verbatim by MVC readiness and the separately approved read-only diagnostic.
public static class CustomerAuthReadinessSql
{
    public const string Query = """
        SELECT r.rolname='glam_v2_backend' AND r.rolcanlogin AND NOT r.rolinherit
          AND NOT (r.rolsuper OR r.rolbypassrls OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication)
          AND NOT EXISTS(SELECT 1 FROM pg_auth_members WHERE member=r.oid)
          AND NOT has_schema_privilege(current_user,'public','CREATE')
          AND (SELECT count(*)=5 AND bool_and(relrowsecurity AND relforcerowsecurity)
               FROM pg_class WHERE oid=ANY(ARRAY[
                 to_regclass('public.customers'),to_regclass('public.admin_users'),
                 to_regclass('public.account_identities'),to_regclass('public.account_security_generations'),
                 to_regclass('public.auth_mvc_signup_attempts')]))
          AND NOT EXISTS (
            SELECT 1 FROM (VALUES
              ('customers','birthday'),('customers','suffix'),('customers','password_hash'),
              ('customers','security_stamp'),('customers','first_name'),('customers','last_name'),
              ('customers','age_at_registration'),('customers','gender'),('customers','address'),
              ('account_identities','supabase_user_id'),('account_identities','credential_epoch'),
              ('account_identities','credential_sync_state'),('account_security_generations','generation'),
              ('auth_mvc_signup_attempts','operation_id'),('auth_mvc_signup_attempts','consumed_operation_id'),
              ('auth_mvc_signup_attempts','encrypted_profile')) required(tbl,col)
            WHERE NOT EXISTS (SELECT 1 FROM information_schema.columns
              WHERE table_schema='public' AND table_name=required.tbl AND column_name=required.col))
          AND NOT EXISTS (
            SELECT 1 FROM (VALUES
              ('customers','SELECT'),('admin_users','SELECT'),('account_identities','SELECT'),
              ('account_security_generations','SELECT'),('auth_mvc_signup_attempts','SELECT')) rights(tbl,priv)
            WHERE NOT has_table_privilege(current_user,'public.'||rights.tbl,rights.priv))
          AND NOT EXISTS (
            SELECT 1 FROM (VALUES
              ('customers','app_id','INSERT'),('customers','email','INSERT'),('customers','first_name','INSERT'),
              ('customers','last_name','INSERT'),('customers','full_name','INSERT'),('customers','age_at_registration','INSERT'),
              ('customers','gender','INSERT'),('customers','address','INSERT'),('customers','password_hash','INSERT'),
              ('customers','is_active','INSERT'),('customers','suffix','INSERT'),('customers','birthday','INSERT'),
              ('account_security_generations','account_id','INSERT'),('account_security_generations','generation','INSERT'),
              ('auth_mvc_signup_attempts','attempt_id','INSERT'),('auth_mvc_signup_attempts','browser_secret_hash','INSERT'),
              ('auth_mvc_signup_attempts','normalized_email','INSERT'),('auth_mvc_signup_attempts','encrypted_profile','INSERT'),
              ('auth_mvc_signup_attempts','state','INSERT'),('auth_mvc_signup_attempts','expires_at','INSERT'),
              ('auth_mvc_signup_attempts','state','UPDATE'),('auth_mvc_signup_attempts','provider_subject','UPDATE'),
              ('auth_mvc_signup_attempts','next_resend_at','UPDATE'),('auth_mvc_signup_attempts','operation_id','UPDATE'),
              ('auth_mvc_signup_attempts','consumed_operation_id','UPDATE'),('auth_mvc_signup_attempts','verification_attempts','UPDATE')
            ) rights(tbl,col,priv)
            WHERE NOT has_column_privilege(current_user,'public.'||rights.tbl,rights.col,rights.priv))
          AND has_sequence_privilege(current_user,'public.customers_id_seq','USAGE')
          AND has_column_privilege(current_user,'public.customers','password_hash','UPDATE')
          AND has_column_privilege(current_user,'public.customers','security_stamp','UPDATE')
          AND has_column_privilege(current_user,'public.admin_users','app_id','UPDATE')
          AND has_column_privilege(current_user,'public.account_identities','credential_epoch','UPDATE')
          AND has_column_privilege(current_user,'public.account_identities','auth_state','UPDATE')
          AND has_column_privilege(current_user,'public.account_identities','supabase_user_id','UPDATE')
          AND has_column_privilege(current_user,'public.account_identities','credential_sync_state','UPDATE')
          AND has_column_privilege(current_user,'public.account_security_generations','generation','UPDATE')
          AND to_regprocedure('v2_authz_api.revoke_my_sessions(uuid,uuid,text,bigint,bigint)') IS NOT NULL
          AND has_function_privilege(current_user,
            to_regprocedure('v2_authz_api.revoke_my_sessions(uuid,uuid,text,bigint,bigint)'),'EXECUTE')
        FROM pg_roles r WHERE rolname=current_user
        """;
}
