using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GlamByMariya.Web.Controllers;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

// Actual controller/actions/services with only in-memory collaborators. No app
// host, socket handler, database, persistent cache, production configuration, or
// real provider is constructed. Every possible HTTP call terminates in FakeHttp.
var passed = 0;
var failed = 0;
var sourcePassed = 0;
var sourceFailed = 0;
void Check(bool condition, string label)
{
    if (!condition)
    {
        failed++;
        Console.WriteLine("FAIL (actual registration/invitation controller): " + label);
        return;
    }
    passed++;
    Console.WriteLine("PASS (actual registration/invitation controller): " + label);
}
void SourceCheck(bool condition, string label)
{
    if (!condition)
    {
        sourceFailed++;
        Console.WriteLine("FAIL (source-contract only): " + label);
        return;
    }
    sourcePassed++;
    Console.WriteLine("PASS (source-contract only): " + label);
}

var relationalAssembly = Assembly.Load("Microsoft.EntityFrameworkCore.Relational");
Check(relationalAssembly.GetName().Version == new Version(10, 0, 13, 0),
    "test runtime resolves EF Core Relational 10.0.13.0");

var invalidPasswords = new (string Label, string Password)[]
{
    ("129 UTF-16 units", new string('a', 128) + "1"),
    ("missing ASCII letter", new string('1', 8)),
    ("missing ASCII digit", new string('a', 8)),
    ("Unicode letter without ASCII letter", "ééééééé1"),
    ("Unicode digit without ASCII digit", "Abcdefg١")
};

foreach (var remote in new[] { false, true })
{
    var branch = remote ? "Supabase-configured fake" : "local-code fake";
    foreach (var sample in invalidPasswords)
    {
        using var fixture = new FlowFixture(remote);
        var result = await fixture.Register(sample.Password);
        Check(result is ViewResult && fixture.Controller.ViewData["Error"] is string,
            branch + ": registration rejects " + sample.Label);
        Check(fixture.Store.UserReads == 0 && fixture.Store.AddUserCalls == 0
            && fixture.Http.SignupCalls == 0 && fixture.Http.EmailCodeCalls == 0
            && !fixture.Codes.HasPendingRegistration(FlowFixture.RegisterEmail)
            && fixture.Auth.SignInCalls == 0,
            branch + ": invalid registration performs no duplicate lookup, signup, code issuance, pending storage, account creation or sign-in");
    }
}

foreach (var remote in new[] { false, true })
foreach (var sample in new[]
{
    (Label: "valid 8-unit password", Password: "Abcd1234"),
    (Label: "valid 128-unit password", Password: new string('a', 126) + "A1"),
    (Label: "Unicode and symbols alongside ASCII composition", Password: "A1é中!xyz")
})
{
    using var fixture = new FlowFixture(remote);
    var started = await fixture.Register(sample.Password);
    Check(started is RedirectToActionResult { ActionName: "Verify" }
        && fixture.Codes.HasPendingRegistration(FlowFixture.RegisterEmail),
        (remote ? "Supabase" : "local") + ": " + sample.Label + " registration enters verification");
    Check(remote
        ? fixture.Http.SignupCalls == 1 && fixture.Http.LastSignupPasswordMatched && fixture.Http.EmailCodeCalls == 0
        : fixture.Http.SignupCalls == 0 && fixture.Http.EmailCodeCalls == 1,
        (remote ? "Supabase" : "local") + ": validation permits the expected signup/code path");

    Check(fixture.Store.AddUserCalls == 0 && fixture.Auth.SignInCalls == 0,
        (remote ? "Supabase" : "local") + ": account creation and sign-in await verification");
}

// In-memory fake delivery only: no real mailbox, provider or verification code is logged.
using (var fixture = new FlowFixture(remote: false))
{
    var password = "A1" + Guid.NewGuid().ToString("N");
    var started = await fixture.Register(password);
    var code = fixture.Http.LocalCode!;
    Check(started is RedirectToActionResult { ActionName: "Verify" } && code.Length == 6,
        "fake email delivery accepts a new registration request");
    var wrong = code == "000000" ? "111111" : "000000";
    Check(await fixture.Controller.Verify(FlowFixture.RegisterEmail, wrong, null) is ViewResult && fixture.Store.AddUserCalls == 0,
        "wrong fake-delivery code does not create an account");
    Check(await fixture.Controller.Verify(FlowFixture.RegisterEmail, code, null) is RedirectResult { Url: "/Bookings" } && fixture.Store.AddUserCalls == 1 && fixture.Auth.SignInCalls == 1,
        "correct fake-delivery code creates and signs in the Customer");
    Check(await fixture.Controller.Verify(FlowFixture.RegisterEmail, code, null) is ViewResult && fixture.Store.AddUserCalls == 1,
        "reused fake-delivery code cannot create a second account");
    Check(await fixture.Controller.Login(FlowFixture.RegisterEmail, password) is RedirectResult { Url: "/Bookings" } && fixture.Auth.SignInCalls == 2,
        "new local Customer can sign in after verification");
    var customer = fixture.Store.AddedUsers.Single();
    var workflow = new BookingService(fixture.Store, fixture.Store, fixture.Store, fixture.Store, fixture.Store);
    var service = fixture.Store.Services[0];
    var quote = workflow.Quote(service.Id, []);
    var booking = workflow.Create(customer.Id, service.Id, [], DateTime.Today.AddDays(10).AddHours(9), "123 Synthetic Street", "Quezon City",
        quote.RequiredDownPayment, "GCash", "SYNTHETIC-" + Guid.NewGuid().ToString("N"), null, true, 1, "auto", true);
    Check(booking.CustomerId == customer.Id && booking.Status == "Pending Payment", "new Customer can submit a synthetic booking after verification");
}

