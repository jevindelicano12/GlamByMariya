using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

var store = new DemoStore(presentationAccounts: true);
var workflow = new BookingService(store, store, store, store, store);
var customer = store.Users.First(u => u.Role == "Customer");
var admin = store.Users.First(u => u.Role == "Admin");
var workflowOwner = store.Users.First(u => u.Role == "SuperAdmin");
var service = store.Services[0];
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine($"PASS: {name}"); }
void Reject(Action action, string name) { try { action(); } catch (InvalidOperationException) { Check(true, name); return; } throw new Exception($"Expected rejection: {name}"); }
if (args.Contains("--cancellation-policy-only"))
{
    var now = new DateTime(2026, 11, 18, 12, 0, 0);
    var free = CancellationPolicy.Evaluate("Pending Payment", now.AddHours(2), now);
    Check(free.Allowed && free.Fee == 0, "unpaid pending booking cancels free before it starts");
    var early = CancellationPolicy.Evaluate("Confirmed", now.AddHours(25), now);
    Check(early.Allowed && early.Fee == 200, "confirmed booking more than 24 hours away cancels with the PHP 200 fee");
    var boundary = CancellationPolicy.Evaluate("Confirmed", now.AddHours(24), now);
    Check(boundary.Allowed && boundary.Fee == 200, "exactly 24 hours before is still allowed");
    var late = CancellationPolicy.Evaluate("Confirmed", now.AddHours(24).AddMinutes(-1), now);
    Check(!late.Allowed && late.Reason!.Contains("24 hours"), "confirmed booking inside the 24-hour cut-off is blocked");
    Check(!CancellationPolicy.Evaluate("Pending Payment", now, now).Allowed, "pending booking that already started is blocked");
    Check(!CancellationPolicy.Evaluate("Confirmed", now.AddMinutes(-5), now).Allowed, "started confirmed booking is blocked");
    foreach (var closed in new[] { "Cancelled", "Rejected", "Completed", "No-show", "Incomplete" })
        Check(!CancellationPolicy.Evaluate(closed, now.AddDays(3), now).Allowed, $"{closed} booking cannot be cancelled again");
    Check(CancellationPolicy.RefundDue(200, 150) == 0 && CancellationPolicy.RefundDue(200, 500) == 300 && CancellationPolicy.RefundDue(0, 0) == 0,
        "refund is verified paid minus the fee, never negative");

    var demo = new DemoStore(presentationAccounts: true); var flow = new BookingService(demo, demo, demo, demo, demo);
    var owner = demo.Users.First(u => u.Role == Roles.Customer);
    var pending = demo.Bookings.Single(); pending.Start = demo.Now.AddHours(3);
    Check(pending.Status == "Pending Payment" && flow.HoldsReservation(pending), "seed booking starts pending and holds its slot");
    flow.Cancel(pending, owner.Id, true);
    Check(pending.Status == "Cancelled" && pending.CancellationFee == 0 && pending.RefundDue == 0 && !flow.HoldsReservation(pending),
        "customer cancels an unpaid pending booking with no fee and the slot is released");

    var demo2 = new DemoStore(presentationAccounts: true); var flow2 = new BookingService(demo2, demo2, demo2, demo2, demo2);
    var owner2 = demo2.Users.First(u => u.Role == Roles.Customer);
    var confirmed = demo2.Bookings.Single(); var deposit = demo2.Payments.Single(p => p.BookingId == confirmed.Id);
    confirmed.Status = "Confirmed"; deposit.Verified = true; confirmed.Start = demo2.Now.AddHours(10);
    Reject(() => flow2.Cancel(confirmed, owner2.Id, true), "confirmed booking 10 hours away cannot be cancelled");
    Check(confirmed.Status == "Confirmed" && flow2.HoldsReservation(confirmed), "blocked cancellation keeps the confirmed reservation");
    confirmed.Start = demo2.Now.AddDays(3);
    flow2.Cancel(confirmed, owner2.Id, true);
    Check(confirmed.Status == "Cancelled" && confirmed.CancellationFee == 200 && confirmed.RefundDue == Math.Max(0, deposit.Amount - 200),
        "confirmed booking 3 days away cancels with PHP 200 fee and refund of verified paid minus fee");
    Console.WriteLine($"All {checks} cancellation policy checks passed."); return;
}
if (args.Contains("--local-auth-only"))
{
    // Real AccountController, Demo store, memory email codes and a fake Brevo delivery: no external auth provider.
    var demo = new DemoStore(presentationAccounts: true);
    var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Email:BrevoApiKey"] = "synthetic-key", ["Email:From"] = "sender@example.test" }).Build();
    var delivery = new CapturingBrevoDelivery();
    var brevo = new BrevoEmailSender(delivery, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<BrevoEmailSender>.Instance);
    var codes = new EmailVerificationService(cache, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailVerificationService>.Instance, new SyntheticWebEnv(), brevo, new MemoryEmailCodeStore(cache));
    var accountSettings = new AccountSettingsService(demo, Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection()), cache);
    var auth = new RecordingAuthentication();
    var limiter = new LoginAttemptLimiter();
    (AccountController Controller, Microsoft.AspNetCore.Http.DefaultHttpContext Http) Account(ClaimsPrincipal? user = null, string? cookie = null)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Microsoft.AspNetCore.Authentication.IAuthenticationService>(services, auth);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services) };
        if (user != null) http.User = user;
        if (cookie != null) http.Request.Headers.Cookie = cookie;
        var controller = new AccountController(demo, codes, accountSettings, null!, new MemoryInvitationService(TimeProvider.System), limiter)
        { ControllerContext = new ControllerContext { HttpContext = http } };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, new NoTempData());
        controller.Url = new Microsoft.AspNetCore.Mvc.Routing.UrlHelper(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
        return (controller, http);
    }
    string? Error(IActionResult result) => result is ViewResult view ? view.ViewData["Error"] as string : null;
    // Runs the app's real per-request cookie check against a signed-in principal.
    bool SessionStillValid(IStudioStore sessionStore, ClaimsPrincipal principal)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, sessionStore);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services) };
        var scheme = new Microsoft.AspNetCore.Authentication.AuthenticationScheme("Cookies", null, typeof(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationHandler));
        var context = new Microsoft.AspNetCore.Authentication.Cookies.CookieValidatePrincipalContext(http, scheme, new Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions(),
            new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal, "Cookies"));
        GlamByMariya.Web.Security.StudioCookieAuthentication.ValidatePrincipalAsync(context).GetAwaiter().GetResult();
        return context.Principal != null;
    }

    // Registration with email OTP, completed in the same browser.
    var email = "local.customer@example.test";
    var (register, registerHttp) = Account();
    var started = await register.Register("Local", "Customer", email, 0, "Female", "24 Synthetic Street, Quezon City", "FirstPass123", birthday: new DateOnly(1995, 5, 5), acceptTerms: true);
    var issued = System.Text.RegularExpressions.Regex.Match(registerHttp.Response.Headers.SetCookie.ToString(), EmailVerificationService.BrowserCookieName + "=([^;]+)");
    Check(started is RedirectToActionResult { ActionName: "Verify" } && issued.Success && delivery.LastCode != null, "registration sends an email code and sets the browser cookie");
    Check(demo.Users.All(u => u.Email != email), "no account exists before the email code is confirmed");
    var browser = $"{EmailVerificationService.BrowserCookieName}={issued.Groups[1].Value}";
    Check(Error(await Account().Controller.Verify(email, delivery.LastCode!, null)) is { } otherBrowser && otherBrowser.Contains("same browser"), "the code cannot be confirmed from a different browser");
    var verified = await Account(cookie: browser).Controller.Verify(email, delivery.LastCode!, null);
    var localCustomer = demo.Users.SingleOrDefault(u => u.Email == email);
    Check(verified is RedirectResult { Url: "/Bookings" } && localCustomer is { Role: Roles.Customer, IsActive: true } && auth.LastPrincipal?.FindFirstValue(ClaimTypes.Email) == email,
        "the same browser confirms the code, creates the customer and signs in");
    Check(localCustomer!.Birthday == new DateOnly(1995, 5, 5) && localCustomer.Suffix == null && localCustomer.Age >= 30, "the registration birthday is saved and the age is computed from it");
    var (oldBirthday, _) = Account();
    Check(Error(await oldBirthday.Register("Very", "Old", "very.old@example.test", 0, "Male", "24 Synthetic Street, Quezon City", "OldPass123", birthday: new DateOnly(1899, 12, 31), acceptTerms: true))?.Contains("birthday") == true,
        "a birthday before 1900 is rejected with a clear message");
    var accountSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "Controllers", "AccountController.cs"));
    var publicSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Backend", "GlamByMariya.Infrastructure", "Data", "PublicStudioStore.cs"));
    Check(!accountSource.Contains("registration is temporarily unavailable", StringComparison.OrdinalIgnoreCase), "database mode no longer blocks customer registration");
    Check(publicSource.Contains("password_hash, is_active, birthday, suffix)") && publicSource.Contains("{birthday}, {storedSuffix})"), "database registration saves the birthday and suffix");

    // Login.
    Check(Error(await Account().Controller.Login(email, "WrongPass123")) == "Email or password is incorrect.", "a wrong password is rejected");
    Check(await Account().Controller.Login(email, "FirstPass123") is RedirectResult { Url: "/Bookings" }, "the right password signs in");
    var session = auth.LastPrincipal!;

    // Change password ends older sessions.
    Check(Error(await Account().Controller.Login(email, "FirstPass123")) == null, "login works before the password change");
    var (change, _) = Account(session);
    await change.ChangePassword("NotMyPass123", "SecondPass456", "SecondPass456");
    Check(Error(await Account().Controller.Login(email, "SecondPass456")) != null, "a wrong current password does not change the password");
    var signOuts = auth.SignOutCalls;
    Check(await Account(session).Controller.ChangePassword("FirstPass123", "SecondPass456", "SecondPass456") is RedirectToActionResult { ActionName: "Login" } && auth.SignOutCalls == signOuts + 1,
        "the right current password changes the password and signs out");
    Check(Error(await Account().Controller.Login(email, "FirstPass123")) != null && await Account().Controller.Login(email, "SecondPass456") is RedirectResult,
        "only the new password works afterwards");
    Check(!SessionStillValid(demo, session), "the session from before the password change is no longer accepted");
    Check(SessionStillValid(demo, auth.LastPrincipal!), "the new session is accepted");

    // Disabling an account ends its sessions and blocks login.
    var current = auth.LastPrincipal!;
    localCustomer!.IsActive = false;
    Check(!SessionStillValid(demo, current) && Error(await Account().Controller.Login(email, "SecondPass456")) != null, "a disabled account is signed out and cannot log in");
    localCustomer.IsActive = true;

    // Forgot / reset password with the email code.
    Check(await Account().Controller.ForgotPassword(email) is RedirectToActionResult { ActionName: "ResetPassword" } && delivery.LastCode != null, "forgot password emails a reset code");
    var resetCode = delivery.LastCode!;
    Check(Error(Account().Controller.ResetPassword(email, resetCode == "000000" ? "111111" : "000000", "ThirdPass789", "ThirdPass789")) == "That code is incorrect.", "a wrong reset code is rejected");
    Check(Error(Account().Controller.ResetPassword(email, resetCode, "SecondPass456", "SecondPass456"))?.Contains("different password") == true, "reset cannot reuse the current password");

    cache.Remove($"emailcode-cooldown:reset:{email}"); // skip the 60-second resend wait in the test
    await Account().Controller.ForgotPassword(email);
    Check(Account().Controller.ResetPassword(email, delivery.LastCode!, "ThirdPass789", "ThirdPass789") is RedirectToActionResult { ActionName: "Login" }, "a new reset code sets a new password");
    Check(await Account().Controller.Login(email, "ThirdPass789") is RedirectResult && Error(await Account().Controller.Login(email, "SecondPass456")) != null, "only the reset password works");
    Console.WriteLine($"All {checks} local authentication checks passed."); return;
}
if (args.Contains("--accounts-only"))
{
    // Step 7: owner setup, admin invitations and admin Disable/Enable, with local accounts only.
    var root = Directory.GetCurrentDirectory();
    var setupSource = File.ReadAllText(Path.Combine(root, "Backend", "GlamByMariya.Infrastructure", "Data", "DatabaseSetup.cs"));
    Check(!setupSource.Contains("Provider-backed SuperAdmin setup is unavailable") && !setupSource.Contains("not ready for provider-backed deployment"),
        "owner setup works with Supabase Storage settings present (local login only, no auth-provider block)");
    Check(setupSource.Contains("SqlMigrator.ApplyAsync") && !setupSource.Contains("MigrateAsync") && !Directory.Exists(Path.Combine(root, "Backend", "GlamByMariya.Infrastructure", "Migrations", "Public")),
        "migrate command uses the SQL migrator, not EF");

    // Invitations: the link's own token picks the invitation, never an older expired one.
    var clock = new ManualClock(new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero));
    var invites = new MemoryInvitationService(clock);
    var inviter = Guid.NewGuid();
    var (_, oldToken) = invites.InviteWithToken("new.admin@example.test", inviter);
    clock.Now = clock.Now.AddHours(73);
    var (_, freshToken) = invites.InviteWithToken("new.admin@example.test", inviter);
    try { invites.Accept("new.admin@example.test", oldToken); throw new Exception("Expected rejection: expired invitation"); }
    catch (InvalidOperationException e) { Check(e.Message.Contains("expired"), "an expired invitation link says it expired"); }
    Check(invites.Accept("New.Admin@example.test ", freshToken).AcceptedAt != null, "a new invitation works even when an older expired one exists for the same email");
    Reject(() => invites.Accept("new.admin@example.test", freshToken), "an invitation link is single use");
    var (_, otherToken) = invites.InviteWithToken("someone@example.test", inviter);
    Reject(() => invites.Accept("new.admin@example.test", otherToken), "an invitation token only works for its own email");
    Reject(() => invites.Accept("someone@example.test", "WRONG"), "a wrong invitation token is rejected");

    // Real AccountController accepts the invite and creates a local Admin account.
    var demo = new DemoStore(presentationAccounts: true);
    var owner = demo.CreateElevatedUser("Store", "Owner", "store.owner@example.test", "OwnerPass123", Roles.SuperAdmin, 35, "", "Address on file");
    var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Email:BrevoApiKey"] = "synthetic-key", ["Email:From"] = "sender@example.test" }).Build();
    var brevo = new BrevoEmailSender(new CapturingBrevoDelivery(), config, Microsoft.Extensions.Logging.Abstractions.NullLogger<BrevoEmailSender>.Instance);
    var codes = new EmailVerificationService(cache, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailVerificationService>.Instance, new SyntheticWebEnv(), brevo, new MemoryEmailCodeStore(cache));
    var accountSettings = new AccountSettingsService(demo, Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection()), cache);
    var auth = new RecordingAuthentication();
    var controllerInvites = new MemoryInvitationService(TimeProvider.System);
    Microsoft.AspNetCore.Http.DefaultHttpContext Http(ClaimsPrincipal? user = null)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Microsoft.AspNetCore.Authentication.IAuthenticationService>(services, auth);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IStudioStore>(services, demo);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services) };
        if (user != null) http.User = user;
        return http;
    }
    T Wire<T>(T controller, Microsoft.AspNetCore.Http.DefaultHttpContext http) where T : Controller
    {
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, new NoTempData());
        controller.Url = new Microsoft.AspNetCore.Mvc.Routing.UrlHelper(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
        return controller;
    }
    AccountController Account() => Wire(new AccountController(demo, codes, accountSettings, null!, controllerInvites, new LoginAttemptLimiter()), Http());
    ClaimsPrincipal As(AppUser u) => new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()), new Claim(ClaimTypes.Email, u.Email),
        new Claim(ClaimTypes.Role, u.Role), new Claim("account_version", AccountSession.Version(u.PasswordHash)) }, "Cookies"));
    AdminController Admin(AppUser u) => Wire(new AdminController(demo, null!, null!, controllerInvites, null!), Http(As(u)));
    bool SessionValid(ClaimsPrincipal principal)
    {
        var scheme = new Microsoft.AspNetCore.Authentication.AuthenticationScheme("Cookies", null, typeof(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationHandler));
        var context = new Microsoft.AspNetCore.Authentication.Cookies.CookieValidatePrincipalContext(Http(), scheme, new Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions(),
            new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal, "Cookies"));
        GlamByMariya.Web.Security.StudioCookieAuthentication.ValidatePrincipalAsync(context).GetAwaiter().GetResult();
        return context.Principal != null;
    }

    var (_, inviteToken) = controllerInvites.InviteWithToken("invited.admin@example.test", owner.Id);
    var page = Account().AcceptInvite("invited.admin@example.test", inviteToken) as ViewResult;
    Check(page != null && page.ViewData["AcceptanceUnavailable"] == null && page.ViewData["Error"] == null, "the invitation page shows the setup form");
    var accepted = await Account().AcceptInvite("invited.admin@example.test", inviteToken, "Invited", "Admin", 30, "Female", "24 Synthetic Street, Quezon City", "AdminPass123");
    var invited = demo.Users.SingleOrDefault(u => u.Email == "invited.admin@example.test");
    Check(accepted is RedirectResult { Url: "/Admin" } && invited is { Role: Roles.Admin, IsActive: true } && auth.LastPrincipal?.FindFirstValue(ClaimTypes.Role) == Roles.Admin,
        "accepting the invitation creates a local Admin account and signs in");
    Check(await Account().Login("invited.admin@example.test", "AdminPass123") is RedirectResult, "the invited admin signs in with the local password");
    var reuse = await Account().AcceptInvite("invited.admin@example.test", inviteToken, "Invited", "Admin", 30, "Female", "24 Synthetic Street, Quezon City", "AdminPass123");
    Check(demo.Users.Count(u => u.Email == "invited.admin@example.test") == 1 && reuse is ViewResult, "a used invitation link cannot create a second account");
    var badToken = await Account().AcceptInvite("other.admin@example.test", "WRONG", "Other", "Admin", 30, "Female", "24 Synthetic Street, Quezon City", "AdminPass123");
    Check(badToken is ViewResult && demo.Users.All(u => u.Email != "other.admin@example.test"), "a wrong invitation token creates no account");

    // Disable / Enable: SuperAdmin only, Admin targets only, takes effect on the next request.
    var adminSession = auth.LastPrincipal!;
    var setActive = typeof(AdminController).GetMethod(nameof(AdminController.SetAdminActive))!;
    Check(setActive.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Any(a => a.Roles == Roles.SuperAdmin)
        && setActive.GetCustomAttributes(typeof(HttpPostAttribute), true).Any(), "Disable/Enable is a POST action limited to SuperAdmin");
    Check(SessionValid(adminSession), "the admin session is valid before disabling");
    Admin(owner).SetAdminActive(invited!.Id, false);
    Check(!invited.IsActive && !SessionValid(adminSession), "a disabled admin is signed out on the next request");
    Check(await Account().Login("invited.admin@example.test", "AdminPass123") is ViewResult, "a disabled admin cannot sign in");
    Admin(owner).SetAdminActive(invited.Id, true);
    Check(invited.IsActive && await Account().Login("invited.admin@example.test", "AdminPass123") is RedirectResult, "an enabled admin can sign in again");
    var accountCustomer = demo.Users.First(u => u.Role == Roles.Customer);
    Reject(() => demo.SetAdminActive(owner.Id, false, owner.Id), "the owner cannot disable a SuperAdmin (including themself)");
    Reject(() => demo.SetAdminActive(accountCustomer.Id, false, owner.Id), "Disable/Enable does not apply to customers");
    Reject(() => demo.SetAdminActive(owner.Id, false, invited.Id), "an Admin cannot disable anyone");
    Admin(owner).SetAdminActive(accountCustomer.Id, false);
    Check(accountCustomer.IsActive, "a refused Disable changes nothing");
    var usersView = File.ReadAllText(Path.Combine(root, "Frontend", "GlamByMariya.Web", "Views", "Admin", "Users.cshtml"));
    Check(usersView.Contains("asp-action=\"SetAdminActive\"") && !usersView.Contains("Invitation acceptance is currently unavailable"), "the Users page has the Disable/Enable button and no longer says invites are closed");
    // No seeded demo accounts: the owner comes from the setup command, admins by invite, customers register.
    var empty = new DemoStore();
    Check(empty.Users.Count == 0 && empty.Bookings.Count == 0 && empty.Payments.Count == 0, "a new Demo store starts with no accounts and no sample booking");
    foreach (var file in new[] { "Backend/GlamByMariya.Infrastructure/Data/DemoStore.cs", "Backend/GlamByMariya.Infrastructure/Integrations/DemoAvatarStore.cs", "Frontend/GlamByMariya.Web/MakeupBookingDatabase.sql" })
        Check(!File.ReadAllText(Path.Combine(root, file)).Contains("lumiere.demo"), $"{Path.GetFileName(file)} has no @lumiere.demo account");

    // A double click on "Create account" posts the form twice. The second post must lead to the
    // code page too, not back to an empty sign-up form with a "wait a minute" error.
    async Task<IActionResult> SignUp() => await Account().Register("Double", "Click", "double.click@example.test", 0, "Female",
        "24 Synthetic Street, Quezon City", "Secret123!", birthday: new DateOnly(1995, 5, 5), acceptTerms: true);

    // Sign-up needs the customer to agree to the Terms and Conditions; the server checks it too.
    var noTerms = await Account().Register("No", "Terms", "no.terms@example.test", 0, "Female",
        "24 Synthetic Street, Quezon City", "Secret123!", birthday: new DateOnly(1995, 5, 5));
    Check(noTerms is ViewResult && (string?)((ViewResult)noTerms).ViewData["Error"] is { } termsError && termsError.Contains("Terms and Conditions")
        && !codes.HasPendingRegistration("no.terms@example.test"), "sign-up without agreeing to the Terms is refused and sends no code");
    var signUpView = File.ReadAllText(Path.Combine(root, "Frontend", "GlamByMariya.Web", "Views", "Account", "Login.cshtml"));
    Check(signUpView.Contains("name=\"acceptTerms\"") && signUpView.Contains("asp-action=\"Terms\""), "the sign-up form has the Terms checkbox and links to the Terms page");
    var homeTerms = typeof(GlamByMariya.Web.Controllers.HomeController).GetMethod("Terms");
    var termsView = Path.Combine(root, "Frontend", "GlamByMariya.Web", "Views", "Home", "Terms.cshtml");
    Check(homeTerms != null && File.Exists(termsView), "there is a public Terms and Conditions page");
    var termsText = File.Exists(termsView) ? File.ReadAllText(termsView) : "";
    Check(new[] { "DownPaymentPercent", "30 minutes", "₱200", "24 hours", "Transportation" }.All(termsText.Contains), "the Terms page states the deposit, payment window, cancellation and transport rules");
    Check(File.ReadAllText(Path.Combine(root, "Frontend", "GlamByMariya.Web", "Views", "Shared", "_PublicFooter.cshtml")).Contains("asp-action=\"Terms\"")
        && File.ReadAllText(Path.Combine(root, "Frontend", "GlamByMariya.Web", "PublicOnlySite.cs")).Contains("\"/Home/Terms\""), "the footer links the Terms page and the public-only site allows it");
    var firstPost = await SignUp();
    var secondPost = await SignUp();
    Check(firstPost is RedirectToActionResult { ActionName: "Verify" }, "signing up sends the customer to the code page");
    Check(secondPost is RedirectToActionResult { ActionName: "Verify" }, "a repeated sign-up post right after the first also goes to the code page");
    var siteJs = File.ReadAllText(Path.Combine(root, "Frontend", "GlamByMariya.Web", "wwwroot", "js", "site.js"));
    Check(siteJs.Contains("data-submit-guard"), "site.js stops a form from being submitted twice");
    Console.WriteLine($"All {checks} account checks passed."); return;
}
if (args.Contains("--catalog-settings-only"))
{
    // Step 8: catalog edits and business-hours changes, checked through the real AdminController.
    var demo = new DemoStore(presentationAccounts: true);
    var owner = demo.Users.Single(u => u.Role == Roles.SuperAdmin);
    AdminController Admin(Dictionary<string, Microsoft.Extensions.Primitives.StringValues>? form = null)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, owner.Id.ToString()), new Claim(ClaimTypes.Role, owner.Role) }, "Cookies"));
        http.Request.Form = new Microsoft.AspNetCore.Http.FormCollection(form ?? new());
        var controller = new AdminController(demo, new BookingService(demo, demo, demo, demo, demo), null!, new MemoryInvitationService(TimeProvider.System), null!)
        { ControllerContext = new ControllerContext { HttpContext = http } };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, new NoTempData());
        return controller;
    }

    // 8a: editing an archived item keeps it archived.
    var archived = demo.Services[1];
    archived.Active = false; archived.Archived = true;
    await Admin().SaveCatalog(archived.Id, "service", "Glamour Makeup (old)", "Updated note", archived.Price, archived.Minutes, false);
    Check(archived.Name == "Glamour Makeup (old)" && archived.Archived && !archived.Active, "editing an archived item saves the change and keeps it archived");
    await Admin().SaveCatalog(archived.Id, "service", "Glamour Makeup (old)", "Updated note", archived.Price, archived.Minutes, true);
    Check(archived.Archived && demo.Services.Where(x => x.Active && !x.Archived).All(x => x.Id != archived.Id), "an archived item stays hidden from customers even if Active is ticked");
    await Admin().SaveCatalog(null, "addon", "Brow shaping", "Neat brows", 150, 10, true);
    var created = demo.AddOns.Single(x => x.Name == "Brow shaping");
    Check(created.Active && !created.Archived, "a new item starts active and not archived");
    // 8b: changing hours warns about upcoming bookings that would fall outside them.
    demo.Settings.EarlyFeeEnabled = false; demo.Settings.LateFeeEnabled = false;
    var held = demo.Bookings.Single(); // Pending Payment, two days ahead at 10:00, 60 minutes
    var percent = demo.Settings.DownPaymentPercent; var areas = demo.Settings.ServiceAreas;
    var refused = Admin();
    refused.Settings(11, 18, demo.Settings.BufferMinutes, demo.Settings.TravelFee, percent, areas, "");
    Check(demo.Settings.OpenHour == 8 && (refused.TempData["Error"] as string ?? "").Contains(held.Reference),
        "new hours that leave an upcoming booking outside are not saved, and the booking is named");
    var confirmed = Admin();
    confirmed.Settings(11, 18, demo.Settings.BufferMinutes, demo.Settings.TravelFee, percent, areas, "", confirmAffected: true);
    Check(demo.Settings.OpenHour == 11 && held.Start.Hour == 10 && (confirmed.TempData["Success"] as string ?? "").Contains("1 upcoming booking"),
        "with Save anyway the hours change, the booking keeps its time, and the owner is told to contact the customer");
    var harmless = Admin();
    harmless.Settings(8, 17, demo.Settings.BufferMinutes, demo.Settings.TravelFee, percent, areas, "");
    Check(demo.Settings.OpenHour == 8 && demo.Settings.CloseHour == 17 && harmless.TempData["Error"] == null, "hours that affect no booking save straight away");
    held.Status = "Cancelled";
    var closed = Admin();
    closed.Settings(12, 17, demo.Settings.BufferMinutes, demo.Settings.TravelFee, percent, areas, "");
    Check(demo.Settings.OpenHour == 12 && closed.TempData["Error"] == null, "cancelled bookings do not block an hours change");
    Console.WriteLine($"All {checks} catalog and settings checks passed."); return;
}
if (args.Contains("--travel-admin-only"))
{
    // Task 5: the owner's travel settings, artist location actions, travel lines and override warning.
    var adminDemo = new DemoStore(presentationAccounts: true);
    var adminOwner = adminDemo.Users.Single(u => u.Role == Roles.SuperAdmin);
    AdminController AdminAs(Guid userId, string role)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, role) }, "Cookies"));
        var controller = new AdminController(adminDemo, new BookingService(adminDemo, adminDemo, adminDemo, adminDemo, adminDemo), null!, new MemoryInvitationService(TimeProvider.System), null!)
        { ControllerContext = new ControllerContext { HttpContext = http } };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, new NoTempData());
        return controller;
    }
    var adminSettings = adminDemo.Settings;
    adminDemo.Settings.EarlyFeeEnabled = false; adminDemo.Settings.LateFeeEnabled = false;
    var pct = adminSettings.DownPaymentPercent; var serviceAreas = adminSettings.ServiceAreas;
    var saveOk = AdminAs(adminOwner.Id, Roles.SuperAdmin);
    saveOk.Settings(adminSettings.OpenHour, adminSettings.CloseHour, adminSettings.BufferMinutes, adminSettings.TravelFee, pct, serviceAreas, "", false, 20m, 45);
    Check(adminSettings.TravelSpeedKmh == 20m && adminSettings.TravelMinimumMinutes == 45 && saveOk.TempData["Error"] == null, "settings save a 20 km/h speed and a 45 minute minimum");
    var tooFast = AdminAs(adminOwner.Id, Roles.SuperAdmin);
    tooFast.Settings(adminSettings.OpenHour, adminSettings.CloseHour, adminSettings.BufferMinutes, adminSettings.TravelFee, pct, serviceAreas, "", false, 70m, 30);
    Check(adminSettings.TravelSpeedKmh == 20m && tooFast.TempData["Error"] as string == "Travel speed must be between 5 and 60 km/h.", "a 70 km/h speed is refused with a friendly message and nothing changes");
    var tooLong = AdminAs(adminOwner.Id, Roles.SuperAdmin);
    tooLong.Settings(adminSettings.OpenHour, adminSettings.CloseHour, adminSettings.BufferMinutes, adminSettings.TravelFee, pct, serviceAreas, "", false, 20m, 200);
    Check(adminSettings.TravelMinimumMinutes == 45 && tooLong.TempData["Error"] as string == "Minimum travel must be between 0 and 180 minutes.", "a 200 minute minimum is refused with a friendly message and nothing changes");
    var tooSlow = AdminAs(adminOwner.Id, Roles.SuperAdmin);
    tooSlow.Settings(adminSettings.OpenHour, adminSettings.CloseHour, adminSettings.BufferMinutes, adminSettings.TravelFee, pct, serviceAreas, "", false, 4m, 30);
    Check(adminSettings.TravelSpeedKmh == 20m && tooSlow.TempData["Error"] != null, "a 4 km/h speed is refused");

    // Artist location actions
    var locatePost = AdminAs(adminOwner.Id, Roles.SuperAdmin);
    locatePost.SetArtistHome(14.6760m, 121.0437m);
    Check(adminDemo.GetArtistLocation() is { HomeLat: 14.6760m, HomeLng: 121.0437m } && locatePost.TempData["Error"] == null, "the owner saves the home base");
    locatePost.SetArtistCurrent(14.5547m, 121.0244m);
    Check(adminDemo.GetArtistLocation() is { CurrentLat: 14.5547m, CurrentUpdatedAt: not null }, "the owner saves the current location");
    var outsidePost = AdminAs(adminOwner.Id, Roles.SuperAdmin);
    outsidePost.SetArtistCurrent(15.5m, 121.0437m);
    Check(outsidePost.TempData["Error"] as string == BookingLocation.ArtistMessage && adminDemo.GetArtistLocation().CurrentLat == 14.5547m, "a current location outside Metro Manila is refused and the old one stays");
    var outsideHome = AdminAs(adminOwner.Id, Roles.SuperAdmin);
    outsideHome.SetArtistHome(10m, 121m);
    Check(outsideHome.TempData["Error"] != null && adminDemo.GetArtistLocation().HomeLat == 14.6760m, "a home base outside Metro Manila is refused");
    var blankPost = AdminAs(adminOwner.Id, Roles.SuperAdmin);
    blankPost.SetArtistCurrent(null, null);
    Check(blankPost.TempData["Error"] != null && adminDemo.GetArtistLocation().CurrentLat == 14.5547m, "a blank post does not clear the current location");
    var backHomePost = AdminAs(adminOwner.Id, Roles.SuperAdmin);
    backHomePost.SetArtistCurrent(null, null, backHome: true);
    Check(adminDemo.GetArtistLocation() is { CurrentLat: null, CurrentUpdatedAt: null, HomeLat: 14.6760m }, "Back to home base clears only the current location");
    Check(backHomePost.TempData["Success"] as string == "The artist is back at home base.", "with a home base saved, the owner is told the artist is back home");
    {
        // No home base yet: the button reads "Clear current location" and the owner is told to set a home base.
        var noHomeDemo = new DemoStore(presentationAccounts: true);
        var noHomeOwner = noHomeDemo.Users.Single(u => u.Role == Roles.SuperAdmin);
        AdminController NoHomeAs()
        {
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, noHomeOwner.Id.ToString()), new Claim(ClaimTypes.Role, Roles.SuperAdmin) }, "Cookies"));
            var controller = new AdminController(noHomeDemo, new BookingService(noHomeDemo, noHomeDemo, noHomeDemo, noHomeDemo, noHomeDemo), null!, new MemoryInvitationService(TimeProvider.System), null!)
            { ControllerContext = new ControllerContext { HttpContext = http } };
            controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, new NoTempData());
            return controller;
        }
        Check(noHomeDemo.GetArtistLocation().HomeLat == null, "a fresh demo starts with no home base");
        NoHomeAs().SetArtistCurrent(14.5547m, 121.0244m);
        var clear = NoHomeAs();
        clear.SetArtistCurrent(null, null, backHome: true);
        Check(noHomeDemo.GetArtistLocation() is { CurrentLat: null, HomeLat: null } && clear.TempData["Success"] as string == AdminController.NoHomeBaseMessage,
            "without a home base, clearing the current location says no home base is saved yet");
    }
    var adminStaff = AdminAs(adminDemo.Users.First(u => u.Role == Roles.Admin).Id, Roles.Admin);
    adminStaff.SetArtistCurrent(14.5547m, 121.0244m);
    Check(adminDemo.GetArtistLocation().CurrentLat == 14.5547m, "an Admin can also save the artist's location");

    // Role and antiforgery gates
    var adminGate = typeof(AdminController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single();
    Check(adminGate.Roles!.Split(',').Select(r => r.Trim()).OrderBy(r => r).SequenceEqual(new[] { Roles.Admin, Roles.SuperAdmin }.OrderBy(r => r)), "the admin controller admits only Admin and SuperAdmin, so a customer gets 403");
    foreach (var action in new[] { nameof(AdminController.SetArtistHome), nameof(AdminController.SetArtistCurrent) })
    {
        var method = typeof(AdminController).GetMethod(action)!;
        Check(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), true).Length == 1
            && method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute), true).Length == 1
            && method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true).Length == 0
            && method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().All(a => a.Roles == "Admin,SuperAdmin"), $"{action} is POST-only, antiforgery-protected and limited to Admin and SuperAdmin");
    }

    // Travel lines
    var lineSettings = new BusinessSettings { TravelSpeedKmh = 15m, TravelMinimumMinutes = 30 };
    Booking LineBooking(int hour, decimal? lat, decimal? lng) => new() { Start = new DateTime(2030, 1, 7, hour, 0, 0), Latitude = lat, Longitude = lng, Service = new(Guid.NewGuid(), "Look", 100m, 60) };
    var lineA = LineBooking(9, 14.5547m, 121.0244m); var lineB = LineBooking(13, 14.7046m, 121.0567m); var lineC = LineBooking(16, null, null);
    var travelLines = GlamByMariya.Web.ViewModels.AdminTravelLines.For(new[] { lineC, lineA, lineB }, lineSettings);
    Check(travelLines.Count == 2 && !travelLines.ContainsKey(lineA.Id) && travelLines[lineB.Id] == "≈ 90 min travel", "the travel line between two pinned bookings is computed from the settings");
    Check(travelLines[lineC.Id] == "≈ 30 min (no pin)", "a booking without a pin shows the minimum and (no pin)");
    lineSettings.TravelSpeedKmh = 60m;
    Check(GlamByMariya.Web.ViewModels.AdminTravelLines.For(new[] { lineA, lineB }, lineSettings)[lineB.Id] == "≈ 30 min travel", "the travel line follows the current speed");
    Check(GlamByMariya.Web.ViewModels.AdminTravelLines.Ago(adminDemo.Now.AddMinutes(-5), adminDemo.Now) == "5 minutes ago" && GlamByMariya.Web.ViewModels.AdminTravelLines.Ago(adminDemo.Now.AddHours(-3), adminDemo.Now) == "3 hours ago", "the current-location age reads in minutes and hours");
    Check(GlamByMariya.Web.ViewModels.AdminTravelLines.Ago(adminDemo.Now.AddMinutes(-1), adminDemo.Now) == "1 minute ago" && GlamByMariya.Web.ViewModels.AdminTravelLines.Ago(adminDemo.Now.AddHours(-1), adminDemo.Now) == "1 hour ago" && GlamByMariya.Web.ViewModels.AdminTravelLines.Ago(adminDemo.Now.AddDays(-1), adminDemo.Now) == "1 day ago" && GlamByMariya.Web.ViewModels.AdminTravelLines.Ago(adminDemo.Now.AddDays(-3), adminDemo.Now) == "3 days ago" && GlamByMariya.Web.ViewModels.AdminTravelLines.Ago(adminDemo.Now.AddSeconds(-20), adminDemo.Now) == "just now", "the age uses full words with singular and plural");

    // View sources
    var adminWeb = Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web");
    string AdminView(params string[] parts) => File.ReadAllText(Path.Combine([adminWeb, .. parts]));
    var dayView = AdminView("Views", "Admin", "_AppointmentCalendar.cshtml");
    Check(dayView.Contains("AdminTravelLines.For(") && dayView.Contains("data-travel-line"), "the day view renders the travel line between bookings");
    Check(!dayView.Contains("travel is not estimated") && !dayView.Contains("Travel time is not included"), "the day view no longer says single-booking travel is not estimated");
    Check(dayView.Contains("mixed bookings have no pin and are not travel-checked"), "the day view keeps accurate wording for mixed bookings");
    Check(!AdminView("Views", "Admin", "Calendar.cshtml").Contains("Travel time is not estimated"), "the calendar page no longer says travel is not estimated");
    var detailsView = AdminView("Views", "Admin", "Details.cshtml");
    Check(!detailsView.Contains("travel not estimated"), "booking details no longer say travel is not estimated");
    Check(AdminView("Views", "Admin", "_MixedBookingDetails.cshtml").Contains("Travel feasibility is not verified."), "mixed booking details keep their accurate wording");
    var overrideAt = detailsView.IndexOf("I'm sure the artist can make it", StringComparison.Ordinal);
    var superAdminAt = detailsView.LastIndexOf("User.IsInRole(\"SuperAdmin\")", overrideAt, StringComparison.Ordinal);
    var elseAt = detailsView.IndexOf("Only SuperAdmin may approve this request.", overrideAt, StringComparison.Ordinal);
    Check(overrideAt > 0 && superAdminAt > 0 && elseAt > overrideAt && !detailsView[superAdminAt..overrideAt].Contains("else {") && detailsView.Contains("name=\"travelOverride\""), "the override checkbox sits only inside the SuperAdmin approval form");
    Check(detailsView.Contains("Travel warning."), "the reschedule approval shows the travel warning");
    var settingsView = AdminView("Views", "Admin", "Settings.cshtml");
    var locationView = AdminView("Views", "Admin", "_ArtistLocation.cshtml");
    Check(locationView.Contains("location.HomeLat.HasValue ? \"Back to home base\" : \"Clear current location\""), "the card says \"Clear current location\" when no home base is saved");
    Check(locationView.Contains("No current location either, so the first booking of the day uses the minimum travel time."), "with no pins at all, the card says the minimum travel time is used");
    var settingsAction = AdminView("Controllers", "AdminController.cs");
    Check(locationView.Contains("Artist location is unavailable right now.") && settingsAction.Contains("ViewBag.ArtistLocation = null;"), "a misconfigured artist only blanks the location card, not the Settings page");
    Check(settingsAction.Contains("LogError(e, \"Artist location could not be loaded for Settings.\")"), "the server log records why the artist location card is unavailable");
    var searchPathMigration = Path.Combine(Directory.GetCurrentDirectory(), "database", "migrations", "0017_function_search_path.sql");
    var searchPathSql = File.Exists(searchPathMigration) ? File.ReadAllText(searchPathMigration) : "";
    foreach (var fn in new[] { "mvc_actor", "mvc_immutable", "mvc_touch" })
        Check(searchPathSql.Contains($"ALTER FUNCTION public.{fn}() SET search_path = pg_catalog, public;"), $"migration 0017 pins the search_path of {fn}");
    Check(settingsView.Contains("name=\"travelSpeedKmh\"") && settingsView.Contains("name=\"travelMinimumMinutes\"") && settingsView.Contains("<partial name=\"_ArtistLocation\" />"), "the settings page has the travel fields and the artist location card");
    foreach (var label in new[] { "Use my current location", "Save as current location", "Set as home base", "Back to home base", "Current location set" })
        Check(locationView.Contains(label), $"the artist location card offers \"{label}\"");
    Check(File.Exists(Path.Combine(adminWeb, "wwwroot", "js", "artist-location.js")), "the artist location map script exists");
    var artistMapScript = File.ReadAllText(Path.Combine(adminWeb, "wwwroot", "js", "artist-location.js"));
    Check(locationView.Contains("data-artist-location-query") && locationView.Contains("data-artist-location-search") && locationView.Contains("aria-label=\"Search a place or address\""),
        "the artist location card has a labelled place search");
    Check(artistMapScript.Contains("https://nominatim.openstreetmap.org/search?format=json&limit=1&countrycodes=ph&q=") && artistMapScript.Contains("lastSearch + 1000 > Date.now()"),
        "the artist search uses Nominatim limited to the Philippines, at most once a second");
    Check(artistMapScript.Contains("No match found. Try another name, or drag the pin.") && artistMapScript.Contains("Search is busy right now. Try again in a moment, or drag the pin."),
        "the artist search says when nothing matched or the search is busy");
    Check(artistMapScript.Contains("event.key !== 'Enter'") && artistMapScript.Contains("event.preventDefault()"), "Enter in the artist search box searches instead of submitting a form");
    Console.WriteLine($"All {checks} travel admin checks passed."); return;
}
if (args.Contains("--storage-key-only"))
{
    // A storage key pasted with a trailing new line (or spaces) still works.
    var seen = new List<string>();
    var storageConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["FileStorage:Provider"] = "Supabase", ["Supabase:Url"] = " https://example.supabase.co/\n", ["SupabaseStorage:ServiceKey"] = "sb_secret_example\r\n",
    }).Build();
    var factory = new RecordingHttpFactory(seen);
    var files = new GlamByMariya.Infrastructure.Integrations.SupabaseAppFileStore(factory, storageConfig, Microsoft.Extensions.Logging.Abstractions.NullLogger<GlamByMariya.Infrastructure.Integrations.SupabaseAppFileStore>.Instance);
    var saveError = "";
    try { files.Save("CatalogImages/test.jpg", [1, 2, 3]); } catch (Exception e) { saveError = e.GetType().Name + ": " + e.Message; }
    Check(saveError == "" && seen.Count == 1 && seen[0] == "https://example.supabase.co/storage/v1/object/app-files/CatalogImages/test.jpg apikey=sb_secret_example auth=",
        "a file upload trims a pasted storage key and URL" + (saveError == "" ? "" : " (" + saveError + ")") + (seen.Count > 0 ? " [" + seen[0] + "]" : ""));
    // A key split across lines when pasted (new line inside it) still works: real keys contain no whitespace.
    seen.Clear();
    var splitConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["FileStorage:Provider"] = "Supabase", ["Supabase:Url"] = "https://example.supabase.co", ["SupabaseStorage:ServiceKey"] = "sb_secret_exa\r\nmple ",
    }).Build();
    var splitFiles = new GlamByMariya.Infrastructure.Integrations.SupabaseAppFileStore(factory, splitConfig, Microsoft.Extensions.Logging.Abstractions.NullLogger<GlamByMariya.Infrastructure.Integrations.SupabaseAppFileStore>.Instance);
    var splitError = "";
    try { splitFiles.Save("CatalogImages/test.jpg", [1, 2, 3]); } catch (Exception e) { splitError = e.GetType().Name + ": " + e.Message; }
    Check(splitError == "" && seen.Count == 1 && seen[0].EndsWith("apikey=sb_secret_example auth="), "a storage key with a new line inside it still uploads" + (splitError == "" ? "" : " (" + splitError + ")"));
    Check(GlamByMariya.Infrastructure.Integrations.StorageKey.Clean(" sb_secret_a\nb\tc ") == "sb_secret_abc" && GlamByMariya.Infrastructure.Integrations.StorageKey.Clean(null) == "", "storage keys drop every whitespace character");
    var avatarSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Backend", "GlamByMariya.Infrastructure", "Integrations", "AvatarStorageService.cs"));
    Check(avatarSource.Contains("StorageKey.Clean(config[\"SupabaseStorage:ServiceKey\"])") && avatarSource.Contains("(config[\"Supabase:Url\"] ?? \"\").Trim().TrimEnd('/')"), "profile picture uploads trim the storage key and URL too");
    Console.WriteLine($"All {checks} storage key checks passed."); return;
}
if (args.Contains("--website-content-live-only"))
{
    // Database mode registers the website content repository and services.
    var programSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "Program.cs")).Replace("\r\n", "\n");
    var dbStart = programSource.IndexOf("builder.Services.AddSingleton<PostgresWebsiteContentRepository>", StringComparison.Ordinal);
    if (dbStart >= 0) dbStart = programSource.LastIndexOf("if (connection != null && schema == \"public\")\n{", dbStart, StringComparison.Ordinal);
    Check(dbStart >= 0, "a database-only block registers PostgresWebsiteContentRepository");
    var dbBlock = dbStart < 0 ? "" : programSource[dbStart..programSource.IndexOf("var app = builder.Build();", dbStart, StringComparison.Ordinal)];
    foreach (var iface in new[] { "IWebsiteContentRepository", "IWebsiteImageRepository", "IWebsitePublishedAssetLookup", "IWebsiteDraftAssetLookup", "IWebsiteAssetWriter" })
        Check(dbBlock.Contains("AddSingleton<" + iface + ">(sp => sp.GetRequiredService<PostgresWebsiteContentRepository>())"), iface + " is backed by the shared repository singleton");
    Check(dbBlock.Contains("AddSingleton<WebsiteContentService>()") && dbBlock.Contains("AddSingleton<WebsiteImageService>()"), "content and image services are singletons");
    Check(dbBlock.Contains("AddScoped<WebsitePublicImageService>()") && dbBlock.Contains("AddScoped<WebsiteDraftImageService>()"), "file-store image services are scoped");
    Check(programSource.Contains("builder.Services.AddSingleton<IWebsiteContentRepository, DemoWebsiteContentRepository>();") && programSource.Contains("new DemoFileWebsiteContentRepository(fullRoot)"), "demo registrations are unchanged");
    Check(dbBlock.Contains("AddScoped<WebsiteImageUploadService>()"), "the photo upload service is scoped (it uses the scoped file store)");

    // Task 4: owner uploads and removes photos on the live draft.
    var uploadOwner = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "SuperAdmin") }, "Cookies"));
    var uploadAdmin = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Admin") }, "Cookies"));
    var photo = File.ReadAllBytes(Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "wwwroot", "images", "looks", "soft-glam.jpg"));
    var keyPattern = new System.Text.RegularExpressions.Regex(@"\Awebsite-content/[0-9a-f]{32}\.(jpg|png)\z");
    var draftId = Guid.NewGuid();
    var cleanupLog = new CapturingLogger<GlamByMariya.Infrastructure.Integrations.WebsiteImageUploadService>();
    (FakeWebsiteImages Images, FakeAssetWriter Assets, RecordingFileStore Files, GlamByMariya.Infrastructure.Integrations.WebsiteImageUploadService Upload) Uploads()
    {
        var fakeImages = new FakeWebsiteImages(); var fakeAssets = new FakeAssetWriter(); var fakeFiles = new RecordingFileStore();
        return (fakeImages, fakeAssets, fakeFiles, new GlamByMariya.Infrastructure.Integrations.WebsiteImageUploadService(fakeImages, fakeAssets, fakeFiles, cleanupLog));
    }
    async Task<Exception?> Fails(Func<Task> action) { try { await action(); return null; } catch (Exception e) { return e; } }

    var up = Uploads();
    await up.Upload.Save(uploadOwner, draftId, up.Images.Revision, "hero", "Bride with soft glam", null, photo, default);
    Check(up.Files.Saved.Count == 1 && keyPattern.IsMatch(up.Files.Saved[0]) && up.Images.List.Count(x => x.Role == "hero") == 1, "owner saves a hero photo under a website-content key");
    Check(up.Assets.Added.Single().Key == up.Files.Saved[0] && up.Images.List.Single().AssetId == up.Assets.Added.Single().Id, "the hero points at the new asset");
    await up.Upload.Save(uploadOwner, draftId, up.Images.Revision, "hero", "Second hero", "", photo, default);
    Check(up.Images.List.Count(x => x.Role == "hero") == 1 && up.Images.List.Single(x => x.Role == "hero").AltText == "Second hero", "a second hero replaces the first");

    up = Uploads();
    Check(await Fails(() => up.Upload.Save(uploadAdmin, draftId, up.Images.Revision, "hero", "Alt", null, photo, default)) is UnauthorizedAccessException && up.Files.Saved.Count == 0, "an Admin who is not SuperAdmin cannot upload photos");
    Check(await Fails(() => up.Upload.Save(uploadOwner, draftId, up.Images.Revision, "hero", "Alt", null, "not an image"u8.ToArray(), default)) != null && up.Files.Saved.Count == 0, "bytes that are not an image are refused before storage");

    up = Uploads(); up.Images.FailReplace = true;
    Check(await Fails(() => up.Upload.Save(uploadOwner, draftId, up.Images.Revision, "portrait", "Mariya", null, photo, default)) != null && up.Files.Saved.Count == 1 && up.Files.Deleted.SequenceEqual(up.Files.Saved), "a failed draft update deletes the stored file");

    up = Uploads();
    up.Images.List.AddRange(Enumerable.Range(0, 20).Select(i => new WebsiteImageReference(Guid.NewGuid(), Guid.NewGuid(), "gallery", $"Look {i}", "", i, true)));
    var galleryFull = await Fails(() => up.Upload.Save(uploadOwner, draftId, up.Images.Revision, "gallery", "One more", null, photo, default));
    Check(galleryFull is ArgumentException { Message: "The gallery already has 20 photos." } && up.Files.Saved.Count == 0, "a 21st gallery photo is refused before storage");

    up = Uploads();
    await up.Upload.Save(uploadOwner, draftId, up.Images.Revision, "gallery", "First look", "Caption one", photo, default);
    await up.Upload.Save(uploadOwner, draftId, up.Images.Revision, "gallery", "Second look", "", photo, default);
    var looks = up.Images.List.Where(x => x.Role == "gallery").OrderBy(x => x.SortOrder).ToList();
    Check(looks.Count == 2 && looks[1].SortOrder == looks[0].SortOrder + 1 && looks[0].Caption == "Caption one", "gallery photos append in order with their captions");
    await up.Upload.Remove(uploadOwner, draftId, up.Images.Revision, looks[0].Id, default);
    Check(up.Images.List.Count == 1 && up.Images.List[0].Id == looks[1].Id, "Remove drops only the chosen photo");

    // A storage outage shows a friendly message; text editing is unaffected.
    up = Uploads(); up.Files.FailSave = true;
    var uploadServices = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(new Microsoft.Extensions.DependencyInjection.ServiceCollection(), up.Upload));
    var uploadHttp = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = uploadOwner, RequestServices = uploadServices };
    var uploadController = new GlamByMariya.Web.Controllers.WebsiteContentController { ControllerContext = new ControllerContext { HttpContext = uploadHttp } };
    uploadController.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(uploadHttp, new NoTempData());
    uploadController.Url = new Microsoft.AspNetCore.Mvc.Routing.UrlHelper(new ActionContext(uploadHttp, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
    var formFile = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(photo), 0, photo.Length, "image", "soft-glam.jpg");
    var stored = await uploadController.SaveImage(draftId, up.Images.Revision, "hero", "Alt", null, formFile, default);
    Check(stored is RedirectToActionResult { ActionName: "Index" } && (string?)uploadController.TempData["Error"] == "Photos can't be saved right now. Text changes still work.", "a storage outage shows the friendly photo message");

    var viewSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "Views", "WebsiteContent", "Index.cshtml"));
    var demoStart = viewSource.IndexOf("ViewData[\"DemoPersistentText\"] is true) {", viewSource.IndexOf("<partial name=\"_TextEditor\"", StringComparison.Ordinal), StringComparison.Ordinal);
    var demoEnd = demoStart < 0 ? -1 : viewSource.IndexOf("website-image-preview.js", demoStart, StringComparison.Ordinal);
    Check(demoEnd > 0 && viewSource.IndexOf("asp-action=\"SaveImage\"", demoEnd, StringComparison.Ordinal) > 0 && viewSource.IndexOf("asp-action=\"RemoveImage\"", demoEnd, StringComparison.Ordinal) > 0, "the live editor has photo save and remove forms outside the demo block");

    // Review fixes: caching, unsaved-text guard, cleanup logging, message mapping.
    up = Uploads(); up.Images.FailReplace = true; up.Files.FailDelete = true;
    var cleanupErr = await Fails(() => up.Upload.Save(uploadOwner, draftId, up.Images.Revision, "portrait", "Mariya", null, photo, default));
    Check(cleanupErr is InvalidOperationException { Message: "Simulated database failure." }, "a failed cleanup delete still rethrows the original error");
    Check(cleanupLog.Entries.Any(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains(up.Files.Saved[0])), "a failed cleanup delete logs a warning with the object key");

    var imagesSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "Controllers", "WebsiteImagesController.cs"));
    Check(!imagesSource.Contains("NoStore") && imagesSource.Contains("public, max-age=86400") && imagesSource.Contains("nosniff"), "published images are cached publicly for a day and keep nosniff");
    var contentSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "Controllers", "WebsiteContentController.cs"));
    var draftAt = contentSource.IndexOf("Task<IActionResult> DraftImage", StringComparison.Ordinal);
    Check(draftAt > 0 && contentSource[(draftAt - 140)..draftAt].Contains("NoStore = true"), "draft images stay no-store");

    Check(viewSource.Contains("Save your text changes before adding or removing photos.") && viewSource.Contains("~/js/website-unsaved-text.js"), "the live photo area warns to save text first and loads the unsaved-text script");
    var guardPath = Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "wwwroot", "js", "website-unsaved-text.js");
    var guard = File.Exists(guardPath) ? File.ReadAllText(guardPath) : "";
    Check(guard.Contains("beforeunload") && guard.Contains("website-content-editor") && guard.Contains("submit"), "the unsaved-text script warns on leave and clears on submit");

    async Task<string?> SaveMessage(Func<GlamByMariya.Web.Controllers.WebsiteContentController, Task> act, RecordingFileStore f, FakeWebsiteImages im, FakeAssetWriter aw)
    {
        var svc = new GlamByMariya.Infrastructure.Integrations.WebsiteImageUploadService(im, aw, f, cleanupLog);
        var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(new Microsoft.Extensions.DependencyInjection.ServiceCollection(), svc));
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = uploadOwner, RequestServices = sp };
        var c = new GlamByMariya.Web.Controllers.WebsiteContentController { ControllerContext = new ControllerContext { HttpContext = http } };
        c.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, new NoTempData());
        c.Url = new Microsoft.AspNetCore.Mvc.Routing.UrlHelper(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
        await act(c);
        return (string?)c.TempData["Error"];
    }
    Microsoft.AspNetCore.Http.FormFile Form() => new(new MemoryStream(photo), 0, photo.Length, "image", "soft-glam.jpg");
    var mm1 = Uploads();
    Check(await SaveMessage(c => c.SaveImage(draftId, mm1.Images.Revision + 5, "hero", "Alt", null, Form(), default), mm1.Files, mm1.Images, mm1.Assets) == "The draft changed. Reload and try again.", "a stale revision shows the draft-changed message");
    var mm2 = Uploads(); mm2.Images.FailReplace = true;
    Check(await SaveMessage(c => c.SaveImage(draftId, mm2.Images.Revision, "hero", "Alt", null, Form(), default), mm2.Files, mm2.Images, mm2.Assets) == "The photo could not be saved. Please try again.", "any other failure shows the generic photo message");
    var mm3 = Uploads(); mm3.Files.FailSave = true;
    Check(await SaveMessage(c => c.SaveImage(draftId, mm3.Images.Revision, "hero", "Alt", null, Form(), default), mm3.Files, mm3.Images, mm3.Assets) == "Photos can't be saved right now. Text changes still work.", "a storage outage shows the outage message");
    var mm4 = Uploads(); mm4.Images.List.Add(new WebsiteImageReference(Guid.NewGuid(), Guid.NewGuid(), "gallery", "x", "", 0, true));
    Check(await SaveMessage(c => c.RemoveImage(draftId, mm4.Images.Revision + 5, mm4.Images.List[0].Id, default), mm4.Files, mm4.Images, mm4.Assets) == "The draft changed. Reload and try again.", "removing with a stale revision shows the draft-changed message");
    var mm5 = Uploads(); mm5.Images.List.Add(new WebsiteImageReference(Guid.NewGuid(), Guid.NewGuid(), "gallery", "x", "", 0, true)); mm5.Images.FailReplace = true;
    Check(await SaveMessage(c => c.RemoveImage(draftId, mm5.Images.Revision, mm5.Images.List[0].Id, default), mm5.Files, mm5.Images, mm5.Assets) is { } removeMsg && !removeMsg.Contains("draft changed"), "removing with another failure does not claim the draft changed");
    // With live photo editing on, the text editor must not claim photos are unavailable.
    var textEditorView = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "Views", "WebsiteContent", "_TextEditor.cshtml"));
    Check(textEditorView.Contains("ViewData[\"DraftImages\"] != null") && textEditorView.Contains("Add or replace photos below, after saving your text.")
          && textEditorView.Contains("Add and remove gallery photos below."), "the live editor's notices point to the photo cards instead of saying photos are unavailable");
    // The draft preview shows the draft's photos on the live site too, not only in the demo.
    var previewController = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "Controllers", "WebsiteContentController.cs"));
    var previewView = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "Views", "WebsiteContent", "Preview.cshtml"));
    Check(previewController.Contains("ViewData[\"PreviewImages\"] = await images.DraftImages(User, draft.Id, cancellationToken)") && previewView.Contains("ViewData[\"PreviewImages\"]")
          && previewView.Contains("\"gallery\" => \"Gallery photo\""), "the live draft preview shows hero, portrait and gallery photos");
    Console.WriteLine($"All {checks} live website content checks passed."); return;
}
if (args.Contains("--package-label-only"))
{
    // Step 9a: the "Package" label only when the group rate was actually applied.
    var soft = new CatalogItem { Name = "Soft Glam", Price = 600, GroupPrice = 525, GroupMinimumPeople = 4, Minutes = 60 };
    Booking FromQuote(int people)
    {
        var q = BookingPricing.Calculate(soft, [], people, 0, "group");
        return new Booking { Service = new(soft.Id, soft.Name, soft.Price, soft.Minutes), People = people, Package = "group",
            GroupPrice = q.GroupPrice, GroupMinimumPeople = q.GroupMinimumPeople, AppliedPricePerPerson = q.AppliedPricePerPerson };
    }
    var small = FromQuote(2);
    Check(small.AppliedPricePerPerson == 600 && small.BookingType == "Group booking (regular rate)", "2 people below the group minimum show the regular-rate label");
    var atMinimum = FromQuote(4);
    Check(atMinimum.AppliedPricePerPerson == 525 && atMinimum.BookingType == "Same-Service Package", "4 people at the group minimum show the package label");
    Check(new Booking { People = 1, Package = "auto" }.BookingType == "Individual", "one person is still an Individual booking");
    Check(GlamByMariya.Web.ViewModels.AdminPaymentLabels.PeopleCount(1) == "1 person" && GlamByMariya.Web.ViewModels.AdminPaymentLabels.PeopleCount(4) == "4 people", "party size reads 1 person / 4 people");
    var adminViews = Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "Views", "Admin");
    foreach (var view in new[] { "Payments.cshtml", "_AppointmentCalendar.cshtml", "_AppointmentList.cshtml", "_MixedBookingDetails.cshtml" })
        Check(!File.ReadAllText(Path.Combine(adminViews, view)).Contains("People people"), $"{view} uses the party size label");
    var adminDetails = File.ReadAllText(Path.Combine(adminViews, "Details.cshtml"));
    Check(adminDetails.IndexOf("BOOKING REVIEW") < adminDetails.IndexOf("_BookingPriceDetails"), "admin booking details show the customer and status before the price table");
    Console.WriteLine($"All {checks} package label checks passed."); return;
}
if (args.Contains("--booking-lookup-only"))
{
    // Step 9c: opening one booking reads that booking, not the whole Bookings list.
    var demo = new DemoStore(presentationAccounts: true);
    var counting = System.Reflection.DispatchProxy.Create<IStudioStore, CountingStore>();
    ((CountingStore)(object)counting).Target = demo;
    var counter = (CountingStore)(object)counting;
    var flow = new BookingService(counting, counting, counting, counting, counting);
    var booking = demo.Bookings.Single();
    var lookupCustomer = demo.Users.Single(u => u.Role == Roles.Customer);
    var lookupAdmin = demo.Users.Single(u => u.Role == Roles.Admin);
    var other = demo.AddUser("Other", "Customer", "other.lookupCustomer@example.test", "OtherPass123", Roles.Customer, 30, "Others", "24 Synthetic Street");
    T Wire<T>(T controller, AppUser user) where T : Controller
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, user.Role) }, "Cookies"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, new NoTempData());
        return controller;
    }
    Check(demo.FindBooking(booking.Id) == booking && demo.FindBooking(Guid.NewGuid()) == null, "FindBooking returns the booking by id, or nothing");
    counter.Reset();
    var adminView = Wire(new AdminController(counting, flow, null!, new MemoryInvitationService(TimeProvider.System), null!), lookupAdmin).Details(booking.Id);
    Check(adminView is ViewResult { Model: Booking shown } && shown.Id == booking.Id && counter.Calls("get_Bookings") == 0, "admin booking details load one booking, not the whole list");
    counter.Reset();
    var mine = Wire(new BookingsController(counting, flow, null!, null!, null!), lookupCustomer).Confirmation(booking.Id);
    Check(mine is ViewResult && counter.Calls("get_Bookings") == 0, "customer booking confirmation loads one booking, not the whole list");
    var notMine = Wire(new BookingsController(counting, flow, null!, null!, null!), other).Confirmation(booking.Id);
    Check(notMine is NotFoundResult, "a customer still cannot open another customer's booking");
    foreach (var controllerFile in new[] { "AdminController.cs", "BookingsController.cs" })
        Check(!File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "Controllers", controllerFile)).Contains("store.Bookings.FirstOrDefault("),
            $"{controllerFile} no longer scans every booking to find one");
    Console.WriteLine($"All {checks} booking lookup checks passed."); return;
}
if (args.Contains("--xendit-only"))
{
    // Xendit client and webhook without network or database: a fake HTTP handler and a fake store.
    IConfiguration Settings(string key) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["Xendit:SecretKey"] = key, ["Xendit:CallbackToken"] = "callback-token-123" }).Build();
    var xenditHttp = new CapturingXenditHttp();
    var client = new XenditClient(xenditHttp, Settings("xnd_development_abc"));
    var invoice = await client.CreateInvoiceAsync("LM-1234ABCD", 300m, "ana@example.test", "Down payment", "https://site.test/done", "https://site.test/done");
    var sent = System.Text.Json.JsonDocument.Parse(xenditHttp.LastBody!).RootElement;
    Check(xenditHttp.LastUri == "https://api.xendit.co/v2/invoices" && xenditHttp.LastAuth == "Basic " + Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("xnd_development_abc:")),
        "the invoice request goes to Xendit with the secret key as basic auth");
    Check(sent.GetProperty("external_id").GetString() == "LM-1234ABCD" && sent.GetProperty("amount").GetDecimal() == 300m && sent.GetProperty("currency").GetString() == "PHP"
        && sent.GetProperty("invoice_duration").GetInt32() == 1800, "the invoice is for the booking reference, the exact deposit in PHP, valid 30 minutes");
    Check(invoice.Id == "inv_fake_1" && invoice.InvoiceUrl == "https://checkout-staging.xendit.co/web/inv_fake_1", "the checkout link comes back from Xendit");
    Reject(() => new XenditClient(xenditHttp, Settings("xnd_production_abc")).CreateInvoiceAsync("LM-1", 1m, "a@example.test", "d", "https://s.test", "https://s.test").GetAwaiter().GetResult(),
        "a live Xendit key is refused for the demo");
    Check(client.CallbackTokenMatches("callback-token-123") && !client.CallbackTokenMatches("wrong") && !client.CallbackTokenMatches(null), "only the configured callback token is accepted");

    var fakeStore = System.Reflection.DispatchProxy.Create<IPersistentStudioStore, XenditStoreProxy>();
    var calls = (XenditStoreProxy)(object)fakeStore;
    System.Text.Json.JsonElement Body(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement;
    var paid = GlamByMariya.Web.Controllers.XenditWebhookController.Apply(fakeStore, Body("""{"id":"inv_9","status":"PAID","paid_amount":300,"currency":"PHP"}"""));
    Check(paid == (200, "confirmed") && calls.Last == "XenditPaid inv_9 300 inv_9:PAID", "a PAID callback confirms through the database with the paid amount");
    var expired = GlamByMariya.Web.Controllers.XenditWebhookController.Apply(fakeStore, Body("""{"id":"inv_9","status":"EXPIRED","currency":"PHP"}"""));
    Check(expired == (200, "noted") && calls.Last == "XenditExpired inv_9 inv_9:EXPIRED", "an EXPIRED callback is passed on");
    Check(GlamByMariya.Web.Controllers.XenditWebhookController.Apply(fakeStore, Body("""{"id":"inv_9","status":"PAID","paid_amount":300,"currency":"USD"}""")).Status == 400,
        "a non-PHP callback is refused");
    Check(GlamByMariya.Web.Controllers.XenditWebhookController.Apply(fakeStore, Body("""{"status":"PAID","paid_amount":300}""")).Status == 400, "a callback without an invoice id is refused");

    var webhook = new GlamByMariya.Web.Controllers.XenditWebhookController(fakeStore, client, Microsoft.Extensions.Logging.Abstractions.NullLogger<GlamByMariya.Web.Controllers.XenditWebhookController>.Instance);
    var hook = new Microsoft.AspNetCore.Http.DefaultHttpContext();
    hook.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""{"id":"inv_9","status":"PAID","paid_amount":300}"""));
    hook.Request.Headers["x-callback-token"] = "forged";
    webhook.ControllerContext = new ControllerContext { HttpContext = hook };
    Check(await webhook.Receive() is UnauthorizedResult && calls.Count == 2, "a webhook with a wrong token is refused before touching the database");

    // MVC treats every non-nullable string parameter as [Required]. The Xendit booking form posts no
    // payment reference or screenshot, so only fields it really sends may be non-nullable strings.
    var createPost = typeof(GlamByMariya.Web.Controllers.BookingsController).GetMethods()
        .Single(m => m.Name == "Create" && m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), false).Length > 0);
    var nullability = new System.Reflection.NullabilityInfoContext();
    var requiredStrings = createPost.GetParameters()
        .Where(p => p.ParameterType == typeof(string) && nullability.Create(p).WriteState == System.Reflection.NullabilityState.NotNull)
        .Select(p => p.Name!).ToHashSet();
    var xenditFormFields = new HashSet<string> { "address", "area", "method", "package" };
    Check(requiredStrings.IsSubsetOf(xenditFormFields),
        "the Xendit booking form posts every field MVC requires (required: " + string.Join(", ", requiredStrings) + ")");

    // Customer-facing wording in Xendit mode: no manual-payment leftovers.
    string View(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "Views" }.Concat(parts).ToArray()));
    var createView = View("Bookings", "Create.cshtml");
    Check(!createView.Contains("_PaymentQr") && createView.Contains("Continue to secure payment"),
        "the booking form has no manual payment QR box and says it continues to secure payment");
    Check(!View("Shared", "_AvailabilityCalendar.cshtml").Contains("not yet verified"), "the calendar note no longer shows an internal 'not yet verified' remark");
    var receipt = View("Bookings", "Confirmation.cshtml");
    Check(receipt.Contains("You’re booked!") && receipt.Contains("Complete payment") && receipt.Contains("PaidOnXendit"),
        "the confirmation page matches the booking status and offers the payment link while unpaid");
    var detailsView = View("Bookings", "Details.cshtml");
    Check(detailsView.Contains("ViewBag.CheckoutUrl is string payUrl") && detailsView.Contains("ViewBag.PaidOnXendit != true"),
        "booking details offer the Xendit link while unpaid and hide the screenshot upload for Xendit payments");
    Check(detailsView.IndexOf("ViewBag.CheckoutUrl is string payUrl") < detailsView.IndexOf("Cancel your appointment"),
        "the Xendit pay panel sits above the cancel section");
    Check(createView.Contains("Never send money to a personal account") && createView.Contains("Next you pay the down payment on Xendit")
          && !createView.Contains("Estimated departure") && !createView.Contains("route feasibility must be reviewed"),
        "the Xendit booking form and review popup describe online payment, not manual review");
    foreach (var script in new[] { "booking-steps.js", "booking-steps-phase2.js" })
    {
        var js = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "wwwroot", "js", script));
        Check(!js.Contains("Route feasibility") && !js.Contains("claimed payment") && js.Contains("Down payment to pay now"), $"{script} review popup has no manual-review leftovers");
    }
    var controllerSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "Controllers", "BookingsController.cs"));
    Check(controllerSource.Contains("Your booking is confirmed once the down payment goes through."), "the time check message in Xendit mode says payment confirms the booking");
    string AdminView(string name) => File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "Views", "Admin", name));
    var overview = AdminView("Index.cshtml"); var paymentsPage = AdminView("Payments.cshtml"); var reportsPage = AdminView("Reports.cshtml");
    Check(overview.Contains("Waiting for Xendit checkout") && paymentsPage.Contains("Xendit marks a down payment verified") && reportsPage.Contains("Xendit down payments and cash balances"),
        "the admin overview, payments and reports describe Xendit when it is on");
    Check(new[] { overview, paymentsPage, reportsPage }.All(v => v.Contains("Xendit.Enabled && DataStore.IsPersistent")), "the admin wording follows whether Xendit is on");
    // Unpaid bookings that ended (expired, cancelled, rejected) are not "awaiting review".
    Booking Unpaid(string status) => new() { Status = status, PaymentStatus = "Pending Verification" };
    var unpaidClaim = new Payment { Verified = false };
    Check(GlamByMariya.Web.ViewModels.AdminPaymentLabels.Booking(Unpaid("Expired"), true) == "Not paid — checkout expired"
          && GlamByMariya.Web.ViewModels.AdminPaymentLabels.Booking(Unpaid("Cancelled"), true) == "Not paid"
          && GlamByMariya.Web.ViewModels.AdminPaymentLabels.Booking(Unpaid("Pending Payment"), true) == "Waiting for Xendit payment"
          && GlamByMariya.Web.ViewModels.AdminPaymentLabels.Booking(new Booking { Status = "Confirmed", PaymentStatus = "Down Payment Verified" }, true) == "Down Payment Verified",
        "the appointment list shows unpaid expired or cancelled bookings as not paid");
    Check(GlamByMariya.Web.ViewModels.AdminPaymentLabels.Payment(unpaidClaim, Unpaid("Expired"), true) == "Not paid — checkout expired"
          && GlamByMariya.Web.ViewModels.AdminPaymentLabels.Payment(unpaidClaim, Unpaid("Pending Payment"), true) == "Waiting for Xendit payment"
          && GlamByMariya.Web.ViewModels.AdminPaymentLabels.Payment(unpaidClaim, Unpaid("Pending Payment"), false) == "Unverified — awaiting review"
          && GlamByMariya.Web.ViewModels.AdminPaymentLabels.Payment(new Payment { Verified = true }, Unpaid("Expired"), true) == "Verified",
        "the payments page shows an expired Xendit checkout as not paid, not awaiting review");
    var calendarView = AdminView("_AppointmentCalendar.cshtml"); var listView = AdminView("_AppointmentList.cshtml");
    Check(calendarView.Contains("AdminPaymentLabels.OnCalendar") && listView.Contains("AdminPaymentLabels.Booking") && paymentsPage.Contains("AdminPaymentLabels.Payment"),
        "the calendar, list and payments pages use these labels");
    Check(paymentsPage.Contains("p.Method == \"Cash\" ? \"Cash balance\""), "a cash balance recorded at completion is not called a down payment");
    Check(!GlamByMariya.Web.ViewModels.AdminPaymentLabels.OnCalendar(Unpaid("Expired")) && !GlamByMariya.Web.ViewModels.AdminPaymentLabels.OnCalendar(Unpaid("Rejected"))
          && GlamByMariya.Web.ViewModels.AdminPaymentLabels.OnCalendar(Unpaid("Pending Payment")) && GlamByMariya.Web.ViewModels.AdminPaymentLabels.OnCalendar(Unpaid("Cancelled")) == false,
        "expired, rejected and cancelled bookings no longer count as appointments on the calendar");
    // Business Settings on the live site (database + Xendit) only shows what applies there.
    var settingsPage = AdminView("Settings.cshtml");
    Check(settingsPage.Contains("Xendit.Enabled && DataStore.IsPersistent") && settingsPage.Contains("@if (!online)") && settingsPage.Contains("<input type=\"hidden\" name=\"paymentInstructions\""),
        "with Xendit on, the GCash/Maya/bank QR uploads and payment instructions are hidden but kept");
    Check(settingsPage.Contains("@(DataStore.IsPersistent ? $\"All times are in {Model.TimeZone}.\"") && settingsPage.Contains("@if (!DataStore.IsPersistent) { <text>Demo settings and bookings reset on restart. </text> }"),
        "the live settings no longer say server local time or that settings reset on restart");
    Check(settingsPage.Contains("Early and late fee settings can't be changed on the live site yet."), "the live settings explain the fee settings plainly");
    Console.WriteLine($"All {checks} Xendit checks passed."); return;
}
if (args.Contains("--email-codes-only"))
{
    // Store rules, checked directly on the memory store (the Postgres store follows the same contract).
    var codeStore = new MemoryEmailCodeStore(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
    var t0 = DateTime.UtcNow;
    Check(codeStore.Consume("reset", "a@example.test", "H1", 5, t0).Result == EmailCodeCheck.Missing, "no code means Missing");
    codeStore.Save("reset", " A@Example.TEST ", "H1", t0.AddMinutes(15), null);
    Check(codeStore.Peek("reset", "a@example.test", t0) != null, "email is normalized (case and spaces)");
    for (var i = 0; i < 5; i++) Check(codeStore.Consume("reset", "a@example.test", "WRONG", 5, t0).Result == EmailCodeCheck.Wrong, $"wrong code {i + 1} counts an attempt");
    Check(codeStore.Consume("reset", "a@example.test", "H1", 5, t0).Result == EmailCodeCheck.Locked, "after 5 wrong codes even the right code is locked");
    codeStore.Save("reset", "a@example.test", "H2", t0.AddMinutes(15), null);
    Check(codeStore.Consume("reset", "a@example.test", "H2", 5, t0).Result == EmailCodeCheck.Ok, "a new code resets the attempt counter");
    Check(codeStore.Consume("reset", "a@example.test", "H2", 5, t0).Result == EmailCodeCheck.Missing, "a correct code is single use");
    codeStore.Save("registration", "a@example.test", "R1", t0.AddMinutes(15), "{}");
    codeStore.Consume("registration", "a@example.test", "WRONG", 5, t0);
    codeStore.Save("reset", "a@example.test", "H3", t0.AddMinutes(15), null);
    Check(codeStore.Consume("registration", "a@example.test", "R1", 5, t0).Result == EmailCodeCheck.Ok && codeStore.Consume("reset", "a@example.test", "H3", 5, t0).Result == EmailCodeCheck.Ok,
        "registration and reset codes have separate attempt counters");
    codeStore.Save("reset", "a@example.test", "H4", t0.AddMinutes(1), null);
    Check(codeStore.Consume("reset", "a@example.test", "H4", 5, t0.AddMinutes(2)).Result == EmailCodeCheck.Expired, "an old code is Expired");
    Check(codeStore.TryStartCooldown("reset", "a@example.test", TimeSpan.FromSeconds(60), t0) && !codeStore.TryStartCooldown("reset", "a@example.test", TimeSpan.FromSeconds(60), t0),
        "a second request inside the cooldown is refused");
    Check(codeStore.TryStartCooldown("registration", "a@example.test", TimeSpan.FromSeconds(60), t0), "a reset cooldown does not block registration");
    codeStore.ClearCooldown("reset", "a@example.test");
    Check(codeStore.TryStartCooldown("reset", "a@example.test", TimeSpan.FromSeconds(60), t0), "a cleared cooldown allows an immediate retry");

    // Service behaviour with a fake Brevo delivery that captures the emailed code.
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Email:BrevoApiKey"] = "synthetic-key", ["Email:From"] = "sender@example.test" }).Build();
    var delivery = new CapturingBrevoDelivery();
    var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
    var serviceStore = new MemoryEmailCodeStore(cache);
    var brevo = new BrevoEmailSender(delivery, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<BrevoEmailSender>.Instance);
    var codes = new EmailVerificationService(cache, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailVerificationService>.Instance, new SyntheticWebEnv(), brevo, serviceStore);
    var email = "new.customer@example.test";
    await codes.RequestRegistrationCodeAsync(email, new("New", "Customer", email, 30, "Others", "24 Synthetic Street", "Secret123!"));
    var held = serviceStore.Peek("registration", email, DateTime.UtcNow)!;
    Check(!held.Contains("Secret123!") && held.Contains("PasswordHash"), "the held registration stores a password hash, never the password");
    Reject(() => codes.RequestRegistrationCodeAsync(email, new("New", "Customer", email, 30, "Others", "24 Synthetic Street", "Secret123!")).GetAwaiter().GetResult(), "a second registration email inside 60 seconds is refused");
    Reject(() => codes.ConfirmRegistrationCode(email, "000000" == delivery.LastCode ? "111111" : "000000"), "a wrong registration code is rejected");
    var pending = codes.ConfirmRegistrationCode(email, delivery.LastCode!);
    Check(pending.Password == "" && pending.PasswordHash is { } hash &&
        new Microsoft.AspNetCore.Identity.PasswordHasher<AppUser>().VerifyHashedPassword(new AppUser(), hash, "Secret123!") != Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed,
        "the confirmed registration returns a hash that matches the original password");
    var demo = new DemoStore(presentationAccounts: true);
    var created = demo.AddCustomerWithPasswordHash(pending.FirstName, pending.LastName, pending.Email, pending.PasswordHash!, pending.Age, pending.Gender, pending.Address);
    Check(created.Role == Roles.Customer && created.PasswordHash == pending.PasswordHash, "a customer account is created from the held hash");
    Reject(() => demo.AddCustomerWithPasswordHash(pending.FirstName, pending.LastName, pending.Email, pending.PasswordHash!, pending.Age, pending.Gender, pending.Address), "the same email cannot be registered twice");
    Reject(() => demo.AddCustomerWithPasswordHash("New", "Customer", "other@example.test", "", 30, "Others", "24 Synthetic Street"), "an empty password hash is rejected");

    await codes.RequestPasswordResetCodeAsync(email);
    Reject(() => codes.RequireResetVerified(email), "reset is not verified before the code is entered");
    codes.ConfirmPasswordResetCode(email, delivery.LastCode!);
    codes.RequireResetVerified(email);
    Check(true, "a correct reset code marks the reset as verified");
    codes.ClearResetVerified(email);
    Reject(() => codes.RequireResetVerified(email), "clearing the reset verification works");

    // Takeover attempt: someone else registers the same email from another browser.
    var target = "target.customer@example.test";
    var victimBrowser = EmailVerificationService.NewBrowserToken(); var attackerBrowser = EmailVerificationService.NewBrowserToken();
    await codes.RequestRegistrationCodeAsync(target, new("Real", "Owner", target, 30, "Others", "24 Synthetic Street", "OwnerPass123!"), victimBrowser);
    var victimCode = delivery.LastCode!;
    serviceStore.ClearCooldown("registration", target); // skip the 60-second wait in the test
    await codes.RequestRegistrationCodeAsync(target, new("Other", "Person", target, 30, "Others", "99 Synthetic Road", "Takeover123!"), attackerBrowser);
    var attackerCode = delivery.LastCode!;
    Check(codes.HasPendingRegistration(target, victimBrowser) && codes.HasPendingRegistration(target, attackerBrowser),
        "each browser keeps its own pending registration; the second one does not replace the first");
    Reject(() => codes.ConfirmRegistrationCode(target, attackerCode, victimBrowser), "the other browser's code does not complete this browser's registration");
    Reject(() => codes.ConfirmRegistrationCode(target, victimCode, attackerBrowser), "a code cannot be completed from a different browser");
    Reject(() => codes.ConfirmRegistrationCode(target, victimCode), "a code cannot be completed without the browser secret");
    var owner = codes.ConfirmRegistrationCode(target, victimCode, victimBrowser);
    var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<AppUser>();
    Check(owner.FirstName == "Real" && hasher.VerifyHashedPassword(new AppUser(), owner.PasswordHash!, "OwnerPass123!") != Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed
        && hasher.VerifyHashedPassword(new AppUser(), owner.PasswordHash!, "Takeover123!") == Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed,
        "the owner's browser creates the account with the owner's own details and password");
    Check(EmailVerificationService.NewBrowserToken() != EmailVerificationService.NewBrowserToken() && EmailVerificationService.NewBrowserToken().Length >= 40,
        "browser secrets are long and random");

    delivery.Fail = true;
    var failing = "failing@example.test";
    Reject(() => codes.RequestPasswordResetCodeAsync(failing).GetAwaiter().GetResult(), "a failed email send is reported");
    delivery.Fail = false;
    await codes.RequestPasswordResetCodeAsync(failing);
    Check(delivery.LastCode != null, "after a failed send the customer can retry immediately");
    Console.WriteLine($"All {checks} email code checks passed."); return;
}
if (args.Contains("--persistent-reschedule-only"))
{
    var persistent = System.Reflection.DispatchProxy.Create<IPersistentStudioStore, PersistentQuoteProxy>();
    var proxy = (PersistentQuoteProxy)persistent;
    proxy.TestServices = store.Services; proxy.TestUsers = store.Users;
    var flow = new BookingService(persistent, persistent, persistent, persistent, persistent);
    var current = DateTime.Today.AddDays(3).AddHours(10); var proposed = DateTime.Today.AddDays(4).AddHours(10);
    var booking = new Booking { CustomerId = customer.Id, Status = "Confirmed", Start = current, Service = new(service.Id, service.Name, service.Price, service.Minutes), BufferMinutesSnapshot = 45, AppliedPricePerPerson = service.Price };
    proxy.TestBookings.Add(booking);
    var offer = flow.OfferReschedule(booking, customer.Id, proposed);
    Check(offer.ProposedStart == proposed && offer.ProposedTotal == booking.Total && offer.EarlyFee == 0 && offer.LateFee == 0,
        "database-mode reschedule quote keeps the same total (no early/late fees)");
    Reject(() => flow.ConsentReschedule(booking, customer.Id, offer.Id, false, proposed, offer.ProposedTotal), "consent is required");
    Reject(() => flow.ConsentReschedule(booking, customer.Id, offer.Id, true, null, offer.ProposedTotal), "the proposed time must be posted back");
    Reject(() => flow.ConsentReschedule(booking, customer.Id, offer.Id, true, proposed, offer.ProposedTotal + 100), "a total that differs from the review page is rejected");
    Reject(() => flow.ConsentReschedule(booking, admin.Id, offer.Id, true, proposed, offer.ProposedTotal), "only the booking customer can request");
    var blocker = new Booking { CustomerId = Guid.NewGuid(), Status = "Confirmed", Start = proposed, Service = booking.Service, BufferMinutesSnapshot = 45 };
    proxy.TestBookings.Add(blocker);
    Reject(() => flow.ConsentReschedule(booking, customer.Id, offer.Id, true, proposed, offer.ProposedTotal), "a time taken after the review is rejected");
    proxy.TestBookings.Remove(blocker);
    Check(proxy.RescheduleArguments == null, "rejected requests never reach the database");
    flow.ConsentReschedule(booking, customer.Id, offer.Id, true, proposed, offer.ProposedTotal);
    Check(proxy.RescheduleArguments is { } sent && (Guid)sent[0]! == booking.Id && (Guid)sent[1]! == customer.Id && (DateTime)sent[2]! == proposed,
        "a valid request is sent to the database with the booking, customer and new time");
    var source = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Backend", "GlamByMariya.Infrastructure", "Services", "BookingService.cs"));
    Check(!source.Contains("public void Reschedule("), "the unused Reschedule method is gone");
    Console.WriteLine($"All {checks} persistent reschedule checks passed."); return;
}
if (args.Contains("--booking-location-only"))
{
    Check(BookingLocation.IsValid(14.5547m, 121.0244m), "a pin in Makati is inside Metro Manila");
    Check(!BookingLocation.IsValid(14.5547m, null) && !BookingLocation.IsValid(null, 121.0244m) && !BookingLocation.IsValid(null, null), "a missing latitude or longitude is not a pin");
    Check(!BookingLocation.IsValid(15.5m, 121m), "a pin north of Metro Manila is refused");
    Check(!BookingLocation.IsValid(14.5547m, 121.3m) && !BookingLocation.IsValid(14.19m, 121m) && !BookingLocation.IsValid(14.5m, 120.84m), "pins outside the box on other sides are refused");
    Check(BookingLocation.IsValid(14.20m, 120.85m) && BookingLocation.IsValid(14.95m, 121.25m), "the box corners are inclusive");
    const string expectedUrl = "https://www.google.com/maps/dir/?api=1&destination=14.554700,121.024400";
    Check(BookingLocation.GoogleMapsUrl(14.5547m, 121.0244m) == expectedUrl, "the Google Maps link uses six decimals");
    var savedCulture = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
        foreach (var name in new[] { "fil-PH", "de-DE" })
        {
            System.Globalization.CultureInfo.CurrentCulture = new(name);
            Check(BookingLocation.GoogleMapsUrl(14.5547m, 121.0244m) == expectedUrl, $"the Google Maps link ignores the {name} culture");
        }
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = savedCulture; }

    var demo = new DemoStore(presentationAccounts: true); var flow = new BookingService(demo, demo, demo, demo, demo);
    var customerUser = demo.Users.First(u => u.Role == Roles.Customer);
    var look = demo.Services.First(); var start = DateTime.Today.AddDays(29).AddHours(8);
    var quote = flow.Quote(look.Id, [], 1, "auto", start);
    Booking Submit(string reference, decimal? lat, decimal? lng) => flow.Create(customerUser.Id, look.Id, [], start, "24 Synthetic Street", "Quezon City", quote.RequiredDownPayment, "GCash",
        reference, "synthetic-proof.png", true, 1, "auto", true, quote.FinalTotal, quote.RequiredDownPayment, latitude: lat, longitude: lng);
    var count = demo.Bookings.Count;
    foreach (var (lat, lng, label) in new (decimal?, decimal?, string)[] { (null, null, "no pin"), (14.5547m, null, "half a pin"), (15.5m, 121m, "a pin outside Metro Manila") })
    {
        string message = "";
        try { Submit("LOC-REF-" + label.Length, lat, lng); } catch (InvalidOperationException e) { message = e.Message; }
        Check(message == BookingLocation.Message, $"a booking with {label} is refused with the pin message");
    }
    Check(demo.Bookings.Count == count, "refused pins create no booking");
    var pinned = Submit("LOC-REF-OK", 14.5547m, 121.0244m);
    Check(pinned.Latitude == 14.5547m && pinned.Longitude == 121.0244m, "a booking keeps its pin");
    Check(demo.Bookings.Single(b => b.Id == pinned.Id).Latitude == 14.5547m, "the stored booking keeps its pin");
    var seeded = demo.Bookings.First(b => b.Id != pinned.Id);
    Check(BookingLocation.IsValid(seeded.Latitude, seeded.Longitude), "the demo seed booking has a pin");

    // Task 3: the booking form pins the location and the controller passes it on.
    var web = Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web");
    var createView = File.ReadAllText(Path.Combine(web, "Views", "Bookings", "Create.cshtml"));
    Check(System.Text.RegularExpressions.Regex.IsMatch(createView, "<input type=\"hidden\" name=\"latitude\"") &&
          System.Text.RegularExpressions.Regex.IsMatch(createView, "<input type=\"hidden\" name=\"longitude\""), "the booking form has hidden latitude and longitude inputs");
    Check(createView.Contains("~/lib/leaflet/leaflet.js") && createView.Contains("~/lib/leaflet/leaflet.css"), "the booking form loads the local Leaflet copy");
    Check(createView.Contains("~/js/booking-location.js"), "the booking form loads the location script");
    Check(!createView.Contains("unpkg.com") && !createView.Contains("cdn.jsdelivr"), "Leaflet is not loaded from a CDN");
    foreach (var file in new[] { "leaflet.js", "leaflet.css", "LICENSE", Path.Combine("images", "marker-icon.png"), Path.Combine("images", "marker-shadow.png") })
        Check(File.Exists(Path.Combine(web, "wwwroot", "lib", "leaflet", file)), $"Leaflet {file} is in wwwroot/lib/leaflet");
    var locationScriptPath = Path.Combine(web, "wwwroot", "js", "booking-location.js");
    var locationScript = File.Exists(locationScriptPath) ? File.ReadAllText(locationScriptPath) : "";
    Check(locationScript.Contains("https://nominatim.openstreetmap.org/search?format=json&limit=1&countrycodes=ph&q="), "the search uses Nominatim limited to the Philippines");
    Check(locationScript.Contains("We couldn't get your location. Drag the pin instead."), "a geolocation error tells the customer to drag the pin");
    var artistScriptPath = Path.Combine(Path.GetDirectoryName(locationScriptPath)!, "artist-location.js");
    var artistScript = File.Exists(artistScriptPath) ? File.ReadAllText(artistScriptPath) : "";
    foreach (var (script, who) in new[] { (locationScript, "customer"), (artistScript, "artist") })
    {
        Check(script.Contains("say(geolocationError(error))") && script.Contains("error?.code === 1") && script.Contains("Location is blocked for this site in your browser."),
            $"the {who} map says when the browser blocks location for this site");
        Check(script.Contains("error?.code === 2") && script.Contains("Turn on location services on your device"), $"the {who} map says when the device location is off");
    }
    Check(locationScript.Contains("No match found; drag the pin instead"), "a search with no result tells the customer to drag the pin");
    Check(locationScript.Contains("© OpenStreetMap contributors"), "the map shows the OpenStreetMap attribution");
    Check(locationScript.Contains("1000") && locationScript.Contains("lastSearch"), "searches are limited to one request per second");
    Check(locationScript.Contains("toFixed(6)"), "the pin is written with six decimals");
    foreach (var (area, centre) in new[] { ("Quezon City", "14.6760, 121.0437"), ("Makati", "14.5547, 121.0244"), ("Manila", "14.5995, 120.9842"), ("Pasig", "14.5764, 121.0851"), ("Taguig", "14.5176, 121.0509"), ("Mandaluyong", "14.5794, 121.0359") })
        Check(locationScript.Contains($"'{area}': [{centre}]"), $"{area} has its map centre");
    foreach (var stepScript in new[] { "booking-steps.js", "booking-steps-phase2.js" })
    {
        var text = File.ReadAllText(Path.Combine(web, "wwwroot", "js", stepScript));
        Check(text.Contains("['Location', 'Pinned ✓']"), $"{stepScript} shows the pinned location in the review");
        Check(text.Contains("form.elements.latitude.value") && text.Contains("form.elements.longitude.value"), $"{stepScript} blocks submit until the pin is set");
    }
    var controllerSource = File.ReadAllText(Path.Combine(web, "Controllers", "BookingsController.cs"));
    Check(System.Text.RegularExpressions.Regex.Matches(controllerSource, @"workflow\.Create\([^;]*latitude: latitude, longitude: longitude\)").Count == 2,
        "both booking paths (manual and Xendit) pass the pin to the booking service");
    var csp = GlamByMariya.Web.Middleware.ContentSecurityPolicy.BuildReportOnly("n", []);
    Check(csp.Contains("img-src 'self' https://tile.openstreetmap.org") && csp.Contains("connect-src 'self' https://nominatim.openstreetmap.org"), "the content policy allows map tiles and the landmark search");

    var bookingsController = new GlamByMariya.Web.Controllers.BookingsController(demo, flow, null!, null!, null!, null!);
    var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
    http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, customerUser.Id.ToString()), new Claim(ClaimTypes.Role, customerUser.Role) }, "Cookies"));
    bookingsController.ControllerContext = new ControllerContext { HttpContext = http };
    bookingsController.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, new NoTempData());
    async Task<(IActionResult Result, string? Error)> Post(decimal? lat, decimal? lng)
    {
        bookingsController.TempData.Clear();
        var result = await bookingsController.Create(look.Id, [], start, "24 Synthetic Street", "Quezon City", quote.RequiredDownPayment, "GCash", "LOC-POST", null, true, 1, "auto", true,
            quote.FinalTotal, quote.RequiredDownPayment, latitude: lat, longitude: lng);
        return (result, bookingsController.TempData["Error"] as string);
    }
    var postCount = demo.Bookings.Count;
    foreach (var (lat, lng, label) in new (decimal?, decimal?, string)[] { (null, null, "no pin"), (null, 121.0244m, "half a pin"), (14.5547m, 121.4m, "a pin outside Metro Manila") })
    {
        var (result, error) = await Post(lat, lng);
        Check(result is RedirectToActionResult { ActionName: "Create" } && error == BookingLocation.Message, $"posting the form with {label} returns to the form with the pin message");
    }
    var (_, pinnedError) = await Post(14.5547m, 121.0244m);
    Check(pinnedError != BookingLocation.Message && pinnedError == "Upload a payment screenshot to submit your booking.", "a valid pin passes the pin check and the form moves on to the payment checks");
    Check(demo.Bookings.Count == postCount, "refused form posts create no booking");

    // Task 4: the details pages show the pin read-only.
    var partialPath = Path.Combine(web, "Views", "Shared", "_BookingLocationMap.cshtml");
    var partialText = File.Exists(partialPath) ? File.ReadAllText(partialPath) : "";
    Check(partialText.Contains("@model Booking") && partialText.Contains("No pinned location"), "the map partial shows No pinned location without a pin");
    Check(partialText.Contains("data-lat=\"@") && partialText.Contains("data-lng=\"@") && partialText.Contains("CultureInfo.InvariantCulture") && partialText.Contains("\"F6\""), "the map partial writes the pin in invariant culture with six decimals");
    Check(partialText.Contains("BookingLocation.GoogleMapsUrl") && partialText.Contains("Open in Google Maps") && partialText.Contains("target=\"_blank\"") && partialText.Contains("rel=\"noopener\""), "the map partial links to Google Maps safely");
    Check(partialText.Contains("~/js/booking-location-view.js") && partialText.Contains("~/lib/leaflet/leaflet.js") && partialText.Contains("~/lib/leaflet/leaflet.css"), "the map partial loads the local Leaflet and view script");
    var noPinBranch = partialText.Contains("else") ? partialText[partialText.LastIndexOf("else", StringComparison.Ordinal)..] : "";
    Check(noPinBranch.Contains("No pinned location") && !noPinBranch.Contains("data-lat"), "the no-pin branch renders no map element");
    foreach (var detailsView in new[] { Path.Combine("Admin", "Details.cshtml"), Path.Combine("Bookings", "Details.cshtml") })
    {
        var text = File.ReadAllText(Path.Combine(web, "Views", detailsView));
        var addressAt = text.IndexOf("Customer Service Address", StringComparison.Ordinal);
        var partialAt = text.IndexOf("_BookingLocationMap", StringComparison.Ordinal);
        Check(addressAt >= 0 && partialAt > addressAt, $"{detailsView} renders the map partial after the address");
    }
    var viewScriptPath = Path.Combine(web, "wwwroot", "js", "booking-location-view.js");
    var viewScript = File.Exists(viewScriptPath) ? File.ReadAllText(viewScriptPath) : "";
    Check(viewScript.Contains("© OpenStreetMap contributors") && viewScript.Contains("https://tile.openstreetmap.org/{z}/{x}/{y}.png"), "the read-only map shows the tiles and attribution");
    Check(viewScript.Contains("draggable: false") && viewScript.Contains("dragging: false") && !viewScript.Contains("map.on('click'"), "the read-only map cannot move the pin");
    // Final review fixes.
    var locScript = File.ReadAllText(Path.Combine(web, "wwwroot", "js", "booking-location.js"));
    var announceAt = locScript.IndexOf("const announce =", StringComparison.Ordinal);
    var clearAt = locScript.IndexOf("if (!restored) {", StringComparison.Ordinal);
    var clearEnd = clearAt >= 0 ? locScript.IndexOf('}', clearAt) : -1;
    Check(announceAt >= 0 && announceAt < clearAt && clearEnd > clearAt && locScript[clearAt..clearEnd].Contains("announce()"), "clearing an invalid restored pin on load announces the change");
    foreach (var stepsName in new[] { "booking-steps.js", "booking-steps-phase2.js" })
    {
        var stepsText = File.ReadAllText(Path.Combine(web, "wwwroot", "js", stepsName));
        var listenAt = stepsText.IndexOf("addEventListener('booking-location-changed'", StringComparison.Ordinal);
        var listenBody = listenAt >= 0 ? stepsText[listenAt..Math.Min(stepsText.Length, listenAt + 400)] : "";
        Check(listenBody.Contains("notPinned()") && listenBody.Contains("Pin your exact location inside Metro Manila.") && listenBody.Contains("notify('')"), $"{stepsName} clears the pin message once a valid pin is set");
    }
    var busyText = "Search is busy right now. Try again in a moment, or drag the pin.";
    var searchStart = locScript.IndexOf("const search = async", StringComparison.Ordinal);
    var searchBody = searchStart >= 0 ? locScript[searchStart..] : "";
    var catchAt = searchBody.IndexOf("catch", StringComparison.Ordinal);
    Check(locScript.Contains("const busyMessage = '" + busyText + "'") && catchAt > 0 && searchBody[catchAt..].Contains("say(busyMessage)") && !searchBody[catchAt..searchBody.IndexOf("finally", StringComparison.Ordinal)].Contains("noMatchMessage") && searchBody[..catchAt].Contains("say(noMatchMessage)"), "a failed search says it is busy while an empty result still says no match");
    Check(searchBody.Contains("lastSearch + 1000"), "the one-request-per-second guard stays");
    var planText = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "docs", "superpowers", "plans", "2026-11-20-booking-location-pin.md"));
    var task5 = planText[planText.IndexOf("### Task 5: Release", StringComparison.Ordinal)..];
    var at13 = task5.IndexOf("0013", StringComparison.Ordinal); var atDeploy = task5.IndexOf("deploy", at13 < 0 ? 0 : at13, StringComparison.OrdinalIgnoreCase);
    var atSmoke = task5.IndexOf("/healthz", StringComparison.Ordinal); var at14 = task5.IndexOf("0014", atSmoke < 0 ? 0 : atSmoke, StringComparison.Ordinal);
    Check(task5.Contains("pg_get_viewdef('public.mvc_booking_summary')") && at13 >= 0 && atDeploy > at13 && atSmoke > atDeploy && at14 > atSmoke, "the release plan applies 0013, deploys and smoke-checks, then applies 0014");
    Check(task5.Contains("mvc_reservation_guard") && task5.Contains("rolling the app back", StringComparison.OrdinalIgnoreCase), "the release plan has the rollback note");
    Console.WriteLine($"All {checks} booking location checks passed."); return;
}
if (args.Contains("--exact-deposit-only"))
{
    var demo = new DemoStore(presentationAccounts: true); var flow = new BookingService(demo, demo, demo, demo, demo);
    var customerUser = demo.Users.First(u => u.Role == Roles.Customer); var ownerUser = demo.Users.First(u => u.Role == Roles.SuperAdmin);
    var look = demo.Services.First(); var start = DateTime.Today.AddDays(29).AddHours(8);
    var quote = flow.Quote(look.Id, [], 1, "auto", start);
    Check(quote.RequiredDownPayment < quote.FinalTotal, "test service has a balance after the down payment");
    var n = 0;
    Booking Submit(decimal amount) => flow.Create(customerUser.Id, look.Id, [], start, "24 Synthetic Street", "Quezon City", amount, "GCash",
        $"EXACT-REF-{++n}", "synthetic-proof.png", true, 1, "auto", true, quote.FinalTotal, quote.RequiredDownPayment, latitude: 14.5547m, longitude: 121.0244m);
    var before = demo.Bookings.Count;
    Reject(() => Submit(quote.RequiredDownPayment + 1), "a higher custom amount is rejected");
    Reject(() => Submit(quote.FinalTotal), "full payment is rejected online");
    Reject(() => Submit(quote.RequiredDownPayment - 1), "less than the down payment is rejected");
    Check(demo.Bookings.Count == before, "rejected amounts create no booking");
    var booking = Submit(quote.RequiredDownPayment);
    Check(booking.Status == "Pending Payment" && demo.Payments.Single(p => p.BookingId == booking.Id).Amount == quote.RequiredDownPayment, "the exact down payment is accepted");
    var claim = demo.Payments.Single(p => p.BookingId == booking.Id);
    claim.Amount = quote.RequiredDownPayment + 50;
    Reject(() => flow.AdminAction(booking, "verify", ownerUser.Id, "", 0, ""), "verification rejects a claim that is not the exact down payment");
    claim.Amount = quote.RequiredDownPayment;
    flow.AdminAction(booking, "verify", ownerUser.Id, "", 0, "");
    Check(booking.Status == "Confirmed", "exact claim verifies and confirms");
    PreparedPersistentBooking.ValidateClaim(quote.RequiredDownPayment, quote);
    Check(true, "database-path claim check accepts the exact down payment");
    Reject(() => PreparedPersistentBooking.ValidateClaim(quote.FinalTotal, quote), "database-path claim check rejects full payment");
    Console.WriteLine($"All {checks} exact deposit checks passed."); return;
}
if (args.Contains("--payment-window-only"))
{
    var demo = new DemoStore(presentationAccounts: true); var flow = new BookingService(demo, demo, demo, demo, demo);
    var customerUser = demo.Users.First(u => u.Role == Roles.Customer); var ownerUser = demo.Users.First(u => u.Role == Roles.SuperAdmin);
    var look = demo.Services.First(); var start = DateTime.Today.AddDays(29).AddHours(8);
    var quote = flow.Quote(look.Id, [], 1, "auto", start);
    var booking = flow.Create(customerUser.Id, look.Id, [], start, "24 Synthetic Street", "Quezon City", quote.RequiredDownPayment, "GCash",
        "WINDOW-REF-1", "synthetic-proof.png", true, 1, "auto", true, quote.FinalTotal, quote.RequiredDownPayment, latitude: 14.5547m, longitude: 121.0244m);
    Check(booking.PaymentDueAt is { } due && Math.Abs((due - demo.Now - PaymentWindow.Length).TotalSeconds) < 5, "new booking gets a payment deadline 30 minutes from now");
    Check(flow.HoldsReservation(booking), "booking inside its payment window holds the slot");
    Reject(() => flow.CheckSlot(start, quote.DurationMinutes), "another customer cannot take a slot that is still inside its payment window");
    Check(flow.ExpireOverduePayments() == 0 && booking.Status == "Pending Payment", "nothing expires before the deadline");

    booking.PaymentDueAt = demo.Now.AddMinutes(-1);
    Check(PaymentWindow.IsExpired(booking, demo.Now) && !flow.HoldsReservation(booking), "overdue unpaid booking no longer holds its slot");
    flow.CheckSlot(start, quote.DurationMinutes);
    Check(true, "slot is free for another customer once the payment window ends");
    Reject(() => flow.AdminAction(booking, "verify", ownerUser.Id, "", 0, ""), "overdue booking cannot be verified");
    Reject(() => flow.CorrectProof(booking, customerUser.Id, "WINDOW-REF-2", null), "overdue booking cannot get a payment correction");
    Check(flow.ExpireOverduePayments() == 1 && booking.Status == PaymentWindow.ExpiredStatus && booking.History.Last().Action == "Payment window expired",
        "overdue booking is marked Expired with a history entry");
    Check(flow.ExpireOverduePayments() == 0, "expiry runs only once per booking");
    Check(!CancellationPolicy.Evaluate(booking.Status, booking.Start, demo.Now).Allowed, "expired booking cannot be cancelled again");

    var legacy = new Booking { Status = "Pending Payment", PaymentDueAt = null, Start = start };
    Check(!PaymentWindow.IsExpired(legacy, demo.Now.AddDays(1)), "older bookings without a deadline never expire automatically");
    var confirmedLate = new Booking { Status = "Confirmed", PaymentDueAt = demo.Now.AddHours(-5), Start = start };
    Check(!PaymentWindow.IsExpired(confirmedLate, demo.Now) && flow.HoldsReservation(confirmedLate), "confirmed bookings are never expired by the payment window");
    Console.WriteLine($"All {checks} payment window checks passed."); return;
}
if (args.Contains("--mixed-read-model-only"))
{
    var bookingId = Guid.NewGuid(); var ownerId = Guid.NewGuid();
    var bridal = Guid.Parse("41c611af-c52c-446a-ae01-1233acf20f32");
    var soft = Guid.NewGuid(); var addon = Guid.NewGuid();
    var header = new PreparedPersistentBooking.MixedHeader(bookingId, ownerId, true, 2, 4700, 250, 240, 15);
    var people = new[] {
        new PreparedPersistentBooking.MixedPersonRow(bookingId, 2, soft, false, "Old Soft", 1200, 90),
        new PreparedPersistentBooking.MixedPersonRow(bookingId, 1, bridal, true, "Old Bridal", 3500, 150)
    };
    var addons = new[] { new PreparedPersistentBooking.MixedAddonRow(bookingId, 1, addon, "Old Lash", 250, 15) };
    var owner = new AppUser { Id=ownerId, Role=Roles.Customer, IsActive=true };
    var super = new AppUser { Id=Guid.NewGuid(), Role=Roles.SuperAdmin, IsActive=true };
    IReadOnlyList<MixedPersonQuote> Read(AppUser actor, PreparedPersistentBooking.MixedHeader h,
        IReadOnlyList<PreparedPersistentBooking.MixedPersonRow> p, IReadOnlyList<PreparedPersistentBooking.MixedAddonRow> a) =>
        PreparedPersistentBooking.ReconstructMixed(actor, h, bridal, p, a);
    var result = Read(owner, header, people, addons);
    Check(result.Count==2 && result[0].Service.ItemId==bridal && result[0].Service.Name=="Old Bridal" &&
        result[0].Service.Price==3500 && result[0].DurationMinutes==165 && result[0].Total==3750 &&
        result[0].AddOns.Single().Name=="Old Lash" && result[1].Service.Name=="Old Soft",
        "sealed snapshot reconstructs historical per-person prices, durations and add-ons in order");
    Check(Read(super, header, people, addons).Count==2, "active SuperAdmin can reconstruct");
    Reject(() => Read(new AppUser { Id=Guid.NewGuid(), Role=Roles.Customer }, header, people, addons), "other Customer rejected");
    Reject(() => Read(new AppUser { Id=ownerId, Role=Roles.Customer, IsActive=false }, header, people, addons), "inactive owner rejected");
    Reject(() => Read(new AppUser { Id=Guid.NewGuid(), Role=Roles.Admin }, header, people, addons), "ordinary Admin rejected");
    Reject(() => Read(new AppUser { Id=Guid.NewGuid(), Role=Roles.SuperAdmin, IsActive=false }, header, people, addons), "inactive SuperAdmin rejected");
    Reject(() => Read(owner, header with { Sealed=false }, people, addons), "unsealed snapshot rejected");
    Reject(() => Read(owner, header with { ServiceSubtotal=4701 }, people, addons), "changed price aggregate rejected");
    Reject(() => Read(owner, header with { AddonMinutes=16 }, people, addons), "changed duration aggregate rejected");
    Reject(() => Read(owner, header, [people[0], people[0]], addons), "duplicate person position rejected");
    Reject(() => Read(owner, header, [people[0], people[1] with { IsBridal=false }], addons), "Bridal classification mismatch rejected");
    Reject(() => Read(owner, header, [people[1], people[1] with { Position=2 }], addons), "second Bridal selection rejected");
    Reject(() => Read(owner, header, people, [addons[0], addons[0]]), "duplicate per-person add-on rejected");
    Reject(() => Read(owner, header, people, [addons[0] with { BookingId=Guid.NewGuid() }]), "cross-booking add-on rejected");
    var mixed = new Booking { Id=bookingId, CustomerId=ownerId, MixedPeople=result };
    var persistent = System.Reflection.DispatchProxy.Create<IPersistentStudioStore, PersistentQuoteProxy>();
    var proxy = (PersistentQuoteProxy)persistent;
    var ordinary = new AppUser { Id=Guid.NewGuid(), Role=Roles.Admin, IsActive=true };
    proxy.TestUsers=[owner, super, ordinary];
    proxy.TestBookings=[mixed];
    var claim = new Payment { BookingId=bookingId, ProofKey="Proofs/synthetic.png" };
    proxy.TestPayments=[claim];
    var flow = new BookingService(persistent, persistent, persistent, persistent, persistent);
    Reject(() => flow.AdminAction(mixed, "verify", super.Id, "", 0, ""), "persistent mixed Admin action remains gated");
    Reject(() => flow.Cancel(mixed, owner.Id, true), "persistent mixed cancellation remains gated");
    Reject(() => flow.CorrectProof(mixed, owner.Id, "SYNTHETIC", null), "persistent mixed correction remains gated");
    var adminController = new AdminController(persistent, flow, null!, null!, null!);
    var customerController = new BookingsController(persistent, flow, null!, null!, null!);
    void Actor(Microsoft.AspNetCore.Mvc.Controller controller, AppUser user) =>
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier,user.Id.ToString()),
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role,user.Role)],"synthetic"))
            }
        };
    Actor(adminController, ordinary);
    Check(adminController.Details(bookingId) is Microsoft.AspNetCore.Mvc.ForbidResult &&
          adminController.Action(bookingId,"reject") is Microsoft.AspNetCore.Mvc.ForbidResult &&
          adminController.Proof(claim.Id) is Microsoft.AspNetCore.Mvc.ForbidResult,
        "ordinary Admin cannot access mixed details, action or proof");
    Actor(adminController, super);
    Check(adminController.Details(bookingId) is Microsoft.AspNetCore.Mvc.NotFoundResult &&
          adminController.Action(bookingId,"reject") is Microsoft.AspNetCore.Mvc.NotFoundResult &&
          adminController.Proof(claim.Id) is Microsoft.AspNetCore.Mvc.NotFoundResult,
        "persistent mixed details, action and proof stay closed even to SuperAdmin");
    Actor(customerController, owner);
    Check(customerController.Details(bookingId) is Microsoft.AspNetCore.Mvc.NotFoundResult &&
          customerController.Proof(claim.Id) is Microsoft.AspNetCore.Mvc.NotFoundResult,
        "persistent mixed Customer detail and proof stay closed");
    Actor(customerController, new AppUser { Id=Guid.NewGuid(), Role=Roles.Customer });
    Check(customerController.Details(bookingId) is Microsoft.AspNetCore.Mvc.NotFoundResult &&
          customerController.Proof(claim.Id) is Microsoft.AspNetCore.Mvc.NotFoundResult,
        "another Customer cannot read mixed detail or proof");
    var standard = new Booking { Id=Guid.NewGuid(), CustomerId=ownerId };
    proxy.TestBookings=[mixed, standard];
    claim.ReviewStatus="Correction Requested";
    proxy.TestPayments.Add(new Payment { BookingId=standard.Id, ReviewStatus="Correction Requested" });
    Actor(adminController, ordinary);
    adminController.Index(null);
    var corrections = (HashSet<Guid>)adminController.ViewBag.CorrectionBookingIds;
    Check(corrections.SetEquals([standard.Id]), "ordinary Admin list includes only authorized standard correction status");
    Actor(adminController, super);
    adminController.Index(null);
    corrections = (HashSet<Guid>)adminController.ViewBag.CorrectionBookingIds;
    Check(corrections.SetEquals([standard.Id]), "persistent mixed correction remains hidden even from SuperAdmin lists");
    var demoMixed = new Booking { Id=Guid.NewGuid(), CustomerId=customer.Id, MixedPeople=result };
    store.Bookings.Add(demoMixed);
    store.Payments.Add(new Payment { BookingId=demoMixed.Id, ReviewStatus="Correction Requested" });
    var demoAdminController = new AdminController(store, workflow, null!, null!, null!);
    Actor(demoAdminController, admin);
    demoAdminController.Index(null);
    Check(!((HashSet<Guid>)demoAdminController.ViewBag.CorrectionBookingIds).Contains(demoMixed.Id),
        "ordinary Admin list hides synthetic mixed correction status");
    Actor(demoAdminController, workflowOwner);
    demoAdminController.Index(null);
    Check(((HashSet<Guid>)demoAdminController.ViewBag.CorrectionBookingIds).Contains(demoMixed.Id),
        "active SuperAdmin list can see an explicitly supplied Demo mixed correction status");
    var view = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),"Frontend","GlamByMariya.Web","Views","Admin","_MixedBookingDetails.cshtml"));
    Check(view.Contains("reviewRevision = payment.PersistentReviewRevision") && view.Contains("reviewVersion = payment.PersistentReviewVersion") &&
          view.Contains("name=\"reviewRevision\"") && view.Contains("name=\"reviewVersion\""), "mixed review carries persistent proof revision and row version (source contract)");
    Console.WriteLine($"{checks} mixed read-model preparation checks passed; fake snapshots only.");
    return;
}
if (args.Contains("--persistent-mixed-guard-only"))
{
    var persistent = System.Reflection.DispatchProxy.Create<IPersistentStudioStore, PersistentQuoteProxy>();
    var proxy = (PersistentQuoteProxy)persistent;
    var flow = new BookingService(persistent, persistent, persistent, persistent, persistent);
    void Unavailable(Action action, string label)
    {
        try { action(); }
        catch (InvalidOperationException e) when (e.Message == "Mixed-service bookings are not available in persistent storage.")
        { Check(true, label); return; }
        throw new Exception("Expected precise persistent mixed guard: " + label);
    }
    var people = new[] { new MixedPersonSelection(service.Id, []), new MixedPersonSelection(service.Id, []) };
    Unavailable(() => flow.QuoteMixed(people), "persistent mixed quote fails closed");
    Unavailable(() => flow.FindMixedAlternatives(people, DateTime.Today, 1), "persistent mixed alternatives fail closed");
    Unavailable(() => flow.CreateMixed(customer.Id, people, DateTime.Today.AddDays(10), "Synthetic address", "Quezon City", 500,
        "GCash", "SYNTHETIC", "proof", true, true, 1000, 500), "persistent mixed creation fails closed");
    Check(proxy.Arguments == null, "no persistent booking command ran");
    var controller = new BookingsController(persistent, flow, null!, null!, null!);
    Check(controller.MixedSelection() is Microsoft.AspNetCore.Mvc.NotFoundResult, "persistent mixed selection route fails closed");
    Check(controller.MixedQuote(people) is Microsoft.AspNetCore.Mvc.NotFoundResult, "persistent mixed quote route fails closed");
    Check(controller.MixedAvailability(null!) is Microsoft.AspNetCore.Mvc.NotFoundResult, "persistent mixed availability route fails closed");
    Check(controller.MixedAlternatives(null!) is Microsoft.AspNetCore.Mvc.NotFoundResult, "persistent mixed alternatives route fails closed");
    Check(controller.MixedValidateSlot(null!) is Microsoft.AspNetCore.Mvc.NotFoundResult, "persistent mixed slot route fails closed");
    var submit = controller.CreateMixed(default, "", "", 0, "", "", null, false, false, null, null, null, Guid.Empty, null, null).GetAwaiter().GetResult();
    Check(submit is Microsoft.AspNetCore.Mvc.NotFoundResult, "persistent mixed submission route fails closed");
    Console.WriteLine($"{checks} persistent mixed guard checks passed; no PostgreSQL was opened.");
    return;
}
if (args.Contains("--persistent-review-only"))
{
    var id = Guid.NewGuid();
    var payment = new Payment { Id = id, ProofKey = "Proofs/synthetic.png", PersistentReviewRevision = 3, PersistentReviewVersion = "42" };
    BookingService.ValidatePersistentPaymentReview(payment, id, 3, "42", false);
    Check(true, "matching unverified claim accepted");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, 2, "42", false), "stale revision rejected");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, null, "42", false), "missing revision rejected");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, -1, "42", false), "negative revision rejected");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, Guid.NewGuid(), 3, "42", false), "wrong payment rejected");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, 3, "43", false), "changed row version rejected");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, 3, "invalid", false), "malformed row version rejected");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, 3, null, false), "missing row version rejected");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, Guid.Empty, 3, "42", false), "empty payment identifier rejected");
    payment.ProofKey = null;
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, 3, "42", false), "missing proof rejected");
    payment.ProofKey = "Proofs/synthetic.png";
    payment.Verified = true; payment.PersistentReviewRevision = 4;
    BookingService.ValidatePersistentPaymentReview(payment, id, 3, "42", true);
    Check(true, "exact already-committed verification retry accepted without another mutation");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, 2, "42", true), "older confirmed review rejected");
    Reject(() => BookingService.ValidatePersistentPaymentReview(payment, id, long.MaxValue, "42", true), "overflowing revision rejected");
    var gate = new object(); var changed = new Payment { Id = id, ProofKey = "Proofs/new.png", PersistentReviewRevision = 0, PersistentReviewVersion = "42" };
    var verifiedWrongProof = false;
    for (var run = 0; run < 100; run++)
    {
        changed.PersistentReviewRevision = 0; changed.Verified = false;
        using var barrier = new Barrier(2);
        var correct = Task.Run(() => { barrier.SignalAndWait(); lock (gate) { if (!changed.Verified) changed.PersistentReviewRevision++; } });
        var verify = Task.Run(() => { barrier.SignalAndWait(); lock (gate) { try { BookingService.ValidatePersistentPaymentReview(changed, id, 0, "42", false); changed.Verified = true; } catch (InvalidOperationException) { } if (changed.Verified && changed.PersistentReviewRevision != 0) verifiedWrongProof = true; } });
        Task.WaitAll(correct, verify);
    }
    Check(!verifiedWrongProof, "fake serialized correction and verification never verify a changed claim");
    var baseSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Backend", "GlamByMariya.Infrastructure", "Data", "PostgresStudioStore.cs"));
    var publicSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Backend", "GlamByMariya.Infrastructure", "Data", "PublicStudioStore.cs"));
    var controllerSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "Controllers", "AdminController.cs"));
    Check(baseSource.Contains("Admin(admin); var b=LockBooking(booking);") && baseSource.Contains("FOR UPDATE OF p") && baseSource.Contains("FOR UPDATE\""), "makeup and public payment rows are locked after staff and booking checks (source contract)");
    Check(publicSource.Contains("Admin(admin);") && publicSource.Contains("CheckPaymentReview(booking, paymentId, reviewRevision"), "public review checks run after staff authorization (source contract)");
    Check(controllerSource.Contains("[HttpPost, ValidateAntiForgeryToken]") && controllerSource.Contains("if (!workflow.CanManageBooking(b, UserId)) return Forbid();"), "controller retains antiforgery and actor checks (source contract)");
    Check(controllerSource.Contains("current.PersistentReviewVersion != reviewVersion") && controllerSource.Contains("current?.PersistentReviewRevision != reviewRevision"), "proof retrieval requires the displayed claim version (source contract)");
    Console.WriteLine($"{checks} persistent payment-review checks passed; fake concurrency and source contracts only.");
    return;
}
if (args.Contains("--persistent-quote-only"))
{
    var quote = workflow.Quote(service.Id, []);
    var persistent = System.Reflection.DispatchProxy.Create<IPersistentStudioStore, PersistentQuoteProxy>();
    var proxy = (PersistentQuoteProxy)persistent;
    proxy.TestServices = store.Services;
    var persistentFlow = new BookingService(persistent, persistent, persistent, persistent, persistent);
    var persistentStart = DateTime.Today.AddDays(2).AddHours(10);
    Reject(() => persistentFlow.Create(customer.Id, service.Id, [], persistentStart, "Test address", "Quezon City", 1, "GCash", "REF", null, true, travelAcknowledged: true, latitude: 14.5547m, longitude: 121.0244m), "persistent submission requires a reviewed quote");
    Check(proxy.Arguments == null, "missing quote never reaches persistent repository");
    var taken = new Booking { CustomerId = Guid.NewGuid(), Status = "Confirmed", Start = persistentStart, Service = new(service.Id, service.Name, service.Price, service.Minutes), BufferMinutesSnapshot = 45 };
    proxy.TestBookings.Add(taken);
    Reject(() => persistentFlow.Create(customer.Id, service.Id, [], persistentStart, "Test address", "Quezon City", 1, "GCash", "REF", null, true, travelAcknowledged: true, expectedTotal: quote.FinalTotal, expectedMinimum: quote.RequiredDownPayment, latitude: 14.5547m, longitude: 121.0244m),
        "persistent submission for an already reserved time is rejected before the database insert");
    Check(proxy.Arguments == null, "overlapping submission never reaches persistent repository");
    proxy.TestBookings.Remove(taken);
    persistentFlow.Create(customer.Id, service.Id, [], persistentStart, "Test address", "Quezon City", 1, "GCash", "REF", null, true, travelAcknowledged: true, expectedTotal: quote.FinalTotal, expectedMinimum: quote.RequiredDownPayment, latitude: 14.5547m, longitude: 121.0244m);
    Check(proxy.Arguments != null && (decimal?)proxy.Arguments[14] == quote.FinalTotal && (decimal?)proxy.Arguments[15] == quote.RequiredDownPayment, "persistent workflow forwards both reviewed amounts to repository");
    BookingService.ValidateExpectedQuote(quote, quote.FinalTotal, quote.RequiredDownPayment);
    Check(true, "matching reviewed total and minimum accepted");
    Reject(() => BookingService.ValidateExpectedQuote(quote, quote.FinalTotal + 1, quote.RequiredDownPayment), "changed total rejected even with matching minimum");
    Reject(() => BookingService.ValidateExpectedQuote(quote, quote.FinalTotal, quote.RequiredDownPayment + 1), "changed minimum rejected");
    Reject(() => BookingService.ValidateExpectedQuote(quote, quote.FinalTotal, null), "partial reviewed quote rejected");
    Reject(() => BookingService.ValidateExpectedQuote(quote, null, quote.RequiredDownPayment), "missing total rejected");
    BookingService.ValidateExpectedQuote(quote, null, null);
    Check(true, "legacy direct repository calls remain compatible");
    var root = Directory.GetCurrentDirectory();
    foreach (var name in new[] { "PostgresStudioStore.cs", "PublicStudioStore.cs" })
    {
        var source = File.ReadAllText(Path.Combine(root, "Backend", "GlamByMariya.Infrastructure", "Data", name));
        var begin = source.IndexOf("SubmitBooking(", StringComparison.Ordinal);
        var validation = source.IndexOf("BookingService.ValidateExpectedQuote(quote, expectedTotal, expectedMinimum)", begin, StringComparison.Ordinal);
        var insert = source.IndexOf("INSERT INTO ", begin, StringComparison.Ordinal);
        Check(begin >= 0 && validation > begin && insert > validation, $"{name}: quote checked before booking insert (source contract)");
    }
    Console.WriteLine($"{checks} persistent quote checks passed; no PostgreSQL was opened.");
    return;
}
if (args.Contains("--mixed-retry-only"))
{
    foreach (var scenario in new[] { "service", "schedule", "addons", "quantity", "person order", "address", "method", "reference", "proof", "agreement", "travel consent", "policy", "travel version", "amount", "expected total", "expected minimum" })
    {
        var demo = new DemoStore(presentationAccounts: true); var flow = new BookingService(demo, demo, demo, demo, demo);
        var actor = demo.Users.First(x => x.Role == Roles.Customer); var services = demo.Services.Take(2).ToArray(); var addons = demo.AddOns.Take(2).ToArray();
        var start = DateTime.Today.AddDays(27).AddHours(8); var id = Guid.NewGuid();
        var people = new[] { new MixedPersonSelection(services[0].Id, [addons[0].Id, addons[1].Id]), new MixedPersonSelection(services[1].Id, []) };
        var quote = flow.QuoteMixed(people, start); var policy = demo.Settings.PolicyVersion; var travel = demo.Settings.TravelReminderVersion;
        Booking Submit(MixedPersonSelection[] selected, DateTime time, string address, string area, decimal amount, string method, string reference, string proof, bool agreed, bool acknowledged, decimal total, decimal minimum, string version, string travelVersion, Guid? customerId = null) =>
            flow.CreateMixed(customerId ?? actor.Id, selected, time, address, area, amount, method, reference, proof, agreed, acknowledged, total, minimum, id, version, travelVersion);
        var initialMixed = Submit(people, start, "Synthetic address", "Quezon City", quote.RequiredDownPayment, "GCash", "Ref-A", "proof-A", true, true, quote.FinalTotal, quote.RequiredDownPayment, policy, travel);
        var payment = demo.Payments.Single(x => x.BookingId == initialMixed.Id);
        var bookingBefore = System.Text.Json.JsonSerializer.Serialize(initialMixed); var paymentBefore = System.Text.Json.JsonSerializer.Serialize(payment);
        var bookingCount = demo.Bookings.Count; var retryPaymentCount = demo.Payments.Count;
        var changedPeople = scenario switch {
            "service" => new[] { new MixedPersonSelection(services[1].Id, [addons[0].Id, addons[1].Id]), people[1] },
            "addons" => new[] { new MixedPersonSelection(services[0].Id, [addons[0].Id]), people[1] },
            "quantity" => new[] { new MixedPersonSelection(services[0].Id, [addons[0].Id, addons[0].Id, addons[1].Id]), people[1] },
            "person order" => new[] { people[1], people[0] }, _ => people
        };
        try
        {
            Submit(changedPeople, scenario == "schedule" ? start.AddDays(1) : start, scenario == "address" ? "Different address" : "Synthetic address",
                "Quezon City", scenario == "amount" ? quote.RequiredDownPayment + 1 : quote.RequiredDownPayment,
                scenario == "method" ? "Maya" : "GCash", scenario == "reference" ? "Ref-B" : "Ref-A",
                scenario == "proof" ? "proof-B" : "proof-A", scenario != "agreement", scenario != "travel consent",
                scenario == "expected total" ? quote.FinalTotal + 1 : quote.FinalTotal,
                scenario == "expected minimum" ? quote.RequiredDownPayment + 1 : quote.RequiredDownPayment,
                scenario == "policy" ? "other" : policy, scenario == "travel version" ? "other" : travel);
            throw new Exception($"Expected changed {scenario} rejection");
        }
        catch (InvalidOperationException e) when (e.Message == "Submission ID was already used for different booking details.") { Check(true, $"changed {scenario} receives precise retry rejection"); }
        Check(bookingBefore == System.Text.Json.JsonSerializer.Serialize(initialMixed) && paymentBefore == System.Text.Json.JsonSerializer.Serialize(payment)
            && demo.Bookings.Count == bookingCount && demo.Payments.Count == retryPaymentCount, $"changed {scenario} preserves original records and counts");
        var reversedAddons = new[] { new MixedPersonSelection(services[0].Id, [addons[1].Id, addons[0].Id]), people[1] };
        demo.Settings.PolicyVersion = "new-policy"; demo.Settings.TravelReminderVersion = "new-travel"; actor.Name = "Changed profile";
        var retry = Submit(reversedAddons, start, " Synthetic address ", "quezon city", quote.RequiredDownPayment, "GCash", " Ref-A ", " proof-A ", true, true, quote.FinalTotal, quote.RequiredDownPayment, policy, travel);
        Check(retry.Id == initialMixed.Id && demo.Bookings.Count == bookingCount && demo.Payments.Count == retryPaymentCount, $"{scenario}: identical normalized retry survives policy and profile changes");
    }
    {
        var demo = new DemoStore(presentationAccounts: true); var flow = new BookingService(demo, demo, demo, demo, demo);
        var actor = demo.Users.First(x => x.Role == Roles.Customer); var item = demo.Services.First();
        var start = DateTime.Today.AddDays(25).AddHours(8); var people = new[] { new MixedPersonSelection(item.Id, []), new MixedPersonSelection(item.Id, []) };
        var quote = flow.QuoteMixed(people, start); var id = Guid.NewGuid();
        var before = demo.Bookings.Count; var paymentBefore = demo.Payments.Count;
        try { flow.CreateMixed(actor.Id, people, start, "Synthetic address", "Quezon City", quote.RequiredDownPayment, "GCash", "PIXEL", "staged-a", true, true, quote.FinalTotal, quote.RequiredDownPayment, id, "old", demo.Settings.TravelReminderVersion, "PIXELS-A"); throw new Exception("Expected stale policy rejection"); }
        catch (InvalidOperationException e) when (e.Message == "The booking policies changed. Reload and review them before submitting.") { Check(true, "initial stale policy receives precise rejection"); }
        try { flow.CreateMixed(actor.Id, people, start, "Synthetic address", "Quezon City", quote.RequiredDownPayment, "GCash", "PIXEL", "staged-a", true, true, quote.FinalTotal, quote.RequiredDownPayment, id, demo.Settings.PolicyVersion, "old", "PIXELS-A"); throw new Exception("Expected stale travel policy rejection"); }
        catch (InvalidOperationException e) when (e.Message == "The booking policies changed. Reload and review them before submitting.") { Check(true, "initial stale travel version receives precise rejection"); }
        Check(demo.Bookings.Count == before && demo.Payments.Count == paymentBefore, "stale policy creates no records");
        Booking SubmitImage(string path, string fingerprint) => flow.CreateMixed(actor.Id, people, start, "Synthetic address", "Quezon City", quote.RequiredDownPayment, "GCash", "PIXEL", path, true, true, quote.FinalTotal, quote.RequiredDownPayment, id, demo.Settings.PolicyVersion, demo.Settings.TravelReminderVersion, fingerprint);
        var initialImage = SubmitImage("staged-a", "PIXELS-A");
        Check(SubmitImage("staged-b", "PIXELS-A").Id == initialImage.Id, "same image fingerprint ignores different staged paths");
        try { SubmitImage("staged-c", "PIXELS-B"); throw new Exception("Expected proof mismatch"); }
        catch (InvalidOperationException e) when (e.Message == "Submission ID was already used for different booking details.") { Check(true, "changed image fingerprint receives precise retry rejection"); }
        Check(demo.Bookings.Count == before + 1 && demo.Payments.Count == paymentBefore + 1 && demo.Payments.Single(x => x.BookingId == initialImage.Id).ProofPath == "staged-a", "image retries preserve initial payment and record counts");
    }
    {
        var demo = new DemoStore(presentationAccounts: true); var flow = new BookingService(demo, demo, demo, demo, demo);
        var actor = demo.Users.First(x => x.Role == Roles.Customer); var item = demo.Services.First();
        var start = DateTime.Today.AddDays(24).AddHours(8);
        var selected = new[] { new MixedPersonSelection(item.Id, []), new MixedPersonSelection(item.Id, []) };
        var quote = flow.QuoteMixed(selected, start); var id = Guid.NewGuid();
        Booking SubmitNullable(IReadOnlyList<MixedPersonSelection>? people, string? area, string? reference) =>
            flow.CreateMixed(actor.Id, people, start, "Synthetic address", area, quote.RequiredDownPayment,
                "GCash", reference, "proof", true, true, quote.FinalTotal, quote.RequiredDownPayment,
                id, demo.Settings.PolicyVersion, demo.Settings.TravelReminderVersion);
        var initialBookings = demo.Bookings.Count; var initialPayments = demo.Payments.Count;
        foreach (var (label, action, expected) in new (string, Action, string)[] {
            ("people", () => SubmitNullable(null, "Quezon City", "Ref-null"), $"Choose between 2 and {BookingPricing.MaximumPeople} people."),
            ("area", () => SubmitNullable(selected, null, "Ref-null"), "The address must be within our service area."),
            ("reference", () => SubmitNullable(selected, "Quezon City", null), "Choose a payment method and provide a valid reference.")
        })
        {
            try { action(); throw new Exception($"Expected null {label} rejection"); }
            catch (InvalidOperationException e) when (e.Message == expected) { Check(true, $"null {label} retains exact initial validation error"); }
            Check(demo.Bookings.Count == initialBookings && demo.Payments.Count == initialPayments, $"null {label} leaves records unchanged");
        }
        var original = SubmitNullable(selected, "Quezon City", "Ref-null");
        var payment = demo.Payments.Single(p => p.BookingId == original.Id);
        var snapshot = System.Text.Json.JsonSerializer.Serialize(original) + System.Text.Json.JsonSerializer.Serialize(payment);
        foreach (var (label, action) in new (string, Action)[] {
            ("people", () => SubmitNullable(null, "Quezon City", "Ref-null")),
            ("area", () => SubmitNullable(selected, null, "Ref-null")),
            ("reference", () => SubmitNullable(selected, "Quezon City", null))
        })
        {
            try { action(); throw new Exception($"Expected null {label} retry rejection"); }
            catch (InvalidOperationException e) when (e.Message == "Submission ID was already used for different booking details.") { Check(true, $"null {label} retry retains exact mismatch error"); }
            Check(snapshot == System.Text.Json.JsonSerializer.Serialize(original) + System.Text.Json.JsonSerializer.Serialize(payment)
                && demo.Bookings.Count == initialBookings + 1 && demo.Payments.Count == initialPayments + 1,
                $"null {label} retry preserves booking and payment");
        }
    }
    Console.WriteLine($"All {checks} isolated mixed retry checks passed."); return;
}
if (args.Contains("--boundary-regression-only"))
{
    // A fresh in-memory fixture per run; never exercises a database or HTTP authorization.
    var demo = new DemoStore(presentationAccounts: true); var flow = new BookingService(demo, demo, demo, demo, demo);
    var boundaryCustomer = demo.Users.First(u => u.Role == Roles.Customer);
    var other = demo.AddUser("Other", "Customer", "other@example.test", "Synthetic123!", Roles.Customer);
    var boundaryAdmin = demo.Users.First(u => u.Role == Roles.Admin);
    var owner = demo.Users.First(u => u.Role == Roles.SuperAdmin);
    var look = demo.Services.First(); var start = DateTime.Today.AddDays(28).AddHours(8);
    var quote = flow.Quote(look.Id, [], 1, "auto", start);
    Booking Submit(DateTime at, string address = "Synthetic service address", decimal? amount = null, string method = "GCash", string reference = "BOUNDARY-1", bool agreed = true, bool travel = true, decimal? total = null) =>
        flow.Create(boundaryCustomer.Id, look.Id, [], at, address, "Quezon City", amount ?? quote.RequiredDownPayment, method, reference, null, agreed, 1, "auto", travel, total ?? quote.FinalTotal, quote.RequiredDownPayment, latitude: 14.5547m, longitude: 121.0244m);
    var initialBookings = demo.Bookings.Count; var initialPayments = demo.Payments.Count;
    foreach (var (label, action) in new (string, Action)[] {
        ("short address", () => Submit(start, address: "short")),
        ("overlong address", () => Submit(start, address: new string('x', 501))),
        ("missing agreement", () => Submit(start, agreed: false)),
        ("missing travel acknowledgement", () => Submit(start, travel: false)),
        ("stale quoted total", () => Submit(start, total: quote.FinalTotal + 1)),
        ("payment precision", () => Submit(start, amount: quote.RequiredDownPayment + 0.001m)),
        ("cash deposit", () => Submit(start, method: "Cash")),
        ("blank payment reference", () => Submit(start, reference: " "))
    })
    {
        Reject(action, label + " rejected");
        Check(demo.Bookings.Count == initialBookings && demo.Payments.Count == initialPayments, label + " leaves bookings and payments unchanged");
    }
    var booking = Submit(start); var payment = demo.Payments.Single(p => p.BookingId == booking.Id);
    Check(booking.Status == "Pending Payment" && flow.HoldsReservation(booking) && !payment.Verified, "new pending booking holds slot with unverified payment");
    Reject(() => flow.CheckSlot(start, quote.DurationMinutes), "pending booking blocks its slot");
    var history = booking.History.Count; var originalReference = payment.Reference;
    foreach (var actor in new[] { boundaryCustomer.Id, other.Id, Guid.NewGuid() })
        Reject(() => flow.AdminAction(booking, "verify", actor, "", 0, ""), "unauthorized or unknown actor cannot verify");
    Reject(() => flow.AdminAction(booking, "unknown-action", owner.Id, "", 0, ""), "unknown admin action rejected");
    Reject(() => flow.AdminAction(booking, "reject", owner.Id, " ", 0, ""), "rejection requires reason");
    Check(booking.Status == "Pending Payment" && !payment.Verified && booking.History.Count == history && payment.Reference == originalReference && flow.Paid(booking) == 0, "failed admin actions preserve booking, history and payment");
    var areas = demo.Settings.ServiceAreas;
    demo.Settings.ServiceAreas = "Synthetic other area";
    Reject(() => flow.AdminAction(booking, "verify", owner.Id, "", 0, ""), "verification rechecks service area");
    Check(booking.Status == "Pending Payment" && !payment.Verified && booking.History.Count == history, "failed verification is atomic for booking and payment");
    demo.Settings.ServiceAreas = areas;
    flow.AdminAction(booking, "verify", owner.Id, "", 0, "");
    Check(booking.Status == "Confirmed" && payment.Verified && flow.HoldsReservation(booking), "verified booking remains reserved");
    Reject(() => flow.CheckSlot(start, quote.DurationMinutes), "confirmed booking blocks its slot");
    history = booking.History.Count;
    Reject(() => flow.Cancel(booking, other.Id, true), "another Customer cannot cancel booking");
    Reject(() => flow.Cancel(booking, boundaryCustomer.Id, false), "owner cancellation needs confirmation");
    Reject(() => flow.OfferReschedule(booking, other.Id, start.AddDays(1)), "another Customer cannot request reschedule");
    Reject(() => flow.AdminAction(booking, "cancel", boundaryAdmin.Id, "synthetic", 0, "", true), "ordinary Admin cannot cancel as business");
    Check(booking.Status == "Confirmed" && payment.Verified && booking.History.Count == history && flow.HoldsReservation(booking), "unauthorized attempts preserve confirmed reservation and payment");
    Reject(() => Submit(start.AddDays(1), reference: originalReference), "duplicate payment reference rejected at another slot");
    Check(demo.Bookings.Count == initialBookings + 1 && demo.Payments.Count == initialPayments + 1, "duplicate reference creates no booking or payment");
    flow.Cancel(booking, boundaryCustomer.Id, true, "Synthetic cancellation");
    flow.CheckSlot(start, quote.DurationMinutes);
    Check(booking.Status == "Cancelled" && !flow.HoldsReservation(booking) && payment.Verified, "customer cancellation releases slot and preserves verified payment");
    Reject(() => flow.Cancel(booking, boundaryCustomer.Id, true), "duplicate customer cancellation rejected");
    // This tests the existing in-process mixed-submission behavior only. The
    // current implementation does not compare changed payloads on retry.
    var mixedStart = start.AddDays(3);
    var selection = new[] { new MixedPersonSelection(look.Id, []), new MixedPersonSelection(look.Id, []) };
    var mixedQuote = flow.QuoteMixed(selection, mixedStart);
    var submission = Guid.NewGuid();
    Booking SubmitMixed(Guid actor, DateTime at, string reference) => flow.CreateMixed(actor, selection, at,
        "Synthetic service address", "Quezon City", mixedQuote.RequiredDownPayment, "GCash", reference,
        "synthetic-proof.png", true, true, mixedQuote.FinalTotal, mixedQuote.RequiredDownPayment, submission, demo.Settings.PolicyVersion, demo.Settings.TravelReminderVersion);
    var beforeMixedBookings = demo.Bookings.Count; var beforeMixedPayments = demo.Payments.Count;
    using (var retryBarrier = new Barrier(2))
    {
        var retries = Enumerable.Range(0, 2).Select(_ => Task.Run(() => {
            retryBarrier.SignalAndWait(); return SubmitMixed(boundaryCustomer.Id, mixedStart, "MIXED-BOUNDARY-A");
        })).ToArray();
        Task.WaitAll(retries);
        Check(retries[0].Result.Id == retries[1].Result.Id, "concurrent identical mixed submissions return the same booking");
        Check(demo.Bookings.Count == beforeMixedBookings + 1 && demo.Payments.Count == beforeMixedPayments + 1,
            "concurrent mixed retries create one booking and one payment");
        var otherBooking = SubmitMixed(other.Id, mixedStart.AddDays(1), "MIXED-BOUNDARY-B");
        Check(otherBooking.Id != retries[0].Result.Id && otherBooking.CustomerId == other.Id,
            "same mixed submission ID is scoped to its Customer");
        Check(demo.Bookings.Count == beforeMixedBookings + 2 && demo.Payments.Count == beforeMixedPayments + 2,
            "second Customer submission creates a separate booking and payment");
    }
    Console.WriteLine($"All {checks} isolated fake-backed boundary regression checks passed."); return;
}
if (args.Contains("--staff-cancellation-completion-only"))
{
    var demo = new DemoStore(presentationAccounts: true); var flow = new BookingService(demo, demo, demo, demo, demo);
    var owner = demo.Users.First(u => u.Role == Roles.SuperAdmin); var staff = demo.Users.First(u => u.Role == Roles.Admin); var user = demo.Users.First(u => u.Role == Roles.Customer);
    var initial = demo.Bookings.Single(); var payment = demo.Payments.Single(p => p.BookingId == initial.Id); payment.ProofPath = "synthetic-proof.png";
    var staffPaymentCount = demo.Payments.Count; var reference = payment.Reference;
    Reject(() => flow.AdminAction(initial, "cancel", staff.Id, "synthetic reason", 0, "", true), "ordinary Admin cannot cancel");
    Reject(() => flow.AdminAction(initial, "cancel", user.Id, "synthetic reason", 0, "", true), "Customer cannot cancel as staff");
    Reject(() => flow.AdminAction(initial, "cancel", owner.Id, "synthetic reason", 0, ""), "staff cancellation requires explicit confirmation");
    Reject(() => flow.AdminAction(initial, "cancel", owner.Id, " ", 0, "", true), "staff cancellation requires reason");
    flow.AdminAction(initial, "cancel", owner.Id, "Synthetic business cancellation", 0, "", true);
    Check(initial.Status == "Cancelled" && !flow.HoldsReservation(initial) && initial.StaffCancelledBy == owner.Id && initial.StaffCancelledAt.HasValue && initial.StaffCancellationReason == "Synthetic business cancellation", "SuperAdmin cancellation records actor, timestamp and reason and releases pending reservation");
    Check(!payment.Verified && payment.Reference == reference && payment.ProofPath == "synthetic-proof.png" && demo.Payments.Count == staffPaymentCount && initial.RefundDue == 0 && initial.Refunded == 0, "pending cancellation preserves unverified payment and creates no refund");
    Reject(() => flow.VerifyCancelledReceipt(initial, staff.Id, payment.Id, payment.Revision, true), "ordinary Admin cannot verify cancelled receipt");
    Reject(() => flow.VerifyCancelledReceipt(initial, owner.Id, payment.Id, payment.Revision, true), "receipt verification requires available proof");
    var receiptProof = Path.GetTempFileName();
    try {
        payment.ProofPath = receiptProof;
        Reject(() => flow.VerifyCancelledReceipt(initial, owner.Id, payment.Id, payment.Revision + 1, true), "stale receipt revision rejected");
        Reject(() => flow.VerifyCancelledReceipt(initial, owner.Id, payment.Id, payment.Revision, false), "receipt verification requires review confirmation");
        flow.VerifyCancelledReceipt(initial, owner.Id, payment.Id, payment.Revision, true);
        Check(payment.Verified && payment.VerifiedBy == owner.Id && payment.VerifiedAt.HasValue && initial.Status == "Cancelled" && initial.RefundDue == 0 && initial.Refunded == 0, "receipt verification keeps cancellation and refund unchanged");
        Reject(() => flow.VerifyCancelledReceipt(initial, owner.Id, payment.Id, payment.Revision, true), "duplicate cancelled receipt verification rejected");
    } finally { File.Delete(receiptProof); }
    Reject(() => flow.AdminAction(initial, "cancel", owner.Id, "again", 0, "", true), "duplicate cancellation rejected");
    Reject(() => flow.AdminAction(initial, "complete", owner.Id, "", 0, "", true), "cancelled booking cannot be completed");
    var item = demo.Services.First(); var day = DateTime.Today.AddDays(30).AddHours(8); var quote = flow.Quote(item.Id, [], 1, "auto", day);
    Booking CreateStaffFixture(int shift) => flow.Create(user.Id, item.Id, [], day.AddDays(shift), "Synthetic service address", "Quezon City", quote.RequiredDownPayment, "GCash", "STAFF-ACTION-" + shift, "proof.png", true, 1, "auto", true, quote.FinalTotal, quote.RequiredDownPayment, latitude: 14.5547m, longitude: 121.0244m);
    var confirmedBooking = CreateStaffFixture(0); flow.AdminAction(confirmedBooking, "verify", owner.Id, "", 0, ""); var verifiedPayment = demo.Payments.Single(p => p.BookingId == confirmedBooking.Id); var staffVerifiedAt = verifiedPayment.VerifiedAt;
    Reject(() => flow.AdminAction(confirmedBooking, "complete", owner.Id, "", 0, "", true), "future appointment cannot be completed");
    flow.AdminAction(confirmedBooking, "cancel", owner.Id, "Synthetic confirmed cancellation", 0, "", true);
    Check(verifiedPayment.Verified && verifiedPayment.VerifiedBy == owner.Id && verifiedPayment.VerifiedAt == staffVerifiedAt && confirmedBooking.RefundDue == 0 && confirmedBooking.Refunded == 0, "confirmed cancellation preserves verified claim without inventing refund");
    var complete = CreateStaffFixture(1); flow.AdminAction(complete, "verify", owner.Id, "", 0, ""); var claim = demo.Payments.Single(p => p.BookingId == complete.Id); var before = demo.Payments.Count;
    complete.Start = DateTime.Now.AddDays(-2);
    Reject(() => flow.AdminAction(complete, "complete", staff.Id, "", 0, "", true), "ordinary Admin cannot complete");
    Reject(() => flow.AdminAction(complete, "complete", owner.Id, "", 0, ""), "completion requires explicit confirmation");
    flow.AdminAction(complete, "complete", owner.Id, "", 0, "", true);
    Check(complete.Status == "Completed" && complete.CompletedBy == owner.Id && complete.CompletedAt.HasValue, "manual completion records actor and timestamp");
    Check(demo.Payments.Count == before && claim.Verified && flow.Paid(complete) == claim.Amount && complete.Refunded == 0, "completion does not settle outstanding balance or create a payment or refund");
    Reject(() => flow.AdminAction(complete, "complete", owner.Id, "", 0, "", true), "duplicate completion rejected");
    var rejected = CreateStaffFixture(2); flow.AdminAction(rejected, "reject", owner.Id, "Synthetic rejection", 0, "");
    Reject(() => flow.AdminAction(rejected, "complete", owner.Id, "", 0, "", true), "rejected booking cannot be completed");
    // Deliberately synthetic states: the unpaid confirmed fixture is not claimed
    // to be reachable through normal booking verification or persistent SQL.
    foreach (var scenario in new[] { "unpaid", "partial", "full" })
    {
        var local = new DemoStore(presentationAccounts: true); var testFlow = new BookingService(local, local, local, local, local);
        var client = local.Users.First(u => u.Role == Roles.Customer);
        var testOwner = local.Users.First(u => u.Role == Roles.SuperAdmin);
        var serviceItem = local.Services.First();
        var start = DateTime.Today.AddDays(25).AddHours(8);
        var quoted = testFlow.Quote(serviceItem.Id, [], 1, "auto", start);
        // Online payment is always the exact down payment; "full" adds the balance collected afterwards.
        var appointment = testFlow.Create(client.Id, serviceItem.Id, [], start, "Synthetic service address", "Quezon City", quoted.RequiredDownPayment, "GCash", "FINANCE-" + scenario, "synthetic-proof.png", true, 1, "auto", true, quoted.FinalTotal, quoted.RequiredDownPayment, latitude: 14.5547m, longitude: 121.0244m);
        var deposit = local.Payments.Single(p => p.BookingId == appointment.Id);
        Reject(() => testFlow.AdminAction(appointment, "verify", client.Id, "", 0, ""), $"{scenario}: Customer cannot verify payment");
        Check(!deposit.Verified && testFlow.Paid(appointment) == 0, $"{scenario}: unverified claim is not collected");
        if (scenario == "unpaid") appointment.Status = "Confirmed";
        else testFlow.AdminAction(appointment, "verify", testOwner.Id, "", 0, "");
        if (scenario == "full")
        {
            local.Payments.Add(new() { BookingId = appointment.Id, Amount = quoted.FinalTotal - quoted.RequiredDownPayment, Method = "Cash", Reference = "FINANCE-full-balance", Verified = true });
            appointment.VerifiedPaid = testFlow.Paid(appointment);
        }
        var originalTotal = appointment.Total; var originalPaid = testFlow.Paid(appointment);
        var originalCount = local.Payments.Count; var originalRef = deposit.Reference; var wasVerified = deposit.Verified;
        appointment.Start = DateTime.Today.AddDays(-2).AddHours(8);
        testFlow.AdminAction(appointment, "complete", testOwner.Id, "", 0, "", true);
        Check(appointment.Status == "Completed" && appointment.Total == originalTotal && testFlow.Paid(appointment) == originalPaid
            && appointment.RemainingBalance == originalTotal - originalPaid,
            $"{scenario}: completion preserves agreed total, verified collections and outstanding balance");
        Check(local.Payments.Count == originalCount && local.Payments.Contains(deposit) && deposit.Reference == originalRef && deposit.Verified == wasVerified,
            $"{scenario}: completion preserves original payment evidence without inserting a payment");
        Reject(() => testFlow.AdminAction(appointment, "complete", testOwner.Id, "", 0, "", true), $"{scenario}: duplicate completion rejected");
        Check(local.Payments.Count == originalCount, $"{scenario}: duplicate completion creates no finance record");
    }
    Check(demo.AdminActivity().Any(a => a.TargetId == initial.Id && a.Action == "Appointment cancelled by SuperAdmin") && demo.AdminActivity().Any(a => a.TargetId == complete.Id && a.Action == "Appointment completed"), "activity contains cancellation and completion");
    // Database mode (the live site): the owner sees and may use both actions.
    var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web");
    var actionsView = File.ReadAllText(Path.Combine(webRoot, "Views", "Shared", "_StaffBookingActions.cshtml"));
    var adminSource = File.ReadAllText(Path.Combine(webRoot, "Controllers", "AdminController.cs"));
    var serviceSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Backend", "GlamByMariya.Infrastructure", "Services", "BookingService.cs"));
    Check(!actionsView.Contains("!DataStore.IsPersistent && User.IsInRole(\"SuperAdmin\")") && actionsView.Contains("balance in cash"), "the owner sees Cancel and Mark as completed in database mode, with the cash balance");
    Check(!adminSource.Contains("(operation is \"cancel\" or \"complete\") && (!User.IsInRole(\"SuperAdmin\") || store.IsPersistent)") && !serviceSource.Contains("Staff cancellation and manual completion are available in the isolated Demo only"), "database mode no longer refuses owner cancellation and completion");
    Check(serviceSource.Contains("store.IsPersistent && action == \"cancel\" && !confirmed") && serviceSource.Contains("store.IsPersistent && action == \"complete\" && !confirmed"), "database mode still requires confirming a cancellation or completion");
    Console.WriteLine($"All {checks} staff cancellation and completion service checks passed."); return;
}
if (args.Contains("--reschedule-policy-only"))
{
    var demo = new DemoStore(presentationAccounts: true); var flow = new BookingService(demo, demo, demo, demo, demo);
    var user = demo.Users.First(u => u.Role == Roles.Customer); var owner = demo.Users.First(u => u.Role == Roles.SuperAdmin); var staff = demo.Users.First(u => u.Role == Roles.Admin);
    var day = DateTime.Today.AddDays(30); demo.Settings.EarlyFeeEnabled = true; demo.Settings.EarlyStartHour = 5; demo.Settings.EarlyFee = 200;
    var policyService = demo.Services.First(); var initial = flow.Quote(policyService.Id, [], 1, "auto", day.AddHours(8));
    var booking = flow.Create(user.Id, policyService.Id, [], day.AddHours(8), "Synthetic address", "Quezon City", initial.RequiredDownPayment, "GCash", "RESCHEDULE-POLICY-1", "synthetic.png", true, 1, "auto", true, initial.FinalTotal, initial.RequiredDownPayment, latitude: 14.5547m, longitude: 121.0244m);
    flow.AdminAction(booking, "verify", owner.Id, "", 0, ""); var paid = flow.Paid(booking); var policyPaymentCount = demo.Payments.Count;
    var offer = flow.OfferReschedule(booking, user.Id, day.AddDays(1).AddHours(5));
    Check(offer.CurrentTotal == initial.FinalTotal && offer.ProposedTotal == initial.FinalTotal + 200 && booking.Start == day.AddHours(8), "offer shows current and revised total without moving reservation");
    Reject(() => flow.ConsentReschedule(booking, user.Id, offer.Id, false), "explicit consent required");
    Reject(() => flow.ConsentReschedule(booking, staff.Id, offer.Id, true), "another actor cannot consent");
    Reject(() => flow.ConsentReschedule(booking, user.Id, Guid.NewGuid(), true), "forged quote rejected");
    demo.Settings.EarlyFee = 250; Reject(() => flow.ConsentReschedule(booking, user.Id, offer.Id, true), "changed quote requires fresh review"); demo.Settings.EarlyFee = 200;
    flow.ConsentReschedule(booking, user.Id, offer.Id, true);
    Reject(() => flow.AdminAction(booking, "reschedule", staff.Id, "", 0, ""), "ordinary Admin cannot approve");
    demo.Settings.EarlyFee = 250; Reject(() => flow.AdminAction(booking, "reschedule", owner.Id, "", 0, ""), "approval rejects changed fee");
    Check(booking.Start == offer.OriginalStart && booking.Total == initial.FinalTotal && flow.Paid(booking) == paid, "failed approval preserves time, price and payment");
    demo.Settings.EarlyFee = 200; flow.AdminAction(booking, "reschedule", owner.Id, "", 0, "");
    Check(booking.Start == offer.ProposedStart && booking.Total == offer.ProposedTotal && booking.InitialBookingTotal == initial.FinalTotal, "approval creates revised total without mutating original snapshot");
    Check(booking.RescheduleConsents.Count == 1 && booking.ApprovedReschedules.Single().ApprovedBy == owner.Id && demo.Payments.Count == policyPaymentCount && flow.Paid(booking) == paid && booking.RefundDue == 0, "consent and approval retained without automatic payment or refund");
    Reject(() => flow.AdminAction(booking, "reschedule", owner.Id, "", 0, ""), "duplicate approval rejected");
    var second = flow.OfferReschedule(booking, user.Id, day.AddDays(2).AddHours(8));
    Check(second.CurrentTotal == offer.ProposedTotal && second.ProposedTotal == initial.FinalTotal && second.ProposedTotal - second.CurrentTotal == -200, "subsequent quote compares against latest revision and supports decreases");
    flow.ConsentReschedule(booking, user.Id, second.Id, true); flow.AdminAction(booking, "reschedule", owner.Id, "", 0, "");
    Check(booking.ApprovedReschedules.Count == 2 && booking.Total == initial.FinalTotal && booking.Note.Contains("please coordinate directly with Glam by Mariya"), "second revision and manual arrangement reminder preserved");
    Console.WriteLine($"All {checks} rescheduling policy service checks passed."); return;
}
if (args.Contains("--mixed-admin-only"))
{
    var demo = new DemoStore(presentationAccounts: true); var flow = new BookingService(demo, demo, demo, demo, demo);
    var member = demo.Users.Single(u => u.Role == Roles.Customer); var staff = demo.Users.Single(u => u.Role == Roles.Admin); var owner = demo.Users.Single(u => u.Role == Roles.SuperAdmin);
    var look = demo.Services.First(); MixedPersonSelection[] selection = [new(look.Id, []), new(look.Id, []), new(look.Id, [])];
    var date = DateTime.Today.AddDays(30).AddHours(8);
    Booking Make(int day) { var q = flow.QuoteMixed(selection, date.AddDays(day)); return flow.CreateMixed(member.Id, selection, date.AddDays(day), "Synthetic test address", "Quezon City", q.RequiredDownPayment, "GCash", Guid.NewGuid().ToString(), "synthetic-proof.png", true, true, q.FinalTotal, q.RequiredDownPayment, policyVersion: demo.Settings.PolicyVersion, travelReminderVersion: demo.Settings.TravelReminderVersion); }
    var managedBooking = Make(0); var payment = demo.Payments.Single(p => p.BookingId == managedBooking.Id); var total = managedBooking.Total; var duration = managedBooking.End - managedBooking.Start;
    Check(!flow.CanManageBooking(managedBooking, staff.Id) && flow.CanManageBooking(managedBooking, owner.Id), "mixed management fails closed for ordinary Admin; SuperAdmin permitted");
    Reject(() => flow.AdminAction(managedBooking, "verify", staff.Id, "", 0, ""), "ordinary Admin cannot verify mixed claims");
    Reject(() => flow.AdminAction(managedBooking, "verify", member.Id, "", 0, ""), "Customer cannot verify claims");
    Reject(() => flow.AdminAction(managedBooking, "verify", Guid.NewGuid(), "", 0, ""), "missing actor is denied");
    owner.IsActive = false; Reject(() => flow.AdminAction(managedBooking, "verify", owner.Id, "", 0, ""), "inactive SuperAdmin is denied"); owner.IsActive = true;
    Reject(() => flow.AdminAction(managedBooking, "approve", owner.Id, "", 0, ""), "unverified payment cannot be approved");
    flow.AdminAction(managedBooking, "verify", owner.Id, "", 0, "");
    Check(payment.Verified && payment.VerifiedBy == owner.Id && payment.VerifiedAt.HasValue && managedBooking.Status == "Confirmed", "verification records reviewer and time and approves atomically");
    Reject(() => flow.AdminAction(managedBooking, "verify", owner.Id, "", 0, ""), "duplicate mixed verification is explicitly rejected");
    flow.AdminAction(managedBooking, "approve", owner.Id, "", 0, ""); Check(managedBooking.History.Count(h => h.Action == "verify") == 1, "approval retry creates no extra payment or decision");
    Reject(() => flow.AdminAction(managedBooking, "reject", owner.Id, "Too late", 0, ""), "confirmed booking cannot be rejected as pending");
    Reject(() => flow.AdminAction(managedBooking, "cancel", owner.Id, "Staff cancellation", 0, ""), "staff cancellation without confirmation is blocked");
    Check(managedBooking.Status == "Confirmed" && flow.HoldsReservation(managedBooking), "unconfirmed staff cancellation retains reservation");
    var rejected = Make(1); var rejectedClaim = demo.Payments.Single(p => p.BookingId == rejected.Id);
    Reject(() => flow.AdminAction(rejected, "reject", owner.Id, " ", 0, ""), "rejection needs a reason");
    flow.AdminAction(rejected, "proof", owner.Id, "Please send a clearer screenshot", 0, "");
    flow.CorrectProof(rejected, member.Id, "REPLACEMENT", "new-synthetic-proof.png");
    Check(rejectedClaim.Revision == 1 && rejectedClaim.ProofHistory.Count == 1 && !rejectedClaim.Verified, "proof correction preserves original reference and proof history and stays unverified");
    flow.AdminAction(rejected, "reject", owner.Id, "Synthetic rejection", 0, "");
    Check(!flow.HoldsReservation(rejected) && rejected.RefundDue == 0 && demo.Payments.Contains(rejectedClaim), "rejection releases reservation but preserves unverified payment history");
    Check(rejected.History.Any(h => h.Reason == "Synthetic rejection") && demo.AdminActivity().Any(a => a.TargetId == rejected.Id && a.Action.Contains("rejected")), "rejection reason and Admin activity retained");
    var old = managedBooking.Start; var target = date.AddDays(3);
    demo.Settings.EarlyFeeEnabled = true; demo.Settings.EarlyStartHour = 5; demo.Settings.EarlyFee = 125;
    var feeOffer = flow.OfferReschedule(managedBooking, member.Id, target.Date.AddHours(5));
    Check(feeOffer.ProposedTotal == total + 125 && managedBooking.Start == old && managedBooking.Total == total, "fee-changing offer preserves original reservation and price until approval");
    demo.Settings.EarlyFeeEnabled = false;
    void Request(Booking item, DateTime proposed) { var quote = flow.OfferReschedule(item, member.Id, proposed); flow.ConsentReschedule(item, member.Id, quote.Id, true); }
    Request(managedBooking, target);
    Check(managedBooking.Start == old && managedBooking.RequestedStart == target, "reschedule proposal retains old reservation");
    Reject(() => flow.OfferReschedule(managedBooking, staff.Id, target), "reschedule ownership enforced");
    Reject(() => flow.OfferReschedule(managedBooking, member.Id, target.AddMinutes(7)), "off-grid replacement rejected");
    var competitor = Make(3);
    Reject(() => flow.AdminAction(managedBooking, "reschedule", owner.Id, "", 0, ""), "fresh competing pending reservation blocks approval");
    Check(managedBooking.Start == old && managedBooking.RequestedStart == target, "failed reschedule keeps original and proposed times");
    flow.AdminAction(competitor, "reject", owner.Id, "Synthetic release", 0, "");
    flow.AdminAction(managedBooking, "reschedule", owner.Id, "", 0, "");
    Check(managedBooking.Start == target && managedBooking.RequestedStart == null && managedBooking.Total == total && managedBooking.End - managedBooking.Start == duration, "approved replacement keeps immutable services, totals and duration");
    var other = Make(4); flow.AdminAction(other, "verify", owner.Id, "", 0, "");
    var raceTarget = date.AddDays(6); Request(managedBooking, raceTarget); Request(other, raceTarget);
    using (var barrier = new Barrier(2)) {
        var attempts = new[] { managedBooking, other }.Select(x => Task.Run(() => { barrier.SignalAndWait(); try { flow.AdminAction(x, "reschedule", owner.Id, "", 0, ""); return true; } catch (InvalidOperationException) { return false; } })).ToArray(); Task.WaitAll(attempts);
        Check(attempts.Count(t => t.Result) == 1 && new[] { managedBooking, other }.Count(x => x.Start == raceTarget) == 1, "concurrent replacements have exactly one winner");
    }
    Reject(() => flow.Cancel(managedBooking, member.Id, true), "mixed cancellation needs a reason");
    flow.Cancel(managedBooking, member.Id, true, "Synthetic Customer cancellation");
    Check(!flow.HoldsReservation(managedBooking) && managedBooking.CancellationFee == 200 && managedBooking.RefundDue == Math.Max(0, payment.Amount - 200), "Customer cancellation releases occupancy using unchanged verified-payment policy");
    Check(managedBooking.Note == "Synthetic Customer cancellation" && managedBooking.History.Any(h => h.Action == "Customer cancellation"), "Customer cancellation reason and history preserved");
    Reject(() => flow.Cancel(managedBooking, member.Id, true, "Again"), "repeated cancellation is rejected");
    look.Price += 100; look.Minutes += 10; Check(managedBooking.Total == total && managedBooking.End - managedBooking.Start == duration, "historical prices and duration do not follow catalog edits");
    var fakeUser = demo.AddUser("Second", "Customer", "second@example.test", "Synthetic123!", Roles.Customer);
    var http = new Microsoft.AspNetCore.Http.DefaultHttpContext(); http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.NameIdentifier, fakeUser.Id.ToString()), new(System.Security.Claims.ClaimTypes.Role, Roles.Customer)], "test"));
    var controller = new GlamByMariya.Web.Controllers.BookingsController(demo, flow, null!, null!, null!, null!) { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http } };
    Check(controller.Proof(payment.Id) is Microsoft.AspNetCore.Mvc.NotFoundResult, "another Customer cannot read the first Customer's proof");
    Check(controller.Details(managedBooking.Id) is Microsoft.AspNetCore.Mvc.NotFoundResult, "another Customer cannot inspect the first Customer's booking");
    Check(demo.AdminActivity().Any(a => a.Action == "Reschedule approved") && demo.AdminActivity().Any(a => a.Action.Contains("Payment verified")), "supported staff decisions appear in existing Activity Logs");
    Console.WriteLine($"All {checks} mixed Admin service/security checks passed."); return;
}
if (args.Contains("--mixed-pricing-only"))
{
    var mixedStore = new DemoStore(presentationAccounts: true); var mixedFlow = new BookingService(mixedStore, mixedStore, mixedStore, mixedStore, mixedStore);
    var softMixed = mixedStore.Services.Single(s => s.Name == "Soft Glam");
    var glamourMixed = mixedStore.Services.Single(s => s.Name == "Glamour Makeup");
    var bridalMixed = mixedStore.Services.Single(s => s.Name == "Bridal Makeup");
    var lashMixed = mixedStore.AddOns.Single(s => s.Name == "Lash application");
    var hairMixed = mixedStore.AddOns.Single(s => s.Name == "Hair styling");
    MixedPersonSelection[] threeMixed = [new(bridalMixed.Id, [lashMixed.Id]), new(glamourMixed.Id, [hairMixed.Id]), new(softMixed.Id, [])];
    var mixed = mixedFlow.QuoteMixed(threeMixed);
    Check(mixed.People.Count == 3 && mixed.ServiceTotal == 2850 && mixed.AddOnTotal == 1050 && mixed.FinalTotal == 3900, "three different services use individual catalog prices and add-ons");
    Check(mixed.GroupDiscount == 0 && mixed.DurationMinutes == 330 && mixed.RequiredDownPayment == 1950, "mixed group has no unapproved discount and sums sequential duration");
    var pair = mixedFlow.QuoteMixed(threeMixed.Take(2).ToArray());
    Check(pair.FinalTotal == 3300 && pair.DurationMinutes == 270, "two-person mixed pricing and duration");
    var fiveMixed = mixedFlow.QuoteMixed(Enumerable.Range(0, 5).Select(i => threeMixed[i % 3]).ToArray());
    Check(fiveMixed.People.Count == 5 && fiveMixed.FinalTotal == 7200 && fiveMixed.DurationMinutes == 600, "five-person mixed totals and duration");
    var tenMixed = mixedFlow.QuoteMixed(Enumerable.Repeat(new MixedPersonSelection(bridalMixed.Id, [hairMixed.Id]), 10).ToArray());
    Check(tenMixed.People.Count == 10 && tenMixed.FinalTotal == 23000 && tenMixed.DurationMinutes == 1650, "ten-person selection retains full duration without inventing availability");
    Reject(() => mixedFlow.QuoteMixed([new(Guid.Empty, []), threeMixed[1]]), "missing mixed service rejected");
    Reject(() => mixedFlow.QuoteMixed([new(Guid.NewGuid(), []), threeMixed[1]]), "unknown mixed service rejected");
    Reject(() => mixedFlow.QuoteMixed([new(softMixed.Id, [lashMixed.Id, lashMixed.Id]), threeMixed[1]]), "duplicate per-person add-on rejected");
    Reject(() => mixedFlow.QuoteMixed([threeMixed[0]]), "one-person mixed group rejected");
    mixedStore.Settings.LateFeeEnabled = true; mixedStore.Settings.LateStartHour = 17; mixedStore.Settings.LateEndHour = 23; mixedStore.Settings.LateFee = 234;
    mixedStore.Settings.EarlyFeeEnabled = true; mixedStore.Settings.EarlyStartHour = 5; mixedStore.Settings.EarlyFee = 123;
    var dateMixed = DateTime.Today.AddDays(30);
    var lateMixed = mixedFlow.QuoteMixed(threeMixed, dateMixed.AddHours(13));
    Check(lateMixed.LateFee == 234 && lateMixed.EarlyFee == 0 && lateMixed.FinalTotal == 4134 && lateMixed.RequiredDownPayment == 2067, "one late fee is included in a mixed quote and minimum");
    var earlyMixed = mixedFlow.QuoteMixed(Enumerable.Repeat(new MixedPersonSelection(bridalMixed.Id, []), 7).ToArray(), dateMixed.AddHours(5));
    Check(earlyMixed.EarlyFee == 123 && earlyMixed.LateFee == 0, "early fee takes priority for a long mixed selection");
    mixedStore.Settings.EarlyFeeEnabled = false; mixedStore.Settings.LateFeeEnabled = false; mixedStore.Settings.CloseHour = 20;
    var busyMixed = DateTime.Today.AddDays(40);
    mixedStore.Bookings.Add(new Booking { Start = busyMixed.AddHours(8), Service = new PriceSnapshot(Guid.NewGuid(), "Synthetic reserved service", 100, 300), People = 1, Status = "Confirmed", BufferMinutesSnapshot = 45 });
    mixedStore.Blocks.Add(new AvailabilityBlock(Guid.NewGuid(), busyMixed.AddDays(1).AddHours(8), busyMixed.AddDays(1).AddHours(20), "Synthetic block"));
    MixedPersonSelection[] tenHourMixed = Enumerable.Repeat(new MixedPersonSelection(bridalMixed.Id, []), 5).ToArray();
    var busyCalendar = mixedFlow.GetMixedAvailability(busyMixed, mixedFlow.QuoteMixed(tenHourMixed).DurationMinutes);
    Check(busyCalendar.Days.Single(d => d.Date == busyMixed.ToString("yyyy-MM-dd")).AvailableCount == 0, "ten-hour group cannot fit around a five-hour reserved appointment");
    var shortCalendar = mixedFlow.GetMixedAvailability(busyMixed, mixedFlow.QuoteMixed([new(softMixed.Id, []), new(softMixed.Id, [])]).DurationMinutes);
    Check(shortCalendar.Days.Single(d => d.Date == busyMixed.ToString("yyyy-MM-dd")).AvailableCount > 0, "a reservation does not automatically make the entire day unavailable");
    var suggestions = mixedFlow.FindMixedAlternatives(tenHourMixed, busyMixed, 10);
    Check(suggestions.Dates.Count == 3 && suggestions.Dates[0].Date == busyMixed.AddDays(2).ToString("yyyy-MM-dd") && suggestions.Dates.All(d => d.Slots.Any(s => s.Available)), "suggestions skip the blocked date and contain server-checked start times");
    var none = mixedFlow.FindMixedAlternatives(tenHourMixed, busyMixed, 1);
    Check(none.Dates.Count == 0 && none.DaysChecked == 1 && none.CheckedThrough == busyMixed.AddDays(1), "no results report the checked range without inventing dates");
    Reject(() => mixedFlow.FindMixedAlternatives(tenMixed.People.Select(p => new MixedPersonSelection(p.Service.ItemId, p.AddOns.Select(a => a.ItemId).ToArray())).ToArray(), busyMixed, 3), "overlong selections cannot produce suggestions");
    var bookingDay = busyMixed.AddDays(12).AddHours(8);
    var claim = mixedFlow.QuoteMixed(threeMixed, bookingDay);
    var mixedCustomer = mixedStore.Users.First(u => u.Role == Roles.Customer);
    Reject(() => mixedFlow.CreateMixed(mixedStore.Users.First(u => u.Role == Roles.Admin).Id, threeMixed, bookingDay, "Synthetic address", "Quezon City", claim.RequiredDownPayment, "GCash", "MIXED-ADMIN", "synthetic-proof.png", true, true, claim.FinalTotal, claim.RequiredDownPayment, policyVersion: mixedStore.Settings.PolicyVersion, travelReminderVersion: mixedStore.Settings.TravelReminderVersion), "mixed booking rejects a non-customer actor");
    Reject(() => mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, bookingDay, "Synthetic address", "Quezon City", claim.RequiredDownPayment - 1, "GCash", "MIXED-LOW", "synthetic-proof.png", true, true, claim.FinalTotal, claim.RequiredDownPayment, policyVersion: mixedStore.Settings.PolicyVersion, travelReminderVersion: mixedStore.Settings.TravelReminderVersion), "mixed booking rejects payment below minimum");
    Reject(() => mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, bookingDay, "Synthetic address", "Quezon City", claim.RequiredDownPayment, "GCash", "MIXED-NO-AGREEMENT", "synthetic-proof.png", false, true, claim.FinalTotal, claim.RequiredDownPayment, policyVersion: mixedStore.Settings.PolicyVersion, travelReminderVersion: mixedStore.Settings.TravelReminderVersion), "mixed booking rejects missing agreement");
    using (var mixedBarrier = new Barrier(2))
    {
        var attemptsMixed = Enumerable.Range(0, 2).Select(i => Task.Run(() => { mixedBarrier.SignalAndWait(); try { return mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, bookingDay.AddMinutes(i * 15), "Synthetic address", "Quezon City", claim.RequiredDownPayment, "GCash", "MIXED-CLAIM-" + i, "synthetic-proof.png", true, true, claim.FinalTotal, claim.RequiredDownPayment, policyVersion: mixedStore.Settings.PolicyVersion, travelReminderVersion: mixedStore.Settings.TravelReminderVersion); } catch (InvalidOperationException) { return null; } })).ToArray();
        Task.WaitAll(attemptsMixed);
        var winner = attemptsMixed.Select(x => x.Result).OfType<Booking>().Single();
        Check(winner.MixedPeople.Count == 3 && winner.Total == claim.FinalTotal && (winner.End - winner.Start).TotalMinutes == claim.DurationMinutes, "mixed reservation preserves each service, total and duration");
        winner.AddOns.Clear();
        Check(winner.Total == claim.FinalTotal && winner.MixedPeople[0].AddOns.Count == 1, "mixed total remains derived from per-person snapshots if legacy add-on list changes");
        Check(mixedStore.Payments.Single(p => p.BookingId == winner.Id).Verified == false && mixedFlow.Paid(winner) == 0, "mixed payment is an unverified claim");
        Check(attemptsMixed.Count(x => x.Result != null) == 1, "concurrent mixed reservations have one winner");
        mixedFlow.AdminAction(winner, "verify", mixedStore.Users.First(u => u.Role == Roles.SuperAdmin).Id, "", 0, "");
        Check(winner.Status == "Confirmed" && mixedFlow.Paid(winner) == claim.RequiredDownPayment, "authorized verification accepts mixed booking totals and duration");
    }
    foreach (var (amount, index) in new[] { (claim.RequiredDownPayment + 50, 1), (claim.FinalTotal, 2) })
    {
        var paymentStart = bookingDay.AddDays(index);
        var paymentQuote = mixedFlow.QuoteMixed(threeMixed, paymentStart);
        var paymentsBefore = mixedStore.Payments.Count;
        Reject(() => mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, paymentStart, "Synthetic address", "Quezon City", amount, "GCash", "MIXED-AMOUNT-" + index, "synthetic-proof.png", true, true, paymentQuote.FinalTotal, paymentQuote.RequiredDownPayment, policyVersion: mixedStore.Settings.PolicyVersion, travelReminderVersion: mixedStore.Settings.TravelReminderVersion),
            index == 1 ? "custom mixed payment is rejected; only the exact down payment is accepted" : "full mixed payment is rejected; only the exact down payment is accepted");
        Check(mixedStore.Payments.Count == paymentsBefore, "rejected mixed payment amount creates no payment");
    }
    var replayKey = Guid.NewGuid();
    var replayDay = bookingDay.AddDays(5);
    var replayBooking = mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, replayDay, "Synthetic address", "Quezon City", claim.RequiredDownPayment, "GCash", "MIXED-REPLAY", "synthetic-proof.png", true, true, claim.FinalTotal, claim.RequiredDownPayment, replayKey, mixedStore.Settings.PolicyVersion, mixedStore.Settings.TravelReminderVersion);
    var replayBookingAgain = mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, replayDay, "Synthetic address", "Quezon City", claim.RequiredDownPayment, "GCash", "MIXED-REPLAY", "synthetic-proof.png", true, true, claim.FinalTotal, claim.RequiredDownPayment, replayKey, mixedStore.Settings.PolicyVersion, mixedStore.Settings.TravelReminderVersion);
    Check(replayBooking.Id == replayBookingAgain.Id && mixedStore.Payments.Count(p => p.BookingId == replayBooking.Id) == 1, "idempotent mixed replay returns one booking and payment");
    Reject(() => mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, replayDay.AddDays(1), "Synthetic address", "Quezon City", claim.RequiredDownPayment, "GCash", "MIXED-NO-PROOF", null, true, true, claim.FinalTotal, claim.RequiredDownPayment, policyVersion: mixedStore.Settings.PolicyVersion, travelReminderVersion: mixedStore.Settings.TravelReminderVersion), "mixed service requires a stored proof");
    bridalMixed.Archived = true;
    Reject(() => mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, replayDay.AddDays(1), "Synthetic address", "Quezon City", claim.RequiredDownPayment, "GCash", "MIXED-ARCHIVED", "synthetic-proof.png", true, true, claim.FinalTotal, claim.RequiredDownPayment, policyVersion: mixedStore.Settings.PolicyVersion, travelReminderVersion: mixedStore.Settings.TravelReminderVersion), "submission rechecks current catalog activation");
    bridalMixed.Archived = false;
    bridalMixed.Price = 1800; bridalMixed.Minutes = 150; lashMixed.Price = 400;
    Reject(() => mixedFlow.CreateMixed(mixedCustomer.Id, threeMixed, replayDay.AddDays(1), "Synthetic address", "Quezon City", claim.RequiredDownPayment, "GCash", "MIXED-PRICE-CHANGED", "synthetic-proof.png", true, true, claim.FinalTotal, claim.RequiredDownPayment, policyVersion: mixedStore.Settings.PolicyVersion, travelReminderVersion: mixedStore.Settings.TravelReminderVersion), "submission refuses an obsolete catalog quote");
    Check(replayBooking.Total == claim.FinalTotal && replayBooking.MixedSubmission!.ClaimedAmount == claim.RequiredDownPayment && replayBooking.MixedSubmission.Total == claim.FinalTotal && replayBooking.MixedSubmission.BufferMinutes == 45, "booking and immutable submission snapshot survive catalog changes");
    Check(mixed.People[0].Service.Price == 1500 && mixed.People[0].Service.Minutes == 120 && mixed.People[0].AddOns[0].Price == 250 && mixed.FinalTotal == 3900, "mixed quote snapshots survive later catalog changes");
    Console.WriteLine($"All {checks} mixed pricing and reservation checks passed; persistent mixed bookings remain unavailable."); return;
}
if (args.Contains("--late-fees-only"))
{
    var demo = new DemoStore(presentationAccounts: true); var flow = new BookingService(demo, demo, demo, demo, demo);
    var user = demo.Users.First(u => u.Role == Roles.Customer); var reviewer = demo.Users.First(u => u.Role == Roles.Admin);
    var day = DateTime.Today.AddDays(20); var item = demo.Services[0]; var extra = demo.AddOns[0];
    demo.Settings.BufferMinutes = 15;
    Check(flow.Quote(item.Id, [], 1, "auto", day.AddHours(19)).LateFee == 0, "late fee disabled by default");
    demo.Settings.LateFeeEnabled = true; demo.Settings.LateFee = 275; demo.Settings.LateStartHour = 17; demo.Settings.LateEndHour = 22;
    foreach (var look in demo.Services.Where(s => s.Active && !s.Archived))
        Check(flow.Quote(look.Id, [], 1, "auto", day.AddHours(19)).LateFee == 275, $"late fee applies once to {look.Name}");
    var normal = flow.Quote(item.Id, [], 1, "auto", day.AddHours(15));
    var before = flow.Quote(item.Id, [extra.Id], 1, "auto", day.AddHours(17).AddMinutes(-1));
    var exact = flow.Quote(item.Id, [], 1, "auto", day.AddHours(17));
    var late = flow.Quote(item.Id, [], 1, "auto", day.AddHours(19));
    var beforeShort = flow.Quote(item.Id, [], 1, "auto", day.AddHours(17).AddMinutes(-1));
    Check(normal.LateFee == 0 && beforeShort.LateFee == 0 && exact.LateFee == 275, "individual late fee includes the exact configured start boundary");
    Check(before.LateFee == 275, "an individual booking that runs past regular closing pays the late fee, the same as a group");
    Check(late.FinalTotal == normal.FinalTotal + 275 && late.RequiredDownPayment == Money.Percentage(late.FinalTotal, demo.Settings.DownPaymentPercent), "late fee charged once in total and down payment");
    var group = flow.Quote(item.Id, [extra.Id, extra.Id], 4, "group", day.AddHours(17));
    Check(group.LateFee == 275 && group.DurationMinutes == item.Minutes * 4 + extra.Minutes * 2, "group duration includes add-on quantities and fee applies once");
    var endsAtClose = flow.Quote(item.Id, [], 4, "group", day.AddHours(14));
    var crossesClose = flow.Quote(item.Id, [], 4, "group", day.AddHours(14).AddMinutes(1));
    Check(endsAtClose.LateFee == 0 && crossesClose.LateFee == 275, "package makeup ending exactly at closing is not late; ending after closing is late");
    Check(crossesClose.FinalTotal == endsAtClose.FinalTotal + 275 && crossesClose.RequiredDownPayment == Money.Percentage(crossesClose.FinalTotal, demo.Settings.DownPaymentPercent), "duration-based late fee applies once to quote and down payment");
    flow.CheckSlot(day.AddHours(14), endsAtClose.DurationMinutes);
    Check(endsAtClose.LateFee == 0, "a buffer extending past regular closing alone does not incur the late fee");
    var groupStart = day.AddDays(2).AddHours(14).AddMinutes(1);
    var lateGroupBooking = flow.Create(user.Id, item.Id, [], groupStart, "Synthetic address", "Quezon City", crossesClose.RequiredDownPayment, "GCash", "LATE-GROUP-SNAPSHOT", null, true, 4, "group", true, crossesClose.FinalTotal, crossesClose.RequiredDownPayment, latitude: 14.5547m, longitude: 121.0244m);
    Check(lateGroupBooking.LateFeeSnapshot == 275 && lateGroupBooking.Total == crossesClose.FinalTotal, "duration-based fee is preserved in the booking snapshot");
    flow.AdminAction(lateGroupBooking, "verify", reviewer.Id, "", 0, "");
    var lateGroupOffer = flow.OfferReschedule(lateGroupBooking, user.Id, day.AddDays(3).AddHours(13));
    Check(lateGroupOffer.LateFee == 0 && lateGroupOffer.ProposedTotal == lateGroupOffer.CurrentTotal - 275 && lateGroupBooking.RequestedStart == null && lateGroupBooking.LateFeeSnapshot == 275,
        "removing a duration-based late fee is shown in a quote that needs customer consent; nothing changes yet");
    flow.CheckSlot(day.AddHours(20).AddMinutes(45), 60);
    Reject(() => flow.CheckSlot(day.AddHours(21), 60), "buffer may not exceed final closing");
    Reject(() => flow.CheckSlot(day.AddHours(21), 120), "9 PM two-hour service is rejected by 10 PM closing");
    Reject(() => flow.CheckSlot(day.AddHours(22), 60), "final closing is not a permitted start");
    Reject(() => flow.CheckSlot(day.AddHours(18), group.DurationMinutes), "group and add-on duration must fit with buffer");
    var slots = flow.GetAvailability(day, 60, latitude: 14.5547m, longitude: 121.0244m).Days.Single(d => d.Date == day.ToString("yyyy-MM-dd")).Slots;
    Check(slots.Any(s => s.Start == day.AddHours(20).AddMinutes(45).ToString("yyyy-MM-ddTHH:mm") && s.Available) && !slots.Any(s => s.Start == day.AddHours(21).ToString("yyyy-MM-ddTHH:mm")), "calendar omits starts that cannot finish with buffer");
    demo.Settings.EarlyFeeEnabled = true; demo.Settings.EarlyStartHour = 5; demo.Settings.EarlyFee = 100;
    var early = flow.Quote(item.Id, [], 1, "auto", day.AddHours(5));
    Check(early.EarlyFee == 100 && early.LateFee == 0, "early start is charged early only");
    var earlyLong = flow.Quote(item.Id, [], 14, "group", day.AddHours(5));
    Check(earlyLong.EarlyFee == 100 && earlyLong.LateFee == 0, "early-start fee takes priority even when a package ends after regular closing");
    using var barrier = new Barrier(2);
    var attempts = Enumerable.Range(0, 2).Select(i => Task.Run(() => { barrier.SignalAndWait(); try { return flow.Create(user.Id, item.Id, [], day.AddHours(19).AddMinutes(i * 15), "Synthetic address", "Quezon City", late.RequiredDownPayment, "GCash", "LATE-RACE-" + i, null, true, 1, "auto", true, late.FinalTotal, late.RequiredDownPayment, latitude: 14.5547m, longitude: 121.0244m); } catch (InvalidOperationException) { return null; } })).ToArray();
    Task.WaitAll(attempts);
    var winners = attempts.Select(t => t.Result).OfType<Booking>().ToArray();
    Check(winners.Length == 1 && winners[0].LateFeeSnapshot == 275, "overlapping late reservations have one winner with a fee snapshot");
    var booking = winners[0];
    Reject(() => flow.CheckSlot(booking.End.AddMinutes(14), 30), "late reservation preparation buffer blocks conflicts");
    demo.Settings.LateFee = 500; demo.Settings.LateFeeEnabled = false;
    Check(booking.Total == late.FinalTotal && booking.LateFeeSnapshot == 275, "policy changes preserve booking snapshot");
    flow.AdminAction(booking, "verify", reviewer.Id, "", 0, "");
    var lateOffer = flow.OfferReschedule(booking, user.Id, day.AddDays(1).AddHours(15));
    Check(lateOffer.LateFee == 0 && lateOffer.ProposedTotal == lateOffer.CurrentTotal - 275 && booking.RequestedStart == null && booking.LateFeeSnapshot == 275,
        "removing a late fee is shown in a quote that needs customer consent; nothing changes yet");
    Console.WriteLine($"All {checks} late fee checks passed."); return;
}
if (args.Contains("--fees-only"))
{
    var demo = new DemoStore(presentationAccounts: true); var flow = new BookingService(demo, demo, demo, demo, demo);
    var user = demo.Users.First(u => u.Role == Roles.Customer); var reviewer = demo.Users.First(u => u.Role == Roles.Admin);
    var start = DateTime.Today.AddDays(20).AddHours(5);
    demo.Settings.EarlyStartHour = 5; demo.Settings.EarlyFee = 321; demo.Settings.EarlyFeeEnabled = true;
    foreach (var item in demo.Services.Where(s => s.Active && !s.Archived))
        Check(flow.Quote(item.Id, [], 1, "auto", start).EarlyFee == 321, $"early fee applies to {item.Name}");
    var item1 = demo.Services.First(s => s.Active && !s.Archived);
    var quote = flow.Quote(item1.Id, [], 1, "auto", start);
    var normal = flow.Quote(item1.Id, [], 1, "auto", start.Date.AddHours(demo.Settings.OpenHour));
    Check(quote.FinalTotal == normal.FinalTotal + 321 && quote.RequiredDownPayment == Money.Percentage(quote.FinalTotal, demo.Settings.DownPaymentPercent), "fee changes total and minimum payment");
    Check(flow.Quote(item1.Id, [], 1, "auto", start.AddMinutes(-1)).EarlyFee == 0 && normal.EarlyFee == 0, "early interval includes earliest start and excludes opening");
    flow.CheckSlot(start, quote.DurationMinutes);
    Reject(() => flow.CheckSlot(start.AddMinutes(-15), quote.DurationMinutes), "starts before early boundary unavailable");
    Reject(() => flow.Create(user.Id, item1.Id, [], start, "Synthetic address", "Quezon City", quote.RequiredDownPayment, "GCash", "FEE-STALE", null, true, 1, "auto", true, normal.FinalTotal, latitude: 14.5547m, longitude: 121.0244m), "stale quote rejected before reservation");
    var booking = flow.Create(user.Id, item1.Id, [], start, "Synthetic address", "Quezon City", quote.RequiredDownPayment, "GCash", "FEE-OK", null, true, 1, "auto", true, quote.FinalTotal, latitude: 14.5547m, longitude: 121.0244m);
    demo.Settings.EarlyFee = 500; demo.Settings.EarlyFeeEnabled = false;
    Check(booking.EarlyFeeSnapshot == 321 && booking.Total == quote.FinalTotal, "booking retains historical fee and total");
    flow.AdminAction(booking, "verify", reviewer.Id, "", 0, "");
    Check(booking.Status == "Confirmed", "existing pending booking can be verified after policy change");
    var feeOffer = flow.OfferReschedule(booking, user.Id, start.AddDays(1).Date.AddHours(10));
    Check(feeOffer.ProposedTotal != feeOffer.CurrentTotal && booking.RequestedStart == null,
        "a fee change from rescheduling is shown in a quote that needs customer consent; nothing changes yet");
    Console.WriteLine($"All {checks} Demo fee checks passed."); return;
}
if (args.Contains("--travel-rule-only"))
{
    // The travel rule: pure functions, all times Manila-local.
    const decimal mkLat = 14.5547m, mkLng = 121.0244m, fvLat = 14.7046m, fvLng = 121.0567m;
    Check(TravelPlanner.Minutes(mkLat, mkLng, fvLat, fvLng, 15m, 30) == 90, "Makati to Fairview at 15 km/h takes 90 minutes");
    Check(TravelPlanner.Minutes(mkLat, mkLng, mkLat, mkLng, 15m, 30) == 30, "the same point still takes the minimum");
    Check(TravelPlanner.Minutes(null, mkLng, fvLat, fvLng, 15m, 30) == 30 && TravelPlanner.Minutes(mkLat, mkLng, fvLat, null, 15m, 30) == 30, "a missing pin on either side takes the minimum");
    Check(TravelPlanner.Minutes(mkLat, mkLng, fvLat, fvLng, 60m, 30) == 30, "a fast trip never drops below the minimum");
    Check(TravelPlanner.Minutes(mkLat, mkLng, mkLat, mkLng, 15m, 0) == 0, "a zero minimum allows zero travel");
    Check(TravelPlanner.RoadFactor == 1.3m && TravelPlanner.RefusalMessage == "That time no longer fits the artist's travel. Choose another time.", "the road factor and refusal copy are fixed");

    var day = new DateTime(2030, 3, 4);
    DateTime At(int h, int m = 0) => day.AddHours(h).AddMinutes(m);
    TravelAppointment Stop(int h, int endH) => new(At(h), At(endH), 45, mkLat, mkLng);
    bool Fit(DateTime s, params TravelAppointment[] stops) =>
        TravelPlanner.Fits(s, 60, 45, fvLat, fvLng, stops, null, At(0), 15m, 30);
    var morning = Stop(7, 8);
    Check(!Fit(At(10), morning), "10:00 is too soon after a Makati stop that ends at 8:00");
    Check(Fit(At(10, 15), morning), "10:15 leaves buffer plus 90 minutes of travel");
    Check(!Fit(At(10, 15), morning, Stop(13, 14)), "a Makati stop at 13:00 is too close to a 10:15 booking");
    Check(Fit(At(10, 15), morning, Stop(15, 16)) && !Fit(At(12), morning, Stop(15, 16)), "a 15:00 stop allows 10:15 but not 12:00");
    Check(Fit(At(10)), "with no neighbours any time fits");

    var novaliches = ((decimal?)14.7290m, (decimal?)121.0370m);
    bool Today(DateTime s) => TravelPlanner.Fits(s, 60, 45, fvLat, fvLng, [], novaliches, At(9), 15m, 30);
    Check(!Today(At(9, 15)) && Today(At(9, 30)), "today the artist's current position needs the minimum travel");
    Check(TravelPlanner.Fits(At(9), 60, 45, fvLat, fvLng, [], novaliches, At(9).AddDays(-1), 15m, 30), "on a later day the artist's current position is ignored");
    Check(TravelPlanner.Fits(At(9, 15), 60, 45, fvLat, fvLng, [], null, At(9), 15m, 30), "today without a known position does not block");
    // Today: an earlier stop that already ended does not let the artist teleport; now still counts.
    bool Late(DateTime s) => TravelPlanner.Fits(s, 60, 45, fvLat, fvLng, [morning], novaliches, At(13), 15m, 30);
    Check(!Late(At(13, 15)) && !Late(At(14, 15)) && Late(At(14, 30)), "today a stop that ended at 8:00 in Makati still needs now plus 90 minutes: 13:15 refused, 14:30 accepted");
    Check(TravelPlanner.Fits(At(9, 15), 60, 45, fvLat, fvLng, [new TravelAppointment(At(7), At(8), 45, null, null)], null, At(0), 15m, 30)
        && !TravelPlanner.Fits(At(9), 60, 45, fvLat, fvLng, [new TravelAppointment(At(7), At(8), 45, null, null)], null, At(0), 15m, 30), "a previous stop without a pin uses the minimum travel time in Fits");

    // The demo store keeps the same travel settings, artist pins and neighbour list as the database.
    var travelDemo = new DemoStore(presentationAccounts: true);
    Check(travelDemo.Settings.TravelSpeedKmh == 15m && travelDemo.Settings.TravelMinimumMinutes == 30, "demo travel settings default to 15 km/h and 30 minutes");
    Check(travelDemo.GetArtistLocation() == new ArtistLocation(null, null, null, null, null), "the demo artist starts with no home or current pin");
    travelDemo.SetArtistHome(14.6760m, 121.0437m);
    travelDemo.SetArtistCurrent(mkLat, mkLng);
    var pins = travelDemo.GetArtistLocation();
    Check(pins.HomeLat == 14.6760m && pins.HomeLng == 121.0437m && pins.CurrentLat == mkLat && pins.CurrentLng == mkLng && pins.CurrentUpdatedAt != null, "the demo artist's home and current pins read back");
    travelDemo.SetArtistCurrent(null, null);
    var home = travelDemo.GetArtistLocation();
    Check(home.CurrentLat == null && home.CurrentLng == null && home.CurrentUpdatedAt == null && home.HomeLat == 14.6760m && home.HomeLng == 121.0437m, "clearing the demo current pin keeps home");
    Reject(() => travelDemo.SetArtistHome(15.5m, 121.0437m), "the demo store refuses a home pin outside Metro Manila");
    Reject(() => travelDemo.SetArtistCurrent(mkLat, null), "the demo store refuses half a current pin");
    var seed = travelDemo.Bookings.Single(); var seedDay = seed.Start.Date;
    var laterStop = new Booking { CustomerId = seed.CustomerId, CustomerName = seed.CustomerName, Service = seed.Service, Start = seedDay.AddHours(14), BufferMinutesSnapshot = 45, Address = seed.Address, Area = seed.Area, Latitude = fvLat, Longitude = fvLng, Status = "Confirmed" };
    var expiredStop = new Booking { CustomerId = seed.CustomerId, CustomerName = seed.CustomerName, Service = seed.Service, Start = seedDay.AddHours(16), BufferMinutesSnapshot = 45, Address = seed.Address, Area = seed.Area, Latitude = fvLat, Longitude = fvLng, PaymentDueAt = travelDemo.Now.AddMinutes(-1) };
    travelDemo.Bookings.Add(expiredStop); travelDemo.Bookings.Add(laterStop);
    var demoStops = travelDemo.TravelStops(seedDay, seedDay.AddDays(1), null);
    Check(demoStops.Count == 2 && demoStops[0].Start == seed.Start && demoStops[1].Start == laterStop.Start, "the demo neighbours are the seed booking and the later one, in order, without the expired one");
    Check(demoStops[0].End == seed.End && demoStops[0].BufferMinutes == seed.BufferMinutesSnapshot && demoStops[0].Latitude == seed.Latitude && demoStops[0].Longitude == seed.Longitude,
        "a demo neighbour carries its times, buffer and pin");
    var demoOthers = travelDemo.TravelStops(seedDay, seedDay.AddDays(1), seed.Id);
    Check(demoOthers.Count == 1 && demoOthers[0].Start == laterStop.Start, "the demo store leaves out the booking being moved");
    var inProgressStop = new Booking { CustomerId = seed.CustomerId, CustomerName = seed.CustomerName, Service = seed.Service, Start = seedDay.AddHours(18), BufferMinutesSnapshot = 45, Address = seed.Address, Area = seed.Area, Latitude = fvLat, Longitude = fvLng, Status = "InProgress" };
    travelDemo.Bookings.Add(inProgressStop);
    Check(travelDemo.TravelStops(seedDay, seedDay.AddDays(1), null).Any(s => s.Start == inProgressStop.Start), "an InProgress demo booking counts as a neighbour like the database function");
    travelDemo.Bookings.Remove(inProgressStop);
    Check(travelDemo.TravelStops(seedDay.AddDays(1), seedDay.AddDays(2), null).Count == 0, "another demo day has no neighbours");

    // Task 3: availability, booking and reschedule apply the rule (demo store).
    var tDemo = new DemoStore(presentationAccounts: true); var tFlow = new BookingService(tDemo, tDemo, tDemo, tDemo, tDemo);
    var tCustomer = tDemo.Users.First(u => u.Role == Roles.Customer); var tOwner = tDemo.Users.First(u => u.Role == Roles.SuperAdmin); var tStaff = tDemo.Users.First(u => u.Role == Roles.Admin);
    var soft = tDemo.Services.First(s => s.Minutes == 60);
    var tDay = DateTime.Today.AddDays(5);
    var makati = new Booking { CustomerId = tCustomer.Id, CustomerName = tCustomer.Name, Service = new(soft.Id, soft.Name, soft.Price, soft.Minutes), Start = tDay.AddHours(7), BufferMinutesSnapshot = 45,
        Address = "1 Synthetic Street, Makati", Area = "Makati", Latitude = mkLat, Longitude = mkLng, Status = "Confirmed", AppliedPricePerPerson = soft.Price };
    tDemo.Bookings.Add(makati);
    string SlotKey(int h, int m = 0) => tDay.AddHours(h).AddMinutes(m).ToString("yyyy-MM-ddTHH:mm");
    List<AvailableTimeSlot> DaySlots(decimal? lat, decimal? lng, Guid? exclude = null) =>
        tFlow.GetAvailability(new DateTime(tDay.Year, tDay.Month, 1), 60, exclude, lat, lng).Days.Single(d => d.Date == tDay.ToString("yyyy-MM-dd")).Slots;
    AvailableTimeSlot At15(List<AvailableTimeSlot> slots, int h, int m = 0) => slots.Single(s => s.Start == SlotKey(h, m));
    var fairviewSlots = DaySlots(fvLat, fvLng);
    Check(Enumerable.Range(0, 5).All(i => !At15(fairviewSlots, 9, i * 15).Available), "a Fairview pin hides 9:00-10:00 after a 7:00 Makati booking");
    Check(At15(fairviewSlots, 10, 15).Available, "a Fairview pin shows 10:15");
    Check(At15(fairviewSlots, 9).Reason == BookingService.UnavailableReason && !fairviewSlots.Any(s => s.Reason?.Contains("travel", StringComparison.OrdinalIgnoreCase) == true),
        "a time hidden by travel gives only the generic reason");
    var samePlace = DaySlots(mkLat, mkLng);
    Check(!At15(samePlace, 9).Available && At15(samePlace, 9, 15).Available, "a pin at the same place needs only the minimum travel");
    var pinless = DaySlots(null, null);
    Check(pinless.Count > 0 && pinless.All(s => !s.Available && s.Reason == "Pin your location to see available times."), "without a pin every time says to pin the location");

    var staleStart = tDay.AddHours(9).AddMinutes(45);
    var staleQuote = tFlow.Quote(soft.Id, [], 1, "auto", staleStart);
    Booking TravelCreate(DateTime start, decimal lat, decimal lng, string reference) => tFlow.Create(tCustomer.Id, soft.Id, [], start, "24 Synthetic Street", "Quezon City", staleQuote.RequiredDownPayment, "GCash",
        reference, "synthetic-proof.png", true, 1, "auto", true, staleQuote.FinalTotal, staleQuote.RequiredDownPayment, latitude: lat, longitude: lng);
    var bookingsBefore = tDemo.Bookings.Count; var staleMessage = "";
    try { TravelCreate(staleStart, fvLat, fvLng, "TRAVEL-STALE-1"); } catch (InvalidOperationException e) { staleMessage = e.Message; }
    Check(staleMessage == TravelPlanner.RefusalMessage && tDemo.Bookings.Count == bookingsBefore, "a stale page booking 9:45 from Fairview is refused with the travel message");

    // The booking pages pass the pin to ValidateSlot and Availability.
    T WireAs<T>(T controller, AppUser user) where T : Controller
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, user.Role) }, "Cookies"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, new NoTempData());
        return controller;
    }
    object? Field(IActionResult result, string name) => result is JsonResult { Value: { } v } ? v.GetType().GetProperty(name)?.GetValue(v) : null;
    var pages = WireAs(new BookingsController(tDemo, tFlow, null!, null!, null!), tCustomer);
    var staleCheck = pages.ValidateSlot(SlotKey(9, 45), soft.Id, [], null, 1, "auto", fvLat, fvLng);
    Check(Field(staleCheck, "available") is false && Field(staleCheck, "message") as string == TravelPlanner.RefusalMessage, "ValidateSlot refuses 9:45 for a Fairview pin");
    Check(Field(pages.ValidateSlot(SlotKey(10, 15), soft.Id, [], null, 1, "auto", fvLat, fvLng), "available") is true, "ValidateSlot accepts 10:15 for a Fairview pin");
    var unpinnedCheck = pages.ValidateSlot(SlotKey(10, 15), soft.Id, [], null, 1, "auto", null, null);
    Check(Field(unpinnedCheck, "available") is false && Field(unpinnedCheck, "message") as string == "Pin your location to see available times.", "ValidateSlot without a pin asks for one");
    var travelCalendar = pages.Availability(tDay.ToString("yyyy-MM"), soft.Id, [], null, 1, "auto", fvLat, fvLng) as JsonResult;
    Check(travelCalendar?.Value is AvailabilityCalendar cal && !cal.Days.Single(d => d.Date == tDay.ToString("yyyy-MM-dd")).Slots.Single(s => s.Start == SlotKey(9, 30)).Available
        && cal.Days.Single(d => d.Date == tDay.ToString("yyyy-MM-dd")).Slots.Single(s => s.Start == SlotKey(10, 15)).Available, "the Availability endpoint uses the pin");

    // Reschedule: the moved booking is not its own neighbour.
    var toNine = tFlow.OfferReschedule(makati, tCustomer.Id, tDay.AddHours(9));
    tFlow.ConsentReschedule(makati, tCustomer.Id, toNine.Id, true);
    tFlow.AdminAction(makati, "reschedule", tOwner.Id, "", 0, "");
    Check(makati.Start == tDay.AddHours(9) && makati.TravelOverrideBy == null && makati.TravelOverrideAt == null, "rescheduling 7:00 to 9:00 does not count the booking itself");

    // A request that fit when asked stops fitting after a new Fairview booking at 13:00.
    var toEleven = tFlow.OfferReschedule(makati, tCustomer.Id, tDay.AddHours(11).AddMinutes(15));
    tFlow.ConsentReschedule(makati, tCustomer.Id, toEleven.Id, true);
    var fairviewBooking = TravelCreate(tDay.AddHours(13), fvLat, fvLng, "TRAVEL-AFTERNOON-1");
    Check(tFlow.HoldsReservation(fairviewBooking), "a 13:00 Fairview booking fits after the 9:00 Makati one");
    string Refusal(Action action) { try { action(); } catch (InvalidOperationException e) { return e.Message; } return ""; }
    Check(Refusal(() => tFlow.AdminAction(makati, "reschedule", tOwner.Id, "", 0, "")) == TravelPlanner.RefusalMessage && makati.Start == tDay.AddHours(9),
        "approval re-runs the rule and refuses 11:15 before a 13:00 Fairview booking");
    Check(Refusal(() => tFlow.AdminAction(makati, "reschedule", tStaff.Id, "", 0, "", travelOverride: true)) == BookingService.TravelOverrideRefused && makati.Start == tDay.AddHours(9),
        "an Admin cannot override the travel rule");
    var adminPages = WireAs(new AdminController(tDemo, tFlow, null!, new MemoryInvitationService(TimeProvider.System), null!), tOwner);
    adminPages.Action(makati.Id, "reschedule", rescheduleQuoteId: toEleven.Id, travelOverride: true);
    Check(makati.Start == tDay.AddHours(11).AddMinutes(15) && makati.TravelOverrideBy == tOwner.Id && makati.TravelOverrideAt != null,
        $"the SuperAdmin override approves and records who and when ({adminPages.TempData["Error"]})");
    // A later approval that fits clears the old override.
    var toEight = tFlow.OfferReschedule(makati, tCustomer.Id, tDay.AddHours(8));
    tFlow.ConsentReschedule(makati, tCustomer.Id, toEight.Id, true);
    tFlow.AdminAction(makati, "reschedule", tOwner.Id, "", 0, "");
    Check(makati.Start == tDay.AddHours(8) && makati.TravelOverrideBy == null && makati.TravelOverrideAt == null, "a later approval that fits clears the travel override");

    // Where the artist is today: the latest finished (InProgress/Completed) booking ending by the start,
    // then the current pin, then home; with none, a location without a pin (minimum travel).
    var clock = new DateTime(2030, 3, 4, 12, 0, 0);
    var nowhere = new ArtistLocation(null, null, null, null, null);
    var homeAndHere = new ArtistLocation(14.6760m, 121.0437m, mkLat, mkLng, clock);
    var finished = new List<TravelAppointment> { new(clock.AddHours(-3), clock.AddHours(-2), 45, fvLat, fvLng), new(clock.AddHours(-5), clock.AddHours(-4), 45, 14.6000m, 121.0000m) };
    Check(BookingService.ArtistLocationAt(clock.AddHours(1), clock, [], nowhere) == ((decimal?)null, (decimal?)null), "an unpinned artist is a location without a pin");
    Check(TravelPlanner.Fits(clock.AddMinutes(15), 60, 45, fvLat, fvLng, [], BookingService.ArtistLocationAt(clock.AddMinutes(15), clock, [], nowhere), clock, 15m, 30) == false
        && TravelPlanner.Fits(clock.AddMinutes(30), 60, 45, fvLat, fvLng, [], BookingService.ArtistLocationAt(clock.AddMinutes(30), clock, [], nowhere), clock, 15m, 30),
        "today, with no earlier stop and an unpinned artist, the start needs now plus the minimum travel");
    Check(BookingService.ArtistLocationAt(clock.AddHours(1), clock, [], homeAndHere) == ((decimal?)mkLat, (decimal?)mkLng), "the current pin comes before home");
    Check(BookingService.ArtistLocationAt(clock.AddHours(1), clock, [], homeAndHere with { CurrentLat = null, CurrentLng = null, CurrentUpdatedAt = null }) == ((decimal?)14.6760m, (decimal?)121.0437m), "home is used without a current pin");
    Check(BookingService.ArtistLocationAt(clock.AddHours(1), clock, finished, homeAndHere) == ((decimal?)fvLat, (decimal?)fvLng), "today the latest finished booking is where the artist is, ahead of the current pin");
    Check(BookingService.ArtistLocationAt(clock.AddHours(-2).AddMinutes(-30), clock, finished, homeAndHere) == ((decimal?)14.6000m, (decimal?)121.0000m), "only a finished booking ending by the start counts");
    Check(BookingService.ArtistLocationAt(clock.AddDays(1), clock, finished, homeAndHere) == ((decimal?)mkLat, (decimal?)mkLng), "finished bookings only count for today");

    // The same through the demo store's travel check, when the clock allows a same-day test.
    var liveNow = DateTime.Now;
    if (liveNow.TimeOfDay > TimeSpan.FromHours(2) && liveNow.TimeOfDay < TimeSpan.FromHours(22))
    {
        var todayDemo = new DemoStore(presentationAccounts: true); var todayFlow = new BookingService(todayDemo, todayDemo, todayDemo, todayDemo, todayDemo);
        Check(!BookingService.TravelFits(todayDemo, liveNow.AddMinutes(20), 60, 45, fvLat, fvLng, null) && BookingService.TravelFits(todayDemo, liveNow.AddMinutes(40), 60, 45, fvLat, fvLng, null),
            "demo: an unpinned artist needs the 30-minute minimum before a booking today");
        todayDemo.SetArtistCurrent(mkLat, mkLng);
        var done = new Booking { CustomerId = tCustomer.Id, Service = new(soft.Id, soft.Name, soft.Price, soft.Minutes), Start = liveNow.AddMinutes(-90), BufferMinutesSnapshot = 45, Latitude = fvLat, Longitude = fvLng, Status = "Completed" };
        todayDemo.Bookings.Add(done);
        Check(todayDemo.FinishedStops(liveNow.Date, liveNow.Date.AddDays(1)).Count == 1, "demo: today's completed booking is a finished stop");
        Check(BookingService.TravelFits(todayDemo, liveNow.AddMinutes(40), 60, 45, fvLat, fvLng, null) && !BookingService.TravelFits(todayDemo, liveNow.AddMinutes(40), 60, 45, mkLat, mkLng, null),
            "demo: after a completed Fairview booking the artist starts from Fairview, not the current Makati pin");
        var todayStart = liveNow.Date.AddMinutes(Math.Ceiling(liveNow.AddMinutes(40).TimeOfDay.TotalMinutes / 15) * 15);
        var todaySlots = todayFlow.GetAvailability(new DateTime(liveNow.Year, liveNow.Month, 1), 60, null, mkLat, mkLng).Days.Single(d => d.Date == liveNow.ToString("yyyy-MM-dd")).Slots;
        var probe = todaySlots.SingleOrDefault(s => s.Start == todayStart.ToString("yyyy-MM-ddTHH:mm"));
        Check(probe == null || !probe.Available, "demo: availability today also starts from the completed booking");
    }
    else Console.WriteLine("SKIP: same-day demo travel checks need a clock between 02:00 and 22:00");
    Console.WriteLine($"All {checks} travel rule checks passed."); return;
}
if (args.Contains("--travel-form-only"))
{
    var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web");
    string Web(params string[] parts) => File.ReadAllText(Path.Combine([webRoot, .. parts]));
    var create = Web("Views", "Bookings", "Create.cshtml");
    var step2 = create[create.IndexOf("Choose your service location", StringComparison.Ordinal)..];
    step2 = step2[..step2.IndexOf("<span>03</span>", StringComparison.Ordinal)];
    var pinAt = step2.IndexOf("id=\"booking-location\"", StringComparison.Ordinal);
    var calendarAt = step2.IndexOf("_AvailabilityCalendar", StringComparison.Ordinal);
    Check(pinAt >= 0 && calendarAt >= 0 && pinAt < calendarAt, "step 2 shows the location block before the calendar");
    const string hint = "Pin your location to see available times.";
    var picker = Web("Views", "Shared", "_AvailabilityCalendar.cshtml");
    var site = Web("wwwroot", "js", "site.js");
    Check(picker.Contains(hint) || site.Contains(hint), "the calendar shows the pin hint until a location is pinned");
    foreach (var name in new[] { "site.js", "booking-steps.js", "booking-steps-phase2.js" })
        Check(Web("wwwroot", "js", name) is var text && text.Contains("'latitude'") && text.Contains("'longitude'"), $"{name} sends latitude and longitude with availability and slot checks");
    Check(Web("wwwroot", "js", "booking-location.js").Contains("booking-location-changed"), "the pin map announces booking-location-changed");
    Check(site.Contains("'booking-location-changed'") && site.Contains("refresh("), "the calendar reloads availability when the pin changes");
    var controller = Web("Controllers", "BookingsController.cs");
    Check(controller.Split("Times include travel from the artist's other appointments that day.").Length - 1 == 2, "the single-booking slot validation messages use the new travel copy");
    var mixedValidate = controller[controller.IndexOf("MixedValidateSlot", StringComparison.Ordinal)..];
    mixedValidate = mixedValidate[..mixedValidate.IndexOf("[HttpGet", StringComparison.Ordinal)];
    Check(mixedValidate.Contains("Travel feasibility has not yet been verified.") && !mixedValidate.Contains("Times include travel"), "the mixed slot message keeps its accurate wording (mixed bookings are not travel-checked)");
    var mixedBlock = create[create.IndexOf("id=\"mixed-calendar\"", StringComparison.Ordinal)..];
    mixedBlock = mixedBlock[..mixedBlock.IndexOf("</section>", StringComparison.Ordinal)];
    Check(mixedBlock.Contains("Travel feasibility has not yet been verified.") && !mixedBlock.Contains("Times include travel"), "the mixed calendar block keeps its accurate wording");
    Check(Web("Views", "Bookings", "Details.cshtml").Contains("Travel feasibility has not been verified.") && !Web("Views", "Bookings", "Details.cshtml").Contains("Times include travel"), "booking details keep the old wording in the mixed payment-claim block");
    Check(Web("Views", "Bookings", "Confirmation.cshtml").Contains("Travel feasibility has not been verified; transportation is separate.") && !Web("Views", "Bookings", "Confirmation.cshtml").Contains("Times include travel"), "the mixed confirmation keeps the old wording");
    var reschedule = Web("Views", "Bookings", "RescheduleReview.cshtml");
    Check(reschedule.Contains("ViewBag.MixedBooking == true ? \"Travel feasibility has not been verified.\" : \"Times include travel from the artist's other appointments that day.\""), "the reschedule review shows the travel copy only for single bookings");
    var copyFiles = Directory.GetFiles(Path.Combine(webRoot, "Views", "Bookings"), "*.cshtml")
        .Concat(Directory.GetFiles(Path.Combine(webRoot, "Views", "Shared"), "*.cshtml"))
        .Concat(Directory.GetFiles(Path.Combine(webRoot, "wwwroot", "js"), "*.js"))
        .Append(Path.Combine(webRoot, "Controllers", "BookingsController.cs"));
    foreach (var file in copyFiles)
        Check(!System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(file), "travel (time )?(is|has) not (been )?(estimated|included|verified)|travel not estimated|travel time to your location is not included|travel is not estimated", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            $"{Path.GetFileName(file)} no longer says travel is not estimated");
    Console.WriteLine($"All {checks} travel form checks passed."); return;
}
if (args.Contains("--travel-only"))
{
    var routes = new Dictionary<(string, string), TimeSpan> {
        [("SYNTHETIC:BASE", "SYNTHETIC:A")] = TimeSpan.FromHours(2),
        [("SYNTHETIC:A", "SYNTHETIC:B")] = TimeSpan.FromHours(2)
    };
    var estimator = new SyntheticTravelEstimator(routes);
    var planning = new OfflineTravelScheduler(estimator);
    var start = new DateTimeOffset(2030, 1, 2, 8, 0, 0, TimeSpan.FromHours(8));
    var anchor = new TravelAnchor("SYNTHETIC:BASE", start.AddHours(-3));
    var stop = new TravelStop("SYNTHETIC:A", start, TimeSpan.FromHours(4), TimeSpan.Zero);
    var result = planning.Review(anchor, stop, null, true, RouteMode.Driving, TimeSpan.Zero).Result;
    Check(result.Status == TravelReviewStatus.SyntheticFit && result.Projection?.Departure == start.AddHours(-2) && result.Projection.MakeupEnd == start.AddHours(4), "synthetic 8 AM start yields 6 AM departure and noon finish");
    Check(result.Projection!.EstimateStatus == RouteEstimateStatus.Synthetic && result.Projection.Evidence.Synthetic, "synthetic estimates are explicitly labeled");
    var next = new TravelStop("SYNTHETIC:B", start.AddHours(5), TimeSpan.FromHours(1), TimeSpan.Zero);
    Check(planning.Review(anchor, stop, next, true, RouteMode.Driving, TimeSpan.Zero).Result.Status == TravelReviewStatus.SyntheticConflict, "two-hour next trip cannot fit before 1 PM after noon finish");
    Check(planning.Review(anchor, stop, next with { MakeupStart = start.AddHours(6) }, true, RouteMode.Driving, TimeSpan.Zero).Result.Status == TravelReviewStatus.SyntheticFit, "touching next departure boundary fits synthetic schedule");
    Check(planning.Review(anchor, stop with { PreparationBuffer = TimeSpan.FromMinutes(15) }, next with { MakeupStart = start.AddHours(6) }, true, RouteMode.Driving, TimeSpan.Zero).Result.Status == TravelReviewStatus.SyntheticConflict, "preparation buffer prevents an otherwise touching next trip");
    Check(planning.Review(anchor with { ReadyAt = start.AddHours(-1) }, stop, null, true, RouteMode.Driving, TimeSpan.Zero).Result.Status == TravelReviewStatus.SyntheticConflict, "previous appointment readiness is respected");
    Check(planning.Review(anchor, stop, next with { LocationReference = "SYNTHETIC:UNKNOWN" }, true, RouteMode.Driving, TimeSpan.Zero).Result.Status == TravelReviewStatus.Unavailable, "unknown route never becomes zero-minute travel");
    Check(planning.Review(anchor, stop, null, false, RouteMode.Driving, TimeSpan.Zero).Result.Status == TravelReviewStatus.InvalidRequest, "unknown next appointment state fails closed");
    Check(estimator.Estimate(new RouteRequest("real address", "SYNTHETIC:A", null, start, RouteMode.Driving, TimeSpan.Zero)).Result.Status == RouteEstimateStatus.InvalidRequest, "fake estimator rejects nonsynthetic locations");
    var demo = new DemoStore(presentationAccounts: true); var serviceForTravel = new BookingService(demo, demo, demo, demo, demo);
    var serviceItem = demo.Services[0]; var addOn = demo.AddOns[0];
    var travelQuote = serviceForTravel.Quote(serviceItem.Id, [addOn.Id, addOn.Id], 4, "group");
    Check(travelQuote.DurationMinutes == serviceItem.Minutes * 4 + addOn.Minutes * 2, "travel-planning input can use existing quantity-based service duration");
    Console.WriteLine($"All {checks} offline synthetic travel checks passed; no route is verified.");
    return;
}
if (args.Contains("--reservation-only"))
{
    var demo = new DemoStore(presentationAccounts: true);
    demo.Settings.BufferMinutes = 15;
    var flow = new BookingService(demo, demo, demo, demo, demo);
    var user = demo.Users.First(u => u.Role == Roles.Customer);
    var reviewer = demo.Users.First(u => u.Role == Roles.Admin);
    var reservationOwner = demo.Users.First(u => u.Role == Roles.SuperAdmin);
    var look = demo.Services[0];
    var when = DateTime.Today.AddDays(20).AddHours(10);
    var quote = flow.Quote(look.Id, []);
    Booking Submit(DateTime start, string reference, decimal? amount = null) => flow.Create(user.Id, look.Id, [], start, "Synthetic address", "Quezon City", amount ?? quote.RequiredDownPayment, "GCash", reference, null, true, 1, "auto", true, latitude: 14.5547m, longitude: 121.0244m);
    using var barrier = new Barrier(2);
    var attempts = Enumerable.Range(0, 2).Select(i => Task.Run(() => { barrier.SignalAndWait(); try { return Submit(when.AddMinutes(i * 15), "SYNTHETIC-RACE-" + i); } catch (InvalidOperationException) { return null; } })).ToArray();
    Task.WaitAll(attempts);
    var winners = attempts.Select(t => t.Result).OfType<Booking>().ToArray();
    Check(winners.Length == 1, "overlapping simultaneous Demo submissions have exactly one winner");
    var reservation = winners[0];
    Check(reservation.Status == "Pending Payment" && reservation.BufferMinutesSnapshot == 15, "pending booking reserves a snapshotted buffer");
    Reject(() => flow.CheckSlot(reservation.Start.AddMinutes(1), 60), "pending service interval blocks overlaps");
    Reject(() => demo.AddBlock(new(Guid.NewGuid(), reservation.Start.AddMinutes(30), reservation.End.AddMinutes(1), "Synthetic"), reviewer.Id), "staff block cannot overlap a pending reservation");
    demo.Settings.BufferMinutes = 30;
    Reject(() => flow.CheckSlot(reservation.End.AddMinutes(14), 60), "existing snapshotted buffer remains reserved after settings change");
    flow.CheckSlot(reservation.End.AddMinutes(15), 60);
    Check(!flow.DemoOpenWindows(reservation.Start.Date).Any(w => w.From <= reservation.Start && w.To > reservation.Start), "open windows exclude the pending interval");
    var reservationCalendar = flow.GetAvailability(new DateTime(when.Year, when.Month, 1), 60, latitude: 14.5547m, longitude: 121.0244m);
    Check(reservationCalendar.Days.Single(x => x.Date == reservation.Start.ToString("yyyy-MM-dd")).Slots.Single(x => x.Start == reservation.Start.ToString("yyyy-MM-ddTHH:mm")).Available == false, "customer availability excludes pending slot");
    flow.AdminAction(reservation, "reject", reviewer.Id, "Synthetic rejection", 0, "");
    flow.CheckSlot(reservation.Start, 60);
    Check(true, "rejection releases the pending interval");
    var confirmed = Submit(reservation.Start, "SYNTHETIC-CONFIRMED");
    flow.AdminAction(confirmed, "verify", reviewer.Id, "", 0, "");
    Reject(() => flow.CheckSlot(confirmed.Start, 60), "confirmed appointment blocks overlapping starts");
    var destination = when.AddDays(1);
    var rescheduleOffer = flow.OfferReschedule(confirmed, user.Id, destination);
    flow.ConsentReschedule(confirmed, user.Id, rescheduleOffer.Id, true);
    Reject(() => flow.CheckSlot(confirmed.Start, 60), "original confirmed interval remains reserved after requesting reschedule");
    var competing = Submit(destination, "SYNTHETIC-DESTINATION");
    Reject(() => flow.AdminAction(confirmed, "reschedule", reservationOwner.Id, "", 0, ""), "approval rejects a newly occupied replacement");
    Check(confirmed.Start == reservation.Start && confirmed.RequestedStart == destination, "failed approval keeps the original reservation and request");
    flow.AdminAction(competing, "reject", reviewer.Id, "Synthetic rejection", 0, "");
    flow.AdminAction(confirmed, "reschedule", reservationOwner.Id, "", 0, "");
    flow.CheckSlot(reservation.Start, 60);
    Reject(() => flow.CheckSlot(destination, 60), "approved replacement is reserved");
    flow.Cancel(confirmed, user.Id, true);
    flow.CheckSlot(destination, 60);
    Check(confirmed.Status == "Cancelled" && confirmed.CancellationFee == 200, "cancellation retains its policy and releases the reservation");
    Reject(() => Submit(destination.AddDays(1), "SYNTHETIC-BAD-PAYMENT", 0), "failed submission creates no reservation");
    flow.CheckSlot(destination.AddDays(1), 60);
    var extra = demo.AddOns[0];
    var groupQuote = flow.Quote(look.Id, [extra.Id, extra.Id], 4, "group");
    var groupStart = destination.AddDays(3).Date.AddHours(8);
    var groupReservation = flow.Create(user.Id, look.Id, [extra.Id, extra.Id], groupStart, "Synthetic address", "Quezon City", groupQuote.RequiredDownPayment, "GCash", "SYNTHETIC-GROUP", null, true, 4, "group", true, latitude: 14.5547m, longitude: 121.0244m);
    Check((groupReservation.End - groupReservation.Start).TotalMinutes == look.Minutes * 4 + extra.Minutes * 2, "group reservation uses service and quantity-based add-on duration");
    Reject(() => flow.CheckSlot(groupReservation.End.AddMinutes(1), 60), "group reservation also blocks its preparation buffer");
    Console.WriteLine($"All {checks} synthetic reservation checks passed.");
    return;
}
var oldAvatars = new DemoAvatarStore("unused");
var presentationAvatars = new DemoAvatarStore("unused", true);
foreach (var (role, oldEmail, newEmail) in new[] { ("Customer", "removed-customer@example.test", "presentation-customer@example.test"), ("Admin", "removed-admin@example.test", "presentation-admin@example.test"), ("SuperAdmin", "removed-owner@example.test", "presentation-owner@example.test") })
{
    var oldUser = new AppUser { Role = role, Email = oldEmail };
    var newUser = new AppUser { Role = role, Email = newEmail };
    Check(!oldAvatars.Supports(oldUser) && !oldAvatars.Supports(newUser) && presentationAvatars.Supports(newUser) && !presentationAvatars.Supports(oldUser), $"{role} avatar mapping is restricted to the active seeded identity");
}
// Focused Demo-only regression; also runs as part of the full workflow suite.
{
    var demo = new DemoStore(presentationAccounts: true);
    var bookingService = new BookingService(demo, demo, demo, demo, demo);
    var user = demo.Users.First(u => u.Role == "Customer");
    var look = demo.Services[0];
    var extra = demo.AddOns[0];
    var start = DateTime.Today.AddDays(5).AddHours(8);
    var originalBuffer = demo.Settings.BufferMinutes;
    var history = System.Text.Json.JsonSerializer.Serialize(demo.Bookings);
    var paymentHistory = System.Text.Json.JsonSerializer.Serialize(demo.Payments);
    // Non-zero informational travel costs must still be excluded from online totals.
    demo.Settings.TravelFee = 275;
    var quote = bookingService.Quote(look.Id, [extra.Id], 4, "group");
    var before = demo.Now;
    var created = bookingService.Create(user.Id, look.Id, [extra.Id], start, "78I Isolated Demo Street", "Quezon City",
        quote.RequiredDownPayment, "GCash", "LOCAL-78I-ACK", null, true, 4, "group", true, latitude: 14.5547m, longitude: 121.0244m);
    var after = demo.Now;
    Check(created.TransportationPolicyAcknowledgedAt.HasValue, "Demo stores accepted transportation-policy timestamp");
    Check(created.TransportationPolicyAcknowledgedAt >= before && created.TransportationPolicyAcknowledgedAt <= after,
        "Demo acknowledgment uses current creation time, not appointment time");
    Check(created.TransportationPolicyAcknowledgedAt!.Value.Kind == before.Kind, "Demo acknowledgment preserves the store's timestamp convention");
    Check(created.TravelFee == 0 && created.Total == quote.FinalTotal
        && created.Total == created.OriginalTotal - created.GroupDiscount + extra.Price, "Transportation remains separate from discounted booking totals");
    Check(created.RequiredDownPayment == quote.RequiredDownPayment && created.DownPaymentPercentSnapshot == demo.Settings.DownPaymentPercent
        && created.BalanceAfterDeposit == quote.RemainingBalance, "Acknowledgment does not change deposit or balance snapshots");
    Check(created.Service == new PriceSnapshot(look.Id, look.Name, look.Price, look.Minutes)
        && created.AddOns.Single() == new PriceSnapshot(extra.Id, extra.Name, extra.Price, extra.Minutes)
        && created.People == 4 && created.GroupPrice == quote.GroupPrice && created.AppliedPricePerPerson == quote.AppliedPricePerPerson,
        "Service, add-on and group-price snapshots are unchanged");
    Check(created.End == start.AddMinutes(quote.DurationMinutes) && demo.Settings.BufferMinutes == originalBuffer,
        "Acknowledgment does not change session duration or travel buffer");
    Check(demo.Payments.Single(p => p.BookingId == created.Id).Amount == quote.RequiredDownPayment,
        "Demo submitted payment retains the exact quoted deposit");
    Check(System.Text.Json.JsonSerializer.Serialize(demo.Bookings.Where(b => b.Id != created.Id)) == history
        && System.Text.Json.JsonSerializer.Serialize(demo.Payments.Where(p => p.BookingId != created.Id)) == paymentHistory,
        "Existing Demo bookings, acknowledgment values and payment history are untouched");
    var timestamp = created.TransportationPolicyAcknowledgedAt;
    var bookingsBeforeRejection = System.Text.Json.JsonSerializer.Serialize(demo.Bookings);
    var paymentsBeforeRejection = System.Text.Json.JsonSerializer.Serialize(demo.Payments);
    foreach (var consent in new[] { (Agreed: true, Travel: false), (Agreed: false, Travel: true), (Agreed: false, Travel: false) })
        Reject(() => bookingService.Create(user.Id, look.Id, [], start.AddDays(1), "78I Isolated Demo Street", "Quezon City",
            bookingService.Quote(look.Id, []).RequiredDownPayment, "GCash", "LOCAL-78I-REJECT", null, consent.Agreed, 1, "auto", consent.Travel, latitude: 14.5547m, longitude: 121.0244m),
            $"Missing required consent is rejected (booking={consent.Agreed}, transportation={consent.Travel})");
    Check(System.Text.Json.JsonSerializer.Serialize(demo.Bookings) == bookingsBeforeRejection
        && System.Text.Json.JsonSerializer.Serialize(demo.Payments) == paymentsBeforeRejection
        && created.TransportationPolicyAcknowledgedAt == timestamp, "Rejected submissions do not change existing records or acknowledgment timestamps");
}
if (args.Contains("--transportation-only"))
{
    var demo = new DemoStore(presentationAccounts: true);
    var serviceCheck = new BookingService(demo, demo, demo, demo, demo);
    var person = demo.Users.First(u => u.Role == "Customer");
    var look = demo.Services[0]; var extra = demo.AddOns[0];
    var quote = serviceCheck.Quote(look.Id, [extra.Id, extra.Id], 4, "group");
    Check(quote.ExtrasTotal == extra.Price * 2 && quote.DurationMinutes == look.Minutes * 4 + extra.Minutes * 2, "quantity pricing and duration use two add-ons");
    Reject(() => serviceCheck.Quote(look.Id, [extra.Id, extra.Id], 1, "auto"), "add-on quantity cannot exceed party size");
    demo.Settings.OpenHour = 0; demo.Settings.CloseHour = 23; demo.Settings.BufferMinutes = 0;
    var booking = serviceCheck.Create(person.Id, look.Id, [extra.Id, extra.Id], DateTime.Today.AddDays(10), "Synthetic address", "Quezon City", quote.RequiredDownPayment, "GCash", "SYNTHETIC-QUANTITY", null, true, 4, "group", true, latitude: 14.5547m, longitude: 121.0244m);
    Check(booking.Total == quote.FinalTotal && booking.End == booking.Start.AddMinutes(quote.DurationMinutes) && booking.AddOns.Single().Quantity == 2, "booking snapshots quantity and duration");
    Check(serviceCheck.Paid(booking) == 0 && demo.Payments.Single(p => p.BookingId == booking.Id).Amount == quote.RequiredDownPayment, "exact down payment claim remains unverified");
    Reject(() => serviceCheck.Create(person.Id, look.Id, [], DateTime.Today.AddDays(11), "Synthetic address", "Quezon City", 0, "GCash", "SYNTHETIC-LOW", null, true, 1, "auto", true, latitude: 14.5547m, longitude: 121.0244m), "below-minimum claim rejected");
    var individual = serviceCheck.Quote(look.Id, []);
    Reject(() => serviceCheck.Create(person.Id, look.Id, [], DateTime.Today.AddDays(11), "Synthetic address", "Quezon City", individual.FinalTotal + 1, "GCash", "SYNTHETIC-HIGH", null, true, 1, "auto", true, latitude: 14.5547m, longitude: 121.0244m), "above-total claim rejected");
    serviceCheck.AdminAction(booking, "verify", demo.Users.First(u => u.Role == "Admin").Id, "", 0, "");
    Check(serviceCheck.Paid(booking) == quote.RequiredDownPayment && booking.Status == "Confirmed", "explicit review verifies the exact down payment");
    Console.WriteLine($"All {checks} focused Demo transportation checks passed.");
    return;
}
var keyStoreSql = File.ReadAllText(Path.Combine("Backend", "GlamByMariya.Infrastructure", "Data", "Sql", "DataProtectionKeys.sql"));
Check(keyStoreSql.Contains("ENABLE ROW LEVEL SECURITY", StringComparison.OrdinalIgnoreCase)
    && keyStoreSql.Contains("FROM PUBLIC, anon, authenticated", StringComparison.OrdinalIgnoreCase),
    "Data Protection key table enables RLS and revokes browser-role access");
Check(keyStoreSql.Contains("data_protection_keys", StringComparison.OrdinalIgnoreCase)
    && !keyStoreSql.Contains("FORCE ROW LEVEL SECURITY", StringComparison.OrdinalIgnoreCase),
    "Data Protection table is dedicated to the server-owned database role");
Check(File.Exists(Path.Combine("Backend", "GlamByMariya.Infrastructure", "Migrations", "Public", "20261017130000_DataProtectionKeys.cs")),
    "Data Protection persistence migration is present and additive");
var connectionConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConnectionStrings:MakeupDatabase"] = "postgresql://postgres.independent-placeholder:example%40password@aws-0-region.pooler.supabase.com:5432/postgres",
    ["Database:ProjectId"] = "independent-placeholder",
}).Build();
var parsedConnection = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.ConnectionString(connectionConfig));
Check(parsedConnection.Host == "aws-0-region.pooler.supabase.com" && parsedConnection.Port == 5432
    && parsedConnection.Database == "postgres" && parsedConnection.SslMode == SslMode.VerifyFull,
    "Supabase Session Pooler URI parses host/port/database and requires VerifyFull");
connectionConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConnectionStrings:MakeupDatabase"] = "tcp://postgres.independent-placeholder:example%40password@aws-0-region.pooler.supabase.com:5432/postgres",
    ["Database:ProjectId"] = "independent-placeholder",
}).Build();
parsedConnection = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.ConnectionString(connectionConfig));
Check(parsedConnection.Host == "aws-0-region.pooler.supabase.com" && !parsedConnection.Host.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)
    && parsedConnection.SslMode == SslMode.VerifyFull,
    "tcp://-prefixed pooler URI normalizes to the hostname without weakening TLS");
connectionConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConnectionStrings:MakeupDatabase"] = "Host=tcp://aws-0-region.pooler.supabase.com:5432;Database=postgres;Username=postgres.independent-placeholder;Password=example",
    ["Database:ProjectId"] = "independent-placeholder",
}).Build();
parsedConnection = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.ConnectionString(connectionConfig));
Check(parsedConnection.Host == "aws-0-region.pooler.supabase.com" && parsedConnection.Port == 5432
    && parsedConnection.SslMode == SslMode.VerifyFull,
    "tcp:// prefix in a key/value Host normalizes without changing port or TLS verification");
Booking Create(DateTime time, decimal? deposit = null, string area = "Quezon City") => workflow.Create(customer.Id, service.Id, [], time, "24 Sample Street", area, deposit ?? workflow.Quote(service.Id, []).RequiredDownPayment, "GCash", "TEST-" + Guid.NewGuid(), null, true, 1, "auto", true, latitude: 14.5547m, longitude: 121.0244m);
var time = DateTime.Today.AddDays(5).AddHours(10);
Reject(() => Create(time, 299), "Down payment below 50% rejected");
Reject(() => workflow.Create(customer.Id, service.Id, [], time, "24 Sample Street", "Quezon City", workflow.Quote(service.Id, []).RequiredDownPayment, "GCash", "NO-TRANSPORT-POLICY", null, true, 1, "auto", false, latitude: 14.5547m, longitude: 121.0244m), "Transportation policy acknowledgment is required");
Reject(() => Create(time, 99999), "Overpayments rejected: exact half required");
Reject(() => Create(time, area: "Outside area"), "Unsupported service area rejected");
Reject(() => Create(time.Date.AddHours(17)), "Session plus travel buffer must fit within hours");
var b = Create(time);
Check(b.Status == "Pending Payment" && b.PaymentStatus == "Pending Verification", "Booking awaits down payment verification");
Reject(() => workflow.AdminAction(b, "approve", admin.Id, "", 0, ""), "Cannot approve an unverified deposit");
workflow.AdminAction(b, "verify", admin.Id, "", 0, "");
workflow.AdminAction(b, "approve", admin.Id, "", 0, "");
Check(b.Status == "Confirmed" && workflow.Paid(b) == b.RequiredDownPayment && b.RemainingBalance == b.BalanceAfterDeposit, "Verification confirms the booking and records the outstanding half");
var verifiedAt = store.Payments.Single(p => p.BookingId == b.Id).VerifiedAt;
var paymentCount = store.Payments.Count;
workflow.AdminAction(b, "verify", admin.Id, "", 0, "");
Check(paymentCount == store.Payments.Count && verifiedAt == store.Payments.Single(p => p.BookingId == b.Id).VerifiedAt, "Repeated verification is idempotent");
Reject(() => Create(time.AddMinutes(75)), "Travel buffer prevents overlapping appointment");
var oldPrice = b.Total;
service.Price += 1000;
Check(b.Total == oldPrice, "Booked prices survive catalog edits");
var c = Create(time.AddDays(1));
Reject(() => Create(time.AddDays(1)), "A pending request reserves its interval before payment verification");
var d = Create(time.AddDays(1).AddHours(3));
workflow.AdminAction(c, "verify", admin.Id, "", 0, "");
workflow.AdminAction(c, "approve", admin.Id, "", 0, "");
Check(d.Status == "Pending Payment" && workflow.Paid(d) == 0, "An independent pending request remains unverified");
Reject(() => workflow.OfferReschedule(b, customer.Id, c.Start), "Conflicting reschedule is rejected during quotation");
Check(b.Start == time, "Original appointment retained on failed reschedule");
Reject(() => workflow.OfferReschedule(d, customer.Id, time.AddDays(2)), "Pending booking cannot be rescheduled");
Reject(() => workflow.OfferReschedule(b, admin.Id, time.AddDays(2)), "Only the customer can request a reschedule quote");
var workflowOffer = workflow.OfferReschedule(b, customer.Id, time.AddDays(2));
Reject(() => workflow.AdminAction(b, "reschedule", workflowOwner.Id, "", 0, ""), "SuperAdmin cannot approve without customer consent");
Reject(() => workflow.ConsentReschedule(b, customer.Id, workflowOffer.Id, false), "Explicit consent is required");
Reject(() => workflow.ConsentReschedule(b, admin.Id, workflowOffer.Id, true), "Another role cannot consent for the customer");
workflow.ConsentReschedule(b, customer.Id, workflowOffer.Id, true);
Check(b.Start == time && b.RequestedStart == workflowOffer.ProposedStart && b.PendingReschedule != null, "Customer consent retains the original appointment pending approval");
Reject(() => workflow.AdminAction(b, "reschedule", admin.Id, "", 0, ""), "Admin cannot approve a reschedule");
var competingBlock = new AvailabilityBlock(Guid.NewGuid(), workflowOffer.ProposedStart, workflowOffer.ProposedStart.AddHours(3), "Synthetic conflict after consent");
store.Blocks.Add(competingBlock);
Reject(() => workflow.AdminAction(b, "reschedule", workflowOwner.Id, "", 0, ""), "SuperAdmin approval rechecks conflicts after consent");
Check(b.Start == time && b.PendingReschedule != null, "Failed approval preserves original appointment and customer consent");
store.Blocks.Remove(competingBlock);
workflow.AdminAction(b, "reschedule", workflowOwner.Id, "", 0, "");
Check(b.Start == time.AddDays(2) && b.RequestedStart == null && b.Total == oldPrice && workflow.Paid(b) == b.RequiredDownPayment, "Reschedule retains prices and payment");
Check(b.PendingReschedule == null && b.ApprovedReschedules.Count == 1 && b.ApprovedReschedules[0].Quote.ProposedStart == b.Start, "Approved booking details retain the agreed reschedule");
var declineOffer = workflow.OfferReschedule(b, customer.Id, time.AddDays(4));
workflow.ConsentReschedule(b, customer.Id, declineOffer.Id, true);
workflow.AdminAction(b, "decline", admin.Id, "Unavailable", 0, "");
Check(b.Start == time.AddDays(2) && b.RequestedStart == null && b.PendingReschedule == null, "Declined request preserves the approved appointment and clears consent");
store.Blocks.Add(new(Guid.NewGuid(), time.AddDays(3), time.AddDays(3).AddHours(3), "Unavailable"));
Reject(() => Create(time.AddDays(3)), "Unavailable time blocks honored");
workflow.AdminAction(d, "reject", admin.Id, "Address issue", 0, "");
Check(d.RefundDue == workflow.Paid(d) && d.CancellationFee == 0, "Admin rejection refunds verified payments only and does not charge a cancellation fee");
Reject(() => workflow.AdminAction(c, "complete", workflowOwner.Id, "", 0, "", true), "Future appointment cannot be completed");
c.Start = DateTime.Today.AddDays(-1).AddHours(10);
Reject(() => workflow.AdminAction(c, "complete", admin.Id, "", 0, "", true), "Admin cannot complete an appointment");
Reject(() => workflow.AdminAction(c, "complete", workflowOwner.Id, "", 0, ""), "Completion requires confirmation");
var paidBeforeCompletion = workflow.Paid(c);
workflow.AdminAction(c, "complete", workflowOwner.Id, "", 0, "", true);
Check(c.Status == "Completed" && workflow.Paid(c) == paidBeforeCompletion, "Completion does not invent a final payment");
Reject(() => workflow.Cancel(b, customer.Id, false), "Cancellation requires explicit confirmation");
Reject(() => workflow.Cancel(b, admin.Id, true), "Customer can only cancel their own booking");
workflow.Cancel(b, customer.Id, true);
Check(b.Status == "Cancelled" && b.CancellationFee == 200 && b.RefundDue == b.RequiredDownPayment - 200, "Cancellation deducts exactly â‚±200 from verified down payment");
workflow.AdminAction(b, "refund", admin.Id, "", 100, "TRANSFER-1");
Check(b.Refunded == 100 && b.PaymentStatus == "Refunded", "Manual refund recorded");
Reject(() => workflow.AdminAction(b, "refund", admin.Id, "", b.RefundDue, "TRANSFER-2"), "Over-refund prevented");
workflow.CheckSlot(b.Start, (int)(b.End - b.Start).TotalMinutes);
Check(true, "Cancelled appointment releases reserved slot");
Reject(() => workflow.Cancel(b, customer.Id, true), "Repeated cancellation rejected");
Check(store.Notifications.Any(n => n.UserId == customer.Id) && store.AuditLogs.Any(), "Notifications and audit events created");
var availabilityStore = new DemoStore(presentationAccounts: true);
availabilityStore.Bookings.Clear();
availabilityStore.Payments.Clear();
var availability = new BookingService(availabilityStore, availabilityStore, availabilityStore, availabilityStore, availabilityStore);
var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1);
var calendar = availability.GetAvailability(first, 60);
Check(calendar.Days.Count == DateTime.DaysInMonth(first.Year, first.Month), "Calendar contains every day of the requested month");
var calendarJson = System.Text.Json.JsonSerializer.Serialize(calendar, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
using (var calendarDocument = System.Text.Json.JsonDocument.Parse(calendarJson))
    Check(calendarDocument.RootElement.TryGetProperty("days", out var jsonDays) && jsonDays.GetArrayLength() == calendar.Days.Count
        && calendarDocument.RootElement.GetProperty("month").GetString() == calendar.Month,
        "Availability endpoint JSON uses the camel-case month/days shape expected by JavaScript");
Check(calendar.Days.All(day => day.Status == "available" && day.AvailableCount == 34), "Open dates have all 15-minute starts that fit session and buffer");
Check(calendar.Days[0].Slots.Last().Start.EndsWith("T16:15"), "Closing boundary includes exactly fitting slot");
var currentCalendar = availability.GetAvailability(DateTime.Today, 60);
Check(currentCalendar.Days.Where(day => DateTime.Parse(day.Date) < DateTime.Today).All(day => day.Status == "past" && day.AvailableCount == 0 && day.Slots.Count == 0), "Past dates have no selectable slots");
Check(currentCalendar.Days.SelectMany(d => d.Slots).Where(s => DateTime.Parse(s.Start) <= DateTime.Now).All(s => !s.Available), "Elapsed times today are disabled");
availabilityStore.Blocks.Add(new(Guid.NewGuid(), first.AddDays(1), first.AddDays(2), "Whole day blocked"));
calendar = availability.GetAvailability(first, 60);
Check(calendar.Days[1].Status == "unavailable" && calendar.Days[1].AvailableCount == 0, "Whole-day artist blocks disable dates");
var full = new Booking { CustomerId = availabilityStore.Users.First(u => u.Role == "Customer").Id, Start = first.AddDays(2).AddHours(8), Service = new(Guid.NewGuid(), "All-day session", 3000, 555), Status = "Confirmed" };
availabilityStore.Bookings.Add(full);
calendar = availability.GetAvailability(first, 60);
Check(calendar.Days[2].Status == "full" && calendar.Days[2].Slots.All(s => !s.Available), "Fully booked dates and times disabled");
var partial = new Booking { Start = first.AddDays(3).AddHours(10), Service = new(Guid.NewGuid(), "Short session", 1500, 60), Status = "Confirmed" };
availabilityStore.Bookings.Add(partial);
calendar = availability.GetAvailability(first, 60);
Check(calendar.Days[3].Status == "limited" && calendar.Days[3].AvailableCount is > 0 and < 34, "Partially booked dates are marked limited");
Check(!calendar.Days[3].Slots.First(s => s.Start.EndsWith("T11:30")).Available && calendar.Days[3].Slots.First(s => s.Start.EndsWith("T11:45")).Available, "Existing travel buffer and exact nonoverlap boundary respected");
foreach (var day in calendar.Days)
foreach (var slot in day.Slots.Where(s => s.Available)) availability.CheckSlot(DateTime.Parse(slot.Start), 60);
Check(true, "Calendar availability uses the same rules as booking submission");
availability.Cancel(full, full.CustomerId, true);
Check(availability.GetAvailability(first, 60).Days[2].Status == "available", "Cancellation immediately restores calendar availability");
Check(availability.GetAvailability(first, 60, partial.Id).Days[3].Status == "available", "Reschedule calendar excludes only its own reservation");
availabilityStore.Services[0].Minutes = 90;
Check(availability.SelectedDuration(availabilityStore.Services[0].Id, [availabilityStore.AddOns[1].Id, availabilityStore.AddOns[1].Id], 2, "group") == 270, "Calendar derives service and quantity-weighted add-on duration");
Check(availability.GetAvailability(first, 135).Days[0].AvailableCount < 34, "Longer services and add-ons reduce available starts");
availabilityStore.AddOns[1].Active = false;
Reject(() => availability.SelectedDuration(availabilityStore.Services[0].Id, [availabilityStore.AddOns[1].Id]), "Inactive add-ons cannot be used for availability");
Reject(() => availability.GetAvailability(first.AddMonths(12), 60), "Availability requests beyond supported month range rejected");
Reject(() => availability.GetAvailability(first.AddMonths(-2), 60), "Past month requests rejected");
Reject(() => availability.GetAvailability(first, 0), "Invalid duration rejected");
Reject(() => availability.CheckSlot(DateTime.MaxValue, 60), "Out-of-range dates fail validation without overflow");
var sampleService = new CatalogItem { Name = "Soft Glam", Price = 600, GroupPrice = 525, GroupMinimumPeople = 4, Minutes = 60 };
foreach (var sample in new[] { (1, 600m, 300m), (2, 1200m, 600m), (3, 1800m, 900m), (4, 2100m, 1050m), (5, 2625m, 1312.50m), (10, 5250m, 2625m) })
{
    var quote = BookingPricing.Calculate(sampleService, [], sample.Item1, 0, sample.Item1 >= sampleService.GroupMinimumPeople ? "group" : "auto");
    Check(quote.FinalTotal == sample.Item2 && quote.RequiredDownPayment == sample.Item3 && quote.RemainingBalance == sample.Item3, $"Soft Glam {sample.Item1} people: discounted total and 50/50 split match sample");
}
var ten = BookingPricing.Calculate(sampleService, [], 10, 0, "group");
Check(ten.OriginalTotal == 6000 && ten.GroupDiscount == 750 && ten.FinalTotal == 5250 && ten.RequiredDownPayment == 2625, "Ten-person summary discounts before halving");
var withExtras = BookingPricing.Calculate(sampleService, [new() { Price = 100, Minutes = 15 }], 5, 250, "group");
Check(withExtras.FinalTotal == 2725 && withExtras.RequiredDownPayment == 1362.50m && withExtras.DurationMinutes == 315, "Extras are included once before the 50% calculation and duration accounts for people; travel is excluded");
Reject(() => BookingPricing.Calculate(sampleService, [], 0, 0), "Zero people rejected");
Reject(() => BookingPricing.Calculate(sampleService, [], 21, 0), "Excessive party size rejected");
Check(BookingPricing.Calculate(sampleService, [], 3, 0, "group").FinalTotal == 1800, "Package below threshold uses regular per-person pricing");
Reject(() => BookingPricing.Calculate(sampleService, [], 4, 0, "tampered"), "Unknown package rejected");
var oddCent = BookingPricing.Calculate(new() { Name = "Odd-cent service", Price = 600.01m, Minutes = 30 }, [], 1, 0);
Check(oddCent.RequiredDownPayment == 300.01m && oddCent.RemainingBalance == 300m && oddCent.RequiredDownPayment + oddCent.RemainingBalance == oddCent.FinalTotal, "Odd-cent totals round the down payment to a cent without losing money");
var forty = BookingPricing.Calculate(sampleService, [], 4, 0, "group", 40);
Check(forty.FinalTotal == 2100 && forty.RequiredDownPayment == 840 && forty.RemainingBalance == 1260 && forty.DownPaymentPercent == 40, "â‚±2,100 at 40% requires â‚±840 down and â‚±1,260 balance");
var onePercent = BookingPricing.Calculate(sampleService, [], 1, 0, "auto", 1);
Check(onePercent.RequiredDownPayment == 6 && onePercent.RemainingBalance == 594, "1% down payment of â‚±600 requires â‚±6 down");
var fullPercent = BookingPricing.Calculate(sampleService, [], 1, 0, "auto", 100);
Check(fullPercent.RequiredDownPayment == 600 && fullPercent.RemainingBalance == 0, "100% down payment collects the entire total");
Reject(() => BookingPricing.Calculate(sampleService, [], 1, 0, "auto", 0), "0% down payment rejected");
Reject(() => BookingPricing.Calculate(sampleService, [], 1, 0, "auto", 101), "101% down payment rejected");
Reject(() => BookingPricing.Calculate(sampleService, [], 1, 0, "auto", 40.5m), "Fractional down payment percentage rejected");
Check(BookingPricing.Calculate(new CatalogItem { Name = "Glamour Makeup", Price = 750, Minutes = 90 }, [], 1, 0, "auto", 40).RequiredDownPayment == 300, "Glamour Makeup â‚±750 at 40% requires â‚±300 down");
Check(BookingPricing.Calculate(new CatalogItem { Name = "Bridal Makeup", Price = 1500, Minutes = 120 }, [], 1, 0, "auto", 50).RequiredDownPayment == 750, "Bridal Makeup â‚±1,500 at 50% requires â‚±750 down");
var paymentStore = new DemoStore(presentationAccounts: true);
paymentStore.Bookings.Clear(); paymentStore.Payments.Clear(); paymentStore.Settings.TravelFee = 0;
var paymentWorkflow = new BookingService(paymentStore, paymentStore, paymentStore, paymentStore, paymentStore);
var pService = paymentStore.Services[0]; pService.Minutes = 30;
var pCustomer = paymentStore.Users.First(u => u.Role == "Customer");
var pAdmin = paymentStore.Users.First(u => u.Role == "Admin");
var pOwner = paymentStore.Users.First(u => u.Role == "SuperAdmin");
var pStart = DateTime.Today.AddDays(20).AddHours(8);
Booking GroupBooking(string reference, decimal deposit = 2625) => paymentWorkflow.Create(pCustomer.Id, pService.Id, [], pStart, "24 Sample Street", "Quezon City", deposit, "Maya", reference, null, true, 10, "group", true, latitude: 14.5547m, longitude: 121.0244m);
Reject(() => GroupBooking("UNDERPAYMENT", 2624.99m), "One-cent down payment shortfall rejected");
Reject(() => GroupBooking("OVERPAYMENT", 5250.01m), "Payment above the booking total is rejected");
var groupBooking = GroupBooking("GROUP-PAYMENT");
Check(groupBooking.Total == 5250 && groupBooking.RequiredDownPayment == 2625 && groupBooking.GroupDiscount == 750, "Discounted prices and down payment saved as booking snapshots");
Reject(() => paymentWorkflow.Create(pCustomer.Id, pService.Id, [], pStart.AddDays(1), "24 Sample Street", "Quezon City", 2625, "Maya", " group-payment ", null, true, 10, "group", true, latitude: 14.5547m, longitude: 121.0244m), "Duplicate payment references are blocked regardless of whitespace/case");
pService.Price = 900; pService.GroupPrice = 700; paymentStore.Settings.TravelFee = 500;
Check(groupBooking.Total == 5250 && groupBooking.RequiredDownPayment == 2625, "Editing catalog rates and travel fees does not change booked group prices");
paymentWorkflow.AdminAction(groupBooking, "verify", pAdmin.Id, "", 0, "");
var verifiedGroupPayment = paymentStore.Payments.Single();
Check(groupBooking.Status == "Confirmed" && groupBooking.VerifiedPaid == 2625 && groupBooking.RemainingBalance == 2625 && groupBooking.PaymentStatus == "Down Payment Verified", "Successful 50% verification atomically confirms group booking");
Parallel.For(0, 10, _ => paymentWorkflow.AdminAction(groupBooking, "verify", pAdmin.Id, "", 0, ""));
Check(paymentStore.Payments.Count == 1 && paymentWorkflow.Paid(groupBooking) == 2625, "Concurrent verification retries do not process payments twice");
var groupOffer = paymentWorkflow.OfferReschedule(groupBooking, pCustomer.Id, pStart.AddDays(1));
paymentWorkflow.ConsentReschedule(groupBooking, pCustomer.Id, groupOffer.Id, true);
paymentWorkflow.AdminAction(groupBooking, "reschedule", pOwner.Id, "", 0, "");
Check(groupBooking.Total == 5250 && groupBooking.RequiredDownPayment == 2625 && groupBooking.RemainingBalance == 2625, "Group reschedule retains discounted price and down payment");
paymentWorkflow.Cancel(groupBooking, pCustomer.Id, true);
Check(groupBooking.RefundDue == 2425 && groupBooking.CancellationFee == 200, "Group cancellation refunds verified half minus the existing â‚±200 fee");
var tampered = GroupBooking("TAMPERED", paymentWorkflow.Quote(pService.Id, [], 10, "group").RequiredDownPayment);
paymentStore.Payments.Single(p => p.BookingId == tampered.Id).Amount -= 1;
Reject(() => paymentWorkflow.AdminAction(tampered, "verify", pAdmin.Id, "", 0, ""), "Verification rechecks stored down payment amount against final price");
Check(tampered.Status == "Pending Payment" && tampered.VerifiedPaid == 0, "Failed verification preserves pending payment status");
// Owner-configurable down payment: change the policy to 40%; existing snapshots stay untouched.
paymentStore.Settings.DownPaymentPercent = 40;
var quote40 = paymentWorkflow.Quote(pService.Id, [], 10, "group");
Check(quote40.RequiredDownPayment == 2800 && quote40.RemainingBalance == 4200, "40% policy recalculates new quotations (â‚±2,800 down on â‚±7,000)");
var booking40 = paymentWorkflow.Create(pCustomer.Id, pService.Id, [], pStart.AddDays(5), "24 Sample Street", "Quezon City", quote40.RequiredDownPayment, "Maya", "POLICY-40", null, true, 10, "group", true, latitude: 14.5547m, longitude: 121.0244m);
Check(booking40.RequiredDownPayment == 2800 && booking40.DownPaymentPercentSnapshot == 40 && booking40.RemainingBalance == 7000, "New booking snapshots the 40% policy");
Check(groupBooking.RequiredDownPayment == 2625 && groupBooking.DownPaymentPercentSnapshot == 50, "Changing the policy does not alter existing booking snapshots");
paymentStore.Settings.DownPaymentPercent = 50;
Check(paymentWorkflow.Quote(pService.Id, [], 10, "group").RequiredDownPayment == 3500, "Restoring 50% applies to new quotations again");
// Exercise the configured Demo catalog without inventing or modifying any prices.
var matrixStore = new DemoStore(presentationAccounts: true);
var matrixWorkflow = new BookingService(matrixStore, matrixStore, matrixStore, matrixStore, matrixStore);
var matrixAddOns = matrixStore.AddOns.Where(x => x.Active && !x.Archived).ToArray();
foreach (var item in matrixStore.Services.Where(x => x.Active && !x.Archived))
{
    foreach (var count in new[] { 1, 2, 4, 5, 10, 20 })
    {
        var addOnIds = matrixAddOns.SelectMany(x => Enumerable.Repeat(x.Id, count)).ToArray();
        var extras = matrixAddOns.Sum(x => x.Price * count);
        if (count == 1)
        {
            var individualQuote = matrixWorkflow.Quote(item.Id, addOnIds, count, "auto");
            var individualExpected = item.Price + extras;
            Check(individualQuote.FinalTotal == individualExpected && individualQuote.RequiredDownPayment == Money.Percentage(individualExpected, matrixStore.Settings.DownPaymentPercent),
                $"Configured {item.Name}: individual total, maximum add-ons and down payment");
        }
        else
            Reject(() => matrixWorkflow.Quote(item.Id, addOnIds, count, "auto"), $"Individual booking rejects {count} people for {item.Name}");
        if (item.GroupPrice.HasValue)
        {
            var groupQuote = matrixWorkflow.Quote(item.Id, addOnIds, count, "group");
            var unit = count >= item.GroupMinimumPeople ? item.GroupPrice.Value : item.Price;
            var groupExpected = unit * count + extras;
            Check(groupQuote.FinalTotal == groupExpected && groupQuote.RequiredDownPayment == Money.Percentage(groupExpected, matrixStore.Settings.DownPaymentPercent),
                $"Configured {item.Name}, {count} people: package rate and down payment");
        }
        else
            Reject(() => matrixWorkflow.Quote(item.Id, addOnIds, count, "group"), $"Unconfigured {item.Name} package rate is rejected for {count} people");
        if (matrixAddOns.Length > 0)
            Reject(() => matrixWorkflow.Quote(item.Id, Enumerable.Repeat(matrixAddOns[0].Id, count + 1).ToArray(), count, count == 1 ? "auto" : "group"),
                $"Configured {item.Name}, {count} people: add-on quantity cannot exceed party size");
    }
}
var catalogRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "catalog-image-tests-" + Guid.NewGuid());
try
{
    var catalog = new CatalogImageService(catalogRoot);
    var catalogServiceId = Guid.NewGuid();
    var sharedId = Guid.NewGuid();
    var pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6cWQAAAAASUVORK5CYII=");
    Check(new CatalogItem().VisualWord == "", "Catalog items default to an empty overlay word");
    Check(catalog.GetImageUrl("service", catalogServiceId) == null && catalog.Read("service", catalogServiceId) == null, "Catalog picture starts empty without uploads");
    catalog.Save("service", catalogServiceId, pixel);
    var saved = catalog.Read("service", catalogServiceId)!.Value;
    Check(saved.ContentType == "image/png" && saved.Bytes.SequenceEqual(pixel), "Uploaded service picture can be read with correct content type");
    Check(catalog.GetImageUrl("service", catalogServiceId)!.StartsWith($"/CatalogImages/Image?kind=service&id={catalogServiceId}&v=") && catalog.Read("addon", catalogServiceId) == null, "Catalog image URL maps only to its kind and item");
    catalog.Save("service", sharedId, pixel);
    catalog.Save("addon", sharedId, pixel);
    Check(catalog.Read("service", sharedId) != null && catalog.Read("addon", sharedId) != null, "Service and add-on pictures coexist for the same item id");
    Check(new CatalogImageService(catalogRoot).Read("service", catalogServiceId)!.Value.Bytes.SequenceEqual(pixel), "Catalog image persists across service restarts");
    Reject(() => catalog.Save("Cash", catalogServiceId, pixel), "Unsupported catalog kind rejected");
    Reject(() => catalog.Save("../service", catalogServiceId, pixel), "Catalog upload path traversal rejected");
    Reject(() => catalog.Save("", catalogServiceId, pixel), "Empty catalog kind rejected");
    Reject(() => catalog.Save("service", Guid.Empty, pixel), "Empty catalog id rejected");
    Reject(() => catalog.Read("../service", catalogServiceId), "Catalog read path traversal rejected");
    Reject(() => catalog.Read("service", Guid.Empty), "Catalog read with empty id rejected");
    Reject(() => catalog.Save("service", catalogServiceId, []), "Empty catalog upload rejected");
    Reject(() => catalog.Save("service", catalogServiceId, new byte[5_000_001]), "Catalog upload above 5 MB rejected");
    Reject(() => catalog.Save("service", catalogServiceId, System.Text.Encoding.UTF8.GetBytes("<svg>not a catalog image</svg>")), "Non-JPG/PNG catalog files rejected");
    Check(catalog.Read("service", catalogServiceId)!.Value.Bytes.SequenceEqual(pixel), "Rejected catalog upload keeps the previous image intact");
    catalog.Save("service", catalogServiceId, pixel);
    Check(Directory.GetFiles(Path.Combine(catalogRoot, "App_Data", "CatalogImages"), $"service-{catalogServiceId:N}.*").Length == 1, "Replacing a catalog picture leaves only the current image");
    catalog.Remove("service", sharedId);
    Check(catalog.Read("service", sharedId) == null && catalog.Read("addon", sharedId) != null, "Removing one catalog picture preserves the other kind");
    catalog.Remove("addon", sharedId);
    Check(catalog.GetImageUrl("addon", sharedId) == null, "Removed catalog picture clears its image URL");
    Check(catalog.GetImageUrl("service", catalogServiceId, "preview") == null && catalog.Read("service", catalogServiceId, "preview") == null, "Catalog preview starts empty without uploads");
    catalog.Save("service", catalogServiceId, pixel, "preview");
    var previewSaved = catalog.Read("service", catalogServiceId, "preview")!.Value;
    Check(previewSaved.ContentType == "image/png" && previewSaved.Bytes.SequenceEqual(pixel), "Uploaded preview picture can be read with correct content type");
    var cardUrl = catalog.GetImageUrl("service", catalogServiceId)!;
    var previewUrl = catalog.GetImageUrl("service", catalogServiceId, "preview")!;
    Check(previewUrl.Contains("&slot=preview") && !cardUrl.Contains("slot="), "Preview URL carries slot while card URL does not");
    var previewJpg = new byte[] { 255, 216, 255, 0 };
    catalog.Save("service", catalogServiceId, previewJpg, "preview");
    Check(catalog.Read("service", catalogServiceId)!.Value.Bytes.SequenceEqual(pixel) && catalog.Read("service", catalogServiceId, "preview")!.Value.Bytes.SequenceEqual(previewJpg), "Card and preview pictures coexist with independent bytes");
    Check(catalog.Read("service", catalogServiceId, "preview")!.Value.ContentType == "image/jpeg", "Preview JPG bytes round-trip with correct content type");
    catalog.Remove("service", catalogServiceId, "preview");
    Check(catalog.Read("service", catalogServiceId, "preview") == null && catalog.Read("service", catalogServiceId) != null, "Removing preview keeps card picture");
    catalog.Save("service", catalogServiceId, previewJpg, "preview");
    catalog.Remove("service", catalogServiceId);
    Check(catalog.Read("service", catalogServiceId) == null && catalog.Read("service", catalogServiceId, "preview") != null, "Removing card keeps preview picture");
    catalog.Save("service", catalogServiceId, pixel);
    Reject(() => catalog.Save("service", catalogServiceId, pixel, "cover"), "Unsupported preview slot rejected on save");
    Reject(() => catalog.Save("service", catalogServiceId, pixel, ""), "Empty preview slot rejected on save");
    Reject(() => catalog.Save("service", catalogServiceId, pixel, "../preview"), "Preview slot path traversal rejected on save");
    Reject(() => catalog.Read("service", catalogServiceId, "cover"), "Unsupported preview slot rejected on read");
    Reject(() => catalog.Read("service", catalogServiceId, ""), "Empty preview slot rejected on read");
    Reject(() => catalog.Read("service", catalogServiceId, "../preview"), "Preview slot path traversal rejected on read");
    Reject(() => catalog.GetImageUrl("service", catalogServiceId, "cover"), "Unsupported preview slot rejected on URL");
    Reject(() => catalog.GetImageUrl("service", catalogServiceId, ""), "Empty preview slot rejected on URL");
    Reject(() => catalog.GetImageUrl("service", catalogServiceId, "../preview"), "Preview slot path traversal rejected on URL");
    Reject(() => catalog.Save("service", catalogServiceId, new byte[5_000_001], "preview"), "Preview upload above 5 MB rejected");
    Reject(() => catalog.Save("service", catalogServiceId, System.Text.Encoding.UTF8.GetBytes("<svg>not a catalog image</svg>"), "preview"), "Non-JPG/PNG preview files rejected");
}
finally { if (Directory.Exists(catalogRoot)) Directory.Delete(catalogRoot, recursive: true); }
string? registrationError = RegistrationRules.Validate("Maria", "Santos", "maria@example.com", 25, "Female", "123 Sampaguita Street, Barangay Malinis", "Glow1234");
Check(registrationError == null, "Valid registration passes all rules");
Check(RegistrationRules.Validate("", "Santos", "maria@example.com", 25, "", "123 Sampaguita Street", "Glow1234") != null, "Blank first name rejected");
Check(RegistrationRules.Validate("Maria", "S4ntos", "maria@example.com", 25, "", "123 Sampaguita Street", "Glow1234") != null, "Last name with numbers rejected");
Check(RegistrationRules.Validate("Maria", "Santos", "not-an-email", 25, "", "123 Sampaguita Street", "Glow1234") != null, "Invalid email rejected");
Check(RegistrationRules.Validate("Maria", "Santos", "maria@example.com", 12, "", "123 Sampaguita Street", "Glow1234") != null, "Age below 13 rejected");
Check(RegistrationRules.Validate("Maria", "Santos", "maria@example.com", 121, "", "123 Sampaguita Street", "Glow1234") != null, "Age above 120 rejected");
Check(RegistrationRules.Validate("Maria", "Santos", "maria@example.com", 25, "Unknown", "123 Sampaguita Street", "Glow1234") != null, "Gender outside the list rejected");
Check(RegistrationRules.Validate("Maria", "Santos", "maria@example.com", 25, "", "Short", "Glow1234") != null, "Address below 8 characters rejected");
Check(RegistrationRules.Validate("Maria", "Santos", "maria@example.com", 25, "", "123 Sampaguita Street", "short") != null, "Password below 8 characters rejected");
Check(RegistrationRules.Validate("Maria", "Santos", "maria@example.com", 25, "", "123 Sampaguita Street", "12345678") != null, "Password without letters rejected");
Check(RegistrationRules.Validate("Maria", "Santos", "maria@example.com", 25, "", "123 Sampaguita Street", "Password") != null, "Password without numbers rejected");
Check(RegistrationRules.Genders.Length == 3, "Gender options list is defined");
Console.WriteLine($"All {checks} workflow, availability, catalog image, preview-slot and registration checks passed.");

public class PersistentQuoteProxy : System.Reflection.DispatchProxy
{
    public object?[]? Arguments { get; private set; }
    public object?[]? RescheduleArguments { get; set; }
    public List<AppUser> TestUsers { get; set; } = [];
    public List<Booking> TestBookings { get; set; } = [];
    public List<Payment> TestPayments { get; set; } = [];
    public List<CatalogItem> TestServices { get; set; } = new DemoStore(presentationAccounts: true).Services;
    public List<CatalogItem> TestAddOns { get; set; } = [];
    public BusinessSettings TestSettings { get; set; } = new();
    private readonly object gate = new();
    protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
    {
        if (method?.Name == "get_IsPersistent") return true;
        if (method?.Name == "get_Gate") return gate;
        if (method?.Name == "get_Now") return DateTime.Now;
        if (method?.Name == "get_Services") return TestServices;
        if (method?.Name == "get_AddOns") return TestAddOns;
        if (method?.Name == "get_Settings") return TestSettings;
        if (method?.Name == "get_Blocks") return new List<AvailabilityBlock>();
        if (method?.Name == nameof(IPersistentStudioStore.RequestReschedule)) { RescheduleArguments = args; return null; }
        if (method?.Name == "get_Users") return TestUsers;
        if (method?.Name == "get_Bookings") return TestBookings;
        if (method?.Name == nameof(IStudioStore.FindBooking)) return TestBookings.FirstOrDefault(b => b.Id == (Guid)args![0]!);
        if (method?.Name == "get_Payments") return TestPayments;
        if (method?.Name == nameof(IStudioStore.GetArtistLocation)) return new ArtistLocation(null, null, null, null, null);
        if (method?.Name == nameof(IStudioStore.FinishedStops)) return new List<GlamByMariya.Core.BusinessRules.TravelAppointment>();
        if (method?.Name == nameof(IStudioStore.TravelStops))
            return TestBookings.Where(b => b.Id != (Guid?)args![2] && b.Status == "Confirmed")
                .Select(b => new GlamByMariya.Core.BusinessRules.TravelAppointment(b.Start, b.End, b.BufferMinutesSnapshot ?? TestSettings.BufferMinutes, b.Latitude, b.Longitude))
                .Where(x => x.Start < (DateTime)args![1]! && x.End.AddMinutes(x.BufferMinutes) > (DateTime)args![0]!).ToList();
        if (method?.Name != nameof(IPersistentStudioStore.SubmitBooking)) throw new InvalidOperationException("Unexpected repository call.");
        Arguments = args;
        return new Booking();
    }
}
// Fake Brevo delivery for email code checks: captures the 6-digit code instead of sending.
public sealed class CapturingBrevoDelivery : HttpMessageHandler, IHttpClientFactory
{
    public string? LastCode { get; private set; }
    public bool Fail { get; set; }
    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Fail) return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        using var json = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var match = System.Text.RegularExpressions.Regex.Match(json.RootElement.GetProperty("textContent").GetString()!, @"\b\d{6}\b");
        LastCode = match.Success ? match.Value : null;
        return new HttpResponseMessage(System.Net.HttpStatusCode.Created);
    }
}
public sealed class SyntheticWebEnv : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
{
    public string WebRootPath { get; set; } = Path.GetTempPath();
    public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    public string ApplicationName { get; set; } = "WorkflowChecks";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    public string ContentRootPath { get; set; } = Path.GetTempPath();
    public string EnvironmentName { get; set; } = "Production";
}
public sealed class RecordingAuthentication : Microsoft.AspNetCore.Authentication.IAuthenticationService
{
    public ClaimsPrincipal? LastPrincipal { get; private set; }
    public int SignOutCalls { get; private set; }
    public Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> AuthenticateAsync(Microsoft.AspNetCore.Http.HttpContext context, string? scheme) => Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
    public Task ChallengeAsync(Microsoft.AspNetCore.Http.HttpContext context, string? scheme, Microsoft.AspNetCore.Authentication.AuthenticationProperties? properties) => Task.CompletedTask;
    public Task ForbidAsync(Microsoft.AspNetCore.Http.HttpContext context, string? scheme, Microsoft.AspNetCore.Authentication.AuthenticationProperties? properties) => Task.CompletedTask;
    public Task SignInAsync(Microsoft.AspNetCore.Http.HttpContext context, string? scheme, ClaimsPrincipal principal, Microsoft.AspNetCore.Authentication.AuthenticationProperties? properties) { LastPrincipal = principal; return Task.CompletedTask; }
    public Task SignOutAsync(Microsoft.AspNetCore.Http.HttpContext context, string? scheme, Microsoft.AspNetCore.Authentication.AuthenticationProperties? properties) { SignOutCalls++; return Task.CompletedTask; }
}
public class CountingStore : System.Reflection.DispatchProxy
{
    public IStudioStore Target { get; set; } = null!;
    private readonly Dictionary<string, int> calls = new();
    public int Calls(string name) => calls.GetValueOrDefault(name);
    public void Reset() => calls.Clear();
    protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
    {
        calls[method!.Name] = Calls(method.Name) + 1;
        try { return method.Invoke(Target, args); }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
}
public sealed class CapturingXenditHttp : HttpMessageHandler, IHttpClientFactory
{
    public string? LastUri, LastAuth, LastBody;
    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUri = request.RequestUri!.ToString(); LastAuth = request.Headers.Authorization?.ToString(); LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"id":"inv_fake_1","invoice_url":"https://checkout-staging.xendit.co/web/inv_fake_1","status":"PENDING"}""") };
    }
}
public class XenditStoreProxy : System.Reflection.DispatchProxy
{
    public string Last = ""; public int Count;
    protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
    {
        Count++; Last = method!.Name + " " + string.Join(" ", args!.Select(a => Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture)));
        return method.Name switch { "XenditPaid" => "confirmed", "XenditExpired" => "noted", _ => throw new InvalidOperationException("Unexpected call " + method.Name) };
    }
}
public sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
public sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
{
    public IDictionary<string, object> LoadTempData(Microsoft.AspNetCore.Http.HttpContext context) => new Dictionary<string, object>();
    public void SaveTempData(Microsoft.AspNetCore.Http.HttpContext context, IDictionary<string, object> values) { }
}

