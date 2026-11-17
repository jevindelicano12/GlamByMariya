using System.Data;
using GlamByMariya.Infrastructure.Services;
using Npgsql;
using NpgsqlTypes;

namespace GlamByMariya.Infrastructure.Data;

// Inactive: no production registration. Database privileges and every competing
// account writer must enforce the same integrity and locking rules before use.
public sealed class PostgresAccountAuthorizationTransactionFactory(NpgsqlDataSource source) : IAccountAuthorizationTransactionFactory
{
    public async Task<IAccountAuthorizationTransaction> BeginAsync(CancellationToken ct)
    {
        var connection = await source.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            return new Transaction(connection, transaction);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private sealed class Transaction(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAccountAuthorizationTransaction
    {
        private Guid targetId;
        private bool locked;
        private bool advanced;
        private bool applied;
        private bool committed;
        private VersionedAccountSecurity? actorSnapshot;
        private VersionedAccountSecurity? targetSnapshot;

        public async Task<(VersionedAccountSecurity? Actor, VersionedAccountSecurity? Target)> LockAccountsAsync(Guid actor, Guid target, CancellationToken ct)
        {
            if (locked || actor == Guid.Empty || target == Guid.Empty || actor == target)
                throw new InvalidOperationException("Invalid account transaction.");
            var ids = new[] { actor, target };
            // The order of tables and IDs must also be used by other security writers.
            foreach (var sql in new[] {
                "SELECT account_id FROM public.account_security_generations WHERE account_id = ANY(@ids) ORDER BY account_id FOR UPDATE",
                "SELECT app_id FROM public.admin_users WHERE app_id = ANY(@ids) ORDER BY app_id FOR UPDATE",
                "SELECT id FROM public.account_identities WHERE id = ANY(@ids) ORDER BY id FOR UPDATE" })
            {
                await using var cmd = new NpgsqlCommand(sql, connection, transaction);
                cmd.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
                var count = 0;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) count++;
                if (count != 2) return (null, null);
            }
            var query = new TransactionQuery(connection, transaction);
            var source = new PostgresAccountSecurityReadSource(query, "public");
            var provider = new RepositoryAccountSecuritySnapshotProvider(source, SessionStoreKind.PublicPostgres, SessionAuthenticationKind.Supabase);
            var actorAccount = await provider.LoadAsync(actor, ct);
            var targetAccount = await provider.LoadAsync(target, ct);
            if (actorAccount == null || targetAccount == null) return (null, null);
            var actorGeneration = await Generation(actor, ct);
            var targetGeneration = await Generation(target, ct);
            if (actorGeneration <= 0 || targetGeneration <= 0) return (null, null);
            actorSnapshot = new(actorAccount, actorGeneration);
            targetSnapshot = new(targetAccount, targetGeneration);
            targetId = target;
            locked = true;
            return (actorSnapshot, targetSnapshot);
        }

        private async Task<long> Generation(Guid id, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand("SELECT generation FROM public.account_security_generations WHERE account_id=@id", connection, transaction);
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
            return await cmd.ExecuteScalarAsync(ct) is long value ? value : 0;
        }

        public async Task<long?> AdvanceAsync(Guid accountId, long expectedGeneration, CancellationToken ct)
        {
            if (!CanModify(accountId) || advanced || expectedGeneration != targetSnapshot!.SecurityVersion) return null;
            var result = await new PostgresAccountSecurityGenerationCommand(connection, transaction).AdvanceAsync(accountId, expectedGeneration, ct);
            advanced = result == expectedGeneration + 1;
            return result;
        }

        public async Task<bool> ApplyAsync(Guid target, AccountAuthorizationAction action, DateTimeOffset? lockUntil, CancellationToken ct)
        {
            if (!CanModify(target) || !advanced || applied) return false;
            var sql = action switch
            {
                AccountAuthorizationAction.Disable when lockUntil == null =>
                    "UPDATE public.admin_users SET is_active=false WHERE app_id=@id AND role='Admin' AND is_active=true",
                AccountAuthorizationAction.Reactivate when lockUntil == null =>
                    "UPDATE public.admin_users SET is_active=true WHERE app_id=@id AND role='Admin' AND is_active=false AND (locked_until IS NULL OR locked_until<=statement_timestamp())",
                AccountAuthorizationAction.Lock when lockUntil.HasValue =>
                    "UPDATE public.admin_users SET locked_until=@until WHERE app_id=@id AND role='Admin' AND is_active=true AND (locked_until IS NULL OR locked_until<=statement_timestamp()) AND @until>statement_timestamp()",
                AccountAuthorizationAction.RevokeSessions when lockUntil == null => null,
                _ => ""
            };
            if (sql == "") return false;
            if (sql == null) { applied = true; return true; }
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, target);
            if (lockUntil.HasValue) cmd.Parameters.AddWithValue("until", NpgsqlDbType.TimestampTz, lockUntil.Value.UtcDateTime);
            applied = await cmd.ExecuteNonQueryAsync(ct) == 1;
            return applied;
        }

        private bool CanModify(Guid id) => locked && !committed && id == targetId &&
            actorSnapshot?.Account is { Role: "SuperAdmin", IsActive: true, IsLocked: false } &&
            targetSnapshot?.Account.Role == "Admin";

        public async Task CommitAsync(CancellationToken ct)
        {
            if (!locked || !advanced || !applied || committed) throw new InvalidOperationException("Incomplete account transaction.");
            await transaction.CommitAsync(ct);
            committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            // Npgsql disposal rolls back an uncommitted transaction. A failed commit
            // acknowledgement remains unknown; disposal cannot prove rollback.
            try { await transaction.DisposeAsync(); }
            finally { await connection.DisposeAsync(); }
        }
    }

    private sealed class TransactionQuery(NpgsqlConnection connection, NpgsqlTransaction transaction) : IPostgresAccountSecurityQuery
    {
        public async Task<string> ExecuteAsync(string sql, Guid accountId, CancellationToken ct)
        {
            if (sql != PostgresAccountSecurityReadSource.Sql || accountId == Guid.Empty)
                throw new InvalidOperationException("Invalid account security read.");
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("account_id", NpgsqlDbType.Uuid, accountId);
            return await cmd.ExecuteScalarAsync(ct) as string ?? throw new InvalidOperationException("Account security read failed.");
        }
    }
}
