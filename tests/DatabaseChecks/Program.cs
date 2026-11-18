// Phase 2 database checks. Run only through tools/test-db.sh, which starts a throwaway
// postgres:17 container on 127.0.0.1 with a random marker:
//   tools/test-db.sh tests/DatabaseChecks [--migrator] [--schema] [--access]
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;

if (args.Length < 3 || !int.TryParse(args[0], out var port) || port is < 1024 or > 65535 ||
    !Guid.TryParseExact(args[1], "N", out var marker) || marker == Guid.Empty)
    throw new Exception("Run via tools/test-db.sh; a fresh marked throwaway PostgreSQL is required.");
var sections = args.Skip(3).ToHashSet();
bool Run(string section) => sections.Count == 0 || sections.Contains(section);

var builder = new NpgsqlDataSourceBuilder($"Host=127.0.0.1;Port={port};Username=postgres;Database=postgres;SSL Mode=Disable;GSS Encryption Mode=Disable;Pooling=false;Timeout=15;Command Timeout=120");
async Task Guard(NpgsqlConnection physical)
{
    // The random marker proves this is the container tools/test-db.sh just started.
    await using var command = new NpgsqlCommand("""
        SELECT current_database()='postgres' AND current_user='postgres'
          AND current_setting('glam.docker_test',true)='on'
          AND current_setting('glam.payment_review_marker',true)=@marker
        """, physical);
    command.Parameters.AddWithValue("marker", marker.ToString("N"));
    if ((bool?)await command.ExecuteScalarAsync() != true) throw new System.Security.SecurityException("Unsafe PostgreSQL physical connection.");
}
builder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), Guard);
await using var source = builder.Build();

var root = Directory.GetCurrentDirectory();
var migrations = Path.Combine(root, "database", "migrations");
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
async Task<object?> Scalar(string sql)
{
    await using var c = await source.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(sql, c);
    return await cmd.ExecuteScalarAsync();
}
async Task Exec(string sql)
{
    await using var c = await source.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(sql, c);
    await cmd.ExecuteNonQueryAsync();
}
async Task ResetPublic() => await Exec("DROP SCHEMA public CASCADE; CREATE SCHEMA public; GRANT ALL ON SCHEMA public TO public;");
async Task<string> Refused(Func<Task> action)
{
    try { await action(); } catch (InvalidOperationException e) { return e.Message; }
    return "";
}

var tempRoot = Path.GetFullPath(args[2]);
var http = new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
var env = new TestEnv { ContentRootPath = tempRoot };
PublicStudioStore NewStore(Dictionary<string, string?>? settings = null)
{
    var config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new()).Build();
    var options = new DbContextOptionsBuilder<PublicMakeupDbContext>();
    options.UseNpgsql(source);
    return new PublicStudioStore(new PublicMakeupDbContext(options.Options), http, env, config, new GlamByMariya.Infrastructure.Integrations.LocalAppFileStore(env));
}

