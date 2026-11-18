using Microsoft.EntityFrameworkCore;

namespace GlamByMariya.Infrastructure.Data;

public static class DatabaseSetup
{
    public static async Task ExecuteScriptAsync(MakeupDbContext db,string sql)
    {
        // Execute reviewed DDL verbatim: ExecuteSqlRaw's format processing would
        // misinterpret literal JSON '{}' and PL/pgSQL braces as format items.
        await using var connectionLease = await DatabaseConnectionLease.OpenAsync(db.Database);
        await using var command=db.Database.GetDbConnection().CreateCommand();
        command.CommandText=sql;
        command.CommandTimeout=120;
        try { await command.ExecuteNonQueryAsync(); }
        catch
        {
            command.CommandText="ROLLBACK;";
            await command.ExecuteNonQueryAsync();
            throw;
        }
    }
    public static async Task RunAsync(WebApplication app,string command)
    {
        using var scope=app.Services.CreateScope();
        var schema = app.Configuration["Database:Schema"] ?? "makeup";
        // Public mode uses its own context; legacy makeup commands keep working.
        var pdb = schema == "public" ? scope.ServiceProvider.GetService<PublicMakeupDbContext>() : null;
        var db=scope.ServiceProvider.GetRequiredService<MakeupDbContext>();
        if(command=="--database-profile")
        {
            await PublicDatabaseProfile.RunAsync(db,app.Environment.ContentRootPath);
            return;
        }
        if(command=="--storage-migrate")
        {
            await StorageMigration.RunAsync(app);
            return;
        }
        if(command=="--database-inspect")
        {
            await DatabaseInspection.InspectAsync(db,app.Configuration,app.Environment.ContentRootPath);
            return;
        }
        if(command=="--database-migrate")
        {
            if (pdb == null) throw new InvalidOperationException("--database-migrate targets the public schema only. Set Database:Schema=public.");
            var before = await PublicCounts(pdb);
            await pdb.Database.MigrateAsync();
            var after = await PublicCounts(pdb);
            foreach (var table in new[] { "customers","admin_users","makeup_artists","services","reservations","payments","blocked_slots","artist_availability","reservation_history" })
                if (before.GetValueOrDefault(table) != after.GetValueOrDefault(table))
                    throw new InvalidOperationException($"Row-count mismatch on {table}: {before.GetValueOrDefault(table)} before, {after.GetValueOrDefault(table)} after. Migration rolled back by EF; restore from backup before retrying.");
            Console.WriteLine("Public EF Core migration applied with all legacy row counts preserved.");
            Console.WriteLine(string.Join(", ", after.OrderBy(kv=>kv.Key).Select(kv=>$"{kv.Key}={kv.Value}")));
            return;
        }
        if(command=="--database-reset-password")
        {
            var config=app.Configuration;
            var email=config["Bootstrap:AdminEmail"]??throw new InvalidOperationException("Set Bootstrap__AdminEmail locally.");
            var password=config["Bootstrap:AdminPassword"]??throw new InvalidOperationException("Set Bootstrap__AdminPassword locally; never put it in source or command arguments.");
            var store=scope.ServiceProvider.GetRequiredService<PostgresStudioStore>();
            store.ResetPassword(email, password);
            Console.WriteLine("Password updated. Clear the bootstrap password from your environment/user-secrets.");
            return;
        }
        if(command=="--database-smoke")
        {
            if (pdb == null) throw new InvalidOperationException("--database-smoke targets the public schema only.");
            await PublicSmokeTest.RunAsync(app);
            return;
        }
        if((app.Configuration["Database:Schema"]??"makeup") != "makeup" && command=="--database-init")
            throw new InvalidOperationException("The legacy initializer must not be used for an existing public-schema project. Inspect public first, map existing data, then use reviewed EF Core migrations. No schema changes were attempted.");
        if(command=="--database-create-admin" && pdb == null)
            await DatabaseInspection.VerifyApplicationAsync(db,app.Configuration);
        if(command=="--database-create-admin" && pdb != null)
        {
            var views = await pdb.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_views WHERE schemaname='public' AND viewname LIKE 'mvc_%'").SingleAsync();
            if (views < 9) throw new InvalidOperationException($"Public integration views are missing (found {views}). Run --database-migrate first.");
            _ = await pdb.Set<SettingsRow>().AsNoTracking().SingleAsync(x => x.Id == 1);
        }
        if(command=="--database-create-superadmin")
        {
            // One-time owner bootstrap. NOT gated on zero users: it requires an
            // explicit opt-in flag plus owner credentials, and it permanently
            // refuses once any SuperAdmin exists, even with the flag still set.
            if (pdb == null) throw new InvalidOperationException("--database-create-superadmin targets the public schema only. Set Database:Schema=public.");
            var config=app.Configuration;
            if (!string.Equals(config["Bootstrap:AllowSuperAdminSetup"], "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SuperAdmin setup is disabled. Set Bootstrap__AllowSuperAdminSetup=true temporarily as the system owner, then unset it.");
            var email=config["Bootstrap:OwnerEmail"]??throw new InvalidOperationException("Set Bootstrap__OwnerEmail locally.");
            var password=config["Bootstrap:OwnerPassword"]??throw new InvalidOperationException("Set Bootstrap__OwnerPassword locally; never put it in source or command arguments.");
            var store=scope.ServiceProvider.GetRequiredService<PublicStudioStore>();
            var user=store.CreateInitialSuperAdmin(config["Bootstrap:OwnerFirstName"]??"Business",config["Bootstrap:OwnerLastName"]??"Owner",
                email,password);
            store.RecordAudit(user.Id,$"Initial SuperAdmin created for {email}.");
            Console.WriteLine($"Owner (SuperAdmin) created for {email}. Sign in at /Account/Login with this email and password. Now unset Bootstrap__AllowSuperAdminSetup and Bootstrap__OwnerPassword.");
            return;
        }
        if(command=="--database-init")
        {
            if(await db.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname='makeup') AS \"Value\"").SingleAsync())
                throw new InvalidOperationException("The makeup schema already exists. Initialization will not overwrite it. Use --database-check and review upgrades explicitly.");
            var root=AppContext.BaseDirectory;
            var designSql=await File.ReadAllTextAsync(Path.Combine(root,"MakeupBookingDatabase.sql"));
            var end=designSql.IndexOf("-- 9. Baseline settings/catalog",StringComparison.Ordinal);
            if(end<0) throw new InvalidOperationException("Cannot identify the production-safe boundary of the schema script.");
            var seed=await File.ReadAllTextAsync(Path.Combine(root,"Data","Sql","ProductionSeed.sql"));
            // The DDL starts its own transaction. Production seed excludes all
            // demonstration users, bookings, payments, refunds and notifications.
            // These are reviewed local SQL files, never request/user input.
            var productionScript=designSql[..end]+seed+"\nCOMMIT;";
            await ExecuteScriptAsync(db,productionScript);
            Console.WriteLine("Production schema and baseline catalog/settings applied. No demo accounts or bookings were inserted.");
        }
        else if(command=="--database-create-admin")
        {
            var config=app.Configuration;
            var email=config["Bootstrap:AdminEmail"]??throw new InvalidOperationException("Set Bootstrap__AdminEmail locally.");
            var password=config["Bootstrap:AdminPassword"]??throw new InvalidOperationException("Set Bootstrap__AdminPassword locally; never put it in source or command arguments.");
            var store=scope.ServiceProvider.GetRequiredService<PostgresStudioStore>();
            if(store.FindUser(email)!=null) throw new InvalidOperationException("That email already exists. No account was overwritten or reactivated.");
            var user=store.AddUser(config["Bootstrap:AdminFirstName"]??"Makeup",config["Bootstrap:AdminLastName"]??"Admin",
                email,password,"Admin",35,"","Address on file");
            Console.WriteLine($"Admin created. Artist ID: {user.Id}. Clear the bootstrap password from your environment/user-secrets.");
        }
        else
        {
            if (schema == "public" && pdb != null)
            {
                var views = await pdb.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_views WHERE schemaname='public' AND viewname LIKE 'mvc_%'").SingleAsync();
                if (views < 9) throw new InvalidOperationException($"Public integration views are missing (found {views}). Run --database-migrate first.");
                var counts = await PublicCounts(pdb);
                _ = pdb.Set<SettingsRow>().AsNoTracking().Single(x => x.Id == 1);
                Console.WriteLine($"TLS connection and public integration verified ({views} views). " + string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
                return;
            }
            await DatabaseInspection.VerifyApplicationAsync(db,app.Configuration);
            var count=await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema='makeup' AND table_type='BASE TABLE'").SingleAsync();
            if(count!=22) throw new InvalidOperationException($"Connection succeeded but expected 22 makeup tables; found {count}. Apply/review the schema first.");
            _=await db.Set<SettingsRow>().AsNoTracking().SingleAsync(x=>x.Id==1);
            Console.WriteLine("TLS database connection and makeup schema read access verified (22 tables).");
        }
    }

    private static async Task<Dictionary<string,long>> PublicCounts(PublicMakeupDbContext pdb)
    {
        var rows = await pdb.Database.SqlQueryRaw<PublicDatabaseProfile.CountRow>("""
            SELECT 'customers'::text AS "Name",count(*) AS "Count" FROM public.customers
            UNION ALL SELECT 'admin_users',count(*) FROM public.admin_users
            UNION ALL SELECT 'makeup_artists',count(*) FROM public.makeup_artists
            UNION ALL SELECT 'services',count(*) FROM public.services
            UNION ALL SELECT 'reservations',count(*) FROM public.reservations
            UNION ALL SELECT 'payments',count(*) FROM public.payments
            UNION ALL SELECT 'blocked_slots',count(*) FROM public.blocked_slots
            UNION ALL SELECT 'artist_availability',count(*) FROM public.artist_availability
            UNION ALL SELECT 'reservation_history',count(*) FROM public.reservation_history
            """).ToListAsync();
        return rows.ToDictionary(r => r.Name, r => r.Count);
    }
}
