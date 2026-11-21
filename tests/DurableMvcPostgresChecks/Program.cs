using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GlamByMariya.Web.Controllers;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

if (Environment.GetEnvironmentVariable("GLAM_MVC_TEST_WRITES") != "YES") throw new InvalidOperationException("Synthetic test-write authorization required.");
var cs = new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 5432, Database = "glam_auth_mvc_workflow_test", Username = "postgres", Pooling = false, Timeout = 5, SslMode = SslMode.Disable };
var sourceBuilder = new NpgsqlDataSourceBuilder(cs.ConnectionString);
async Task Guard(NpgsqlConnection connection)
{
    await using var cmd = new NpgsqlCommand("SELECT current_database()='glam_auth_mvc_workflow_test' AND current_user='postgres' AND inet_server_addr()='127.0.0.1'::inet AND inet_client_addr()='127.0.0.1'::inet AND inet_server_port()=5432 AND (SELECT count(*)=1 FROM public.auth_mvc_test_sentinel WHERE name='glam_auth_mvc_workflow_test')", connection);
    if ((bool?)await cmd.ExecuteScalarAsync() != true) throw new InvalidOperationException("Unsafe test database.");
}
sourceBuilder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), c => Guard(c));
await using var source = sourceBuilder.Build();
var mvcProfileTests = args.Contains("--mvc-profile");
using var mvcFault = new MvcProfileFaultLogger();
using var mvcLogging = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug).AddProvider(mvcFault));
var mvcProfileName = "GlamMvcProfile-" + Guid.NewGuid().ToString("N");
var mvcBuilder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(cs.ConnectionString) { ApplicationName = mvcProfileName }.ConnectionString);
mvcBuilder.UsePhysicalConnectionInitializer(c => { Guard(c).GetAwaiter().GetResult(); mvcFault.FailConnectionIfArmed(); }, async c => { await Guard(c); mvcFault.FailConnectionIfArmed(); });
mvcBuilder.UseLoggerFactory(mvcLogging);
await using var mvcSource = mvcBuilder.Build();
await using var mvcServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddSingleton(mvcSource).BuildServiceProvider();
var accounts = new Dictionary<string, (Guid Id, Guid Subject, string Email)>();
var worker = args.Contains("--worker");
var signupHttps = args.Contains("--signup-https");
foreach (var role in worker || args.Contains("--registration-writer-only") || args.Contains("--attempt-only") || args.Contains("--signup-mvc") || signupHttps ? Array.Empty<string>() : args.Contains("--fence-only") ? new[] { "Customer" } : new[] { "Customer", "Admin", "SuperAdmin" })
{
    var id = Guid.NewGuid(); var subject = Guid.NewGuid(); var email = $"mvc-{id:N}@example.test";
    var user = new AppUser { Id = id, Email = email, Role = role };
    var hash = new PasswordHasher<AppUser>().HashPassword(user, "Synthetic123!");
    await using var c = await source.OpenConnectionAsync(); await using var tx = await c.BeginTransactionAsync();
    var insertSql = role == "Customer"
        ? "INSERT INTO public.customers(app_id,email,first_name,last_name,full_name,age_at_registration,gender,address,password_hash,is_active) VALUES(@id,@email,'Synthetic',@role,'Synthetic Test',25,'Others','Synthetic address',@hash,true)"
        : "INSERT INTO public.admin_users(app_id,email,username,first_name,last_name,age_at_registration,gender,address,password_hash,role,is_active) VALUES(@id,@email,@email,'Synthetic',@role,25,'Others','Synthetic address',@hash,@role,true)";
    await using (var cmd = new NpgsqlCommand(insertSql, c, tx))
    { cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("email", email); cmd.Parameters.AddWithValue("role", role); cmd.Parameters.AddWithValue("hash", hash); await cmd.ExecuteNonQueryAsync(); }
    await using (var cmd = new NpgsqlCommand("UPDATE public.account_identities SET auth_state='Supabase',supabase_user_id=@subject,credential_sync_state='Ready' WHERE id=@id AND auth_state='Unknown'", c, tx))
    { cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("subject", subject); if (await cmd.ExecuteNonQueryAsync() != 1) throw new Exception("Synthetic identity initialization failed."); }
    await using (var cmd = new NpgsqlCommand("INSERT INTO public.account_security_generations(account_id,generation) VALUES(@id,1)", c, tx))
    { cmd.Parameters.AddWithValue("id", id); await cmd.ExecuteNonQueryAsync(); }
    await tx.CommitAsync(); accounts[role] = (id, subject, email);
    Console.WriteLine($"CREATED retained synthetic {role} identity/account: {id}");
}

var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
builder.Logging.ClearProviders();
using var tlsKey = signupHttps ? RSA.Create(2048) : null;
X509Certificate2? tlsCertificate = null;
if (signupHttps)
{
    var request = new CertificateRequest("CN=localhost", tlsKey!, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); names.AddDnsName("localhost");
    request.CertificateExtensions.Add(names.Build());
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
    using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    // Windows Schannel cannot serve an ephemeral-key certificate; import only
    // this test certificate with a user-scoped, non-persisted key.
    tlsCertificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(tlsCertificate)));
}
else builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddDbContext<DataProtectionKeyContext>(o => o.UseNpgsql(source));
builder.Services.AddDataProtection().SetApplicationName("GlamAuthMvcIsolatedTest").PersistKeysToDbContext<DataProtectionKeyContext>();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddStudioCookieAuthentication(!signupHttps, true);
builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o => o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; });
builder.Services.AddAuthorization();
if (args.Contains("--signup-mvc"))
    builder.Services.AddRateLimiter(o => { foreach (var name in RateLimitPolicies.All.Keys) o.AddPolicy(name, _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("synthetic")); });
else builder.Services.AddRateLimiter(RateLimitPolicies.Configure);
builder.Services.AddMemoryCache(); builder.Services.AddHttpContextAccessor();
builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute())).AddApplicationPart(typeof(AccountController).Assembly);
builder.Services.AddSingleton<IControllerActivator, AccountOnlyActivator>(); builder.Services.AddSingleton(args.Contains("--signup-mvc") ? mvcSource : source);
builder.Services.AddDbContext<PublicMakeupDbContext>(o => o.UseNpgsql(source));
builder.Services.AddScoped<PublicStudioStore>(); builder.Services.AddScoped<IStudioStore>(sp => sp.GetRequiredService<PublicStudioStore>());
builder.Services.AddSingleton<IAppFileStore, NoFiles>();
builder.Services.AddSingleton<IPostgresAccountSecurityQuery>(new PostgresAccountSecurityQuery(source));
builder.Services.AddSingleton<IAccountSecurityReadSource>(sp => new PostgresAccountSecurityReadSource(sp.GetRequiredService<IPostgresAccountSecurityQuery>(), "public"));
builder.Services.AddSingleton<IAccountSecuritySnapshotProvider>(sp => new RepositoryAccountSecuritySnapshotProvider(sp.GetRequiredService<IAccountSecurityReadSource>(), SessionStoreKind.PublicPostgres, SessionAuthenticationKind.Supabase));
builder.Services.AddSingleton<DurableSessionAuthorization>(); builder.Services.AddSingleton<IDurableCredentialStore>(new PostgresDurableCredentialStore(source)); builder.Services.AddSingleton<DurableCredentialUpdate>();
var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Authentication:DurableSessionEnforcement"]="true", ["Supabase:Url"]="https://auth.example.test", ["Supabase:AnonKey"]="synthetic" }).Build();
var fake = new FakeProvider(accounts); var supabase = new SupabaseAuthService(fake, cfg, NullLogger<SupabaseAuthService>.Instance);
builder.Services.AddSingleton(new LoginAttemptLimiter());
builder.Services.AddTransient(sp => new AccountController(sp.GetRequiredService<IStudioStore>(), supabase,
    new EmailVerificationService(sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), cfg, NullLogger<EmailVerificationService>.Instance, null!, null!),
    sp.GetRequiredService<AccountSettingsService>(), null!, null!, sp.GetRequiredService<LoginAttemptLimiter>(), cfg) { TestOnlyDurableSignup = args.Contains("--signup-mvc") || signupHttps, TestOnlySignupRetry = false });
if (mvcProfileTests)
    builder.Services.AddScoped(sp => new AccountSettingsService(sp.GetRequiredService<IStudioStore>(), mvcServices, sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));
