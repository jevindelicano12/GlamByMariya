using Npgsql;
using NpgsqlTypes;

namespace GlamByMariya.Infrastructure.Data;

// Inactive transaction seam. The caller must authorize and lock the actor and
// target, perform any role/status change, and commit in the same transaction.
public interface IAccountSecurityGenerationCommand
{
    Task<long?> AdvanceAsync(Guid accountId, long expectedGeneration, CancellationToken ct);
}

public sealed class AccountSecurityGenerationRepository(IAccountSecurityGenerationCommand command)
{
    public async Task<bool> AdvanceInTransactionAsync(Guid accountId, long expectedGeneration, CancellationToken ct = default)
    {
        if (accountId == Guid.Empty || expectedGeneration <= 0 || expectedGeneration == long.MaxValue || ct.IsCancellationRequested)
            return false;
        var result = await command.AdvanceAsync(accountId, expectedGeneration, ct);
        ct.ThrowIfCancellationRequested();
        if (result == null) return false;
        if (result != expectedGeneration + 1) throw new InvalidOperationException("Security generation update returned an invalid result.");
        return true;
    }
}

// Requires an already-open caller-owned transaction; a returned value is not a commit.
public sealed class PostgresAccountSecurityGenerationCommand(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAccountSecurityGenerationCommand
{
    public const string Sql = """
        UPDATE public.account_security_generations
        SET generation = generation + 1
        WHERE account_id = @account_id AND generation = @expected AND generation < 9223372036854775807
        RETURNING generation
        """;

    public async Task<long?> AdvanceAsync(Guid accountId, long expectedGeneration, CancellationToken ct)
    {
        if (transaction.Connection != connection) throw new InvalidOperationException("A matching active transaction is required.");
        await using var cmd = new NpgsqlCommand(Sql, connection, transaction);
        cmd.Parameters.AddWithValue("account_id", NpgsqlDbType.Uuid, accountId);
        cmd.Parameters.AddWithValue("expected", NpgsqlDbType.Bigint, expectedGeneration);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : (long)value;
    }
}