using (var fixture = new FlowFixture(remote: true))
{
    var password = "A1" + Guid.NewGuid().ToString("N");
    Check(await fixture.Register(password) is RedirectToActionResult { ActionName: "Verify" }, "fake provider signup starts a subject-bound registration");
    foreach (var malformed in new[] { "12345x", "１２３４５６", "12345" })
        Check(await fixture.Controller.Verify(FlowFixture.RegisterEmail, malformed, null) is ViewResult &&
            fixture.Http.VerifyCalls == 0 && fixture.Codes.HasPendingRegistration(FlowFixture.RegisterEmail) &&
            fixture.Auth.SignInCalls == 0, "malformed signup code is rejected before provider verification or pending-state consumption");
    var verified = await fixture.Controller.Verify(FlowFixture.RegisterEmail, "123456", null);
    var created = fixture.Store.Users.SingleOrDefault(u => u.Email == FlowFixture.RegisterEmail);
    Check(verified is RedirectResult { Url: "/Bookings" } && created?.Role == Roles.Customer && fixture.Auth.LastSignedInRole == Roles.Customer &&
        fixture.Auth.LastPrincipal?.FindFirst(DurableSessionAuthorization.SubjectClaim)?.Value == FlowTestIds.RegisterEmailId,
        "verified provider signup creates only a subject-bound Customer session");
    Check(!await RoleAuthorization.AuthorizeAdminAsync(fixture.Auth.LastPrincipal), "new provider Customer does not satisfy the Admin policy");
    Check(await fixture.Controller.Verify(FlowFixture.RegisterEmail, "123456", null) is ViewResult && fixture.Auth.SignInCalls == 1,
        "reused provider OTP cannot issue another session");
    fixture.Http.ExpectedLoginEmail = FlowFixture.RegisterEmail; fixture.Http.ExpectedLoginPassword = password;
    Check(await fixture.Controller.Login(FlowFixture.RegisterEmail, password) is RedirectResult && fixture.Auth.LastSignedInRole == Roles.Customer,
        "the same verified provider subject can sign in to its bound Customer account");
    foreach (var defect in new[] { "unconfirmed", "phone-only", "wrong-email", "missing-subject", "missing-token" })
    {
        fixture.Http.LoginDefect = defect;
        Check(await fixture.Controller.Login(FlowFixture.RegisterEmail, password) is ViewResult && fixture.Auth.SignInCalls == 2,
            defect + ": incomplete provider login evidence issues no session");
    }
    fixture.Http.LoginDefect = null;
    fixture.Http.LoginSubject = Guid.NewGuid().ToString("D");
    Check(await fixture.Controller.Login(FlowFixture.RegisterEmail, password) is ViewResult && fixture.Auth.SignInCalls == 2,
        "a different subject claiming the same email is rejected");
    fixture.Http.LoginSubject = FlowTestIds.RegisterEmailId;
    created!.Email = "changed@example.test";
    fixture.Http.ExpectedLoginEmail = created.Email;
    Check(await fixture.Controller.Login(created.Email, password) is ViewResult && fixture.Auth.SignInCalls == 2,
        "an email change cannot silently move a provider identity binding");
    Check(new DemoStore().FindVerifiedCustomer(Guid.Parse(FlowTestIds.RegisterEmailId), FlowFixture.RegisterEmail) == null,
        "Demo provider bindings are not durable across restart");
}

foreach (var defect in new[] { "unconfirmed", "phone-only", "missing-subject", "empty-subject", "wrong-subject", "wrong-email", "missing-token", "invalid-otp" })
{
    using var fixture = new FlowFixture(remote: true);
    await fixture.Register("A1" + Guid.NewGuid().ToString("N"));
    fixture.Http.VerifyDefect = defect;
    var result = await fixture.Controller.Verify(FlowFixture.RegisterEmail, "123456", null);
    Check(result is ViewResult && fixture.Auth.SignInCalls == 0 && !fixture.Store.Users.Any(u => u.Email == FlowFixture.RegisterEmail),
        defect + ": fake provider OTP creates no local account or session");
}

foreach (var role in new[] { Roles.Admin, Roles.SuperAdmin })
{
    using var fixture = new FlowFixture(remote: true);
    var staff = fixture.Store.AddLegacyUser(role, FlowFixture.RegisterEmail, FlowFixture.LegacyPassword);
    fixture.Http.VerifyEmail = staff.Email;
    var result = await fixture.Controller.Verify(staff.Email, "123456", null);
    Check(result is ViewResult && fixture.Auth.SignInCalls == 0 && staff.Role == role,
        role + ": verified provider OTP for a matching staff email cannot create a staff session");
}

{
    var demo = new DemoStore(); var subject = Guid.NewGuid(); var email = "bound@example.test";
    var pending = new EmailVerificationService.PendingRegistration("Test", "Customer", email, 25, "Others", "123 Test Street", "Test1234", subject);
    var user = demo.CreateVerifiedCustomer(subject, pending);
    bool Reject(Action operation) { try { operation(); return false; } catch (InvalidOperationException) { return true; } }
    var secondSubject = Guid.NewGuid();
    Check(Reject(() => demo.CreateVerifiedCustomer(secondSubject, pending)) &&
        Reject(() => demo.CreateVerifiedCustomer(subject, pending with { Email = "other@example.test" })) &&
        Reject(() => demo.CreateVerifiedCustomer(secondSubject, pending with { ProviderSubject = secondSubject, Email = user.Email })),
        "conflicting provider subject and email bindings are rejected");
    user.Role = Roles.Admin;
    Check(demo.FindVerifiedCustomer(subject, email) == null, "a provider Customer binding cannot follow a local role escalation");
}

using (var fixture = new FlowFixture(remote: true))
{
    fixture.Http.SignupUncertain = true;
    var result = await fixture.Register("Abcd1234");
    Check(result is ViewResult && fixture.Controller.ViewData["Error"] is string message && message.Contains("could not be confirmed") && !message.Contains("No password was stored")
        && fixture.Http.SignupCalls == 1 && fixture.Store.AddUserCalls == 0 && fixture.Auth.SignInCalls == 0 && !fixture.Codes.HasPendingRegistration(FlowFixture.RegisterEmail),
        "uncertain provider signup is not presented as a confirmed failure or authenticated locally");
}

