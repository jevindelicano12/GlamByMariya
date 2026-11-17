using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using GlamByMariya.Web.Controllers;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

var passed = 0;
var failed = 0;
var sourcePassed = 0;
var sourceFailed = 0;
void Check(bool condition, string name)
{
    if (condition)
    {
        passed++;
        Console.WriteLine("PASS (actual standalone workflow + terminal fake HTTP, DemoStore): " + name);
    }
    else
    {
        failed++;
        Console.WriteLine("FAIL (actual standalone workflow + terminal fake HTTP, DemoStore): " + name);
    }
}
void SourceCheck(bool condition, string name)
{
    if (condition)
    {
        sourcePassed++;
        Console.WriteLine("PASS (source-contract only): " + name);
    }
    else
    {
        sourceFailed++;
        Console.WriteLine("FAIL (source-contract only): " + name);
    }
}

const string password = "SyntheticWorkflowAdmin123";
var providerId = Guid.Parse("7b0e0f2a-8d25-4a42-9c52-1f6b2291f301");

// Successful workflow: the terminal fake verifies that provider provisioning
// starts only after the real DemoStore has created the exact inactive Pending row.
using (var fixture = new WorkflowFixture(new TerminalProvider(TerminalProvider.Mode.Valid, providerId)))
{
    var result = await fixture.RunAsync(password);
    var account = fixture.Store.Users.SingleOrDefault(x => x.Email == fixture.Invitation.Email);
    var snapshot = account == null ? null : fixture.Store.GetAdminProvisioning(account.Id);
    Check(fixture.Provider.Calls == 1 && fixture.Provider.SawInactivePendingAdmin,
        "valid invitation is consumed and exact inactive Pending Admin exists before one provider request");
    Check(result is { Succeeded: true, Status: AdminInvitationWorkflowStatus.Completed }
        && account is { Role: Roles.Admin, IsActive: true }
        && snapshot is { State: AdminProvisioningState.Confirmed, SupabaseUserId: var linked }
        && linked == providerId && snapshot.InvitationId == fixture.Invitation.Id,
        "matching structured provider UUID, invitation, local account and activation complete together");
    Check(fixture.Invitation.AcceptedAt != null,
        "successful workflow uses the ID returned by the consumed invitation");
}

// Invalid input is rejected before invitation consumption or local creation.
using (var fixture = new WorkflowFixture(new TerminalProvider(TerminalProvider.Mode.Valid, providerId)))
{
    var result = await fixture.RunAsync("short");
    Check(!result.Succeeded && result.Status == AdminInvitationWorkflowStatus.InvalidInput
        && fixture.Invitation.AcceptedAt == null && fixture.Store.Users.All(x => x.Email != fixture.Invitation.Email)
        && fixture.Provider.Calls == 0,
        "invalid password consumes no token, creates no account and makes no provider request");
}

foreach (var scenario in new[]
{
    (Mode: TerminalProvider.Mode.Reject, Label: "explicit HTTP rejection", Expected: AdminProvisioningState.Rejected),
    (Mode: TerminalProvider.Mode.TransportFailure, Label: "transport failure", Expected: AdminProvisioningState.Unknown),
    (Mode: TerminalProvider.Mode.Timeout, Label: "provider timeout", Expected: AdminProvisioningState.Unknown),
    (Mode: TerminalProvider.Mode.Malformed, Label: "malformed provider identity", Expected: AdminProvisioningState.Unknown),
    (Mode: TerminalProvider.Mode.ConflictingEmail, Label: "provider identity with conflicting email", Expected: AdminProvisioningState.Unknown)
})
{
    using var fixture = new WorkflowFixture(new TerminalProvider(scenario.Mode, providerId));
    var result = await fixture.RunAsync(password);
    var admin = fixture.Store.Users.SingleOrDefault(x => x.Email == fixture.Invitation.Email);
    var state = admin == null ? null : fixture.Store.GetAdminProvisioning(admin.Id);
    Check(!result.Succeeded && result.Status == AdminInvitationWorkflowStatus.ProviderNotConfirmed
        && admin is { Role: Roles.Admin, IsActive: false }
        && state?.State == scenario.Expected && fixture.Provider.Calls == 1,
        scenario.Label + " leaves the Admin inactive, records a non-confirmed state and makes no retry");
}

