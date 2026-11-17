using System.Data;
using GlamByMariya.Infrastructure.Services;
using Npgsql;
using NpgsqlTypes;

namespace GlamByMariya.Infrastructure.Data;

// Candidate public schema only. No registration in normal startup.
public sealed class PostgresAdminRosterReadSessionFactory(NpgsqlDataSource source) : IAdminRosterReadSessionFactory
{
    public async Task<IAdminRosterReadSession> OpenAsync(CancellationToken ct)
    {
        var connection = await source.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            try
            {
                await using var command = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
                await command.ExecuteNonQueryAsync(ct);
                return new Session(connection, transaction);
            }
            catch { await transaction.DisposeAsync(); throw; }
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private sealed class Session(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAdminRosterReadSession
    {
        public Task<VersionedAccountSecurity?> LoadSecurityAsync(Guid accountId, CancellationToken ct) =>
            new PostgresVersionedAccountSecurityProvider(new SnapshotQuery(connection, transaction), "public").LoadAsync(accountId, ct);

        public async Task<IReadOnlyList<AdminRosterCandidate>> ReadTargetsAsync(CancellationToken ct)
        {
            var result = new List<AdminRosterCandidate>();
            var total = 0;
            await using var command = new NpgsqlCommand("""
                SELECT app_id,role,username,email FROM public.admin_users
                ORDER BY app_id LIMIT 101
                """, connection, transaction);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (++total > AdminRosterService.MaximumAccounts || reader.IsDBNull(0) || reader.IsDBNull(1))
                    throw new InvalidOperationException("Invalid roster.");
                var role = reader.GetString(1);
                if (role == "SuperAdmin") continue;
                if (role != "Admin") throw new InvalidOperationException("Invalid roster.");
                result.Add(new(reader.GetGuid(0), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            try { await transaction.DisposeAsync(); }
            finally { await connection.DisposeAsync(); }
        }
    }

    private sealed class SnapshotQuery(NpgsqlConnection connection, NpgsqlTransaction transaction) : IPostgresAccountSecurityQuery
    {
        public async Task<string> ExecuteAsync(string sql, Guid accountId, CancellationToken ct)
        {
            if (sql != PostgresAccountSecurityReadSource.VersionedSql || accountId == Guid.Empty)
                throw new InvalidOperationException("Invalid account security read.");
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("account_id", NpgsqlDbType.Uuid, accountId);
            return await command.ExecuteScalarAsync(ct) as string ?? throw new InvalidOperationException("Invalid account security read.");
        }
    }
}