else builder.Services.AddScoped<AccountSettingsService>();
var app = builder.Build();
if (args.Contains("--signup-mvc")) app.Use(async (context, next) => { if (context.Request.Headers.ContainsKey("X-Synthetic-Https")) context.Request.Scheme = "https"; await next(); });
app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
app.MapControllerRoute("mvc", "{controller=Home}/{action=Index}/{id?}");
app.MapGet("/csrf", (HttpContext c, IAntiforgery a) => a.GetAndStoreTokens(c).RequestToken!);
app.MapGet("/protected", (ClaimsPrincipal p) => Results.Text(p.FindFirstValue(ClaimTypes.Role)!)).RequireAuthorization();
await app.StartAsync();
if (worker)
{
    Console.WriteLine("READY=" + app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    await Console.In.ReadLineAsync();
    await app.StopAsync(); await app.DisposeAsync(); return;
}
try
{
    var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    if (args.Contains("--fence-only"))
    {
        var account = accounts["Customer"];
        var store = new PostgresDurableCredentialStore(source);
        var jar = new CookieContainer();
        using var client = new HttpClient(new HttpClientHandler { CookieContainer=jar, AllowAutoRedirect=false }) { BaseAddress=new Uri(url) };
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login") { Content=new FormUrlEncodedContent(new Dictionary<string,string> { ["email"]=account.Email,["password"]="Synthetic123!" }) })
        {
            request.Headers.Add("X-CSRF-TOKEN", await client.GetStringAsync("/csrf"));
            using var response=await client.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.Redirect || await client.GetStringAsync("/protected") != "Customer") throw new Exception("Synthetic login failed.");
        }
        var originalCookie = jar.GetCookieHeader(new Uri(url));
        if (await store.Begin(account.Id, account.Subject, 1, CancellationToken.None) != 2) throw new Exception("Begin failed.");
        if (await store.FencePending(account.Id, Guid.NewGuid(), 2, CancellationToken.None) != null ||
            await store.FencePending(account.Id, account.Subject, 1, CancellationToken.None) != null) throw new Exception("Mismatch or stale epoch fenced.");
        using (var cancel = new CancellationTokenSource())
        using (var observer = new UpdateCompletionLogger("UPDATE public.account_identities SET credential_epoch=credential_epoch+1", cancel))
        using (var logger = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information).AddProvider(observer)))
        {
            var faultBuilder = new NpgsqlDataSourceBuilder(cs.ConnectionString);
            faultBuilder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), c => Guard(c)); faultBuilder.UseLoggerFactory(logger);
            await using var faultSource = faultBuilder.Build();
            try { await new PostgresDurableCredentialStore(faultSource).FencePending(account.Id, account.Subject, 2, cancel.Token); throw new Exception("Cancelled fence returned success."); }
            catch (OperationCanceledException) { }
            if (observer.CompletedUpdates != 1) throw new Exception("Fence update not observed.");
        }
        await using (var c=await source.OpenConnectionAsync())
        await using (var q=new NpgsqlCommand("SELECT credential_epoch=2 AND credential_sync_state='Pending' FROM public.account_identities WHERE id=@id", c))
        { q.Parameters.AddWithValue("id",account.Id); if ((bool?)await q.ExecuteScalarAsync()!=true) throw new Exception("Cancelled fence did not roll back."); }
        async Task<long?> Compete()
        {
            try { return await store.FencePending(account.Id, account.Subject, 2, CancellationToken.None); }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.SerializationFailure) { return null; }
        }
        var results = await Task.WhenAll(Compete(), Compete());
        if (results.Count(x => x == 3) != 1 || results.Count(x => x == null) != 1) throw new Exception("Competing fences both completed.");
        if (await store.Complete(account.Id, account.Subject, 2, "SyntheticNew123!", CancellationToken.None) ||
            await store.Reject(account.Id, account.Subject, 2, CancellationToken.None) ||
            await store.Begin(account.Id, account.Subject, 3, CancellationToken.None) != null ||
            (await client.GetAsync("/protected")).StatusCode != HttpStatusCode.Unauthorized) throw new Exception("Fenced local authorization was restored.");
        await using (var c=await source.OpenConnectionAsync())
        await using (var q=new NpgsqlCommand("SELECT credential_epoch=3 AND credential_sync_state='ReconciliationRequired' FROM public.account_identities WHERE id=@id", c))
        { q.Parameters.AddWithValue("id",account.Id); if ((bool?)await q.ExecuteScalarAsync()!=true) throw new Exception("Fence not retained."); }
        var info = new ProcessStartInfo("dotnet") { UseShellExecute=false, RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true };
        info.ArgumentList.Add(typeof(AccountOnlyActivator).Assembly.Location); info.ArgumentList.Add("--worker");
        using var process = Process.Start(info) ?? throw new Exception("Restart worker failed.");
        try
        {
            var line=await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (line==null || !line.StartsWith("READY=http://127.0.0.1:",StringComparison.Ordinal)) throw new Exception("Restart worker address invalid.");
            using var remote = new HttpClient { BaseAddress=new Uri(line[6..]) }; remote.DefaultRequestHeaders.Add("Cookie",originalCookie);
            if ((await remote.GetAsync("/protected")).StatusCode != HttpStatusCode.Unauthorized) throw new Exception("Stale cookie accepted after process restart.");
            await process.StandardInput.WriteLineAsync("stop"); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { if (!process.HasExited) process.Kill(true); }
        var uncertainId = Guid.NewGuid(); var uncertainSubject = Guid.NewGuid(); var uncertainEmail = $"mvc-{uncertainId:N}@example.test";
        var uncertainUser = new AppUser { Id=uncertainId, Email=uncertainEmail, Role="Customer" };
        var uncertainHash = new PasswordHasher<AppUser>().HashPassword(uncertainUser, "Synthetic123!");
        await using (var c=await source.OpenConnectionAsync())
        await using (var tx=await c.BeginTransactionAsync())
        {
            await using (var insert = new NpgsqlCommand("INSERT INTO public.customers(app_id,email,first_name,last_name,full_name,age_at_registration,gender,address,password_hash,is_active) VALUES(@id,@email,'Synthetic','Customer','Synthetic Customer',25,'Others','Synthetic address',@hash,true)",c,tx))
            { insert.Parameters.AddWithValue("id",uncertainId); insert.Parameters.AddWithValue("email",uncertainEmail); insert.Parameters.AddWithValue("hash",uncertainHash); await insert.ExecuteNonQueryAsync(); }
            await using (var link = new NpgsqlCommand("UPDATE public.account_identities SET auth_state='Supabase',supabase_user_id=@subject,credential_sync_state='Ready' WHERE id=@id AND auth_state='Unknown'",c,tx))
            { link.Parameters.AddWithValue("id",uncertainId); link.Parameters.AddWithValue("subject",uncertainSubject); if (await link.ExecuteNonQueryAsync()!=1) throw new Exception("Synthetic setup failed."); }
            await using (var generation = new NpgsqlCommand("INSERT INTO public.account_security_generations(account_id,generation) VALUES(@id,1)",c,tx))
            { generation.Parameters.AddWithValue("id",uncertainId); await generation.ExecuteNonQueryAsync(); }
            await tx.CommitAsync();
        }
        Console.WriteLine($"CREATED retained synthetic Customer identity/account: {uncertainId}");
        if (await store.Begin(uncertainId,uncertainSubject,1,CancellationToken.None)!=2) throw new Exception("Uncertain fence setup failed.");
        using (var observer = new CommitFailureLogger("UPDATE public.account_identities SET credential_epoch=credential_epoch+1"))
        using (var logger = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug).AddProvider(observer)))
        {
            var faultBuilder = new NpgsqlDataSourceBuilder(cs.ConnectionString);
            faultBuilder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), c => Guard(c)); faultBuilder.UseLoggerFactory(logger);
            await using var faultSource=faultBuilder.Build();
            try { await new PostgresDurableCredentialStore(faultSource).FencePending(uncertainId,uncertainSubject,2,CancellationToken.None); throw new Exception("Uncertain fence reported success."); }
            catch (AggregateException e) when (e.InnerExceptions.Any(x => x is SimulatedCommitAcknowledgmentException)) { }
            catch (SimulatedCommitAcknowledgmentException) { }
            if (observer.CompletedUpdates!=1 || observer.CommitFailures!=1) throw new Exception("Fence commit simulation did not execute once.");
        }
        await using (var c=await source.OpenConnectionAsync())
        await using (var q=new NpgsqlCommand("SELECT credential_epoch=3 AND credential_sync_state='ReconciliationRequired' FROM public.account_identities WHERE id=@id",c))
        { q.Parameters.AddWithValue("id",uncertainId); if ((bool?)await q.ExecuteScalarAsync()!=true) throw new Exception("Uncertain fence state unexpected."); }
        if (await store.Complete(uncertainId,uncertainSubject,2,"SyntheticNew123!",CancellationToken.None)) throw new Exception("Uncertain fence allowed old completion.");
        Console.WriteLine("PASS simulated post-commit fence acknowledgment failure: no success, no retry, separately observed denied state; not a network fault");
        Console.WriteLine("PASS Customer one-way pending fence: mismatch/stale denial, rollback, competing attempts, delayed local completion denial, stale cookie and restarted-process rejection");
        return;
    }
    if (signupHttps)
    {
        var evidenceUser = new AppUser { Role="Customer", PasswordHash="synthetic-hash" };
        var currentEvidence = new AccountSessionEvidence(evidenceUser.Id, "Customer", "1", AccountSession.Version(evidenceUser.PasswordHash));
        if (!AccountController.RegistrationEvidenceUnchanged(currentEvidence, evidenceUser) ||
            AccountController.RegistrationEvidenceUnchanged(currentEvidence with { CredentialEpoch="2" }, evidenceUser) ||
            AccountController.RegistrationEvidenceUnchanged(currentEvidence with { AccountVersion=AccountSession.Version("other-hash") }, evidenceUser) ||
            AccountController.RegistrationEvidenceUnchanged(currentEvidence with { AccountId=Guid.NewGuid() }, evidenceUser) ||
            AccountController.RegistrationEvidenceUnchanged(currentEvidence with { Role="Admin" }, evidenceUser))
            throw new Exception("Registration evidence freshness guard failed.");
        Console.WriteLine("PASS registration ticket evidence rejects changed epoch, credential, account and role");
        if (!url.StartsWith("https://127.0.0.1:", StringComparison.Ordinal)) throw new Exception("HTTPS listener is not isolated loopback.");
        var expectedCertificate = tlsCertificate!.GetRawCertData();
        var tlsAccepted = false;
        HttpClient TlsClient() => new(new HttpClientHandler {
            CookieContainer = new CookieContainer(), AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => {
                var valid = certificate != null && certificate.GetRawCertData().AsSpan().SequenceEqual(expectedCertificate)
                    && (errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) == System.Net.Security.SslPolicyErrors.None
                    && DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() && DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime();
                if (valid) tlsAccepted = true;
                return valid;
            }
        }) { BaseAddress = new Uri(url) };
        async Task<HttpResponseMessage> TlsPost(HttpClient client, string path, Dictionary<string,string> fields)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
            request.Headers.Add("X-CSRF-TOKEN", await client.GetStringAsync("/csrf"));
            return await client.SendAsync(request);
        }
        static bool CookieFlags(HttpResponseMessage response, string name, string sameSite) =>
            response.Headers.TryGetValues("Set-Cookie", out var values) && values.Any(value =>
                value.StartsWith(name + "=", StringComparison.Ordinal) && value.Contains("; secure", StringComparison.OrdinalIgnoreCase)
                && value.Contains("; httponly", StringComparison.OrdinalIgnoreCase) && value.Contains("; samesite=" + sameSite, StringComparison.OrdinalIgnoreCase));
        Dictionary<string,string> Registration(string address) => new() { ["acceptTerms"]="true", ["firstName"]="Synthetic", ["lastName"]="Customer", ["email"]=address, ["age"]="25", ["gender"]="Others", ["address"]="Synthetic address", ["password"]="Synthetic123!" };
        var email = $"mvc-signup-{Guid.NewGuid():N}@example.test";
        using var client = TlsClient();
        using (var start = await TlsPost(client, "/Account/Register", Registration(email)))
            if (start.StatusCode != HttpStatusCode.Redirect || !CookieFlags(start, "GlamSignup", "strict")) throw new Exception("HTTPS signup cookie flags failed.");
        using (var verify = await TlsPost(client, "/Account/Verify", new() { ["email"]=email, ["code"]="123456" }))
            if (verify.StatusCode != HttpStatusCode.Redirect || !CookieFlags(verify, ".AspNetCore.Cookies", "lax")) throw new Exception("HTTPS authentication cookie flags failed.");
        if (!tlsAccepted || await client.GetStringAsync("/protected") != "Customer") throw new Exception("TLS or authenticated session failed.");
        await using (var c = await source.OpenConnectionAsync())
        await using (var q = new NpgsqlCommand("SELECT c.app_id FROM public.customers c JOIN public.auth_mvc_signup_attempts a ON a.attempt_id=c.app_id WHERE c.email=@email AND a.state='Consumed'", c))
        { q.Parameters.AddWithValue("email", email); Console.WriteLine("CREATED retained HTTPS Customer and attempt: " + ((Guid?)await q.ExecuteScalarAsync() ?? throw new Exception("HTTPS signup state missing."))); }
        using var failedClient = TlsClient(); var failedEmail = $"mvc-signup-{Guid.NewGuid():N}@example.test";
        using (var start = await TlsPost(failedClient, "/Account/Register", Registration(failedEmail)))
            if (start.StatusCode != HttpStatusCode.Redirect || !CookieFlags(start, "GlamSignup", "strict")) throw new Exception("HTTPS failure setup failed.");
        using (var failed = await TlsPost(failedClient, "/Account/Verify", new() { ["email"]=failedEmail, ["code"]="000000" }))
            if (failed.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable) ||
                (failed.Headers.TryGetValues("Set-Cookie", out var values) && values.Any(value => value.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal))))
                throw new Exception("Failed HTTPS signup issued authentication.");
        if ((await failedClient.GetAsync("/protected")).StatusCode != HttpStatusCode.Unauthorized) throw new Exception("Failed HTTPS signup authenticated.");
        await using (var c = await source.OpenConnectionAsync())
        await using (var q = new NpgsqlCommand("SELECT a.attempt_id FROM public.auth_mvc_signup_attempts a WHERE a.normalized_email=@email AND a.state='Unconfirmed' AND NOT EXISTS (SELECT 1 FROM public.customers WHERE app_id=a.attempt_id)", c))
        { q.Parameters.AddWithValue("email", failedEmail); Console.WriteLine("CREATED retained failed HTTPS attempt: " + ((Guid?)await q.ExecuteScalarAsync() ?? throw new Exception("Failure state missing."))); }
        Console.WriteLine("PASS real loopback TLS with pinned ephemeral test certificate; signup Secure/HttpOnly/Strict and auth Secure/HttpOnly/Lax; rejected OTP issues no auth cookie");
        return;
    }
    if (args.Contains("--signup-mvc"))
    {
        var email = $"mvc-signup-{Guid.NewGuid():N}@example.test";
        var jar = new CookieContainer();
        using var client = new HttpClient(new HttpClientHandler { CookieContainer=jar, AllowAutoRedirect=false }) { BaseAddress=new Uri(url) };
        async Task<HttpResponseMessage> SignupPost(string path, Dictionary<string,string> fields)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content=new FormUrlEncodedContent(fields) };
            request.Headers.Add("X-CSRF-TOKEN", await client.GetStringAsync("/csrf"));
            return await client.SendAsync(request);
        }
        using var start = await SignupPost("/Account/Register", new() { ["acceptTerms"]="true", ["firstName"]="Synthetic", ["lastName"]="Customer", ["email"]=email, ["age"]="25", ["gender"]="Others", ["address"]="Synthetic address", ["password"]="Synthetic123!" });
        if (start.StatusCode != HttpStatusCode.Redirect || !start.Headers.TryGetValues("Set-Cookie", out var headers) || !headers.Any(x => x.StartsWith("GlamSignup=") && x.Contains("httponly", StringComparison.OrdinalIgnoreCase) && x.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase))) throw new Exception("Signup start or cookie failed: " + start.StatusCode);
        var signupCookie = jar.GetCookies(new Uri(url + "/Account"))["GlamSignup"]?.Value ?? throw new Exception("Signup cookie missing.");
        using (var outsider = new HttpClient(new HttpClientHandler { CookieContainer=new CookieContainer(), AllowAutoRedirect=false }) { BaseAddress=new Uri(url) })
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Verify") { Content=new FormUrlEncodedContent(new Dictionary<string,string> { ["email"]=email,["code"]="123456" }) })
        {
            request.Headers.Add("X-CSRF-TOKEN", await outsider.GetStringAsync("/csrf"));
            using var response = await outsider.SendAsync(request);
            if (response.StatusCode is not (HttpStatusCode.ServiceUnavailable or HttpStatusCode.Unauthorized) || (response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(x => x.StartsWith(".AspNetCore.Cookies=")))) throw new Exception("Unbound browser accepted.");
        }
        using var verify = await SignupPost("/Account/Verify", new() { ["email"]="untrusted@example.test", ["code"]="123456" });
        if (verify.StatusCode != HttpStatusCode.Redirect || await client.GetStringAsync("/protected") != "Customer") throw new Exception("Signup verification failed: " + verify.StatusCode);
        await using (var c = await source.OpenConnectionAsync())
        await using (var q = new NpgsqlCommand("SELECT c.app_id FROM public.customers c JOIN public.account_identities i ON i.id=c.app_id JOIN public.auth_mvc_signup_attempts a ON a.attempt_id=c.app_id WHERE c.email=@email AND i.auth_state='Supabase' AND i.credential_sync_state='Ready' AND i.credential_epoch=1 AND a.state='Consumed'", c))
        { q.Parameters.AddWithValue("email", email); Console.WriteLine("CREATED retained synthetic Customer and consumed attempt: " + ((Guid?)await q.ExecuteScalarAsync() ?? throw new Exception("Signup database state missing."))); }
        jar.Add(new Uri(url + "/Account"), new Cookie("GlamSignup", signupCookie, "/Account"));
        using (var replay = await SignupPost("/Account/Verify", new() { ["email"]=email, ["code"]="123456" }))
            if (replay.StatusCode != HttpStatusCode.Unauthorized || (replay.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(x => x.StartsWith(".AspNetCore.Cookies=")))) throw new Exception("Signup replay accepted.");
        var concurrentEmail = $"mvc-signup-{Guid.NewGuid():N}@example.test";
        using (var second = await SignupPost("/Account/Register", new() { ["acceptTerms"]="true", ["firstName"]="Synthetic", ["lastName"]="Customer", ["email"]=concurrentEmail, ["age"]="25", ["gender"]="Others", ["address"]="Synthetic address", ["password"]="Synthetic123!" }))
            if (second.StatusCode != HttpStatusCode.Redirect) throw new Exception("Concurrent signup setup failed.");
        var responses = await Task.WhenAll(SignupPost("/Account/Verify", new() { ["email"]=concurrentEmail, ["code"]="123456" }), SignupPost("/Account/Verify", new() { ["email"]=concurrentEmail, ["code"]="123456" }));
        if (responses.Count(x => x.StatusCode == HttpStatusCode.Redirect) != 1 || responses.Count(x => x.StatusCode == HttpStatusCode.Unauthorized) != 1) throw new Exception("Concurrent signup was not single-use.");
        foreach (var response in responses) response.Dispose();
        await using (var c = await source.OpenConnectionAsync())
        await using (var q = new NpgsqlCommand("SELECT c.app_id FROM public.customers c JOIN public.auth_mvc_signup_attempts a ON a.attempt_id=c.app_id WHERE c.email=@email AND a.state='Consumed'", c))
        { q.Parameters.AddWithValue("email", concurrentEmail); Console.WriteLine("CREATED retained synthetic concurrent Customer and attempt: " + ((Guid?)await q.ExecuteScalarAsync() ?? throw new Exception("Concurrent signup missing."))); }
        Console.WriteLine("PASS actual MVC signup, protected HttpOnly browser cookie, fake OTP and authenticated Customer session; browser email ignored");
        Console.WriteLine("PASS actual MVC unrelated browser denial, replay denial and concurrent single-use signup");
        using (var loginClient = new HttpClient(new HttpClientHandler { CookieContainer=new CookieContainer(), AllowAutoRedirect=false }) { BaseAddress=new Uri(url) })
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login") { Content=new FormUrlEncodedContent(new Dictionary<string,string> { ["email"]=email,["password"]="Synthetic123!" }) })
        {
            request.Headers.Add("X-CSRF-TOKEN", await loginClient.GetStringAsync("/csrf"));
            using var response = await loginClient.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.Redirect || await loginClient.GetStringAsync("/protected") != "Customer") throw new Exception("Separate post-registration login failed.");
        }
        Console.WriteLine("PASS separate fake-provider login after registration");
        async Task<(HttpClient Client, string Email, Guid Id)> StartAttempt(bool https = false)
        {
            var fresh = new HttpClient(new HttpClientHandler { CookieContainer=new CookieContainer(), AllowAutoRedirect=false }) { BaseAddress=new Uri(url) };
            var address = $"mvc-signup-{Guid.NewGuid():N}@example.test";
            using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Register") { Content=new FormUrlEncodedContent(new Dictionary<string,string> { ["acceptTerms"]="true", ["firstName"]="Synthetic",["lastName"]="Customer",["email"]=address,["age"]="25",["gender"]="Others",["address"]="Synthetic address",["password"]="Synthetic123!" }) };
            request.Headers.Add("X-CSRF-TOKEN", await fresh.GetStringAsync("/csrf"));
            if (https) request.Headers.Add("X-Synthetic-Https", "1");
            using var response = await fresh.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.Redirect) throw new Exception("New attempt failed: " + response.StatusCode);
            if (https && (!response.Headers.TryGetValues("Set-Cookie", out var values) || !values.Any(x => x.StartsWith("GlamSignup=") && x.Contains("; secure", StringComparison.OrdinalIgnoreCase) && x.Contains("httponly", StringComparison.OrdinalIgnoreCase) && x.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase)))) throw new Exception("HTTPS scheme cookie flags invalid.");
            await using var c = await source.OpenConnectionAsync();
            await using var q = new NpgsqlCommand("SELECT attempt_id FROM public.auth_mvc_signup_attempts WHERE normalized_email=@email", c); q.Parameters.AddWithValue("email", address);
            var attemptId = (Guid)(await q.ExecuteScalarAsync() ?? throw new Exception("Attempt missing."));
            Console.WriteLine($"CREATED retained synthetic signup attempt: {attemptId}");
            return (fresh,address,attemptId);
        }
        async Task<HttpResponseMessage> VerifyAttempt(HttpClient target, string address)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Verify") { Content=new FormUrlEncodedContent(new Dictionary<string,string> { ["email"]=address,["code"]="123456" }) };
            request.Headers.Add("X-CSRF-TOKEN", await target.GetStringAsync("/csrf"));
            return await target.SendAsync(request);
        }
        bool HasAuth(HttpResponseMessage response) => response.Headers.TryGetValues("Set-Cookie", out var values) && values.Any(x => x.StartsWith(".AspNetCore.Cookies=", StringComparison.OrdinalIgnoreCase));
        async Task<(string State, bool Account)> State(Guid attempt)
        {
            await using var c = await source.OpenConnectionAsync();
            await using var q = new NpgsqlCommand("SELECT state,EXISTS(SELECT 1 FROM public.customers WHERE app_id=@id) FROM public.auth_mvc_signup_attempts WHERE attempt_id=@id", c);
            q.Parameters.AddWithValue("id", attempt); await using var reader = await q.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new Exception("Attempt missing."); return (reader.GetString(0),reader.GetBoolean(1));
        }
        var secureAttempt = await StartAttempt(true); secureAttempt.Client.Dispose();
        Console.WriteLine("PASS Secure/HttpOnly/SameSite=Strict with synthetic HTTPS request scheme (no TLS transport)");
        var expired = await StartAttempt();
        using (expired.Client)
        {
            await using (var c = await source.OpenConnectionAsync())
            await using (var cmd = new NpgsqlCommand("UPDATE public.auth_mvc_signup_attempts SET created_at=now()-interval '2 hours',expires_at=now()-interval '1 hour' WHERE attempt_id=@id", c))
            { cmd.Parameters.AddWithValue("id", expired.Id); if (await cmd.ExecuteNonQueryAsync()!=1) throw new Exception("Expiry setup failed."); }
            using var response = await VerifyAttempt(expired.Client, expired.Email);
            if (response.StatusCode != HttpStatusCode.Unauthorized || HasAuth(response) || (await State(expired.Id)).Account) throw new Exception("Expired attempt accepted.");
        }
        Console.WriteLine("PASS MVC expired attempt denied without auth cookie");
        foreach (var mode in new[] { "timeout", "reject", "mismatch" })
        {
            var item = await StartAttempt(); using (item.Client)
            {
                var beforeCalls = fake.OtpCalls; fake.Mode=mode;
                using var response = await VerifyAttempt(item.Client, item.Email); fake.Mode=null;
                var state = await State(item.Id);
                if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable) || HasAuth(response) || state.State != "Unconfirmed" || state.Account || fake.OtpCalls != beforeCalls+1) throw new Exception("Provider failure not contained: " + mode);
                using var retry = await VerifyAttempt(item.Client,item.Email);
                if (retry.StatusCode != HttpStatusCode.Unauthorized || HasAuth(retry) || fake.OtpCalls != beforeCalls+1) throw new Exception("Unconfirmed OTP was retried.");
            }
            Console.WriteLine($"PASS MVC provider {mode}, no account or auth cookie, no automatic OTP retry");
        }
        foreach (var mode in new[] { "connect", "signup-rollback", "signup-commit" })
        {
            var item=await StartAttempt(); using (item.Client)
            {
                var beforeCalls=fake.OtpCalls; mvcFault.Arm(mode);
                using var response=await VerifyAttempt(item.Client,item.Email);
                var state=await State(item.Id);
                if (response.StatusCode != HttpStatusCode.ServiceUnavailable || HasAuth(response) || !mvcFault.Fired || state.Account != (mode == "signup-commit") || (mode != "connect" && mvcFault.Updates != 1)) throw new Exception("DB fault not contained: " + mode);
                if (mode == "signup-commit" && (state.State != "Consumed" || fake.OtpCalls != beforeCalls+1)) throw new Exception("Committed outcome was not observed once.");
                if (mode == "signup-rollback" && state.State != "Unconfirmed") throw new Exception("Rollback left partial attempt state.");
            }
            Console.WriteLine($"PASS MVC database {mode}, no authenticated cookie; separate connection checked persisted outcome");
        }
        fake.Mode="duplicate-subject";
        var duplicateFirst = await StartAttempt();
        using (duplicateFirst.Client)
        using (var firstResult = await VerifyAttempt(duplicateFirst.Client, duplicateFirst.Email))
            if (firstResult.StatusCode != HttpStatusCode.Redirect) throw new Exception("Duplicate-subject setup failed.");
        Console.WriteLine($"CREATED retained synthetic Customer and attempt: {duplicateFirst.Id}");
        var duplicateSecond = await StartAttempt();
        using (duplicateSecond.Client)
        using (var secondResult = await VerifyAttempt(duplicateSecond.Client, duplicateSecond.Email))
            if (secondResult.StatusCode != HttpStatusCode.Unauthorized || HasAuth(secondResult) || (await State(duplicateSecond.Id)).Account) throw new Exception("Duplicate subject accepted.");
        fake.Mode=null;
        using (var duplicateClient = new HttpClient(new HttpClientHandler { CookieContainer=new CookieContainer(),AllowAutoRedirect=false }) { BaseAddress=new Uri(url) })
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Register") { Content=new FormUrlEncodedContent(new Dictionary<string,string> { ["acceptTerms"]="true", ["firstName"]="Synthetic",["lastName"]="Customer",["email"]=duplicateFirst.Email,["age"]="25",["gender"]="Others",["address"]="Synthetic address",["password"]="Synthetic123!" }) })
        {
            request.Headers.Add("X-CSRF-TOKEN", await duplicateClient.GetStringAsync("/csrf"));
            using var response = await duplicateClient.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable || HasAuth(response)) throw new Exception("Duplicate email accepted.");
            await using var c = await source.OpenConnectionAsync();
            await using var q = new NpgsqlCommand("SELECT attempt_id FROM public.auth_mvc_signup_attempts WHERE normalized_email=@email AND state='Initiated'", c);
            q.Parameters.AddWithValue("email", duplicateFirst.Email);
            Console.WriteLine("CREATED retained duplicate-email signup attempt: " + ((Guid?)await q.ExecuteScalarAsync() ?? throw new Exception("Duplicate attempt not found.")));
        }
        Console.WriteLine("PASS MVC duplicate provider subject and email rejected without new authenticated cookie");
        async Task<(Process Process, string Address)> StartSignupWorker()
        {
            var info = new ProcessStartInfo("dotnet") { UseShellExecute=false, RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true };
            info.ArgumentList.Add(typeof(AccountOnlyActivator).Assembly.Location); info.ArgumentList.Add("--worker"); info.ArgumentList.Add("--signup-mvc");
            var process = Process.Start(info) ?? throw new Exception("Worker start failed.");
            try
            {
                var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                if (line == null || !line.StartsWith("READY=http://127.0.0.1:", StringComparison.Ordinal)) throw new Exception("Worker address invalid.");
                return (process,line[6..]);
            }
            catch { if (!process.HasExited) process.Kill(true); process.Dispose(); throw; }
        }
        async Task StopSignupWorker(Process process)
        {
            await process.StandardInput.WriteLineAsync("stop");
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { if (!process.HasExited) process.Kill(true); process.Dispose(); }
        }
        var workerOne = await StartSignupWorker();
        var restartJar = new CookieContainer(); var restartEmail = $"mvc-signup-{Guid.NewGuid():N}@example.test";
        try
        {
            using var firstClient = new HttpClient(new HttpClientHandler { CookieContainer=restartJar, AllowAutoRedirect=false }) { BaseAddress=new Uri(workerOne.Address) };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Register") { Content=new FormUrlEncodedContent(new Dictionary<string,string> { ["acceptTerms"]="true", ["firstName"]="Synthetic",["lastName"]="Customer",["email"]=restartEmail,["age"]="25",["gender"]="Others",["address"]="Synthetic address",["password"]="Synthetic123!" }) };
            request.Headers.Add("X-CSRF-TOKEN", await firstClient.GetStringAsync("/csrf"));
            using var response = await firstClient.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.Redirect) throw new Exception("Restart signup start failed.");
        }
        finally { await StopSignupWorker(workerOne.Process); }
        var workerTwo = await StartSignupWorker();
        try
        {
            using var secondClient = new HttpClient(new HttpClientHandler { CookieContainer=restartJar, AllowAutoRedirect=false }) { BaseAddress=new Uri(workerTwo.Address) };
            using var response = await VerifyAttempt(secondClient, restartEmail);
            if (response.StatusCode != HttpStatusCode.Redirect || !HasAuth(response) || await secondClient.GetStringAsync("/protected") != "Customer") throw new Exception("Restart signup completion failed.");
        }
        finally { await StopSignupWorker(workerTwo.Process); }
        await using (var c = await source.OpenConnectionAsync())
        await using (var q = new NpgsqlCommand("SELECT c.app_id FROM public.customers c JOIN public.auth_mvc_signup_attempts a ON a.attempt_id=c.app_id WHERE c.email=@email AND a.state='Consumed'", c))
        { q.Parameters.AddWithValue("email",restartEmail); Console.WriteLine("CREATED retained restart Customer and attempt: " + ((Guid?)await q.ExecuteScalarAsync() ?? throw new Exception("Restart signup missing."))); }
        Console.WriteLine("PASS MVC attempt started in one process and completed after restart in a second process");
        return;
    }
    if (args.Contains("--attempt-only"))
    {
        var dp = app.Services.GetRequiredService<IDataProtectionProvider>();
        var attempts = new PostgresDurableSignupAttempts(source, dp);
        var subject = Guid.NewGuid();
        var entry = await attempts.Create($"attempt-{Guid.NewGuid():N}@example.test", "synthetic protected profile");
        Console.WriteLine($"CREATED retained synthetic signup attempt: {entry.Id}");
        if (!await attempts.MarkAwaitingOtp(entry.Id, subject)) throw new Exception("Attempt setup failed.");
        if (await attempts.Claim(entry.Id, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))) != null) throw new Exception("Wrong browser accepted.");
        var restarted = new PostgresDurableSignupAttempts(source, dp);
        var claims = await Task.WhenAll(restarted.Claim(entry.Id, entry.Secret), attempts.Claim(entry.Id, entry.Secret));
        if (claims.Count(x => x != null) != 1 || claims.Single(x => x != null)!.Value.Subject != subject || claims.Single(x => x != null)!.Value.Profile != "synthetic protected profile") throw new Exception("Concurrent attempt claim failed.");
        if (await restarted.Claim(entry.Id, entry.Secret) != null) throw new Exception("Attempt replay accepted.");
        var expired = await attempts.Create($"expired-{Guid.NewGuid():N}@example.test", "synthetic protected profile");
        Console.WriteLine($"CREATED retained synthetic signup attempt: {expired.Id}");
        if (!await attempts.MarkAwaitingOtp(expired.Id, Guid.NewGuid())) throw new Exception("Expiry setup failed.");
        await using (var c = await source.OpenConnectionAsync())
        await using (var expire = new NpgsqlCommand("UPDATE public.auth_mvc_signup_attempts SET created_at=now()-interval '2 hours',expires_at=now()-interval '1 hour' WHERE attempt_id=@id", c))
        { expire.Parameters.AddWithValue("id", expired.Id); if (await expire.ExecuteNonQueryAsync() != 1) throw new Exception("Expiry update failed."); }
        if (await attempts.Claim(expired.Id, expired.Secret) != null) throw new Exception("Expired attempt accepted.");
        Console.WriteLine("PASS guarded PostgreSQL attempt wrong secret, expiry, concurrent single claim and replay; new service instance reads protected state. No process restart or MVC ticket tested.");
        return;
    }
    if (args.Contains("--registration-writer-only"))
    {
        var providerConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Supabase:Url"]="https://auth.example.test", ["Supabase:AnonKey"]="synthetic" }).Build();
        PostgresDurableCustomerRegistration Writer(NpgsqlDataSource db, RegistrationProvider fakeProvider) => new(db, new SupabaseAuthService(fakeProvider, providerConfig, NullLogger<SupabaseAuthService>.Instance));
        async Task<AppUser?> Create(PostgresDurableCustomerRegistration writer, Guid id, string email, string otp = "123456") =>
            await writer.RegisterVerifiedOtpAsync(id, email, otp, "Synthetic", "Customer", 25, "Others", "Synthetic address", "Synthetic123!");
        async Task<bool> Exists(Guid id)
        {
            await using var c = await source.OpenConnectionAsync();
            await using var q = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM public.customers WHERE app_id=@id) OR EXISTS(SELECT 1 FROM public.account_identities WHERE id=@id)", c);
            q.Parameters.AddWithValue("id", id); return (bool)(await q.ExecuteScalarAsync())!;
        }
        var id = Guid.NewGuid(); var email = $"signup-{id:N}@example.test"; var subject = Guid.NewGuid();
        Console.WriteLine($"ATTEMPT synthetic Customer identity/account: {id}");
        var signupProvider = new RegistrationProvider(subject, email); var writer = Writer(source, signupProvider);
        var created = await Create(writer, id, email) ?? throw new Exception("Verified synthetic registration denied.");
        await using (var c = await source.OpenConnectionAsync())
        await using (var q = new NpgsqlCommand("SELECT c.is_active AND i.auth_state='Supabase' AND i.supabase_user_id=@subject AND i.credential_sync_state='Ready' AND i.credential_epoch=1 AND i.customer_id=c.id AND i.admin_id IS NULL FROM public.customers c JOIN public.account_identities i ON i.id=c.app_id WHERE c.app_id=@id", c))
        { q.Parameters.AddWithValue("id", id); q.Parameters.AddWithValue("subject", subject); if ((bool?)await q.ExecuteScalarAsync() != true || created.Role != "Customer") throw new Exception("Synthetic registration state invalid."); }
        Console.WriteLine($"CREATED retained synthetic Customer identity/account: {id}");
        if (await Create(writer, id, email) != null) throw new Exception("Committed registration attempt replay accepted.");
        var emailConflict = Guid.NewGuid(); var subjectConflict = Guid.NewGuid();
        if (await Create(Writer(source, new RegistrationProvider(Guid.NewGuid(), email)), emailConflict, email) != null || await Exists(emailConflict)) throw new Exception("Duplicate email accepted.");
        var otherEmail = $"signup-{subjectConflict:N}@example.test";
        if (await Create(Writer(source, new RegistrationProvider(subject, otherEmail)), subjectConflict, otherEmail) != null || await Exists(subjectConflict)) throw new Exception("Duplicate subject accepted.");
        foreach (var mode in new[] { "unconfirmed", "wrong-email", "timeout", "reject" })
        {
            var rejectedId = Guid.NewGuid(); var rejectedEmail = $"signup-{rejectedId:N}@example.test";
            var rejected = new RegistrationProvider(Guid.NewGuid(), rejectedEmail) { Mode = mode };
            try { if (await Create(Writer(source, rejected), rejectedId, rejectedEmail) != null) throw new Exception("Invalid provider proof accepted."); }
            catch (Exception e) when (mode == "timeout" && e is TaskCanceledException || mode == "reject" && e is InvalidOperationException) { }
            if (await Exists(rejectedId) || rejected.Calls != 1) throw new Exception("Rejected provider proof mutated data or was retried.");
        }
        var raceEmail = $"signup-{Guid.NewGuid():N}@example.test"; var raceSubject = Guid.NewGuid();
        var raceIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var raceId in raceIds) Console.WriteLine($"ATTEMPT synthetic concurrent Customer identity/account: {raceId}");
        async Task<AppUser?> Race(Guid raceId)
        {
            try { return await Create(Writer(source, new RegistrationProvider(raceSubject, raceEmail)), raceId, raceEmail); }
            catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure) { return null; }
        }
        var raced = await Task.WhenAll(raceIds.Select(Race));
        if (raced.Count(x => x != null) != 1 || (await Exists(raceIds[0]) ? 1 : 0) + (await Exists(raceIds[1]) ? 1 : 0) != 1) throw new Exception("Concurrent registration was not unique.");
        Console.WriteLine($"CREATED retained synthetic concurrent Customer identity/account: {raced.Single(x => x != null)!.Id}");
        var rollbackId = Guid.NewGuid(); var rollbackEmail = $"signup-{rollbackId:N}@example.test";
        using (var observer = new SignupInsertFailureLogger())
        using (var logging = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information).AddProvider(observer)))
        {
            var faultBuilder = new NpgsqlDataSourceBuilder(cs.ConnectionString);
            faultBuilder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), c => Guard(c)); faultBuilder.UseLoggerFactory(logging);
            await using var faultSource = faultBuilder.Build();
            try { await Create(Writer(faultSource, new RegistrationProvider(Guid.NewGuid(), rollbackEmail)), rollbackId, rollbackEmail); throw new Exception("Interrupted transaction returned successfully."); }
            catch (AggregateException e) when (e.InnerExceptions.Any(x => x is SimulatedCommitAcknowledgmentException)) { }
            catch (SimulatedCommitAcknowledgmentException) { }
            if (!observer.Fired || await Exists(rollbackId)) throw new Exception("Interrupted insertion did not roll back.");
        }
        var unavailableId = Guid.NewGuid(); var unavailableEmail = $"signup-{unavailableId:N}@example.test";
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var unavailableCs = new NpgsqlConnectionStringBuilder(cs.ConnectionString) { Port = ((IPEndPoint)listener.LocalEndpoint).Port };
            var unavailableBuilder = new NpgsqlDataSourceBuilder(unavailableCs.ConnectionString);
            unavailableBuilder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), c => Guard(c));
            await using var unavailable = unavailableBuilder.Build();
            var fakeUnavailable = new RegistrationProvider(Guid.NewGuid(), unavailableEmail);
            var accept = listener.AcceptTcpClientAsync(); var attempt = Create(Writer(unavailable, fakeUnavailable), unavailableId, unavailableEmail);
            using (await accept.WaitAsync(TimeSpan.FromSeconds(10))) { }
            try { await attempt.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Unavailable database returned successfully."); }
            catch (NpgsqlException) { }
            if (fakeUnavailable.Calls != 1 || await Exists(unavailableId)) throw new Exception("Database failure retried or mutated data.");
        }
        finally { listener.Stop(); }
        var uncertainId = Guid.NewGuid(); var uncertainEmail = $"signup-{uncertainId:N}@example.test";
        Console.WriteLine($"ATTEMPT synthetic uncertain Customer identity/account: {uncertainId}");
        using (var observer = new CommitFailureLogger("UPDATE public.account_identities SET auth_state="))
        using (var logging = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug).AddProvider(observer)))
        {
            var faultBuilder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(cs.ConnectionString) { ApplicationName = "GlamSignupCommit-" + Guid.NewGuid().ToString("N") }.ConnectionString);
            faultBuilder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), c => Guard(c)); faultBuilder.UseLoggerFactory(logging);
            await using var faultSource = faultBuilder.Build();
            var uncertainProvider = new RegistrationProvider(Guid.NewGuid(), uncertainEmail);
            try { await Create(Writer(faultSource, uncertainProvider), uncertainId, uncertainEmail); throw new Exception("Unconfirmed commit returned successfully."); }
            catch (AggregateException e) when (e.InnerExceptions.Any(x => x is SimulatedCommitAcknowledgmentException)) { }
            catch (SimulatedCommitAcknowledgmentException) { }
            if (observer.CompletedUpdates != 1 || observer.CommitFailures != 1 || uncertainProvider.Calls != 1 || !await Exists(uncertainId)) throw new Exception("Simulated commit outcome invalid.");
        }
        Console.WriteLine($"CREATED retained synthetic unconfirmed Customer identity/account: {uncertainId}");
        Console.WriteLine("PASS direct fake-provider PostgreSQL registration writer: verified success, duplicate identities, invalid/uncertain provider proof, concurrent uniqueness, rollback, connection failure and simulated committed-but-unconfirmed outcome. No MVC signup attempt or ticket was exercised.");
        return;
    }
    if (args.Contains("--credential-only"))
    {
        foreach (var (role, account) in accounts)
        {
            HttpClient CredentialClient() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(url) };
            async Task<HttpResponseMessage> PostCredential(HttpClient client, string path, Dictionary<string,string> fields)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
                request.Headers.Add("X-CSRF-TOKEN", await client.GetStringAsync("/csrf"));
                return await client.SendAsync(request);
            }
            using var one = CredentialClient(); using var two = CredentialClient();
            var login = new Dictionary<string,string> { ["email"] = account.Email, ["password"] = "Synthetic123!" };
            using var first = await PostCredential(one, "/Account/Login", login);
            using var second = await PostCredential(two, "/Account/Login", login);
            if (first.StatusCode != HttpStatusCode.Redirect || second.StatusCode != HttpStatusCode.Redirect || await two.GetStringAsync("/protected") != role) throw new Exception("Credential test login failed.");
            using var changed = await PostCredential(one, "/Account/ChangePassword", new() { ["currentPassword"]="Synthetic123!", ["newPassword"]="SyntheticNew123!", ["confirmPassword"]="SyntheticNew123!" });
            if (changed.StatusCode != HttpStatusCode.Redirect || (await two.GetAsync("/protected")).StatusCode != HttpStatusCode.Unauthorized) throw new Exception("Credential update or stale-session rejection failed.");
            await using var c = await source.OpenConnectionAsync();
            await using var check = new NpgsqlCommand("SELECT credential_epoch=2 AND credential_sync_state='Ready' FROM public.account_identities WHERE id=@id", c);
            check.Parameters.AddWithValue("id", account.Id);
            if ((bool?)await check.ExecuteScalarAsync() != true) throw new Exception("Credential update not confirmed.");
            Console.WriteLine($"PASS {role} PostgreSQL MVC verified fake-provider password response, epoch advance and stale-cookie rejection");
        }
        return;
    }
    if (mvcProfileTests)
    {
        var format = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme).TicketDataFormat;
        foreach (var (role, account) in accounts)
        {
            var jar = new CookieContainer();
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = false }) { BaseAddress = new Uri(url) };
            async Task<HttpResponseMessage> PostProfile(string path, Dictionary<string,string> fields)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
                request.Headers.Add("X-CSRF-TOKEN", await client.GetStringAsync("/csrf"));
                return await client.SendAsync(request);
            }
            var login = new Dictionary<string,string> { ["email"] = account.Email, ["password"] = "Synthetic123!" };
            using var loginResponse = await PostProfile("/Account/Login", login);
            if (loginResponse.StatusCode != HttpStatusCode.Redirect || await client.GetStringAsync("/protected") != role) throw new Exception(role + " login failed.");
            var originalCookie = jar.GetCookies(new Uri(url))[".AspNetCore.Cookies"]?.Value ?? throw new Exception("Missing login cookie.");
            var ticket = format.Unprotect(originalCookie) ?? throw new Exception("Cannot read test ticket.");
            if (ticket.Principal.FindFirstValue(DurableSessionAuthorization.SubjectClaim) != account.Subject.ToString() || ticket.Principal.FindFirstValue(DurableSessionAuthorization.EpochClaim) != "1") throw new Exception("Login did not bind original subject and epoch.");
            Dictionary<string,string> Profile(string name) => new() { ["firstName"] = name, ["lastName"] = "Test", ["age"] = "25", ["gender"] = "Others", ["address"] = "Synthetic address" };
            bool AuthCookie(HttpResponseMessage response) => response.Headers.TryGetValues("Set-Cookie", out var values) && values.Any(x => x.StartsWith(".AspNetCore.Cookies=", StringComparison.OrdinalIgnoreCase));
            async Task AssertName(string name)
            {
                await using var c = await source.OpenConnectionAsync();
                await using var cmd = new NpgsqlCommand(role == "Customer" ? "SELECT first_name=@name FROM public.customers WHERE app_id=@id" : "SELECT first_name=@name FROM public.admin_users WHERE app_id=@id", c);
                cmd.Parameters.AddWithValue("id", account.Id); cmd.Parameters.AddWithValue("name", name);
                if ((bool?)await cmd.ExecuteScalarAsync() != true) throw new Exception(role + " unexpected profile.");
            }
            using (var saved = await PostProfile("/Account/SaveProfile", Profile("Saved")))
                if (saved.StatusCode != HttpStatusCode.Redirect || !AuthCookie(saved)) throw new Exception(role + " profile save failed.");
            await AssertName("Saved");
            var reissued = format.Unprotect(jar.GetCookies(new Uri(url))[".AspNetCore.Cookies"]!.Value) ?? throw new Exception("Missing refreshed ticket.");
            if (reissued.Principal.FindFirstValue(DurableSessionAuthorization.SubjectClaim) != account.Subject.ToString() || reissued.Principal.FindFirstValue(DurableSessionAuthorization.EpochClaim) != "1") throw new Exception("Profile reissued different identity or epoch.");
            foreach (var mode in new[] { "rollback", "commit" })
            {
                mvcFault.Arm(mode);
                using var response = await PostProfile("/Account/SaveProfile", Profile(mode == "rollback" ? "Rolledback" : "Committed"));
                if (response.StatusCode != HttpStatusCode.ServiceUnavailable || AuthCookie(response) || mvcFault.Updates != 1 || !mvcFault.Fired) throw new Exception(role + " " + mode + " fault was not contained.");
                await AssertName(mode == "rollback" ? "Saved" : "Committed");
            }
            mvcFault.Arm("connect");
            using (var unavailable = await PostProfile("/Account/SaveProfile", Profile("Unavailable")))
                if (unavailable.StatusCode != HttpStatusCode.ServiceUnavailable || AuthCookie(unavailable) || !mvcFault.Fired || mvcFault.Updates != 0) throw new Exception("MVC connection initialization failure not contained.");
            await AssertName("Committed");
            if (await client.GetStringAsync("/protected") != role) throw new Exception("Existing session lost after simulated fault.");
            foreach (var value in new string?[] { null, Guid.NewGuid().ToString() })
            {
                var identity = new ClaimsIdentity(reissued.Principal.Identity as ClaimsIdentity ?? throw new Exception());
                identity.RemoveClaim(identity.FindFirst(DurableSessionAuthorization.SubjectClaim)!);
                if (value != null) identity.AddClaim(new Claim(DurableSessionAuthorization.SubjectClaim, value));
                var bad = format.Protect(new Microsoft.AspNetCore.Authentication.AuthenticationTicket(new ClaimsPrincipal(identity), reissued.Properties, CookieAuthenticationDefaults.AuthenticationScheme));
                using var badClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(url) };
                badClient.DefaultRequestHeaders.Add("Cookie", ".AspNetCore.Cookies=" + bad);
                if ((await badClient.GetAsync("/protected")).StatusCode != HttpStatusCode.Unauthorized) throw new Exception(role + " invalid subject accepted.");
            }
            await using (var c = await source.OpenConnectionAsync())
            await using (var changeSubject = new NpgsqlCommand("UPDATE public.account_identities SET supabase_user_id=@subject WHERE id=@id AND supabase_user_id=@old", c))
            {
                changeSubject.Parameters.AddWithValue("subject", Guid.NewGuid()); changeSubject.Parameters.AddWithValue("old", account.Subject); changeSubject.Parameters.AddWithValue("id", account.Id);
                if (await changeSubject.ExecuteNonQueryAsync() != 1) throw new Exception("Synthetic identity test setup failed.");
            }
            if ((await client.GetAsync("/protected")).StatusCode != HttpStatusCode.Unauthorized) throw new Exception("Changed authoritative identity accepted.");
            await AssertName("Committed");
            await using (var c = await source.OpenConnectionAsync())
            await using (var restore = new NpgsqlCommand("UPDATE public.account_identities SET supabase_user_id=@subject WHERE id=@id", c))
            { restore.Parameters.AddWithValue("subject", account.Subject); restore.Parameters.AddWithValue("id", account.Id); if (await restore.ExecuteNonQueryAsync() != 1) throw new Exception("Synthetic identity restore failed."); }
            var otherJar = new CookieContainer(); otherJar.Add(new Uri(url), new Cookie(".AspNetCore.Cookies", jar.GetCookies(new Uri(url))[".AspNetCore.Cookies"]!.Value));
            using var other = new HttpClient(new HttpClientHandler { CookieContainer = otherJar, AllowAutoRedirect = false }) { BaseAddress = new Uri(url) };
            using var passwordRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/ChangePassword") { Content = new FormUrlEncodedContent(new Dictionary<string,string> { ["currentPassword"]="Synthetic123!", ["newPassword"]="SyntheticNew123!", ["confirmPassword"]="SyntheticNew123!" }) };
            passwordRequest.Headers.Add("X-CSRF-TOKEN", await other.GetStringAsync("/csrf"));
            await using (var owner = await source.OpenConnectionAsync())
            await using (var tx = await owner.BeginTransactionAsync())
            {
                await using var lockOwner = new NpgsqlCommand(role == "Customer" ? "SELECT pg_backend_pid() FROM public.customers WHERE app_id=@id FOR UPDATE" : "SELECT pg_backend_pid() FROM public.admin_users WHERE app_id=@id FOR UPDATE", owner, tx);
                lockOwner.Parameters.AddWithValue("id", account.Id);
                var ownerPid = (int)(await lockOwner.ExecuteScalarAsync() ?? throw new Exception("Race owner missing."));
                async Task<int> WaitForBlocker(int blocker, string? appName)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await using var monitor = await source.OpenConnectionAsync(timeout.Token);
                    await using var cmd = new NpgsqlCommand("SELECT pid FROM pg_stat_activity WHERE @blocker=ANY(pg_blocking_pids(pid)) AND (@name='' OR application_name=@name)", monitor);
                    cmd.Parameters.AddWithValue("blocker", blocker); cmd.Parameters.AddWithValue("name", appName ?? "");
                    while (true) { timeout.Token.ThrowIfCancellationRequested(); if (await cmd.ExecuteScalarAsync(timeout.Token) is int pid) return pid; }
                }
                var profileRace = PostProfile("/Account/SaveProfile", Profile("Ordered"));
                var profilePid = await WaitForBlocker(ownerPid, mvcProfileName);
                var passwordRace = other.SendAsync(passwordRequest);
                await WaitForBlocker(profilePid, null);
                await tx.CommitAsync();
                using var profileResult = await profileRace;
                using var passwordResult = await passwordRace;
                if (profileResult.StatusCode != HttpStatusCode.Redirect || !AuthCookie(profileResult) || passwordResult.StatusCode != HttpStatusCode.Redirect) throw new Exception("Concurrent MVC requests failed.");
            }
            await AssertName("Ordered");
            long epoch;
            await using (var c = await source.OpenConnectionAsync())
            await using (var check = new NpgsqlCommand("SELECT credential_epoch FROM public.account_identities WHERE id=@id", c))
            { check.Parameters.AddWithValue("id", account.Id); epoch = (long)(await check.ExecuteScalarAsync() ?? throw new Exception("Missing race epoch.")); }
            if (epoch == 1 && await new PostgresDurableCredentialStore(source).Begin(account.Id, account.Subject, 1, CancellationToken.None) != 2) throw new Exception("Credential invalidation failed.");
            if (epoch is not (1 or 2)) throw new Exception("Unexpected race epoch.");
            using (var stale = await PostProfile("/Account/SaveProfile", Profile("Stale")))
                if (stale.StatusCode != HttpStatusCode.Unauthorized || AuthCookie(stale)) throw new Exception("Stale profile cookie accepted or reissued.");
            await AssertName("Ordered");
            Console.WriteLine($"PASS {role} MVC save, concurrent MVC password/profile ordering, original ticket binding, failure containment and stale/invalid subjects denied");
        }
        return;
    }
    if (args.Contains("--profile-only") || args.Contains("--failure-only") || args.Contains("--commit-only"))
    {
        foreach (var (role, account) in accounts)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var local = scope.ServiceProvider.GetRequiredService<PublicStudioStore>();
            var user = local.FindUser(account.Email) ?? throw new Exception("Synthetic account missing.");
            var profileName = "GlamProfile-" + Guid.NewGuid().ToString("N");
            var credentialName = "GlamCredential-" + Guid.NewGuid().ToString("N");
            NpgsqlDataSource NamedSource(string name)
            {
                var namedCs = new NpgsqlConnectionStringBuilder(cs.ConnectionString) { ApplicationName = name };
                var named = new NpgsqlDataSourceBuilder(namedCs.ConnectionString);
                named.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), c => Guard(c));
                return named.Build();
            }
            await using var profileSource = NamedSource(profileName);
            await using var credentialSource = NamedSource(credentialName);
            await using var profileServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddSingleton(profileSource).BuildServiceProvider();
            var profiles = new AccountSettingsService(local, profileServices, scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>());
            var credentials = new PostgresDurableCredentialStore(credentialSource);
            var table = role == "Customer" ? "customers" : "admin_users";
            var version = AccountSession.Version(user.PasswordHash);
            AccountSessionEvidence Evidence(long epoch) => new(account.Id, role, epoch.ToString(System.Globalization.CultureInfo.InvariantCulture), version);
            async Task<int> WaitBlocked(string applicationName, int blocker)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await using var monitor = await source.OpenConnectionAsync(timeout.Token);
                await using var command = new NpgsqlCommand("SELECT pid FROM pg_stat_activity WHERE application_name=@name AND @blocker=ANY(pg_blocking_pids(pid))", monitor);
                command.Parameters.AddWithValue("name", applicationName); command.Parameters.AddWithValue("blocker", blocker);
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (await command.ExecuteScalarAsync(timeout.Token) is int pid) return pid;
                }
            }
            async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction, int Pid)> LockOwner()
            {
                var c = await source.OpenConnectionAsync(); var tx = await c.BeginTransactionAsync();
                await using var cmd = new NpgsqlCommand($"SELECT pg_backend_pid() FROM public.{table} WHERE app_id=@id FOR UPDATE", c, tx);
                cmd.Parameters.AddWithValue("id", account.Id);
                return (c, tx, (int)(await cmd.ExecuteScalarAsync() ?? throw new Exception("Owner lock failed.")));
            }
            async Task AssertProfile(string expected)
            {
                await using var c = await source.OpenConnectionAsync();
                await using var cmd = new NpgsqlCommand($"SELECT first_name=@first FROM public.{table} WHERE app_id=@id", c);
                cmd.Parameters.AddWithValue("first", expected); cmd.Parameters.AddWithValue("id", account.Id);
                if ((bool?)await cmd.ExecuteScalarAsync() != true) throw new Exception(role + " unexpected persisted profile.");
            }
            if (args.Contains("--commit-only"))
            {
                var before = (user.FirstName, user.LastName, user.Name, user.Age, user.Gender, user.Address);
                using var observer = new CommitFailureLogger($"UPDATE public.{table} SET first_name=");
                using var logging = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug).AddProvider(observer));
                var faultCs = new NpgsqlConnectionStringBuilder(cs.ConnectionString) { ApplicationName = "GlamCommit-" + Guid.NewGuid().ToString("N") };
                var faultBuilder = new NpgsqlDataSourceBuilder(faultCs.ConnectionString);
                faultBuilder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), c => Guard(c));
                faultBuilder.UseLoggerFactory(logging);
                await using (var faultSource = faultBuilder.Build())
                await using (var faultServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddSingleton(faultSource).BuildServiceProvider())
                {
                    var faultProfiles = new AccountSettingsService(local, faultServices, scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>());
                    try { await faultProfiles.ProfileWithDurableEpoch(user, Evidence(1), account.Subject, "Committed", "Test", 25, "Others", "Synthetic address"); throw new Exception("Simulated lost acknowledgment returned normally."); }
                    catch (AggregateException e) when (e.InnerExceptions.Any(x => x is SimulatedCommitAcknowledgmentException)) { }
                    catch (SimulatedCommitAcknowledgmentException) { }
                    if (observer.CompletedUpdates != 1 || observer.CommitFailures != 1) throw new Exception("Commit-boundary simulation did not execute exactly once.");
                }
                if ((user.FirstName, user.LastName, user.Name, user.Age, user.Gender, user.Address) != before) throw new Exception("Unconfirmed profile changed in-memory account.");
                await AssertProfile("Committed");
                await using (var c = await source.OpenConnectionAsync())
                await using (var check = new NpgsqlCommand("SELECT credential_epoch=1 AND credential_sync_state='Ready' FROM public.account_identities WHERE id=@id", c))
                { check.Parameters.AddWithValue("id", account.Id); if ((bool?)await check.ExecuteScalarAsync() != true) throw new Exception("Unexpected credential change."); }
                Console.WriteLine($"PASS {role} simulated acknowledgment exception after committed transaction: no success, no retry, persisted outcome observed separately, epoch unchanged");
                continue;
            }
            if (args.Contains("--failure-only"))
            {
                var originalProfile = (user.FirstName, user.LastName, user.Name, user.Age, user.Gender, user.Address);
                using var cancel = new CancellationTokenSource();
                using var observer = new UpdateCompletionLogger($"UPDATE public.{table} SET first_name=", cancel);
                using var logging = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information).AddProvider(observer));
                var faultCs = new NpgsqlConnectionStringBuilder(cs.ConnectionString) { ApplicationName = "GlamRollback-" + Guid.NewGuid().ToString("N") };
                var faultBuilder = new NpgsqlDataSourceBuilder(faultCs.ConnectionString);
                faultBuilder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), c => Guard(c));
                faultBuilder.UseLoggerFactory(logging);
                await using (var faultSource = faultBuilder.Build())
                await using (var faultServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddSingleton(faultSource).BuildServiceProvider())
                {
                    var faultProfiles = new AccountSettingsService(local, faultServices, scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>());
                    try
                    {
                        await faultProfiles.ProfileWithDurableEpoch(user, Evidence(1), account.Subject, "Rolledback", "Changed", 30, "Others", "Changed address", cancel.Token);
                        throw new Exception("Post-update cancellation returned normally.");
                    }
                    catch (OperationCanceledException) { }
                    if (observer.CompletedUpdates != 1) throw new Exception("Expected one completed profile UPDATE before cancellation.");
                }
                await using (var c = await source.OpenConnectionAsync())
                await using (var check = new NpgsqlCommand($"SELECT first_name='Synthetic' AND last_name=@role AND age_at_registration=25 AND gender='Others' AND address='Synthetic address'{(role == "Customer" ? " AND full_name='Synthetic Test'" : "")} FROM public.{table} WHERE app_id=@id", c))
                {
                    check.Parameters.AddWithValue("role", role); check.Parameters.AddWithValue("id", account.Id);
                    if ((bool?)await check.ExecuteScalarAsync() != true) throw new Exception("Post-update rollback left profile changes.");
                }
                if ((user.FirstName, user.LastName, user.Name, user.Age, user.Gender, user.Address) != originalProfile) throw new Exception("Post-update rollback changed in-memory account.");
                Console.WriteLine($"PASS {role} observed completed UPDATE, cancelled before commit, persisted and in-memory profile unchanged");

                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                try
                {
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var unavailableCs = new NpgsqlConnectionStringBuilder(cs.ConnectionString) { Port = port };
                    var unavailableBuilder = new NpgsqlDataSourceBuilder(unavailableCs.ConnectionString);
                    unavailableBuilder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), c => Guard(c));
                    await using var unavailable = unavailableBuilder.Build();
                    await using var unavailableServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddSingleton(unavailable).BuildServiceProvider();
                    var unavailableProfiles = new AccountSettingsService(local, unavailableServices, scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>());
                    var accept = listener.AcceptTcpClientAsync();
                    var failed = unavailableProfiles.ProfileWithDurableEpoch(user, Evidence(1), account.Subject, "Unreachable", "Test", 25, "Others", "Synthetic address");
                    using (await accept.WaitAsync(TimeSpan.FromSeconds(10))) { }
                    try { await failed.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Failed connection returned normally."); }
                    catch (NpgsqlException) { }
                }
                finally { listener.Stop(); }
                await AssertProfile("Synthetic");
                if ((user.FirstName, user.LastName, user.Name, user.Age, user.Gender, user.Address) != originalProfile) throw new Exception("Connection failure changed in-memory account.");
                Console.WriteLine($"PASS {role} loopback connection closed during establishment; profile operation failed without mutation");
                using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(url) };
                async Task<HttpResponseMessage> PostFailureCheck(string path, Dictionary<string, string> fields)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
                    request.Headers.Add("X-CSRF-TOKEN", await client.GetStringAsync("/csrf"));
                    return await client.SendAsync(request);
                }
                using var login = await PostFailureCheck("/Account/Login", new() { ["email"] = account.Email, ["password"] = "Synthetic123!" });
                if (login.StatusCode != HttpStatusCode.Redirect || await client.GetStringAsync("/protected") != role) throw new Exception("Failure-check login failed.");
                using var blocked = await PostFailureCheck("/Account/SaveProfile", new() { ["firstName"] = "Blocked", ["lastName"] = "Test", ["age"] = "25", ["gender"] = "Others", ["address"] = "Synthetic address" });
                if (blocked.StatusCode != HttpStatusCode.Redirect || !(blocked.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(x => x.StartsWith(".AspNetCore.Cookies=", StringComparison.OrdinalIgnoreCase))))
                    throw new Exception("Gated profile did not complete with an authentication cookie.");
                await AssertProfile("Blocked");
                Console.WriteLine($"PASS {role} gated MVC profile succeeds after direct failure checks");
                continue;
            }
            var cancelLock = await LockOwner();
            await using (cancelLock.Connection)
            await using (cancelLock.Transaction)
            using (var cancel = new CancellationTokenSource())
            {
                var interrupted = profiles.ProfileWithDurableEpoch(user, Evidence(1), account.Subject, "Cancelled", "Test", 25, "Others", "Synthetic address", cancel.Token);
                await WaitBlocked(profileName, cancelLock.Pid);
                cancel.Cancel();
                try { await interrupted; throw new Exception("Cancelled profile returned normally."); }
                catch (OperationCanceledException) { }
                await cancelLock.Transaction.RollbackAsync();
            }
            await AssertProfile("Synthetic");
            if (user.FirstName != "Synthetic") throw new Exception("Cancelled profile changed in-memory user.");
            Console.WriteLine($"PASS {role} profile transaction cancelled while waiting before mutation; no profile change");
            var failureLock = await LockOwner();
            await using (failureLock.Connection)
            await using (failureLock.Transaction)
            {
                var interrupted = profiles.ProfileWithDurableEpoch(user, Evidence(1), account.Subject, "Disconnected", "Test", 25, "Others", "Synthetic address");
                var pid = await WaitBlocked(profileName, failureLock.Pid);
                await using (var c = await source.OpenConnectionAsync())
                await using (var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE pid=@pid AND application_name=@name AND @blocker=ANY(pg_blocking_pids(pid))", c))
                {
                    terminate.Parameters.AddWithValue("pid", pid); terminate.Parameters.AddWithValue("name", profileName); terminate.Parameters.AddWithValue("blocker", failureLock.Pid);
                    if ((bool?)await terminate.ExecuteScalarAsync() != true) throw new Exception("Test backend was not terminated.");
                }
                try { await interrupted; throw new Exception("Disconnected profile returned normally."); }
                catch (NpgsqlException) { }
                await failureLock.Transaction.RollbackAsync();
            }
            await AssertProfile("Synthetic");
            if (user.FirstName != "Synthetic") throw new Exception("Disconnected profile changed in-memory user.");
            Console.WriteLine($"PASS {role} profile database connection terminated before mutation; no profile change");
            // Credential transaction locks identity first, then waits for the held owner row.
            var firstLock = await LockOwner();
            await using (firstLock.Connection)
            await using (firstLock.Transaction)
            {
                var begin = credentials.Begin(account.Id, account.Subject, 1, CancellationToken.None);
                var credentialPid = await WaitBlocked(credentialName, firstLock.Pid);
                var staleProfile = profiles.ProfileWithDurableEpoch(user, Evidence(1), account.Subject, "TooLate", "Test", 25, "Others", "Synthetic address");
                await WaitBlocked(profileName, credentialPid);
                await firstLock.Transaction.CommitAsync();
                if (await begin != 2) throw new Exception(role + " credential invalidation failed.");
                try { if (await staleProfile) throw new Exception(role + " stale profile committed."); }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.SerializationFailure) { }
            }
            await AssertProfile("Synthetic");
            if (!await credentials.Reject(account.Id, account.Subject, 2, CancellationToken.None)) throw new Exception("Synthetic transition failed.");
            if (await profiles.ProfileWithDurableEpoch(user, Evidence(1), account.Subject, "Stale", "Test", 25, "Others", "Synthetic address")) throw new Exception("Stale epoch accepted.");
            Console.WriteLine($"PASS {role} credential identity lock first; stale profile denied after committed invalidation");

            // Profile transaction locks identity first, then waits for the held owner row.
            var secondLock = await LockOwner();
            await using (secondLock.Connection)
            await using (secondLock.Transaction)
            {
                var profile = profiles.ProfileWithDurableEpoch(user, Evidence(2), account.Subject, "First", "Test", 25, "Others", "Synthetic address");
                var profilePid = await WaitBlocked(profileName, secondLock.Pid);
                var begin = credentials.Begin(account.Id, account.Subject, 2, CancellationToken.None);
                await WaitBlocked(credentialName, profilePid);
                await secondLock.Transaction.CommitAsync();
                if (!await profile) throw new Exception(role + " first profile did not commit.");
                long? epoch;
                try { epoch = await begin; }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.SerializationFailure)
                { epoch = await credentials.Begin(account.Id, account.Subject, 2, CancellationToken.None); }
                if (epoch != 3) throw new Exception(role + " subsequent invalidation failed.");
            }
            await AssertProfile("First");
            if (await profiles.ProfileWithDurableEpoch(user, Evidence(2), account.Subject, "Stale", "Test", 25, "Others", "Synthetic address")) throw new Exception("Invalidated epoch accepted.");
            await AssertProfile("First");
            Console.WriteLine($"PASS {role} profile identity lock first; committed profile precedes invalidation and stale epoch denied");
        }
        return;
    }
    HttpClient Client(CookieContainer? jar = null) => new(new HttpClientHandler { CookieContainer = jar ?? new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(url) };
    async Task<(Process Process, string Url)> StartWorker()
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add(typeof(AccountOnlyActivator).Assembly.Location); info.ArgumentList.Add("--worker");
        var process = Process.Start(info) ?? throw new InvalidOperationException("Worker did not start.");
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (line == null || !line.StartsWith("READY=http://127.0.0.1:", StringComparison.Ordinal)) { process.Kill(true); throw new InvalidOperationException("Worker did not report a safe loopback URL."); }
        return (process, line[6..]);
    }
    async Task StopWorker(Process process)
    {
        await process.StandardInput.WriteLineAsync("stop");
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { if (!process.HasExited) process.Kill(true); process.Dispose(); }
    }
    async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string,string>? fields = null)
    { using var request = new HttpRequestMessage(HttpMethod.Post, path); request.Headers.Add("X-CSRF-TOKEN", await client.GetStringAsync("/csrf")); if (fields != null) request.Content = new FormUrlEncodedContent(fields); return await client.SendAsync(request); }
    foreach (var role in accounts.Keys)
    {
        var account = accounts[role]; var secondJar = new CookieContainer(); using var one = Client(); using var two = Client(secondJar);
        var login = new Dictionary<string,string> { ["email"]=account.Email, ["password"]="Synthetic123!" };
        if ((await Post(one,"/Account/Login",login)).StatusCode != HttpStatusCode.Redirect || (await Post(two,"/Account/Login",login)).StatusCode != HttpStatusCode.Redirect || await one.GetStringAsync("/protected") != role || await two.GetStringAsync("/protected") != role) throw new Exception(role + " login failed");
        var profile = new Dictionary<string,string> { ["firstName"]="Changed",["lastName"]="Test",["age"]="25",["gender"]="Others",["address"]="Synthetic address" };
        AppUser profileUser;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var local = scope.ServiceProvider.GetRequiredService<PublicStudioStore>();
            profileUser = local.FindUser(account.Email) ?? throw new Exception("Synthetic profile missing.");
            var evidence = new AccountSessionEvidence(account.Id, role, "1", AccountSession.Version(profileUser.PasswordHash));
            var profiles = scope.ServiceProvider.GetRequiredService<AccountSettingsService>();
            if (await profiles.ProfileWithDurableEpoch(profileUser, evidence, Guid.NewGuid(), "Mismatch", "Test", 25, "Others", "Synthetic address"))
                throw new Exception(role + " mismatched subject accepted.");
            await using (var c = await source.OpenConnectionAsync())
            await using (var disable = new NpgsqlCommand(role == "Customer" ? "UPDATE public.customers SET is_active=false WHERE app_id=@id" : "UPDATE public.admin_users SET is_active=false WHERE app_id=@id", c))
            { disable.Parameters.AddWithValue("id", account.Id); if (await disable.ExecuteNonQueryAsync() != 1) throw new Exception("Disable failed."); }
            if (await profiles.ProfileWithDurableEpoch(profileUser, evidence, account.Subject, "Disabled", "Test", 25, "Others", "Synthetic address"))
                throw new Exception(role + " disabled profile accepted.");
            await using (var c = await source.OpenConnectionAsync())
            await using (var enable = new NpgsqlCommand(role == "Customer" ? "UPDATE public.customers SET is_active=true WHERE app_id=@id AND first_name='Synthetic'" : "UPDATE public.admin_users SET is_active=true WHERE app_id=@id AND first_name='Synthetic'", c))
            { enable.Parameters.AddWithValue("id", account.Id); if (await enable.ExecuteNonQueryAsync() != 1) throw new Exception("Denied profile mutated or enable failed."); }
            Console.WriteLine($"PASS {role} profile subject mismatch and disabled account denied without mutation");
            if (!await profiles.ProfileWithDurableEpoch(profileUser, evidence, account.Subject, "Approved", "Test", 25, "Others", "Synthetic address"))
                throw new Exception(role + " atomic profile preparation failed.");
        }
        await using (var c = await source.OpenConnectionAsync())
        await using (var check = new NpgsqlCommand(role == "Customer" ? "SELECT first_name='Approved' FROM public.customers WHERE app_id=@id" : "SELECT first_name='Approved' FROM public.admin_users WHERE app_id=@id", c))
        { check.Parameters.AddWithValue("id", account.Id); if ((bool?)await check.ExecuteScalarAsync() != true) throw new Exception("Profile did not persist."); }
        profile["firstName"] = "Approved";
        if ((await Post(one,"/Account/SaveProfile",profile)).StatusCode != HttpStatusCode.Redirect) throw new Exception(role + " MVC profile save failed");
        var change = new Dictionary<string,string> { ["currentPassword"]="Synthetic123!",["newPassword"]="SyntheticNew123!",["confirmPassword"]="SyntheticNew123!" };
        var cookie = secondJar.GetCookieHeader(new Uri(url));
        var workerHost = await StartWorker();
        try
        {
            using var remote = new HttpClient { BaseAddress = new Uri(workerHost.Url) }; remote.DefaultRequestHeaders.Add("Cookie", cookie);
            if (await remote.GetStringAsync("/protected") != role) throw new Exception(role + " worker rejected current cookie.");
            if ((await Post(one,"/Account/ChangePassword",change)).StatusCode != HttpStatusCode.Redirect || (await two.GetAsync("/protected")).StatusCode != HttpStatusCode.Unauthorized || (await remote.GetAsync("/protected")).StatusCode != HttpStatusCode.Unauthorized)
                throw new Exception(role + " cross-process stale-cookie rejection failed.");
            if ((await Post(two,"/Account/SaveProfile",profile)).StatusCode != HttpStatusCode.Unauthorized)
                throw new Exception(role + " stale cookie reached profile action.");
        }
        finally { await StopWorker(workerHost.Process); }
        var restarted = await StartWorker();
        try
        {
            using var remote = new HttpClient { BaseAddress = new Uri(restarted.Url) }; remote.DefaultRequestHeaders.Add("Cookie", cookie);
            if ((await remote.GetAsync("/protected")).StatusCode != HttpStatusCode.Unauthorized) throw new Exception(role + " stale cookie survived process restart.");
        }
        finally { await StopWorker(restarted.Process); }
        Console.WriteLine($"PASS {role} second process rejects invalidated cookie before and after process restart");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var stale = new AccountSessionEvidence(account.Id, role, "1", AccountSession.Version(profileUser.PasswordHash));
            if (await scope.ServiceProvider.GetRequiredService<AccountSettingsService>().ProfileWithDurableEpoch(profileUser, stale, account.Subject, "Stale", "Test", 25, "Others", "Synthetic address")) throw new Exception("Stale profile update accepted.");
        }
        var reset = new Dictionary<string,string> { ["email"]=account.Email,["code"]="123456",["newPassword"]="SyntheticReset123!",["confirmPassword"]="SyntheticReset123!" };
        if ((await Post(one,"/Account/ResetPassword",reset)).StatusCode != HttpStatusCode.Redirect) throw new Exception(role + " reset response failed.");
        await using (var c = await source.OpenConnectionAsync())
        await using (var check = new NpgsqlCommand("SELECT credential_epoch=3 AND credential_sync_state='Ready' FROM public.account_identities WHERE id=@id", c))
        { check.Parameters.AddWithValue("id", account.Id); if ((bool?)await check.ExecuteScalarAsync() != true) throw new Exception(role + " reset did not commit."); }
        if ((await Post(one,"/Account/Login",login)).StatusCode != HttpStatusCode.Redirect || await one.GetStringAsync("/protected") != role || (await Post(one,"/Account/Logout")).StatusCode != HttpStatusCode.Redirect || (await one.GetAsync("/protected")).StatusCode != HttpStatusCode.Unauthorized)
            throw new Exception(role + " login/logout after reset failed.");
        await using (var scope = app.Services.CreateAsyncScope())
        await using (var c = await source.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var current = scope.ServiceProvider.GetRequiredService<PublicStudioStore>().FindUser(account.Email) ?? throw new Exception("Synthetic user missing.");
            var evidence = new AccountSessionEvidence(account.Id, role, "3", AccountSession.Version(current.PasswordHash));
            await using (var locked = new NpgsqlCommand("SELECT id FROM public.account_identities WHERE id=@id AND credential_epoch=3 AND credential_sync_state='Ready' FOR UPDATE", c, tx))
            { locked.Parameters.AddWithValue("id", account.Id); if (await locked.ExecuteScalarAsync() == null) throw new Exception("Race setup failed."); }
            var pendingProfile = scope.ServiceProvider.GetRequiredService<AccountSettingsService>().ProfileWithDurableEpoch(current, evidence, account.Subject, "Stale", "Test", 25, "Others", "Synthetic address");
            await using (var revoke = new NpgsqlCommand("UPDATE public.account_identities SET credential_epoch=4,credential_sync_state='Pending' WHERE id=@id AND credential_epoch=3 AND credential_sync_state='Ready'", c, tx))
            { revoke.Parameters.AddWithValue("id", account.Id); if (await revoke.ExecuteNonQueryAsync() != 1) throw new Exception("Revocation failed."); }
            await tx.CommitAsync();
            try { if (await pendingProfile) throw new Exception("Profile committed after invalidation."); }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.SerializationFailure) { }
        }
        await using (var c = await source.OpenConnectionAsync())
        await using (var check = new NpgsqlCommand(role == "Customer" ? "SELECT first_name='Approved' FROM public.customers WHERE app_id=@id" : "SELECT first_name='Approved' FROM public.admin_users WHERE app_id=@id", c))
        { check.Parameters.AddWithValue("id", account.Id); if ((bool?)await check.ExecuteScalarAsync() != true) throw new Exception("Stale profile mutation detected."); }
        Console.WriteLine($"PASS {role} PostgreSQL profile denied after locked epoch invalidation");
        Console.WriteLine($"PASS {role} MVC login, cookies, change/reset/logout, atomic profile and stale profile denial");
    }
}
finally { await app.StopAsync(); await app.DisposeAsync(); tlsCertificate?.Dispose(); }
Console.WriteLine("Test identities and isolated Data Protection keys retained. Opposite profile lock order and database-server restart not covered by this run.");