// Missing configuration is an explicit not-attempted result, not a provider
// success, and it does not call the terminal HTTP handler.
using (var fixture = new WorkflowFixture(new TerminalProvider(TerminalProvider.Mode.Valid, providerId), configured: false))
{
    var result = await fixture.RunAsync(password);
    var admin = fixture.Store.Users.Single(x => x.Email == fixture.Invitation.Email);
    Check(!result.Succeeded && result.Status == AdminInvitationWorkflowStatus.ProviderNotConfirmed
        && !admin.IsActive && fixture.Store.GetAdminProvisioning(admin.Id)?.State == AdminProvisioningState.NotAttempted
        && fixture.Provider.Calls == 0,
        "missing configuration leaves the account inactive and records NotAttempted without HTTP");
}

// Caller cancellation is terminal for this attempt. The request may have
// reached the provider before cancellation is observed, so assert at most one
// request and preserve an inactive/uncertain local account.
using (var fixture = new WorkflowFixture(new TerminalProvider(TerminalProvider.Mode.Valid, providerId)))
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var result = await fixture.RunAsync(password, cancellation.Token);
    var admin = fixture.Store.Users.Single(x => x.Email == fixture.Invitation.Email);
    Check(!result.Succeeded && !admin.IsActive && fixture.Provider.Calls == 1
        && fixture.Store.GetAdminProvisioning(admin.Id)?.State == AdminProvisioningState.Unknown,
        "caller cancellation stays inactive, records Unknown and does not retry (status=" + result.Status
        + ", state=" + fixture.Store.GetAdminProvisioning(admin.Id)?.State + ", calls=" + fixture.Provider.Calls + ")");
}

// A provider UUID already linked to another pending DemoStore account must not
// be claimed by the workflow's new account.
using (var fixture = new WorkflowFixture(new TerminalProvider(TerminalProvider.Mode.Valid, providerId)))
{
    var otherInvite = fixture.Invitations.InviteWithToken("already-linked@example.test", Guid.NewGuid()).Invite;
    var other = fixture.Store.CreatePendingInvitedAdmin("Existing", "Synthetic", otherInvite.Email, password,
        35, "Others", "Synthetic test address", otherInvite.Id);
    fixture.Store.ConfirmAdminProvisioning(other.Id, otherInvite.Id, other.Email,
        SupabaseProvisioningResult.Confirmed(providerId));
    var result = await fixture.RunAsync(password);
    var target = fixture.Store.Users.Single(x => x.Email == fixture.Invitation.Email);
    Check(!result.Succeeded && result.Status == AdminInvitationWorkflowStatus.ConfirmationNotVerified
        && !target.IsActive && fixture.Store.GetAdminProvisioning(target.Id) is { State: AdminProvisioningState.Unknown, SupabaseUserId: null }
        && fixture.Provider.Calls == 1,
        "duplicate provider UUID fails local correlation without activation or another provider request");
}

// Simulate a mis-correlated pending row and a local confirmation outage. In both
// cases the remote result is confirmed, but local confirmation is authoritative.
using (var fixture = new WorkflowFixture(new TerminalProvider(TerminalProvider.Mode.Valid, providerId),
    store: new MisCorrelatedDemoStore()))
{
    var result = await fixture.RunAsync(password);
    var target = fixture.Store.Users.Single(x => x.Email == fixture.Invitation.Email);
    Check(!result.Succeeded && result.Status == AdminInvitationWorkflowStatus.ConfirmationNotVerified
        && !target.IsActive && fixture.Provider.Calls == 1,
        "incorrect invitation/account correlation cannot complete after remote success");
}

