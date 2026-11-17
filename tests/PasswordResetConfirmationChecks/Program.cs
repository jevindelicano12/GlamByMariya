using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GlamByMariya.Web.Controllers;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

// Actual application assembly/controller/services, never the application host.
// Every HTTP client is backed by an in-memory handler (no socket handler exists).
// Store and OTP cache are in-memory. No configuration files, DB, SMTP, or disk outbox.
var passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL (actual reset controller): " + label);
    passed++;
    Console.WriteLine("PASS (actual reset controller): " + label);
}

var invalid = new (string Label, string Password, string? Confirmation)[]
{
    ("missing confirmation", "Abcd1234", null),
    ("empty confirmation", "Abcd1234", ""),
    ("mismatched confirmation", "Abcd1234", "Other123"),
    ("case difference", "Abcd1234", "abcd1234"),
    ("leading whitespace difference", "Abcd1234", " Abcd1234"),
    ("trailing whitespace difference", "Abcd1234", "Abcd1234 "),
    ("Unicode representation difference", "A1\u00e9bcdef", "A1e\u0301bcdef")
};
foreach (var remote in new[] { false, true })
{
    var branch = remote ? "Supabase fake HTTP" : "local real OTP service";
    foreach (var sample in invalid)
    {
        using var fixture = new ResetFixture(remote, sample.Password);
        await fixture.PrepareAsync();
        var originalHash = fixture.User.PasswordHash;
        var originalProof = fixture.PendingRecovery;
        var result = await fixture.Controller.ResetPassword(ResetFixture.Email, fixture.Code, sample.Password, sample.Confirmation);
        Check(result is ViewResult && fixture.Controller.ViewData["Error"] is string, branch + ": " + sample.Label + " rejected");
        Check(fixture.Http.VerifyCalls == 0 && fixture.Http.UpdateCalls == 0 && fixture.Store.LinkCalls == 0
            && fixture.Store.UserReads == 0 && fixture.User.PasswordHash == originalHash,
            branch + ": invalid confirmation performs no OTP/link/provider/local mutation");
        Check(remote ? fixture.Http.RecoveryAvailable : ReferenceEquals(originalProof, fixture.PendingRecovery)
            && !fixture.ResetVerified && fixture.ResetAttempts == 0,
            branch + ": recovery proof and attempt budget untouched");
        Check(fixture.Controller.TempData["Notice"] == null && fixture.Controller.TempData["Success"] == null,
            branch + ": rejection never reports success");

        // Retry SAME recovery proof after correcting only confirmation: verifies
        // rejection did not consume it. This is actual controller execution.
        var corrected = await fixture.Controller.ResetPassword(ResetFixture.Email, fixture.Code, sample.Password, sample.Password);
        Check(corrected is RedirectToActionResult { ActionName: "Login" }
            && fixture.Controller.TempData["Notice"] is string, branch + ": corrected confirmation succeeds with same proof");
        Check(fixture.UsesPassword(sample.Password) && fixture.User.Role == "Customer" && fixture.User.IsActive,
            branch + ": successful retry updates only synthetic credential");
        Check(remote ? fixture.Http.VerifyCalls == 1 && fixture.Http.UpdateCalls == 1
            && fixture.Store.LinkCalls == 1 && !fixture.Http.RecoveryAvailable
            : fixture.PendingRecovery == null && !fixture.ResetVerified,
            branch + ": successful proof consumed once and local verification cleared");
    }

    var invalidPasswords = new[]
    {
        (Label: "129 UTF-16 units", Password: new string('a', 128) + "1"),
        (Label: "no ASCII letter", Password: new string('1', 8)),
        (Label: "no ASCII digit", Password: new string('a', 8)),
        (Label: "Unicode letter plus ASCII digit without ASCII letter", Password: "ééééééé1")
    };
    foreach (var sample in invalidPasswords)
    {
        using var fixture = new ResetFixture(remote, sample.Password);
        await fixture.PrepareAsync();
        var originalHash = fixture.User.PasswordHash;
        var originalProof = fixture.PendingRecovery;
        var result = await fixture.Controller.ResetPassword(ResetFixture.Email, fixture.Code, sample.Password, sample.Password);
        Check(result is ViewResult && fixture.Controller.ViewData["Error"] is string, branch + ": " + sample.Label + " rejected");
        Check(fixture.Http.VerifyCalls == 0 && fixture.Http.UpdateCalls == 0 && fixture.Store.LinkCalls == 0
            && fixture.Store.UserReads == 0 && fixture.User.PasswordHash == originalHash,
            branch + ": invalid policy leaves provider, identity, local password and account reads untouched");
        Check(fixture.Auth.SignOutCalls == 0 && (remote
            ? fixture.Http.RecoveryAvailable
            : ReferenceEquals(originalProof, fixture.PendingRecovery) && fixture.ResetAttempts == 0),
            branch + ": invalid policy leaves recovery proof/attempts untouched and never signs out");
    }

    foreach (var sample in new[]
    {
        (Label: "valid 8-unit password", Password: "Abcd1234"),
        (Label: "valid 128-unit password", Password: new string('a', 126) + "A1"),
        (Label: "Unicode and symbols with ASCII composition", Password: "A1é中!xyz")
    })
    {
        using var fixture = new ResetFixture(remote, sample.Password);
        await fixture.PrepareAsync();
        var result = await fixture.Controller.ResetPassword(ResetFixture.Email, fixture.Code, sample.Password, sample.Password);
        Check(result is RedirectToActionResult { ActionName: "Login" } && fixture.UsesPassword(sample.Password),
            branch + ": " + sample.Label + " remains successful");
        Check(remote ? fixture.Http.VerifyCalls == 1 && fixture.Http.UpdateCalls == 1 && fixture.Store.LinkCalls == 1
            : fixture.PendingRecovery == null && !fixture.ResetVerified,
            branch + ": successful recovery proof is consumed once after policy validation");
    }

    using (var fixture = new ResetFixture(remote, "Abc1234"))
    {
        await fixture.PrepareAsync();
        var result = await fixture.Controller.ResetPassword(ResetFixture.Email, fixture.Code, "Abc1234", "Abc1234");
        Check(result is ViewResult && fixture.Store.UserReads == 0 && fixture.Http.VerifyCalls == 0 && fixture.Http.UpdateCalls == 0,
            branch + ": shared minimum policy rejects before proof use");
    }
}

