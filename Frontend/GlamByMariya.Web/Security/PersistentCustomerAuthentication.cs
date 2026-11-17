using System.Security.Cryptography;
using System.Text.Json;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace GlamByMariya.Web.Security;

// Normal activation stays closed; only the validated loopback integration profile
// or isolated fake-provider tests register the durable authentication path.
internal sealed class PersistentCustomerAuthentication(
    NpgsqlDataSource source, SupabaseAuthService provider, IDataProtectionProvider protection,
    VersionedSessionAuthorization authorization, TrustedRequestSessionAccessor sessions,
    IConfiguration configuration)
{
    internal sealed record RegistrationStart(string? BrowserCookie, bool AwaitingOtp);
    internal sealed record LoginTicket(AppUser User, AccountSessionEvidence Evidence, Guid Subject, long Generation);
    private IDataProtector CookieProtector => protection.CreateProtector("Glam.DurableSignupCookie.v1");

    internal async Task<bool> Ready(CancellationToken ct, bool throwOnFailure = false)
    {
        if (!configuration.GetValue("Authentication:DurableSessionEnforcement", false) ||
            configuration["Database:Schema"] != "public" || !provider.IsConfigured) return false;
        try
        {
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var command = new NpgsqlCommand(CustomerAuthReadinessSql.Query, connection);
            return await command.ExecuteScalarAsync(ct) is true;
        }
        catch (Exception) when (!throwOnFailure) { return false; }
    }

    private bool AllowedEmail(string? email) => CustomerAuthIntegrationProfile.AllowedEmail(configuration, email);

    private (Guid Id, string Secret)? ReadBrowserCookie(string? cookie)
    {
        try
        {
            var text = CookieProtector.Unprotect(cookie ?? "");
            var separator = text.IndexOf(':');
            if (separator < 0 || !Guid.TryParseExact(text[..separator], "D", out var id) || id == Guid.Empty) return null;
            var secret = text[(separator + 1)..];
            return Convert.FromBase64String(secret).Length == 32 ? (id, secret) : null;
        }
        catch (Exception) { return null; }
    }

    private async Task<string?> AttemptState(Guid id, string secret, string email, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT state FROM public.auth_mvc_signup_attempts
            WHERE attempt_id=@id AND browser_secret_hash=@hash
              AND normalized_email=lower(btrim(@email)) AND expires_at>now()
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("hash", SHA256.HashData(Convert.FromBase64String(secret)));
        command.Parameters.AddWithValue("email", email);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    internal async Task<RegistrationStart> RegisterAsync(string first, string last, string email,
        DateOnly? birthday, string? suffix, string gender, string address, string password,
        string? browserCookie, CancellationToken ct)
    {
        if (!AllowedEmail(email) || !await Ready(ct)) return new(null, false);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        if (!birthday.HasValue || birthday > today || suffix is not (null or "" or "Jr." or "Sr." or "II" or "III" or "IV"))
            throw new InvalidOperationException("Enter a valid birthday and suffix.");
        var age = today.Year - birthday.Value.Year;
        if (birthday.Value.AddYears(age) > today) age--;
        var error = RegistrationRules.Validate(first, last, email, age, gender, address, password, enforceLegacyAge: false);
        if (error != null || string.IsNullOrWhiteSpace(gender))
            throw new InvalidOperationException(error ?? "Select a gender.");
        if (ReadBrowserCookie(browserCookie) is { } existing)
        {
            var state = await AttemptState(existing.Id, existing.Secret, email, ct);
            // Preserve the original protected profile; a repeat POST cannot replace it.
            if (state != null) return new(browserCookie, state == "AwaitingOtp");
        }
        var user = new AppUser { FirstName=first.Trim(), LastName=last.Trim(), Name=first.Trim()+" "+last.Trim(),
            Email=email.Trim(), Age=age, Birthday=birthday, Suffix=suffix, Gender=gender, Address=address.Trim(),
            Role=Roles.Customer, IsActive=true };
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password);
        var attempts = new PostgresDurableSignupAttempts(source, protection);
        var entry = await attempts.Create(user.Email, JsonSerializer.Serialize(user), ct);
        var cookie = CookieProtector.Protect(entry.Id.ToString("D")+":"+entry.Secret);
        try
        {
            var created = await provider.SignUpAsync(user.Email, password, new { first_name=user.FirstName, last_name=user.LastName });
            if (!Guid.TryParseExact(created.Id, "D", out var subject) || subject == Guid.Empty ||
                !string.Equals(created.Email?.Trim(), user.Email, StringComparison.OrdinalIgnoreCase) ||
                !await attempts.MarkAwaitingOtp(entry.Id, subject, ct)) return new(cookie, false);
            return new(cookie, true);
        }
        catch (Exception) { return new(cookie, false); } // Keep Initiated; never repeat an uncertain signup.
    }

    internal async Task<SignupRetryResult> VerifyAsync(string? cookie, string email, string? code, bool resend, CancellationToken ct)
    {
        if (!AllowedEmail(email) || !await Ready(ct) || ReadBrowserCookie(cookie) is not { } attempt ||
            await AttemptState(attempt.Id, attempt.Secret, email, ct) != "AwaitingOtp") return SignupRetryResult.Unavailable;
        var retry = PostgresPersistentSignupRetryStore.Create(source, protection, new SupabaseSignupRetryProvider(provider));
        return resend ? await retry.ResendAsync(attempt.Id, attempt.Secret, ct)
            : await retry.VerifyAsync(attempt.Id, attempt.Secret, code ?? "", ct);
    }

    private async Task<AppUser?> BoundCustomer(Guid subject, string email, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT c.app_id,c.first_name,c.last_name,c.email,c.age_at_registration,c.gender,c.address,
                   c.password_hash,c.birthday,c.suffix
            FROM public.customers c JOIN public.account_identities i ON i.customer_id=c.id AND i.id=c.app_id
            WHERE i.admin_id IS NULL AND i.auth_state='Supabase' AND i.credential_sync_state='Ready'
              AND i.supabase_user_id=@subject AND lower(btrim(i.login))=lower(btrim(@email))
              AND lower(btrim(c.email))=lower(btrim(@email)) AND c.is_active
              AND (c.locked_until IS NULL OR c.locked_until<=statement_timestamp())
            """, connection);
        command.Parameters.AddWithValue("subject", subject); command.Parameters.AddWithValue("email", email);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var user = new AppUser { Id=reader.GetGuid(0), FirstName=reader.GetString(1), LastName=reader.GetString(2),
            Email=reader.GetString(3), Age=reader.GetInt16(4), Gender=reader.GetString(5), Address=reader.GetString(6),
            PasswordHash=reader.GetString(7), Birthday=reader.IsDBNull(8) ? null : reader.GetFieldValue<DateOnly>(8),
            Suffix=reader.IsDBNull(9) ? null : reader.GetString(9), Role=Roles.Customer, IsActive=true };
        user.Name=user.FirstName+" "+user.LastName;
        return await reader.ReadAsync(ct) ? null : user;
    }

    internal async Task<LoginTicket?> LoginAsync(string email, string password, CancellationToken ct)
    {
        if (!AllowedEmail(email) || !await Ready(ct)) return null;
        var response = await provider.SignInWithPasswordAsync(email, password);
        var verified = await provider.VerifySessionUserAsync(response, email, ct: ct);
        var subject = Guid.Parse(verified.Id);
        var user = await BoundCustomer(subject, verified.Email, ct);
        if (user == null) return null; // Never bind an existing provider identity by email during login.
        var ticket = await authorization.PrepareSupabaseLogin(user.Id, subject, Roles.Customer, ct);
        return ticket == null ? null : new(user, ticket.Value.Evidence, subject, ticket.Value.SecurityVersion);
    }

    internal async Task<bool> LogoutAsync(HttpContext context)
    {
        if (!await Ready(context.RequestAborted)) return false;
        var session = await sessions.ResolveAsync();
        if (session == null || !session.TryGetActor(context, out var actor) || actor?.Role != Roles.Customer) return false;
        return await new PostgresTrustedIdentityBoundary(source).RevokeTrustedSessionAsync(actor, context.RequestAborted) != null;
    }

    internal async Task<bool> RecoverAsync(string email, CancellationToken ct)
    {
        if (!AllowedEmail(email) || !await Ready(ct)) return false;
        await provider.SendRecoveryAsync(email);
        return true;
    }

    internal async Task<DurableCredentialResult> PasswordAsync(HttpContext context, string? email,
        string? currentPassword, string? code, string password, bool recovery)
    {
        var ct=context.RequestAborted;
        if (!await Ready(ct)) return DurableCredentialResult.NotStarted;
        TrustedActorSession? actor=null;
        if (!recovery)
        {
            var session=await sessions.ResolveAsync();
            if (session == null || !session.TryGetActor(context,out actor) || actor?.Role!=Roles.Customer)
                return DurableCredentialResult.NotStarted;
            // The email claim is not authority; resolve it from the locked-validated actor's mapping.
            await using var connection=await source.OpenConnectionAsync(ct);
            await using var lookup=new NpgsqlCommand("SELECT login FROM public.account_identities WHERE id=@id AND supabase_user_id=@subject AND admin_id IS NULL",connection);
            lookup.Parameters.AddWithValue("id",actor.AccountId); lookup.Parameters.AddWithValue("subject",actor.ProviderSubject);
            email=await lookup.ExecuteScalarAsync(ct) as string;
        }
        if (string.IsNullOrWhiteSpace(email) || !AllowedEmail(email)) return DurableCredentialResult.NotStarted;
        var response=recovery ? await provider.VerifyOtpAsync(email,code ?? "","recovery")
            : await provider.SignInWithPasswordAsync(email,currentPassword ?? "");
        if (response == null) return DurableCredentialResult.NotStarted;
        var verified=await provider.VerifySessionUserAsync(response,email,ct:ct);
        var subject=Guid.Parse(verified.Id);
        var user=await BoundCustomer(subject,verified.Email,ct);
        if (user == null || (actor!=null && (actor.AccountId!=user.Id || actor.ProviderSubject!=subject)))
            return DurableCredentialResult.NotStarted;
        if (new PasswordHasher<AppUser>().VerifyHashedPassword(user,user.PasswordHash,password) != PasswordVerificationResult.Failed)
            return DurableCredentialResult.NotStarted;
        var evidence=await authorization.PrepareSupabaseLogin(user.Id,subject,Roles.Customer,ct);
        if (evidence == null || !long.TryParse(evidence.Value.Evidence.CredentialEpoch,out var epoch))
            return DurableCredentialResult.NotStarted;
        var update=new DurableCredentialUpdate(new PostgresDurableCredentialStore(source,actor?.SecurityGeneration));
        return await update.Change(user.Id,subject,epoch,password,async token =>
        {
            await provider.UpdatePasswordVerifiedAsync(response.AccessToken,password,subject,verified.Email,token);
            return CredentialProviderResult.Confirmed;
        },ct);
    }
}

internal static class PersistentCustomerAuthenticationRegistration
{
    internal static IServiceCollection AddPersistentCustomerAuthentication(this IServiceCollection services)
    {
        services.AddScoped<PersistentCustomerAuthentication>();
        services.AddScoped<TrustedRequestSessionAccessor>();
        services.AddSingleton<IPostgresAccountSecurityQuery>(sp => new PostgresAccountSecurityQuery(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IAccountSecurityReadSource>(sp => new PostgresAccountSecurityReadSource(sp.GetRequiredService<IPostgresAccountSecurityQuery>(),"public"));
        services.AddSingleton<IAccountSecuritySnapshotProvider>(sp => new RepositoryAccountSecuritySnapshotProvider(
            sp.GetRequiredService<IAccountSecurityReadSource>(),SessionStoreKind.PublicPostgres,SessionAuthenticationKind.Supabase));
        services.AddSingleton<IVersionedAccountSecurityProvider>(sp => new PostgresVersionedAccountSecurityProvider(sp.GetRequiredService<IPostgresAccountSecurityQuery>(),"public"));
        services.AddSingleton<VersionedSessionAuthorization>();
        return services;
    }
}
