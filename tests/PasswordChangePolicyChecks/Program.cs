using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using GlamByMariya.Web.Controllers;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

// Directly invokes the actual controller and application authentication services.
// No application host, socket handler, database, persistent cache, or external
// configuration is present. Every provider request terminates in FakeAuthHandler.
var passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL (actual ChangePassword controller): " + label);
    passed++;
    Console.WriteLine("PASS (actual ChangePassword controller): " + label);
}

var invalid = new (string Label, string Password, string? Confirmation)[]
{
    ("129 UTF-16 units", new string('a', 128) + "1", new string('a', 128) + "1"),
    ("no ASCII letter", new string('1', 8), new string('1', 8)),
    ("no ASCII digit", new string('a', 8), new string('a', 8)),
    ("missing confirmation", "Abcd1234", null),
    ("empty confirmation", "Abcd1234", ""),
    ("mismatched confirmation", "Abcd1234", "Abcd1235"),
    ("case-mismatched confirmation", "Abcd1234", "abcd1234")
};
foreach (var sample in invalid)
{
    using var fixture = new ChangeFixture(remote: true);
    var originalHash = fixture.User.PasswordHash;
    var result = await fixture.Change(sample.Password, sample.Confirmation);
    Check(result is RedirectToActionResult { ActionName: "Settings" }
        && fixture.Controller.TempData["Error"] is string, sample.Label + " rejected");
    Check(fixture.Http.SignInCalls == 0 && fixture.Http.UpdateCalls == 0 && fixture.Store.LinkCalls == 0,
        sample.Label + ": no provider authentication, identity link, or password update");
    Check(fixture.User.PasswordHash == originalHash && fixture.Auth.SignOutCalls == 0,
        sample.Label + ": no local password mutation or sign-out");
}

foreach (var sample in new[]
{
    (Label: "valid 8-unit password", Password: "Abcd1234"),
    (Label: "valid 128-unit password", Password: new string('a', 126) + "A1"),
    (Label: "Unicode and symbols allowed with ASCII composition", Password: "Ab1é中!xyz")
})
{
    using var fixture = new ChangeFixture(remote: true);
    var result = await fixture.Change(sample.Password, sample.Password);
    Check(result is RedirectToActionResult { ActionName: "Login" }
        && fixture.Controller.TempData["Success"] is string, sample.Label + " succeeds");
    Check(fixture.Http.SignInCalls == 1 && fixture.Http.LastCurrentPasswordMatched
        && fixture.Http.UpdateCalls == 1 && fixture.Store.LinkCalls == 1,
        sample.Label + ": existing password is passed unchanged and provider update follows identity link");
    Check(fixture.UsesPassword(sample.Password) && fixture.Auth.SignOutCalls == 1,
        sample.Label + ": local synthetic hash updated and successful change signs out");
}

// Local-only branch: real PasswordHasher verification remains the existing check.
using (var fixture = new ChangeFixture(remote: false))
{
    var originalHash = fixture.User.PasswordHash;
    var denied = await fixture.Change("NewPass123!", "NewPass123!", "incorrect-current");
    Check(denied is RedirectToActionResult { ActionName: "Settings" }
        && (string?)fixture.Controller.TempData["Error"] == "Current password is incorrect.",
        "local current-password verification still rejects an incorrect existing password");
    Check(fixture.User.PasswordHash == originalHash && fixture.Auth.SignOutCalls == 0,
        "incorrect current password causes no mutation or sign-out");
    var accepted = await fixture.Change("NewPass123!", "NewPass123!");
    Check(accepted is RedirectToActionResult { ActionName: "Login" } && fixture.UsesPassword("NewPass123!")
        && fixture.Auth.SignOutCalls == 1, "local current-password verification and successful update remain functional");
    Check(fixture.Http.SignInCalls == 0 && fixture.Http.UpdateCalls == 0,
        "local-only change makes no provider calls");
}