foreach (var role in new[] { "Customer", "Admin", "SuperAdmin" })
{
    using var fixture = new ResetFixture(true, "Abcd1234", role, active: false);
    await fixture.PrepareAsync();
    var result = await fixture.Controller.ResetPassword(ResetFixture.Email, fixture.Code, "Abcd1234", "Abcd1234");
    Check(result is ViewResult && !fixture.User.IsActive && fixture.User.Role == role && fixture.Http.UpdateCalls == 0,
        role + ": inactive or unbound account cannot reset through the Demo provider path");
}

using (var fixture = new ResetFixture(true, "Abcd1234"))
{
    fixture.Http.FailLocalAfterUpdate = true;
    await fixture.PrepareAsync();
    var original = fixture.User.PasswordHash;
    var result = await fixture.Controller.ResetPassword(ResetFixture.Email, fixture.Code, "Abcd1234", "Abcd1234");
    Check(result is RedirectToActionResult { ActionName: "Login" } && fixture.Http.UpdateCalls == 1 && fixture.Store.LinkCalls == 1,
        "Phase 1.6: partial failure follows confirmed proof/link/remote update");
    Check(fixture.User.PasswordHash == original && fixture.Controller.TempData["Error"] is string error
        && error.Contains("Supabase may have accepted the new password", StringComparison.Ordinal)
        && fixture.Controller.TempData["Notice"] == null && fixture.Auth.SignOutCalls == 1,
        "Phase 1.6: partial failure reports uncertainty, not success");
}

foreach (var outcome in new[] { "timeout", "transport", "server" })
{
    using var fixture = new ResetFixture(true, "Abcd1234");
    fixture.Http.Outcome = outcome;
    var original = fixture.User.PasswordHash;
    var result = await fixture.Controller.ResetPassword(ResetFixture.Email, fixture.Code, "Abcd1234", "Abcd1234");
    Check(result is RedirectToActionResult { ActionName: "Login" } && fixture.Auth.SignOutCalls == 1
        && fixture.User.PasswordHash == original && fixture.Controller.TempData["Notice"] == null
        && fixture.Controller.TempData["Error"] is string error && error.Contains("may have accepted"),
        "recovery " + outcome + ": uncertain update signs out current browser without claiming success");
}

