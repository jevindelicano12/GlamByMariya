using GlamByMariya.Infrastructure.Data;
using Npgsql;

// Independent commits retain synthetic records. Never run without separate approval.
if (Environment.GetEnvironmentVariable("GLAM_AUTH_TEST_WRITES") != "YES")
    throw new InvalidOperationException("Explicit authorization for persistent authentication test writes is required.");
var cs = new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 5432, Database = "glam_auth_security_test", Username = "postgres", Pooling = false, Timeout = 5, SslMode = SslMode.Disable };
var builder = new NpgsqlDataSourceBuilder(cs.ConnectionString);
async Task Verify(NpgsqlConnection c)
{
    await using var cmd = new NpgsqlCommand("SELECT current_database()='glam_auth_security_test' AND current_user='postgres' AND inet_server_addr()='127.0.0.1'::inet AND inet_client_addr()='127.0.0.1'::inet AND inet_server_port()=5432 AND (SELECT count(*)=1 FROM public.auth_test_sentinel WHERE name='glam_auth_security_test')", c);
    if ((bool?)await cmd.ExecuteScalarAsync() != true) throw new InvalidOperationException("Unsafe authentication test target.");
}
builder.UsePhysicalConnectionInitializer(c => Verify(c).GetAwaiter().GetResult(), c => Verify(c));
await using var source = builder.Build();
var first = new PostgresDurableCredentialStore(source); var second = new PostgresDurableCredentialStore(source);
foreach (var role in new[] { "Customer", "Admin", "SuperAdmin" })
{
    var id = Guid.NewGuid(); var subject = Guid.NewGuid(); var email = "synthetic-" + id.ToString("N") + "@example.test";
    await using (var c = await source.OpenConnectionAsync())
    await using (var tx = await c.BeginTransactionAsync())
    {
        var ownerSql = role == "Customer"
            ? "INSERT INTO public.customers(app_id,email,is_active,password_hash,security_stamp) VALUES(@id,@email,true,'synthetic',@stamp) RETURNING id"
            : "INSERT INTO public.admin_users(app_id,username,email,role,is_active,password_hash,security_stamp) VALUES(@id,@email,@email,@role,true,'synthetic',@stamp) RETURNING id";
        await using var insert = new NpgsqlCommand(ownerSql, c, tx);
        insert.Parameters.AddWithValue("id", id); insert.Parameters.AddWithValue("email", email); insert.Parameters.AddWithValue("stamp", Guid.NewGuid());
        if (role != "Customer") insert.Parameters.AddWithValue("role", role);
        var owner = (long)(await insert.ExecuteScalarAsync())!;
        var column = role == "Customer" ? "customer_id" : "admin_id";
        await using var link = new NpgsqlCommand($"INSERT INTO public.account_identities(id,{column},login,auth_state,supabase_user_id,credential_sync_state) VALUES(@id,@owner,@email,'Supabase',@subject,'Ready')", c, tx);
        link.Parameters.AddWithValue("id", id); link.Parameters.AddWithValue("owner", owner); link.Parameters.AddWithValue("email", email); link.Parameters.AddWithValue("subject", subject);
        await link.ExecuteNonQueryAsync();
        // Credential writers now share the revocation generation lock.
        await using var generation = new NpgsqlCommand("INSERT INTO public.account_security_generations(account_id,generation) VALUES(@id,1)", c, tx);
        generation.Parameters.AddWithValue("id", id);
        await generation.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }
    Console.WriteLine($"CREATED retained synthetic {role} identity/account: {id}");

    // One explicit transaction contains both updates, so rollback does not rely on a repository connection.
    await using (var c = await source.OpenConnectionAsync())
    await using (var tx = await c.BeginTransactionAsync())
    {
        await using var update = new NpgsqlCommand("UPDATE public.account_identities SET credential_epoch=2,credential_sync_state='Pending' WHERE id=@id", c, tx);
        update.Parameters.AddWithValue("id", id); if (await update.ExecuteNonQueryAsync() != 1) throw new Exception("Rollback setup failed.");
        await using var invalid = new NpgsqlCommand("UPDATE public.account_identities SET credential_sync_state='InvalidSyntheticState' WHERE id=@id", c, tx);
        invalid.Parameters.AddWithValue("id", id);
        try { await invalid.ExecuteNonQueryAsync(); throw new Exception("Invalid state accepted."); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation) { await tx.RollbackAsync(); }
    }
    await using (var c = await source.OpenConnectionAsync())
    await using (var check = new NpgsqlCommand("SELECT credential_epoch=1 AND credential_sync_state='Ready' FROM public.account_identities WHERE id=@id", c))
    { check.Parameters.AddWithValue("id", id); if ((bool?)await check.ExecuteScalarAsync() != true) throw new Exception("Rollback failed."); }
    Console.WriteLine($"PASS {role} failed-write rollback verified on independent connection");

    if (await first.Begin(id, Guid.NewGuid(), 1, default) != null) throw new Exception("Mismatched identity accepted.");
    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    async Task<long?> Compete(PostgresDurableCredentialStore repo)
    {
        await start.Task;
        try { return await repo.Begin(id, subject, 1, default); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.SerializationFailure) { return null; }
    }
    var one = Compete(first); var two = Compete(second); start.SetResult();
    var results = await Task.WhenAll(one, two);
    if (results.Count(x => x == 2) != 1 || results.Count(x => x == null) != 1) throw new Exception("Concurrent transition did not have exactly one winner.");
    await using (var c = await source.OpenConnectionAsync())
    await using (var check = new NpgsqlCommand("SELECT credential_epoch=2 AND credential_sync_state='Pending' FROM public.account_identities WHERE id=@id", c))
    { check.Parameters.AddWithValue("id", id); if ((bool?)await check.ExecuteScalarAsync() != true) throw new Exception("Pending not persisted."); }
    if (await second.Reject(id, subject, 1, default)) throw new Exception("Stale operation accepted.");
    if (!await first.Complete(id, subject, 2, "SyntheticNew123!", default)) throw new Exception("Completion failed.");
    if (await second.Complete(id, subject, 2, "SyntheticOther123!", default)) throw new Exception("Stale completion accepted.");
    if (await first.Begin(id, subject, 2, default) != 3 || !await second.MarkReconciliation(id, subject, 3, default)
        || await first.Reject(id, subject, 3, default)) throw new Exception("Reconciliation isolation failed.");
    await using (var c = await source.OpenConnectionAsync())
    await using (var check = new NpgsqlCommand("SELECT credential_epoch=3 AND credential_sync_state='ReconciliationRequired' FROM public.account_identities WHERE id=@id", c))
    { check.Parameters.AddWithValue("id", id); if ((bool?)await check.ExecuteScalarAsync() != true) throw new Exception("Reconciliation not persisted."); }
    Console.WriteLine($"PASS {role} concurrent begin, epoch, stale tokens, completion and reconciliation across independent connections");
}
Console.WriteLine("Synthetic identities retained. Provider, cookie middleware, multi-process behavior, server restart and recovery are not covered.");
