using System.Net;
using System.Security;
using System.Text;
using GlamByMariya.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

// This runner never loads application configuration, user secrets or files.
if (args.SequenceEqual(new[] { "--self-test" }))
{
    const string safe = "https://isolated-test.supabase.co";
    void Check(bool value) { if (!value) throw new Exception("Offline safety check failed."); }
    Check(OriginPolicy.Valid(safe, safe));
    foreach (var bad in new[] { "", "http://isolated-test.supabase.co", "https://other.supabase.co", "https://isolated-test.supabase.co/path", "https://user@isolated-test.supabase.co", "https://isolated-test.supabase.co:444", "https://isolated-test.supabase.co?x=1", "https://isolated-test.supabase.co.evil.test" })
        Check(!OriginPolicy.Valid(bad, safe));
    Check(!OriginPolicy.Valid(safe, ""));
    Check(OriginPolicy.AllowedRequest(new Uri(safe + "/auth/v1/signup"), HttpMethod.Post, safe));
    Check(OriginPolicy.AllowedRequest(new Uri(safe + "/auth/v1/token?grant_type=password"), HttpMethod.Post, safe));
    Check(!OriginPolicy.AllowedRequest(new Uri("https://other.supabase.co/auth/v1/signup"), HttpMethod.Post, safe));
    Check(!OriginPolicy.AllowedRequest(new Uri(safe + "/auth/v1/admin/users"), HttpMethod.Post, safe));
    Check(!OriginPolicy.AllowedRequest(new Uri(safe + "/auth/v1/user"), HttpMethod.Post, safe));
    Check(OriginPolicy.AllowedForMode("--recovery-email", new Uri(safe + "/auth/v1/recover"), HttpMethod.Post));
    Check(!OriginPolicy.AllowedForMode("--recovery-email", new Uri(safe + "/auth/v1/user"), HttpMethod.Put));
    Check(!OriginPolicy.AllowedForMode("--recovery-complete", new Uri(safe + "/auth/v1/signup"), HttpMethod.Post));
    Check(OriginPolicy.AllowedForMode("--recovery-complete", new Uri(safe + "/auth/v1/user"), HttpMethod.Put));
    Check(OriginPolicy.AllowedForMode("--login-only", new Uri(safe + "/auth/v1/token?grant_type=password"), HttpMethod.Post));
    Check(!OriginPolicy.AllowedForMode("--login-only", new Uri(safe + "/auth/v1/signup"), HttpMethod.Post));
    Check(!OriginPolicy.AllowedForMode("--login-only", new Uri(safe + "/auth/v1/recover"), HttpMethod.Post));
    Check(!OriginPolicy.AllowedForMode("--login-only", new Uri(safe + "/auth/v1/user"), HttpMethod.Put));
    using var client = new HttpClient(new OriginGuard(safe) { InnerHandler = new FakeTerminal() });
    using var accepted = await client.PostAsync(safe + "/auth/v1/signup", new StringContent("{}"));
    Check(accepted.StatusCode == HttpStatusCode.OK);
    try { await client.PostAsync("https://other.supabase.co/auth/v1/signup", new StringContent("{}")); throw new Exception("Unsafe request passed the guard."); }
    catch (InvalidOperationException) { }
    using var recoveryOnly = new HttpClient(new OriginGuard(safe, "--recovery-email") { InnerHandler = new FakeTerminal() });
    using var recoveryAccepted = await recoveryOnly.PostAsync(safe + "/auth/v1/recover", new StringContent("{}"));
    Check(recoveryAccepted.StatusCode == HttpStatusCode.OK);
    try { await recoveryOnly.PutAsync(safe + "/auth/v1/user", new StringContent("{}")); throw new Exception("Recovery-email mode allowed password mutation."); }
    catch (InvalidOperationException) { }
    var fakeConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Supabase:Url"]=safe, ["Supabase:AnonKey"]="synthetic" }).Build();
    var fakeProvider = new SupabaseAuthService(new FakeProviderFactory(safe), fakeConfig, NullLogger<SupabaseAuthService>.Instance);
    var synthetic = await fakeProvider.SignUpAsync("synthetic@example.test", "Synthetic123!");
    var otp = await fakeProvider.VerifyOtpAsync("synthetic@example.test", "123456", "signup");
    var login = await fakeProvider.SignInWithPasswordAsync("synthetic@example.test", "Synthetic123!");
    var loginOnlyProvider = new SupabaseAuthService(new FakeProviderFactory(safe, "--login-only"), fakeConfig, NullLogger<SupabaseAuthService>.Instance);
    var loginOnly = await loginOnlyProvider.SignInWithPasswordAsync("synthetic@example.test", "Synthetic123!");
    Check(loginOnly.User.Id == FakeProviderTerminal.Subject && loginOnly.User.EmailConfirmedAt != null);
    try { await loginOnlyProvider.SendRecoveryAsync("synthetic@example.test"); throw new Exception("Login-only allowed recovery."); }
    catch (InvalidOperationException) { }
    await fakeProvider.SendRecoveryAsync("synthetic@example.test");
    var recovery = await fakeProvider.VerifyOtpAsync("synthetic@example.test", "123456", "recovery");
    Check(synthetic.Id == FakeProviderTerminal.Subject && otp?.User.EmailConfirmedAt != null && login.User.EmailConfirmedAt != null && recovery?.User.Id == synthetic.Id);
    await fakeProvider.UpdatePasswordVerifiedAsync(recovery!.AccessToken, "SyntheticNew123!", Guid.Parse(synthetic.Id), "synthetic@example.test");
    Console.WriteLine("PASS offline origin and request allowlist checks; no credentials read and no network access.");
    Console.WriteLine("PASS fake-only signup, OTP, login and recovery response shapes through the existing Supabase service.");
    return;
}