using (var fixture = new ResetFixture(false, "Original123"))
{
    await fixture.PrepareAsync();
    var original = fixture.User.PasswordHash;
    var code = fixture.Code;
    var result = await fixture.Controller.ResetPassword(ResetFixture.Email, code, "Original123", "Original123");
    Check(result is ViewResult && fixture.User.PasswordHash == original && !fixture.ResetVerified,
        "local recovery rejects password reuse and clears proof");
    Check(await fixture.Controller.ResetPassword(ResetFixture.Email, code, "Different123", "Different123") is ViewResult,
        "rejected reuse consumes the OTP");
}
using (var fixture = new ResetFixture(false, "Different123"))
{
    fixture.User.PasswordHash = "not-base64!";
    await fixture.PrepareAsync();
    var result = await fixture.Controller.ResetPassword(ResetFixture.Email, fixture.Code, "Different123", "Different123");
    Check(result is ViewResult && fixture.User.PasswordHash == "not-base64!" && !fixture.ResetVerified,
        "local recovery fails closed on malformed hash and clears proof");
}
using (var fixture = new ResetFixture(false, "Different123"))
{
    await fixture.PrepareAsync();
    var code = fixture.Code;
    var attempts = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => fixture.ConfirmCode(code))));
    Check(attempts.Count(x => x) == 1, "concurrent local reset verification consumes the OTP once");
}

Console.WriteLine($"Actual reset-controller checks: {passed} passed; 0 failed; 0 skipped.");
Console.WriteLine("Direct action calls only: MVC form binding/rendering, cookies, real Supabase and PostgreSQL are not exercised.");

sealed class ResetFixture : IDisposable
{
    public const string Email = "reset-member@example.test";
    private readonly MemoryCache cache = new(new MemoryCacheOptions());
    private readonly EmailVerificationService codes;
    private readonly bool remote;
    public FakeStore Store { get; } = new();
    public AppUser User { get; }
    public FakeHttpHandler Http { get; }
    public FakeAuthenticationService Auth { get; } = new();
    public AccountController Controller { get; }
    private readonly ServiceProvider requestServices;
    public string Code => remote ? "123456" : Http.LocalCode ?? throw new InvalidOperationException("Synthetic recovery preparation failed.");
    public object? PendingRecovery => cache.Get("emailcode:reset:" + Email);
    public bool ResetVerified => cache.Get<bool>("emailcode:reset-ok:" + Email);
    public int ResetAttempts => cache.Get<int>("emailcode:attempts:" + Email);

    public ResetFixture(bool remote, string password, string role = "Customer", bool active = true)
    {
        this.remote = remote;
        var subject = Guid.Parse("11111111-1111-1111-1111-111111111111");
        User = remote && role == "Customer"
            ? Store.CreateVerifiedCustomer(subject, new("Test", "Member", Email, 25, "", "Synthetic test address", "Original123", subject))
            : Store.AddUser("Test", "Member", Email, "Original123", role, 25, "", "Synthetic test address");
        User.IsActive = active;
        Http = new FakeHttpHandler(Store, password);
        var factory = new FakeHttpClientFactory(Http);
        var values = new Dictionary<string, string?>
        {
            ["Email:BrevoApiKey"] = "synthetic-test-key",
            ["Email:From"] = "sender@example.test"
        };
        if (remote)
        {
            values["Supabase:Url"] = "https://auth.example.test";
            values["Supabase:AnonKey"] = "synthetic-test-key";
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var supabase = new SupabaseAuthService(factory, config, NullLogger<SupabaseAuthService>.Instance);
        var mail = new BrevoEmailSender(factory, config, NullLogger<BrevoEmailSender>.Instance);
        codes = new EmailVerificationService(cache, config, NullLogger<EmailVerificationService>.Instance, new TestEnvironment(), mail);
        // These unrelated dependencies must remain untouched by ResetPassword.
        Controller = new AccountController(Store, supabase, codes, null!, null!, null!, null!, config);
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IAuthenticationService>(Auth);
        serviceCollection.AddSingleton<IUrlHelperFactory, TestUrlHelperFactory>();
        requestServices = serviceCollection.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = requestServices };
        Controller.ControllerContext = new ControllerContext { HttpContext = context };
        Controller.TempData = new TempDataDictionary(context, new MemoryTempDataProvider());
    }

