using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace GlamByMariya.Infrastructure.Data;

// Unwired: MVC and the registration writer must atomically finish the claimed
// attempt before this can authorize a ticket.
internal sealed class PostgresDurableSignupAttempts(NpgsqlDataSource source, IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("Glam.DurableSignupAttempt.v1");
    internal const string CreateSql = """
        INSERT INTO public.auth_mvc_signup_attempts(attempt_id,browser_secret_hash,normalized_email,encrypted_profile,state,expires_at)
        SELECT @id,@hash,lower(btrim(@email)),@profile,'Initiated',now()+interval '30 minutes'
        WHERE NOT EXISTS (
            SELECT 1 FROM public.auth_mvc_signup_attempts
            WHERE normalized_email=lower(btrim(@email))
              AND state IN ('Initiated','AwaitingOtp','Verifying','Resending','Unconfirmed'))
          AND NOT EXISTS (SELECT 1 FROM public.customers WHERE lower(btrim(email))=lower(btrim(@email)))
          AND NOT EXISTS (SELECT 1 FROM public.admin_users WHERE lower(btrim(coalesce(nullif(btrim(email),''),username)))=lower(btrim(@email)))
          AND NOT EXISTS (SELECT 1 FROM public.account_identities WHERE lower(btrim(login))=lower(btrim(@email)))
        RETURNING attempt_id
        """;

    internal async Task<(Guid Id, string Secret)> Create(string email, string profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(profile)) throw new ArgumentException("Invalid signup attempt.");
        var id = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        var encrypted = protector.Protect(System.Text.Encoding.UTF8.GetBytes(profile));
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await using (var guard = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(lower(btrim(@email)),0))", connection, transaction) { CommandTimeout = 5 })
        {
            guard.Parameters.AddWithValue("email", email);
            await guard.ExecuteNonQueryAsync(ct);
        }
        await using var command = new NpgsqlCommand(CreateSql, connection, transaction);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("hash", SHA256.HashData(secret));
        command.Parameters.AddWithValue("email", email); command.Parameters.AddWithValue("profile", encrypted);
        if (await command.ExecuteScalarAsync(ct) is not Guid inserted || inserted != id)
            throw new InvalidOperationException("Registration could not be started. Contact support.");
        await transaction.CommitAsync(ct);
        return (id, Convert.ToBase64String(secret));
    }

    // Call only after a validated signup response for this attempt, never from request claims.
    internal async Task<bool> MarkAwaitingOtp(Guid id, Guid subject, CancellationToken ct = default)
    {
        if (id == Guid.Empty || subject == Guid.Empty) return false;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE public.auth_mvc_signup_attempts SET state='AwaitingOtp',provider_subject=@subject,next_resend_at=now()+interval '60 seconds'
            WHERE attempt_id=@id AND state='Initiated' AND expires_at>now()
            """, connection);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("subject", subject);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    // Claim before making an OTP call. A crash or uncertain result leaves it
    // Unconfirmed rather than allowing replay or an automatic provider retry.
    internal async Task<(string Email, Guid Subject, string Profile)?> Claim(Guid id, string secretText, CancellationToken ct = default)
    {
        byte[] secret;
        try { secret = Convert.FromBase64String(secretText); }
        catch (FormatException) { return null; }
        if (id == Guid.Empty || secret.Length != 32) return null;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE public.auth_mvc_signup_attempts SET state='Unconfirmed'
            WHERE attempt_id=@id AND browser_secret_hash=@hash AND state='AwaitingOtp' AND expires_at>now()
            RETURNING normalized_email,provider_subject,encrypted_profile
            """, connection);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("hash", SHA256.HashData(secret));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetString(0), reader.GetGuid(1), System.Text.Encoding.UTF8.GetString(protector.Unprotect(reader.GetFieldValue<byte[]>(2))));
    }
}
