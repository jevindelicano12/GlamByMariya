using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using GlamByMariya.Infrastructure.Services;

namespace GlamByMariya.Infrastructure.Data;

public enum PostgresSecurityReadFailure { UnsupportedSchema, MetadataUnavailable, InvalidResult, RepositoryUnavailable }

public sealed class PostgresSecurityReadException(PostgresSecurityReadFailure failure)
    : Exception("PostgreSQL account security read denied.")
{
    public PostgresSecurityReadFailure Failure { get; } = failure;
}

// Query seam for offline tests. Implementations must execute the exact supplied
// statement with a UUID parameter in a consistent, read-only database snapshot.
public interface IPostgresAccountSecurityQuery
{
    Task<string> ExecuteAsync(string sql, Guid accountId, CancellationToken cancellationToken);
}

// Inactive: no DI registration, no current authentication callers. No fallback
// to mvc_users (which hides missing identities and does not expose sync state).
public sealed class PostgresAccountSecurityReadSource : IAccountSecurityReadSource
{
    private readonly IPostgresAccountSecurityQuery query;

    public PostgresAccountSecurityReadSource(IPostgresAccountSecurityQuery query, string schema)
    {
        this.query = query ?? throw new ArgumentNullException(nameof(query));
        if (schema != "public") throw new PostgresSecurityReadException(PostgresSecurityReadFailure.UnsupportedSchema);
    }

    public async Task<AccountSecurityRead> ReadAsync(Guid accountId, CancellationToken cancellationToken)
        => Parse(await ExecuteAsync(Sql, accountId, cancellationToken), accountId);

    internal async Task<(AccountSecurityRead Read, long Generation)> ReadVersionedAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var json = await ExecuteAsync(VersionedSql, accountId, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(json);
            var generations = document.RootElement.GetProperty("generations");
            if (generations.GetArrayLength() != 1 || !generations[0].TryGetInt64(out var generation) || generation <= 0)
                throw new PostgresSecurityReadException(PostgresSecurityReadFailure.InvalidResult);
            return (Parse(json, accountId), generation);
        }
        catch (PostgresSecurityReadException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        {
            throw new PostgresSecurityReadException(PostgresSecurityReadFailure.InvalidResult);
        }
    }

    private async Task<string> ExecuteAsync(string sql, Guid accountId, CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty) throw new AccountSecurityReadException(AccountSecurityReadFailure.InvalidRequest);
        string json;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            json = await query.ExecuteAsync(sql, accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (PostgresSecurityReadException) { throw; }
        catch (Exception)
        {
            throw new PostgresSecurityReadException(PostgresSecurityReadFailure.RepositoryUnavailable);
        }
        return json;
    }

