using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace GlamByMariya.Infrastructure.Services;

/// <summary>
/// Local 6-digit email verification codes. Used when Supabase Auth is not
/// configured, or as the pending-registration holder so no account exists
/// before the email code is verified. Codes are SHA-256 hashed at rest,
/// expire in 15 minutes, allow 5 attempts, and resends are rate-limited to 60s.
/// Registration requires configured email delivery; the development outbox
/// remains available only to the other existing local email workflows.
/// </summary>
public sealed class EmailVerificationService(
    IMemoryCache cache, IConfiguration config, ILogger<EmailVerificationService> log, IWebHostEnvironment env,
    BrevoEmailSender brevo)
{
    public sealed record PendingRegistration(
        string FirstName, string LastName, string Email, int Age, string Gender, string Address, string Password, Guid? ProviderSubject = null, string? Suffix = null, DateOnly? Birthday = null);

    private static string Key(string scope, string email) => $"emailcode:{scope}:{email.Trim().ToLowerInvariant()}";
    private sealed record CodeEntry(string CodeHash, DateTime ExpiresAt);
    private sealed record ResetEntry(string CodeHash, DateTime ExpiresAt);
    private static readonly object VerificationGate = new();
    private sealed record AccountCode(string Hash, DateTime Expires, int Attempts);

    public async Task SendAccountCodeAsync(Guid userId, string email)
    {
        var key = Key("account:" + userId, email);
        CheckResendCooldown(email);
        var code = NewCode();
        await SendAsync(email, "Your Glam by Mariya Verification Code",
            $"Your Glam by Mariya verification code is {code}. It expires in 15 minutes. If you did not request this, please ignore this email.");
        cache.Set(key, new AccountCode(Hash(code), DateTime.UtcNow.AddMinutes(15), 0), TimeSpan.FromMinutes(15));
        MarkResendCooldown(email);
    }

    public void ConfirmAccountCode(Guid userId, string email, string code)
    {
        lock (VerificationGate)
        {
            var key = Key("account:" + userId, email);
            var entry = cache.Get<AccountCode>(key);
            if (entry == null || entry.Expires <= DateTime.UtcNow || entry.Attempts >= 5)
                throw new InvalidOperationException("Code expired or attempt limit reached. Request a new code.");
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(entry.Hash), Convert.FromHexString(Hash((code ?? "").Trim()))))
            {
                cache.Set(key, entry with { Attempts = entry.Attempts + 1 }, entry.Expires);
                throw new InvalidOperationException("Incorrect code. Check your newest email.");
            }
            cache.Remove(key);
        }
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

    private void CheckResendCooldown(string email)
    {
        if (cache.TryGetValue(Key("cooldown", email), out _))
            throw new InvalidOperationException("A code was just sent. Wait a minute before requesting another.");
    }

    private void MarkResendCooldown(string email) => cache.Set(Key("cooldown", email), true, TimeSpan.FromSeconds(60));

    private CodeEntry GetEntry(string email)
    {
        if (!cache.TryGetValue<CodeEntry>(Key("code", email), out var entry) || entry == null)
            throw new InvalidOperationException("No active code for this email. Request a new one.");
        if (entry.ExpiresAt <= DateTime.UtcNow)
        {
            cache.Remove(Key("code", email));
            throw new InvalidOperationException("That code expired. Request a new one.");
        }
        return entry;
    }

    private void CheckAttempts(string email)
    {
        var k = Key("attempts", email);
        var attempts = cache.GetOrCreate(k, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15); return 0; });
        if (attempts >= 5) throw new InvalidOperationException("Too many wrong codes. Request a new one.");
        cache.Set(k, attempts + 1, TimeSpan.FromMinutes(15));
    }

    public bool CanSendRegistrationCode => !string.IsNullOrWhiteSpace(config["Email:BrevoApiKey"])
        || (env?.IsDevelopment() == true && !string.IsNullOrWhiteSpace(config["Email:SmtpHost"]));

    public async Task RequestRegistrationCodeAsync(string email, PendingRegistration pending)
    {
        if (!CanSendRegistrationCode)
            throw new InvalidOperationException("Email verification is not configured in this environment. New customer registration is unavailable; no email was sent.");
        CheckResendCooldown(email);
        var code = NewCode();
        await SendAsync(email, "Your Glam by Mariya Verification Code",
            $"Your Glam by Mariya verification code is {code}. It expires in 15 minutes. If you did not request this, please ignore this email.");
        // Only create a verifiable pending account after the provider accepted
        // the email. Failed sends are retryable and never strand the user.
        cache.Set(Key("code", email), new CodeEntry(Hash(code), DateTime.UtcNow.AddMinutes(15)), TimeSpan.FromMinutes(15));
        cache.Set(Key("pending", email), pending, TimeSpan.FromMinutes(15));
        cache.Remove(Key("attempts", email));
        MarkResendCooldown(email);
    }

    public bool HasPendingRegistration(string email) =>
        cache.TryGetValue(Key("pending", email), out _);

    /// <summary>Hold a verified-elsewhere (Supabase) registration profile without a local code.</summary>
    public void HoldRegistration(string email, PendingRegistration pending) =>
        cache.Set(Key("pending", email), pending, TimeSpan.FromMinutes(30));

    public PendingRegistration? TakeRegistration(string email)
    {
        lock (VerificationGate)
        {
            var pending = cache.Get<PendingRegistration>(Key("pending", email));
            if (pending != null)
            {
                cache.Remove(Key("pending", email));
                cache.Remove(Key("code", email));
                cache.Remove(Key("attempts", email));
            }
            return pending;
        }
    }

    public async Task ResendRegistrationCodeAsync(string email)
    {
        if (!CanSendRegistrationCode)
            throw new InvalidOperationException("Email verification is not configured in this environment. New customer registration is unavailable; no email was sent.");
        var pending = cache.Get<PendingRegistration>(Key("pending", email))
            ?? throw new InvalidOperationException("Registration expired. Please register again.");
        await RequestRegistrationCodeAsync(email, pending);
    }

    public PendingRegistration ConfirmRegistrationCode(string email, string code)
    {
        lock (VerificationGate)
        {
            var entry = GetEntry(email);
            if (cache.Get<int>(Key("attempts", email)) >= 5)
                throw new InvalidOperationException("Too many wrong codes. Request a new one.");
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(entry.CodeHash), Encoding.UTF8.GetBytes(Hash(code.Trim()))))
            {
                CheckAttempts(email);
                throw new InvalidOperationException("That code is incorrect.");
            }
            var pending = cache.Get<PendingRegistration>(Key("pending", email))
                ?? throw new InvalidOperationException("Registration expired. Please register again.");
            cache.Remove(Key("code", email));
            cache.Remove(Key("pending", email));
            cache.Remove(Key("attempts", email));
            return pending;
        }
    }

    public async Task RequestPasswordResetCodeAsync(string email)
    {
        CheckResendCooldown(email);
        var code = NewCode();
        await SendAsync(email, "Your Glam by Mariya Password Reset Code",
            $"Your Glam by Mariya password reset code is {code}. It expires in 15 minutes. If you did not request this, please ignore this email.");
        cache.Set(Key("reset", email), new ResetEntry(Hash(code), DateTime.UtcNow.AddMinutes(15)), TimeSpan.FromMinutes(15));
        cache.Remove(Key("attempts", email));
        MarkResendCooldown(email);
    }

    public void ConfirmPasswordResetCode(string email, string code)
    {
        lock (VerificationGate)
        {
            if (cache.Get<int>(Key("attempts", email)) >= 5)
                throw new InvalidOperationException("Too many wrong codes. Request a new one.");
            if (!cache.TryGetValue<ResetEntry>(Key("reset", email), out var entry) || entry == null)
                throw new InvalidOperationException("No active reset code. Request a new one.");
            if (entry.ExpiresAt <= DateTime.UtcNow)
                throw new InvalidOperationException("That code expired. Request a new one.");
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(entry.CodeHash), Encoding.UTF8.GetBytes(Hash(code.Trim()))))
            {
                CheckAttempts(email);
                throw new InvalidOperationException("That code is incorrect.");
            }
            cache.Remove(Key("reset", email));
            cache.Remove(Key("attempts", email));
            cache.Set(Key("reset-ok", email), true, TimeSpan.FromMinutes(10));
        }
    }

    public void RequireResetVerified(string email)
    {
        if (!cache.TryGetValue<bool>(Key("reset-ok", email), out var ok) || !ok)
            throw new InvalidOperationException("Verify the email code first.");
    }

    public void ClearResetVerified(string email) => cache.Remove(Key("reset-ok", email));

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
