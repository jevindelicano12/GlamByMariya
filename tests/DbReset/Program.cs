using System.Text.Json;
using Npgsql;

// One-shot TEST-DATA reset. Deletes ALL user-domain rows (every account is
// owner-declared test). Refuses without --confirm=RESET-ALL-TEST-DATA.
// Single transaction: any error rolls everything back. Never prints secrets.
if (!args.Contains("--confirm=RESET-ALL-TEST-DATA")) throw new InvalidOperationException("Refusing: pass --confirm=RESET-ALL-TEST-DATA.");

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
using var setup = new NpgsqlCommand("SET session_replication_role = 'replica'", conn);
await setup.ExecuteNonQueryAsync(); // owner session: bypass append-only guards for this reset only
using var tx = await conn.BeginTransactionAsync();
async Task<long> Del(string sql)
{
    using var cmd = new NpgsqlCommand(sql, conn, (NpgsqlTransaction)tx);
    var n = (await cmd.ExecuteNonQueryAsync());
    Console.WriteLine($"deleted {n}: {sql[7..Math.Min(sql.Length, 90)]}");
    return n;
}
try
{
    await Del("DELETE FROM public.payment_revisions");
    await Del("DELETE FROM public.payment_adjustments");
    await Del("DELETE FROM public.refunds");
    await Del("DELETE FROM public.reservation_closures");
    await Del("DELETE FROM public.payments");
    await Del("DELETE FROM public.reschedule_requests");
    await Del("DELETE FROM public.reservation_history");
    await Del("DELETE FROM public.reservation_addons");
    await Del("DELETE FROM public.notifications");
    await Del("DELETE FROM public.audit_logs");
    await Del("DELETE FROM public.reservations");
    await Del("DELETE FROM public.account_preferences");
    await Del("DELETE FROM public.admin_invitations");
    await Del("DELETE FROM public.account_identities");
    await Del("DELETE FROM public.customers");
    await Del("DELETE FROM public.admin_users");
    await tx.CommitAsync();
    Console.WriteLine("RESET-COMMITTED");
}
catch (Exception e)
{
    await tx.RollbackAsync();
    Console.WriteLine($"RESET-ROLLED-BACK: {e.GetType().Name}: {e.Message.Split('\n')[0]}");
    return 1;
}
return 0;
