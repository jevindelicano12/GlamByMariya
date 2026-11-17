using System.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace GlamByMariya.Infrastructure.Data;

// Inactive: callers must establish fresh provider proof and authorization before
// Begin. An identity UUID is not itself proof of ownership.
public sealed class PostgresDurableCredentialStore(NpgsqlDataSource source, long? expectedGeneration = null) : IDurableCredentialStore
{
    public async Task<long?> Begin(Guid accountId, Guid providerSubject, long expectedEpoch, CancellationToken ct)
    {
        if (!Valid(accountId, providerSubject, expectedEpoch) || expectedEpoch == long.MaxValue) return null;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var owner = await LockOwner(connection, tx, accountId, providerSubject, expectedEpoch, "Ready", ct, expectedGeneration);
        if (owner == null) return null;
        await using var update = new NpgsqlCommand("UPDATE public.account_identities SET credential_epoch=credential_epoch+1, credential_sync_state='Pending' WHERE id=@id AND supabase_user_id=@subject AND credential_epoch=@epoch AND credential_sync_state='Ready'", connection, tx);
        Bind(update, accountId, providerSubject, expectedEpoch);
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Credential transition failed.");
        await tx.CommitAsync(ct);
        return expectedEpoch + 1;
    }

    public async Task<bool> Complete(Guid accountId, Guid providerSubject, long epoch, string newPassword, CancellationToken ct)
    {
        if (!Valid(accountId, providerSubject, epoch) || PasswordPolicy.ValidateNewPassword(newPassword) != null) return false;
        var hash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), newPassword);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var owner = await LockOwner(connection, tx, accountId, providerSubject, epoch, "Pending", ct);
        if (owner == null) return false;
        var table = owner == "Customer" ? "customers" : "admin_users";
        await using (var password = new NpgsqlCommand($"UPDATE public.{table} SET password_hash=@hash,security_stamp=@stamp WHERE app_id=@id", connection, tx))
        {
            password.Parameters.AddWithValue("hash", hash); password.Parameters.AddWithValue("stamp", Guid.NewGuid()); password.Parameters.AddWithValue("id", accountId);
            if (await password.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Credential synchronization failed.");
        }
        if (!await SetState(connection, tx, accountId, providerSubject, epoch, "Ready", ct)) throw new InvalidOperationException("Credential transition failed.");
        await tx.CommitAsync(ct); return true;
    }

    public Task<bool> Reject(Guid accountId, Guid providerSubject, long epoch, CancellationToken ct) => Transition(accountId, providerSubject, epoch, "Ready", ct);
    public Task<bool> MarkReconciliation(Guid accountId, Guid providerSubject, long epoch, CancellationToken ct) => Transition(accountId, providerSubject, epoch, "ReconciliationRequired", ct);

    // Preparation only: this fences local completion, not a delayed provider write.
    // No operator endpoint may call it until authorization and audit are approved.
    internal async Task<long?> FencePending(Guid accountId, Guid providerSubject, long epoch, CancellationToken ct)
    {
        if (!Valid(accountId, providerSubject, epoch) || epoch == long.MaxValue) return null;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (await LockOwner(connection, tx, accountId, providerSubject, epoch, "Pending", ct) == null) return null;
        await using var command = new NpgsqlCommand("""
            UPDATE public.account_identities SET credential_epoch=credential_epoch+1, credential_sync_state='ReconciliationRequired'
            WHERE id=@id AND auth_state='Supabase' AND supabase_user_id=@subject
              AND credential_epoch=@epoch AND credential_sync_state='Pending'
            """, connection, tx);
        Bind(command, accountId, providerSubject, epoch);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Credential fence could not be confirmed.");
        await tx.CommitAsync(ct);
        return epoch + 1;
    }

    private async Task<bool> Transition(Guid accountId, Guid subject, long epoch, string target, CancellationToken ct)
    {
        if (!Valid(accountId, subject, epoch)) return false;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (await LockOwner(connection, tx, accountId, subject, epoch, "Pending", ct) == null) return false;
        if (!await SetState(connection, tx, accountId, subject, epoch, target, ct)) throw new InvalidOperationException("Credential transition failed.");
        await tx.CommitAsync(ct); return true;
    }

    private static async Task<bool> SetState(NpgsqlConnection connection, NpgsqlTransaction tx, Guid id, Guid subject, long epoch, string state, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("UPDATE public.account_identities SET credential_sync_state=@state WHERE id=@id AND auth_state='Supabase' AND supabase_user_id=@subject AND credential_epoch=@epoch AND credential_sync_state='Pending'", connection, tx);
        Bind(command, id, subject, epoch); command.Parameters.AddWithValue("state", state);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task<string?> LockOwner(NpgsqlConnection connection, NpgsqlTransaction tx, Guid id, Guid subject, long epoch, string state, CancellationToken ct, long? generation = null)
    {
        // Same order as revocation: generation -> Customer -> staff -> identity.
        await using (var security = new NpgsqlCommand("SELECT generation FROM public.account_security_generations WHERE account_id=@id FOR UPDATE", connection, tx))
        {
            security.Parameters.AddWithValue("id", id);
            if (await security.ExecuteScalarAsync(ct) is not long current || current <= 0 ||
                (generation.HasValue && generation.Value != current)) return null;
        }
        foreach (var ownerTable in new[] { "customers", "admin_users" })
        {
            await using var ownerLock = new NpgsqlCommand($"SELECT id FROM public.{ownerTable} WHERE app_id=@id FOR UPDATE", connection, tx);
            ownerLock.Parameters.AddWithValue("id", id);
            await ownerLock.ExecuteScalarAsync(ct);
        }
        await using var identity = new NpgsqlCommand("SELECT customer_id,admin_id,login FROM public.account_identities WHERE id=@id AND auth_state='Supabase' AND supabase_user_id=@subject AND credential_epoch=@epoch AND credential_sync_state=@state FOR UPDATE", connection, tx);
        Bind(identity, id, subject, epoch); identity.Parameters.AddWithValue("state", state);
        long? customer; long? admin; string login;
        await using (var reader = await identity.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct) || reader.IsDBNull(2)) return null;
            customer = reader.IsDBNull(0) ? null : reader.GetInt64(0); admin = reader.IsDBNull(1) ? null : reader.GetInt64(1); login = reader.GetString(2);
        }
        if ((customer.HasValue == admin.HasValue) || string.IsNullOrWhiteSpace(login)) return null;
        var table = customer.HasValue ? "customers" : "admin_users";
        var loginField = customer.HasValue ? "email" : "coalesce(nullif(btrim(email),''),username)";
        var roleCheck = customer.HasValue ? "TRUE" : "role IN ('Admin','SuperAdmin')";
        await using (var owner = new NpgsqlCommand($"SELECT 1 FROM public.{table} WHERE id=@owner AND app_id=@id AND is_active AND (locked_until IS NULL OR locked_until<=statement_timestamp()) AND {roleCheck} AND lower(btrim({loginField}))=lower(btrim(@login)) FOR UPDATE", connection, tx))
        {
            owner.Parameters.AddWithValue("owner", customer ?? admin!.Value); owner.Parameters.AddWithValue("id", id); owner.Parameters.AddWithValue("login", login);
            if (await owner.ExecuteScalarAsync(ct) == null) return null;
        }
        await using var integrity = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM public.customers WHERE app_id=@id)+(SELECT count(*) FROM public.admin_users WHERE app_id=@id)=1
              AND (SELECT count(*) FROM public.account_identities WHERE lower(btrim(login))=lower(btrim(@login)))=1
              AND (SELECT count(*) FROM public.account_identities WHERE supabase_user_id=@subject)=1
              AND ((SELECT count(*) FROM public.customers WHERE lower(btrim(email))=lower(btrim(@login)))+
                   (SELECT count(*) FROM public.admin_users WHERE lower(btrim(coalesce(nullif(btrim(email),''),username)))=lower(btrim(@login))))=1
            """, connection, tx);
        integrity.Parameters.AddWithValue("id", id); integrity.Parameters.AddWithValue("subject", subject); integrity.Parameters.AddWithValue("login", login);
        return (bool?)await integrity.ExecuteScalarAsync(ct) == true ? (customer.HasValue ? "Customer" : "Admin") : null;
    }

    private static bool Valid(Guid id, Guid subject, long epoch) => id != Guid.Empty && subject != Guid.Empty && epoch > 0;
    private static void Bind(NpgsqlCommand command, Guid id, Guid subject, long epoch)
    { command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("subject", subject); command.Parameters.AddWithValue("epoch", epoch); }
}