// Supabase accepted the remote update, then local sync fails. Exercise actual
// coordinator/controller behavior while the coordinator itself remains unchanged.
using (var fixture = new ChangeFixture(remote: true))
{
    fixture.Http.FailLocalSyncAfterUpdate = true;
    var originalHash = fixture.User.PasswordHash;
    var result = await fixture.Change("Abcd1234", "Abcd1234");
    Check(result is RedirectToActionResult { ActionName: "Login" }
        && fixture.Http.SignInCalls == 1 && fixture.Http.UpdateCalls == 1 && fixture.Store.LinkCalls == 1,
        "Phase 1.6 partial failure occurs after proof of current password, identity link and remote update");
    Check(fixture.Auth.SignOutCalls == 1 && fixture.Auth.LastSignOutScheme == CookieAuthenticationDefaults.AuthenticationScheme,
        "Phase 1.6 partial failure signs out the current cookie scheme");
    Check(fixture.User.PasswordHash == originalHash && fixture.Controller.TempData["Error"] is string error
        && error.Contains("Supabase may have accepted the new password", StringComparison.Ordinal)
        && fixture.Controller.TempData["Success"] == null,
        "Phase 1.6 partial failure preserves old local hash and reports possible remote success, not completion");
}

foreach (var role in new[] { "Customer", "Admin", "SuperAdmin" })
{
    using var fixture = new ChangeFixture(remote: true, role: role);
    var result = await fixture.Change("Abcd1234", "Abcd1234");
    Check(role == "Customer" ? result is RedirectToActionResult { ActionName: "Login" } && fixture.UsesPassword("Abcd1234")
        : result is RedirectToActionResult { ActionName: "Settings" } && fixture.Http.UpdateCalls == 0,
        role + ": only a bound Customer can change through the Demo provider path");
}
foreach (var role in new[] { "Customer", "Admin", "SuperAdmin" })
{
    using var fixture = new ChangeFixture(remote: true, role: role, durable: true);
    var result = await fixture.Controller.Login(ChangeFixture.Email, ChangeFixture.ExistingPassword);
    Check(result is RedirectResult && fixture.Auth.LastPrincipal?.FindFirstValue(DurableSessionAuthorization.EpochClaim) == "1"
        && fixture.Auth.LastPrincipal?.FindFirstValue(VersionedSessionAuthorization.SecurityVersionClaim) == "1"
        && fixture.Auth.LastPrincipal?.IsInRole(role) == true && fixture.Store.LinkCalls == 0,
        role + ": durable MVC login issues epoch-bearing ticket without relinking");
}
foreach (var role in new[] { "Customer", "Admin", "SuperAdmin" })
{
    using var fixture = new ChangeFixture(remote: true, role: role, durable: true, persistentStore: false);
    var result = await fixture.Controller.Login(ChangeFixture.Email, ChangeFixture.ExistingPassword);
    Check(result is ViewResult && fixture.Auth.LastPrincipal == null && fixture.Http.SignInCalls == 0,
        role + ": durable MVC login fails closed on a non-persistent store before contacting the provider");
}
foreach (var scenario in new[] { "unknown", "mismatch", "database", "disabled" })
{
    using var fixture = new ChangeFixture(remote: true, durable: true);
    if (scenario == "unknown") fixture.Durable.AuthState = AuthenticationIdentityPolicy.Unknown;
    if (scenario == "mismatch") fixture.Http.IdentityMismatch = true;
    if (scenario == "database") fixture.Durable.FailRead = true;
    if (scenario == "disabled") fixture.User.IsActive = false;
    var result = await fixture.Controller.Login(ChangeFixture.Email, ChangeFixture.ExistingPassword);
    Check(result is ViewResult && fixture.Auth.LastPrincipal == null, "durable login " + scenario + " denies ticket");
}
using (var fixture = new ChangeFixture(remote: true, durable: true))
{
    fixture.Http.Outcome = "phone-confirmed-only";
    var result = await fixture.Controller.Login(ChangeFixture.Email, ChangeFixture.ExistingPassword);
    Check(result is not RedirectResult && fixture.Auth.LastPrincipal == null,
        "provider confirmed_at alone cannot prove confirmed email or issue a durable login ticket");
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Supabase:Url"]="https://auth.example.test", ["Supabase:AnonKey"]="synthetic" }).Build();
    var provider = new SupabaseAuthService(new FakeHttpClientFactory(fixture.Http), config, NullLogger<SupabaseAuthService>.Instance);
    var otp = await provider.VerifyOtpAsync(ChangeFixture.Email, "123456", "signup");
    Check(otp != null && otp.User.EmailConfirmedAt == null,
        "provider OTP response confirmed_at alone does not establish email confirmation");
}
foreach (var outcome in new[] { "proof-timeout", "proof-transport" })
{
    using var fixture = new ChangeFixture(remote: true, durable: true);
    fixture.Http.Outcome = outcome;
    var result = await fixture.Controller.Login(ChangeFixture.Email, ChangeFixture.ExistingPassword);
    Check(result is ViewResult && fixture.Auth.LastPrincipal == null && fixture.Http.UpdateCalls == 0,
        "durable login " + outcome + " fails without issuing a ticket");
    var reset = await fixture.Controller.ResetPassword(ChangeFixture.Email, "123456", "Abcd1234", "Abcd1234");
    Check(reset is ViewResult && fixture.Auth.LastPrincipal == null && fixture.Http.UpdateCalls == 0,
        "durable recovery " + outcome + " fails before credential mutation");
    var change = await fixture.Change("Abcd1234", "Abcd1234");
    Check(change is RedirectToActionResult { ActionName: "Settings" } && fixture.Http.UpdateCalls == 0 && fixture.Durable.Epoch == 1 && fixture.Auth.LastPrincipal == null,
        "durable password change " + outcome + " fails before epoch or provider mutation");
}
using (var fixture = new ChangeFixture(remote: true, durable: true))
{
    fixture.Durable.Epoch = 2;
    var result = await fixture.Controller.SaveProfile("Test", "Member", 25, "Others", "Synthetic address");
    Check(result is UnauthorizedResult && fixture.Auth.LastPrincipal == null,
        "stale profile request is denied before mutation or cookie reissue");
}
using (var fixture = new ChangeFixture(remote: true))
{
    var result = await fixture.Controller.SaveProfile("Changed", "Member", 25, "Others", "Synthetic address");
    Check(result is RedirectToActionResult { ActionName: "Settings" } && fixture.User.FirstName == "Test" && fixture.User.Address == "Synthetic address" && fixture.Auth.LastPrincipal != null,
        "customer profile ignores a forged name while saving other details and reissuing the cookie");
}
using (var fixture = new ChangeFixture(remote: true, durable: true))
{
    var before = fixture.User.Name;
    var result = await fixture.Controller.SaveProfile("Changed", "Member", 25, "Others", "Synthetic address");
    Check(result is ObjectResult { StatusCode: 503 } && fixture.User.Name == before && fixture.Auth.LastPrincipal == null,
        "durable profile update fails closed before mutation when atomic epoch check is unavailable");
}
using (var fixture = new ChangeFixture(remote: true, durable: true))
{
    var blocked = new IActionResult[] { await fixture.Controller.UploadAvatar(null), await fixture.Controller.RemoveAvatar(),
        fixture.Controller.SaveNotifications(true), await fixture.Controller.SendEmailVerification(), fixture.Controller.ConfirmEmail("123456") };
    Check(blocked.All(x => x is ObjectResult { StatusCode: 503 }) && fixture.Auth.LastPrincipal == null && fixture.Http.UpdateCalls == 0,
        "gated nontransactional account mutations fail closed before side effects or ticket issuance");
}
using (var fixture = new ChangeFixture(remote: true, durable: true))
{
    var blocked = await fixture.Controller.Register("Synthetic", "Member", "new@example.test", 25, "Others", "Synthetic address", "Abcd1234");
    Check(blocked is ObjectResult { StatusCode: 503 } && fixture.Http.SignInCalls == 0 && fixture.Http.UpdateCalls == 0 && fixture.Auth.LastPrincipal == null,
        "durable registration remains blocked without provider or ticket side effects");
}
using (var fixture = new ChangeFixture(remote: false, durable: true))
{
    var forgot = await fixture.Controller.ForgotPassword(ChangeFixture.Email);
    var reset = await fixture.Controller.ResetPassword(ChangeFixture.Email, "123456", "Abcd1234", "Abcd1234");
    var login = await fixture.Controller.Login(ChangeFixture.Email, ChangeFixture.ExistingPassword);
    Check(forgot is ObjectResult { StatusCode: 503 } && reset is ObjectResult { StatusCode: 503 }
        && login is ViewResult && fixture.Auth.LastPrincipal == null && fixture.UsesPassword(ChangeFixture.ExistingPassword),
        "durable mode without provider denies recovery and login without local fallback or credential mutation");
}
using (var fixture = new ChangeFixture(remote: true, durable: true))
{
    var blocked = await fixture.Controller.Verify(ChangeFixture.Email, "123456", null);
    Check(blocked is ObjectResult { StatusCode: 503 } && fixture.Http.SignInCalls == 0 && fixture.Http.UpdateCalls == 0 && fixture.Auth.LastPrincipal == null,
        "durable signup OTP cannot issue an existing-account ticket without a browser-bound attempt");
}
using (var fixture = new ChangeFixture(remote: true, durable: true))
{
    var settings = new AccountSettingsService(fixture.Store, null!, null!);
    var valid = new AccountSessionEvidence(fixture.User.Id, fixture.User.Role, "1", AccountSession.Version(fixture.User.PasswordHash));
    foreach (var invalidEvidence in new[] { valid with { AccountId = Guid.NewGuid() }, valid with { Role = "Admin" }, valid with { CredentialEpoch = "0" }, valid with { AccountVersion = "" } })
        Check(!await settings.ProfileWithDurableEpoch(fixture.User, invalidEvidence, Guid.NewGuid(), "Synthetic", "Member", 25, "Others", "Synthetic address"),
            "atomic profile transaction rejects invalid evidence before database access");
}
using (var fixture = new ChangeFixture(remote: true))
{
    fixture.Http.IdentityMismatch = true;
    var result = await fixture.Change("Abcd1234", "Abcd1234");
    Check(result is RedirectToActionResult { ActionName: "Settings" } && fixture.Http.UpdateCalls == 0
        && fixture.Store.LinkCalls == 0 && fixture.Controller.TempData["Success"] == null,
        "identity mismatch prevents linkage and provider update");
}