var mode = args.Length == 1 ? args[0] : "";
if (mode is not ("--run" or "--recovery-email" or "--recovery-complete" or "--login-only") || Environment.GetEnvironmentVariable("GLAM_AUTH_TEST_WRITES") != "YES" ||
    Environment.GetEnvironmentVariable("GLAM_AUTH_TEST_PROJECT") != "glam-by-mariya-auth-test")
    throw new InvalidOperationException("Disabled: explicit test project and write authorization are required.");
var origin = Environment.GetEnvironmentVariable("GLAM_AUTH_TEST_URL") ?? "";
var allowed = Environment.GetEnvironmentVariable("GLAM_AUTH_TEST_ALLOWED_ORIGIN") ?? "";
var key = Environment.GetEnvironmentVariable("GLAM_AUTH_TEST_PUBLISHABLE_KEY") ?? "";
if (!OriginPolicy.Valid(origin, allowed) || !key.StartsWith("sb_publishable_", StringComparison.Ordinal) || key.Any(char.IsWhiteSpace))
    throw new InvalidOperationException("Test-only origin, exact allowlist or publishable key is invalid.");
if (Console.IsInputRedirected) throw new InvalidOperationException("Interactive confirmation is required.");

var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Supabase:Url"]=origin, ["Supabase:AnonKey"]=key }).Build();
var provider = new SupabaseAuthService(new IsolatedClientFactory(origin, mode), config, NullLogger<SupabaseAuthService>.Instance);
Console.Write("Approved synthetic mailbox: ");
var email = Console.ReadLine()?.Trim() ?? "";
if (!System.Net.Mail.MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || email.Length > 254)
    throw new InvalidOperationException("A single synthetic mailbox is required.");
