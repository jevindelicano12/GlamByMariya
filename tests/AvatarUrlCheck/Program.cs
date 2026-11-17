using System.Text.Json;
using Npgsql;

// Reads saved avatar URLs (public links only) and tests anonymous download.
// Never prints secrets.
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
    Password = Get("Database:Password"),
};
if (string.IsNullOrEmpty(csb.Password))
{
    // Password lives inside the full connection string secret instead.
    var full = Get("ConnectionStrings:MakeupDatabase");
    var m = System.Text.RegularExpressions.Regex.Match(full, @"://[^:]+:([^@]+)@");
    if (m.Success) csb.Password = Uri.UnescapeDataString(m.Groups[1].Value);
}
csb.SslMode = SslMode.VerifyFull;
csb.RootCertificate = Get("Database:RootCertificate");

using var conn = new NpgsqlConnection(csb.ToString());
await conn.OpenAsync();
using var cmd = new NpgsqlCommand("SELECT verified_email, avatar_url, notifications_enabled FROM public.account_preferences WHERE avatar_url IS NOT NULL", conn);
using var reader = await cmd.ExecuteReaderAsync();
var urls = new List<string>();
while (await reader.ReadAsync()) urls.Add(reader.GetString(1));
await reader.CloseAsync();
Console.WriteLine($"saved avatars: {urls.Count}");
if (args.Contains("--clear-b2"))
{
    foreach (var u in urls.Where(u => u.Contains("backblazeb2.com")))
    {
        using var clear = new NpgsqlCommand("UPDATE public.account_preferences SET avatar_url=NULL WHERE avatar_url=@u", conn);
        clear.Parameters.AddWithValue("u", u);
        Console.WriteLine($"cleared: {await clear.ExecuteNonQueryAsync()} row(s)");
    }
    return;
}
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
foreach (var url in urls)
{
    Console.WriteLine($"url len={url.Length} host={new Uri(url).Host}");
    try
    {
        using var res = await http.GetAsync(url);
        Console.WriteLine($"GET -> {(int)res.StatusCode} {res.StatusCode} bytes={res.Content.Headers.ContentLength}");
    }
    catch (Exception e) { Console.WriteLine($"GET failed: {e.GetType().Name}"); }
}
