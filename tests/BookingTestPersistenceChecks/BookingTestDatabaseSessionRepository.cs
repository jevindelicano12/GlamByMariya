using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;

// Test-only transaction helper. Caller must first verify target/login/role and
// must not grant direct booking access that could bypass this session check.
// No migration, provisioning or connection is performed by this class.
internal static class BookingTestDatabaseSessionRepository
{
    // Trusted, separately authenticated issuer only. Caller supplies an already
    // open connection/transaction; this method never opens or migrates anything.
    internal static async Task IssueAsync(NpgsqlConnection c, NpgsqlTransaction tx, SyntheticBookingSessions.VerifiedCustomer customer, CancellationToken ct)
    {
        BookingTestProtectedBoundary.RequireImplemented();
        if (tx.Connection != c || customer is null) throw new InvalidOperationException("Invalid isolated issuer transaction.");
        await using (var guard = new NpgsqlCommand("""
            SELECT current_database()='glam_auth_mvc_workflow_test'
              AND current_user='booking_test_session_issuer' AND session_user='booking_test_session_issuer'
              AND inet_server_addr()='127.0.0.1'::inet AND inet_client_addr()='127.0.0.1'::inet AND inet_server_port()=5432
              AND EXISTS(SELECT 1 FROM public.auth_mvc_test_sentinel WHERE name='glam_auth_mvc_workflow_test')
              AND EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user AND rolcanlogin AND NOT rolsuper AND NOT rolbypassrls)
            """, c, tx))
            if ((bool?)await guard.ExecuteScalarAsync(ct) != true) throw new InvalidOperationException("Unsafe isolated issuer.");
        var actor = customer.CustomerId;
        var login = actor == SyntheticBookingSessions.CustomerA ? "booking_test_customer_a" :
                    actor == SyntheticBookingSessions.CustomerB ? "booking_test_customer_b" : throw new InvalidOperationException("Invalid synthetic identity.");
        var proof = customer.CopyProof();
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT booking_test.issue_synthetic_session(@login::name,@proof,@expires)", c, tx);
            cmd.Parameters.AddWithValue("login", login);
            cmd.Parameters.Add("proof", NpgsqlDbType.Bytea).Value = proof;
            cmd.Parameters.AddWithValue("expires", customer.ExpiresAt);
            await cmd.ExecuteNonQueryAsync(ct);
            _ = customer.CustomerId;
        }
        finally { CryptographicOperations.ZeroMemory(proof); }
    }

    internal static Task LockAsync(NpgsqlConnection c, NpgsqlTransaction tx, SyntheticBookingSessions.VerifiedCustomer customer, CancellationToken ct) =>
        ExecuteAsync(c, tx, customer, "SELECT booking_test.lock_synthetic_session(@proof)", ct);
    internal static Task RevokeAsync(NpgsqlConnection c, NpgsqlTransaction tx, SyntheticBookingSessions.VerifiedCustomer customer, CancellationToken ct) =>
        ExecuteAsync(c, tx, customer, "SELECT booking_test.revoke_synthetic_session(@proof)", ct);

    private static async Task ExecuteAsync(NpgsqlConnection c, NpgsqlTransaction tx, SyntheticBookingSessions.VerifiedCustomer customer, string sql, CancellationToken ct)
    {
        BookingTestProtectedBoundary.RequireImplemented();
        if (tx.Connection != c || customer is null) throw new InvalidOperationException("Invalid isolated session transaction.");
        var actor = customer.CustomerId;
        var login = actor == SyntheticBookingSessions.CustomerA ? "booking_test_customer_a" :
                    actor == SyntheticBookingSessions.CustomerB ? "booking_test_customer_b" : throw new InvalidOperationException("Invalid synthetic identity.");
        await using (var guard = new NpgsqlCommand("""
            SELECT current_database()='glam_auth_mvc_workflow_test' AND current_user=@login AND session_user=@login
              AND inet_server_addr()='127.0.0.1'::inet AND inet_client_addr()='127.0.0.1'::inet AND inet_server_port()=5432
              AND EXISTS(SELECT 1 FROM public.auth_mvc_test_sentinel WHERE name='glam_auth_mvc_workflow_test')
              AND EXISTS(SELECT 1 FROM pg_roles WHERE rolname=@login AND rolcanlogin AND NOT rolsuper AND NOT rolbypassrls)
              AND pg_has_role(current_user,'booking_test_runtime','USAGE')
            """, c, tx))
        {
            guard.Parameters.AddWithValue("login", login);
            if ((bool?)await guard.ExecuteScalarAsync(ct) != true) throw new InvalidOperationException("Unsafe isolated session target.");
        }
        var proof = customer.CopyProof();
        try
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            cmd.Parameters.Add("proof", NpgsqlDbType.Bytea).Value = proof;
            await cmd.ExecuteNonQueryAsync(ct);
            _ = customer.CustomerId;
        }
        finally { CryptographicOperations.ZeroMemory(proof); }
    }
}
