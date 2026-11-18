using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

// Runs only a locally published host with a cleared environment; no user-secrets
// or provider credentials are read, and no hosted services are contacted.
if (args.Length != 1) throw new InvalidOperationException("Pass the local publish directory.");
var publish = Path.GetFullPath(args[0]);
if (!File.Exists(Path.Combine(publish, "GlamByMariya.Web.dll"))) throw new InvalidOperationException("Publish output is missing.");
var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}
ProcessStartInfo StartInfo(Dictionary<string, string?> overrides, int port)
{
    var info = new ProcessStartInfo("dotnet") { WorkingDirectory = publish, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add(Path.Combine(publish, "GlamByMariya.Web.dll"));
    info.Environment.Clear();
    foreach (var key in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "DOTNET_ROOT", "ProgramFiles" })
        if (Environment.GetEnvironmentVariable(key) is { } value) info.Environment[key] = value;
    info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
    info.Environment["Deployment__Profile"] = "v2-public-only";
    info.Environment["PublicSite__InformationOnly"] = "true";
    info.Environment["Email__DeliveryDisabled"] = "true";
    info.Environment["Authentication__DurableSessionEnforcement"] = "false";
    info.Environment["AllowedHosts"] = "127.0.0.1";
    info.Environment["PORT"] = port.ToString();
    foreach (var (key, value) in overrides)
        if (value == null) info.Environment.Remove(key); else info.Environment[key] = value;
    return info;
}
int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}
var port = FreePort();
using (var process = Process.Start(StartInfo(new() { ["AllowedHosts"] = "127.0.0.1;v2-public.example.invalid" }, port))!)
{
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    try
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(3) };
        var ready = false;
        for (var attempt = 0; attempt < 100 && !process.HasExited; attempt++)
        {
            try { using var response = await client.GetAsync("/healthz"); ready = response.IsSuccessStatusCode; }
            catch (HttpRequestException) { }
            if (ready) break;
            await Task.Delay(100);
        }
        Check(ready, "Render PORT binding and public-only startup without provider credentials");
        var health = JsonDocument.Parse(await client.GetStringAsync("/healthz"));
        Check(health.RootElement.GetProperty("service").GetString() == "glam-by-mariya-v2" &&
              health.RootElement.GetProperty("mode").GetString() == "information-only", "health identifies V2 information-only host, not booking readiness");
        foreach (var path in new[] { "/", "/Home/About", "/Home/Services", "/Home/Gallery", "/Home/Contact", "/css/site.css", "/css/landing.css", "/favicon.svg" })
        {
            using var response = await client.GetAsync(path);
            Check(response.StatusCode == HttpStatusCode.OK, "public route or pink design asset " + path);
        }
        foreach (var path in new[] { "/Account/Login", "/Account/Register", "/Account/ForgotPassword", "/Bookings/Create", "/Admin", "/WebsiteContent", "/App_Data/Proofs/test.png" })
        {
            using var response = await client.GetAsync(path);
            Check(response.StatusCode == HttpStatusCode.NotFound, "unfinished/private route denied " + path);
        }
        foreach (var path in new[] { "/", "/Account/Login", "/Account/Register", "/Bookings/CorrectProof", "/Admin/Verify" })
        {
            using var response = await client.PostAsync(path, new StringContent(""));
            Check(response.StatusCode == HttpStatusCode.NotFound, "mutation denied " + path);
        }
        using var home = await client.GetAsync("/");
        Check(home.Headers.TryGetValues("X-Content-Type-Options", out var header) && header.Contains("nosniff"), "security headers retained");
        Check(!home.Headers.Contains("Set-Cookie"), "public site issues no session cookie");
        using (var approvedHost = new HttpRequestMessage(HttpMethod.Get, "/"))
        {
            approvedHost.Headers.Host = "v2-public.example.invalid";
            using var response = await client.SendAsync(approvedHost);
            Check(response.StatusCode == HttpStatusCode.OK, "explicit public DNS hostname accepted without remote DNS");
        }
        using (var hostileHost = new HttpRequestMessage(HttpMethod.Get, "/"))
        {
            hostileHost.Headers.Host = "unapproved.invalid";
            using var response = await client.SendAsync(hostileHost);
            Check(response.StatusCode == HttpStatusCode.BadRequest, "unapproved Host header rejected");
        }
        var html = await home.Content.ReadAsStringAsync();
        var assets = Regex.Matches(html, "(?:href|src)=\"(?<asset>/(?:css|js|images)/[^\"<>]+)\"")
            .Select(match => WebUtility.HtmlDecode(match.Groups["asset"].Value)).Distinct().ToArray();
        Check(assets.Length > 0, "rendered pink layout exposes a verifiable asset set");
        foreach (var asset in assets)
        {
            using var response = await client.GetAsync(asset);
            Check(response.IsSuccessStatusCode, "rendered layout asset served " + new Uri(client.BaseAddress!, asset).AbsolutePath);
        }
    }
    finally
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        await Task.WhenAll(output, errors); // Never print raw child diagnostics.
    }
}
foreach (var (label, config) in new (string, Dictionary<string, string?>)[] {
    ("public mode removed", new() { ["PublicSite__InformationOnly"] = "false" }),
    ("email enabled", new() { ["Email__DeliveryDisabled"] = "false" }),
    ("provider configuration attached", new() { ["Supabase__Url"] = "https://unused.example.invalid" }),
    ("staff provider key attached", new() { ["Supabase__ServiceKey"] = "synthetic-blocked" }),
    ("B2 key attached", new() { ["B2__AppKey"] = "synthetic-blocked" }),
    ("private signing directory attached", new() { ["DataProtection__KeyRingPath"] = "synthetic-blocked" }),
    ("Brevo key attached", new() { ["Email__BrevoApiKey"] = "synthetic-blocked" }),
    ("database configuration attached", new() { ["Database__Password"] = "synthetic-blocked" }),
    ("wildcard public host", new() { ["AllowedHosts"] = "*" }),
    ("missing explicit public host", new() { ["AllowedHosts"] = null }),
    ("blank public host", new() { ["AllowedHosts"] = "" }),
    ("URL instead of public hostname", new() { ["AllowedHosts"] = "https://unused.example.invalid" }),
    ("port in public hostname", new() { ["AllowedHosts"] = "unused.example.invalid:443" }),
    ("empty public host-list entry", new() { ["AllowedHosts"] = "127.0.0.1;" }),
    ("invalid profile", new() { ["Deployment__Profile"] = "unknown" }),
    ("loopback Customer test profile on Render", new() { ["Deployment__Profile"] = "v2-customer-auth-test" }),
    ("normal production missing database", new() { ["Deployment__Profile"] = null, ["PublicSite__InformationOnly"] = "false" }),
})
{
    using var process = Process.Start(StartInfo(config, FreePort()))!;
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
    finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    Check(process.ExitCode != 0, "startup fails closed: " + label);
    await Task.WhenAll(output, errors);
}
Console.WriteLine($"{checks} V2 deployment checks passed; local published host only.");