// Fail-closed invitation containment runs before validation, token lookup, and
// the Phase 2.17N internal workflow seam for every input/provider configuration.
foreach (var scenario in new[]
{
    (Remote: true, ServiceKey: true, Password: "SafeSynthetic123", Label: "configured provider and valid input"),
    (Remote: false, ServiceKey: false, Password: "SafeSynthetic123", Label: "local-only mode and valid input"),
    (Remote: true, ServiceKey: true, Password: "short", Label: "invalid password")
})
{
    using var fixture = new FlowFixture(scenario.Remote, scenario.ServiceKey);
    var originalToken = fixture.Invitation.TokenAvailable;
    var action = await fixture.Invite(scenario.Password);
    Check(action is ViewResult
        && fixture.Controller.ViewData["Error"] is string error
        && error == FlowFixture.InvitationUnavailableMessage,
        scenario.Label + ": controller returns only the generic temporary-unavailability message");
    Check(fixture.Invitation.AcceptCalls == 0 && fixture.Invitation.TokenAvailable == originalToken
        && fixture.Store.UserReads == 0 && fixture.Store.ElevatedCalls == 0
        && fixture.Store.AddedUsers.Count == 0 && fixture.Store.AuditCalls == 0
        && fixture.Http.ProvisionCalls == 0 && fixture.Auth.SignInCalls == 0,
        scenario.Label + ": token, accounts, audit, provider, and sign-in remain untouched");
}

using (var fixture = new FlowFixture(remote: true, serviceKey: true))
{
    var get = fixture.Controller.AcceptInvite(FlowFixture.InviteEmail, FakeInvitationService.Token);
    Check(get is ViewResult && fixture.Controller.ViewData["AcceptanceUnavailable"] is true
        && fixture.Controller.ViewData["Error"] as string == FlowFixture.InvitationUnavailableMessage,
        "GET invitation page clearly reports temporary unavailability without changing the invitation");
    Check(fixture.Invitation.AcceptCalls == 0 && fixture.Invitation.TokenAvailable
        && fixture.Store.ElevatedCalls == 0 && fixture.Http.ProvisionCalls == 0,
        "GET invitation route does not consume the token, create an account, or contact the provider");
}

// Login must continue verifying historical credentials outside the new-password
// policy. Exercise all application roles using only local synthetic hashes.
foreach (var remote in new[] { false, true })
foreach (var role in new[] { Roles.Customer, Roles.Admin, Roles.SuperAdmin })
{
    using var fixture = new FlowFixture(remote);
    var legacy = fixture.Store.AddLegacyUser(role, FlowFixture.LoginEmail(role), FlowFixture.LegacyPassword);
    Check(PasswordPolicy.ValidateNewPassword(FlowFixture.LegacyPassword) != null,
        role + ": synthetic historical password is outside the new-password policy");
    fixture.Http.ExpectedLoginEmail = legacy.Email;
    fixture.Http.ExpectedLoginPassword = FlowFixture.LegacyPassword;
    var result = await fixture.Controller.Login(legacy.Email, FlowFixture.LegacyPassword);
    Check(remote ? result is ViewResult && fixture.Auth.SignInCalls == 0 : result is RedirectResult && fixture.Auth.SignInCalls == 1 && fixture.Auth.LastSignedInRole == role,
        (remote ? "Unbound provider " : "Local ") + role + ": " + (remote ? "email match cannot authorize an existing account" : "historical credential remains usable"));
    Check(fixture.Http.SignupCalls == 0 && fixture.Http.ProvisionCalls == 0,
        role + ": historical local login does not call a provider");
    if (remote)
        Check(fixture.Http.PasswordSignInCalls == 1 && fixture.Http.LastLoginPasswordMatched,
            "Supabase-authenticated " + role + ": provider password sign-in receives the existing credential");
}

foreach (var role in new[] { Roles.Customer, Roles.Admin, Roles.SuperAdmin })
{
    using var fixture = new FlowFixture(remote: true);
    var account = fixture.Store.AddLegacyUser(role, FlowFixture.LoginEmail(role), FlowFixture.LegacyPassword);
    fixture.Http.ExpectedLoginEmail = account.Email;
    fixture.Http.ExpectedLoginPassword = FlowFixture.LegacyPassword;
    fixture.Http.RejectPasswordSignIn = true;
    var result = await fixture.Controller.Login(account.Email, FlowFixture.LegacyPassword);
    Check(result is ViewResult && fixture.Auth.SignInCalls == 0,
        role + ": rejected Supabase credential cannot use stale local hash");
}

// Pending-state containment checks call the real DemoStore method and actual
// AccountController.Login action. The invite controller is intentionally not
// changed or treated as contained; its active-account orphan path is covered by
// the characterization above.
foreach (var path in new[]
{
    (Remote: false, Reject: false, Label: "Supabase unavailable"),
    (Remote: true, Reject: true, Label: "fake Supabase rejects password"),
    (Remote: true, Reject: false, Label: "fake Supabase succeeds")
})
{
    using var fixture = new FlowFixture(path.Remote);
    var pending = fixture.Store.CreatePendingInvitedAdmin("Synthetic", "Pending", "pending-" + Guid.NewGuid().ToString("N") + "@example.test",
        "SyntheticPendingAdmin123", 35, "Others", "Synthetic test address", Guid.NewGuid());
    var metadata = fixture.Store.GetAdminProvisioning(pending.Id);
    Check(pending.Role == Roles.Admin && !pending.IsActive
        && metadata is { State: AdminProvisioningState.Pending, SupabaseUserId: null },
        "actual DemoStore pending creation produces an inactive Admin with Pending metadata");

    fixture.Http.RejectPasswordSignIn = path.Reject;
    fixture.Http.ExpectedLoginEmail = pending.Email;
    fixture.Http.ExpectedLoginPassword = "SyntheticPendingAdmin123";
    var result = await fixture.Controller.Login(pending.Email, "SyntheticPendingAdmin123");
    var principal = fixture.Auth.LastPrincipal;
    Check(result is ViewResult && fixture.Auth.SignInCalls == 0 && principal == null,
        path.Label + ": inactive pending Admin receives no authenticated principal");
    Check(!await RoleAuthorization.AuthorizeAdminAsync(principal),
        path.Label + ": no pending-account principal satisfies the Admin role policy");
    Check(fixture.Http.PasswordSignInCalls == (path.Remote ? 1 : 0)
        && fixture.Http.LastLoginPasswordMatched == path.Remote,
        path.Label + ": only configured fake-provider cases issue provider password authentication");
}

