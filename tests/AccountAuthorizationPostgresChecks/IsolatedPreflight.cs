using Npgsql;

internal readonly record struct ColumnShape(string Table, string Name, string Type, bool Required, string Identity, string Generated);

internal static class IsolatedPreflight
{
    private static readonly ColumnShape[] Expected =
    [
        new("authz_sprint3_sentinel", "name", "text", true, "", ""),
        new("customers", "id", "bigint", true, "a", ""),
        new("customers", "app_id", "uuid", true, "", ""),
        new("customers", "email", "text", true, "", ""),
        new("customers", "is_active", "boolean", true, "", ""),
        new("customers", "locked_until", "timestamp with time zone", false, "", ""),
        new("customers", "password_hash", "text", true, "", ""),
        new("admin_users", "id", "bigint", true, "a", ""),
        new("admin_users", "app_id", "uuid", true, "", ""),
        new("admin_users", "username", "text", true, "", ""),
        new("admin_users", "email", "text", true, "", ""),
        new("admin_users", "role", "text", true, "", ""),
        new("admin_users", "is_active", "boolean", true, "", ""),
        new("admin_users", "locked_until", "timestamp with time zone", false, "", ""),
        new("admin_users", "password_hash", "text", true, "", ""),
        new("account_identities", "id", "uuid", true, "", ""),
        new("account_identities", "customer_id", "bigint", false, "", ""),
        new("account_identities", "admin_id", "bigint", false, "", ""),
        new("account_identities", "login", "text", true, "", ""),
        new("account_identities", "auth_state", "text", true, "", ""),
        new("account_identities", "supabase_user_id", "uuid", false, "", ""),
        new("account_identities", "credential_epoch", "bigint", true, "", ""),
        new("account_identities", "credential_sync_state", "text", true, "", ""),
        new("account_security_generations", "account_id", "uuid", true, "", ""),
        new("account_security_generations", "generation", "bigint", true, "", "")
    ];

    internal static IReadOnlyList<ColumnShape> ExpectedColumns => Array.AsReadOnly(Expected);
    internal static bool MatchesColumns(IEnumerable<ColumnShape> actual)
    {
        var columns = actual.ToArray();
        return columns.Length == Expected.Length && columns.ToHashSet().SetEquals(Expected);
    }
    internal static bool IsExclusive(bool leaseAcquired, long otherSessions) => leaseAcquired && otherSessions == 0;
    internal static bool IsFixtureEmpty(long rowCount) => rowCount == 0;

    // Holds a cooperative session-level lease; unrelated clients can ignore it.
    internal static async Task VerifyAsync(NpgsqlConnection connection, bool requireEmpty)
    {
        bool acquired;
        await using (var lease = new NpgsqlCommand("SELECT pg_try_advisory_lock(1196182833, 3)", connection))
            acquired = (bool?)await lease.ExecuteScalarAsync() == true;
        if (!acquired) throw new InvalidOperationException("Another cooperating test runner holds the lease.");

        await using (var sessions = new NpgsqlCommand("""
            SELECT count(*) FROM pg_catalog.pg_stat_activity
            WHERE datid=(SELECT oid FROM pg_catalog.pg_database WHERE datname=current_database())
              AND pid<>pg_backend_pid()
            """, connection))
            if (!IsExclusive(acquired, (long?)await sessions.ExecuteScalarAsync() ?? -1)) throw new InvalidOperationException("Other database sessions detected.");

        await using (var tables = new NpgsqlCommand("""
            SELECT count(*)=5 AND count(*) FILTER (WHERE c.relname IN
              ('authz_sprint3_sentinel','customers','admin_users','account_identities','account_security_generations')
              AND c.relkind='r' AND pg_get_userbyid(c.relowner)=current_user)=5
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','f')
            """, connection))
            if ((bool?)await tables.ExecuteScalarAsync() != true) throw new InvalidOperationException("Unexpected test tables or ownership.");

        var columns = new List<ColumnShape>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT c.relname, a.attname, pg_catalog.format_type(a.atttypid,a.atttypmod),
                   a.attnotnull, a.attidentity::text, a.attgenerated::text
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_catalog.pg_attribute a ON a.attrelid=c.oid
            WHERE n.nspname='public' AND c.relkind='r' AND a.attnum>0 AND NOT a.attisdropped
            """, connection))
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                columns.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetString(4), reader.GetString(5)));
        if (!MatchesColumns(columns)) throw new InvalidOperationException("Unexpected test-table columns.");

        await using (var constraints = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE contype='p')=5
               AND count(*) FILTER (WHERE contype='u')=4
               AND count(*) FILTER (WHERE contype='f')=2
               AND count(*) FILTER (WHERE contype='c')=8
            FROM pg_catalog.pg_constraint WHERE conrelid IN
              ('public.authz_sprint3_sentinel'::regclass,'public.customers'::regclass,'public.admin_users'::regclass,
               'public.account_identities'::regclass,'public.account_security_generations'::regclass)
            """, connection))
            if ((bool?)await constraints.ExecuteScalarAsync() != true) throw new InvalidOperationException("Unexpected test constraint counts.");

        await using (var sentinel = new NpgsqlCommand("SELECT count(*)=1 AND count(*) FILTER (WHERE name='synthetic-sprint3-only')=1 FROM public.authz_sprint3_sentinel", connection))
            if ((bool?)await sentinel.ExecuteScalarAsync() != true) throw new InvalidOperationException("Unexpected sentinel rows.");

        if (requireEmpty)
        {
            await using var empty = new NpgsqlCommand("""
                SELECT (SELECT count(*) FROM public.customers)+(SELECT count(*) FROM public.admin_users)+
                       (SELECT count(*) FROM public.account_identities)+(SELECT count(*) FROM public.account_security_generations)
                """, connection);
            if (!IsFixtureEmpty((long?)await empty.ExecuteScalarAsync() ?? -1)) throw new InvalidOperationException("Synthetic fixture is not empty.");
        }
    }
}