using (var fixture = new WorkflowFixture(new TerminalProvider(TerminalProvider.Mode.Valid, providerId),
    store: new FailingConfirmationDemoStore()))
{
    var result = await fixture.RunAsync(password);
    var target = fixture.Store.Users.Single(x => x.Email == fixture.Invitation.Email);
    Check(!result.Succeeded && result.Status == AdminInvitationWorkflowStatus.ConfirmationNotVerified
        && !target.IsActive && fixture.Provider.Calls == 1,
        "local confirmation failure after remote success does not report completion or retry");
}

// Actual AccountController.AcceptInvite is contained before the internal test
// collaborator and legacy flow, even when the test-only workflow is attached.
using (var fixture = new WorkflowFixture(new TerminalProvider(TerminalProvider.Mode.Valid, providerId)))
{
    var result = await fixture.AcceptThroughControllerAsync(password);
    Check(result is ViewResult && fixture.Controller.ViewData["Error"] as string
        == "Administrator invitation acceptance is temporarily unavailable. Please contact the business owner for assistance."
        && fixture.Invitation.AcceptedAt == null
        && fixture.Store.Users.All(x => x.Email != fixture.Invitation.Email)
        && fixture.Provider.Calls == 0 && fixture.Store.AuditLogs.Count == 0
        && fixture.Authentication.SignInCalls == 0,
        "actual controller containment blocks valid input before the test workflow can consume or provision");
}

using (var fixture = new WorkflowFixture(new TerminalProvider(TerminalProvider.Mode.Valid, providerId)))
{
    var result = await fixture.AcceptThroughControllerAsync("short");
    Check(result is ViewResult && fixture.Controller.ViewData["Error"] as string
        == "Administrator invitation acceptance is temporarily unavailable. Please contact the business owner for assistance."
        && fixture.Invitation.AcceptedAt == null
        && fixture.Store.Users.All(x => x.Email != fixture.Invitation.Email)
        && fixture.Provider.Calls == 0 && fixture.Store.AuditLogs.Count == 0
        && fixture.Authentication.SignInCalls == 0,
        "actual controller blocks invalid input identically before token, account, provider, audit, or sign-in side effects");
}

Console.WriteLine($"Workflow runtime checks (in-memory DemoStore + terminal fake HTTP): {passed} passed; {failed} failed.");
var repositoryRoot = new DirectoryInfo(Directory.GetCurrentDirectory());
while (repositoryRoot != null && !File.Exists(Path.Combine(repositoryRoot.FullName, "MakeupSystem.slnx")))
    repositoryRoot = repositoryRoot.Parent;
if (repositoryRoot == null) throw new InvalidOperationException("Repository root unavailable for source-contract checks.");
var accountControllerSource = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "Frontend", "GlamByMariya.Web", "Controllers", "AccountController.cs"));
var appProgramSource = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "Frontend", "GlamByMariya.Web", "Program.cs"));
SourceCheck(!appProgramSource.Contains("AdminInvitationProvisioningWorkflow", StringComparison.Ordinal)
    && !appProgramSource.Contains("TestOnlyAdminInvitationWorkflow", StringComparison.Ordinal)
    && accountControllerSource.Contains("internal AdminInvitationProvisioningWorkflow? TestOnlyAdminInvitationWorkflow", StringComparison.Ordinal),
    "workflow is absent from production DI/startup and reachable through only an internal controller seam");
SourceCheck(accountControllerSource.Contains("internal AdminInvitationProvisioningWorkflow? TestOnlyAdminInvitationWorkflow", StringComparison.Ordinal)
    && !accountControllerSource.Contains("TestOnlyAdminInvitationWorkflow =", StringComparison.Ordinal)
    && !appProgramSource.Contains("TestOnlyAdminInvitationWorkflow", StringComparison.Ordinal),
    "controller test collaborator is internal, null by default and never assigned by production startup");
var inviteActionStart = accountControllerSource.IndexOf("public async Task<IActionResult> AcceptInvite(", StringComparison.Ordinal);
var inviteActionEnd = accountControllerSource.IndexOf("private AppUser? FindLocal", inviteActionStart, StringComparison.Ordinal);
var inviteActionSource = inviteActionStart >= 0 && inviteActionEnd > inviteActionStart
    ? accountControllerSource[inviteActionStart..inviteActionEnd] : "";