foreach (var outcome in new[] { "timeout", "transport", "server" })
{
    using var fixture = new ChangeFixture(remote: true);
    fixture.Http.Outcome = outcome;
    var original = fixture.User.PasswordHash;
    var result = await fixture.Change("Abcd1234", "Abcd1234");
    Check(result is RedirectToActionResult { ActionName: "Login" } && fixture.Auth.SignOutCalls == 1
        && fixture.Controller.TempData["Error"] is string error && error.Contains("may have accepted")
        && fixture.Controller.TempData["Success"] == null && fixture.User.PasswordHash == original,
        outcome + ": uncertain provider update signs out current browser without claiming success or synchronizing");
}
using (var fixture = new ChangeFixture(remote: true))
{
    fixture.Http.Outcome = "reject";
    var original = fixture.User.PasswordHash;
    var result = await fixture.Change("Abcd1234", "Abcd1234");
    Check(result is RedirectToActionResult { ActionName: "Settings" } && fixture.User.PasswordHash == original
        && fixture.Controller.TempData["Error"] is string && fixture.Controller.TempData["Success"] == null,
        "provider rejection does not update local credentials or claim success");
}

foreach (var role in new[] { "Customer", "Admin", "SuperAdmin" })
{
    using var fixture = new ChangeFixture(remote: true, role: role, durable: true);
    var result = await fixture.Change("Abcd1234", "Abcd1234");
    Check(result is RedirectToActionResult { ActionName: "Login" } && fixture.Durable.State == SessionCredentialState.Ready
        && fixture.Durable.Epoch == 2 && fixture.Http.UpdateCalls == 1 && fixture.Store.LinkCalls == 0 && fixture.UsesPassword("Abcd1234")
        && fixture.User.Role == role && fixture.User.IsActive, role + ": durable controller change completes with linked subject and advanced epoch");
}
foreach (var outcome in new[] { "timeout", "reject", "empty-response", "malformed-response", "wrong-subject", "wrong-email", "duplicate-id" })
{
    using var fixture = new ChangeFixture(remote: true, durable: true);
    fixture.Http.Outcome = outcome;
    var result = await fixture.Change("Abcd1234", "Abcd1234");
    Check(result is RedirectToActionResult { ActionName: "Login" } && fixture.Durable.State == SessionCredentialState.ReconciliationRequired
        && fixture.Controller.TempData["Success"] == null && fixture.Auth.SignOutCalls == 1 && fixture.Http.UpdateCalls == 1 && fixture.UsesPassword(ChangeFixture.ExistingPassword),
        "durable " + outcome + ": no success and reconciliation required");
}
foreach (var scenario in new[] { "unknown", "stale", "mismatch", "database", "sync" })
{
    using var fixture = new ChangeFixture(remote: true, durable: true);
    if (scenario == "unknown") fixture.Durable.AuthState = AuthenticationIdentityPolicy.Unknown;
    if (scenario == "stale") fixture.Durable.Epoch = 2;
    if (scenario == "mismatch") fixture.Http.IdentityMismatch = true;
    if (scenario == "database") fixture.Durable.FailRead = true;
    if (scenario == "sync") fixture.Durable.FailComplete = true;
    var result = await fixture.Change("Abcd1234", "Abcd1234");
    Check(result is RedirectToActionResult { ActionName: "Login" } && fixture.Controller.TempData["Success"] == null
        && (scenario == "sync" ? fixture.Http.UpdateCalls == 1 && fixture.Durable.State == SessionCredentialState.Pending : fixture.Http.UpdateCalls == 0),
        "durable " + scenario + ": fails closed without false success");
}
foreach (var scenario in new[] { "success", "timeout", "sync", "unknown", "mismatch" })
{
    using var fixture = new ChangeFixture(remote: true, durable: true);
    fixture.Http.ExpectedNewPassword = "Abcd1234";
    if (scenario == "timeout") fixture.Http.Outcome = "timeout";
    if (scenario == "sync") fixture.Durable.FailComplete = true;
    if (scenario == "unknown") fixture.Durable.AuthState = AuthenticationIdentityPolicy.Unknown;
    if (scenario == "mismatch") fixture.Http.IdentityMismatch = true;
    var result = await fixture.Controller.ResetPassword(ChangeFixture.Email, "123456", "Abcd1234", "Abcd1234");
    Check(result is RedirectToActionResult { ActionName: "Login" } && fixture.Auth.SignOutCalls == 1 &&
        (scenario == "success" ? fixture.Controller.TempData["Notice"] != null && fixture.Durable.Epoch == 2 && fixture.UsesPassword("Abcd1234") :
            fixture.Controller.TempData["Notice"] == null && (scenario is "unknown" or "mismatch" ? fixture.Http.UpdateCalls == 0 : fixture.Http.UpdateCalls == 1)),
        "durable reset " + scenario + " uses confirmed subject and does not claim false success");
}

