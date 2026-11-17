using System.Text.Json;
using Npgsql;

// One-off: correct the SuperAdmin email (typo at bootstrap). Refuses without --confirm.
if (!args.Contains("--confirm=GLAMBYMARIYA10")) throw new InvalidOperationException("Refusing without --confirm=GLAMBYMARIYA10.");
if (args.Length != 3 || args[1] != "--confirm=GLAMBYMARIYA10" ||
    string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[2]) ||
    string.Equals(args[0].Trim(), args[2].Trim(), StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Supply the old email, --confirm=GLAMBYMARIYA10, and a different new email.");
var oldEmail = args[0].Trim();
var newEmail = args[2].Trim();
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
    using var check = new NpgsqlCommand("SELECT count(*) FROM public.admin_users WHERE lower(email)=lower(@email)", conn, (NpgsqlTransaction)tx);
    check.Parameters.AddWithValue("email", newEmail);
    if ((long)(await check.ExecuteScalarAsync())! > 0) throw new InvalidOperationException("Target email already in use.");
    using var up = new NpgsqlCommand("UPDATE public.admin_users SET email=@newEmail, username=@newEmail WHERE lower(email)=lower(@oldEmail)", conn, (NpgsqlTransaction)tx);
    up.Parameters.AddWithValue("newEmail", newEmail);
    up.Parameters.AddWithValue("oldEmail", oldEmail);
    var n = await up.ExecuteNonQueryAsync();
    if (n != 1) throw new InvalidOperationException($"Expected 1 row, updated {n}.");
    await tx.CommitAsync();
    Console.WriteLine("EMAIL-FIX-COMMITTED");
}
catch (Exception e) { await tx.RollbackAsync(); Console.WriteLine($"ROLLED-BACK: {e.Message.Split('\n')[0]}"); return 1; }
using var verify = new NpgsqlCommand("SELECT u.email, u.username, u.role, u.is_active, i.login FROM public.admin_users u LEFT JOIN public.account_identities i ON i.admin_id=u.id", conn);
using var r = await verify.ExecuteReaderAsync();
while (await r.ReadAsync()) Console.WriteLine($"admin: {r.GetString(0)} user={r.GetString(1)} role={r.GetString(2)} active={r.GetBoolean(3)} identity_login={r.GetString(4)}");
return 0;