sealed class RecordingHttpFactory(List<string> seen) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new Recorder(seen));
    sealed class Recorder(List<string> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            seen.Add($"{request.RequestUri} apikey={string.Join(",", request.Headers.TryGetValues("apikey", out var k) ? k : [])} auth={request.Headers.Authorization}");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}

sealed class FakeWebsiteImages : IWebsiteImageRepository
{
    public List<WebsiteImageReference> List { get; } = [];
    public long Revision { get; private set; } = 1;
    public bool FailReplace { get; set; }
    public Task<IReadOnlyList<WebsiteImageReference>> ReadDraftImages(Guid draftId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WebsiteImageReference>>(List.ToList());
    public Task<IReadOnlyList<WebsiteImageReference>> ReadPublishedImages(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WebsiteImageReference>>([]);
    public Task ReplaceDraftImages(Guid actor, Guid draftId, long expectedRevision, IReadOnlyList<WebsiteImageReference> images, CancellationToken cancellationToken)
    {
        if (FailReplace) throw new InvalidOperationException("Simulated database failure.");
        if (expectedRevision != Revision) throw new InvalidOperationException("The draft changed.");
        List.Clear(); List.AddRange(images); Revision++; return Task.CompletedTask;
    }
}

sealed class FakeAssetWriter : IWebsiteAssetWriter
{
    public List<(Guid Id, string Key)> Added { get; } = [];
    public Task<Guid> AddAsset(Guid actor, string objectKey, string contentType, int byteLength, CancellationToken ct) { var id = Guid.NewGuid(); Added.Add((id, objectKey)); return Task.FromResult(id); }
}

sealed class RecordingFileStore : GlamByMariya.Infrastructure.Integrations.IAppFileStore
{
    public List<string> Saved { get; } = [];
    public List<string> Deleted { get; } = [];
    public bool FailSave { get; set; }
    public bool FailDelete { get; set; }
    public void Save(string key, byte[] bytes) { if (FailSave) throw new InvalidOperationException("Cloud file storage is temporarily unavailable. Please try again."); Saved.Add(key); }
    public (byte[] Bytes, string ContentType)? Read(string? key) => null;
    public void Delete(string? key) { if (FailDelete) throw new IOException("delete failed"); if (key != null) Deleted.Add(key); }
}

sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt) => Entries.Add((level, fmt(state, ex)));
}
