using Npgsql;

internal static class RoleMembershipCompatibilityChecks
{
    internal static async Task RunAsync()
    {
        var root=Directory.GetCurrentDirectory();
        var expectedData=Path.GetFullPath(Path.Combine(root,"temp","v2-role-compatibility-20261116-3","data"));
        var password=Environment.GetEnvironmentVariable("PGPASSWORD");
        if(password is not {Length:64} || !password.All(c=>c is >= '0' and <= '9' or >= 'A' and <= 'F'))
            throw new InvalidOperationException("Private disposable role-test password required.");
        var cs=new NpgsqlConnectionStringBuilder {Host="127.0.0.1",Port=55435,Database="glam_v2_role_prerequisite_test",
            Username="supabase_admin",Password=password,Pooling=false,SslMode=SslMode.Disable,Timeout=5,CommandTimeout=20};
        await using var administrator=NpgsqlDataSource.Create(cs.ConnectionString);
        await using(var c=await administrator.OpenConnectionAsync())
        await using(var q=new NpgsqlCommand("""
            SELECT current_user='supabase_admin' AND current_database()='glam_v2_role_prerequisite_test'
              AND inet_server_addr()='127.0.0.1'::inet AND inet_server_port()=55435
              AND current_setting('server_version_num')::integer BETWEEN 180000 AND 189999
              AND EXISTS(SELECT 1 FROM pg_roles WHERE oid=10 AND rolname='supabase_admin' AND rolsuper)
              AND NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname IN
                ('glam_v2_backend','glam_v2_authz_owner','role_membership_intruder'))
              AND NOT EXISTS(SELECT 1 FROM pg_class WHERE relnamespace='public'::regnamespace AND relkind='r'),
              current_setting('data_directory')
            """,c))
        await using(var reader=await q.ExecuteReaderAsync())
        {
            if(!await reader.ReadAsync() || !reader.GetBoolean(0) ||
               !string.Equals(Path.GetFullPath(reader.GetString(1)),expectedData,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing a non-fresh role compatibility target.");
        }
        cs.Username="postgres";
        await using var actor=NpgsqlDataSource.Create(cs.ConnectionString);
        var passed=0;
        void Check(bool ok,string label)
        {
            if(!ok)throw new InvalidOperationException("FAIL role compatibility: "+label);
            passed++;Console.WriteLine("PASS role compatibility: "+label);
        }
        async Task<object?> Sql(NpgsqlDataSource source,string sql)
        {
            await using var c=await source.OpenConnectionAsync();
            await using var q=new NpgsqlCommand(sql,c);return await q.ExecuteScalarAsync();
        }
        async Task File(NpgsqlDataSource source,string relative)
        {
            await using var c=await source.OpenConnectionAsync();
            await using var q=new NpgsqlCommand(await System.IO.File.ReadAllTextAsync(Path.Combine(root,relative)),c);
            await q.ExecuteNonQueryAsync();
        }
        const string migration="database/v2/migrations/0004_customer_auth_compatibility.sql";
        async Task Denied(string label)
        {
            var denied=false;
            try {await File(actor,migration);}
            catch(PostgresException e) when(e.SqlState=="P0001" &&
                (e.MessageText.StartsWith("Unexpected auth membership") ||
                 e.MessageText.StartsWith("API role can reach") ||
                 e.MessageText.StartsWith("Invalid restricted authentication role"))) {denied=true;}
            Check(denied && await Sql(actor,"SELECT to_regclass('public.account_identities') IS NULL") is true,label);
        }
        Check(await Sql(actor,"SELECT NOT rolsuper AND rolcreaterole FROM pg_roles WHERE rolname=current_user") is true,
            "real non-superuser postgres CREATEROLE connection");
        await File(actor,"database/v2/prerequisites/customer_auth_roles_sql_editor.sql");
        Check(await Sql(actor,"""
            SELECT count(*)=2 AND bool_and(m.admin_option AND NOT m.set_option AND NOT m.inherit_option AND m.grantor=10)
            FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.roleid
            WHERE r.rolname IN ('glam_v2_backend','glam_v2_authz_owner')
            """) is true,"exact SQL Editor proposal produces only genuine bootstrap ADMIN-only memberships");
        Check(await Sql(actor,"SELECT NOT pg_has_role('postgres','glam_v2_backend','SET') AND NOT pg_has_role('postgres','glam_v2_backend','USAGE')") is true,
            "ADMIN-only membership does not itself enable backend SET/INHERIT");
        await Sql(actor,"CREATE SCHEMA extensions; CREATE EXTENSION btree_gist WITH SCHEMA extensions");
        await File(actor,"database/v2/migrations/0001_catalog_schedule_foundation.sql");
        await File(actor,"database/v2/migrations/0002_customer_reservation_foundation.sql");
        await Sql(actor,"GRANT glam_v2_authz_owner TO postgres WITH ADMIN FALSE, INHERIT TRUE, SET TRUE");
        await Sql(administrator,"CREATE ROLE role_membership_intruder NOLOGIN");
        foreach(var variant in new[]{
            (Options:"ADMIN TRUE, INHERIT TRUE, SET FALSE",Label:"bootstrap row with INHERIT TRUE rejected"),
            (Options:"ADMIN TRUE, INHERIT FALSE, SET TRUE",Label:"bootstrap row with SET TRUE rejected"),
            (Options:"ADMIN FALSE, INHERIT FALSE, SET FALSE",Label:"bootstrap row without ADMIN TRUE rejected")})
        {
            await Sql(administrator,$"GRANT glam_v2_backend TO postgres WITH {variant.Options}");
            await Denied(variant.Label);
            await Sql(administrator,"GRANT glam_v2_backend TO postgres WITH ADMIN TRUE, INHERIT FALSE, SET FALSE");
        }
        var migrationText=await System.IO.File.ReadAllTextAsync(Path.Combine(root,migration));
        var predicateStart=migrationText.IndexOf("OR EXISTS (SELECT 1 FROM pg_auth_members m JOIN pg_roles r",StringComparison.Ordinal)+3;
        var predicateEnd=migrationText.IndexOf(" THEN",predicateStart,StringComparison.Ordinal);
        if(predicateStart<3 || predicateEnd<predicateStart)throw new InvalidOperationException("Membership predicate not found.");
        var membershipPredicate=migrationText[predicateStart..predicateEnd];
        Check(await Sql(actor,"""
            WITH pg_auth_members(roleid,member,grantor,admin_option,inherit_option,set_option) AS (
              SELECT target.oid,actor.oid,actor.oid,true,false,false
              FROM pg_roles target,pg_roles actor WHERE target.rolname='glam_v2_backend' AND actor.rolname='postgres'
            ) SELECT
            """+" "+membershipPredicate) is true,"exact migration predicate rejects a synthetic wrong-grantor ADMIN-only row");
        await Sql(actor,"GRANT glam_v2_backend TO postgres WITH ADMIN FALSE, INHERIT FALSE, SET FALSE");
        await Denied("additional self-granted membership rejected even with INHERIT/SET off");
        await Sql(actor,"REVOKE glam_v2_backend FROM postgres GRANTED BY postgres");
        await Sql(administrator,"GRANT glam_v2_backend TO role_membership_intruder WITH ADMIN TRUE, INHERIT FALSE, SET FALSE");
        await Denied("bootstrap grant to a member other than postgres rejected");
        await Sql(administrator,"REVOKE glam_v2_backend FROM role_membership_intruder");
        await Sql(administrator,"GRANT role_membership_intruder TO glam_v2_backend WITH ADMIN FALSE, INHERIT FALSE, SET FALSE");
        await Denied("backend joining any other role remains rejected");
        await Sql(administrator,"REVOKE role_membership_intruder FROM glam_v2_backend");
        await Sql(administrator,"ALTER ROLE glam_v2_backend INHERIT");
        await Denied("role-level INHERIT remains rejected");
        await Sql(administrator,"ALTER ROLE glam_v2_backend NOINHERIT");
        await Sql(administrator,"ALTER ROLE glam_v2_backend BYPASSRLS");
        await Denied("backend BYPASSRLS remains rejected");
        await Sql(administrator,"ALTER ROLE glam_v2_backend NOBYPASSRLS");
        await Sql(administrator,"ALTER ROLE postgres NOCREATEROLE");
        await Denied("missing expected administrator attributes fail closed instead of accepting SQL NULL");
        await Sql(administrator,"ALTER ROLE postgres CREATEROLE");
        foreach(var variant in new[]{
            (Role:"authenticated",Options:"ADMIN FALSE, INHERIT TRUE, SET FALSE",Label:"API inherits postgres"),
            (Role:"authenticated",Options:"ADMIN FALSE, INHERIT FALSE, SET TRUE",Label:"API can SET postgres"),
            (Role:"authenticated",Options:"ADMIN TRUE, INHERIT FALSE, SET FALSE",Label:"API can administer postgres without SET/INHERIT"),
            (Role:"authenticator",Options:"ADMIN FALSE, INHERIT FALSE, SET TRUE",Label:"authenticator can SET postgres")})
        {
            await Sql(administrator,$"GRANT postgres TO {variant.Role} WITH {variant.Options}");
            await Denied(variant.Label+" rejected");
            await Sql(administrator,$"REVOKE postgres FROM {variant.Role}");
        }
        await Sql(administrator,"GRANT postgres TO role_membership_intruder WITH ADMIN FALSE, INHERIT FALSE, SET TRUE; GRANT role_membership_intruder TO authenticated WITH ADMIN FALSE, INHERIT FALSE, SET TRUE");
        await Denied("transitive API SET ROLE path to postgres rejected");
        await Sql(administrator,"REVOKE role_membership_intruder FROM authenticated; REVOKE postgres FROM role_membership_intruder");
        await Sql(administrator,"GRANT postgres TO role_membership_intruder WITH ADMIN TRUE, INHERIT FALSE, SET FALSE; GRANT role_membership_intruder TO authenticated WITH ADMIN TRUE, INHERIT FALSE, SET FALSE");
        await Denied("transitive ADMIN-only API self-grant escalation path rejected");
        await Sql(administrator,"REVOKE role_membership_intruder FROM authenticated; REVOKE postgres FROM role_membership_intruder");
        await Sql(administrator,"GRANT glam_v2_backend TO service_role WITH ADMIN FALSE, INHERIT FALSE, SET TRUE");
        await Denied("direct API access to backend rejected");
        await Sql(administrator,"REVOKE glam_v2_backend FROM service_role");

        await Sql(actor,"GRANT glam_v2_backend TO postgres WITH ADMIN FALSE, INHERIT FALSE, SET TRUE");
        Check(await Sql(actor,"SELECT pg_has_role('postgres','glam_v2_backend','SET')") is true,
            "ADMIN operator can deliberately self-grant: this is an administrator trust assumption");
        await Denied("administrator self-granted backend capability is rejected before migration");
        await Sql(actor,"REVOKE glam_v2_backend FROM postgres GRANTED BY postgres");
        await Sql(actor,"""
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO anon,authenticated,service_role;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO anon,authenticated,service_role;
            ALTER DEFAULT PRIVILEGES GRANT EXECUTE ON FUNCTIONS TO anon,authenticated,service_role
            """);
        await File(actor,migration);
        Check(await Sql(actor,"SELECT to_regclass('public.account_identities') IS NOT NULL") is true,
            "complete revised migration succeeds as SQL Editor-style operator with genuine creator memberships");
        foreach(var api in new[]{"anon","authenticated","service_role","authenticator"})
        {
            var safe=await Sql(actor,$"""
                SELECT bool_and(NOT has_table_privilege('{api}',c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,MAINTAIN')
                  AND NOT has_any_column_privilege('{api}',c.oid,'SELECT,INSERT,UPDATE,REFERENCES'))
                FROM pg_class c WHERE c.oid=ANY(ARRAY[
                  'public.customers'::regclass,'public.admin_users'::regclass,'public.account_identities'::regclass,
                  'public.account_security_generations'::regclass,'public.auth_mvc_signup_attempts'::regclass])
                """);
            Check(safe is true,api+" retains no private table/column privileges despite permissive defaults");
        }
        // Synthetic local password only, never part of the hosted provisioning proposal.
        await Sql(actor,$"ALTER ROLE glam_v2_backend PASSWORD '{password}'");
        cs.Username="glam_v2_backend";
        await using var backend=NpgsqlDataSource.Create(cs.ConnectionString);
        await CustomerMvcChecks.RunAsync(actor,backend);
        await Sql(actor,"REVOKE glam_v2_authz_owner FROM postgres GRANTED BY postgres");
        Check(await Sql(actor,"""
            SELECT NOT pg_has_role('postgres','glam_v2_backend','SET')
              AND NOT pg_has_role('postgres','glam_v2_backend','USAGE')
              AND NOT pg_has_role('postgres','glam_v2_authz_owner','SET')
              AND NOT pg_has_role('postgres','glam_v2_authz_owner','USAGE')
            """) is true,"post-migration cleanup leaves only ADMIN-only management, no active private role capabilities");
        Check(await Sql(actor,"SELECT count(*)=2 FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.roleid WHERE r.rolname IN ('glam_v2_backend','glam_v2_authz_owner') AND m.grantor=10 AND m.admin_option AND NOT m.inherit_option AND NOT m.set_option") is true,
            "both bootstrap ADMIN-only rows remain distinguishable from temporary capability grants");
        Console.WriteLine($"{passed} role compatibility checks passed; local PostgreSQL only, plus the actual MVC checks above.");
    }
}
