using System.Security.Claims;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using System.Text.Json;

namespace GlamByMariya.Web.Controllers;
public class AccountController(
    IStudioStore store, SupabaseAuthService supabase, EmailVerificationService codes, AccountSettingsService settings, AvatarStorageService avatars, IInvitationService invites, LoginAttemptLimiter loginLimiter, IConfiguration configuration) : Controller
{
    private const string AdminInvitationUnavailableMessage = "Administrator invitation acceptance is temporarily unavailable. Please contact the business owner for assistance.";
    private bool DurableEnabled => configuration.GetValue("Authentication:DurableSessionEnforcement", false);
    private PersistentCustomerAuthentication? PersistentCustomerAuth => store.IsPersistent
        ? HttpContext.RequestServices.GetService<PersistentCustomerAuthentication>() : null;

    // Internal test seam only. No production DI registration or configuration
    // selects this path; normal MVC-created controllers leave it null.
    internal AdminInvitationProvisioningWorkflow? TestOnlyAdminInvitationWorkflow { get; set; }
    internal bool TestOnlyDurableSignup { get; set; }
    internal bool TestOnlySignupRetry { get; set; } = true;

    [Authorize, HttpPost, RequestSizeLimit(2200000)]
    public async Task<IActionResult> UploadAvatar(IFormFile? avatar)
    {
        if (DurableEnabled) return StatusCode(503, "Account changes are temporarily unavailable.");
        var user = CurrentAccount();
        try
        {
            var demo = HttpContext.RequestServices.GetService<DemoAvatarStore>();
            if (demo != null) { demo.Save(user, avatar); TempData["Success"] = "Profile picture updated."; return RedirectToAction(nameof(Settings)); }
            var old = settings.Get(user.Id).AvatarUrl;
            var url = await avatars.Upload(user.Id, avatar);
            try { settings.Avatar(user.Id, url); }
            catch { await avatars.Delete(user.Id, url); throw; }
            await avatars.Delete(user.Id, old);
            TempData["Success"] = "Profile picture updated.";
        }
        catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        { TempData["Error"] = "Storage is temporarily unavailable. Please try again."; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { TempData["Error"] = "Local profile storage is unavailable."; }
        return RedirectToAction(nameof(Settings));
    }
    [Authorize, HttpPost]
    public async Task<IActionResult> RemoveAvatar()
    {
        if (DurableEnabled) return StatusCode(503, "Account changes are temporarily unavailable.");
        var user = CurrentAccount();
        var demo = HttpContext.RequestServices.GetService<DemoAvatarStore>();
        if (demo != null) { try { demo.Remove(user); TempData["Success"] = "Profile picture removed from display."; } catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { TempData["Error"] = "Local profile storage is unavailable."; } return RedirectToAction(nameof(Settings)); }
        var old = settings.Get(user.Id).AvatarUrl;
        settings.Avatar(user.Id, null);
        await avatars.Delete(user.Id, old);
        TempData["Success"] = "Profile picture removed.";
        return RedirectToAction(nameof(Settings));
    }
    private AppUser CurrentAccount() => FindLocal(User.FindFirstValue(ClaimTypes.Email) ?? "")
        is { IsActive: true } user && user.Id.ToString() == User.FindFirstValue(ClaimTypes.NameIdentifier)
        ? user : throw new InvalidOperationException("Sign in again to manage your account.");

    [Authorize, HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult DemoAvatar()
    {
        var demo = HttpContext.RequestServices.GetService<DemoAvatarStore>();
        if (demo == null) return NotFound();
        var image = demo.Read(CurrentAccount());
        if (image == null) return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(image.Value.Bytes, image.Value.Type);
    }

    [Authorize, HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Settings()
    {
        var user = CurrentAccount();
        if (store.IsPersistent && user.Role == "Customer" && PreparedPersistentBooking.Enabled)
            PreparedPersistentProfile.LoadOwnProfile(HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>(), user, store is PublicStudioStore);
        return View(new AccountSettingsPage(user, settings.Get(user.Id)));
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> SaveProfile(string? firstName, string? lastName, int age, string? gender, string address)
    {
        if (DurableEnabled)
        {
            try
            {
                if (!ModelState.IsValid) throw new InvalidOperationException("Check your profile details.");
                if (!await DurableCookieValid()) return Unauthorized();
                if (!long.TryParse(User.FindFirstValue(VersionedSessionAuthorization.SecurityVersionClaim),
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var securityVersion) || securityVersion <= 0)
                    return Unauthorized();
                var durableUser = CurrentAccount();
                if (store.IsPersistent && durableUser.Role == "Customer" && PreparedPersistentBooking.Enabled)
                    PreparedPersistentProfile.LoadOwnProfile(HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>(), durableUser, store is PublicStudioStore);
                var evidence = new AccountSessionEvidence(durableUser.Id, User.FindFirstValue(ClaimTypes.Role),
                    User.FindFirstValue(DurableSessionAuthorization.EpochClaim), User.FindFirstValue("account_version"));
                if (!Guid.TryParseExact(User.FindFirstValue(DurableSessionAuthorization.SubjectClaim), "D", out var subject)) return Unauthorized();
                if (!await settings.ProfileWithDurableEpoch(durableUser, evidence, subject, firstName ?? "", lastName ?? "", age, gender ?? "", address, HttpContext.RequestAborted)) return Unauthorized();
                await SignIn(durableUser, evidence, subject, securityVersion);
                TempData["Success"] = "Profile saved.";
                return RedirectToAction(nameof(Settings));
            }
            catch (InvalidOperationException e) when (e.Message is "Check your profile details.")
            { TempData["Error"] = e.Message; return RedirectToAction(nameof(Settings)); }
            catch (Exception) { return StatusCode(503, "Profile changes could not be confirmed. Please try again later."); }
        }
        var user = CurrentAccount();
        try
        {
            if (store.IsPersistent && user.Role == "Customer" && PreparedPersistentBooking.Enabled)
                PreparedPersistentProfile.LoadOwnProfile(HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>(), user, store is PublicStudioStore);
            if (!ModelState.IsValid) throw new InvalidOperationException("Check your profile details.");
            lock (store.Gate) settings.Profile(user, firstName ?? "", lastName ?? "", age, gender ?? "", address);
            await SignIn(user, verifiedSubject: Guid.TryParseExact(User.FindFirstValue(DurableSessionAuthorization.SubjectClaim), "D", out var providerSubject) ? providerSubject : null);
            TempData["Success"] = "Profile saved.";
        }
        catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
        return RedirectToAction(nameof(Settings));
    }

    [Authorize, HttpPost]
    public IActionResult SaveNotifications(bool notificationsEnabled)
    {
        if (DurableEnabled) return StatusCode(503, "Account changes are temporarily unavailable.");
        settings.Notifications(CurrentAccount().Id, notificationsEnabled);
        TempData["Success"] = "Notification preferences saved.";
        return RedirectToAction(nameof(Settings));
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> SendEmailVerification()
    {
        if (DurableEnabled) return StatusCode(503, "Account changes are temporarily unavailable.");
        var user = CurrentAccount();
        try { await codes.SendAccountCodeAsync(user.Id, user.Email); TempData["Success"] = "A code was sent to your account email. It expires in 15 minutes."; }
        catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
        return RedirectToAction(nameof(Settings));
    }

    [Authorize, HttpPost]
    public IActionResult ConfirmEmail(string code)
    {
        if (DurableEnabled) return StatusCode(503, "Account changes are temporarily unavailable.");
        var user = CurrentAccount();
        try { codes.ConfirmAccountCode(user.Id, user.Email, code); settings.Verify(user); TempData["Success"] = "Your email is verified."; }
        catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
        return RedirectToAction(nameof(Settings));
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> ChangePassword(string currentPassword, string newPassword, string confirmPassword)
    {
        if (PersistentCustomerAuth is { } persistent)
        {
            var error = PasswordPolicy.ValidateNewPassword(newPassword) ?? PasswordPolicy.ValidateConfirmation(newPassword, confirmPassword);
            if (error != null) { TempData["Error"] = error; return RedirectToAction(nameof(Settings)); }
            try
            {
                var outcome = await persistent.PasswordAsync(HttpContext, null, currentPassword, null, newPassword, recovery: false);
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                TempData[outcome == DurableCredentialResult.Completed ? "Success" : "Error"] = outcome == DurableCredentialResult.Completed
                    ? "Password changed. Sign in with your new password."
                    : "Your password update could not be confirmed. Contact support before retrying.";
            }
            catch (Exception)
            {
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                TempData["Error"] = "Your password update could not be confirmed. Contact support before retrying.";
            }
            return RedirectToAction(nameof(Login));
        }
        var user = CurrentAccount();
        try
        {
            var passwordError = PasswordPolicy.ValidateNewPassword(newPassword);
            if (passwordError != null) throw new InvalidOperationException(passwordError);
            var confirmationError = PasswordPolicy.ValidateConfirmation(newPassword, confirmPassword);
            if (confirmationError != null) throw new InvalidOperationException(confirmationError);
            if (supabase.IsConfigured)
            {
                var session = await supabase.SignInWithPasswordAsync(user.Email, currentPassword);
                if (DurableEnabled)
                {
                    var result = await UpdateDurablePassword(user, session, newPassword, requireConfirmed: true, requireCurrentTicket: true);
                    await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    if (result == DurableCredentialResult.Completed) TempData["Success"] = "Password changed. Sign in with your new password.";
                    else TempData["Error"] = "Your password update could not be confirmed. Contact support before retrying.";
                    return RedirectToAction(nameof(Login));
                }
                var synchronized = await AuthenticationPasswordCoordinator.UpdateSupabasePasswordAsync(
                    () => RequireVerifiedBinding(session, user.Email),
                    () => supabase.UpdatePasswordAsync(session.AccessToken, newPassword),
                    () => SetLocalPassword(user.Email, newPassword));
                if (!synchronized)
                {
                    // The remote outcome or local synchronization is uncertain.
                    await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    TempData["Error"] = "Your password update could not be fully completed. Supabase may have accepted the new password. Try signing in with it; if that fails, use Forgot Password or contact support.";
                    return RedirectToAction(nameof(Login));
                }
            }
            else
            {
                if (DurableEnabled) throw new InvalidOperationException("Secure password changes are unavailable.");
                if (!PasswordOk(user, currentPassword)) throw new InvalidOperationException("Current password is incorrect.");
                SetLocalPassword(user.Email, newPassword);
            }
            await HttpContext.SignOutAsync();
            TempData["Success"] = "Password changed. Sign in with your new password.";
            return RedirectToAction(nameof(Login));
        }
        catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
        catch (Exception e) when (DurableEnabled && e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        { TempData["Error"] = "The authentication service is temporarily unavailable. Try again later."; }
        return RedirectToAction(nameof(Settings));
    }
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return Redirect(ReturnDestination(returnUrl, User.FindFirstValue(ClaimTypes.Role)));
        ViewBag.ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        return View();
    }
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Register(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return Redirect(ReturnDestination(returnUrl, User.FindFirstValue(ClaimTypes.Role)));
        ViewBag.ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        return View("Login");
    }

    [HttpPost, EnableRateLimiting(RateLimitPolicies.Login)]
    public async Task<IActionResult> Login(string email, string password, string? returnUrl = null)
    {
        ViewBag.ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        email = (email ?? "").Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        { ViewBag.Error = "Email or password is incorrect."; return View(); }
        if (!loginLimiter.TryAcquire(email, HttpContext.Connection.RemoteIpAddress?.ToString()))
        { ViewBag.Error = "Too many attempts. Wait a minute before retrying."; return View(); }

        if (PersistentCustomerAuth is { } persistent)
        {
            try
            {
                var ticket = await persistent.LoginAsync(email, password, HttpContext.RequestAborted);
                if (ticket != null && DurableEnabled)
                {
                    await SignIn(ticket.User, ticket.Evidence, ticket.Subject, ticket.Generation);
                    return Redirect(ReturnDestination(returnUrl, Roles.Customer));
                }
            }
            catch (Exception) { }
            ViewBag.Error = "Sign-in could not be verified. Check your credentials or contact support.";
            return View();
        }

        // Registration was started but the email code was never verified: no
        // account exists yet, so send them to Verify instead of failing login.
        if (codes.HasPendingRegistration(email))
        {
            TempData["Notice"] = "Check your email for the 6-digit code, then enter it to finish creating your account.";
            return RedirectToAction("Verify", new { email, returnUrl = ViewBag.ReturnUrl });
        }

        if (supabase.IsConfigured)
        {
            try
            {
                if (DurableEnabled && !store.IsPersistent)
                { ViewBag.Error = "Sign-in could not be verified. Contact support."; return View(); }
                var session = await supabase.SignInWithPasswordAsync(email, password);
                var subject = VerifiedProviderSubject(session, email);
                var user = DurableEnabled ? FindLocal(email) : FindVerifiedProviderCustomer(subject, email);
                if (user == null || !user.IsActive)
                { ViewBag.Error = "This account is disabled."; return View(); }
                if (DurableEnabled)
                {
                    var authorization = HttpContext.RequestServices.GetService<VersionedSessionAuthorization>();
                    var prepared = authorization == null ? null : await authorization.PrepareSupabaseLogin(user.Id, subject, user.Role, HttpContext.RequestAborted);
                    if (prepared == null) { ViewBag.Error = "Sign-in could not be verified. Contact support."; return View(); }
                    await SignIn(user, prepared.Value.Evidence, subject, prepared.Value.SecurityVersion);
                    return Redirect(ReturnDestination(returnUrl, user.Role));
                }
                await SignIn(user, verifiedSubject: subject);
                return Redirect(ReturnDestination(returnUrl, user.Role));
            }
            catch (InvalidOperationException e) when (e.Message is "Email or password is incorrect.")
            {
                // A local hash can be stale after a remote reset; never use it as a fallback.
                ViewBag.Error = "Email or password is incorrect.";
                return View();
            }
            catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View(); }
            catch (Exception e) when (DurableEnabled && e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            { ViewBag.Error = "The authentication service is temporarily unavailable. Try again later."; return View(); }
        }

        if (DurableEnabled) { ViewBag.Error = "Sign-in could not be verified. Contact support."; return View(); }
        var account = FindLocal(email);
        if (account == null || !account.IsActive || !PasswordOk(account, password))
        { ViewBag.Error = "Email or password is incorrect."; return View(); }
        await SignIn(account);
        return Redirect(ReturnDestination(returnUrl, account.Role));
    }

    [HttpPost, EnableRateLimiting(RateLimitPolicies.Register)]
    public async Task<IActionResult> Register(string firstName, string lastName, string email, int age, string gender, string address, string password, string? returnUrl = null, string? suffix = null, DateOnly? birthday = null)
    {
        if (PersistentCustomerAuth is { } persistent)
        {
            ViewBag.ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
            try
            {
                if (!ModelState.IsValid) throw new InvalidOperationException("Check your registration details.");
                var result = await persistent.RegisterAsync(firstName,lastName,email,birthday,suffix?.Trim(),
                    gender,address,password,Request.Cookies["GlamSignup"],HttpContext.RequestAborted);
                if (result.BrowserCookie != null)
                {
                    var environment = HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
                    Response.Cookies.Append("GlamSignup", result.BrowserCookie, new CookieOptions { HttpOnly=true,
                        Secure=Request.IsHttps || !environment.IsDevelopment(), SameSite=SameSiteMode.Strict,
                        IsEssential=true, MaxAge=TimeSpan.FromMinutes(30), Path="/Account" });
                }
                if (!result.AwaitingOtp) return StatusCode(503, "Registration could not be confirmed. Contact support before retrying.");
                TempData["Notice"] = "Check your email for the verification code.";
                return RedirectToAction(nameof(Verify),new { email=email.Trim(), returnUrl=ViewBag.ReturnUrl });
            }
            catch (InvalidOperationException e) when (e.Message is not "Registration could not be started. Contact support.")
            { ViewBag.Error=e.Message; return View("Login"); }
            catch (Exception) { return StatusCode(503,"Registration could not be confirmed. Contact support before retrying."); }
        }
        if (DurableEnabled)
        {
            if (!TestOnlyDurableSignup) return StatusCode(503, "Registration is temporarily unavailable.");
            if (TestOnlySignupRetry && !store.IsPersistent) return StatusCode(503, "Registration is temporarily unavailable.");
            if (PreparedPersistentBooking.Enabled)
            {
                var registrationDay = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
                suffix = (suffix ?? "").Trim();
                if (!ModelState.IsValid || !birthday.HasValue || birthday.Value > registrationDay || suffix is not ("" or "Jr." or "Sr." or "II" or "III" or "IV"))
                { ViewBag.Error = "Enter a valid birthday and suffix."; return View("Login"); }
                age = registrationDay.Year - birthday.Value.Year;
                if (birthday.Value.AddYears(age) > registrationDay) age--;
            }
            var validation = RegistrationRules.Validate(firstName, lastName, email, age, gender ?? "", address, password, enforceLegacyAge: !PreparedPersistentBooking.Enabled);
            if (validation != null || string.IsNullOrWhiteSpace(gender)) { ViewBag.Error = validation ?? "Select a gender."; return View("Login"); }
            try
            {
                if (!supabase.IsConfigured) return StatusCode(503, "Registration is temporarily unavailable.");
                var source = HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>();
                var protection = HttpContext.RequestServices.GetRequiredService<IDataProtectionProvider>();
                var attempts = new PostgresDurableSignupAttempts(source, protection);
                var user = new AppUser { FirstName=firstName.Trim(), LastName=lastName.Trim(), Name=firstName.Trim()+" "+lastName.Trim(), Email=email.Trim(), Age=age, Gender=gender, Address=address.Trim(), Role="Customer", IsActive=true, Birthday=PreparedPersistentBooking.Enabled ? birthday : null, Suffix=PreparedPersistentBooking.Enabled ? suffix : null };
                user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password);
                var entry = await attempts.Create(user.Email, JsonSerializer.Serialize(user), HttpContext.RequestAborted);
                // The attempt ID is also the eventual immutable account ID.
                var providerUser = await supabase.SignUpAsync(user.Email, password, new { first_name=user.FirstName, last_name=user.LastName });
                if (!Guid.TryParseExact(providerUser.Id, "D", out var subject) || subject == Guid.Empty || !string.Equals(providerUser.Email.Trim(), user.Email, StringComparison.OrdinalIgnoreCase) || !await attempts.MarkAwaitingOtp(entry.Id, subject, HttpContext.RequestAborted))
                    return StatusCode(503, "Registration could not be confirmed. Contact support.");
                var cookie = protection.CreateProtector("Glam.DurableSignupCookie.v1").Protect(entry.Id.ToString() + ":" + entry.Secret);
                var environment = HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
                Response.Cookies.Append("GlamSignup", cookie, new CookieOptions { HttpOnly=true, Secure=Request.IsHttps || !environment.IsDevelopment(), SameSite=SameSiteMode.Strict, IsEssential=true, MaxAge=TimeSpan.FromMinutes(30), Path="/Account" });
                TempData["Notice"] = "Check your email for the verification code.";
                return RedirectToAction(nameof(Verify), new { email=user.Email });
            }
            catch (Exception) { return StatusCode(503, "Registration could not be confirmed. Contact support before retrying."); }
        }
        ViewBag.ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        ViewBag.Gender = gender;
        if (store.IsPersistent) { ViewBag.Error = "Customer registration is temporarily unavailable until secure profile storage is ready."; return View("Login"); }
        if (!ModelState.IsValid || !birthday.HasValue || birthday.Value > DateOnly.FromDateTime(store.Now))
        { ViewBag.Error = "Enter a valid birthday that is not in the future."; return View("Login"); }
        suffix = (suffix ?? "").Trim();
        if (suffix is not ("" or "Jr." or "Sr." or "II" or "III" or "IV"))
        { ViewBag.Error = "Choose a valid suffix."; return View("Login"); }
        var today = DateOnly.FromDateTime(store.Now);
        age = today.Year - birthday.Value.Year;
        if (birthday.Value.AddYears(age) > today) age--;
        var error = RegistrationRules.Validate(firstName, lastName, email, age, gender ?? "", address, password, enforceLegacyAge: false);
        if (error != null)
        { ViewBag.Error = error; return View("Login"); }
        if (string.IsNullOrEmpty(gender))
        { ViewBag.Error = "Select a gender."; return View("Login"); }
        email = email.Trim();
        if (FindLocal(email) != null)
        { ViewBag.Error = "This email is already registered."; return View("Login"); }

        var pending = new EmailVerificationService.PendingRegistration(
            firstName.Trim(), lastName.Trim(), email, age, gender ?? "", address.Trim(), password, Suffix: suffix, Birthday: birthday);

        if (supabase.IsConfigured)
        {
            if (store.IsPersistent || store is not IVerifiedProviderCustomerStore)
            { ViewBag.Error = "Secure provider registration is temporarily unavailable."; return View("Login"); }
            try
            {
                var providerUser = await supabase.SignUpAsync(email, password, new { first_name = pending.FirstName, last_name = pending.LastName });
                if (!Guid.TryParseExact(providerUser.Id, "D", out var subject) || subject == Guid.Empty ||
                    !string.Equals(providerUser.Email?.Trim(), email, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The account identity could not be verified. Contact support.");
                pending = pending with { ProviderSubject = subject };
            }
            catch (InvalidOperationException e) when (e.Message == "This email is already registered.")
            {
                return await ResumeSupabaseRegistration(pending, returnUrl);
            }
            catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View("Login"); }
            // Hold profile server-side only; the local account is created after
            // the Supabase email code is verified, so login is impossible before.
            // No local code is issued here: the user types Supabase's code.
            codes.HoldRegistration(email, pending);
            TempData["Notice"] = "Your verification request was accepted. Check your inbox and spam folder for the code; delivery can take time.";
            return RedirectToAction("Verify", new { email, returnUrl = ViewBag.ReturnUrl });
        }

        try { await codes.RequestRegistrationCodeAsync(email, pending); }
        catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View("Login"); }
        TempData["Notice"] = "Your verification request was accepted. Check your inbox and spam folder for the code; delivery can take time.";
        return RedirectToAction("Verify", new { email, returnUrl = ViewBag.ReturnUrl });
    }

    [HttpGet]
    public IActionResult Verify(string email, string? returnUrl = null)
    {
        ViewBag.Email = email;
        ViewBag.ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        ViewBag.Notice = TempData["Notice"];
        return View();
    }

    [HttpPost, EnableRateLimiting(RateLimitPolicies.VerifyCode)]
    public async Task<IActionResult> Verify(string email, string code, string? returnUrl = null)
    {
        if (DurableEnabled && !TestOnlyDurableSignup && PersistentCustomerAuth == null) return StatusCode(503, "Verification is temporarily unavailable.");
        ViewBag.Email = email;
        ViewBag.ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        email = (email ?? "").Trim();
        code = (code ?? "").Trim();
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9'))
        { ViewBag.Error = "Enter the 6-digit code from your email."; return View(); }

        if (PersistentCustomerAuth is { } persistent)
            return await VerifyPersistentCustomer(persistent,email,code,false,returnUrl);

        if (DurableEnabled && TestOnlyDurableSignup)
        {
            if (TestOnlySignupRetry) return await RunPersistentSignupRetry(email, code, false, returnUrl);
            try
            {
                var protection = HttpContext.RequestServices.GetRequiredService<IDataProtectionProvider>();
                var raw = protection.CreateProtector("Glam.DurableSignupCookie.v1").Unprotect(Request.Cookies["GlamSignup"] ?? "");
                var separator = raw.IndexOf(':');
                if (separator < 0 || !Guid.TryParseExact(raw[..separator], "D", out var id)) return Unauthorized();
                var source = HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>();
                var attempts = new PostgresDurableSignupAttempts(source, protection);
                var result = await new PostgresDurableCustomerRegistration(source, supabase).CompleteClaimedAsync(id, raw[(separator+1)..], code, attempts, HttpContext.RequestAborted);
                if (result == null) return Unauthorized();
                if (!store.IsPersistent) return StatusCode(503, "Registration was saved but sign-in could not be confirmed.");
                var auth = HttpContext.RequestServices.GetRequiredService<VersionedSessionAuthorization>();
                var prepared = await auth.PrepareSupabaseLogin(result.Value.User.Id, result.Value.Subject, "Customer", HttpContext.RequestAborted);
                // Never mint a fresh epoch if credentials changed after registration committed.
                if (prepared == null || prepared.Value.SecurityVersion != 1 || !RegistrationEvidenceUnchanged(prepared.Value.Evidence, result.Value.User))
                    return StatusCode(503, "Registration was saved but sign-in could not be confirmed.");
                await SignIn(result.Value.User, prepared.Value.Evidence, result.Value.Subject, prepared.Value.SecurityVersion);
                Response.Cookies.Delete("GlamSignup", new CookieOptions { Path="/Account" });
                return Redirect(ReturnDestination(returnUrl, "Customer"));
            }
            catch (Exception) { return StatusCode(503, "Registration could not be confirmed. Contact support before retrying."); }
        }

        if (supabase.IsConfigured)
        {
            SupabaseAuthService.AuthSession? session;
            try { session = await supabase.VerifyOtpAsync(email, code, "signup"); }
            catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View(); }
            catch (Exception e) when (DurableEnabled && e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            { ViewBag.Error = "The authentication service is temporarily unavailable. Try again later."; return View(); }
            if (session == null)
            { ViewBag.Error = "That code is incorrect or expired. Request a new one."; return View(); }
            AppUser user;
            Guid subject;
            try
            {
                subject = VerifiedProviderSubject(session, email);
                var pending = codes.TakeRegistration(email);
                if (store.IsPersistent || store is not IVerifiedProviderCustomerStore binding || pending?.ProviderSubject != subject)
                    throw new InvalidOperationException("The account identity could not be verified. Contact support.");
                user = binding.CreateVerifiedCustomer(subject, pending);
            }
            catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View(); }
            await SignIn(user, verifiedSubject: subject);
            return Redirect(ReturnDestination(returnUrl, user.Role));
        }

        if (DurableEnabled) return StatusCode(503, "Verification is temporarily unavailable.");
        EmailVerificationService.PendingRegistration reg;
        try { reg = codes.ConfirmRegistrationCode(email, code); }
        catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View(); }
        if (FindLocal(email) != null)
        { ViewBag.Error = "This email is already registered."; return View("Login"); }
        AppUser created;
        lock (store.Gate)
        {
            // The held registration carries only a password hash, never the password.
            try { created = store.AddCustomerWithPasswordHash(reg.FirstName, reg.LastName, reg.Email, reg.PasswordHash ?? "", reg.Age, reg.Gender, reg.Address); }
            catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View("Login"); }
            created.Suffix = reg.Suffix; created.Birthday = reg.Birthday;
        }
        settings.Verify(created);
        await SignIn(created);
        return Redirect(ReturnDestination(returnUrl, created.Role));
    }

    [HttpPost, EnableRateLimiting(RateLimitPolicies.ResendCode)]
    public async Task<IActionResult> Resend(string email, string? returnUrl = null)
    {
        if (DurableEnabled && !TestOnlyDurableSignup && PersistentCustomerAuth == null) return StatusCode(503, "Verification is temporarily unavailable.");
        email = (email ?? "").Trim();
        ViewBag.Email = email;
        ViewBag.ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        if (PersistentCustomerAuth is { } persistent)
            return await VerifyPersistentCustomer(persistent,email,null,true,returnUrl);
        if (DurableEnabled && TestOnlyDurableSignup && TestOnlySignupRetry)
            return await RunPersistentSignupRetry(email, null, true, returnUrl);
        try
        {
            if (supabase.IsConfigured) await supabase.ResendSignupAsync(email);
            else await codes.ResendRegistrationCodeAsync(email);
            TempData["Notice"] = "Your verification request was accepted. Check your inbox and spam folder for the code; delivery can take time.";
        }
        catch (InvalidOperationException e)
        {
            ViewBag.Error = e.Message;
            return View("Verify");
        }
        return RedirectToAction("Verify", new { email, returnUrl });
    }

    private async Task<IActionResult> RunPersistentSignupRetry(string email, string? code, bool resend, string? returnUrl)
    {
        if (!store.IsPersistent) return StatusCode(503, "Registration is temporarily unavailable.");
        try
        {
            var protection = HttpContext.RequestServices.GetRequiredService<IDataProtectionProvider>();
            var raw = protection.CreateProtector("Glam.DurableSignupCookie.v1").Unprotect(Request.Cookies["GlamSignup"] ?? "");
            var separator = raw.IndexOf(':');
            if (separator < 0 || !Guid.TryParseExact(raw[..separator], "D", out var id)) return Unauthorized();
            var source = HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>();
            var retry = PostgresPersistentSignupRetryStore.Create(source, protection, new SupabaseSignupRetryProvider(supabase));
            var result = resend
                ? await retry.ResendAsync(id, raw[(separator + 1)..], HttpContext.RequestAborted)
                : await retry.VerifyAsync(id, raw[(separator + 1)..], code!, HttpContext.RequestAborted);
            if (result == SignupRetryResult.Completed)
            {
                Response.Cookies.Delete("GlamSignup", new CookieOptions { Path = "/Account" });
                TempData["Notice"] = "Registration completed. Sign in to continue.";
                return RedirectToAction(nameof(Login), new { returnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null });
            }
            if (result == SignupRetryResult.ResendAccepted)
            {
                TempData["Notice"] = "Your verification request was accepted. Check your inbox and spam folder.";
                return RedirectToAction(nameof(Verify), new { email, returnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null });
            }
            if (result == SignupRetryResult.TryAgain)
            {
                ViewBag.Error = "The request was not accepted. Check the code or request a new one.";
                return View("Verify");
            }
            if (result == SignupRetryResult.Unavailable)
            {
                ViewBag.Error = "This verification request is unavailable or may have expired. Contact support before starting again.";
                return View("Verify");
            }
        }
        catch (Exception) { }
        Response.StatusCode = 503;
        ViewBag.Error = "We could not confirm the verification request. Contact support before retrying.";
        return View("Verify");
    }

    private async Task<IActionResult> VerifyPersistentCustomer(PersistentCustomerAuthentication persistent,
        string email, string? code, bool resend, string? returnUrl)
    {
        try
        {
            var result=await persistent.VerifyAsync(Request.Cookies["GlamSignup"],email,code,resend,HttpContext.RequestAborted);
            if (result==SignupRetryResult.Completed)
            {
                Response.Cookies.Delete("GlamSignup",new CookieOptions { Path="/Account" });
                TempData["Notice"]="Registration completed. Sign in to continue.";
                return RedirectToAction(nameof(Login),new { returnUrl=Url.IsLocalUrl(returnUrl) ? returnUrl : null });
            }
            if (result==SignupRetryResult.ResendAccepted)
            {
                TempData["Notice"]="Your verification request was accepted. Check your inbox and spam folder.";
                return RedirectToAction(nameof(Verify),new { email,returnUrl=Url.IsLocalUrl(returnUrl) ? returnUrl : null });
            }
        }
        catch (Exception) { }
        Response.StatusCode=503;
        ViewBag.Email=email;
        ViewBag.Error="We could not confirm the verification request. Contact support before retrying.";
        return View("Verify");
    }

    private async Task<IActionResult> ResumeSupabaseRegistration(EmailVerificationService.PendingRegistration pending, string? returnUrl)
    {
        try
        {
            SupabaseAuthService.AuthSession? session = null;
            try { session = await supabase.SignInWithPasswordAsync(pending.Email, pending.Password); }
            catch (InvalidOperationException e) when (e.Message is "Email or password is incorrect." or "Email confirmation is required.") { }

            if (session != null)
            {
                RequireVerifiedBinding(session, pending.Email);
                TempData["Notice"] = "This email is already verified. Sign in, or use Forgot password if needed.";
                return RedirectToAction(nameof(Login), new { returnUrl });
            }
            throw new InvalidOperationException("The account identity could not be verified. Contact support.");
        }
        catch (InvalidOperationException e)
        {
            ViewBag.Error = e.Message.Contains("Too many", StringComparison.OrdinalIgnoreCase)
                ? e.Message
                : "This email is already registered. Sign in, or use Forgot password to recover it.";
            return View("Login");
        }
    }

    [HttpGet]
    public IActionResult ForgotPassword() => View();

    [HttpPost, EnableRateLimiting(RateLimitPolicies.ForgotPassword)]
    public async Task<IActionResult> ForgotPassword(string email)
    {
        email = (email ?? "").Trim();
        if (PersistentCustomerAuth is { } persistent)
        {
            try
            {
                if (!await persistent.RecoverAsync(email,HttpContext.RequestAborted)) return StatusCode(503,"Password recovery is temporarily unavailable.");
                TempData["Notice"]="If that email has an account, a reset code was sent. Enter it below.";
                return RedirectToAction(nameof(ResetPassword),new { email });
            }
            catch (Exception) { return StatusCode(503,"Password recovery could not be confirmed."); }
        }
        if (supabase.IsConfigured)
        {
            try { await supabase.SendRecoveryAsync(email); }
            catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View(); }
            catch (Exception e) when (DurableEnabled && e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            { ViewBag.Error = "The authentication service is temporarily unavailable. Try again later."; return View(); }
            TempData["Notice"] = "If that email has an account, a reset code was sent. Enter it below.";
            return RedirectToAction("ResetPassword", new { email });
        }
        if (DurableEnabled) return StatusCode(503, "Password recovery is temporarily unavailable.");
        try { await codes.RequestPasswordResetCodeAsync(email); }
        catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View(); }
        TempData["Notice"] = "If that email has an account, a reset code was sent. Enter it below.";
        return RedirectToAction("ResetPassword", new { email });
    }

    [HttpGet]
    public IActionResult ResetPassword(string email)
    {
        ViewBag.Email = email;
        ViewBag.Notice = TempData["Notice"];
        return View();
    }

    [HttpPost, EnableRateLimiting(RateLimitPolicies.ResetPassword)]
    public async Task<IActionResult> ResetPassword(string email, string code, string newPassword, string? confirmPassword)
    {
        ViewBag.Email = email;
        email = (email ?? "").Trim();
        var passwordError = PasswordPolicy.ValidateNewPassword(newPassword);
        if (passwordError != null)
        { ViewBag.Error = passwordError; return View(); }
        var confirmationError = PasswordPolicy.ValidateConfirmation(newPassword, confirmPassword);
        if (confirmationError != null)
        { ViewBag.Error = confirmationError; return View(); }
        if ((code ?? "").Trim().Length != 6)
        { ViewBag.Error = "Enter the 6-digit code from your email."; return View(); }

        if (PersistentCustomerAuth is { } persistent)
        {
            try
            {
                if ((code ?? "").Trim().Any(c=>c is < '0' or > '9')) throw new InvalidOperationException();
                var outcome=await persistent.PasswordAsync(HttpContext,email,null,code!.Trim(),newPassword,recovery:true);
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                TempData[outcome==DurableCredentialResult.Completed ? "Notice" : "Error"]=outcome==DurableCredentialResult.Completed
                    ? "Password updated. Sign in with your new password."
                    : "Your password update could not be confirmed. Contact support before retrying.";
                return RedirectToAction(nameof(Login));
            }
            catch (Exception) { return StatusCode(503,"Your password update could not be confirmed. Contact support before retrying."); }
        }

        if (supabase.IsConfigured)
        {
            SupabaseAuthService.AuthSession? session;
            try { session = await supabase.VerifyOtpAsync(email, (code ?? "").Trim(), "recovery"); }
            catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View(); }
            catch (Exception e) when (DurableEnabled && e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            { ViewBag.Error = "The authentication service is temporarily unavailable. Try again later."; return View(); }
            if (session == null)
            { ViewBag.Error = "That code is incorrect or expired."; return View(); }
            if (DurableEnabled)
            {
                var existing = FindLocal(email);
                var outcome = existing == null ? DurableCredentialResult.NotStarted : await UpdateDurablePassword(existing, session, newPassword, requireConfirmed: false, requireCurrentTicket: false);
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                if (outcome == DurableCredentialResult.Completed) TempData["Notice"] = "Password updated. Sign in with your new password.";
                else TempData["Error"] = "Your password update could not be confirmed. Contact support before retrying.";
                return RedirectToAction(nameof(Login));
            }
            try
            {
                var synchronized = await AuthenticationPasswordCoordinator.UpdateSupabasePasswordAsync(
                    () => RequireVerifiedBinding(session, email),
                    () => supabase.UpdatePasswordAsync(session.AccessToken, newPassword),
                    () => SetLocalPassword(email, newPassword));
                if (!synchronized)
                {
                    // The recovery OTP was consumed; the remote outcome may be uncertain.
                    await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    TempData["Error"] = "Your password update could not be fully completed. Supabase may have accepted the new password. Try signing in with it; if that fails, request another reset code or contact support.";
                    return RedirectToAction(nameof(Login));
                }
            }
            catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View(); }
            TempData["Notice"] = "Password updated. Sign in with your new password.";
            return RedirectToAction("Login");
        }

        if (DurableEnabled) return StatusCode(503, "Password recovery is temporarily unavailable.");
        try { codes.ConfirmPasswordResetCode(email, (code ?? "").Trim()); }
        catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View(); }
        try
        {
            codes.RequireResetVerified(email);
            var account = FindLocal(email);
            if (account == null || !TryCheckExistingPassword(account, newPassword, out var reused))
                throw new InvalidOperationException("Unable to verify the existing password. Request a new code or contact support.");
            if (reused) throw new InvalidOperationException("Choose a different password and request a new reset code.");
            SetLocalPassword(email, newPassword);
        }
        catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View(); }
        finally { codes.ClearResetVerified(email); }
        TempData["Notice"] = "Password updated. Sign in with your new password.";
        return RedirectToAction("Login");
    }

    // Administrator invitation acceptance. Anonymous: the single-use token in the
    // emailed link is the credential. The Admin role is assigned server-side only.
    [HttpGet]
    public IActionResult AcceptInvite(string email, string token)
    {
        ViewBag.Email = (email ?? "").Trim();
        ViewBag.Token = (token ?? "").Trim();
        ViewBag.Notice = TempData["Notice"];
        ViewBag.AcceptanceUnavailable = true;
        ViewBag.Error = AdminInvitationUnavailableMessage;
        return View();
    }

    [HttpPost, EnableRateLimiting(RateLimitPolicies.AcceptInvite)]
    public async Task<IActionResult> AcceptInvite(string email, string token, string firstName, string lastName, int age, string? gender, string address, string password)
    {
        // Temporary LOCAL-ONLY containment pending verified persistent provisioning.
        // Keep this fixed code-level guard ahead of validation, the internal test
        // seam, token consumption, account mutation, provider calls, audit, and sign-in.
        if (AdminInvitationAcceptanceIsContained())
        {
            ViewBag.Email = (email ?? "").Trim();
            ViewBag.Token = (token ?? "").Trim();
            ViewBag.AcceptanceUnavailable = true;
            ViewBag.Error = AdminInvitationUnavailableMessage;
            return View();
        }

        email = (email ?? "").Trim();
        var error = RegistrationRules.Validate(firstName, lastName, email, age, gender ?? "", address, password);
        if (error != null)
        { ViewBag.Error = error; return View(); }
        if (FindLocal(email) != null)
        { ViewBag.Error = "This email is already registered. Sign in instead."; return View("Login"); }

        if (TestOnlyAdminInvitationWorkflow is { } testWorkflow)
        {
            AdminInvitationWorkflowResult workflowResult;
            try
            {
                workflowResult = await testWorkflow.AcceptAsync(email, (token ?? "").Trim(), firstName,
                    lastName, age, gender ?? "", address, password, HttpContext.RequestAborted);
            }
            catch (Exception)
            {
                ViewBag.Error = "Administrator account setup could not be confirmed. Contact the business owner before retrying this invitation.";
                return View();
            }

            if (!workflowResult.Succeeded)
            {
                ViewBag.Error = "Administrator account setup could not be confirmed. Contact the business owner before retrying this invitation.";
                return View();
            }

            var activated = FindLocal(email);
            if (activated is not { Role: Roles.Admin, IsActive: true }
                || !activated.Email.Equals(email, StringComparison.OrdinalIgnoreCase))
            {
                ViewBag.Error = "Administrator account setup could not be confirmed. Contact the business owner before retrying this invitation.";
                return View();
            }

            settings.Verify(activated);
            store.RecordAudit(activated.Id, $"Administrator invitation accepted for {email}.");
            await SignIn(activated);
            return Redirect("/Admin");
        }

        try { invites.Accept(email, (token ?? "").Trim()); }
        catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View(); }
        AppUser user;
        lock (store.Gate)
        {
            try { user = store.CreateElevatedUser(firstName.Trim(), lastName.Trim(), email, password, Roles.Admin, age, gender ?? "", address.Trim()); }
            catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View(); }
        }
        // The invite has already been consumed and the local Admin created. Do
        // not report success or issue a session unless provider provisioning is
        // explicitly confirmed. Exceptions are ambiguous: do not retry here.
        settings.Verify(user);
        var provisioned = false;
        try
        {
            provisioned = await supabase.CreateConfirmedUserAsync(
                email, password, new { first_name = user.FirstName, last_name = user.LastName });
        }
        catch (Exception)
        {
            // The provider may have created the identity before the connection
            // failed. Keep the local state for operator reconciliation; never
            // retry, audit success, or authenticate on an uncertain outcome.
        }
        if (!provisioned)
        {
            ViewBag.Error = "Administrator account setup could not be confirmed. Contact the business owner before retrying this invitation.";
            return View();
        }
        store.RecordAudit(user.Id, $"Administrator invitation accepted for {email}.");
        await SignIn(user);
        return Redirect("/Admin");
    }

    // Fixed fail-closed operational safeguard. No configuration, request value,
    // DI registration, or test-only collaborator can disable it.
    private static bool AdminInvitationAcceptanceIsContained() => true;

    private AppUser? FindLocal(string email)
    {
        lock (store.Gate)
        {
            if (store is PostgresStudioStore database) return database.FindUser(email);
            return store.Users.FirstOrDefault(x => x.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool PasswordOk(AppUser user, string password) =>
        !string.IsNullOrEmpty(password) &&
        new PasswordHasher<AppUser>().VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;

    private static bool TryCheckExistingPassword(AppUser user, string password, out bool matches)
    {
        matches = false;
        byte[] hash;
        try { hash = Convert.FromBase64String(user.PasswordHash ?? ""); }
        catch (FormatException) { return false; }
        if (hash.Length > 0 && hash.Skip(1).All(value => value == 0)) return false;
        if (hash.Length == 49 && hash[0] == 0) { }
        else if (hash.Length >= 45 && hash[0] == 1)
        {
            var prf = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(1, 4));
            var iterations = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(5, 4));
            var saltLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(9, 4));
            if (prf > 2 || iterations == 0 || iterations > int.MaxValue || saltLength < 16 || saltLength > hash.Length - 29) return false;
        }
        else return false;
        try { matches = PasswordOk(user, password); return true; }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException or System.Security.Cryptography.CryptographicException) { return false; }
    }

    private void SetLocalPassword(string email, string newPassword)
    {
        lock (store.Gate)
        {
            if (store is PostgresStudioStore database) { database.ResetPassword(email, newPassword); return; }
            var user = store.Users.FirstOrDefault(x => x.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("No account uses that email.");
            user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, newPassword);
        }
    }

    private static Guid VerifiedProviderSubject(SupabaseAuthService.AuthSession session, string expectedEmail)
    {
        if (string.IsNullOrWhiteSpace(session.AccessToken) || string.IsNullOrWhiteSpace(expectedEmail) ||
            session.User?.EmailConfirmedAt == null || !string.Equals(session.User.Email?.Trim(), expectedEmail.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(session.User.Id, "D", out var subject) || subject == Guid.Empty)
            throw new InvalidOperationException("The account identity could not be verified. Contact support.");
        return subject;
    }
    private AppUser? FindVerifiedProviderCustomer(Guid subject, string email) =>
        !store.IsPersistent && store is IVerifiedProviderCustomerStore binding ? binding.FindVerifiedCustomer(subject, email) : null;
    private void RequireVerifiedBinding(SupabaseAuthService.AuthSession session, string email)
    {
        var subject = VerifiedProviderSubject(session, email);
        if (FindVerifiedProviderCustomer(subject, email) == null)
            throw new InvalidOperationException("The account identity could not be verified. Contact support.");
    }

    [AllowAnonymous, HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult AccessDenied()
    {
        // Never redirect or render the supplied ReturnUrl on an authorization failure.
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View();
    }

    private string ReturnDestination(string? returnUrl, string? role)
    {
        if (CustomerAuthIntegrationProfile.Enabled(configuration)) return "/auth-test/session";
        var isAdmin = role is Roles.Admin or Roles.SuperAdmin;
        var dashboard = isAdmin ? "/Admin" : "/Bookings";
        if (!Url.IsLocalUrl(returnUrl)) return dashboard;
        var localUrl = Url.Content(returnUrl)!;
        var origin = new Uri("https://localhost");
        if (!Uri.TryCreate(origin, localUrl, out var destination)) return dashboard;
        var path = Uri.UnescapeDataString(destination.AbsolutePath);
        if (!Url.IsLocalUrl(path) || path.Any(char.IsControl) || path.Contains('\\') || path.Contains('%')
            || !Uri.TryCreate(origin, path, out var normalized) || normalized.Authority != origin.Authority) return dashboard;
        var segments = normalized.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var controller = segments.FirstOrDefault();
        if (string.Equals(controller, "Account", StringComparison.OrdinalIgnoreCase)
            || (isAdmin && string.Equals(controller, "Bookings", StringComparison.OrdinalIgnoreCase))
            || (!isAdmin && string.Equals(controller, "Admin", StringComparison.OrdinalIgnoreCase))) return dashboard;
        // Login returns for ordinary Admins are limited to their existing GET pages.
        // Endpoint authorization remains authoritative; no role is granted here.
        if (role == Roles.Admin && string.Equals(controller, "Admin", StringComparison.OrdinalIgnoreCase)
            && !new[] { "Index", "Details", "Calendar", "Catalog", "Reports", "Settings", "Proof" }
                .Contains(segments.ElementAtOrDefault(1) ?? "Index", StringComparer.OrdinalIgnoreCase)) return dashboard;
        return localUrl;
    }
    private async Task<AccountSessionEvidence?> DurableEvidence(AppUser user, SupabaseAuthService.AuthSession session, bool requireConfirmed)
    {
        if (!user.IsActive || string.IsNullOrWhiteSpace(session.AccessToken) ||
            !string.Equals(session.User.Email?.Trim(), user.Email.Trim(), StringComparison.OrdinalIgnoreCase) ||
            (requireConfirmed && session.User.EmailConfirmedAt == null) ||
            !Guid.TryParse(session.User.Id, out var subject) || subject == Guid.Empty) return null;
        var service = HttpContext.RequestServices.GetService<DurableSessionAuthorization>();
        return service == null ? null : await service.PrepareSupabaseLogin(user.Id, subject, user.Role, HttpContext.RequestAborted);
    }

    private async Task<bool> DurableCookieValid()
    {
        var service = HttpContext.RequestServices.GetService<VersionedSessionAuthorization>();
        return service != null && await service.ValidateCookie(User, HttpContext.RequestAborted);
    }

    internal static bool RegistrationEvidenceUnchanged(AccountSessionEvidence? evidence, AppUser user) =>
        evidence != null && evidence.AccountId == user.Id && evidence.Role == "Customer" && evidence.CredentialEpoch == "1" &&
        evidence.AccountVersion == AccountSession.Version(user.PasswordHash);

    private async Task<DurableCredentialResult> UpdateDurablePassword(AppUser user, SupabaseAuthService.AuthSession session, string password, bool requireConfirmed, bool requireCurrentTicket)
    {
        var evidence = await DurableEvidence(user, session, requireConfirmed);
        if (evidence == null || !long.TryParse(evidence.CredentialEpoch, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var epoch) ||
            !Guid.TryParse(session.User.Id, out var subject)) return DurableCredentialResult.NotStarted;
        if (requireCurrentTicket && (!await DurableCookieValid() || User.FindFirstValue(DurableSessionAuthorization.EpochClaim) != evidence.CredentialEpoch ||
            User.FindFirstValue(ClaimTypes.NameIdentifier) != user.Id.ToString() ||
            User.FindFirstValue(DurableSessionAuthorization.SubjectClaim) != subject.ToString())) return DurableCredentialResult.NotStarted;
        var update = HttpContext.RequestServices.GetService<DurableCredentialUpdate>();
        if (update == null) return DurableCredentialResult.NotStarted;
        return await update.Change(user.Id, subject, epoch, password, async ct =>
        {
            // Until provider rejection semantics are verified, every exception is uncertain.
            await supabase.UpdatePasswordVerifiedAsync(session.AccessToken, password, subject, session.User.Email, ct);
            return CredentialProviderResult.Confirmed;
        }, HttpContext.RequestAborted);
    }

    private Task SignIn(AppUser user, AccountSessionEvidence? evidence = null, Guid? verifiedSubject = null, long? securityVersion = null)
    {
        if (DurableEnabled && (!store.IsPersistent || evidence == null || evidence.AccountId != user.Id || evidence.Role != user.Role ||
            string.IsNullOrEmpty(evidence.CredentialEpoch) || string.IsNullOrEmpty(evidence.AccountVersion) ||
            verifiedSubject == null || verifiedSubject == Guid.Empty || securityVersion is null or <= 0))
            throw new InvalidOperationException("Secure sign-in is unavailable.");
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Name, user.Name), new(ClaimTypes.Email, user.Email), new(ClaimTypes.Role, user.Role), new("account_version", evidence?.AccountVersion ?? AccountSession.Version(user.PasswordHash)) };
        if (evidence != null) claims.Add(new(DurableSessionAuthorization.EpochClaim, evidence.CredentialEpoch!));
        if (verifiedSubject != null) claims.Add(new(DurableSessionAuthorization.SubjectClaim, verifiedSubject.Value.ToString()));
        if (securityVersion != null) claims.Add(new(VersionedSessionAuthorization.SecurityVersionClaim, securityVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    }
    [HttpPost("/Account/Logout")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Logout()
    {
        var revoked=true;
        if (PersistentCustomerAuth is { } persistent)
        {
            try { revoked=await persistent.LogoutAsync(HttpContext); }
            catch (Exception) { revoked=false; }
        }
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        if (!revoked) return StatusCode(503,"This browser was signed out, but session revocation could not be confirmed. Contact support.");
        return RedirectToAction(nameof(Login));
    }
}