var containmentGuard = inviteActionSource.IndexOf("if (AdminInvitationAcceptanceIsContained())", StringComparison.Ordinal);
var testWorkflowPath = inviteActionSource.IndexOf("TestOnlyAdminInvitationWorkflow is", StringComparison.Ordinal);
var oldTokenPath = inviteActionSource.IndexOf("invites.Accept(", StringComparison.Ordinal);
SourceCheck(containmentGuard >= 0 && testWorkflowPath > containmentGuard && oldTokenPath > containmentGuard
    && inviteActionSource[containmentGuard..].Contains("return View();", StringComparison.Ordinal),
    "fail-closed guard precedes and blocks both test workflow and former production acceptance paths");
Console.WriteLine($"Source-contract checks: {sourcePassed} passed; {sourceFailed} failed.");
Console.WriteLine("Actual controller actions ran only with the explicit test collaborator; no production DI route, real provider, database, SQL, cookie or persistent transaction was exercised.");
if (failed != 0 || sourceFailed != 0) Environment.ExitCode = 1;

sealed class WorkflowFixture : IDisposable
{
    private readonly TerminalProvider handler;
    private readonly SupabaseAuthService supabase;
    private readonly MemoryCache cache;
    private readonly ServiceProvider controllerServices;
    private readonly ServiceProvider settingsServices;

    public DemoStore Store { get; }
    public MemoryInvitationService Invitations { get; }
    public AdminInvitation Invitation { get; }
    public TerminalProvider Provider => handler;
    public AdminInvitationProvisioningWorkflow Workflow { get; }
    public AccountController Controller { get; }
    public FakeAuthenticationService Authentication { get; } = new();

    public WorkflowFixture(TerminalProvider handler, bool configured = true, DemoStore? store = null)
    {
        this.handler = handler;
        Store = store ?? new DemoStore();
        Invitations = new MemoryInvitationService(TimeProvider.System);
        var created = Invitations.InviteWithToken("workflow@example.test", Guid.NewGuid());
        Invitation = created.Invite;
        Token = created.Token;
        handler.PendingInvariant = () =>
        {
            var pending = Store.Users.SingleOrDefault(x => x.Email == Invitation.Email);
            return Invitation.AcceptedAt != null && pending is { Role: Roles.Admin, IsActive: false }
                && Store.GetAdminProvisioning(pending.Id) is { State: AdminProvisioningState.Pending, SupabaseUserId: null }
                && Store.GetAdminProvisioning(pending.Id)?.InvitationId == Invitation.Id;
        };
        var values = new Dictionary<string, string?>();
        if (configured)
        {
            values["Supabase:Url"] = "https://auth.example.test";
            values["Supabase:AnonKey"] = "synthetic-anon-key";
            values["Supabase:ServiceKey"] = "synthetic-service-key";
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var httpFactory = new TerminalHttpClientFactory(handler);
        supabase = new SupabaseAuthService(httpFactory, configuration, NullLogger<SupabaseAuthService>.Instance);
        Workflow = new AdminInvitationProvisioningWorkflow(Invitations, Store, supabase);

        cache = new MemoryCache(new MemoryCacheOptions());
        var mail = new BrevoEmailSender(httpFactory, configuration, NullLogger<BrevoEmailSender>.Instance);
        var codes = new EmailVerificationService(cache, configuration, NullLogger<EmailVerificationService>.Instance,
            new TestEnvironment(), mail);
        settingsServices = new ServiceCollection().BuildServiceProvider();
        var settings = new AccountSettingsService(Store, settingsServices, cache);
        Controller = new AccountController(Store, supabase, codes, settings, null!, Invitations,
            new LoginAttemptLimiter(), configuration)
        {
            TestOnlyAdminInvitationWorkflow = Workflow
        };
        var requestServices = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(Authentication)
            .AddSingleton<IUrlHelperFactory, TestUrlHelperFactory>()
            .BuildServiceProvider();
        controllerServices = requestServices;
        var context = new DefaultHttpContext { RequestServices = requestServices };
        Controller.ControllerContext = new ControllerContext { HttpContext = context };
        Controller.TempData = new TempDataDictionary(context, new TestTempDataProvider());
    }

    private string Token { get; }

    public Task<AdminInvitationWorkflowResult> RunAsync(string suppliedPassword,
        CancellationToken cancellationToken = default) =>
        Workflow.AcceptAsync(Invitation.Email, Token, "Synthetic", "Invitee", 35, "Others",
            "Synthetic test address", suppliedPassword, cancellationToken);

    public Task<IActionResult> AcceptThroughControllerAsync(string suppliedPassword) =>
        Controller.AcceptInvite(Invitation.Email, Token, "Synthetic", "Invitee", 35, "Others",
            "Synthetic test address", suppliedPassword);

    public void Dispose()
    {
        cache.Dispose();
        controllerServices.Dispose();
        settingsServices.Dispose();
        handler.Dispose();
    }
}

sealed class TerminalProvider(TerminalProvider.Mode mode, Guid providerId) : HttpMessageHandler
{
    public enum Mode { Valid, Reject, TransportFailure, Timeout, Malformed, ConflictingEmail }
    public int Calls { get; private set; }
    public int LoginCalls { get; private set; }
    public bool SawInactivePendingAdmin { get; private set; }
    public Func<bool>? PendingInvariant { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && request.RequestUri?.Host == "auth.example.test"
            && request.RequestUri.AbsolutePath == "/auth/v1/token"
            && request.RequestUri.Query.Contains("grant_type=password", StringComparison.Ordinal))
        {
            LoginCalls++;
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent("{\"msg\":\"Invalid login credentials\"}", Encoding.UTF8, "application/json") };
        }

