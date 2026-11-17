using System.Text.Json;
using Npgsql;

// One-off: rename placeholder artist id=1 to the real studio artist and
// deactivate the unused placeholder id=2. Refuses without --confirm.
if (!args.Contains("--confirm=MARIA-NOELLA")) throw new InvalidOperationException("Refusing without --confirm=MARIA-NOELLA.");
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
var m = System.Text.RegularExpressions.Regex.Match(Get("ConnectionStrings:MakeupDatabase"), @"://[^:]+:([^@]+)@");
csb.Password = Uri.UnescapeDataString(m.Groups[1].Value);
csb.SslMode = SslMode.VerifyFull;
csb.RootCertificate = Get("Database:RootCertificate");
using var conn = new NpgsqlConnection(csb.ToString());
await conn.OpenAsync();
using var tx = await conn.BeginTransactionAsync();
try
{
    foreach (var sql in new[]
    {
        "UPDATE public.makeup_artists SET full_name='MariaNoella' WHERE id=1",
        "UPDATE public.makeup_artists SET is_active=false WHERE id=2",
    })
    {
        using var cmd = new NpgsqlCommand(sql, conn, (NpgsqlTransaction)tx);
        Console.WriteLine($"updated {await cmd.ExecuteNonQueryAsync()}: {sql[..40]}");
    }
    await tx.CommitAsync();
    Console.WriteLine("ARTIST-FIX-COMMITTED");
}
catch (Exception e) { await tx.RollbackAsync(); Console.WriteLine($"ROLLED-BACK: {e.Message.Split('\n')[0]}"); return 1; }
return 0;
