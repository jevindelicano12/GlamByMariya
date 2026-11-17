using System.Security.Cryptography;
using System.Text;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace GlamByMariya.Infrastructure.Data;

// Inactive until the review-only schema and provider adapter are independently verified.
internal sealed class PostgresPersistentSignupRetryStore(NpgsqlDataSource source, IDataProtectionProvider protection) : IPersistentSignupRetryStore
{
    private readonly IDataProtector protector = protection.CreateProtector("Glam.DurableSignupAttempt.v1");

    internal static PersistentSignupRetry Create(NpgsqlDataSource source, IDataProtectionProvider protection, IPersistentSignupRetryProvider provider) =>
        new(new PostgresPersistentSignupRetryStore(source, protection), provider);

    private static byte[]? Hash(string secret)
    {
        try
        {
            var bytes = Convert.FromBase64String(secret);
            return bytes.Length == 32 ? SHA256.HashData(bytes) : null;
        }
        catch (Exception e) when (e is FormatException or ArgumentNullException) { return null; }
    }

    public async Task<SignupOperationClaim?> BeginAsync(Guid attemptId, string browserSecret, SignupOperation operation, CancellationToken ct)
    {
        var hash = Hash(browserSecret);
        if (attemptId == Guid.Empty || hash == null || operation is not (SignupOperation.Verify or SignupOperation.Resend)) return null;
        var operationId = Guid.NewGuid();
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE public.auth_mvc_signup_attempts
            SET state=CASE WHEN @verify THEN 'Verifying' ELSE 'Resending' END,
                operation_id=@operation,
                verification_attempts=verification_attempts + CASE WHEN @verify THEN 1 ELSE 0 END,
                next_resend_at=CASE WHEN @verify THEN next_resend_at ELSE now()+interval '60 seconds' END
            WHERE attempt_id=@id AND browser_secret_hash=@hash AND state='AwaitingOtp'
              AND provider_subject IS NOT NULL AND expires_at>now()
              AND (NOT @verify OR verification_attempts<5)
              AND (@verify OR next_resend_at<=now())
            RETURNING normalized_email,provider_subject
            """, connection);
        command.Parameters.AddWithValue("id", attemptId);
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("operation", operationId);
        command.Parameters.AddWithValue("verify", operation == SignupOperation.Verify);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(operationId, reader.GetString(0), reader.GetGuid(1));
    }

    public async Task<bool> ReleaseAsync(Guid attemptId, string browserSecret, Guid operationId, CancellationToken ct)
    {
        var hash = Hash(browserSecret);
        if (attemptId == Guid.Empty || operationId == Guid.Empty || hash == null) return false;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE public.auth_mvc_signup_attempts SET state='AwaitingOtp',operation_id=NULL
            WHERE attempt_id=@id AND browser_secret_hash=@hash AND operation_id=@operation
              AND state IN ('Verifying','Resending') AND expires_at>now()
            """, connection);
        command.Parameters.AddWithValue("id", attemptId);
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("operation", operationId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> CommitAsync(Guid attemptId, string browserSecret, SignupOperationClaim claim, CancellationToken ct)
    {
        var hash = Hash(browserSecret);
        if (attemptId == Guid.Empty || hash == null || claim.OperationId == Guid.Empty || claim.Subject == Guid.Empty ||
            string.IsNullOrWhiteSpace(claim.Email)) return false;
        byte[] profile;
        await using (var connection = await source.OpenConnectionAsync(ct))
        await using (var command = new NpgsqlCommand("""
            SELECT encrypted_profile FROM public.auth_mvc_signup_attempts
            WHERE attempt_id=@id AND browser_secret_hash=@hash AND operation_id=@operation
              AND state='Verifying' AND provider_subject=@subject AND normalized_email=lower(btrim(@email))
              AND expires_at>now()
            """, connection))
        {
            command.Parameters.AddWithValue("id", attemptId);
            command.Parameters.AddWithValue("hash", hash);
            command.Parameters.AddWithValue("operation", claim.OperationId);
            command.Parameters.AddWithValue("subject", claim.Subject);
            command.Parameters.AddWithValue("email", claim.Email);
            if (await command.ExecuteScalarAsync(ct) is not byte[] encrypted) return false;
            profile = encrypted;
        }
        var user = PostgresDurableCustomerRegistration.ReadProfile(Encoding.UTF8.GetString(protector.Unprotect(profile)), attemptId, claim.Email);
        if (user == null) return false;
        var fence = new PostgresDurableCustomerRegistration.OperationFence(claim.OperationId, hash, profile);
        try
        {
            return await PostgresDurableCustomerRegistration.Persist(source, user, claim.Subject, true, fence, ct) != null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A failed acknowledgement is not a rollback. Only positive, locked
            // evidence can establish the committed result; never repeat the write.
            try { return await ReconcileCommittedAsync(attemptId, browserSecret, claim, ct); }
            catch (Exception) { return false; }
        }
    }

    // A locked Consumed marker is positive evidence only; all other outcomes remain unknown.
    internal async Task<bool> ReconcileCommittedAsync(Guid attemptId, string browserSecret, SignupOperationClaim claim, CancellationToken ct)
    {
        var hash = Hash(browserSecret);
        if (attemptId == Guid.Empty || hash == null || claim.OperationId == Guid.Empty || claim.Subject == Guid.Empty ||
            string.IsNullOrWhiteSpace(claim.Email)) return false;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        byte[] profile;
        await using (var locked = new NpgsqlCommand("""
            SELECT encrypted_profile FROM public.auth_mvc_signup_attempts
            WHERE attempt_id=@id AND browser_secret_hash=@hash AND state='Consumed'
              AND consumed_operation_id=@operation AND provider_subject=@subject
              AND normalized_email=lower(btrim(@email))
            FOR UPDATE
            """, connection, tx) { CommandTimeout = 5 })
        {
            locked.Parameters.AddWithValue("id", attemptId);
            locked.Parameters.AddWithValue("hash", hash);
            locked.Parameters.AddWithValue("operation", claim.OperationId);
            locked.Parameters.AddWithValue("subject", claim.Subject);
            locked.Parameters.AddWithValue("email", claim.Email);
            if (await locked.ExecuteScalarAsync(ct) is not byte[] encrypted) return false;
            profile = encrypted;
        }
        var user = PostgresDurableCustomerRegistration.ReadProfile(Encoding.UTF8.GetString(protector.Unprotect(profile)), attemptId, claim.Email);
        if (user == null) return false;
        await using var verify = new NpgsqlCommand("""
            SELECT count(*)=1
              AND (SELECT count(*) FROM public.customers WHERE lower(btrim(email))=lower(btrim(@email)))=1
              AND (SELECT count(*) FROM public.admin_users WHERE lower(btrim(coalesce(nullif(btrim(email),''),username)))=lower(btrim(@email)))=0
              AND (SELECT count(*) FROM public.account_identities WHERE lower(btrim(login))=lower(btrim(@email)))=1
              AND (SELECT count(*) FROM public.account_identities WHERE supabase_user_id=@subject)=1
            FROM public.customers c
            JOIN public.account_identities i ON i.customer_id=c.id AND i.admin_id IS NULL
            JOIN public.account_security_generations g ON g.account_id=c.app_id
            WHERE c.app_id=@id AND lower(btrim(c.email))=lower(btrim(@email))
              AND c.is_active AND (c.locked_until IS NULL OR c.locked_until<=now()) AND c.password_hash=@password_hash
              AND i.id=@id AND lower(btrim(i.login))=lower(btrim(@email)) AND i.auth_state='Supabase'
              AND i.supabase_user_id=@subject AND i.credential_sync_state='Ready' AND i.credential_epoch=1 AND g.generation=1
            """, connection, tx) { CommandTimeout = 5 };
        verify.Parameters.AddWithValue("id", attemptId);
        verify.Parameters.AddWithValue("email", claim.Email);
        verify.Parameters.AddWithValue("subject", claim.Subject);
        verify.Parameters.AddWithValue("password_hash", user.PasswordHash);
        return (bool?)await verify.ExecuteScalarAsync(ct) == true;
    }
}