// Exercise the exact shared registration and principal-validation callback
// used by Program.cs through ASP.NET Core's real cookie handler and authz middleware.
{
    var cookieStore = new DemoStore();
    var cookieFlow = new CookieHandlerFlow(cookieStore);
    foreach (var role in new[] { Roles.Customer, Roles.Admin, Roles.SuperAdmin })
    {
        var historical = cookieStore.Users.Single(x => x.Role == role);
        var cookie = await cookieFlow.IssueCookieAsync(historical);
        var outcome = await cookieFlow.AuthorizeAdminAsync(cookie);
        Check(outcome.Authenticated && outcome.Authorized == (role is Roles.Admin or Roles.SuperAdmin),
            $"historical {role} cookie retains its established authentication and Admin authorization behavior");
    }

    var cookieAdmin = cookieStore.Users.Single(x => x.Role == Roles.Admin);
    var activeAdminCookie = await cookieFlow.IssueCookieAsync(cookieAdmin);
    Check((await cookieFlow.AuthorizeAdminAsync(activeAdminCookie)) is { Authenticated: true, Authorized: true },
        "valid active historical Admin cookie authenticates and passes Admin authorization");

    var pending = cookieStore.AddUser("Pending", "Synthetic", "pending-cookie@example.test", "SyntheticPassword123!", Roles.Admin);
    pending.IsActive = false;
    var pendingCookie = await cookieFlow.IssueCookieAsync(pending);
    Check((await cookieFlow.AuthorizeAdminAsync(pendingCookie)) is { Authenticated: false, Authorized: false },
        "inactive pending Admin cannot reuse a validly issued cookie");

    cookieAdmin.IsActive = false;
    Check((await cookieFlow.AuthorizeAdminAsync(activeAdminCookie)) is { Authenticated: false, Authorized: false },
        "previously active Admin cookie is rejected after deactivation");
    cookieAdmin.IsActive = true;

    var demotedCookie = await cookieFlow.IssueCookieAsync(cookieAdmin);
    cookieAdmin.Role = Roles.Customer;
    Check((await cookieFlow.AuthorizeAdminAsync(demotedCookie)) is { Authenticated: false, Authorized: false },
        "old Admin cookie is rejected after the local account is demoted to Customer");
    cookieAdmin.Role = Roles.Admin;

    foreach (var defect in new[] { "missing-version", "invalid-version", "missing-account", "id-mismatch", "multiple-roles", "conflicting-role" })
    {
        var target = cookieStore.AddUser("Cookie", "Synthetic", $"cookie-{defect}@example.test", "SyntheticPassword123!", Roles.Admin);
        var malformedCookie = await cookieFlow.IssueCookieAsync(target, defect);
        if (defect == "missing-account") cookieStore.Users.Remove(target);
        Check((await cookieFlow.AuthorizeAdminAsync(malformedCookie)) is { Authenticated: false, Authorized: false },
            $"cookie with {defect} is rejected before Admin authorization");
    }

    // The test-only invitation path remains outside application startup; the
    // normal callback validates eligibility from the authoritative local store.
    await cookieFlow.DisposeAsync();
}

{
    var store = new DemoStore(); var subject = Guid.NewGuid(); var email = "cookie-provider@example.test";
    var user = store.CreateVerifiedCustomer(subject, new("Cookie", "Customer", email, 25, "Others", "Synthetic address", "Test1234", subject));
    await using var flow = new CookieHandlerFlow(store, providerConfigured: true);
    var bound = await flow.IssueCookieAsync(user, providerSubject: subject.ToString("D"));
    Check((await flow.AuthorizeAdminAsync(bound)) is { Authenticated: true, Authorized: false }, "bound provider Customer cookie authenticates without staff access");
    var old = await flow.IssueCookieAsync(user);
    Check((await flow.AuthorizeAdminAsync(old)) is { Authenticated: false, Authorized: false }, "old provider cookie without subject is rejected");
    var wrong = await flow.IssueCookieAsync(user, providerSubject: Guid.NewGuid().ToString("D"));
    Check((await flow.AuthorizeAdminAsync(wrong)) is { Authenticated: false, Authorized: false }, "provider cookie with a different subject is rejected");
    var staff = store.Users.Single(u => u.Role == Roles.Admin);
    var forged = await flow.IssueCookieAsync(staff, providerSubject: subject.ToString("D"));
    Check((await flow.AuthorizeAdminAsync(forged)) is { Authenticated: false, Authorized: false }, "bound Customer subject cannot authorize an Admin cookie");
    user.Email = "new-cookie-email@example.test";
    Check((await flow.AuthorizeAdminAsync(bound)) is { Authenticated: false, Authorized: false }, "provider cookie is rejected after an unbound local email change");
}