Console.WriteLine($"Actual ChangePassword controller checks: {passed} passed; 0 failed; 0 skipped.");
Console.WriteLine("Direct action execution only; no browser, production host, real provider, database, or cookie store was used.");

sealed class ChangeFixture : IDisposable
{
    public const string Email = "change-member@example.test";
    public const string ExistingPassword = "SyntheticCurrent9!";
    public FakeStore Store { get; } = new();
    public AppUser User { get; }
    public FakeAuthHandler Http { get; }
    public FakeAuthenticationService Auth { get; } = new();
    public AccountController Controller { get; }
    public FakeDurable Durable { get; }
    private readonly ServiceProvider requestServices;

    public ChangeFixture(bool remote, string role = "Customer", bool durable = false, bool persistentStore = true)
    {
        var subject = Guid.Parse("11111111-1111-1111-1111-111111111111");
        User = remote && !durable && role == "Customer"
            ? Store.CreateVerifiedCustomer(subject, new("Test", "Member", Email, 25, "", "Synthetic test address", ExistingPassword, subject))
            : Store.AddUser("Test", "Member", Email, ExistingPassword, role, 25, "", "Synthetic test address");
        Http = new FakeAuthHandler(Store, ExistingPassword);
        var factory = new FakeHttpClientFactory(Http);
        var values = new Dictionary<string, string?>();
        if (durable) values["Authentication:DurableSessionEnforcement"] = "true";
        if (remote)
        {
            values["Supabase:Url"] = "https://auth.example.test";
            values["Supabase:AnonKey"] = "synthetic-anon-key";
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var supabase = new SupabaseAuthService(factory, config, NullLogger<SupabaseAuthService>.Instance);
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var codes = new EmailVerificationService(cache, config, NullLogger<EmailVerificationService>.Instance, null!, null!);
        Controller = new AccountController(Store, supabase, codes, new AccountSettingsService(Store, null!, cache), null!, null!, new LoginAttemptLimiter(), config);

        var services = new ServiceCollection();
        Durable = new FakeDurable(User);
        Store.Persistent = durable && persistentStore;
        if (durable)
        {
            services.AddSingleton<IAccountSecuritySnapshotProvider>(Durable);
            services.AddSingleton<IVersionedAccountSecurityProvider>(Durable);
            services.AddSingleton(new VersionedSessionAuthorization(Durable));
            services.AddSingleton(new DurableSessionAuthorization(Durable));
            services.AddSingleton(new DurableCredentialUpdate(Durable));
        }
        services.AddSingleton<IAuthenticationService>(Auth);
        services.AddSingleton<IUrlHelperFactory, TestUrlHelperFactory>();
        requestServices = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = requestServices };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, Email),
            new Claim(ClaimTypes.NameIdentifier, User.Id.ToString()),
            new Claim(ClaimTypes.Role, User.Role),
            new Claim(DurableSessionAuthorization.EpochClaim, "1"),
            new Claim(DurableSessionAuthorization.SubjectClaim, "11111111-1111-1111-1111-111111111111"),
            new Claim(VersionedSessionAuthorization.SecurityVersionClaim, "1"),
            new Claim("account_version", AccountSession.Version(User.PasswordHash))
        ], "SyntheticTestAuth"));
        Controller.ControllerContext = new ControllerContext { HttpContext = context };
        Controller.TempData = new TempDataDictionary(context, new MemoryTempDataProvider());
    }

    public Task<IActionResult> Change(string password, string? confirmation, string? currentPassword = null)
    {
        Http.ExpectedNewPassword = password;
        return Controller.ChangePassword(currentPassword ?? ExistingPassword, password, confirmation!);
    }

    public bool UsesPassword(string password) =>
        new PasswordHasher<AppUser>().VerifyHashedPassword(User, User.PasswordHash, password) != PasswordVerificationResult.Failed;

    public void Dispose()
    {
        requestServices.Dispose();
        Http.Dispose();
    }
}