string Secret(string prompt)
{
    Console.Write(prompt); var text = new StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept:true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (text.Length != 0) text.Length--; continue; }
        if (!char.IsControl(k.KeyChar)) text.Append(k.KeyChar);
    }
    Console.WriteLine();
    return text.ToString();
}
void Confirm(string operation)
{
    Console.Write($"Type YES to perform {operation} against the explicitly allowlisted test project: ");
    if (Console.ReadLine() != "YES") throw new OperationCanceledException("Operation not approved.");
}
void RequireIdentity(SupabaseAuthService.AuthSession? session, Guid subject)
{
    if (session == null || string.IsNullOrWhiteSpace(session.AccessToken) || session.User.EmailConfirmedAt == null ||
        !Guid.TryParseExact(session.User.Id, "D", out var id) || id != subject ||
        !string.Equals(session.User.Email.Trim(), email, StringComparison.OrdinalIgnoreCase))
        throw new SecurityException("Provider identity or confirmed email did not match; test stopped.");
}
try
{
    if (mode == "--login-only")
    {
        Console.Write("Expected provider subject UUID for the approved synthetic account: ");
        if (!Guid.TryParseExact(Console.ReadLine(), "D", out var expectedSubject) || expectedSubject == Guid.Empty)
            throw new InvalidOperationException("A verified synthetic subject UUID is required.");
        var currentPassword = Secret("Synthetic account password (not echoed): ");
        if (string.IsNullOrEmpty(currentPassword)) throw new InvalidOperationException("A password is required.");
        Confirm("password login");
        var verifiedLogin = await provider.SignInWithPasswordAsync(email, currentPassword);
        RequireIdentity(verifiedLogin, expectedSubject);
        Console.WriteLine("Password login response matched the expected synthetic subject and confirmed email.");
        return;
    }
    if (mode == "--recovery-email")
    {
        Confirm("recovery email delivery");
        await provider.SendRecoveryAsync(email);
        Console.WriteLine("Recovery request accepted; delivery is not assumed. No password was changed.");
        return;
    }
    if (mode == "--recovery-complete")
    {
        Console.Write("Expected provider subject UUID for the approved synthetic account: ");
        if (!Guid.TryParseExact(Console.ReadLine(), "D", out var expectedSubject) || expectedSubject == Guid.Empty)
            throw new InvalidOperationException("A verified synthetic subject UUID is required.");
        var code = Secret("Recovery OTP (not echoed): ");
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) throw new InvalidOperationException("Recovery OTP must be six digits.");
        Confirm("recovery OTP verification");
        var recoverySession = await provider.VerifyOtpAsync(email, code, "recovery");
        RequireIdentity(recoverySession, expectedSubject);
        Console.WriteLine("Recovery OTP response matched the expected subject and confirmed email; no password changed yet.");
        var replacement = Secret("New synthetic password (not echoed): ");
        if (PasswordPolicy.ValidateNewPassword(replacement) != null) throw new InvalidOperationException("New synthetic password does not meet local policy.");
        Confirm("synthetic provider password change");
        await provider.UpdatePasswordVerifiedAsync(recoverySession!.AccessToken, replacement, expectedSubject, email);
        Console.WriteLine("Recovery password update response matched the synthetic subject and email.");
        return;
    }
    var password = Secret("Synthetic password (not echoed): ");
    if (PasswordPolicy.ValidateNewPassword(password) != null) throw new InvalidOperationException("Synthetic password does not meet local policy.");
    Confirm("signup and email delivery");
    var signup = await provider.SignUpAsync(email, password);
    if (!Guid.TryParseExact(signup.Id, "D", out var subject) || subject == Guid.Empty || !string.Equals(signup.Email.Trim(), email, StringComparison.OrdinalIgnoreCase))
        throw new SecurityException("Signup response did not match; test stopped.");
    Console.WriteLine("Signup response matched the supplied synthetic mailbox; no local account or ticket created.");
    var otp = Secret("Signup OTP (not echoed): ");
    if (otp.Length != 6 || !otp.All(char.IsAsciiDigit)) throw new InvalidOperationException("Signup OTP must be six digits.");
    Confirm("signup OTP verification");
    var verified = await provider.VerifyOtpAsync(email, otp, "signup");
    RequireIdentity(verified, subject);
    Console.WriteLine("Signup OTP response matched subject and confirmed email.");
    Confirm("password login");
    var login = await provider.SignInWithPasswordAsync(email, password);
    RequireIdentity(login, subject);
    Console.WriteLine("Password login response matched subject and confirmed email.");
    Confirm("recovery email delivery");
    await provider.SendRecoveryAsync(email);
    Console.WriteLine("Recovery request accepted; delivery is not assumed.");
    var recoveryOtp = Secret("Recovery OTP (not echoed): ");
    if (recoveryOtp.Length != 6 || !recoveryOtp.All(char.IsAsciiDigit)) throw new InvalidOperationException("Recovery OTP must be six digits.");
    var newPassword = Secret("New synthetic password (not echoed): ");
    if (PasswordPolicy.ValidateNewPassword(newPassword) != null) throw new InvalidOperationException("New synthetic password does not meet local policy.");
    Confirm("recovery OTP verification");
    var recovery = await provider.VerifyOtpAsync(email, recoveryOtp, "recovery");
    RequireIdentity(recovery, subject);
    Confirm("synthetic provider password change");
    await provider.UpdatePasswordVerifiedAsync(recovery!.AccessToken, newPassword, subject, email);
    Console.WriteLine("Recovery password update response matched the synthetic subject and email.");
    Console.WriteLine("No local database, MVC cookies, cleanup, or production services were used.");
}
catch (Exception)
{
    Console.Error.WriteLine("Test stopped: outcome may be uncertain. Do not automatically retry; contact the authorized operator.");
    Environment.ExitCode = 1;
}