        Calls++;
        if (request.Method != HttpMethod.Post || request.RequestUri?.Host != "auth.example.test"
            || request.RequestUri.AbsolutePath != "/auth/v1/admin/users")
            throw new InvalidOperationException("Unexpected fake HTTP request; external traffic is forbidden.");

        SawInactivePendingAdmin = PendingInvariant?.Invoke() == true;
        if (mode == Mode.TransportFailure) throw new HttpRequestException("synthetic transport failure");
        if (mode == Mode.Timeout) throw new TaskCanceledException("synthetic provider timeout");
        if (mode == Mode.Reject)
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent("synthetic rejection", Encoding.UTF8, "application/json") };

        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var email = document.RootElement.GetProperty("email").GetString()!;

        var body = mode switch
        {
            Mode.Valid => JsonSerializer.Serialize(new { id = providerId, email }),
            Mode.Malformed => "{\"id\":\"not-a-uuid\",\"email\":\"" + email + "\"}",
            Mode.ConflictingEmail => JsonSerializer.Serialize(new { id = providerId, email = "different@example.test" }),
            _ => "{}"
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

sealed class TerminalHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

sealed class FakeAuthenticationService : IAuthenticationService
{
    public int SignInCalls { get; private set; }
    public ClaimsPrincipal? LastPrincipal { get; private set; }
    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
        Task.FromResult(AuthenticateResult.NoResult());
    public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
    {
        SignInCalls++;
        LastPrincipal = principal;
        return Task.CompletedTask;
    }
}

sealed class TestTempDataProvider : ITempDataProvider
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

sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "AdminInvitationWorkflowChecks";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public string EnvironmentName { get; set; } = "Tests";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

sealed class MisCorrelatedDemoStore : DemoStore
{
    public override AppUser CreatePendingInvitedAdmin(string firstName, string lastName, string email,
        string password, int age, string gender, string address, Guid invitationId) =>
        base.CreatePendingInvitedAdmin(firstName, lastName, email, password, age, gender, address, Guid.NewGuid());
}

sealed class FailingConfirmationDemoStore : DemoStore
{
    public override void ConfirmAdminProvisioning(Guid accountId, Guid invitationId, string email,
        SupabaseProvisioningResult provisioningResult) => throw new InvalidOperationException("synthetic local confirmation failure");
}
