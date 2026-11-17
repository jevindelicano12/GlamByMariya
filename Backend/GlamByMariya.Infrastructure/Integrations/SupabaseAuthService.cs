using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GlamByMariya.Infrastructure.Integrations;

/// <summary>
/// Minimal Supabase Auth (GoTrue) client over REST. No external package.
/// Uses the anon public key only; never the service_role key.
/// Secrets come from Supabase:Url / Supabase:AnonKey (User Secrets or env).
/// </summary>
public sealed class SupabaseAuthService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<SupabaseAuthService> log)
{
    public string Url => (config["Supabase:Url"] ?? "").Trim().TrimEnd('/');
    public bool IsConfigured => Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(config["Supabase:AnonKey"]);

    private HttpClient Client()
    {
        var client = httpFactory.CreateClient("supabase-auth");
        client.BaseAddress = new Uri(Url + "/auth/v1/");
        client.Timeout = TimeSpan.FromSeconds(15);
        var key = config["Supabase:AnonKey"] ?? "";
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("apikey", key);
        // Publishable keys are API keys, not JWT bearer tokens. User calls below
        // supply the independently issued access token instead.
        if (!key.StartsWith("sb_publishable_", StringComparison.Ordinal))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    public sealed record SupabaseUser(string Id, string Email, DateTime? EmailConfirmedAt, DateTime? ConfirmationSentAt);
    public sealed record AuthSession(string AccessToken, string RefreshToken, SupabaseUser User);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? Dt(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), out var d) ? d : null;

    private static SupabaseUser ParseUser(JsonElement u) => new(
        Str(u, "id") ?? "", Str(u, "email") ?? "", Dt(u, "email_confirmed_at"), Dt(u, "confirmation_sent_at"));