var repositoryRoot = new DirectoryInfo(AppContext.BaseDirectory);
while (repositoryRoot != null && !File.Exists(Path.Combine(repositoryRoot.FullName, "MakeupSystem.slnx"))) repositoryRoot = repositoryRoot.Parent;
if (repositoryRoot == null)
{
    repositoryRoot = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (repositoryRoot != null && !File.Exists(Path.Combine(repositoryRoot.FullName, "MakeupSystem.slnx"))) repositoryRoot = repositoryRoot.Parent;
}
if (repositoryRoot == null) throw new InvalidOperationException("Repository source root unavailable.");
var applicationSource = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "Frontend", "GlamByMariya.Web", "Program.cs"));
var accountControllerSource = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "Frontend", "GlamByMariya.Web", "Controllers", "AccountController.cs"));
var registrationWriterSource = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "Backend", "GlamByMariya.Infrastructure", "Data", "PostgresDurableCustomerRegistration.cs"));
var retrySchema = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "docs", "persistent-signup-retry-schema-candidate.sql"));
var cookieValidationSource = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "Frontend", "GlamByMariya.Web", "Security", "StudioCookieAuthentication.cs"));
SourceCheck(applicationSource.Contains("AddStudioCookieAuthentication(builder.Environment.IsDevelopment(), durableAuthentication)", StringComparison.Ordinal)
    && cookieValidationSource.Contains("durableEnforcement ? ValidateDurablePrincipalAsync : ValidatePrincipalAsync", StringComparison.Ordinal)
    && cookieValidationSource.Contains("user.Role, roles[0].Value", StringComparison.Ordinal)
    && cookieValidationSource.Contains("AccountSession.Version(user.PasswordHash)", StringComparison.Ordinal),
    "production startup and tests share the extracted callback with active, role and password-version checks");
SourceCheck(cookieValidationSource.Contains("FindVerifiedCustomer", StringComparison.Ordinal)
    && cookieValidationSource.Contains("DurableSessionAuthorization.SubjectClaim", StringComparison.Ordinal),
    "provider cookie validation requires the bound subject");
var generationInsert = registrationWriterSource.IndexOf("INSERT INTO public.account_security_generations", StringComparison.Ordinal);
SourceCheck(generationInsert >= 0 && registrationWriterSource.IndexOf("connection, tx", generationInsert, StringComparison.Ordinal) > generationInsert &&
    registrationWriterSource.IndexOf("tx.CommitAsync", generationInsert, StringComparison.Ordinal) > generationInsert &&
    !registrationWriterSource.Contains("ON CONFLICT", StringComparison.OrdinalIgnoreCase),
    "candidate inserts a non-reused initial security generation inside its account transaction before commit (source contract only)");
SourceCheck(accountControllerSource.Contains("prepared.Value.SecurityVersion != 1", StringComparison.Ordinal),
    "candidate refuses an already-advanced registration security generation before issuing a cookie (source contract only)");
SourceCheck(accountControllerSource.Contains("internal bool TestOnlySignupRetry { get; set; } = true;", StringComparison.Ordinal) &&
    accountControllerSource.Contains("if (!TestOnlyDurableSignup) return StatusCode(503", StringComparison.Ordinal) &&
    applicationSource.Contains("if (durableAuthentication)\n    throw new InvalidOperationException", StringComparison.Ordinal),
    "retry defaults only inside the test seam while registration and startup locks remain (source contract only)");
var schemaAbort = retrySchema.IndexOf("RAISE EXCEPTION", StringComparison.Ordinal);
var schemaAlter = retrySchema.IndexOf("ALTER TABLE", StringComparison.Ordinal);
SourceCheck(schemaAbort >= 0 && schemaAlter > schemaAbort && retrySchema.Contains("ROLLBACK;", StringComparison.Ordinal) &&
    retrySchema.Contains("consumed_operation_id IS NULL AND operation_id IS NULL", StringComparison.Ordinal) &&
    retrySchema.Contains("auth_mvc_signup_operation_nonempty", StringComparison.Ordinal) &&
    retrySchema.Contains("ux_auth_mvc_signup_unresolved_email", StringComparison.Ordinal),
    "review-only schema aborts before alteration and constrains operation IDs (source contract only)");
var adminAuthorize = typeof(AdminController).GetCustomAttribute<AuthorizeAttribute>();
SourceCheck(adminAuthorize?.Roles == "Admin,SuperAdmin",
    "AdminController authorization metadata requires Admin or SuperAdmin role");
var acceptActionStart = accountControllerSource.IndexOf("public async Task<IActionResult> AcceptInvite(", StringComparison.Ordinal);
var acceptGuard = accountControllerSource.IndexOf("if (AdminInvitationAcceptanceIsContained())", acceptActionStart, StringComparison.Ordinal);
var acceptWorkflow = accountControllerSource.IndexOf("TestOnlyAdminInvitationWorkflow is", acceptActionStart, StringComparison.Ordinal);
var acceptLegacyToken = accountControllerSource.IndexOf("invites.Accept(", acceptActionStart, StringComparison.Ordinal);
SourceCheck(acceptActionStart >= 0 && acceptGuard > acceptActionStart
    && (acceptWorkflow < 0 || acceptGuard < acceptWorkflow)
    && acceptGuard < acceptLegacyToken
    && accountControllerSource.Contains("AdminInvitationAcceptanceIsContained() => true", StringComparison.Ordinal),
    "fixed acceptance guard precedes both test-only workflow and legacy token consumption paths");

Console.WriteLine($"Actual controller/fake-provider checks: {passed} passed; {failed} failed; 0 skipped.");
Console.WriteLine($"Source-contract checks: {sourcePassed} passed; {sourceFailed} failed; 0 skipped.");
Console.WriteLine("No production host, external provider, or persistent database ran. Cookie handler and authorization middleware used the shared registration and exact production callback.");
if (failed != 0 || sourceFailed != 0) Environment.ExitCode = 1;

sealed class CookieHandlerFlow : IAsyncDisposable
{
    private const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    private readonly ServiceProvider services;
    private readonly RequestDelegate pipeline;