sealed class FakeDurable(AppUser user) : IAccountSecuritySnapshotProvider, IVersionedAccountSecurityProvider, IDurableCredentialStore
{
    public long SecurityVersion { get; set; } = 1;
    async Task<VersionedAccountSecurity?> IVersionedAccountSecurityProvider.LoadAsync(Guid id, CancellationToken ct) =>
        await LoadAsync(id, ct) is { } account ? new VersionedAccountSecurity(account, SecurityVersion) : null;
    private static readonly Guid Subject = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public long Epoch { get; set; } = 1;
    public SessionCredentialState State { get; set; } = SessionCredentialState.Ready;
    public string AuthState { get; set; } = AuthenticationIdentityPolicy.Supabase;
    public bool FailRead { get; set; }
    public bool FailComplete { get; set; }
    public Task<AccountSecuritySnapshot?> LoadAsync(Guid id, CancellationToken ct)
    {
        if (FailRead) throw new IOException("synthetic");
        return Task.FromResult<AccountSecuritySnapshot?>(new(user.Id, user.IsActive, false, user.Role, SessionStoreKind.PublicPostgres,
            SessionAuthenticationKind.Supabase, 1, user.Id, user.Id, user.Role == "Customer" ? SessionIdentityOwnerKind.Customer : SessionIdentityOwnerKind.Admin,
            true, AuthState, Subject, State, Epoch, AccountSession.Version(user.PasswordHash)));
    }
    public Task<long?> Begin(Guid id, Guid subject, long expected, CancellationToken ct)
    {
        if (id != user.Id || subject != Subject || expected != Epoch || State != SessionCredentialState.Ready) return Task.FromResult<long?>(null);
        State = SessionCredentialState.Pending; return Task.FromResult<long?>(++Epoch);
    }
    public Task<bool> Complete(Guid id, Guid subject, long epoch, string password, CancellationToken ct)
    {
        if (FailComplete) throw new IOException("synthetic");
        if (id != user.Id || subject != Subject || epoch != Epoch || State != SessionCredentialState.Pending) return Task.FromResult(false);
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password); State = SessionCredentialState.Ready; return Task.FromResult(true);
    }
    public Task<bool> Reject(Guid id, Guid subject, long epoch, CancellationToken ct) => Task.FromResult(false);
    public Task<bool> MarkReconciliation(Guid id, Guid subject, long epoch, CancellationToken ct)
    {
        if (id != user.Id || subject != Subject || epoch != Epoch || State != SessionCredentialState.Pending) return Task.FromResult(false);
        State = SessionCredentialState.ReconciliationRequired; return Task.FromResult(true);
    }
}