    private static async Task<JsonDocument> Send(HttpClient client, string path, object body)
    {
        using var res = await client.PostAsync(path,
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        var text = await res.Content.ReadAsStringAsync();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text); }
        catch { doc = JsonDocument.Parse("{}"); }
        if (res.IsSuccessStatusCode) return doc;
        using (doc)
        {
            var root = doc.RootElement;
            var msg = Str(root, "msg") ?? Str(root, "error_description") ?? Str(root, "error") ?? $"HTTP {(int)res.StatusCode}";
            throw res.StatusCode switch
            {
                System.Net.HttpStatusCode.BadRequest when ContainsAny(msg, "already registered", "already exists", "User already")
                    => new InvalidOperationException("This email is already registered."),
                System.Net.HttpStatusCode.UnprocessableEntity when ContainsAny(msg, "already registered", "already exists", "User already")
                    => new InvalidOperationException("This email is already registered."),
                System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.UnprocessableEntity
                    when msg.Contains("Invalid login credentials", StringComparison.OrdinalIgnoreCase)
                    => new InvalidOperationException("Email or password is incorrect."),
                System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.UnprocessableEntity
                    when ContainsAny(msg, "invalid token", "invalid otp", "invalid code", "otp")
                    => new InvalidOperationException("That code is incorrect or expired."),
                System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.UnprocessableEntity
                    when ContainsAny(msg, "email not confirmed", "email_not_confirmed", "confirm your email")
                    => new InvalidOperationException("Email confirmation is required."),
                System.Net.HttpStatusCode.TooManyRequests
                    => new InvalidOperationException("Too many attempts. Wait a minute before retrying."),
                _ => new InvalidOperationException("The authentication request could not be confirmed. Contact support before retrying."),
            };
        }
    }

    private static bool ContainsAny(string msg, params string[] parts) =>
        parts.Any(p => msg.Contains(p, StringComparison.OrdinalIgnoreCase));

    private void RequireConfigured()
    {
        if (!IsConfigured) throw new InvalidOperationException("Supabase Auth is not configured. Set Supabase:Url and Supabase:AnonKey (User Secrets or environment).");
    }

    public async Task<SupabaseUser> SignUpAsync(string email, string password, object? metadata = null)
    {
        RequireConfigured();
        using var client = Client();
        using var doc = await Send(client, "signup", new { email = email.Trim(), password, data = metadata ?? new { } });
        var root = doc.RootElement;
        // GoTrue returns the User object directly at the top level; some
        // versions/gateways wrap it as { "user": { ... } }. Accept both.
        var u = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("user", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object
            ? wrapped
            : root;
        if (u.ValueKind != JsonValueKind.Object || Str(u, "id") == null)
            throw new InvalidOperationException("Supabase signup returned an unexpected response.");
        log.LogInformation("Supabase signup request accepted for {EmailHash}.", Hash(email));
        return ParseUser(u);
    }

    /// <summary>Resend the signup confirmation (6-digit code when the email template uses OTP).</summary>
    public async Task ResendSignupAsync(string email)
    {
        RequireConfigured();
        using var client = Client();
        using var _ = await Send(client, "resend", new { type = "signup", email = email.Trim() });
        log.LogInformation("Supabase signup resend request accepted for {EmailHash}.", Hash(email));
    }

    /// <summary>Verify a 6-digit email OTP. Type is signup, recovery or email.</summary>
    public async Task<AuthSession?> VerifyOtpAsync(string email, string code, string type = "signup")
    {
        RequireConfigured();
        using var client = Client();
        using var doc = await Send(client, "verify", new { type, email = email.Trim(), token = code.Trim() });
        var root = doc.RootElement;
        var session = root.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.Object ? s : root;
        var token = Str(session, "access_token");
        JsonElement userEl = default;
        if (root.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object) userEl = u;
        else if (session.ValueKind == JsonValueKind.Object && session.TryGetProperty("user", out var su) && su.ValueKind == JsonValueKind.Object) userEl = su;
        if (token == null || userEl.ValueKind != JsonValueKind.Object) return null;
        return new AuthSession(token, Str(session, "refresh_token") ?? "", ParseUser(userEl));
    }

    public async Task<AuthSession> SignInWithPasswordAsync(string email, string password)
    {
        RequireConfigured();
        using var client = Client();
        using var doc = await Send(client, "token?grant_type=password", new { email = email.Trim(), password });
        var root = doc.RootElement;
        var token = Str(root, "access_token") ?? throw new InvalidOperationException("Email or password is incorrect.");
        if (!root.TryGetProperty("user", out var u) || u.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Supabase sign-in returned an unexpected response.");
        return new AuthSession(token, Str(root, "refresh_token") ?? "", ParseUser(u));
    }

    // Resolve the access token at the provider; never trust a decoded JWT or a
    // caller's principal as proof of the session's subject.
    public async Task<SupabaseUser> VerifySessionUserAsync(AuthSession session, string expectedEmail,
        bool requireConfirmed = true, CancellationToken ct = default)
    {
        RequireConfigured();
        if (string.IsNullOrWhiteSpace(session.AccessToken) ||
            !Guid.TryParseExact(session.User.Id, "D", out var expectedSubject) || expectedSubject == Guid.Empty ||
            string.IsNullOrWhiteSpace(expectedEmail) ||
            !string.Equals(session.User.Email?.Trim(), expectedEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The account identity could not be verified. Contact support.");
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, "user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("The account identity could not be verified. Contact support.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var fields = doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.EnumerateObject().ToArray() : [];
        if (new[] { "id", "email", "email_confirmed_at" }.Any(name =>
            fields.Count(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) != 1))
            throw new InvalidOperationException("The account identity could not be verified. Contact support.");
        var verified = ParseUser(doc.RootElement);
        if (!Guid.TryParseExact(verified.Id, "D", out var subject) || subject != expectedSubject ||
            !string.Equals(verified.Email?.Trim(), expectedEmail.Trim(), StringComparison.OrdinalIgnoreCase) ||
            (requireConfirmed && verified.EmailConfirmedAt == null))
            throw new InvalidOperationException("The account identity could not be verified. Contact support.");
        return verified;
    }

    public async Task SendRecoveryAsync(string email)
    {
        RequireConfigured();
        using var client = Client();
        using var _ = await Send(client, "recover", new { email = email.Trim() });
        log.LogInformation("Supabase recovery sent for {EmailHash}.", Hash(email));
    }

    /// <summary>
    /// Create an email-confirmed Auth user (invite accept / owner bootstrap).
    /// Needs the service_role key in Supabase:ServiceKey; server-side only, never
    /// frontend. Returns false when unconfigured or provider provisioning is rejected.
    /// </summary>
    public async Task<bool> CreateConfirmedUserAsync(string email, string password, object? metadata = null)
    {
        var serviceKey = config["Supabase:ServiceKey"];
        if (!IsConfigured || string.IsNullOrWhiteSpace(serviceKey)) return false;
        using var client = httpFactory.CreateClient("supabase-auth-admin");
        client.BaseAddress = new Uri(Url + "/auth/v1/");
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("apikey", serviceKey);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
        using var res = await client.PostAsync("admin/users",
            new StringContent(JsonSerializer.Serialize(new
            {
                email = email.Trim(),
                password,
                email_confirm = true,
                user_metadata = metadata ?? new { },
            }), Encoding.UTF8, "application/json"));
        if (res.IsSuccessStatusCode)
        {
            log.LogInformation("Supabase Auth user provisioned for {EmailHash}.", Hash(email));
            return true;
        }
        log.LogWarning("Supabase Auth provisioning failed; local account continues without it.");
        return false;
    }

    /// <summary>
    /// Structured, fail-closed variant for future callers. A successful HTTP
    /// status is insufficient: GoTrue's raw adminUserCreate handler serializes
    /// the created user directly (sendJSON(..., user)); the JavaScript SDK's
    /// { data: { user }, error } wrapper is not an HTTP response envelope. The
    /// response must contain one non-empty UUID and one matching email. This
    /// method neither links the identity nor activates a local account.
    /// </summary>
    public async Task<SupabaseProvisioningResult> CreateConfirmedUserWithResultAsync(
        string email, string password, object? metadata = null, CancellationToken cancellationToken = default)
    {
        var serviceKey = config["Supabase:ServiceKey"];
        if (!IsConfigured || string.IsNullOrWhiteSpace(serviceKey))
            return SupabaseProvisioningResult.Unconfirmed(
                SupabaseProvisioningOutcome.NotAttempted, SupabaseProvisioningFailure.MissingConfiguration);

        try
        {
            using var client = httpFactory.CreateClient("supabase-auth-admin");
            client.BaseAddress = new Uri(Url + "/auth/v1/");
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("apikey", serviceKey);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
            using var response = await client.PostAsync("admin/users",
                new StringContent(JsonSerializer.Serialize(new
                {
                    email = email.Trim(), password, email_confirm = true,
                    user_metadata = metadata ?? new { },
                }), Encoding.UTF8, "application/json"), cancellationToken);

            if (!response.IsSuccessStatusCode)
                return SupabaseProvisioningResult.Unconfirmed(
                    SupabaseProvisioningOutcome.Unconfirmed, SupabaseProvisioningFailure.HttpRejected);

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || root.EnumerateObject().Any(p => p.Name.Equals("user", StringComparison.OrdinalIgnoreCase)))
                    return SupabaseProvisioningResult.Unconfirmed(
                        SupabaseProvisioningOutcome.Unconfirmed, SupabaseProvisioningFailure.InvalidResponse);

                var properties = root.EnumerateObject().ToArray();
                var idProperties = properties.Where(p => p.Name.Equals("id", StringComparison.OrdinalIgnoreCase)).ToArray();
                var emailProperties = properties.Where(p => p.Name.Equals("email", StringComparison.OrdinalIgnoreCase)).ToArray();
                var idText = idProperties.Length == 1 && idProperties[0].Name == "id"
                    && idProperties[0].Value.ValueKind == JsonValueKind.String
                    ? idProperties[0].Value.GetString() : null;
                var returnedEmail = emailProperties.Length == 1 && emailProperties[0].Name == "email"
                    && emailProperties[0].Value.ValueKind == JsonValueKind.String
                    ? emailProperties[0].Value.GetString() : null;
                if (!Guid.TryParseExact(idText, "D", out var userId) || userId == Guid.Empty
                    || string.IsNullOrWhiteSpace(returnedEmail)
                    || !returnedEmail.Trim().Equals(email.Trim(), StringComparison.OrdinalIgnoreCase))
                    return SupabaseProvisioningResult.Unconfirmed(
                        SupabaseProvisioningOutcome.Unconfirmed, SupabaseProvisioningFailure.InvalidResponse);

                log.LogInformation("Supabase Auth user provisioned for {EmailHash}.", Hash(email));
                return SupabaseProvisioningResult.Confirmed(userId);
            }
            catch (JsonException)
            {
                return SupabaseProvisioningResult.Unconfirmed(
                    SupabaseProvisioningOutcome.Unconfirmed, SupabaseProvisioningFailure.InvalidResponse);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SupabaseProvisioningResult.Unconfirmed(
                SupabaseProvisioningOutcome.Canceled, SupabaseProvisioningFailure.Canceled);
        }
        catch (OperationCanceledException)
        {
            return SupabaseProvisioningResult.Unconfirmed(
                SupabaseProvisioningOutcome.TimedOut, SupabaseProvisioningFailure.Timeout);
        }
        catch (HttpRequestException)
        {
            return SupabaseProvisioningResult.Unconfirmed(
                SupabaseProvisioningOutcome.TransportUncertain, SupabaseProvisioningFailure.TransportFailure);
        }
        catch (IOException)
        {
            return SupabaseProvisioningResult.Unconfirmed(
                SupabaseProvisioningOutcome.TransportUncertain, SupabaseProvisioningFailure.TransportFailure);
        }
    }

    /// <summary>Set a new password using a recovery session token (from VerifyOtp type=recovery).</summary>
    public Task UpdatePasswordAsync(string accessToken, string newPassword) => UpdatePasswordCoreAsync(accessToken, newPassword, null, null, CancellationToken.None);

    public Task UpdatePasswordVerifiedAsync(string accessToken, string newPassword, Guid subject, string email, CancellationToken cancellationToken = default)
    {
        if (subject == Guid.Empty || string.IsNullOrWhiteSpace(email)) throw new InvalidOperationException("Verified identity is required.");
        return UpdatePasswordCoreAsync(accessToken, newPassword, subject, email, cancellationToken);
    }

    private async Task UpdatePasswordCoreAsync(string accessToken, string newPassword, Guid? subject, string? email, CancellationToken cancellationToken)
    {
        RequireConfigured();
        using var client = Client();
        using var req = new HttpRequestMessage(HttpMethod.Put, "user")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { password = newPassword }), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await client.SendAsync(req, cancellationToken);
        if ((int)res.StatusCode >= 500 || res.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
            throw new PasswordUpdateUncertainException();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException("Password update failed. Request a new code and try again.");
        if (subject == null) return;
        try
        {
            await using var stream = await res.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new PasswordUpdateUncertainException();
            var fields = root.EnumerateObject().ToArray();
            var ids = fields.Where(p => p.Name.Equals("id", StringComparison.OrdinalIgnoreCase)).ToArray();
            var emails = fields.Where(p => p.Name.Equals("email", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (ids.Length != 1 || ids[0].Name != "id" || ids[0].Value.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(ids[0].Value.GetString(), "D", out var returnedId) || returnedId != subject ||
                emails.Length != 1 || emails[0].Name != "email" || emails[0].Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(emails[0].Value.GetString()) || !string.Equals(emails[0].Value.GetString()!.Trim(), email!.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new PasswordUpdateUncertainException();
        }
        catch (JsonException) { throw new PasswordUpdateUncertainException(); }
    }

    private static string Hash(string email)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..12];
    }
}