    private static AccountSecurityRead Parse(string json, Guid accountId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.GetProperty("metadata_available").GetBoolean())
                throw new PostgresSecurityReadException(PostgresSecurityReadFailure.MetadataUnavailable);
            // Missing properties/types are malformed results, never empty success.
            var accounts = root.GetProperty("accounts").EnumerateArray().Select(row =>
            {
                var hash = Text(row, "password_hash");
                return new AccountSecurityAccountRow(row.GetProperty("id").GetGuid(), Boolean(row, "active"),
                    Boolean(row, "locked"), Text(row, "role"), Kind(Text(row, "kind")),
                    string.IsNullOrWhiteSpace(hash) ? null : AccountSession.Version(hash));
            }).ToImmutableArray();
            var identities = root.GetProperty("identities").EnumerateArray().Select(row =>
                new AccountSecurityIdentityRow(row.GetProperty("id").GetGuid(), Uuid(row, "customer"), Uuid(row, "admin"),
                    Text(row, "auth_state"), Uuid(row, "subject"), Text(row, "state"), Integer(row, "epoch"))).ToImmutableArray();
            if (accounts.Any(row => row.AccountId != accountId))
                throw new PostgresSecurityReadException(PostgresSecurityReadFailure.InvalidResult);
            return new(accounts, identities, Boolean(root, "login_integrity"), Boolean(root, "subject_unique"));
        }
        catch (PostgresSecurityReadException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        {
            throw new PostgresSecurityReadException(PostgresSecurityReadFailure.InvalidResult);
        }
        catch (Exception)
        {
            throw new PostgresSecurityReadException(PostgresSecurityReadFailure.RepositoryUnavailable);
        }
    }

    private static string? Text(JsonElement row, string key) => row.GetProperty(key).ValueKind == JsonValueKind.Null ? null : row.GetProperty(key).GetString();
    private static bool? Boolean(JsonElement row, string key) => row.GetProperty(key).ValueKind == JsonValueKind.Null ? null : row.GetProperty(key).GetBoolean();
    private static Guid? Uuid(JsonElement row, string key) => row.GetProperty(key).ValueKind == JsonValueKind.Null ? null : row.GetProperty(key).GetGuid();
    private static long? Integer(JsonElement row, string key) => row.GetProperty(key).ValueKind == JsonValueKind.Null ? null : row.GetProperty(key).GetInt64();
    private static SessionIdentityOwnerKind Kind(string? kind) => kind switch
    {
        "Customer" => SessionIdentityOwnerKind.Customer,
        "Admin" => SessionIdentityOwnerKind.Admin,
        _ => SessionIdentityOwnerKind.Unknown
    };

    // One MVCC statement for accounts, owner-based candidates, global uniqueness,
    // server-side timestamptz lockout comparison and catalog capability check.
    // to_jsonb permits detection of not-yet-existing sync columns without referencing
    // those columns directly. Missing metadata causes an exception before projection.
    public const string Sql = """
        WITH all_accounts AS (
          SELECT c.id AS physical_id,c.app_id,'Customer'::text AS kind,'Customer'::text AS role,
            c.is_active,(c.locked_until IS NOT NULL AND c.locked_until > statement_timestamp()) AS locked,
            c.password_hash,lower(btrim(c.email)) AS login
          FROM public.customers c
          UNION ALL
          SELECT u.id,u.app_id,'Admin'::text,
            CASE WHEN u.role IN ('Admin','SuperAdmin') THEN u.role::text ELSE NULL END,
            u.is_active,(u.locked_until IS NOT NULL AND u.locked_until > statement_timestamp()),
            u.password_hash,lower(btrim(coalesce(nullif(btrim(u.email),''),u.username)))
          FROM public.admin_users u
        ), accounts AS (
          SELECT * FROM all_accounts WHERE app_id=@account_id
        ), candidates AS (
          SELECT i.*,c.app_id AS customer_uuid,u.app_id AS admin_uuid
          FROM public.account_identities i
          LEFT JOIN public.customers c ON c.id=i.customer_id
          LEFT JOIN public.admin_users u ON u.id=i.admin_id
          WHERE i.id=@account_id
             OR i.customer_id IN (SELECT physical_id FROM accounts WHERE kind='Customer')
             OR i.admin_id IN (SELECT physical_id FROM accounts WHERE kind='Admin')
        ), capabilities AS (
          SELECT count(*)=4 AS available FROM information_schema.columns
          WHERE table_schema='public' AND table_name='account_identities'
            AND ((column_name='credential_sync_state' AND data_type IN ('text','character varying'))
              OR (column_name='credential_epoch' AND data_type='bigint')
              OR (column_name='auth_state' AND data_type IN ('text','character varying'))
              OR (column_name='supabase_user_id' AND data_type='uuid'))
        )
        SELECT jsonb_build_object(
          'metadata_available',(SELECT available FROM capabilities),
          'accounts',coalesce((SELECT jsonb_agg(jsonb_build_object(
            'id',app_id,'kind',kind,'role',role,'active',is_active,'locked',locked,'password_hash',password_hash)) FROM accounts),'[]'::jsonb),
          'identities',coalesce((SELECT jsonb_agg(jsonb_build_object(
            'id',i.id,
            'customer',CASE WHEN i.customer_id IS NOT NULL THEN coalesce(i.customer_uuid,'00000000-0000-0000-0000-000000000000'::uuid) END,
            'admin',CASE WHEN i.admin_id IS NOT NULL THEN coalesce(i.admin_uuid,'00000000-0000-0000-0000-000000000000'::uuid) END,
            'auth_state',to_jsonb(i)->>'auth_state','subject',to_jsonb(i)->>'supabase_user_id',
            'state',to_jsonb(i)->>'credential_sync_state','epoch',to_jsonb(i)->'credential_epoch')) FROM candidates i),'[]'::jsonb),
          'login_integrity',coalesce((SELECT bool_and(
            i.login IS NOT NULL AND btrim(i.login)<>''
            AND (SELECT count(*) FROM all_accounts a WHERE a.login=lower(btrim(i.login)))=1
            AND EXISTS(SELECT 1 FROM accounts a WHERE a.app_id=i.id AND a.login=lower(btrim(i.login)))
            AND (SELECT count(*) FROM public.account_identities x WHERE lower(btrim(x.login))=lower(btrim(i.login)))=1
          ) FROM candidates i),false),
          'subject_unique',coalesce((SELECT bool_and(
            to_jsonb(i)->>'supabase_user_id' IS NULL OR
            (SELECT count(*) FROM public.account_identities x
              WHERE to_jsonb(x)->>'supabase_user_id'=to_jsonb(i)->>'supabase_user_id')=1
          ) FROM candidates i),false)
        )::text
        """;

    internal static readonly string VersionedSql = """
        SELECT (snapshot.value::jsonb || jsonb_build_object(
          'generations', coalesce((SELECT jsonb_agg(g.generation)
            FROM public.account_security_generations g WHERE g.account_id=@account_id), '[]'::jsonb)))::text
        FROM (
        """ + "\n" + Sql + "\n" + """
        ) AS snapshot(value)
        """;
}

// Uses the application's existing Npgsql-backed DbDataSource, supplied explicitly
// by a future caller. Does not construct connection strings or register anything.
// Offline tests do NOT instantiate this transport or open a connection.
public sealed class PostgresAccountSecurityQuery : IPostgresAccountSecurityQuery
{
    private readonly DbDataSource dataSource;

    public PostgresAccountSecurityQuery(DbDataSource dataSource)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        if (dataSource.GetType().Assembly.GetName().Name != "Npgsql")
            throw new PostgresSecurityReadException(PostgresSecurityReadFailure.UnsupportedSchema);
    }

    public async Task<string> ExecuteAsync(string sql, Guid accountId, CancellationToken cancellationToken)
    {
        // Prevent callers using this read-only transport to execute arbitrary SQL.
        if ((sql != PostgresAccountSecurityReadSource.Sql && sql != PostgresAccountSecurityReadSource.VersionedSql) || accountId == Guid.Empty)
            throw new PostgresSecurityReadException(PostgresSecurityReadFailure.InvalidResult);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 30;
            command.CommandText = "SET TRANSACTION READ ONLY";
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = sql;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "account_id";
            parameter.DbType = DbType.Guid;
            parameter.Value = accountId;
            command.Parameters.Add(parameter);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is not string json)
                throw new PostgresSecurityReadException(PostgresSecurityReadFailure.InvalidResult);
            await transaction.CommitAsync(cancellationToken);
            return json;
        }
        catch (PostgresSecurityReadException) { throw; }
        catch (Exception)
        {
            throw new PostgresSecurityReadException(PostgresSecurityReadFailure.RepositoryUnavailable);
        }
    }
}
