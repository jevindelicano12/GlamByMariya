using System.Net;
using System.Security.Claims;
using System.Text.Json;
using GlamByMariya.Web.Controllers;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

// Isolated loopback host; fake proof and snapshots only, never application startup.
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddStudioCookieAuthentication(isDevelopment: true, durableEnforcement: true);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<TrustedRequestSessionAccessor>();
builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(RateLimitPolicies.Configure);
builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
    .AddApplicationPart(typeof(AccountController).Assembly);
builder.Services.AddSingleton<IControllerActivator, AccountOnlyActivator>();
var store = new FakeSnapshots();
var demo = new FakePersistentStore();
var accounts = new Dictionary<string, AppUser>();
foreach (var role in new[] { "Customer", "Admin", "SuperAdmin" })
    accounts[role] = demo.AddUser("Synthetic", role, $"http-{role.ToLowerInvariant()}@example.test", "Synthetic123!", role, 25, "Others", "Synthetic address");
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
    ["Authentication:DurableSessionEnforcement"] = "true", ["Supabase:Url"] = "https://auth.example.test", ["Supabase:AnonKey"] = "synthetic" }).Build();
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:DurableSessionEnforcement"] = "true" });
var clientFactory = new FakeProviderFactory(accounts, store);
var providerAuth = new SupabaseAuthService(clientFactory, config, NullLogger<SupabaseAuthService>.Instance);
var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
builder.Services.AddSingleton(sp => new AccountSettingsService(new DemoStore(), sp, cache));
var codes = new EmailVerificationService(cache, config, NullLogger<EmailVerificationService>.Instance, null!, null!);
builder.Services.AddSingleton<IStudioStore>(demo);
builder.Services.AddSingleton(new LoginAttemptLimiter());
builder.Services.AddTransient(sp => new AccountController(demo, providerAuth, codes, null!, null!, null!, sp.GetRequiredService<LoginAttemptLimiter>(), config));
builder.Services.AddSingleton<IAccountSecuritySnapshotProvider>(store);
builder.Services.AddSingleton<DurableSessionAuthorization>();
builder.Services.AddVersionedAuthenticationCandidate();
builder.Services.AddSingleton<IPostgresAccountSecurityQuery>(new FakeSecurityQuery(store));
var changes = new FakeAdminTransactions(store);
var adminStore = new AdminStoreChoice(demo);
var rosterReads = new FakeRosterReads(store);
builder.Services.AddSingleton<IAccountAuthorizationTransactionFactory>(changes);
builder.Services.AddSingleton<IAdminRosterReadSessionFactory>(rosterReads);
builder.Services.AddSingleton(adminStore);
builder.Services.AddTransient(sp => new AdminController(sp.GetRequiredService<AdminStoreChoice>().Store, null!, null!, null!, null!, null!));
builder.Services.AddSingleton<IDurableCredentialStore>(store);
builder.Services.AddSingleton<DurableCredentialUpdate>();
var app = builder.Build();
app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
app.MapControllerRoute("isolated-mvc", "{controller=Home}/{action=Index}/{id?}");
app.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) => antiforgery.GetAndStoreTokens(context).RequestToken!);
app.MapPost("/login/{role}/{proof}", async (string role, string proof, HttpContext context, IAntiforgery antiforgery, DurableSessionAuthorization authorization) =>
{
    try { await antiforgery.ValidateRequestAsync(context); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }
    if (!store.TryGet(role, out var snapshot)) return Results.Unauthorized();
    // Fake provider proof for this test host only; never a real identity verifier.
    var subject = proof == "matched" ? snapshot.SupabaseSubject!.Value : Guid.NewGuid();
    var evidence = await authorization.PrepareSupabaseLogin(snapshot.AccountId, subject, role);
    if (evidence == null) return Results.Unauthorized();
    var claims = new[] { new Claim(ClaimTypes.NameIdentifier, evidence.AccountId.ToString()), new Claim(ClaimTypes.Role, evidence.Role!),
        new Claim(DurableSessionAuthorization.EpochClaim, evidence.CredentialEpoch!), new Claim("account_version", evidence.AccountVersion!), new Claim(DurableSessionAuthorization.SubjectClaim, subject.ToString()),
        new Claim(VersionedSessionAuthorization.SecurityVersionClaim, "1") };
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok();
});
app.MapGet("/protected", (ClaimsPrincipal user) => Results.Text(user.FindFirstValue(ClaimTypes.Role)!)).RequireAuthorization();
app.MapGet("/trusted-context", async (TrustedRequestSessionAccessor accessor) =>
    await accessor.ResolveAsync() is not null ? Results.Ok() : Results.Unauthorized());
app.MapGet("/trusted-context-disabled", async (HttpContext context, Microsoft.Extensions.Options.IOptionsMonitor<CookieAuthenticationOptions> options) =>
    await TrustedRequestSession.FromRequestAsync(context, new ConfigurationBuilder().Build(), options) is not null
        ? Results.Ok() : Results.Unauthorized());
app.MapGet("/forged-context", async (HttpContext context, TrustedRequestSessionAccessor accessor) =>
{
    // Simulate an in-process caller setting User; the accessor must use the cookie handler.
    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "SuperAdmin")], "synthetic"));
    var session = await accessor.ResolveAsync();
    return session != null && session.TryGetActor(context, out var actor) && actor != null
        ? Results.Text(actor.Role) : Results.Unauthorized();
});
TrustedRequestSession? previousRequestSession = null;
app.MapGet("/capture-trusted-context", async (HttpContext context, TrustedRequestSessionAccessor accessor) =>
{
    var session = await accessor.ResolveAsync();
    if (session == null || !session.TryGetActor(context, out _)) return Results.Unauthorized();
    previousRequestSession = session;
    return Results.Ok();
});
app.MapGet("/reuse-trusted-context", (HttpContext context) =>
    previousRequestSession?.TryGetActor(context, out _) == true ? Results.Ok() : Results.Unauthorized());