sealed class AccountOnlyActivator : IControllerActivator
{
    public object Create(ControllerContext c) => c.ActionDescriptor.ControllerTypeInfo.AsType() == typeof(AccountController) ? c.HttpContext.RequestServices.GetRequiredService<AccountController>() : throw new InvalidOperationException();
    public void Release(ControllerContext c, object controller) { }
}
// Only attached to the uniquely named test data source; SQL parameters are never logged.
sealed class UpdateCompletionLogger(string prefix, CancellationTokenSource cancel) : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ILogger
{
    public int CompletedUpdates { get; private set; }
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;
    public void Dispose() { }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => level == Microsoft.Extensions.Logging.LogLevel.Information;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
        var entries = fields.ToArray();
        if (entries.Any(x => x.Key == "{OriginalFormat}" && x.Value is string format && format.StartsWith("Command execution completed", StringComparison.Ordinal)) &&
            entries.Any(x => x.Key == "CommandText" && x.Value is string sql && sql.StartsWith(prefix, StringComparison.Ordinal)))
        { CompletedUpdates++; cancel.Cancel(); }
    }
}
sealed class SimulatedCommitAcknowledgmentException : Exception;
sealed class SignupInsertFailureLogger : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ILogger
{
    public bool Fired { get; private set; }
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;
    public void Dispose() { }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => level == Microsoft.Extensions.Logging.LogLevel.Information;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (Fired || state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
        var entries = fields.ToArray();
        if (entries.Any(x => x.Key == "{OriginalFormat}" && x.Value is string f && f.StartsWith("Command execution completed", StringComparison.Ordinal)) &&
            entries.Any(x => x.Key == "CommandText" && x.Value is string sql && sql.StartsWith("INSERT INTO public.customers(", StringComparison.Ordinal)))
        { Fired = true; throw new SimulatedCommitAcknowledgmentException(); }
    }
}
sealed class RegistrationProvider(Guid subject, string email) : IHttpClientFactory
{
    private readonly Guid subjectValue = subject;
    private readonly string emailValue = email;
    public string? Mode { get; set; }
    public int Calls { get; private set; }
    public HttpClient CreateClient(string name) => new(new Handler(this), false);
    private sealed class Handler(RegistrationProvider owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.Host != "auth.example.test" || request.RequestUri.AbsolutePath != "/auth/v1/verify" || request.Method != HttpMethod.Post)
                throw new InvalidOperationException("Unexpected provider request.");
            owner.Calls++;
            if (owner.Mode == "timeout") throw new TaskCanceledException("Synthetic provider timeout.");
            if (owner.Mode == "reject") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"msg\":\"invalid otp\"}") });
            var payload = JsonSerializer.Serialize(new { access_token="synthetic-proof", user=new { id=owner.subjectValue, email=owner.Mode == "wrong-email" ? "other@example.test" : owner.emailValue, email_confirmed_at=owner.Mode == "unconfirmed" ? null : "2026-01-01T00:00:00Z" } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });
        }
    }
}
sealed class MvcProfileFaultLogger : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ILogger
{
    private string? mode;
    public int Updates { get; private set; }
    public bool Fired { get; private set; }
    public void Arm(string value) { mode = value; Updates = 0; Fired = false; }
    public void FailConnectionIfArmed()
    { if (mode == "connect") { Fired = true; mode = null; throw new IOException("Synthetic test connection initialization failure."); } }
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;
    public void Dispose() { }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => level is Microsoft.Extensions.Logging.LogLevel.Information or Microsoft.Extensions.Logging.LogLevel.Debug;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (mode == null || state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
        var entries = fields.ToArray();
        if (mode is "signup-rollback" or "signup-commit" && entries.Any(x => x.Key == "{OriginalFormat}" && x.Value is string text && text.StartsWith("Command execution completed", StringComparison.Ordinal)) &&
            entries.Any(x => x.Key == "CommandText" && x.Value is string sql && sql.StartsWith("INSERT INTO public.customers(", StringComparison.Ordinal)))
        {
            Updates++;
            if (mode == "signup-rollback") { Fired = true; mode = null; throw new SimulatedCommitAcknowledgmentException(); }
        }
        if (mode == "signup-commit" && Updates == 1 && entries.Any(x => x.Key == "{OriginalFormat}" && Equals(x.Value, "Committed transaction")))
        { Fired = true; mode = null; throw new SimulatedCommitAcknowledgmentException(); }
        if (entries.Any(x => x.Key == "{OriginalFormat}" && x.Value is string f && f.StartsWith("Command execution completed", StringComparison.Ordinal)) &&
            entries.Any(x => x.Key == "CommandText" && x.Value is string sql && (sql.StartsWith("UPDATE public.customers SET first_name=", StringComparison.Ordinal) || sql.StartsWith("UPDATE public.admin_users SET first_name=", StringComparison.Ordinal))))
        {
            Updates++;
            if (mode == "rollback") { Fired = true; mode = null; throw new SimulatedCommitAcknowledgmentException(); }
        }
        if (mode == "commit" && Updates == 1 && entries.Any(x => x.Key == "{OriginalFormat}" && Equals(x.Value, "Committed transaction")))
        { Fired = true; mode = null; throw new SimulatedCommitAcknowledgmentException(); }
    }
}
sealed class CommitFailureLogger(string prefix) : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ILogger
{
    public int CompletedUpdates { get; private set; }
    public int CommitFailures { get; private set; }
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;
    public void Dispose() { }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => level is Microsoft.Extensions.Logging.LogLevel.Information or Microsoft.Extensions.Logging.LogLevel.Debug;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
        var entries = fields.ToArray();
        if (entries.Any(x => x.Key == "{OriginalFormat}" && x.Value is string format && format.StartsWith("Command execution completed", StringComparison.Ordinal)) &&
            entries.Any(x => x.Key == "CommandText" && x.Value is string sql && sql.StartsWith(prefix, StringComparison.Ordinal))) CompletedUpdates++;
        if (CompletedUpdates == 1 && CommitFailures == 0 && entries.Any(x => x.Key == "{OriginalFormat}" && Equals(x.Value, "Committed transaction")))
        { CommitFailures++; throw new SimulatedCommitAcknowledgmentException(); }
    }
}
sealed class NoFiles : IAppFileStore
{
    public void Save(string key, byte[] bytes) => throw new InvalidOperationException();
    public (byte[] Bytes, string ContentType)? Read(string? key) => throw new InvalidOperationException();
    public void Delete(string? key) => throw new InvalidOperationException();
}
sealed class FakeProvider(Dictionary<string, (Guid Id, Guid Subject, string Email)> accounts) : IHttpClientFactory
{
    private readonly Handler handler = new(accounts);
    public string? Mode { get => handler.Mode; set => handler.Mode = value; }
    public int OtpCalls => handler.OtpCalls;
    public HttpClient CreateClient(string name) => new(handler, false);
    private sealed class Handler(Dictionary<string, (Guid Id, Guid Subject, string Email)> accounts) : HttpMessageHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Guid> signups = new(StringComparer.OrdinalIgnoreCase);
        public string? Mode { get; set; }
        public int OtpCalls;
        private readonly Guid duplicateSubject = Guid.NewGuid();
        private static Guid SubjectFor(string email) => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(email.ToLowerInvariant()))[..16]);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (r.RequestUri?.Host != "auth.example.test") throw new InvalidOperationException("External provider forbidden.");
            if (r.Method == HttpMethod.Put && r.RequestUri.AbsolutePath == "/auth/v1/user")
            {
                var matching = accounts.Values.Single(x => x.Subject.ToString() == r.Headers.Authorization?.Parameter);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id = matching.Subject, email = matching.Email })) };
            }
            if (r.Method != HttpMethod.Post || r.RequestUri.AbsolutePath is not ("/auth/v1/token" or "/auth/v1/verify" or "/auth/v1/signup")) throw new InvalidOperationException("Unexpected provider request.");
            using var doc = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            var email = doc.RootElement.GetProperty("email").GetString()!;
            if (r.RequestUri.AbsolutePath == "/auth/v1/signup")
            {
                var subject = Mode == "duplicate-subject" ? duplicateSubject : SubjectFor(email);
                if (!signups.TryAdd(email, subject)) return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"msg\":\"already registered\"}") };
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id=subject, email })) };
            }
            if (signups.TryGetValue(email, out var signupSubject))
            {
                if (r.RequestUri.AbsolutePath == "/auth/v1/verify")
                {
                    Interlocked.Increment(ref OtpCalls);
                    if (Mode == "timeout") throw new TaskCanceledException("Synthetic timeout.");
                    if (Mode == "reject") return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"msg\":\"invalid otp\"}") };
                    if (Mode == "mismatch") signupSubject = Guid.NewGuid();
                }
                if (r.RequestUri.AbsolutePath == "/auth/v1/verify" && doc.RootElement.GetProperty("token").GetString() != "123456") return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"msg\":\"invalid otp\"}") };
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { access_token=signupSubject.ToString(), user=new { id=signupSubject, email, email_confirmed_at="2026-01-01T00:00:00Z" } })) };
            }
            if (email.StartsWith("mvc-signup-", StringComparison.Ordinal) && r.RequestUri.AbsolutePath is "/auth/v1/verify" or "/auth/v1/token")
            {
                var deterministicSubject = SubjectFor(email);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { access_token=deterministicSubject.ToString(), user=new { id=deterministicSubject, email, email_confirmed_at="2026-01-01T00:00:00Z" } })) };
            }
            var account = accounts.Values.Single(x => x.Email == email);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { access_token=account.Subject.ToString(), user=new { id=account.Subject, email=account.Email, email_confirmed_at="2026-01-01T00:00:00Z" } })) };
        }
    }
}