static class OriginPolicy
{
    public static bool Valid(string value, string allowlist)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, allowlist, StringComparison.Ordinal) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            uri.AbsolutePath == "/" && value == uri.GetLeftPart(UriPartial.Authority) &&
            uri.Host.EndsWith(".supabase.co", StringComparison.Ordinal) && uri.Host.Length > ".supabase.co".Length;
    }
    public static bool AllowedRequest(Uri uri, HttpMethod method, string origin)
    {
        if (uri.GetLeftPart(UriPartial.Authority) != origin || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0) return false;
        return method == HttpMethod.Post && (uri.AbsolutePath, uri.Query) is
            ("/auth/v1/signup", "") or ("/auth/v1/verify", "") or ("/auth/v1/recover", "") or ("/auth/v1/token", "?grant_type=password")
            || method == HttpMethod.Put && uri.AbsolutePath == "/auth/v1/user" && uri.Query == "";
    }
    public static bool AllowedForMode(string mode, Uri uri, HttpMethod method) => mode switch
    {
        "--recovery-email" => method == HttpMethod.Post && uri.AbsolutePath == "/auth/v1/recover" && uri.Query == "",
        "--recovery-complete" => method == HttpMethod.Post && uri.AbsolutePath == "/auth/v1/verify" && uri.Query == "" ||
            method == HttpMethod.Put && uri.AbsolutePath == "/auth/v1/user" && uri.Query == "",
        "--login-only" => method == HttpMethod.Post && uri.AbsolutePath == "/auth/v1/token" && uri.Query == "?grant_type=password",
        "--run" => true,
        _ => false
    };
}
sealed class IsolatedClientFactory(string origin, string mode) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        if (name != "supabase-auth") throw new InvalidOperationException("Admin provider access is forbidden.");
        return new HttpClient(new OriginGuard(origin, mode) { InnerHandler = new SocketsHttpHandler { AllowAutoRedirect=false, UseProxy=false } });
    }
}
sealed class OriginGuard(string origin, string mode = "--run") : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri == null || !OriginPolicy.AllowedRequest(request.RequestUri, request.Method, origin) || !OriginPolicy.AllowedForMode(mode, request.RequestUri, request.Method))
            throw new InvalidOperationException("Request outside the isolated test allowlist was blocked.");
        return base.SendAsync(request, cancellationToken);
    }
}
sealed class FakeTerminal : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
}
sealed class FakeProviderFactory(string origin, string mode = "--run") : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => name == "supabase-auth"
        ? new HttpClient(new OriginGuard(origin, mode) { InnerHandler = new FakeProviderTerminal() })
        : throw new InvalidOperationException("Unexpected client.");
}
sealed class FakeProviderTerminal : HttpMessageHandler
{
    public const string Subject = "11111111-1111-1111-1111-111111111111";
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        const string user = "{\"id\":\"11111111-1111-1111-1111-111111111111\",\"email\":\"synthetic@example.test\",\"email_confirmed_at\":\"2026-01-01T00:00:00Z\"}";
        var body = request.RequestUri!.AbsolutePath switch
        {
            "/auth/v1/signup" or "/auth/v1/user" => user,
            "/auth/v1/verify" or "/auth/v1/token" => "{\"access_token\":\"synthetic-token\",\"user\":" + user + "}",
            "/auth/v1/recover" => "{}",
            _ => throw new InvalidOperationException("Unexpected fake request.")
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
