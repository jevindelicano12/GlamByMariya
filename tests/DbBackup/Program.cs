using System.Text.Json;
using Npgsql;

// Logical backup: COPY every public base table to CSV + manifest with counts.
// Read-only. Never prints secrets. Usage: DbBackup [verify-only]
var secretsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Microsoft", "UserSecrets", "GlamByMariya-V2-Independent", "secrets.json");
using var doc = JsonDocument.Parse(File.ReadAllText(secretsPath));
string Get(string key) => doc.RootElement.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";

var csb = new NpgsqlConnectionStringBuilder
{
    Host = "aws-0-ap-south-1.pooler.supabase.com",
    Port = 5432,
    Database = "postgres",
    Username = "postgres.independent-placeholder",
};
var full = Get("ConnectionStrings:MakeupDatabase");
var m = System.Text.RegularExpressions.Regex.Match(full, @"://[^:]+:([^@]+)@");
csb.Password = Uri.UnescapeDataString(m.Groups[1].Value);
csb.SslMode = SslMode.VerifyFull;
csb.RootCertificate = Get("Database:RootCertificate");

var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Frontend", "GlamByMariya.Web", "App_Data", "Backups", stamp);
var verifyOnly = args.Contains("verify-only");
if (!verifyOnly) Directory.CreateDirectory(root);

using var conn = new NpgsqlConnection(csb.ToString());
await conn.OpenAsync();
using var list = new NpgsqlCommand(
    "SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY tablename", conn);
using var reader = await list.ExecuteReaderAsync();
var tables = new List<string>();
while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
await reader.CloseAsync();

var manifest = new Dictionary<string, long>();
foreach (var table in tables)
{
    using var count = new NpgsqlCommand($"SELECT count(*) FROM public.\"{table}\"", conn);
    var n = (long)(await count.ExecuteScalarAsync())!;
    manifest[table] = n;
    Console.WriteLine($"{table}={n}");
    if (verifyOnly) continue;
    using var exporter = conn.BeginTextExport($"COPY public.\"{table}\" TO STDOUT WITH CSV HEADER");
    using var file = File.CreateText(Path.Combine(root, table + ".csv"));
    string? line;
    while ((line = await exporter.ReadLineAsync()) != null) await file.WriteLineAsync(line);
}
if (!verifyOnly)
{
    File.WriteAllText(Path.Combine(root, "manifest.json"),
        JsonSerializer.Serialize(new { taken_at = stamp, tables = manifest }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"backup: {root} tables={tables.Count} rows={manifest.Values.Sum()}");
}
else Console.WriteLine($"verify: tables={tables.Count} rows={manifest.Values.Sum()}");

// User emails for the deletion-approval list (console only, never written to disk here).
foreach (var (sql, label) in new[] {
    ("SELECT app_id, email, is_active FROM public.customers ORDER BY id", "customer"),
    ("SELECT app_id, coalesce(email, username), is_active FROM public.admin_users ORDER BY id", "admin") })
{
    using var cmd = new NpgsqlCommand(sql, conn);
    using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync()) Console.WriteLine($"{label}: {r.GetGuid(0)} | {r.GetString(1)} | active={r.GetBoolean(2)}");
}
