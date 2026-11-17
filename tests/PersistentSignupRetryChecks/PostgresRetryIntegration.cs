using System.Security.Cryptography;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using GlamByMariya.Core.Entities;
using Microsoft.AspNetCore.Identity;
using System.Text.Json;

// Never run without separate approval and an independently created disposable database.
internal static class PostgresRetryIntegration
{
    internal static async Task RunAsync()
    {
        const string prefix = "glam_retry_disposable_";
        var database = Environment.GetEnvironmentVariable("GLAM_RETRY_DISPOSABLE_DB") ?? "";
        if (Environment.GetEnvironmentVariable("GLAM_RETRY_TEST_WRITES") != "YES" ||
            !database.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(database[prefix.Length..], "N", out var marker) || marker == Guid.Empty ||
            !int.TryParse(Environment.GetEnvironmentVariable("GLAM_RETRY_PORT"), out var port) || port is < 1024 or > 65535)
            throw new InvalidOperationException("Explicit disposable PostgreSQL test authorization is required.");

        var role = "glam_retry_runner_" + marker.ToString("N");
        var builder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = port, Database = database, Username = role,
            Pooling = false, Timeout = 3, SslMode = SslMode.Disable
        }.ConnectionString);
        async Task Guard(NpgsqlConnection connection)
        {
            await using var command = new NpgsqlCommand("""
                SELECT current_database()=@db AND current_user=@role
                  AND inet_server_addr()='127.0.0.1'::inet AND inet_client_addr()='127.0.0.1'::inet
                  AND inet_server_port()=@port
                  AND EXISTS (SELECT 1 FROM pg_roles r WHERE r.rolname=current_user
                    AND r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolcreatedb
                    AND NOT r.rolcreaterole AND NOT r.rolbypassrls AND NOT r.rolinherit
                    AND NOT EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.member=r.oid))
                  AND NOT has_database_privilege(current_user,current_database(),'CREATE')
                  AND NOT has_database_privilege(current_user,current_database(),'TEMP')
                  AND NOT has_schema_privilege(current_user,'public','CREATE')
                  AND NOT has_table_privilege(current_user,'public.glam_retry_disposable_sentinel','INSERT,UPDATE,DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'public.customers','UPDATE,DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'public.admin_users','INSERT,UPDATE,DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'public.account_identities','DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'public.auth_mvc_signup_attempts','DELETE,TRUNCATE')
                  AND NOT has_table_privilege(current_user,'public.account_security_generations','DELETE,TRUNCATE')
                  AND (SELECT count(*)=1 AND count(*) FILTER (WHERE marker=@marker)=1 FROM public.glam_retry_disposable_sentinel)
                """, connection);
            command.Parameters.AddWithValue("db", database);
            command.Parameters.AddWithValue("role", role);
            command.Parameters.AddWithValue("port", port);
            command.Parameters.AddWithValue("marker", marker);
            if ((bool?)await command.ExecuteScalarAsync() != true) throw new InvalidOperationException("Unsafe PostgreSQL test target.");
        }
        builder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), Guard);
        await using var source = builder.Build();
        var protection = new EphemeralDataProtectionProvider();
        var attempts = new PostgresDurableSignupAttempts(source, protection);
        var entry = await attempts.Create($"synthetic-{Guid.NewGuid():N}@example.test", "{}");
        var subject = Guid.NewGuid();
        if (!await attempts.MarkAwaitingOtp(entry.Id, subject)) throw new Exception("Attempt initialization failed.");
        var first = new PostgresPersistentSignupRetryStore(source, protection);
        var second = new PostgresPersistentSignupRetryStore(source, protection);
        var sharedEmail = $"competing-{Guid.NewGuid():N}@example.test";
        async Task<bool> TryCreate()
        {
            try { await attempts.Create(sharedEmail, "{}"); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var competingCreates = await Task.WhenAll(TryCreate(), TryCreate());
        if (competingCreates.Count(x => x) != 1) throw new Exception("Overlapping signup attempts were not serialized.");
        var wrong = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        if (await first.BeginAsync(entry.Id, wrong, SignupOperation.Verify, default) != null ||
            await first.BeginAsync(entry.Id, entry.Secret, SignupOperation.Resend, default) != null)
            throw new Exception("Ownership or initial cooldown was not enforced.");
        await using (var connection = await source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("UPDATE public.auth_mvc_signup_attempts SET next_resend_at=now()-interval '1 second' WHERE attempt_id=@id", connection))
        {
            command.Parameters.AddWithValue("id", entry.Id);
            if (await command.ExecuteNonQueryAsync() != 1) throw new Exception("Synthetic cooldown setup failed.");
        }
        var claims = await Task.WhenAll(
            first.BeginAsync(entry.Id, entry.Secret, SignupOperation.Verify, default),
            second.BeginAsync(entry.Id, entry.Secret, SignupOperation.Resend, default));
        var winner = claims.SingleOrDefault(x => x != null);
        if (winner == null || claims.Count(x => x != null) != 1 ||
            await second.ReleaseAsync(entry.Id, entry.Secret, Guid.NewGuid(), default) ||
            !await second.ReleaseAsync(entry.Id, entry.Secret, winner.OperationId, default))
            throw new Exception("Concurrent claim or stale operation fence failed.");
        var restarted = new PostgresPersistentSignupRetryStore(source, protection);
        if (claims[1] == null)
        {
            var resend = await restarted.BeginAsync(entry.Id, entry.Secret, SignupOperation.Resend, default);
            if (resend == null || !await restarted.ReleaseAsync(entry.Id, entry.Secret, resend.OperationId, default))
                throw new Exception("Synthetic resend setup failed.");
        }
        if (await restarted.BeginAsync(entry.Id, entry.Secret, SignupOperation.Resend, default) != null)
            throw new Exception("Resend cooldown did not persist across store recreation.");
        if (await restarted.BeginAsync(entry.Id, entry.Secret, SignupOperation.Verify, default) == null)
            throw new Exception("Recreated store did not observe persisted state.");

        async Task<(Guid Id, string Secret, SignupOperationClaim Claim)> NewClaim()
        {
            var email = $"synthetic-{Guid.NewGuid():N}@example.test";
            var user = new AppUser { FirstName = "Synthetic", LastName = "Customer", Name = "Synthetic Customer",
                Email = email, Age = 25, Gender = "Others", Address = "123 Synthetic Street", Role = "Customer", IsActive = true };
            user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, "Fixture123!");
            var attempt = await attempts.Create(email, JsonSerializer.Serialize(user));
            if (!await attempts.MarkAwaitingOtp(attempt.Id, Guid.NewGuid())) throw new Exception("Synthetic signup setup failed.");
            var claim = await first.BeginAsync(attempt.Id, attempt.Secret, SignupOperation.Verify, default);
            return (attempt.Id, attempt.Secret, claim ?? throw new Exception("Synthetic verification claim failed."));
        }

        var expiredEmail = $"expired-{Guid.NewGuid():N}@example.test";
        var expired = await attempts.Create(expiredEmail, "{}");
        if (!await attempts.MarkAwaitingOtp(expired.Id, Guid.NewGuid())) throw new Exception("Expiry setup failed.");
        await using (var connection = await source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("""
            UPDATE public.auth_mvc_signup_attempts SET created_at=now()-interval '31 minutes',expires_at=now()-interval '1 minute'
            WHERE attempt_id=@id
            """, connection))
        {
            command.Parameters.AddWithValue("id", expired.Id);
            if (await command.ExecuteNonQueryAsync() != 1) throw new Exception("Synthetic expiry setup failed.");
        }
        if (await first.BeginAsync(expired.Id, expired.Secret, SignupOperation.Verify, default) != null)
            throw new Exception("Expired attempt was claimed.");
        try { await attempts.Create(expiredEmail, "{}"); throw new Exception("Expired unresolved attempt was replaced."); }
        catch (InvalidOperationException) { }

        var exhaustedEmail = $"exhausted-{Guid.NewGuid():N}@example.test";
        var exhausted = await attempts.Create(exhaustedEmail, "{}");
        if (!await attempts.MarkAwaitingOtp(exhausted.Id, Guid.NewGuid())) throw new Exception("Exhaustion setup failed.");
        await using (var connection = await source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("UPDATE public.auth_mvc_signup_attempts SET verification_attempts=5 WHERE attempt_id=@id", connection))
        {
            command.Parameters.AddWithValue("id", exhausted.Id);
            if (await command.ExecuteNonQueryAsync() != 1) throw new Exception("Synthetic exhaustion setup failed.");
        }
        if (await first.BeginAsync(exhausted.Id, exhausted.Secret, SignupOperation.Verify, default) != null)
            throw new Exception("Exhausted attempt was claimed.");
        try { await attempts.Create(exhaustedEmail, "{}"); throw new Exception("Exhausted unresolved attempt was replaced."); }
        catch (InvalidOperationException) { }

        var duplicate = await NewClaim();
        await using (var connection = await source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("INSERT INTO public.account_security_generations(account_id,generation) VALUES(@id,1)", connection))
        {
            command.Parameters.AddWithValue("id", duplicate.Id);
            await command.ExecuteNonQueryAsync();
        }
        if (await first.CommitAsync(duplicate.Id, duplicate.Secret, duplicate.Claim, default))
            throw new Exception("Duplicate generation committed.");
        await using (var connection = await source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM public.customers WHERE app_id=@id)=0
              AND (SELECT count(*) FROM public.account_security_generations WHERE account_id=@id AND generation=1)=1
              AND (SELECT state='Verifying' FROM public.auth_mvc_signup_attempts WHERE attempt_id=@id)
            """, connection))
        {
            command.Parameters.AddWithValue("id", duplicate.Id);
            if ((bool?)await command.ExecuteScalarAsync() != true) throw new Exception("Failed account transaction was not rolled back.");
        }

        var completed = await NewClaim();
        // Discard the result to model a lost acknowledgement after an actual commit;
        // this is not a driver-level lost-acknowledgement fault injection.
        _ = await first.CommitAsync(completed.Id, completed.Secret, completed.Claim, default);
        if (!await restarted.ReconcileCommittedAsync(completed.Id, completed.Secret, completed.Claim, default) ||
            await restarted.ReconcileCommittedAsync(completed.Id, wrong, completed.Claim, default) ||
            await restarted.ReconcileCommittedAsync(completed.Id, completed.Secret, completed.Claim with { OperationId = Guid.NewGuid() }, default) ||
            await restarted.CommitAsync(completed.Id, completed.Secret, completed.Claim, default))
            throw new Exception("Positive, negative or replay reconciliation failed.");
        await using (var connection = await source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("UPDATE public.account_security_generations SET generation=2 WHERE account_id=@id AND generation=1", connection))
        {
            command.Parameters.AddWithValue("id", completed.Id);
            if (await command.ExecuteNonQueryAsync() != 1) throw new Exception("Synthetic revocation setup failed.");
        }
        if (await restarted.ReconcileCommittedAsync(completed.Id, completed.Secret, completed.Claim, default))
            throw new Exception("Revoked generation was accepted by reconciliation.");
        Console.WriteLine("Disposable PostgreSQL claim, cooldown, expiry, fencing, store recreation, rollback, duplicate generation and reconciliation checks passed.");
    }
}