app.MapGet("/cancel-trusted-context", async (HttpContext context, TrustedRequestSessionAccessor accessor) =>
{
    var session = await accessor.ResolveAsync();
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    context.RequestAborted = cancelled.Token;
    return session?.TryGetActor(context, out _) == true ? Results.Ok() : Results.Unauthorized();
});
app.MapGet("/forged-matching-context/{role}", async (HttpContext context, TrustedRequestSessionAccessor accessor, string role) =>
{
    if (!store.TryGet(role, out var account)) return Results.Unauthorized();
    context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, account.AccountId.ToString()), new(ClaimTypes.Role, role),
         new(DurableSessionAuthorization.EpochClaim, account.CredentialEpoch!.Value.ToString()),
         new("account_version", account.AccountVersion!), new(DurableSessionAuthorization.SubjectClaim, account.SupabaseSubject!.Value.ToString()),
         new(VersionedSessionAuthorization.SecurityVersionClaim, store.Generation[role].ToString())], "synthetic"));
    return await accessor.ResolveAsync() is not null ? Results.Ok() : Results.Unauthorized();
});
app.MapGet("/synthetic-expired-cookie", async (HttpContext context) =>
{
    if (!store.TryGet("Customer", out var account)) return Results.Unauthorized();
    var claims = new[] { new Claim(ClaimTypes.NameIdentifier, account.AccountId.ToString()), new Claim(ClaimTypes.Role, "Customer"),
        new Claim(DurableSessionAuthorization.EpochClaim, account.CredentialEpoch!.Value.ToString()), new Claim("account_version", account.AccountVersion!),
        new Claim(DurableSessionAuthorization.SubjectClaim, account.SupabaseSubject!.Value.ToString()),
        new Claim(VersionedSessionAuthorization.SecurityVersionClaim, "1") };
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
        new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow.AddMinutes(-2), ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1), AllowRefresh = false });
    return Results.Ok();
});
app.MapGet("/admin-only", () => Results.Ok()).RequireAuthorization(p => p.RequireRole("Admin", "SuperAdmin"));
app.MapGet("/owner-only", () => Results.Ok()).RequireAuthorization(p => p.RequireRole("SuperAdmin"));
app.MapGet("/synthetic-legacy-cookie", async (HttpContext context) =>
{
    var a = store.TryGet("Customer", out var snapshot) ? snapshot : throw new InvalidOperationException();
    var claims = new[] { new Claim(ClaimTypes.NameIdentifier, a.AccountId.ToString()), new Claim(ClaimTypes.Role, "Customer"),
        new Claim(DurableSessionAuthorization.EpochClaim, a.CredentialEpoch!.Value.ToString()), new Claim("account_version", a.AccountVersion!),
        new Claim(DurableSessionAuthorization.SubjectClaim, a.SupabaseSubject!.Value.ToString()) };
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok();
});
app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    try { await antiforgery.ValidateRequestAsync(context); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return Results.Ok();
}).RequireAuthorization();
app.MapPost("/change/{outcome}", async (string outcome, HttpContext context, IAntiforgery antiforgery, DurableCredentialUpdate updater) =>
{
    try { await antiforgery.ValidateRequestAsync(context); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }
    var role = context.User.FindFirstValue(ClaimTypes.Role)!;
    if (!store.TryGet(role, out var snapshot) || !long.TryParse(context.User.FindFirstValue(DurableSessionAuthorization.EpochClaim), out var epoch)) return Results.Unauthorized();
    var result = await updater.Change(snapshot.AccountId, snapshot.SupabaseSubject!.Value, epoch, "synthetic",
        _ => outcome == "timeout" ? throw new TimeoutException("synthetic") : Task.FromResult(CredentialProviderResult.Confirmed));
    return Results.Text(result.ToString());
}).RequireAuthorization();
await app.StartAsync();
try
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    HttpClient Client() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
    var passed = 0;
    void Check(bool condition, string text) { if (!condition) throw new Exception(text); passed++; Console.WriteLine("PASS " + text); }
    Check(!StudioCookieAuthentication.HasDurableValidation(new CookieAuthenticationOptions()), "unconfigured cookie validation is not trusted");
    async Task<HttpResponseMessage> Post(HttpClient client, string path, bool token = true, Dictionary<string, string>? form = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (token) request.Headers.Add("X-CSRF-TOKEN", await client.GetStringAsync("/csrf"));
        if (form != null) request.Content = new FormUrlEncodedContent(form);
        return await client.SendAsync(request);
    }
    foreach (var role in new[] { "Customer", "Admin", "SuperAdmin" })
    {
        var user = accounts[role]; store.Seed(user);
        using var one = Client(); using var two = Client();
        Check((await one.GetAsync("/forged-context")).StatusCode == HttpStatusCode.Unauthorized, role + " forged in-process User cannot create trusted context");
        Check((await one.GetAsync("/forged-matching-context/" + role)).StatusCode == HttpStatusCode.Unauthorized, role + " matching fabricated claims without cookie denied");
        var login = new Dictionary<string, string> { ["email"] = user.Email, ["password"] = "Synthetic123!" };
        Check((await Post(one, "/Account/Login", false, login)).StatusCode == HttpStatusCode.BadRequest, role + " actual MVC login requires antiforgery");
        Check((await Post(one, "/Account/Login", true, login)).StatusCode == HttpStatusCode.Redirect && (await Post(two, "/Account/Login", true, login)).StatusCode == HttpStatusCode.Redirect, role + " actual MVC login issues two cookies");
        Check(await one.GetStringAsync("/protected") == role && await two.GetStringAsync("/protected") == role, role + " actual MVC cookies validated");
        Check((await one.GetAsync("/trusted-context")).StatusCode == HttpStatusCode.OK, role + " validated durable cookie resolves trusted context");
        Check((await one.GetAsync("/capture-trusted-context")).StatusCode == HttpStatusCode.OK &&
              (await one.GetAsync("/reuse-trusted-context")).StatusCode == HttpStatusCode.Unauthorized,
            role + " trusted context cannot be reused in a later request");
        Check((await one.GetAsync("/cancel-trusted-context")).StatusCode == HttpStatusCode.Unauthorized,
            role + " cancelled request cannot use trusted context");
        Check(await one.GetStringAsync("/forged-context") == role, role + " forged User cannot replace the cookie identity");
        Check((await one.GetAsync("/trusted-context-disabled")).StatusCode == HttpStatusCode.Unauthorized, role + " disabled durable setting denies trusted context");
        if (role == "Customer")
        {
            using var expired = Client();
            Check((await expired.GetAsync("/synthetic-expired-cookie")).IsSuccessStatusCode &&
                  (await expired.GetAsync("/trusted-context")).StatusCode == HttpStatusCode.Unauthorized, "expired cookie denies trusted context");
        }
        var profile = new Dictionary<string, string> { ["firstName"] = "Changed", ["lastName"] = "Test", ["age"] = "25", ["gender"] = "Others", ["address"] = "Synthetic address" };
        Check((await Post(one, "/Account/SaveProfile", true, profile)).StatusCode == HttpStatusCode.ServiceUnavailable && user.FirstName == "Synthetic", role + " actual MVC profile write blocked before mutation");
        store.Set(role, SessionCredentialState.Pending, 2);
        Check((await one.GetAsync("/trusted-context")).StatusCode == HttpStatusCode.Unauthorized, role + " pending credential denies trusted context");
        Check((await Post(one, "/Account/SaveProfile", true, profile)).StatusCode == HttpStatusCode.Unauthorized && user.FirstName == "Synthetic", role + " stale cookie cannot mutate profile");
        store.Set(role, SessionCredentialState.Ready, 2);
        Check((await Post(one, "/Account/Login", true, login)).StatusCode == HttpStatusCode.Redirect && (await Post(two, "/Account/Login", true, login)).StatusCode == HttpStatusCode.Redirect, role + " actual MVC refreshed epoch sessions");
        var change = new Dictionary<string, string> { ["currentPassword"] = "Synthetic123!", ["newPassword"] = "SyntheticNew123!", ["confirmPassword"] = "SyntheticNew123!" };
        var beforeChange = clientFactory.UpdateCalls;
        Check((await Post(one, "/Account/ChangePassword", true, change)).StatusCode == HttpStatusCode.Redirect && clientFactory.UpdateCalls == beforeChange + 1
            && store.TryGet(role, out var changedState) && changedState.CredentialEpoch == 3 && changedState.CredentialState == SessionCredentialState.Ready, role + " actual MVC password change completes in fake state");
        Check((await two.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, role + " actual MVC change invalidates second cookie in fake state");
        store.Seed(user);
        var reset = new Dictionary<string, string> { ["email"] = user.Email, ["code"] = "123456", ["newPassword"] = "SyntheticNew123!", ["confirmPassword"] = "SyntheticNew123!" };
        var beforeReset = clientFactory.UpdateCalls;
        Check((await Post(one, "/Account/ResetPassword", true, reset)).StatusCode == HttpStatusCode.Redirect && clientFactory.UpdateCalls == beforeReset + 1
            && store.TryGet(role, out var resetState) && resetState.CredentialEpoch == 2 && resetState.CredentialState == SessionCredentialState.Ready, role + " actual MVC password reset completes in fake state");
        Check((await Post(one, "/Account/Login", true, login)).StatusCode == HttpStatusCode.Redirect, role + " actual MVC login after reset");
        Check((await Post(one, "/Account/Logout")).StatusCode == HttpStatusCode.Redirect && (await one.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, role + " actual MVC logout clears cookie");
    }
    foreach (var scenario in new[] { "timeout", "reject", "sync", "mismatch", "database", "unknown" })
    {
        var user = demo.AddUser("Synthetic", "Customer", $"scenario-{Guid.NewGuid():N}@example.test", "Synthetic123!", "Customer", 25, "Others", "Synthetic address");
        accounts["Customer"] = user; store.Seed(user); clientFactory.Outcome = null;
        using var browser = Client();
        var login = new Dictionary<string, string> { ["email"] = user.Email, ["password"] = "Synthetic123!" };
        Check((await Post(browser, "/Account/Login", true, login)).StatusCode == HttpStatusCode.Redirect, scenario + " actual MVC setup login");
        var before = clientFactory.UpdateCalls;
        if (scenario is "timeout" or "reject" or "mismatch") clientFactory.Outcome = scenario;
        if (scenario == "sync") store.FailComplete = true;
        if (scenario == "database") store.Fail = true;
        if (scenario == "unknown") store.Unknown("Customer");
        var change = new Dictionary<string, string> { ["currentPassword"] = "Synthetic123!", ["newPassword"] = "SyntheticNew123!", ["confirmPassword"] = "SyntheticNew123!" };
        var response = await Post(browser, "/Account/ChangePassword", true, change);
        store.Fail = false; store.FailComplete = false; clientFactory.Outcome = null;
        Check(store.TryGet("Customer", out var status) && (scenario switch {
            "timeout" or "reject" => response.StatusCode == HttpStatusCode.Redirect && status.CredentialState == SessionCredentialState.ReconciliationRequired && clientFactory.UpdateCalls == before + 1,
            "sync" => response.StatusCode == HttpStatusCode.Redirect && status.CredentialState == SessionCredentialState.Pending && clientFactory.UpdateCalls == before + 1,
            "mismatch" => response.StatusCode == HttpStatusCode.Redirect && status.CredentialState == SessionCredentialState.Ready && clientFactory.UpdateCalls == before,
            _ => response.StatusCode == HttpStatusCode.Unauthorized && clientFactory.UpdateCalls == before
        }), scenario + " actual MVC password change fails closed with fake dependencies");
    }
    foreach (var role in new[] { "Customer", "Admin", "SuperAdmin" })
    {
        store.Reset(role);
        using var one = Client(); using var two = Client();
        Check((await one.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, role + " guest denied");
        Check((await Post(one, $"/login/{role}/matched", false)).StatusCode == HttpStatusCode.BadRequest, role + " missing antiforgery denied");
        Check((await Post(one, $"/login/{role}/wrong")).StatusCode == HttpStatusCode.Unauthorized, role + " mismatched fake subject denied");
        Check((await Post(one, $"/login/{role}/matched")).IsSuccessStatusCode && (await Post(two, $"/login/{role}/matched")).IsSuccessStatusCode, role + " two cookie sessions issued");
        Check(await one.GetStringAsync("/protected") == role && await two.GetStringAsync("/protected") == role, role + " current cookies accepted");
        store.Set(role, SessionCredentialState.Pending, 2);
        Check((await one.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized && (await two.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, role + " pending rejects both cookies");
        Check((await Post(one, $"/login/{role}/matched")).StatusCode == HttpStatusCode.Unauthorized, role + " pending denies login");
        store.Set(role, SessionCredentialState.ReconciliationRequired, 2);
        Check((await Post(one, $"/login/{role}/matched")).StatusCode == HttpStatusCode.Unauthorized, role + " reconciliation denies login");
        store.Set(role, SessionCredentialState.Ready, 2);
        Check((await one.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, role + " stale cookie stays rejected");
        Check((await Post(one, $"/login/{role}/matched")).IsSuccessStatusCode && (await one.GetAsync("/protected")).IsSuccessStatusCode, role + " current epoch login works");
        store.Fail = true;
        Check((await one.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, role + " unavailable snapshot fails closed");
        store.Fail = false;
        Check((await Post(one, "/logout", false)).StatusCode == HttpStatusCode.BadRequest, role + " logout antiforgery enforced");
        Check((await Post(one, "/logout")).IsSuccessStatusCode && (await one.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, role + " logout clears cookie");
        store.Disable(role);
        Check((await Post(two, $"/login/{role}/matched")).StatusCode == HttpStatusCode.Unauthorized, role + " disabled login denied");
        store.Unknown(role);
        Check((await Post(two, $"/login/{role}/matched")).StatusCode == HttpStatusCode.Unauthorized, role + " Unknown identity denied");
        store.Reset(role);
        Check((await Post(one, $"/login/{role}/matched")).IsSuccessStatusCode && (await Post(two, $"/login/{role}/matched")).IsSuccessStatusCode, role + " sessions for synthetic update");
        using (var changed = await Post(one, "/change/success"))
            Check(await changed.Content.ReadAsStringAsync() == "Completed", role + " synthetic password update completed");
        Check((await one.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized && (await two.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, role + " update revokes two prior cookies");
        Check((await Post(one, $"/login/{role}/matched")).IsSuccessStatusCode, role + " login after synthetic update");
        store.FailBegin = true;
        using (var failure = await Post(one, "/change/success"))
            Check(await failure.Content.ReadAsStringAsync() == "BeginUnconfirmed", role + " database begin failure never claims update");
        store.FailBegin = false;
        using (var timeout = await Post(one, "/change/timeout"))
            Check(await timeout.Content.ReadAsStringAsync() == "Unconfirmed", role + " synthetic provider timeout unconfirmed");
        Check((await one.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized && (await Post(two, $"/login/{role}/matched")).StatusCode == HttpStatusCode.Unauthorized, role + " timeout reconciliation denies cookie and login");
        store.Reset(role);
        Check((await Post(one, $"/login/{role}/matched")).IsSuccessStatusCode, role + " login for local sync failure");
        store.FailComplete = true;
        using (var failure = await Post(one, "/change/success"))
            Check(await failure.Content.ReadAsStringAsync() == "Unconfirmed", role + " local sync failure is unconfirmed");
        store.FailComplete = false;
        Check((await one.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized && (await Post(two, $"/login/{role}/matched")).StatusCode == HttpStatusCode.Unauthorized,
            role + " local sync failure leaves pending and denies access");
    }
    foreach (var role in new[] { "Customer", "Admin", "SuperAdmin" })
    {
        var user = demo.AddUser("Synthetic", role, $"versioned-{role.ToLowerInvariant()}@example.test", "Synthetic123!", role, 25, "Others", "Synthetic address");
        accounts[role] = user;
        store.Seed(user);
        using var browser = Client();
        var login = new Dictionary<string, string> { ["email"] = user.Email, ["password"] = "Synthetic123!" };
        async Task Login() { Check((await Post(browser, "/Account/Login", true, login)).StatusCode == HttpStatusCode.Redirect && (await browser.GetAsync("/protected")).IsSuccessStatusCode, role + " versioned MVC login"); }
        await Login();
        Check(((await browser.GetAsync("/admin-only")).StatusCode == HttpStatusCode.OK) == (role != "Customer") &&
              ((await browser.GetAsync("/owner-only")).StatusCode == HttpStatusCode.OK) == (role == "SuperAdmin"), role + " role authorization preserved");
        store.Generation[role]++;
        Check((await browser.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, role + " generation revocation invalidates cookie");
        Check((await browser.GetAsync("/trusted-context")).StatusCode == HttpStatusCode.Unauthorized, role + " revoked generation denies trusted context");
        store.Seed(user); await Login();
        store.Mutate(role, a => a with { IsLocked = true });
        Check((await browser.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, role + " locked invalidates cookie");
        store.Seed(user); await Login();
        store.Mutate(role, a => a with { IsActive = false });
        Check((await browser.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, role + " disabled invalidates cookie");
        store.Seed(user); await Login();
        store.Mutate(role, a => a with { Role = role == "Customer" ? "Admin" : "Customer" });
        Check((await browser.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, role + " role change invalidates cookie");
        Check((await browser.GetAsync("/trusted-context")).StatusCode == HttpStatusCode.Unauthorized, role + " changed role denies trusted context");
        store.Seed(user); await Login();
        store.Mutate(role, a => a with { IdentityMappingConsistent = false });
        Check((await browser.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, role + " inconsistent identity invalidates cookie");
        store.Seed(user); await Login();
        store.Mutate(role, a => a with { SupabaseSubject = null });
        Check((await browser.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, role + " missing identity invalidates cookie");
        store.Seed(user); await Login();
        store.Hash[role] = "changed synthetic hash";
        Check((await browser.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, role + " changed hash invalidates cookie");
        store.Seed(user); await Login();
        store.Fail = true;
        Check((await browser.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, role + " PostgreSQL query failure denies cookie");
        store.Fail = false;
    }
    foreach (var role in new[] { "Customer", "Admin", "SuperAdmin" })
    {
        var user = demo.AddUser("Synthetic", role, $"disable-{role.ToLowerInvariant()}@example.test", "Synthetic123!", role, 25, "Others", "Synthetic address");
        accounts[role] = user;
        store.Seed(user);
    }
    using (var ownerBrowser = Client())
    using (var otherOwner = Client())
    using (var adminBrowser = Client())
    using (var customerBrowser = Client())
    {
        async Task LoginAs(HttpClient browser, string role)
        {
            var user = accounts[role];
            Check((await Post(browser, "/Account/Login", true, new() { ["email"] = user.Email, ["password"] = "Synthetic123!" })).StatusCode == HttpStatusCode.Redirect,
                role + " disable test login");
        }
        Dictionary<string, string> Disable(Guid id, long generation = 1) => new() { ["id"] = id.ToString(), ["expectedGeneration"] = generation.ToString() };
        await LoginAs(ownerBrowser, "SuperAdmin");
        await LoginAs(otherOwner, "SuperAdmin");
        await LoginAs(adminBrowser, "Admin");
        await LoginAs(customerBrowser, "Customer");
        var adminId = accounts["Admin"].Id;
        using (var response = await ownerBrowser.GetAsync("/Admin/AdminRoster"))
        {
            var json = await response.Content.ReadAsStringAsync();
            Check(response.StatusCode == HttpStatusCode.OK && json.Contains(adminId.ToString()) &&
                  json.Contains("\"securityGeneration\":1") && !json.Contains("password", StringComparison.OrdinalIgnoreCase) &&
                  response.Headers.CacheControl?.NoStore == true, "SuperAdmin receives no-store safe roster");
        }
        Check((await adminBrowser.GetAsync("/Admin/AdminRoster")).StatusCode == HttpStatusCode.Forbidden &&
              (await customerBrowser.GetAsync("/Admin/AdminRoster")).StatusCode == HttpStatusCode.Forbidden,
            "roster denies non-SuperAdmin roles");
        Check((await ownerBrowser.GetAsync("/Admin/Users")).Headers.Location?.OriginalString == "/Admin/ManageAccounts",
            "candidate management navigation opens authoritative roster");
        using (var page = await ownerBrowser.GetAsync("/Admin/ManageAccounts"))
        {
            var html = await page.Content.ReadAsStringAsync();
            Check(page.StatusCode == HttpStatusCode.OK && html.Contains(adminId.ToString()) &&
                  html.Contains("name=\"expectedGeneration\" value=\"1\"") && html.Contains("__RequestVerificationToken") &&
                  html.Contains("Active") && html.Contains("Not locked") && html.Contains("Generation: 1") &&
                  !html.Contains("password_hash", StringComparison.OrdinalIgnoreCase),
                "management page renders safe status, generation and antiforgery form");
        }
        Check((await adminBrowser.GetAsync("/Admin/ManageAccounts")).StatusCode == HttpStatusCode.Forbidden &&
              (await customerBrowser.GetAsync("/Admin/ManageAccounts")).StatusCode == HttpStatusCode.Forbidden,
            "management page restricted to SuperAdmin");
        Check((await Post(adminBrowser, "/Admin/DisableAdminFromRoster", true, Disable(adminId))).StatusCode == HttpStatusCode.Forbidden,
            "management form rejects non-SuperAdmin");
        Check((await Post(ownerBrowser, "/Admin/DisableAdminFromRoster", false, Disable(adminId))).StatusCode == HttpStatusCode.BadRequest,
            "management form requires antiforgery");
        var originalAdmin = store.Find(adminId)!.Value.Snapshot;
        store.Generation["Admin"] = 2;
        var staleResponse = await Post(ownerBrowser, "/Admin/DisableAdminFromRoster", true, Disable(adminId));
        Check(staleResponse.StatusCode == HttpStatusCode.Redirect && staleResponse.Headers.Location?.OriginalString == "/Admin/ManageAccounts" &&
              changes.Commits == 0, "stale form redirects without retry or commit");
        using (var refreshed = await ownerBrowser.GetAsync("/Admin/ManageAccounts"))
        {
            var html = await refreshed.Content.ReadAsStringAsync();
            Check(refreshed.StatusCode == HttpStatusCode.OK && html.Contains("change could not be confirmed", StringComparison.OrdinalIgnoreCase) &&
                  html.Contains("name=\"expectedGeneration\" value=\"2\"") && changes.Commits == 0,
                "stale conflict reloads current generation");
        }
        rosterReads.Fail = true;
        Check((await Post(ownerBrowser, "/Admin/DisableAdminFromRoster", true, Disable(adminId))).StatusCode == HttpStatusCode.Redirect &&
              (await ownerBrowser.GetAsync("/Admin/ManageAccounts")).StatusCode == HttpStatusCode.ServiceUnavailable && changes.Commits == 0,
            "failed refresh exposes no stale roster or automatic retry");
        rosterReads.Fail = false;
        changes.FailAfterCommit = true;
        var unknownResponse = await Post(ownerBrowser, "/Admin/DisableAdminFromRoster", true, Disable(adminId, 2));
        Check(unknownResponse.StatusCode == HttpStatusCode.Redirect && changes.Commits == 1,
            "unknown commit redirects without retry");
        changes.FailAfterCommit = false;
        using (var refreshed = await ownerBrowser.GetAsync("/Admin/ManageAccounts"))
        {
            var html = await refreshed.Content.ReadAsStringAsync();
            Check(refreshed.StatusCode == HttpStatusCode.OK && html.Contains("change could not be confirmed", StringComparison.OrdinalIgnoreCase) &&
                  html.Contains("Disabled") && html.Contains("Generation: 3") && !html.Contains("Confirm disable") && changes.Commits == 1,
                "unknown outcome reloads status without claiming rollback");
        }
        store.Replace("Admin", new(originalAdmin, 1));
        changes.Commits = 0;
        var successResponse = await Post(ownerBrowser, "/Admin/DisableAdminFromRoster", true, Disable(adminId));
        Check(successResponse.StatusCode == HttpStatusCode.Redirect && changes.Commits == 1,
            "management form commits disable once");
        using (var refreshed = await ownerBrowser.GetAsync("/Admin/ManageAccounts"))
        {
            var html = await refreshed.Content.ReadAsStringAsync();
            Check(refreshed.StatusCode == HttpStatusCode.OK && html.Contains("disable request completed", StringComparison.OrdinalIgnoreCase) &&
                  html.Contains("Disabled") && html.Contains("Generation: 2") && !html.Contains("Confirm disable"),
                "confirmed disable reloads authoritative status");
        }
        store.Replace("Admin", new(originalAdmin, 1));
        changes.Commits = 0;
        rosterReads.DuplicateGeneration = true;
        Check((await ownerBrowser.GetAsync("/Admin/AdminRoster")).StatusCode == HttpStatusCode.ServiceUnavailable,
            "roster fails closed on ambiguous generation");
        rosterReads.DuplicateGeneration = false;
        rosterReads.Fail = true;
        Check((await ownerBrowser.GetAsync("/Admin/AdminRoster")).StatusCode == HttpStatusCode.ServiceUnavailable,
            "roster fails closed on database error");
        rosterReads.Fail = false;
        rosterReads.OnOpen = () => store.Generation["SuperAdmin"]++;
        Check((await ownerBrowser.GetAsync("/Admin/AdminRoster")).StatusCode == HttpStatusCode.ServiceUnavailable,
            "roster rechecks revocation after cookie middleware");
        rosterReads.OnOpen = null;
        store.Generation["SuperAdmin"] = 1;
        rosterReads.OnOpen = () => store.Generation["SuperAdmin"]++;
        Check((await ownerBrowser.GetAsync("/Admin/ManageAccounts")).StatusCode == HttpStatusCode.ServiceUnavailable,
            "management page rechecks actor after cookie middleware");
        rosterReads.OnOpen = null;
        store.Generation["SuperAdmin"] = 1;
        Check((await Post(ownerBrowser, "/Admin/DisableAdmin", false, Disable(adminId))).StatusCode == HttpStatusCode.BadRequest,
            "disable requires antiforgery");
        Check((await Post(adminBrowser, "/Admin/DisableAdmin", true, Disable(adminId))).StatusCode == HttpStatusCode.Forbidden &&
              (await Post(customerBrowser, "/Admin/DisableAdmin", true, Disable(adminId))).StatusCode == HttpStatusCode.Forbidden,
            "non-SuperAdmin roles forbidden");
        Check((await Post(ownerBrowser, "/Admin/DisableAdmin", true, Disable(accounts["SuperAdmin"].Id))).StatusCode == HttpStatusCode.Conflict,
            "SuperAdmin self target denied");
        Check((await Post(ownerBrowser, "/Admin/DisableAdmin", true, Disable(accounts["Customer"].Id))).StatusCode == HttpStatusCode.Conflict,
            "non-Admin target denied");
        builder.Configuration["Authentication:DurableSessionEnforcement"] = "false";
        Check((await Post(ownerBrowser, "/Admin/DisableAdmin", true, Disable(adminId))).StatusCode == HttpStatusCode.NotFound,
            "disable candidate unavailable outside durable mode");
        Check((await ownerBrowser.GetAsync("/Admin/AdminRoster")).StatusCode == HttpStatusCode.NotFound,
            "roster unavailable outside durable mode");
        Check((await ownerBrowser.GetAsync("/Admin/ManageAccounts")).StatusCode == HttpStatusCode.NotFound &&
              (await Post(ownerBrowser, "/Admin/DisableAdminFromRoster", true, Disable(adminId))).StatusCode == HttpStatusCode.NotFound,
            "management workflow unavailable outside durable mode");
        builder.Configuration["Authentication:DurableSessionEnforcement"] = "true";
        adminStore.UseDemo = true;
        Check((await Post(ownerBrowser, "/Admin/DisableAdmin", true, Disable(adminId))).StatusCode == HttpStatusCode.NotFound,
            "disable has no Demo fallback");
        Check((await ownerBrowser.GetAsync("/Admin/AdminRoster")).StatusCode == HttpStatusCode.NotFound,
            "roster has no Demo fallback");
        Check((await ownerBrowser.GetAsync("/Admin/ManageAccounts")).StatusCode == HttpStatusCode.NotFound &&
              (await Post(ownerBrowser, "/Admin/DisableAdminFromRoster", true, Disable(adminId))).StatusCode == HttpStatusCode.NotFound,
            "management workflow has no Demo fallback");
        adminStore.UseDemo = false;
        changes.FailApply = true;
        Check((await Post(ownerBrowser, "/Admin/DisableAdmin", true, Disable(adminId))).StatusCode == HttpStatusCode.Conflict &&
              store.TryGet("Admin", out var failed) && failed.IsActive == true && store.Generation["Admin"] == 1,
            "failed update rolls back generation");
        changes.FailApply = false;
        changes.FailCommit = true;
        Check((await Post(ownerBrowser, "/Admin/DisableAdmin", true, Disable(adminId))).StatusCode == HttpStatusCode.Conflict &&
              store.TryGet("Admin", out failed) && failed.IsActive == true && store.Generation["Admin"] == 1,
            "database failure rolls back both changes");
        changes.FailCommit = false;
        using var first = new HttpRequestMessage(HttpMethod.Post, "/Admin/DisableAdmin") { Content = new FormUrlEncodedContent(Disable(adminId)) };
        using var second = new HttpRequestMessage(HttpMethod.Post, "/Admin/DisableAdmin") { Content = new FormUrlEncodedContent(Disable(adminId)) };
        first.Headers.Add("X-CSRF-TOKEN", await ownerBrowser.GetStringAsync("/csrf"));
        second.Headers.Add("X-CSRF-TOKEN", await otherOwner.GetStringAsync("/csrf"));
        var concurrent = await Task.WhenAll(ownerBrowser.SendAsync(first), otherOwner.SendAsync(second));
        Check(concurrent.Count(r => r.StatusCode == HttpStatusCode.NoContent) == 1 &&
              concurrent.Count(r => r.StatusCode == HttpStatusCode.Conflict) == 1 &&
              store.Generation["Admin"] == 2 && store.TryGet("Admin", out var disabled) && disabled.IsActive == false,
            "concurrent disables commit exactly once");
        Check((await adminBrowser.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized,
            "disabled Admin cookie rejected on next request");
        Check((await Post(ownerBrowser, "/Admin/DisableAdmin", true, Disable(adminId))).StatusCode == HttpStatusCode.Conflict,
            "stale target generation rejected");
        store.Seed(accounts["Admin"]);
        changes.FailAfterCommit = true;
        Check((await Post(ownerBrowser, "/Admin/DisableAdmin", true, Disable(adminId))).StatusCode == HttpStatusCode.Conflict &&
              store.Generation["Admin"] == 2 && store.TryGet("Admin", out var unknown) && unknown.IsActive == false,
            "lost commit acknowledgement never reports success");
        changes.FailAfterCommit = false;
        store.Seed(accounts["Admin"]);
        store.Generation["SuperAdmin"]++;
        Check((await Post(ownerBrowser, "/Admin/DisableAdmin", true, Disable(adminId))).StatusCode == HttpStatusCode.Unauthorized &&
              store.Generation["Admin"] == 1, "revoked SuperAdmin session cannot disable");
        Check((await Post(ownerBrowser, "/Admin/DisableAdminFromRoster", true, Disable(adminId))).StatusCode == HttpStatusCode.Unauthorized &&
              store.Generation["Admin"] == 1, "revoked SuperAdmin cannot submit management form");
    }
    var outageUser = demo.AddUser("Synthetic", "Outage", "versioned-outage@example.test", "Synthetic123!", "Customer", 25, "Others", "Synthetic address");
    accounts["Customer"] = outageUser;
    store.Seed(outageUser);
    using (var browser = Client())
    {
        Check((await browser.GetAsync("/synthetic-legacy-cookie")).IsSuccessStatusCode &&
              (await browser.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, "cookie without generation denied");
        store.Fail = true;
        var response = await Post(browser, "/Account/Login", true, new() { ["email"] = outageUser.Email, ["password"] = "Synthetic123!" });
        Check(response.StatusCode != HttpStatusCode.Redirect && (await browser.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized,
            "database outage prevents durable MVC login");
        store.Fail = false;
    }
    store.Seed(accounts["Customer"]);
    using (var browser = Client())
    {
        var user = accounts["Customer"];
        var login = new Dictionary<string, string> { ["email"] = user.Email, ["password"] = "Synthetic123!" };
        Check((await Post(browser, "/Account/Login", true, login)).StatusCode == HttpStatusCode.Redirect &&
              (await browser.GetAsync("/protected")).IsSuccessStatusCode, "persistent test store accepts durable session");
        demo.TestPersistent = false;
        Check((await browser.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized, "durable cookie never falls back to in-memory store");
        var result = await Post(browser, "/Account/Login", true, login);
        Check(result.StatusCode != HttpStatusCode.Redirect && (await browser.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized,
            "durable login never falls back to in-memory store");
    }
    Console.WriteLine($"{passed} isolated HTTP checks passed; fake query/provider, no real database or normal application startup.");
}
finally { await app.StopAsync(); await app.DisposeAsync(); }

sealed class FakeSnapshots : IAccountSecuritySnapshotProvider, IDurableCredentialStore
{
    private readonly Dictionary<string, AccountSecuritySnapshot> accounts = new();
    public Dictionary<string, long> Generation { get; } = new();
    public Dictionary<string, string> Hash { get; } = new();
    public bool Fail { get; set; }
    public bool FailBegin { get; set; }
    public bool FailComplete { get; set; }
    public void Reset(string role)
    {
        var id = Guid.NewGuid();
        accounts[role] = new(id, true, false, role, SessionStoreKind.PublicPostgres, SessionAuthenticationKind.Supabase, 1, id, id,
            role == "Customer" ? SessionIdentityOwnerKind.Customer : SessionIdentityOwnerKind.Admin, true,
            AuthenticationIdentityPolicy.Supabase, Guid.NewGuid(), SessionCredentialState.Ready, 1, AccountSession.Version("synthetic"));
        Generation[role] = 1; Hash[role] = "synthetic";
    }
    public bool TryGet(string role, out AccountSecuritySnapshot snapshot) => accounts.TryGetValue(role, out snapshot!);
    public void Seed(AppUser user)
    {
        accounts[user.Role] = new(user.Id, user.IsActive, false, user.Role, SessionStoreKind.PublicPostgres, SessionAuthenticationKind.Supabase, 1, user.Id, user.Id,
            user.Role == "Customer" ? SessionIdentityOwnerKind.Customer : SessionIdentityOwnerKind.Admin, true, AuthenticationIdentityPolicy.Supabase,
            Guid.NewGuid(), SessionCredentialState.Ready, 1, AccountSession.Version(user.PasswordHash));
        Generation[user.Role] = 1; Hash[user.Role] = user.PasswordHash;
    }
    public void Mutate(string role, Func<AccountSecuritySnapshot, AccountSecuritySnapshot> update) => accounts[role] = update(accounts[role]);
    public (string Role, AccountSecuritySnapshot Snapshot)? Find(Guid id)
    {
        foreach (var entry in accounts) if (entry.Value.AccountId == id) return (entry.Key, entry.Value);
        return null;
    }
    public void Replace(string role, VersionedAccountSecurity value)
    {
        accounts[role] = value.Account;
        Generation[role] = value.SecurityVersion;
    }
    public Dictionary<string, VersionedAccountSecurity> CopyVersioned() =>
        accounts.ToDictionary(x => x.Key, x => new VersionedAccountSecurity(x.Value, Generation[x.Key]));
    public void Set(string role, SessionCredentialState state, long epoch) => accounts[role] = accounts[role] with { CredentialState = state, CredentialEpoch = epoch };
    public void Disable(string role) => accounts[role] = accounts[role] with { IsActive = false };
    public void Unknown(string role) => accounts[role] = accounts[role] with { IsActive = true, AuthState = AuthenticationIdentityPolicy.Unknown };
    public Task<AccountSecuritySnapshot?> LoadAsync(Guid accountId, CancellationToken ct) => Fail
        ? throw new IOException("synthetic database outage")
        : Task.FromResult(accounts.Values.SingleOrDefault(x => x.AccountId == accountId));
    public Task<long?> Begin(Guid id, Guid subject, long epoch, CancellationToken ct)
    {
        if (FailBegin) throw new IOException("synthetic begin failure");
        var entry = accounts.Single(x => x.Value.AccountId == id);
        if (entry.Value.SupabaseSubject != subject || entry.Value.CredentialEpoch != epoch || entry.Value.CredentialState != SessionCredentialState.Ready) return Task.FromResult<long?>(null);
        accounts[entry.Key] = entry.Value with { CredentialEpoch = epoch + 1, CredentialState = SessionCredentialState.Pending };
        return Task.FromResult<long?>(epoch + 1);
    }
    public Task<bool> Complete(Guid id, Guid subject, long epoch, string password, CancellationToken ct) => FailComplete ? throw new IOException("synthetic sync failure") : Transition(id, subject, epoch, SessionCredentialState.Ready);
    public Task<bool> Reject(Guid id, Guid subject, long epoch, CancellationToken ct) => Transition(id, subject, epoch, SessionCredentialState.Ready);
    public Task<bool> MarkReconciliation(Guid id, Guid subject, long epoch, CancellationToken ct) => Transition(id, subject, epoch, SessionCredentialState.ReconciliationRequired);
    private Task<bool> Transition(Guid id, Guid subject, long epoch, SessionCredentialState state)
    {
        var entry = accounts.Single(x => x.Value.AccountId == id);
        if (entry.Value.SupabaseSubject != subject || entry.Value.CredentialEpoch != epoch || entry.Value.CredentialState != SessionCredentialState.Pending) return Task.FromResult(false);
        accounts[entry.Key] = entry.Value with { CredentialState = state };
        return Task.FromResult(true);
    }
}

sealed class FakePersistentStore : DemoStore, IStudioStore
{
    public bool TestPersistent { get; set; } = true;
    bool IStudioStore.IsPersistent => TestPersistent;
}

sealed class AdminStoreChoice(DemoStore demo)
{
    public bool UseDemo { get; set; }
    public IStudioStore Store => UseDemo ? demo : new PublicStudioStore(null!, null!, null!, null!, null!);
}

sealed class FakeRosterReads(FakeSnapshots store) : IAdminRosterReadSessionFactory
{
    public bool Fail { get; set; }
    public bool DuplicateGeneration { get; set; }
    public Action? OnOpen { get; set; }
    public Task<IAdminRosterReadSession> OpenAsync(CancellationToken ct)
    {
        if (Fail) throw new IOException("synthetic");
        OnOpen?.Invoke();
        return Task.FromResult<IAdminRosterReadSession>(new Session(store.CopyVersioned(), DuplicateGeneration));
    }
    private sealed class Session(Dictionary<string, VersionedAccountSecurity> snapshot, bool duplicate) : IAdminRosterReadSession
    {
        public Task<VersionedAccountSecurity?> LoadSecurityAsync(Guid id, CancellationToken ct)
        {
            var result = snapshot.Values.SingleOrDefault(x => x.Account.AccountId == id);
            if (duplicate && result?.Account.Role == "Admin")
                throw new PostgresSecurityReadException(PostgresSecurityReadFailure.InvalidResult);
            return Task.FromResult(result);
        }
        public Task<IReadOnlyList<AdminRosterCandidate>> ReadTargetsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AdminRosterCandidate>>(snapshot.Where(x => x.Value.Account.Role == "Admin")
                .Select(x => new AdminRosterCandidate(x.Value.Account.AccountId, "synthetic-admin", "admin@example.invalid")).ToArray());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

sealed class FakeAdminTransactions(FakeSnapshots store) : IAccountAuthorizationTransactionFactory
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public bool FailApply { get; set; }
    public bool FailCommit { get; set; }
    public bool FailAfterCommit { get; set; }
    public int Commits { get; set; }
    public async Task<IAccountAuthorizationTransaction> BeginAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        return new Transaction(this, store);
    }
    private sealed class Transaction(FakeAdminTransactions factory, FakeSnapshots store) : IAccountAuthorizationTransaction
    {
        private string? targetRole;
        private VersionedAccountSecurity? staged;
        public Task<(VersionedAccountSecurity? Actor, VersionedAccountSecurity? Target)> LockAccountsAsync(Guid actor, Guid target, CancellationToken ct)
        {
            var source = store.Find(actor);
            var targetEntry = store.Find(target);
            targetRole = targetEntry?.Role;
            staged = targetEntry == null ? null : new(targetEntry.Value.Snapshot, store.Generation[targetEntry.Value.Role]);
            return Task.FromResult<(VersionedAccountSecurity?, VersionedAccountSecurity?)>(
                (source == null ? null : new(source.Value.Snapshot, store.Generation[source.Value.Role]), staged));
        }
        public Task<long?> AdvanceAsync(Guid id, long expected, CancellationToken ct)
        {
            if (staged?.Account.AccountId != id || staged.SecurityVersion != expected) return Task.FromResult<long?>(null);
            staged = staged with { SecurityVersion = expected + 1 };
            return Task.FromResult<long?>(staged.SecurityVersion);
        }
        public Task<bool> ApplyAsync(Guid id, AccountAuthorizationAction action, DateTimeOffset? until, CancellationToken ct)
        {
            if (factory.FailApply || staged?.Account.AccountId != id || action != AccountAuthorizationAction.Disable) return Task.FromResult(false);
            staged = staged with { Account = staged.Account with { IsActive = false } };
            return Task.FromResult(true);
        }
        public Task CommitAsync(CancellationToken ct)
        {
            if (factory.FailCommit) throw new IOException("synthetic");
            store.Replace(targetRole!, staged!);
            factory.Commits++;
            if (factory.FailAfterCommit) throw new IOException("synthetic unknown commit");
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { factory.gate.Release(); return ValueTask.CompletedTask; }
    }
}

sealed class FakeSecurityQuery(FakeSnapshots store) : IPostgresAccountSecurityQuery
{
    public Task<string> ExecuteAsync(string sql, Guid id, CancellationToken ct)
    {
        if (store.Fail) throw new IOException("synthetic");
        var entry = store.Find(id);
        if (entry == null) return Task.FromResult("{\"metadata_available\":true,\"accounts\":[],\"identities\":[],\"login_integrity\":false,\"subject_unique\":false,\"generations\":[]}");
        var (role, a) = entry.Value;
        var customer = a.IdentityOwnerKind == SessionIdentityOwnerKind.Customer;
        return Task.FromResult(JsonSerializer.Serialize(new {
            metadata_available = true,
            accounts = new[] { new { id = a.AccountId, kind = customer ? "Customer" : "Admin", role = a.Role,
                active = a.IsActive, locked = a.IsLocked, password_hash = store.Hash[role] } },
            identities = new[] { new { id = a.IdentityId, customer = customer ? a.IdentityOwnerAccountId : null,
                admin = customer ? null : a.IdentityOwnerAccountId, auth_state = a.AuthState, subject = a.SupabaseSubject,
                state = a.CredentialState.ToString(), epoch = a.CredentialEpoch } },
            login_integrity = a.IdentityMappingConsistent, subject_unique = true, generations = new[] { store.Generation[role] }
        }));
    }
}

sealed class FakeProviderFactory(Dictionary<string, AppUser> accounts, FakeSnapshots snapshots) : IHttpClientFactory
{
    private readonly Handler handler = new(accounts, snapshots);
    public int UpdateCalls => handler.UpdateCalls;
    public string? Outcome { get => handler.Outcome; set => handler.Outcome = value; }
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    private sealed class Handler(Dictionary<string, AppUser> accounts, FakeSnapshots snapshots) : HttpMessageHandler
    {
        public int UpdateCalls { get; private set; }
        public string? Outcome { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.Host != "auth.example.test") throw new InvalidOperationException("External provider is forbidden.");
            if (request.Method == HttpMethod.Put && request.RequestUri.AbsolutePath == "/auth/v1/user")
            {
                UpdateCalls++;
                if (Outcome == "timeout") throw new TaskCanceledException("synthetic timeout");
                if (Outcome == "reject") return new HttpResponseMessage(HttpStatusCode.BadRequest);
                var subject = request.Headers.Authorization?.Parameter;
                var account = accounts.Values.Single(x => snapshots.TryGet(x.Role, out var s) && s.SupabaseSubject.ToString() == subject);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id = subject, email = account.Email })) };
            }
            if (request.Method != HttpMethod.Post || request.RequestUri.AbsolutePath is not ("/auth/v1/token" or "/auth/v1/verify"))
                throw new InvalidOperationException("Unexpected synthetic provider request.");
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var email = document.RootElement.GetProperty("email").GetString();
            var user = accounts.Values.Single(x => x.Email == email);
            if (!snapshots.TryGet(user.Role, out var snapshot)) throw new InvalidOperationException("Missing synthetic identity.");
            var body = JsonSerializer.Serialize(new { access_token = snapshot.SupabaseSubject!.Value.ToString(), user = new { id = Outcome == "mismatch" ? Guid.NewGuid() : snapshot.SupabaseSubject!.Value, email = user.Email, email_confirmed_at = "2026-01-01T00:00:00Z" } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}

sealed class AccountOnlyActivator : IControllerActivator
{
    public object Create(ControllerContext context) => context.ActionDescriptor.ControllerTypeInfo.AsType() switch
    {
        var type when type == typeof(AccountController) => context.HttpContext.RequestServices.GetRequiredService<AccountController>(),
        var type when type == typeof(AdminController) => context.HttpContext.RequestServices.GetRequiredService<AdminController>(),
        _ => throw new InvalidOperationException("Only synthetic controllers are available.")
    };
    public void Release(ControllerContext context, object controller) { }
}
