using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace GlamByMariya.Infrastructure.Services;

/// <summary>
/// Local 6-digit email verification codes, sent through Brevo. Codes are SHA-256
/// hashed at rest, expire in 15 minutes, allow 5 wrong attempts, and resends are
/// rate-limited to 60s. Each purpose (registration, password reset, account email)
/// has its own code, attempt counter and cooldown. Storage is an IEmailCodeStore:
/// memory for Demo/local, PostgreSQL in database mode so codes survive restarts.
/// A pending registration keeps only a password hash, never the password itself.
/// </summary>
public sealed class EmailVerificationService
{
    public sealed record PendingRegistration(
        string FirstName, string LastName, string Email, int Age, string Gender, string Address, string Password, Guid? ProviderSubject = null, string? Suffix = null, DateOnly? Birthday = null,
        string? PasswordHash = null);

    public const int MaxAttempts = 5;
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);
    private const string Registration = "registration";
    private const string Reset = "reset";
    private const string ResetVerified = "reset-ok";
    private static string Account(Guid userId) => "account:" + userId.ToString("N");

    private readonly IMemoryCache cache;
    private readonly IConfiguration config;
    private readonly ILogger<EmailVerificationService> log;
    private readonly IWebHostEnvironment env;
    private readonly BrevoEmailSender brevo;
    private readonly IEmailCodeStore store;

    // Kept for existing callers and tests: memory-backed codes.
    public EmailVerificationService(IMemoryCache cache, IConfiguration config, ILogger<EmailVerificationService> log, IWebHostEnvironment env,
        BrevoEmailSender brevo) : this(cache, config, log, env, brevo, new MemoryEmailCodeStore(cache)) { }

    public EmailVerificationService(IMemoryCache cache, IConfiguration config, ILogger<EmailVerificationService> log, IWebHostEnvironment env,
        BrevoEmailSender brevo, IEmailCodeStore store)
    {
        this.cache = cache; this.config = config; this.log = log; this.env = env; this.brevo = brevo; this.store = store;
    }

    private static DateTime UtcNow => DateTime.UtcNow;

    public async Task SendAccountCodeAsync(Guid userId, string email)
    {
        var code = NewCode();
        await SendWithCooldownAsync("account", email, "Your Glam by Mariya Verification Code",
            $"Your Glam by Mariya verification code is {code}. It expires in 15 minutes. If you did not request this, please ignore this email.");
        store.Save(Account(userId), email, Hash(code), UtcNow.Add(CodeLifetime), null);
    }

    public void ConfirmAccountCode(Guid userId, string email, string code)
    {
        var (result, _) = store.Consume(Account(userId), email, Hash((code ?? "").Trim()), MaxAttempts, UtcNow);
        if (result == EmailCodeCheck.Ok) return;
        throw new InvalidOperationException(result == EmailCodeCheck.Wrong
            ? "Incorrect code. Check your newest email."
            : "Code expired or attempt limit reached. Request a new code.");
    }

    private static string NewCode()
    {
        var n = RandomNumberGenerator.GetInt32(100000, 1000000);
        return n.ToString("D6");
    }

    private static string Hash(string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("emailcode:" + code));
        return Convert.ToHexString(bytes);
    }

    private void StartCooldown(string scope, string email)
    {
        if (!store.TryStartCooldown(scope, email, ResendCooldown, UtcNow))
            throw new InvalidOperationException("A code was just sent. Wait a minute before requesting another.");
    }

    // The cooldown is claimed before sending (so two clicks cannot send twice) and
    // released again if the send fails, so a failed send never strands the customer.
    private async Task SendWithCooldownAsync(string scope, string email, string subject, string body)
    {
        StartCooldown(scope, email);
        try { await SendAsync(email, subject, body); }
        catch { store.ClearCooldown(scope, email); throw; }
    }

    public bool CanSendRegistrationCode => !string.IsNullOrWhiteSpace(config["Email:BrevoApiKey"])
        || (env?.IsDevelopment() == true && !string.IsNullOrWhiteSpace(config["Email:SmtpHost"]));

    public async Task RequestRegistrationCodeAsync(string email, PendingRegistration pending)
    {
        if (!CanSendRegistrationCode)
            throw new InvalidOperationException("Email verification is not configured in this environment. New customer registration is unavailable; no email was sent.");
        // Only a hash of the password is kept while the email code is pending.
        var held = pending.PasswordHash != null ? pending with { Password = "" }
            : pending with { Password = "", PasswordHash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), pending.Password) };
        var code = NewCode();
        await SendWithCooldownAsync(Registration, email, "Your Glam by Mariya Verification Code",
            $"Your Glam by Mariya verification code is {code}. It expires in 15 minutes. If you did not request this, please ignore this email.");
        // Only create a verifiable pending account after the provider accepted
        // the email. Failed sends are retryable and never strand the user.
        store.Save(Registration, email, Hash(code), UtcNow.Add(CodeLifetime), JsonSerializer.Serialize(held));
    }

    public bool HasPendingRegistration(string email) =>
        store.Peek(Registration, email, UtcNow) != null || cache.TryGetValue(HoldKey(email), out _);

    // Supabase-only path (removed in a later step): holds the profile in process memory.
    private static string HoldKey(string email) => $"emailcode:pending:{email.Trim().ToLowerInvariant()}";
    private static readonly object HoldGate = new();

    /// <summary>Hold a verified-elsewhere (Supabase) registration profile without a local code.</summary>
    public void HoldRegistration(string email, PendingRegistration pending) =>
        cache.Set(HoldKey(email), pending, TimeSpan.FromMinutes(30));

    public PendingRegistration? TakeRegistration(string email)
    {
        lock (HoldGate)
        {
            var pending = cache.Get<PendingRegistration>(HoldKey(email));
            if (pending != null) cache.Remove(HoldKey(email));
            return pending;
        }
    }

    public async Task ResendRegistrationCodeAsync(string email)
    {
        if (!CanSendRegistrationCode)
            throw new InvalidOperationException("Email verification is not configured in this environment. New customer registration is unavailable; no email was sent.");
        var payload = store.Peek(Registration, email, UtcNow);
        var pending = string.IsNullOrEmpty(payload) ? null : JsonSerializer.Deserialize<PendingRegistration>(payload);
        if (pending == null) throw new InvalidOperationException("Registration expired. Please register again.");
        await RequestRegistrationCodeAsync(email, pending);
    }

    public PendingRegistration ConfirmRegistrationCode(string email, string code)
    {
        var (result, payload) = store.Consume(Registration, email, Hash(code.Trim()), MaxAttempts, UtcNow);
        return result switch
        {
            EmailCodeCheck.Ok => (string.IsNullOrEmpty(payload) ? null : JsonSerializer.Deserialize<PendingRegistration>(payload))
                ?? throw new InvalidOperationException("Registration expired. Please register again."),
            EmailCodeCheck.Missing => throw new InvalidOperationException("No active code for this email. Request a new one."),
            EmailCodeCheck.Expired => throw new InvalidOperationException("That code expired. Request a new one."),
            EmailCodeCheck.Locked => throw new InvalidOperationException("Too many wrong codes. Request a new one."),
            _ => throw new InvalidOperationException("That code is incorrect.")
        };
    }

    public async Task RequestPasswordResetCodeAsync(string email)
    {
        var code = NewCode();
        await SendWithCooldownAsync(Reset, email, "Your Glam by Mariya Password Reset Code",
            $"Your Glam by Mariya password reset code is {code}. It expires in 15 minutes. If you did not request this, please ignore this email.");
        store.Save(Reset, email, Hash(code), UtcNow.Add(CodeLifetime), null);
    }

    public void ConfirmPasswordResetCode(string email, string code)
    {
        var (result, _) = store.Consume(Reset, email, Hash(code.Trim()), MaxAttempts, UtcNow);
        switch (result)
        {
            case EmailCodeCheck.Ok:
                store.Save(ResetVerified, email, "", UtcNow.AddMinutes(10), "");
                return;
            case EmailCodeCheck.Missing: throw new InvalidOperationException("No active reset code. Request a new one.");
            case EmailCodeCheck.Expired: throw new InvalidOperationException("That code expired. Request a new one.");
            case EmailCodeCheck.Locked: throw new InvalidOperationException("Too many wrong codes. Request a new one.");
            default: throw new InvalidOperationException("That code is incorrect.");
        }
    }

    public void RequireResetVerified(string email)
    {
        if (store.Peek(ResetVerified, email, UtcNow) == null)
            throw new InvalidOperationException("Verify the email code first.");
    }

    public void ClearResetVerified(string email) => store.Remove(ResetVerified, email);

    /// <summary>Send a non-code email (administrator invitations).</summary>
    public Task SendEmailAsync(string email, string subject, string body) => SendAsync(email, subject, body);

    private async Task SendAsync(string email, string subject, string body)
    {
        if (config.GetValue("Email:DeliveryDisabled", false))
            throw new InvalidOperationException("Email delivery is disabled for this environment.");
        var from = config["Email:From"] ?? "noreply@localhost";
        if (!string.IsNullOrWhiteSpace(config["Email:BrevoApiKey"]))
        {
            await brevo.SendAsync(email, subject, body);
            return;
        }

        // SMTP is retained only as a Development fallback. Production must use
        // Brevo's HTTPS API; Render's SMTP networking is not relied upon.
        if (env.IsDevelopment() && !string.IsNullOrWhiteSpace(config["Email:SmtpHost"]))
        {
            var host = config["Email:SmtpHost"]!;
            var port = config.GetValue("Email:SmtpPort", 587);
            var user = config["Email:SmtpUser"] ?? "";
            var pass = config["Email:SmtpPassword"] ?? "";
            using var client = new SmtpClient(host, port) { EnableSsl = true, Timeout = 12000 };
            if (!string.IsNullOrWhiteSpace(user)) client.Credentials = new System.Net.NetworkCredential(user, pass);
            using var msg = new MailMessage(from, email.Trim(), subject, body);
            try { await client.SendMailAsync(msg); }
            catch (Exception e) when (e is SmtpException or OperationCanceledException)
            { throw new InvalidOperationException("We could not send the email through the development SMTP fallback."); }
            return;
        }

        if (env.IsDevelopment())
        {
            log.LogInformation("Email outbox write for {EmailHash} (development only).", EmailHash(email));
            var dir = Path.Combine(env.ContentRootPath, "App_Data", "EmailCodes");
            Directory.CreateDirectory(dir);
            var safe = string.Concat(email.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            await File.WriteAllTextAsync(Path.Combine(dir, safe + ".txt"),
                $"{subject}\n{body}\n{DateTime.UtcNow:u}\n");
            return;
        }

        throw new InvalidOperationException("Transactional email is not configured. Set Email__BrevoApiKey in the hosting environment.");
    }

    private static string EmailHash(string email) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())))[..12];
}