    public CookieHandlerFlow(DemoStore store, bool providerConfigured = false)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddDataProtection().UseEphemeralDataProtectionProvider();
        collection.AddSingleton<IStudioStore>(store);
        var providerConfig = new ConfigurationBuilder().AddInMemoryCollection(providerConfigured ? new Dictionary<string, string?> { ["Supabase:Url"] = "https://auth.example.test", ["Supabase:AnonKey"] = "synthetic" } : new Dictionary<string, string?>()).Build();
        collection.AddSingleton(new SupabaseAuthService(new FakeHttpClientFactory(new FakeHttp()), providerConfig, NullLogger<SupabaseAuthService>.Instance));
        collection.AddRouting();
        collection.AddAuthorization();
        collection.AddStudioCookieAuthentication(isDevelopment: true);
        collection.Configure<CookieAuthenticationOptions>(Scheme, options =>
        {
            options.Cookie.Name = "phase217p-isolated-cookie";
            options.Cookie.SecurePolicy = CookieSecurePolicy.None;
        });
        services = collection.BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseAuthentication();
        app.UseAuthorization();
        app.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        pipeline = app.Build();
    }

    public async Task<string> IssueCookieAsync(AppUser user, string defect = "", string? providerSubject = null)
    {
        using var scope = services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, defect == "id-mismatch" ? Guid.NewGuid().ToString() : user.Id.ToString()),
            new(ClaimTypes.Name, user.Name), new(ClaimTypes.Email, user.Email), new(ClaimTypes.Role, user.Role)
        };
        if (defect != "missing-version") claims.Add(new("account_version", defect == "invalid-version" ? "not-a-valid-version" : AccountSession.Version(user.PasswordHash)));
        if (defect == "multiple-roles") claims.Add(new(ClaimTypes.Role, user.Role));
        if (defect == "conflicting-role") claims.Add(new(ClaimTypes.Role, Roles.SuperAdmin));
        if (providerSubject != null) claims.Add(new(DurableSessionAuthorization.SubjectClaim, providerSubject));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
        await scope.ServiceProvider.GetRequiredService<IAuthenticationService>()
            .SignInAsync(context, Scheme, principal, new AuthenticationProperties());
        var setCookie = context.Response.Headers["Set-Cookie"].ToString();
        if (string.IsNullOrWhiteSpace(setCookie)) throw new InvalidOperationException("Cookie handler did not issue the synthetic cookie.");
        return setCookie.Split(';', 2)[0];
    }

    public async Task<(bool Authenticated, bool Authorized)> AuthorizeAdminAsync(string cookie)
    {
        var scope = services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = new ClaimsPrincipal() };
        context.Request.Headers.Cookie = cookie;
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new AuthorizeAttribute { Roles = "Admin,SuperAdmin" }), "synthetic-admin-endpoint"));
        context.Response.Body = new MemoryStream();
        try
        {
            await pipeline(context);
            var authenticated = context.User.Identity?.IsAuthenticated == true;
            return (authenticated, context.Response.StatusCode == StatusCodes.Status204NoContent);
        }
        finally { scope.Dispose(); }
    }

    public ValueTask DisposeAsync() => services.DisposeAsync();
}

sealed class FlowFixture : IDisposable
{
    public const string RegisterEmail = "registration-member@example.test";
    public const string InviteEmail = "invited-admin@example.test";
    public const string LegacyPassword = "HistoricalOnly";
    public const string InvitationUnavailableMessage = "Administrator invitation acceptance is temporarily unavailable. Please contact the business owner for assistance.";
    private readonly MemoryCache cache = new(new MemoryCacheOptions());
    private readonly ServiceProvider requestServices;
    private readonly ServiceProvider settingsServices;
    public FakeStore Store { get; } = new();
    public FakeInvitationService Invitation { get; } = new();
    public FakeAuthenticationService Auth { get; } = new();
    public FakeHttp Http { get; }
    public EmailVerificationService Codes { get; }
    public AccountController Controller { get; }
    private readonly bool remote;

    public FlowFixture(bool remote, bool serviceKey = false)
    {
        this.remote = remote;
        var values = new Dictionary<string, string?>
        {
            ["Email:BrevoApiKey"] = "synthetic-email-key",
            ["Email:From"] = "sender@example.test"
        };
        if (remote)
        {
            values["Supabase:Url"] = "https://auth.example.test";
            values["Supabase:AnonKey"] = "synthetic-anon-key";
            if (serviceKey) values["Supabase:ServiceKey"] = "synthetic-admin-key";
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Http = new FakeHttp();
        var clients = new FakeHttpClientFactory(Http);
        var supabase = new SupabaseAuthService(clients, config, NullLogger<SupabaseAuthService>.Instance);
        var mail = new BrevoEmailSender(clients, config, NullLogger<BrevoEmailSender>.Instance);
        Codes = new EmailVerificationService(cache, config, NullLogger<EmailVerificationService>.Instance, new TestEnvironment(), mail);
        settingsServices = new ServiceCollection().BuildServiceProvider();
        var settings = new AccountSettingsService(Store, settingsServices, cache);
        Controller = new AccountController(Store, supabase, Codes, settings, null!, Invitation, new LoginAttemptLimiter(), config);

        var requestServiceCollection = new ServiceCollection();
        requestServiceCollection.AddSingleton<IAuthenticationService>(Auth);
        requestServiceCollection.AddSingleton<IUrlHelperFactory, TestUrlHelperFactory>();
        requestServices = requestServiceCollection.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = requestServices };
        Controller.ControllerContext = new ControllerContext { HttpContext = context };
        Controller.TempData = new TempDataDictionary(context, new MemoryTempDataProvider());
    }

    public Task<IActionResult> Register(string password)
    {
        Http.ExpectedPassword = password;
        return Controller.Register("Test", "Member", RegisterEmail, 25, "Others", "Synthetic address for tests", password, birthday: new DateOnly(2000, 1, 15));
    }

    public Task<IActionResult> Invite(string password)
    {
        Http.ExpectedPassword = password;
        return Controller.AcceptInvite(InviteEmail, FakeInvitationService.Token, "Test", "Invitee", 30, "", "Synthetic invite address", password);
    }

    public AppUser AddLegacyUser(string role, string email, string password) => Store.AddLegacyUser(role, email, password);

    public void Dispose()
    {
        cache.Dispose();
        Http.Dispose();
        requestServices.Dispose();
        settingsServices.Dispose();
    }

    public static string LoginEmail(string role) => role.ToLowerInvariant() + "-legacy@example.test";
}