    public Task PrepareAsync() => remote ? Task.CompletedTask : codes.RequestPasswordResetCodeAsync(Email);
    public bool ConfirmCode(string code) { try { codes.ConfirmPasswordResetCode(Email, code); return true; } catch (InvalidOperationException) { return false; } }
    public bool UsesPassword(string value) => new PasswordHasher<AppUser>().VerifyHashedPassword(User, User.PasswordHash, value) != PasswordVerificationResult.Failed;
    public void Dispose() { cache.Dispose(); Http.Dispose(); requestServices.Dispose(); }
}

sealed class FakeStore : DemoStore, IStudioStore, ISupabaseIdentityStore, IVerifiedProviderCustomerStore
{
    public int UserReads { get; private set; }
    public int LinkCalls { get; private set; }
    public bool FailRead { get; set; }
    public bool IdentityLinkingEnabled => true;
    public bool RestrictLegacyFallback => true;
    public bool LegacyFallbackAllowed(string email, bool restrictUnknown) => false;
    public void LinkSupabaseIdentity(string email, Guid supabaseUserId) => LinkCalls++;
    AppUser? IVerifiedProviderCustomerStore.FindVerifiedCustomer(Guid subject, string email) { LinkCalls++; return FindVerifiedCustomer(subject, email); }
    List<AppUser> IStudioStore.Users
    {
        get
        {
            UserReads++;
            if (FailRead) throw new InvalidOperationException("Synthetic local synchronization failure.");
            return Users;
        }
    }
}

sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

sealed class FakeHttpHandler(FakeStore store, string expectedPassword) : HttpMessageHandler
{
    public int VerifyCalls { get; private set; }
    public int UpdateCalls { get; private set; }
    public bool RecoveryAvailable { get; private set; } = true;
    public string? LocalCode { get; private set; }
    public bool FailLocalAfterUpdate { get; set; }
    public string? Outcome { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // No fallback, DelegatingHandler, socket handler, DNS or real HTTP exists.
        var body = request.Content == null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(body);
        if (request.RequestUri?.Host == "api.brevo.com" && request.RequestUri.AbsolutePath == "/v3/smtp/email")
        {
            var match = Regex.Match(json.RootElement.GetProperty("textContent").GetString() ?? "", @"\b\d{6}\b");
            if (!match.Success) throw new InvalidOperationException("Synthetic email did not contain recovery proof.");
            LocalCode = match.Value;
            return Reply("{}");
        }
        if (request.RequestUri?.Host == "auth.example.test" && request.RequestUri.AbsolutePath == "/auth/v1/verify")
        {
            VerifyCalls++;
            if (!RecoveryAvailable || json.RootElement.GetProperty("type").GetString() != "recovery"
                || json.RootElement.GetProperty("token").GetString() != "123456")
                throw new InvalidOperationException("Synthetic recovery proof was consumed or invalid.");
            RecoveryAvailable = false;
            return Reply(JsonSerializer.Serialize(new
            {
                access_token = "synthetic-session",
                user = new { id = "11111111-1111-1111-1111-111111111111", email = ResetFixture.Email, email_confirmed_at = "2026-01-01T00:00:00Z" }
            }));
        }
        if (request.RequestUri?.Host == "auth.example.test" && request.RequestUri.AbsolutePath == "/auth/v1/user" && request.Method == HttpMethod.Put)
        {
            UpdateCalls++;
            if (Outcome == "timeout") throw new TaskCanceledException("synthetic timeout");
            if (Outcome == "transport") throw new HttpRequestException("synthetic connection loss");
            if (Outcome == "server") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (json.RootElement.GetProperty("password").GetString() != expectedPassword)
                throw new InvalidOperationException("Synthetic update input was transformed.");
            if (FailLocalAfterUpdate) store.FailRead = true;
            return Reply("{}");
        }
        throw new InvalidOperationException("Unexpected fake HTTP request; network fallback forbidden.");
    }
    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

sealed class MemoryTempDataProvider : ITempDataProvider
{
    public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
    public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
}

sealed class FakeAuthenticationService : IAuthenticationService
{
    public int SignOutCalls { get; private set; }
    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
    public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
    {
        SignOutCalls++;
        return Task.CompletedTask;
    }
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

sealed class TestEnvironment : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "Production";
    public string ApplicationName { get; set; } = "IsolatedResetChecks";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
