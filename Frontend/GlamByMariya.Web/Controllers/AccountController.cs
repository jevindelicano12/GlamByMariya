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
using Npgsql;

namespace GlamByMariya.Web.Controllers;

// Local accounts only: email + password hashes in our own database, with Brevo
// email codes (OTP) for registration and password reset. No external auth provider.
public class AccountController(
    IStudioStore store, EmailVerificationService codes, AccountSettingsService settings, AvatarStorageService avatars, IInvitationService invites, LoginAttemptLimiter loginLimiter) : Controller
{
    private const string AdminInvitationUnavailableMessage = "Administrator invitation acceptance is temporarily unavailable. Please contact the business owner for assistance.";
    // Secret that binds a pending email-code registration to the browser that started it.
    private string? RegistrationBrowserToken => Request.Cookies[EmailVerificationService.BrowserCookieName];
    private string IssueRegistrationBrowserToken()
    {
        var token = RegistrationBrowserToken is { Length: >= 32 } existing ? existing : EmailVerificationService.NewBrowserToken();
        var development = HttpContext.RequestServices.GetService<IHostEnvironment>()?.IsDevelopment() == true;
        Response.Cookies.Append(EmailVerificationService.BrowserCookieName, token, new CookieOptions
        {
            HttpOnly = true, Secure = Request.IsHttps || !development, SameSite = SameSiteMode.Strict,
            IsEssential = true, MaxAge = TimeSpan.FromMinutes(30), Path = "/Account"
        });
        return token;
    }

    [Authorize, HttpPost, RequestSizeLimit(2200000)]
    public async Task<IActionResult> UploadAvatar(IFormFile? avatar)
    {
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
        var user = CurrentAccount();
        try
        {
            if (store.IsPersistent && user.Role == "Customer" && PreparedPersistentBooking.Enabled)
                PreparedPersistentProfile.LoadOwnProfile(HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>(), user, store is PublicStudioStore);
            if (!ModelState.IsValid) throw new InvalidOperationException("Check your profile details.");
            lock (store.Gate) settings.Profile(user, firstName ?? "", lastName ?? "", age, gender ?? "", address);
            await SignIn(user);
            TempData["Success"] = "Profile saved.";
        }
        catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
        return RedirectToAction(nameof(Settings));
    }

    [Authorize, HttpPost]
    public IActionResult SaveNotifications(bool notificationsEnabled)
    {
        settings.Notifications(CurrentAccount().Id, notificationsEnabled);
        TempData["Success"] = "Notification preferences saved.";
        return RedirectToAction(nameof(Settings));
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> SendEmailVerification()
    {
        var user = CurrentAccount();
        try { await codes.SendAccountCodeAsync(user.Id, user.Email); TempData["Success"] = "A code was sent to your account email. It expires in 15 minutes."; }
        catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
        return RedirectToAction(nameof(Settings));
    }

    [Authorize, HttpPost]
    public IActionResult ConfirmEmail(string code)
    {
        var user = CurrentAccount();
        try { codes.ConfirmAccountCode(user.Id, user.Email, code); settings.Verify(user); TempData["Success"] = "Your email is verified."; }
        catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
        return RedirectToAction(nameof(Settings));
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> ChangePassword(string currentPassword, string newPassword, string confirmPassword)
    {
        var user = CurrentAccount();
        try
        {
            var passwordError = PasswordPolicy.ValidateNewPassword(newPassword);
            if (passwordError != null) throw new InvalidOperationException(passwordError);
            var confirmationError = PasswordPolicy.ValidateConfirmation(newPassword, confirmPassword);
            if (confirmationError != null) throw new InvalidOperationException(confirmationError);
            if (!PasswordOk(user, currentPassword)) throw new InvalidOperationException("Current password is incorrect.");
            SetLocalPassword(user.Email, newPassword);
            // The password hash changed, so every other session of this account stops working too.
            await HttpContext.SignOutAsync();
            TempData["Success"] = "Password changed. Sign in with your new password.";
            return RedirectToAction(nameof(Login));
        }
        catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
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

        // Registration was started in this browser but the email code was never
        // verified: no account exists yet, so send them to Verify instead.
        if (codes.HasPendingRegistration(email, RegistrationBrowserToken))
        {
            TempData["Notice"] = "Check your email for the 6-digit code, then enter it to finish creating your account.";
            return RedirectToAction("Verify", new { email, returnUrl = ViewBag.ReturnUrl });
        }

        var account = FindLocal(email);
        if (account == null || !account.IsActive || !PasswordOk(account, password))
        { ViewBag.Error = "Email or password is incorrect."; return View(); }
        await SignIn(account);
        return Redirect(ReturnDestination(returnUrl, account.Role));
    }

    [HttpPost, EnableRateLimiting(RateLimitPolicies.Register)]
    public async Task<IActionResult> Register(string firstName, string lastName, string email, int age, string gender, string address, string password, string? returnUrl = null, string? suffix = null, DateOnly? birthday = null)
    {
        ViewBag.ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        ViewBag.Gender = gender;
        if (!ModelState.IsValid || !birthday.HasValue || birthday.Value > DateOnly.FromDateTime(store.Now) || birthday.Value < new DateOnly(1900, 1, 1))
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
        try { await codes.RequestRegistrationCodeAsync(email, pending, IssueRegistrationBrowserToken()); }
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
        ViewBag.Email = email;
        ViewBag.ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        email = (email ?? "").Trim();
        code = (code ?? "").Trim();
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9'))
        { ViewBag.Error = "Enter the 6-digit code from your email."; return View(); }

        EmailVerificationService.PendingRegistration reg;
        if (RegistrationBrowserToken is not { Length: > 0 } browserToken)
        { ViewBag.Error = "Finish verification in the same browser you used to register, or register again."; return View(); }
        try { reg = codes.ConfirmRegistrationCode(email, code, browserToken); }
        catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View(); }
        Response.Cookies.Delete(EmailVerificationService.BrowserCookieName, new CookieOptions { Path = "/Account" });
        if (FindLocal(email) != null)
        { ViewBag.Error = "This email is already registered."; return View("Login"); }
        AppUser created;
        lock (store.Gate)
        {
            // The held registration carries only a password hash, never the password.
            try { created = store.AddCustomerWithPasswordHash(reg.FirstName, reg.LastName, reg.Email, reg.PasswordHash ?? "", reg.Age, reg.Gender, reg.Address, reg.Birthday, reg.Suffix); }
            catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View("Login"); }
        }
        settings.Verify(created);
        await SignIn(created);
        return Redirect(ReturnDestination(returnUrl, created.Role));
    }

    [HttpPost, EnableRateLimiting(RateLimitPolicies.ResendCode)]
    public async Task<IActionResult> Resend(string email, string? returnUrl = null)
    {
        email = (email ?? "").Trim();
        ViewBag.Email = email;
        ViewBag.ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        try
        {
            await codes.ResendRegistrationCodeAsync(email, RegistrationBrowserToken);
            TempData["Notice"] = "Your verification request was accepted. Check your inbox and spam folder for the code; delivery can take time.";
        }
        catch (InvalidOperationException e)
        {
            ViewBag.Error = e.Message;
            return View("Verify");
        }
        return RedirectToAction("Verify", new { email, returnUrl });
    }

    [HttpGet]
    public IActionResult ForgotPassword() => View();

    [HttpPost, EnableRateLimiting(RateLimitPolicies.ForgotPassword)]
    public async Task<IActionResult> ForgotPassword(string email)
    {
        email = (email ?? "").Trim();
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
    public IActionResult ResetPassword(string email, string code, string newPassword, string? confirmPassword)
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
        // Still contained until the accounts step reopens it with local accounts only.
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
        try { invites.Accept(email, (token ?? "").Trim()); }
        catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View(); }
        AppUser user;
        lock (store.Gate)
        {
            try { user = store.CreateElevatedUser(firstName.Trim(), lastName.Trim(), email, password, Roles.Admin, age, gender ?? "", address.Trim()); }
            catch (InvalidOperationException e) { ViewBag.Error = e.Message; return View(); }
        }
        settings.Verify(user);
        store.RecordAudit(user.Id, $"Administrator invitation accepted for {email}.");
        await SignIn(user);
        return Redirect("/Admin");
    }

    // Fixed fail-closed safeguard until the accounts step reopens invitations.
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

    [AllowAnonymous, HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult AccessDenied()
    {
        // Never redirect or render the supplied ReturnUrl on an authorization failure.
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View();
    }

    private string ReturnDestination(string? returnUrl, string? role)
    {
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

    // The account_version claim is a fingerprint of the password hash: changing the
    // password (or disabling the account) makes every older session invalid.
    private Task SignIn(AppUser user)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Name, user.Name), new(ClaimTypes.Email, user.Email), new(ClaimTypes.Role, user.Role), new("account_version", AccountSession.Version(user.PasswordHash)) };
        return HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    }
    [HttpPost("/Account/Logout")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        return RedirectToAction(nameof(Login));
    }
}