sealed class FakeStore : DemoStore, IStudioStore, ISupabaseIdentityStore
{
    public int UserReads { get; private set; }
    public int AddUserCalls { get; private set; }
    public int ElevatedCalls { get; private set; }
    public int AuditCalls { get; private set; }
    public int IdentityLinkCalls { get; private set; }
    public bool IdentityLinkingEnabled => false;
    public bool RestrictLegacyFallback => false;
    public string IdentityState => AuthenticationIdentityPolicy.Unknown;
    public Guid? SupabaseSubject => null;
    public bool LegacyFallbackAllowed(string email, bool restrictUnknown) =>
        AuthenticationIdentityPolicy.CanUseLegacyFallback(IdentityState, SupabaseSubject, restrictUnknown);
    public List<AppUser> AddedUsers { get; } = [];
    public List<(Guid Actor, string Message)> AuditEntries { get; } = [];
    public bool PasswordMatches(AppUser user, string password) =>
        new PasswordHasher<AppUser>().VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;

    public AppUser AddLegacyUser(string role, string email, string password) => base.AddUser("Legacy", "Member", email, password, role, 30, "", "Synthetic legacy address");

    List<AppUser> IStudioStore.Users
    {
        get { UserReads++; return Users; }
    }

    AppUser IStudioStore.AddUser(string firstName, string lastName, string email, string password, string role, int age, string gender, string address)
    {
        AddUserCalls++;
        var error = RegistrationRules.Validate(firstName, lastName, email, age, gender, address, password);
        if (error != null) throw new InvalidOperationException(error);
        var user = base.AddUser(firstName, lastName, email, password, role, age, gender, address);
        AddedUsers.Add(user);
        return user;
    }

    AppUser IStudioStore.CreateElevatedUser(string firstName, string lastName, string email, string password, string role, int age, string gender, string address)
    {
        ElevatedCalls++;
        var error = RegistrationRules.Validate(firstName, lastName, email, age, gender, address, password);
        if (error != null) throw new InvalidOperationException(error);
        var user = base.CreateElevatedUser(firstName, lastName, email, password, role, age, gender, address);
        AddedUsers.Add(user);
        return user;
    }

    void IStudioStore.RecordAudit(Guid actor, string message)
    {
        AuditCalls++;
        AuditEntries.Add((actor, message));
    }
    void ISupabaseIdentityStore.LinkSupabaseIdentity(string email, Guid supabaseUserId) => IdentityLinkCalls++;
}

sealed class FakeInvitationService : IInvitationService
{
    public const string Token = "synthetic-invitation-token";
    public int AcceptCalls { get; private set; }
    public bool TokenAvailable { get; private set; } = true;
    public AdminInvitation Invite(string email, Guid invitedBy) => throw new NotSupportedException();
    public (AdminInvitation Invite, string Token) InviteWithToken(string email, Guid invitedBy) => throw new NotSupportedException();
    public IReadOnlyList<AdminInvitation> Pending() => [];
    public AdminInvitation Accept(string email, string token)
    {
        AcceptCalls++;
        if (!TokenAvailable || token != Token) throw new InvalidOperationException("Synthetic invitation unavailable.");
        TokenAvailable = false;
        return new AdminInvitation { Email = email, AcceptedAt = DateTime.UtcNow };
    }
    public void Revoke(Guid id, Guid actor) => throw new NotSupportedException();
}

sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

