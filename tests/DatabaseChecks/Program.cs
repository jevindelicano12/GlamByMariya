// Phase 2 database checks. Run only through tools/test-db.sh, which starts a throwaway
// postgres:17 container on 127.0.0.1 with a random marker:
//   tools/test-db.sh tests/DatabaseChecks [--migrator] [--schema] [--access]
using Npgsql;

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
