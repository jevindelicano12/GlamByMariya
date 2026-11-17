using System.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace GlamByMariya.Infrastructure.Data;

// A persistent browser-bound, single-use attempt must supply
// these fields; an email or attempt id from a request is not sufficient.
internal sealed class PostgresDurableCustomerRegistration(NpgsqlDataSource source, SupabaseAuthService provider)
{
    internal async Task<AppUser?> RegisterVerifiedOtpAsync(Guid attemptId, string email, string code,
        string firstName, string lastName, int age, string gender, string address, string password,
        CancellationToken cancellationToken = default)
    {
        // This older preparation seam has no browser-bound protected profile.
        if (PreparedPersistentBooking.Enabled) return null;
        if (attemptId == Guid.Empty || string.IsNullOrWhiteSpace(code) || code.Trim().Length != 6 ||
            RegistrationRules.Validate(firstName, lastName, email, age, gender, address, password) != null) return null;

        // Provider proof is obtained here, never from the browser or a decoded JWT.
        var session = await provider.VerifyOtpAsync(email, code, "signup");
        if (session == null || string.IsNullOrWhiteSpace(session.AccessToken) || session.User.EmailConfirmedAt == null ||
            !Guid.TryParseExact(session.User.Id, "D", out var subject) || subject == Guid.Empty ||
            !string.Equals(session.User.Email.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase)) return null;

        var user = new AppUser { Id = attemptId, Email = email.Trim(), FirstName = firstName.Trim(), LastName = lastName.Trim(),
            Name = firstName.Trim() + " " + lastName.Trim(), Age = age, Gender = gender, Address = address.Trim(), Role = "Customer", IsActive = true };
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password);
        return await Persist(source, user, subject, false, null, cancellationToken);
    }

    internal async Task<(AppUser User, Guid Subject)?> CompleteClaimedAsync(Guid attemptId, string secret, string code, PostgresDurableSignupAttempts attempts, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length != 6 || code.Trim().Any(c => c is < '0' or > '9')) return null;
        var claim = await attempts.Claim(attemptId, secret, ct);
        if (claim == null) return null;
        var session = await provider.VerifyOtpAsync(claim.Value.Email, code, "signup");
        if (session == null || string.IsNullOrWhiteSpace(session.AccessToken) || session.User.EmailConfirmedAt == null ||
            !Guid.TryParseExact(session.User.Id, "D", out var subject) || subject != claim.Value.Subject ||
            !string.Equals(session.User.Email.Trim(), claim.Value.Email, StringComparison.OrdinalIgnoreCase)) return null;
        var user = ReadProfile(claim.Value.Profile, attemptId, claim.Value.Email);
        if (user == null) return null;
        var saved = await Persist(source, user, subject, true, null, ct);
        return saved == null ? null : (saved, subject);
    }

    internal static AppUser? ReadProfile(string profile, Guid attemptId, string email)
    {
        var user = System.Text.Json.JsonSerializer.Deserialize<AppUser>(profile);
        if (user == null || user.Role != "Customer" || !user.IsActive || string.IsNullOrEmpty(user.PasswordHash) ||
            !string.Equals(user.Email.Trim(), email, StringComparison.OrdinalIgnoreCase) ||
            RegistrationRules.Validate(user.FirstName, user.LastName, user.Email, user.Age, user.Gender, user.Address, "Synthetic123!", enforceLegacyAge: !PreparedPersistentBooking.Enabled && !user.Birthday.HasValue) != null) return null;
        if ((PreparedPersistentBooking.Enabled || user.Birthday.HasValue) && (!user.Birthday.HasValue || user.Birthday.Value > DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)) ||
            user.Suffix is not (null or "" or "Jr." or "Sr." or "II" or "III" or "IV"))) return null;
        if (PreparedPersistentBooking.Enabled || user.Birthday.HasValue)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
            var age = today.Year - user.Birthday!.Value.Year;
            if (user.Birthday.Value.AddYears(age) > today) age--;
            user.Age = age;
        }
        user.Id = attemptId;
        return user;
    }

    internal sealed record OperationFence(Guid OperationId, byte[] BrowserHash, byte[] EncryptedProfile);

    internal static async Task<AppUser?> Persist(NpgsqlDataSource source, AppUser user, Guid subject, bool consumeAttempt, OperationFence? fence, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (consumeAttempt)
        {
            await using var consume = new NpgsqlCommand(fence == null ? """
                UPDATE public.auth_mvc_signup_attempts SET state='Consumed'
                WHERE attempt_id=@id AND state='Unconfirmed' AND provider_subject=@subject
                  AND normalized_email=lower(btrim(@email)) AND expires_at>now()
                """ : """
                UPDATE public.auth_mvc_signup_attempts SET state='Consumed', consumed_operation_id=@operation
                WHERE attempt_id=@id AND state='Verifying' AND operation_id=@operation
                  AND browser_secret_hash=@hash AND encrypted_profile=@profile
                  AND provider_subject=@subject AND normalized_email=lower(btrim(@email))
                  AND expires_at>now()
                """, connection, tx);
            consume.Parameters.AddWithValue("id", user.Id); consume.Parameters.AddWithValue("subject", subject); consume.Parameters.AddWithValue("email", user.Email);
            if (fence != null)
            {
                consume.Parameters.AddWithValue("operation", fence.OperationId);
                consume.Parameters.AddWithValue("hash", fence.BrowserHash);
                consume.Parameters.AddWithValue("profile", fence.EncryptedProfile);
            }
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1) return null;
        }
        await using (var conflict = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM public.customers WHERE lower(btrim(email))=lower(btrim(@email)))
                OR EXISTS(SELECT 1 FROM public.admin_users WHERE lower(btrim(coalesce(nullif(btrim(email),''),username)))=lower(btrim(@email)))
                OR EXISTS(SELECT 1 FROM public.account_identities WHERE lower(btrim(login))=lower(btrim(@email)) OR supabase_user_id=@subject OR id=@id)
            """, connection, tx))
        {
            conflict.Parameters.AddWithValue("email", user.Email); conflict.Parameters.AddWithValue("subject", subject); conflict.Parameters.AddWithValue("id", user.Id);
            if ((bool?)await conflict.ExecuteScalarAsync(cancellationToken) != false) return null;
        }
        await using (var insert = new NpgsqlCommand(PreparedPersistentBooking.Enabled || user.Birthday.HasValue ? """
            INSERT INTO public.customers(app_id,email,first_name,last_name,full_name,age_at_registration,gender,address,password_hash,is_active,suffix,birthday)
            VALUES(@id,@email,@first,@last,@name,@age,@gender,@address,@hash,true,@suffix,@birthday) RETURNING id
            """ : """
            INSERT INTO public.customers(app_id,email,first_name,last_name,full_name,age_at_registration,gender,address,password_hash,is_active)
            VALUES(@id,@email,@first,@last,@name,@age,@gender,@address,@hash,true) RETURNING id
            """, connection, tx))
        {
            insert.Parameters.AddWithValue("id", user.Id); insert.Parameters.AddWithValue("email", user.Email);
            insert.Parameters.AddWithValue("first", user.FirstName); insert.Parameters.AddWithValue("last", user.LastName);
            insert.Parameters.AddWithValue("name", user.Name); insert.Parameters.AddWithValue("age", user.Age);
            insert.Parameters.AddWithValue("gender", user.Gender); insert.Parameters.AddWithValue("address", user.Address); insert.Parameters.AddWithValue("hash", user.PasswordHash);
            if (PreparedPersistentBooking.Enabled || user.Birthday.HasValue)
            {
                insert.Parameters.AddWithValue("suffix", NpgsqlTypes.NpgsqlDbType.Varchar, (object?)user.Suffix ?? DBNull.Value);
                insert.Parameters.AddWithValue("birthday", user.Birthday ?? throw new InvalidOperationException("A birthday is required."));
            }
            if (await insert.ExecuteScalarAsync(cancellationToken) is not long ownerId) throw new InvalidOperationException("Customer creation could not be confirmed.");
            await using var link = new NpgsqlCommand("""
                UPDATE public.account_identities SET auth_state='Supabase',supabase_user_id=@subject,credential_sync_state='Ready'
                WHERE id=@id AND customer_id=@owner AND admin_id IS NULL AND auth_state='Unknown'
                  AND supabase_user_id IS NULL AND credential_sync_state='Unknown' AND credential_epoch=1
                  AND lower(btrim(login))=lower(btrim(@email))
                """, connection, tx);
            link.Parameters.AddWithValue("id", user.Id); link.Parameters.AddWithValue("owner", ownerId);
            link.Parameters.AddWithValue("subject", subject); link.Parameters.AddWithValue("email", user.Email);
            if (await link.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Customer identity initialization could not be confirmed.");
        }
        if (consumeAttempt)
        {
            // A retained generation must never be reset or reused for a new account.
            await using var generation = new NpgsqlCommand(
                "INSERT INTO public.account_security_generations(account_id,generation) VALUES(@id,1)", connection, tx);
            generation.Parameters.AddWithValue("id", user.Id);
            if (await generation.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Customer security generation initialization could not be confirmed.");
        }
        await tx.CommitAsync(cancellationToken);
        return user;
    }
}