sealed class FakeHttp : HttpMessageHandler
{
    public enum ProvisionMode { Success, Reject, ThrowTransport, TimeoutAfterAcceptance }
    public int EmailCodeCalls { get; private set; }
    public int SignupCalls { get; private set; }
    public int VerifyCalls { get; private set; }
    public int ProvisionCalls { get; private set; }
    public int PasswordSignInCalls { get; private set; }
    public string? LocalCode { get; private set; }
    public string? ExpectedPassword { get; set; }
    public bool LastSignupPasswordMatched { get; private set; }
    public bool SignupUncertain { get; set; }
    public bool LastProvisionPasswordMatched { get; private set; }
    public bool LastProvisionRequestMatched { get; private set; }
    public ProvisionMode ProvisionBehavior { get; set; }
    public bool ProviderMayHaveCreatedUser { get; private set; }
    public bool RejectPasswordSignIn { get; set; }
    public string? ExpectedLoginEmail { get; set; }
    public string? ExpectedLoginPassword { get; set; }
    public bool LastLoginPasswordMatched { get; private set; }
    public string? VerifyDefect { get; set; }
    public string? VerifyEmail { get; set; }
    public string LoginSubject { get; set; } = FlowTestIds.RegisterEmailId;
    public string? LoginDefect { get; set; }
    private bool otpConsumed;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(body);
        if (request.RequestUri?.Host == "api.brevo.com" && request.RequestUri.AbsolutePath == "/v3/smtp/email")
        {
            EmailCodeCalls++;
            var match = Regex.Match(json.RootElement.GetProperty("textContent").GetString() ?? "", @"\b\d{6}\b");
            if (!match.Success) throw new InvalidOperationException("Synthetic verification message was invalid.");
            LocalCode = match.Value;
            return Reply("{}");
        }
        if (request.RequestUri?.Host == "auth.example.test" && request.RequestUri.AbsolutePath == "/auth/v1/signup" && request.Method == HttpMethod.Post)
        {
            SignupCalls++;
            LastSignupPasswordMatched = json.RootElement.GetProperty("password").GetString() == ExpectedPassword;
            if (!LastSignupPasswordMatched) throw new InvalidOperationException("Fake signup input changed unexpectedly.");
            if (SignupUncertain) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
            return Reply(JsonSerializer.Serialize(new { id = FlowTestIds.RegisterEmailId, email = FlowFixture.RegisterEmail, confirmation_sent_at = "2026-01-01T00:00:00Z" }));
        }
        if (request.RequestUri?.Host == "auth.example.test" && request.RequestUri.AbsolutePath == "/auth/v1/verify" && request.Method == HttpMethod.Post)
        {
            VerifyCalls++;
            if (json.RootElement.GetProperty("type").GetString() != "signup" || json.RootElement.GetProperty("token").GetString() != "123456" || otpConsumed || VerifyDefect == "invalid-otp")
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"msg\":\"Invalid OTP\"}") };
            otpConsumed = true;
            return Reply(JsonSerializer.Serialize(new
            {
                access_token = VerifyDefect == "missing-token" ? "" : "synthetic-access-token",
                refresh_token = "synthetic-refresh-token",
                user = new { id = VerifyDefect == "missing-subject" ? null : VerifyDefect == "empty-subject" ? Guid.Empty.ToString("D") : VerifyDefect == "wrong-subject" ? Guid.NewGuid().ToString("D") : FlowTestIds.RegisterEmailId,
                    email = VerifyDefect == "wrong-email" ? "wrong@example.test" : VerifyEmail ?? FlowFixture.RegisterEmail,
                    email_confirmed_at = VerifyDefect is "unconfirmed" or "phone-only" ? null : "2026-01-01T00:00:00Z",
                    phone_confirmed_at = VerifyDefect == "phone-only" ? "2026-01-01T00:00:00Z" : null }
            }));
        }
        if (request.RequestUri?.Host == "auth.example.test" && request.RequestUri.AbsolutePath == "/auth/v1/admin/users" && request.Method == HttpMethod.Post)
        {
            ProvisionCalls++;
            LastProvisionPasswordMatched = json.RootElement.GetProperty("password").GetString() == ExpectedPassword;
            if (!LastProvisionPasswordMatched) throw new InvalidOperationException("Fake admin provisioning input changed unexpectedly.");
            LastProvisionRequestMatched = json.RootElement.GetProperty("email").GetString() == FlowFixture.InviteEmail
                && json.RootElement.GetProperty("email_confirm").GetBoolean()
                && json.RootElement.GetProperty("user_metadata").GetProperty("first_name").GetString() == "Test"
                && json.RootElement.GetProperty("user_metadata").GetProperty("last_name").GetString() == "Invitee";
            if (!LastProvisionRequestMatched) throw new InvalidOperationException("Fake admin provisioning account data changed unexpectedly.");
            if (ProvisionBehavior == ProvisionMode.Reject)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent("synthetic provider rejection marker", Encoding.UTF8, "application/json") };
            if (ProvisionBehavior == ProvisionMode.ThrowTransport)
            {
                ProviderMayHaveCreatedUser = true;
                throw new HttpRequestException("synthetic transport failure marker");
            }
            if (ProvisionBehavior == ProvisionMode.TimeoutAfterAcceptance)
            {
                ProviderMayHaveCreatedUser = true;
                throw new TaskCanceledException("synthetic timeout marker");
            }
            return Reply("{}");
        }
        if (request.RequestUri?.Host == "auth.example.test"
            && request.RequestUri.AbsolutePath == "/auth/v1/token"
            && request.RequestUri.Query.Contains("grant_type=password", StringComparison.Ordinal)
            && request.Method == HttpMethod.Post)
        {
            PasswordSignInCalls++;
            LastLoginPasswordMatched = json.RootElement.GetProperty("email").GetString() == ExpectedLoginEmail
                && json.RootElement.GetProperty("password").GetString() == ExpectedLoginPassword;
            if (!LastLoginPasswordMatched) throw new InvalidOperationException("Fake password sign-in input changed unexpectedly.");
            if (RejectPasswordSignIn)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent("{\"msg\":\"Invalid login credentials\"}", Encoding.UTF8, "application/json") };
            return Reply(JsonSerializer.Serialize(new
            {
                access_token = LoginDefect == "missing-token" ? "" : "synthetic-login-access-token",
                refresh_token = "synthetic-login-refresh-token",
                user = new { id = LoginDefect == "missing-subject" ? null : LoginSubject,
                    email = LoginDefect == "wrong-email" ? "wrong@example.test" : ExpectedLoginEmail,
                    email_confirmed_at = LoginDefect is "unconfirmed" or "phone-only" ? null : "2026-01-01T00:00:00Z",
                    phone_confirmed_at = LoginDefect == "phone-only" ? "2026-01-01T00:00:00Z" : null }
            }));
        }
        throw new InvalidOperationException("Unexpected fake HTTP request; network fallback is forbidden.");
    }

    private static HttpResponseMessage Reply(string content) => new(HttpStatusCode.OK)
    { Content = new StringContent(content, Encoding.UTF8, "application/json") };
}

sealed class FakeAuthenticationService : IAuthenticationService
{
    public int SignInCalls { get; private set; }
    public string? LastSignedInRole { get; private set; }
    public string? LastSignedInUserId { get; private set; }
    public string? LastSignedInEmail { get; private set; }
    public string? LastSignedInName { get; private set; }
    public ClaimsPrincipal? LastPrincipal { get; private set; }
    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
    public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
    {
        SignInCalls++;
        LastPrincipal = principal;
        LastSignedInRole = principal.FindFirst(ClaimTypes.Role)?.Value;
        LastSignedInUserId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        LastSignedInEmail = principal.FindFirst(ClaimTypes.Email)?.Value;
        LastSignedInName = principal.FindFirst(ClaimTypes.Name)?.Value;
        return Task.CompletedTask;
    }
}

static class RoleAuthorization
{
    public static async Task<bool> AuthorizeAdminAsync(ClaimsPrincipal? principal)
    {
        if (principal == null) return false;
        var serviceProvider = new ServiceCollection().AddLogging().AddAuthorization().BuildServiceProvider();
        try
        {
            var authorization = serviceProvider.GetRequiredService<IAuthorizationService>();
            var policy = new AuthorizationPolicyBuilder().RequireRole(Roles.Admin, Roles.SuperAdmin).Build();
            var result = await authorization.AuthorizeAsync(principal, resource: null, policy.Requirements);
            return result.Succeeded;
        }
        finally { await serviceProvider.DisposeAsync(); }
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

sealed class TestEnvironment : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "Production";
    public string ApplicationName { get; set; } = "IsolatedRegistrationChecks";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}

static class FlowTestIds
{
    public const string RegisterEmailId = "11111111-1111-1111-1111-111111111111";
}
