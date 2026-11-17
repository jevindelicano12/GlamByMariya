using GlamByMariya.Infrastructure.Services;
using Npgsql;
using System.Security.Cryptography;

internal static class ProofStagingChecks
{
    internal static async Task RunAsync(NpgsqlDataSource admin, NpgsqlDataSource backend,
        TrustedActorSession owner, TrustedActorSession other, long reservation, Action<bool, string> check)
    {
        var id = Guid.NewGuid();
        var digest = SHA256.HashData([1, 2, 3]);
        check(await Begin(backend, owner, reservation, 0, id, digest) == id, "staging intent bound to owner and current revision");
        check(await Begin(backend, owner, reservation, 0, id, digest) == id, "identical staging retry is idempotent");
        check(await Begin(backend, owner, reservation, 0, id, SHA256.HashData([4])) == null, "conflicting staging retry denied");
        check(await Begin(backend, other, reservation, 0, Guid.NewGuid(), digest) == null, "cross-Customer staging denied");
        check(await Begin(backend, owner, reservation, 1, Guid.NewGuid(), digest) == null, "stale staging revision denied");
        check(await Begin(backend, owner with { SecurityGeneration = owner.SecurityGeneration + 1 }, reservation, 0, Guid.NewGuid(), digest) == null,
            "wrong generation cannot create staging intent");
        check(await Begin(backend, owner with { ProviderSubject = Guid.NewGuid() }, reservation, 0, Guid.NewGuid(), digest) == null,
            "mismatched provider cannot create staging intent");
        var rolledBack = Guid.NewGuid();
        await using (var connection = await backend.OpenConnectionAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        {
            check(await Begin(connection, tx, owner, reservation, 0, rolledBack, digest) == rolledBack, "staging intent can be staged in transaction");
            await tx.RollbackAsync();
        }
        await using var adminConnection = await admin.OpenConnectionAsync();
        await using (var inspect = new NpgsqlCommand("""
            SELECT count(*),bool_and(state='pending'),bool_and(object_key='ProofStaging/'||replace(id::text,'-','')||extension)
            FROM public.fixture_proof_staging WHERE id=ANY(@ids)
            """, adminConnection))
        {
            inspect.Parameters.AddWithValue("ids", new[] { id, rolledBack });
            await using var reader = await inspect.ExecuteReaderAsync();
            await reader.ReadAsync();
            check(reader.GetInt64(0) == 1 && reader.GetBoolean(1) && reader.GetBoolean(2),
                "rollback removes only its intent; persisted key is generated and not eligible");
        }
        check(!await Quarantine(backend, other, reservation, id), "another Customer cannot quarantine staging intent");
        check(!await Quarantine(backend, owner with { SecurityGeneration = owner.SecurityGeneration + 1 }, reservation, id),
            "stale generation cannot quarantine staging intent");
        check(await Quarantine(backend, owner, reservation, id), "uncertain proof can be quarantined by its authorized owner");
        check(await Quarantine(backend, owner, reservation, id), "quarantine retry is idempotent");
        check(await Begin(backend, owner, reservation, 0, id, digest) == null, "quarantined intent is not accepted as pending on retry");
        await using (var state = new NpgsqlCommand("SELECT state FROM public.fixture_proof_staging WHERE id=@id", adminConnection))
        {
            state.Parameters.AddWithValue("id", id);
            check((string?)await state.ExecuteScalarAsync() == "reconciliation_required", "uncertain intent is durably quarantined");
        }
        try
        {
            await using var forbidden = new NpgsqlCommand("UPDATE public.fixture_proof_staging SET state='verified' WHERE id=@id", adminConnection);
            forbidden.Parameters.AddWithValue("id", id);
            await forbidden.ExecuteNonQueryAsync();
            throw new InvalidOperationException("Unapproved promotion succeeded.");
        }
        catch (PostgresException e) when (e.MessageText == "Unapproved proof staging transition")
        { check(true, "unapproved verification transition is blocked"); }
    }

    private static async Task<bool> Quarantine(NpgsqlDataSource source, TrustedActorSession actor, long reservation, Guid id)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT v2_authz_api.fixture_quarantine_proof_stage(
                @account,@subject,@role,@epoch,@generation,@reservation,@id)
            """, connection);
        command.Parameters.AddWithValue("account", actor.AccountId);
        command.Parameters.AddWithValue("subject", actor.ProviderSubject);
        command.Parameters.AddWithValue("role", actor.Role);
        command.Parameters.AddWithValue("epoch", actor.CredentialEpoch);
        command.Parameters.AddWithValue("generation", actor.SecurityGeneration);
        command.Parameters.AddWithValue("reservation", reservation);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync() is true;
    }
    private static async Task<Guid?> Begin(NpgsqlDataSource source, TrustedActorSession actor, long reservation, long revision, Guid id, byte[] digest)
    {
        await using var connection = await source.OpenConnectionAsync();
        return await Begin(connection, null, actor, reservation, revision, id, digest);
    }

    private static async Task<Guid?> Begin(NpgsqlConnection connection, NpgsqlTransaction? tx,
        TrustedActorSession actor, long reservation, long revision, Guid id, byte[] digest)
    {
        await using var command = new NpgsqlCommand("""
            SELECT v2_authz_api.fixture_begin_proof_stage(
                @account,@subject,@role,@epoch,@generation,@reservation,@revision,@id,@digest,'.png')
            """, connection, tx);
        command.Parameters.AddWithValue("account", actor.AccountId);
        command.Parameters.AddWithValue("subject", actor.ProviderSubject);
        command.Parameters.AddWithValue("role", actor.Role);
        command.Parameters.AddWithValue("epoch", actor.CredentialEpoch);
        command.Parameters.AddWithValue("generation", actor.SecurityGeneration);
        command.Parameters.AddWithValue("reservation", reservation);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("digest", digest);
        return await command.ExecuteScalarAsync() is Guid result ? result : null;
    }
}