if (Run("--migrator"))
{
    var temp = Path.Combine(Path.GetTempPath(), "glam-migrator-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        await ResetPublic();
        File.WriteAllText(Path.Combine(temp, "0001_a.sql"), "CREATE TABLE public.first_table(id int);");
        File.WriteAllText(Path.Combine(temp, "0002_b.sql"), "CREATE TABLE public.second_table(id int);");
        var applied = await SqlMigrator.ApplyAsync(source, temp);
        var versions = (long)(await Scalar("SELECT count(*) FROM public.schema_migrations WHERE version IN (1,2)"))!;
        Check(applied.SequenceEqual(["0001_a.sql", "0002_b.sql"]) && versions == 2, "applies 0001 and 0002 in order and records both");
        Check((await SqlMigrator.ApplyAsync(source, temp)).Count == 0, "second run applies nothing");
        Check(SqlMigrator.Checksum("a\r\nb") == SqlMigrator.Checksum("a\nb"), "CRLF and LF give the same checksum");

        File.WriteAllText(Path.Combine(temp, "0001_a.sql"), "CREATE TABLE public.first_table(id bigint);");
        Check((await Refused(() => SqlMigrator.ApplyAsync(source, temp))).Contains("0001_a.sql"), "an edited applied file is refused by name");
        File.WriteAllText(Path.Combine(temp, "0001_a.sql"), "CREATE TABLE public.first_table(id int);");

        File.WriteAllText(Path.Combine(temp, "0004_d.sql"), "SELECT 1;");
        Check((await Refused(() => SqlMigrator.ApplyAsync(source, temp))) != "", "a gap is refused");
        File.Delete(Path.Combine(temp, "0004_d.sql"));

        File.WriteAllText(Path.Combine(temp, "0003_c.sql"), "CREATE TABLE public.third_table(id int); SELECT broken syntax here;");
        var failed = false;
        try { await SqlMigrator.ApplyAsync(source, temp); } catch (PostgresException) { failed = true; }
        Check(failed && await Scalar("SELECT to_regclass('public.third_table')::text") is DBNull
              && (long)(await Scalar("SELECT count(*) FROM public.schema_migrations WHERE version=3"))! == 0,
            "a failing file leaves nothing behind");

        await ResetPublic();
        await SqlMigrator.ApplyAsync(source, migrations);
        Check(await Scalar("SELECT to_regclass('public.reservations')::text") is not DBNull, "real folder builds the base tables");
    }
    finally { Directory.Delete(temp, true); }
    Console.WriteLine($"All {checks} migrator checks passed.");
}

if (Run("--schema"))
{
    var before = checks;
    await ResetPublic();
    await SqlMigrator.ApplyAsync(source, migrations);
    // Every relation and function the app reads in database mode (mixed bookings and
    // website content excluded: they stay switched off there).
    string[] relations = ["account_identities", "account_preferences", "admin_invitations", "admin_users", "artist_availability", "audit_logs",
        "blocked_slots", "business_settings", "catalog_categories", "catalog_images", "customers", "data_protection_keys", "email_codes",
        "email_code_cooldowns", "makeup_addons", "makeup_artists", "media_assets", "notifications", "payment_adjustments", "payment_methods",
        "payment_revisions", "payments", "refunds", "reschedule_requests", "reservation_addons", "reservation_closures", "reservation_history",
        "reservations", "service_areas", "services", "mvc_blocks", "mvc_booking_addons", "mvc_booking_summary", "mvc_catalog_images",
        "mvc_closures", "mvc_payments", "mvc_reschedules", "mvc_roles", "mvc_services", "mvc_users"];
    var missing = new List<string>();
    foreach (var name in relations)
        if (await Scalar($"SELECT to_regclass('public.{name}')::text") is DBNull) missing.Add(name);
    foreach (var name in new[] { "mvc_assert_role", "mvc_check_slot" })
        if ((long)(await Scalar($"SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public' AND p.proname='{name}'"))! == 0) missing.Add(name + "()");
    Check(missing.Count == 0, "every object the app reads exists" + (missing.Count > 0 ? ": missing " + string.Join(", ", missing) : ""));
    var people = (long)(await Scalar("SELECT (SELECT count(*) FROM public.customers)+(SELECT count(*) FROM public.admin_users)+(SELECT count(*) FROM public.makeup_artists)+(SELECT count(*) FROM public.reservations)"))!;
    Check(people == 0, "seed has no people and no bookings");
    var catalog = (string)(await Scalar("""
        SELECT (SELECT count(*) FROM public.services WHERE is_active AND NOT is_archived)||','||(SELECT count(*) FROM public.makeup_addons)||','||
               (SELECT count(*) FROM public.service_areas WHERE is_active)||','||(SELECT count(*) FROM public.payment_methods)||','||
               (SELECT count(*) FROM public.business_settings WHERE id=1)
        """))!;
    Check(catalog == "3,3,6,4,1", $"seed has the catalog (services,add-ons,areas,methods,settings = {catalog})");
    var store = NewStore();
    Check(store.Services.Count(x => x.Active && !x.Archived) == 3 && store.AddOns.Count == 3 && store.Settings.OpenHour == 8 && store.Settings.BufferMinutes == 45,
        "the app reads the catalog and settings through its views");
    Console.WriteLine($"All {checks - before} schema checks passed.");
}

sealed class TestEnv : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "DatabaseChecks";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
}