sealed class FakeStore : DemoStore, IStudioStore, ISupabaseIdentityStore, IVerifiedProviderCustomerStore
{
    public int LinkCalls { get; private set; }
    public bool Persistent { get; set; }
    bool IStudioStore.IsPersistent => Persistent;
    public bool FailReads { get; set; }
    public bool IdentityLinkingEnabled => true;
    public bool RestrictLegacyFallback => true;
    public bool LegacyFallbackAllowed(string email, bool restrictUnknown) => false;
    public void LinkSupabaseIdentity(string email, Guid supabaseUserId) => LinkCalls++;
    AppUser? IVerifiedProviderCustomerStore.FindVerifiedCustomer(Guid subject, string email) { LinkCalls++; return FindVerifiedCustomer(subject, email); }
    List<AppUser> IStudioStore.Users
    {
        get
        {
            if (FailReads) throw new InvalidOperationException("Synthetic local credential synchronization failure.");
            return Users;
        }
    }
}

sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

sealed class FakeAuthHandler(FakeStore store, string expectedCurrentPassword) : HttpMessageHandler
{
    public int SignInCalls { get; private set; }
    public int UpdateCalls { get; private set; }
    public bool LastCurrentPasswordMatched { get; private set; }
    public bool FailLocalSyncAfterUpdate { get; set; }
    public string? Outcome { get; set; }
    public bool IdentityMismatch { get; set; }
    public string? ExpectedNewPassword { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(body);
        if (request.RequestUri?.Host == "auth.example.test" && request.RequestUri.AbsolutePath is "/auth/v1/token" or "/auth/v1/verify")
        {
            if (Outcome == "proof-timeout") throw new TaskCanceledException("synthetic timeout");
            if (Outcome == "proof-transport") throw new HttpRequestException("synthetic transport failure");
        }
        if (request.RequestUri?.Host == "auth.example.test" && request.RequestUri.AbsolutePath == "/auth/v1/verify")
            return Reply(JsonSerializer.Serialize(new { access_token = "synthetic-session-token", user = new {
                id = "11111111-1111-1111-1111-111111111111", email = IdentityMismatch ? "other@example.test" : ChangeFixture.Email,
                email_confirmed_at = Outcome == "phone-confirmed-only" ? null : "2026-01-01T00:00:00Z",
                confirmed_at = Outcome == "phone-confirmed-only" ? "2026-01-01T00:00:00Z" : null } }));
        if (request.RequestUri?.Host == "auth.example.test"
            && request.RequestUri.AbsolutePath == "/auth/v1/token"
            && request.Method == HttpMethod.Post)
        {
            SignInCalls++;
            LastCurrentPasswordMatched = json.RootElement.GetProperty("password").GetString() == expectedCurrentPassword;
            if (!LastCurrentPasswordMatched)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent("{\"msg\":\"Email or password is incorrect.\"}", Encoding.UTF8, "application/json") };
            return Reply(JsonSerializer.Serialize(new
            {
                access_token = "synthetic-session-token",
                refresh_token = "synthetic-refresh-token",
                user = new
                {
                    id = "11111111-1111-1111-1111-111111111111",
                    email = IdentityMismatch ? "other@example.test" : ChangeFixture.Email,
                    email_confirmed_at = Outcome == "phone-confirmed-only" ? null : "2026-01-01T00:00:00Z",
                    confirmed_at = "2026-01-01T00:00:00Z"
                }
            }));
        }
        if (request.RequestUri?.Host == "auth.example.test"
            && request.RequestUri.AbsolutePath == "/auth/v1/user"
            && request.Method == HttpMethod.Put)
        {
            UpdateCalls++;
            if (Outcome == "timeout") throw new TaskCanceledException("synthetic timeout");
            if (Outcome == "transport") throw new HttpRequestException("synthetic connection loss");
            if (Outcome == "server") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (Outcome == "reject") return new HttpResponseMessage(HttpStatusCode.BadRequest);
            if (json.RootElement.GetProperty("password").GetString() != ExpectedNewPassword)
                throw new InvalidOperationException("The provider update did not preserve the submitted new password.");
            if (FailLocalSyncAfterUpdate) store.FailReads = true;
            if (Outcome == "empty-response") return Reply("{}");
            if (Outcome == "malformed-response") return Reply("{");
            if (Outcome == "duplicate-id") return Reply("{\"id\":\"11111111-1111-1111-1111-111111111111\",\"id\":\"11111111-1111-1111-1111-111111111111\",\"email\":\"change-member@example.test\"}");
            return Reply(JsonSerializer.Serialize(new { id = Outcome == "wrong-subject" ? Guid.NewGuid().ToString() : "11111111-1111-1111-1111-111111111111", email = Outcome == "wrong-email" ? "wrong@example.test" : ChangeFixture.Email }));
        }
        throw new InvalidOperationException("Unexpected fake request; network fallback is forbidden.");
    }

    private static HttpResponseMessage Reply(string content) => new(HttpStatusCode.OK)
    { Content = new StringContent(content, Encoding.UTF8, "application/json") };
}

sealed class FakeAuthenticationService : IAuthenticationService
{
    public int SignOutCalls { get; private set; }
    public string? LastSignOutScheme { get; private set; }
    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
    public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    public ClaimsPrincipal? LastPrincipal { get; private set; }
    public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) { LastPrincipal = principal; return Task.CompletedTask; }
    public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
    {
        SignOutCalls++;
        LastSignOutScheme = scheme;
        return Task.CompletedTask;
    }
}

sealed class MemoryTempDataProvider : ITempDataProvider
{
    public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
    public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
}

sealed class TestUrlHelperFactory : IUrlHelperFactory
{
    public IUrlHelper GetUrlHelper(ActionContext context) => new TestUrlHelper(context);
}

sealed class TestUrlHelper(ActionContext context) : IUrlHelper
{
    public ActionContext ActionContext { get; } = context;
    public string? Action(UrlActionContext actionContext) => "/" + actionContext.Action;
    public string? Content(string? contentPath) => contentPath;
    public bool IsLocalUrl(string? url) => url?.StartsWith("/", StringComparison.Ordinal) == true;
    public string? Link(string? routeName, object? values) => null;
    public string? RouteUrl(UrlRouteContext routeContext) => null;
}
