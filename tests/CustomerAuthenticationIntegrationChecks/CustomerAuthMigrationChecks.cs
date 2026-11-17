using Npgsql;

internal static class CustomerAuthMigrationChecks
{
    private const string Migration="database/v2/migrations/0004_customer_auth_compatibility.sql";
    private static int passed;
    private static void Check(bool condition,string label)
    {
        if(!condition)throw new InvalidOperationException("FAIL migration: "+label);
        passed++;Console.WriteLine("PASS migration: "+label);
    }
    private static async Task<object?> Sql(NpgsqlDataSource source,string sql)
    {
        await using var c=await source.OpenConnectionAsync();
        await using var q=new NpgsqlCommand(sql,c);
        return await q.ExecuteScalarAsync();
    }
    private static async Task Apply(string root,NpgsqlDataSource source,string file)
    {
        await using var c=await source.OpenConnectionAsync();
        await using var q=new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(root,file)),c);
        await q.ExecuteNonQueryAsync();
    }
    private static async Task<bool> Rejected(Func<Task> action,string? state=null)
    {
        try {await action();return false;}
        catch(PostgresException e) when(state==null || e.SqlState==state) {return true;}
    }
    private static async Task Foundation(string root,NpgsqlDataSource source,bool second=true)
    {
        await Sql(source,"CREATE SCHEMA extensions; CREATE EXTENSION btree_gist WITH SCHEMA extensions");
        await Apply(root,source,"database/v2/migrations/0001_catalog_schedule_foundation.sql");
        if(second)await Apply(root,source,"database/v2/migrations/0002_customer_reservation_foundation.sql");
    }
    internal static async Task PrepareAsync(string root,NpgsqlDataSource cluster,NpgsqlDataSource admin,NpgsqlConnectionStringBuilder credentials)
    {
        await Foundation(root,admin);
        var exclusion=await Sql(admin,"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname='reservations_artist_reserved_overlap'");
        // Simulate Supabase-style creator defaults, including BYPASSRLS service_role.
        await Sql(admin,"""
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO anon,authenticated,service_role;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO anon,authenticated,service_role;
            ALTER DEFAULT PRIVILEGES GRANT EXECUTE ON FUNCTIONS TO anon,authenticated,service_role;
            GRANT SELECT ON public.customers TO glam_v2_inherited_probe;
            GRANT glam_v2_inherited_probe TO authenticated;
            """);
        Check(await Rejected(()=>Apply(root,admin,Migration),"P0001") &&
            await Sql(admin,"SELECT to_regclass('public.account_identities') IS NULL") is true,
            "inherited API privilege rejects the entire migration without partial DDL");
        await Sql(admin,"ALTER ROLE authenticated NOINHERIT");
        Check(await Rejected(()=>Apply(root,admin,Migration),"P0001") &&
            await Sql(admin,"SELECT to_regclass('public.account_identities') IS NULL") is true,
            "NOINHERIT membership with SET ROLE access also rejects private data exposure");
        await Sql(admin,"REVOKE glam_v2_inherited_probe FROM authenticated; REVOKE SELECT ON public.customers FROM glam_v2_inherited_probe");
        await Sql(admin,"GRANT SELECT(email) ON public.customers TO PUBLIC");
        await Apply(root,admin,Migration);
        Check(Equals(exclusion,await Sql(admin,"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname='reservations_artist_reserved_overlap'")),
            "reservation exclusion definition is unchanged");
        Check(await Sql(admin,"SELECT count(*)=10 AND bool_and(relrowsecurity AND relforcerowsecurity) FROM pg_class WHERE relnamespace='public'::regnamespace AND relkind='r'") is true,
            "six foundations and four new tables all enable and force RLS");
        Check(await Sql(admin,"SELECT NOT has_table_privilege('glam_v2_backend','public.account_identities','INSERT') AND NOT has_any_column_privilege('glam_v2_backend','public.account_identities','INSERT')") is true,
            "identity initialization requires no runtime identity INSERT privilege");

        await Sql(cluster,"CREATE DATABASE glam_v2_auth_order_test TEMPLATE template0");
        await Sql(cluster,"CREATE DATABASE glam_v2_auth_repeat_test OWNER glam_v2_migration_probe TEMPLATE template0");
        var connection=new NpgsqlConnectionStringBuilder(credentials.ConnectionString){Database="glam_v2_auth_order_test"};
        await using var order=NpgsqlDataSource.Create(connection.ConnectionString);
        await Foundation(root,order,false);
        Check(await Rejected(()=>Apply(root,order,Migration)),"0004 rejects missing 0002 without applying payment 0003");
        await Apply(root,order,"database/v2/migrations/0002_customer_reservation_foundation.sql");
        await Sql(order,"INSERT INTO public.customers(full_name,email) VALUES('Retained foundation record','retained@example.test')");
        Check(await Rejected(()=>Apply(root,order,Migration),"P0001") &&
            await Sql(order,"SELECT count(*)=1 FROM public.customers WHERE full_name='Retained foundation record'") is true &&
            await Sql(order,"SELECT NOT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='customers' AND column_name='app_id')") is true,
            "occupied foundation fails without changing existing Customer data or columns");

        connection.Database="glam_v2_auth_repeat_test";
        connection.Username="glam_v2_migration_probe";
        await using var repeat=NpgsqlDataSource.Create(connection.ConnectionString);
        await Sql(cluster,"GRANT glam_v2_authz_owner TO glam_v2_migration_probe WITH INHERIT TRUE, SET TRUE");
        await Foundation(root,repeat);
        await Sql(repeat,"INSERT INTO public.services(name,price,duration_minutes) VALUES('Retained catalog',1,30)");
        await Apply(root,repeat,Migration);
        Check(await Sql(repeat,"SELECT NOT rolsuper FROM pg_roles WHERE rolname=current_user") is true,
            "migration also succeeds as a non-superuser DDL operator with explicit definer-owner membership");
        Check(await Sql(repeat,"SELECT count(*)=1 FROM public.services WHERE name='Retained catalog'") is true,
            "same migration applies on a second fresh database without altering catalog data");
        Check(await Rejected(()=>Apply(root,repeat,Migration),"P0001") &&
            await Sql(repeat,"SELECT count(*)=1 FROM public.services") is true,
            "repeat application fails safely; it is not a destructive or silently idempotent reset");
        await Sql(cluster,"REVOKE glam_v2_authz_owner FROM glam_v2_migration_probe");
        Check(await Sql(admin,"SELECT NOT EXISTS(SELECT 1 FROM pg_auth_members WHERE roleid=(SELECT oid FROM pg_roles WHERE rolname='glam_v2_authz_owner'))") is true,
            "temporary migration-owner membership is removed after ownership transfer");
    }
    internal static async Task PermissionsAsync(NpgsqlDataSource admin,NpgsqlConnectionStringBuilder connection)
    {
        var tables=new[]{"customers","admin_users","account_identities","account_security_generations","auth_mvc_signup_attempts"};
        foreach(var role in new[]{"anon","authenticated","service_role","glam_v2_public_probe"})
        {
            await using var source=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connection.ConnectionString){Username=role}.ConnectionString);
            foreach(var table in tables)
            {
                Check(await Rejected(async()=>{await Sql(source,$"SELECT * FROM public.{table}");},PostgresErrorCodes.InsufficientPrivilege),
                    $"{role} cannot read {table}");
                Check(await Rejected(async()=>{await Sql(source,$"INSERT INTO public.{table} DEFAULT VALUES");},PostgresErrorCodes.InsufficientPrivilege),
                    $"{role} cannot insert {table}");
                Check(await Rejected(async()=>{await Sql(source,$"DELETE FROM public.{table}");},PostgresErrorCodes.InsufficientPrivilege),
                    $"{role} cannot delete {table}");
                var column=table=="customers" ? "full_name" : table=="admin_users" ? "role" :
                    table=="account_identities" ? "credential_epoch" : table=="account_security_generations" ? "generation" : "state";
                Check(await Rejected(async()=>{await Sql(source,$"UPDATE public.{table} SET {column}={column}");},PostgresErrorCodes.InsufficientPrivilege),
                    $"{role} cannot update {table}");
            }
            Check(await Rejected(async()=>{await Sql(source,"SELECT v2_authz_api.revoke_my_sessions(gen_random_uuid(),gen_random_uuid(),'Customer',1,1)");},
                PostgresErrorCodes.InsufficientPrivilege),$"{role} cannot invoke revocation");
            Check(await Rejected(async()=>{await Sql(source,"SELECT nextval('public.customers_id_seq')");},PostgresErrorCodes.InsufficientPrivilege),
                $"{role} cannot use private Customer sequence");
        }
        Check(await Sql(admin,"SELECT rolbypassrls FROM pg_roles WHERE rolname='service_role'") is true,
            "service_role denials hold even with real BYPASSRLS");
        await using var backend=NpgsqlDataSource.Create(connection.ConnectionString);
        Check(await Rejected(async()=>{await Sql(backend,"INSERT INTO public.admin_users(app_id,email,username,role) VALUES(gen_random_uuid(),'escalation@example.test','escalation','SuperAdmin')");},
            PostgresErrorCodes.InsufficientPrivilege),"backend cannot provision Admin or SuperAdmin");
        Check(await Rejected(async()=>{await Sql(backend,"INSERT INTO public.account_identities(id,customer_id,login) VALUES(gen_random_uuid(),1,'forged@example.test')");},
            PostgresErrorCodes.InsufficientPrivilege),"backend cannot directly insert forged identities");
        Check(await Sql(backend,"SELECT v2_authz_api.revoke_my_sessions(gen_random_uuid(),gen_random_uuid(),'SuperAdmin',1,1) IS NULL") is true,
            "Customer revocation does not accept forged staff authority");
        Check(await Sql(admin,"""
            SELECT NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace,
              LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
              WHERE n.nspname IN ('v2_authz_api','v2_authz_private') AND a.grantee=0)
            """) is true,"PUBLIC has no implicit function EXECUTE");
        Check(await Sql(admin,"""
            SELECT NOT has_table_privilege('glam_v2_backend','public.customers','INSERT')
              AND NOT has_table_privilege('glam_v2_backend','public.customers','DELETE,TRUNCATE,TRIGGER,REFERENCES')
            """) is true,
            "backend Customer INSERT is column-scoped, with no delete/truncate/trigger rights");
    }
    internal static async Task AfterMvcAsync(string root,NpgsqlDataSource admin,NpgsqlDataSource backend)
    {
        var before=await Sql(admin,"SELECT md5(string_agg(to_jsonb(c)::text,'' ORDER BY id)) FROM public.customers c");
        Check(await Rejected(()=>Apply(root,admin,Migration),"P0001") && Equals(before,
            await Sql(admin,"SELECT md5(string_agg(to_jsonb(c)::text,'' ORDER BY id)) FROM public.customers c")),
            "reapplication after real MVC writes preserves every Customer row");
        foreach(var statement in new[]{
            "UPDATE public.account_identities SET supabase_user_id=gen_random_uuid() WHERE login='owner@example.test'",
            "UPDATE public.account_identities SET credential_epoch=1 WHERE login='owner@example.test'",
            "UPDATE public.account_security_generations SET generation=generation+1",
            "UPDATE public.customers SET is_active=true"})
        {
            Check(await Rejected(async()=>{await Sql(backend,statement);}),
                "backend rejects unauthorized mutation: "+statement.Split(" SET ")[0]);
        }
        await using(var c=await admin.OpenConnectionAsync())
        await using(var tx=await c.BeginTransactionAsync())
        {
            await using var q=new NpgsqlCommand("""
                INSERT INTO public.admin_users(app_id,email,username,role)
                VALUES(gen_random_uuid(),'private-owner@example.test','private-owner','SuperAdmin');
                """,c,tx);
            await q.ExecuteNonQueryAsync();
            await using var immutable=new NpgsqlCommand("UPDATE public.admin_users SET app_id=gen_random_uuid() WHERE email='private-owner@example.test'",c,tx);
            Check(await Rejected(async()=>{await immutable.ExecuteNonQueryAsync();},PostgresErrorCodes.CheckViolation),
                "row-lock UPDATE grant cannot rewrite staff ownership");
            await tx.RollbackAsync();
        }
        Check(await Sql(admin,"SELECT count(*)=0 FROM public.admin_users") is true,
            "private provisioning test rolls back; no first-user or seeded SuperAdmin remains");
        await using(var c=await admin.OpenConnectionAsync())
        {
            await using var q=new NpgsqlCommand("""
                SELECT i.id,i.supabase_user_id,i.credential_epoch,g.generation
                FROM public.account_identities i JOIN public.account_security_generations g ON g.account_id=i.id
                WHERE i.login='race@example.test'
                """,c);
            await using var reader=await q.ExecuteReaderAsync();
            if(!await reader.ReadAsync())throw new InvalidOperationException("MVC concurrency account missing");
            var id=reader.GetGuid(0);var subject=reader.GetGuid(1);var epoch=reader.GetInt64(2);var generation=reader.GetInt64(3);
            var call=$"SELECT v2_authz_api.revoke_my_sessions('{id}','{subject}','Customer',{epoch},{generation})";
            var results=await Task.WhenAll(Sql(backend,call),Sql(backend,call));
            Check(results.Count(x=>x is long n && n==generation+1)==1 && results.Count(x=>x is DBNull)==1,
                "concurrent revocation has one winner; the stale generation cannot revoke again");
            Check(await Sql(backend,call+" IS NULL") is true,"stale generation evidence returns no authority");
            Check(await Sql(backend,$"SELECT v2_authz_api.revoke_my_sessions('{id}','{Guid.NewGuid()}','Customer',{epoch},{generation+1}) IS NULL") is true,
                "wrong provider identity cannot revoke the Customer");
        }
        Check(await Rejected(async()=>{await Sql(backend,"UPDATE public.auth_mvc_signup_attempts SET state='AwaitingOtp',consumed_operation_id=NULL,operation_id=NULL WHERE normalized_email='race@example.test'");},
            PostgresErrorCodes.CheckViolation),"consumed attempt cannot be reopened");
        Check(await Rejected(async()=>{await Sql(backend,"UPDATE public.auth_mvc_signup_attempts SET encrypted_profile=decode('01','hex')");},
            PostgresErrorCodes.InsufficientPrivilege),"backend cannot rewrite protected signup profiles");
        await BookingAndBindingRollback(admin);
        Console.WriteLine($"{passed} Customer-auth migration/permission checks passed; disposable PostgreSQL only.");
    }
    private static async Task BookingAndBindingRollback(NpgsqlDataSource admin)
    {
        const string insertCustomer="""
            INSERT INTO public.customers(app_id,email,first_name,last_name,full_name,age_at_registration,gender,address,password_hash,birthday)
            VALUES(gen_random_uuid(),'schema-rollback@example.test','Schema','Rollback','Schema Rollback',25,'Others','Synthetic address','synthetic',DATE '2001-01-01')
            """;
        await using(var c=await admin.OpenConnectionAsync())
        await using(var tx=await c.BeginTransactionAsync())
        {
            await using var setup=new NpgsqlCommand(insertCustomer,c,tx);await setup.ExecuteNonQueryAsync();
            await using var collision=new NpgsqlCommand("""
                UPDATE public.account_identities SET auth_state='Supabase',credential_sync_state='Ready',
                  supabase_user_id=(SELECT supabase_user_id FROM public.account_identities WHERE login='race@example.test')
                WHERE login='schema-rollback@example.test'
                """,c,tx);
            Check(await Rejected(async()=>{await collision.ExecuteNonQueryAsync();},PostgresErrorCodes.UniqueViolation),
                "unique provider index rejects duplicate binding to another Customer");
            await tx.RollbackAsync();
        }
        await using(var c=await admin.OpenConnectionAsync())
        await using(var tx=await c.BeginTransactionAsync())
        {
            await using var setup=new NpgsqlCommand(insertCustomer+"""
                ;
                INSERT INTO public.makeup_artists(full_name) VALUES('Synthetic slot artist');
                INSERT INTO public.services(name,price,duration_minutes) VALUES('Synthetic slot service',100,60);
                INSERT INTO public.reservations(reference,customer_id,service_id,artist_id,start_at,end_at,reserved_until,total_price,status)
                SELECT 'SCHEMA-SLOT-1',c.id,s.id,a.id,'2027-01-01 10:00Z','2027-01-01 11:00Z','2027-01-01 11:30Z',100,'Pending Payment'
                FROM public.customers c,public.services s,public.makeup_artists a
                WHERE c.email='schema-rollback@example.test' AND s.name='Synthetic slot service' AND a.full_name='Synthetic slot artist';
                """,c,tx);
            await setup.ExecuteNonQueryAsync();
            await using var overlapping=new NpgsqlCommand("""
                INSERT INTO public.reservations(reference,customer_id,service_id,artist_id,start_at,end_at,reserved_until,total_price,status)
                SELECT 'SCHEMA-SLOT-2',customer_id,service_id,artist_id,'2027-01-01 11:10Z','2027-01-01 12:00Z','2027-01-01 12:30Z',100,'Confirmed'
                FROM public.reservations WHERE reference='SCHEMA-SLOT-1'
                """,c,tx);
            Check(await Rejected(async()=>{await overlapping.ExecuteNonQueryAsync();},PostgresErrorCodes.ExclusionViolation),
                "Pending Payment still protects the reservation buffer from overlapping bookings");
            await tx.RollbackAsync();
        }
        Check(await Sql(admin,"SELECT NOT EXISTS(SELECT 1 FROM public.customers WHERE email='schema-rollback@example.test') AND NOT EXISTS(SELECT 1 FROM public.account_identities WHERE login='schema-rollback@example.test') AND NOT EXISTS(SELECT 1 FROM public.reservations)") is true,
            "failed binding and booking transactions leave no Customer, identity or reservation debris");
    }
}
