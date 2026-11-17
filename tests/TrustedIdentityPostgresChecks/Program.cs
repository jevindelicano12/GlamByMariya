using System.Security.Claims;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;
using Npgsql;

const string TestDatabase = "glam_v2_identity_boundary_test";
const int TestPort = 55433;
const string BackendRole = "glam_v2_backend";
const string OwnerRole = "glam_v2_authz_owner";
var repoRoot = FindRepoRoot();
var pgData = Path.GetFullPath(Path.Combine(repoRoot, "temp", "v2-proof-state-20261116-fresh", "data"));
var adminBuilder = new NpgsqlConnectionStringBuilder
{
    Host = "127.0.0.1", Port = TestPort, Database = "postgres", Username = "postgres",
    Pooling = false, Timeout = 5, CommandTimeout = 20, SslMode = SslMode.Disable
};

var mode = args.SingleOrDefault();
if (mode == "--offline")
{
    RunOfflineChecks();
    return;
}
if (mode != "--exercise")
    throw new InvalidOperationException("Choose --offline or --exercise.");

var password = Environment.GetEnvironmentVariable("GLAM_V2_IDENTITY_TEST_PASSWORD");
if (password is not { Length: 64 } || !password.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'))
    throw new InvalidOperationException("A fresh 32-byte test-only password is required.");
adminBuilder.Password = password;
await PrepareAndExerciseAsync();

async Task PrepareAndExerciseAsync()
{
    await using var cluster = NpgsqlDataSource.Create(adminBuilder.ConnectionString);
    await using (var connection = await cluster.OpenConnectionAsync())
    {
        var preflight = new NpgsqlCommand("""
            SELECT inet_server_addr() = '127.0.0.1'::inet, inet_server_port(), current_database(), current_user,
                   current_setting('data_directory'), current_setting('server_version_num')::integer,
                   (SELECT rolsuper FROM pg_roles WHERE rolname=current_user),
                   EXISTS(SELECT 1 FROM pg_database WHERE datname=@target),
                   (SELECT count(*) FROM pg_roles WHERE rolname=ANY(@roles))
            """, connection);
        preflight.Parameters.AddWithValue("target", TestDatabase);
        preflight.Parameters.AddWithValue("roles", new[] { "anon", "authenticated", "service_role", BackendRole, OwnerRole });
        await using var reader = await preflight.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Local PostgreSQL preflight failed.");
        var isLoopback = reader.GetBoolean(0);
        var port = reader.GetInt32(1);
        var database = reader.GetString(2);
        var user = reader.GetString(3);
        var dataDirectory = Path.GetFullPath(reader.GetString(4));
        var version = reader.GetInt32(5);
        var isSuperuser = reader.GetBoolean(6);
        var databaseExists = reader.GetBoolean(7);
        var roleCount = reader.GetInt32(8);
        if (!isLoopback || port != TestPort || database != "postgres"
            || user != "postgres" || !isSuperuser || !string.Equals(dataDirectory.TrimEnd(Path.DirectorySeparatorChar), pgData.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || version is < 180000 or >= 190000 || databaseExists || roleCount != 0)
            throw new InvalidOperationException("Refusing a non-fresh or unexpected PostgreSQL target.");
    }

    await using (var connection = await cluster.OpenConnectionAsync())
    {
        await ExecuteAsync(connection, $"CREATE ROLE anon LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{password}';");
        await ExecuteAsync(connection, $"CREATE ROLE authenticated LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{password}';");
        await ExecuteAsync(connection, $"CREATE ROLE service_role LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION BYPASSRLS PASSWORD '{password}';");
        await ExecuteAsync(connection, $"CREATE ROLE {OwnerRole} NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;");
        await ExecuteAsync(connection, $"CREATE ROLE {BackendRole} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{password}';");
        await ExecuteAsync(connection, $"CREATE DATABASE {TestDatabase} TEMPLATE template0;");
    }

    var dbBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = TestDatabase };
    await using var databaseSource = NpgsqlDataSource.Create(dbBuilder.ConnectionString);
    await using (var connection = await databaseSource.OpenConnectionAsync())
    {
        await ExecuteAsync(connection, """
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO PUBLIC, anon, authenticated, service_role;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO PUBLIC, anon, authenticated, service_role;
            """);
        await ExecuteAsync(connection, "CREATE SCHEMA extensions; CREATE EXTENSION btree_gist WITH SCHEMA extensions;");
        await ExecuteFileAsync(connection, Path.Combine(repoRoot, "database", "v2", "migrations", "0001_catalog_schedule_foundation.sql"));
        await ExecuteFileAsync(connection, Path.Combine(repoRoot, "database", "v2", "migrations", "0002_customer_reservation_foundation.sql"));
        await ExecuteFileAsync(connection, Path.Combine(repoRoot, "database", "v2", "candidates", "identity_authorization_candidate.sql"));
        await ExecuteFileAsync(connection, Path.Combine(repoRoot, "tests", "TrustedIdentityPostgresChecks", "proof_correction_fixture.sql"));
        await ExecuteFileAsync(connection, Path.Combine(repoRoot, "database", "v2", "candidates", "customer_proof_correction_test_only.sql"));
        await ExecuteFileAsync(connection, Path.Combine(repoRoot, "database", "v2", "candidates", "payment_proof_staging_test_only.sql"));
    }

    var actors = await SeedSyntheticActorsAsync(databaseSource);
    var backendBuilder = new NpgsqlConnectionStringBuilder(dbBuilder.ConnectionString) { Username = BackendRole };
    await using var backendData = NpgsqlDataSource.Create(backendBuilder.ConnectionString);
    var boundary = PostgresTrustedIdentityBoundary.ForIsolatedTests(backendData);
    var closedBoundary = new PostgresTrustedIdentityBoundary(backendData);
    var passed = 0;
    void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + label);
        passed++;
        Console.WriteLine("PASS " + label);
    }

    foreach (var actor in actors.Values)
        Check(await boundary.ValidateProtectedSessionAsync(Principal(actor)), actor.Role + " valid synthetic session accepted by PostgreSQL");
    Check(!await closedBoundary.ValidateProtectedSessionAsync(Principal(actors["customer-a"])),
        "public adapter stays fail-closed even with plausible claims");
    Check(!await closedBoundary.UpdateCustomerFullNameAsync(Principal(actors["customer-b"]), "Must not write"),
        "public adapter denies a fabricated authenticated principal before database access");
    Check(await boundary.ValidateProtectedSessionAsync(Principal(actors["customer-b"])),
        "test-only adapter demonstrates that fabricated matching claims are indistinguishable from protected claims");
    Check(!await boundary.ValidateProtectedSessionAsync(Principal(actors["customer-a"] with { ProviderSubject = Guid.NewGuid() })),
        "database rejects mismatched provider subject");
    Check(!await boundary.ValidateProtectedSessionAsync(Principal(actors["customer-a"] with { CredentialEpoch = 2 })),
        "database rejects stale credential epoch");
    Check(!await boundary.ValidateProtectedSessionAsync(Principal(actors["customer-a"] with { SecurityGeneration = 2 })),
        "database rejects stale security generation");
    Check(!await boundary.ValidateProtectedSessionAsync(Principal(actors["customer-a"] with { Role = "Admin" })),
        "database rejects a forged role claim");

    var withUntrustedCustomerId = Principal(actors["customer-a"], [new Claim("customer_id", actors["customer-b"].AccountId.ToString("D"))]);
    Check(await boundary.UpdateCustomerFullNameAsync(withUntrustedCustomerId, "Synthetic Customer A Updated"),
        "profile mutation ignores browser customer_id and resolves owner from protected actor");
    Check(await ReadNameAsync(databaseSource, actors["customer-a"].AccountId) == "Synthetic Customer A Updated"
        && await ReadNameAsync(databaseSource, actors["customer-b"].AccountId) == "Synthetic Customer B",
        "profile mutation changed only the authenticated Customer");
    Check(!await boundary.UpdateCustomerFullNameAsync(Principal(actors["admin"]), "Not a customer"),
        "staff role cannot call Customer profile mutation");
    var proofHttp = await ProofCorrectionChecks.RunAsync(cluster, databaseSource, backendData, actors["customer-a"], actors["customer-b"], actors["customer-proof-race"], actors["customer-http"], Check);
    await CookieHostChecks.RunAsync(boundary, backendData, proofHttp, actors["customer-http"], Check);
    Check(await ReadNameAsync(databaseSource, actors["customer-http"].AccountId) == "Synthetic HTTP Customer",
        "isolated cookie host mutation affects its own synthetic customer");

    await ExpectDeniedAsync(backendData, "SELECT count(*) FROM public.customers");
    await ExpectDeniedAsync(backendData, "SELECT count(*) FROM public.reservations");
    await ExpectDeniedAsync(backendData, "SELECT count(*) FROM public.admin_users");
    await ExpectDeniedAsync(backendData, "SELECT count(*) FROM public.account_identities");
    await ExpectDeniedAsync(backendData, "SELECT count(*) FROM public.account_security_generations");
    await ExpectDeniedAsync(backendData, "UPDATE public.customers SET full_name='Bypass' WHERE app_id='" + actors["customer-a"].AccountId + "'");
    await ExpectDeniedAsync(backendData, "INSERT INTO public.payment_revisions(payment_id,revision,reference,verification_status) VALUES(1,99,'bypass','Pending Verification')");
    await ExpectDeniedAsync(backendData, "UPDATE public.payment_proof_assets SET is_validated=true");
    await ExpectDeniedAsync(backendData, "SELECT count(*) FROM public.fixture_proof_staging");
    await ExpectDeniedAsync(backendData, "DELETE FROM public.fixture_proof_staging");
    await ExpectDeniedAsync(backendData, "SELECT nextval('public.admin_users_id_seq')");
    await ExpectRoleDeniedAsync(dbBuilder, "anon");
    await ExpectRoleDeniedAsync(dbBuilder, "authenticated");
    await ExpectRoleDeniedAsync(dbBuilder, "service_role");
    foreach (var role in new[] { "anon", "authenticated", "service_role" })
    {
        var builder = new NpgsqlConnectionStringBuilder(dbBuilder.ConnectionString) { Username = role };
        await using var roleSource = NpgsqlDataSource.Create(builder.ConnectionString);
        await ExpectDeniedAsync(roleSource, "SELECT nextval('public.admin_users_id_seq')");
    }

    var firstRevocation = await boundary.RevokeMySessionsAsync(Principal(actors["customer-a"]));
    Check(firstRevocation == 2, "self revocation increments the locked generation");
    Check(!await boundary.ValidateProtectedSessionAsync(Principal(actors["customer-a"])),
        "revocation invalidates the old protected session");

    var currentCustomer = actors["customer-a"] with { SecurityGeneration = 2 };
    await AssertRevocationWaitsForProtectedWriteAsync(cluster, backendData, currentCustomer, Check);
    Check(!await boundary.ValidateProtectedSessionAsync(Principal(currentCustomer)),
        "session started before revocation cannot be reused after the ordered commit");
    var postRace = currentCustomer with { SecurityGeneration = 3 };
    Check(await boundary.UpdateCustomerFullNameAsync(Principal(postRace), "Synthetic Customer A Current"),
        "new generation can perform a protected write after revocation");

    await AssertProtectedWriteWaitsForRevocationAsync(cluster, backendData, actors["customer-b"], Check);
    var currentCustomerB = actors["customer-b"] with { SecurityGeneration = 2 };
    Check(await ReadNameAsync(databaseSource, actors["customer-b"].AccountId) == "Synthetic Customer B",
        "a write blocked by committed revocation does not change the customer");
    Check(await boundary.ValidateProtectedSessionAsync(Principal(currentCustomerB)),
        "new Customer generation remains valid after revocation-first race");
    await SetCustomerSecurityAsync(databaseSource, actors["customer-b"].AccountId, active: false, lockedUntil: null);
    Check(!await boundary.ValidateProtectedSessionAsync(Principal(currentCustomerB)), "inactive account is denied by PostgreSQL");
    await SetCustomerSecurityAsync(databaseSource, actors["customer-b"].AccountId, active: true, lockedUntil: DateTimeOffset.UtcNow.AddMinutes(5));
    Check(!await boundary.ValidateProtectedSessionAsync(Principal(currentCustomerB)), "locked account is denied by PostgreSQL");

    Console.WriteLine($"Trusted identity PostgreSQL checks: {passed} passed; synthetic local database only.");
    Console.WriteLine("Supabase remote verification, production cookie issuance, other security writers, and real booking/payment integration remain untested and gated.");
}

static void RunOfflineChecks()
{
    var passed = 0;
    void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + label);
        passed++;
        Console.WriteLine("PASS " + label);
    }
    var actor = new TrustedActorSession(Guid.NewGuid(), "Customer", Guid.NewGuid(), 4, 9);
    var valid = Principal(actor);
    Check(TrustedActorSession.TryReadProtectedPrincipal(valid, out var parsed) && parsed == actor,
        "strict protected-session claim parsing");
    Check(!TrustedActorSession.TryReadProtectedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(valid.Claims)), out _),
        "anonymous principal cannot create trusted actor evidence");
    var duplicate = Principal(actor, [new Claim(ClaimTypes.Role, "Admin")]);
    Check(!TrustedActorSession.TryReadProtectedPrincipal(duplicate, out _), "duplicate role claim is rejected");
    var malformed = new ClaimsPrincipal(new ClaimsIdentity(
        valid.Claims.Where(c => c.Type != DurableSessionAuthorization.EpochClaim)
            .Append(new Claim(DurableSessionAuthorization.EpochClaim, "04")), "synthetic"));
    Check(!TrustedActorSession.TryReadProtectedPrincipal(malformed, out _), "noncanonical credential epoch is rejected");

    var sql = File.ReadAllText(Path.Combine(FindRepoRoot(), "database", "v2", "candidates", "identity_authorization_candidate.sql"));
    var generationLock = sql.IndexOf("FROM public.account_security_generations\n        WHERE account_id = p_account_id FOR UPDATE", StringComparison.Ordinal);
    var customerLock = sql.IndexOf("FROM public.customers WHERE app_id = p_account_id FOR UPDATE", StringComparison.Ordinal);
    var staffLock = sql.IndexOf("FROM public.admin_users WHERE app_id = p_account_id FOR UPDATE", StringComparison.Ordinal);
    var identityLock = sql.IndexOf("FROM public.account_identities WHERE id = p_account_id FOR UPDATE", StringComparison.Ordinal);
    Check(generationLock >= 0 && generationLock < customerLock && customerLock < staffLock && staffLock < identityLock,
        "candidate source states generation, customer, staff, identity lock order");
    Check(sql.Contains("REVOKE ALL ON ALL TABLES IN SCHEMA public FROM glam_v2_backend", StringComparison.Ordinal)
        && sql.Contains("GRANT EXECUTE ON FUNCTION v2_authz_api.validate_session", StringComparison.Ordinal),
        "candidate denies direct backend table access and grants narrow API execution");
    Console.WriteLine($"Trusted identity offline checks: {passed} passed.");
}

static ClaimsPrincipal Principal(TrustedActorSession actor, IEnumerable<Claim>? extraClaims = null)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, actor.AccountId.ToString("D")),
        new(ClaimTypes.Role, actor.Role),
        new(DurableSessionAuthorization.SubjectClaim, actor.ProviderSubject.ToString("D")),
        new(DurableSessionAuthorization.EpochClaim, actor.CredentialEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new(VersionedSessionAuthorization.SecurityVersionClaim, actor.SecurityGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture))
    };
    if (extraClaims != null) claims.AddRange(extraClaims);
    return new(new ClaimsIdentity(claims, "synthetic-protected-cookie"));
}

async Task<Dictionary<string, TrustedActorSession>> SeedSyntheticActorsAsync(NpgsqlDataSource source)
{
    var result = new Dictionary<string, TrustedActorSession>();
    await using var connection = await source.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    await SeedCustomerAsync("customer-a", "customer-a@example.test", "Synthetic Customer A");
    await SeedCustomerAsync("customer-b", "customer-b@example.test", "Synthetic Customer B");
    await SeedCustomerAsync("customer-http", "customer-http@example.test", "Before HTTP");
    await SeedCustomerAsync("customer-proof-race", "proof-race@example.test", "Synthetic proof race");
    await SeedAdminAsync("admin", "admin@example.test", "Admin");
    await SeedAdminAsync("superadmin", "superadmin@example.test", "SuperAdmin");
    await transaction.CommitAsync();
    return result;

    async Task SeedCustomerAsync(string key, string email, string fullName)
    {
        var accountId = Guid.NewGuid(); var subject = Guid.NewGuid();
        await using var insert = new NpgsqlCommand("INSERT INTO public.customers(app_id,full_name,phone,email) VALUES(@id,@name,NULL,@email) RETURNING id", connection, transaction);
        insert.Parameters.AddWithValue("id", accountId); insert.Parameters.AddWithValue("name", fullName); insert.Parameters.AddWithValue("email", email);
        var customerId = (long)(await insert.ExecuteScalarAsync() ?? throw new InvalidOperationException("Synthetic customer insert failed."));
        await SeedIdentityAsync(accountId, subject, "customer_id", customerId, email);
        result.Add(key, new(accountId, "Customer", subject, 1, 1));
    }

    async Task SeedAdminAsync(string key, string email, string role)
    {
        var accountId = Guid.NewGuid(); var subject = Guid.NewGuid();
        await using var insert = new NpgsqlCommand("INSERT INTO public.admin_users(app_id,username,email,role) VALUES(@id,@email,@email,@role) RETURNING id", connection, transaction);
        insert.Parameters.AddWithValue("id", accountId); insert.Parameters.AddWithValue("email", email); insert.Parameters.AddWithValue("role", role);
        var adminId = (long)(await insert.ExecuteScalarAsync() ?? throw new InvalidOperationException("Synthetic staff insert failed."));
        await SeedIdentityAsync(accountId, subject, "admin_id", adminId, email);
        result.Add(key, new(accountId, role, subject, 1, 1));
    }

    async Task SeedIdentityAsync(Guid accountId, Guid subject, string ownerColumn, long ownerId, string email)
    {
        var sql = ownerColumn switch
        {
            "customer_id" => "INSERT INTO public.account_identities(id,customer_id,login,auth_state,supabase_user_id,credential_epoch,credential_sync_state) VALUES(@id,@owner,@login,'Supabase',@subject,1,'Ready')",
            "admin_id" => "INSERT INTO public.account_identities(id,admin_id,login,auth_state,supabase_user_id,credential_epoch,credential_sync_state) VALUES(@id,@owner,@login,'Supabase',@subject,1,'Ready')",
            _ => throw new InvalidOperationException("Unknown synthetic owner type.")
        };
        await using (var identity = new NpgsqlCommand(sql, connection, transaction))
        {
            identity.Parameters.AddWithValue("id", accountId); identity.Parameters.AddWithValue("owner", ownerId);
            identity.Parameters.AddWithValue("login", email); identity.Parameters.AddWithValue("subject", subject);
            await identity.ExecuteNonQueryAsync();
        }
        await using var generation = new NpgsqlCommand("INSERT INTO public.account_security_generations(account_id,generation) VALUES(@id,1)", connection, transaction);
        generation.Parameters.AddWithValue("id", accountId);
        await generation.ExecuteNonQueryAsync();
    }
}

static async Task<string?> ReadNameAsync(NpgsqlDataSource source, Guid accountId)
{
    await using var connection = await source.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("SELECT full_name FROM public.customers WHERE app_id=@id", connection);
    command.Parameters.AddWithValue("id", accountId);
    return await command.ExecuteScalarAsync() as string;
}

static async Task SetCustomerSecurityAsync(NpgsqlDataSource source, Guid accountId, bool active, DateTimeOffset? lockedUntil)
{
    await using var connection = await source.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("UPDATE public.customers SET is_active=@active,locked_until=@locked WHERE app_id=@id", connection);
    command.Parameters.AddWithValue("active", active); command.Parameters.AddWithValue("locked", (object?)lockedUntil?.UtcDateTime ?? DBNull.Value);
    command.Parameters.AddWithValue("id", accountId);
    await command.ExecuteNonQueryAsync();
}

static async Task ExpectDeniedAsync(NpgsqlDataSource source, string sql)
{
    await using var connection = await source.OpenConnectionAsync();
    try
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
        throw new InvalidOperationException("Expected a direct-access permission denial.");
    }
    catch (PostgresException exception) when (exception.SqlState == "42501")
    {
        Console.WriteLine("PASS backend direct table operation denied");
    }
}

static async Task ExpectRoleDeniedAsync(NpgsqlConnectionStringBuilder template, string role)
{
    var builder = new NpgsqlConnectionStringBuilder(template.ConnectionString) { Username = role };
    await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
    await using var connection = await source.OpenConnectionAsync();
    try
    {
        await using var command = new NpgsqlCommand("SELECT v2_authz_api.validate_session(NULL,NULL,NULL,NULL,NULL)", connection);
        await command.ExecuteScalarAsync();
        throw new InvalidOperationException($"Expected {role} to be denied from the auth API.");
    }
    catch (PostgresException exception) when (exception.SqlState == "42501")
    {
        Console.WriteLine("PASS " + role + " cannot execute V2 auth API");
    }
}

static async Task AssertRevocationWaitsForProtectedWriteAsync(
    NpgsqlDataSource monitor,
    NpgsqlDataSource backend,
    TrustedActorSession actor,
    Action<bool, string> check)
{
    await using var first = await backend.OpenConnectionAsync();
    await using var firstTransaction = await first.BeginTransactionAsync();
    await using (var update = CreateActorCommand(first, firstTransaction, actor,
        "SELECT v2_authz_api.update_customer_full_name(@account_id,@provider_subject,@role,@credential_epoch,@security_generation,@full_name)"))
    {
        update.Parameters.AddWithValue("full_name", "Synthetic Customer A Before Revocation");
        check(await update.ExecuteScalarAsync() is true, "protected write validates and mutates in one PostgreSQL transaction");
    }

    await using var revoker = await backend.OpenConnectionAsync();
    var revokeTask = new NpgsqlCommand("SELECT v2_authz_api.revoke_my_sessions(@account_id,@provider_subject,@role,@credential_epoch,@security_generation)", revoker);
    Bind(revokeTask, actor);
    var revokeProcessId = revoker.ProcessID;
    var task = revokeTask.ExecuteScalarAsync();
    var blocked = false;
    try
    {
        await using var monitorConnection = await monitor.OpenConnectionAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!timeout.IsCancellationRequested)
        {
            await using var probe = new NpgsqlCommand("SELECT @blocker = ANY(pg_blocking_pids(@waiter))", monitorConnection);
            probe.Parameters.AddWithValue("blocker", first.ProcessID);
            probe.Parameters.AddWithValue("waiter", revokeProcessId);
            if (await probe.ExecuteScalarAsync(timeout.Token) is true) { blocked = true; break; }
            await Task.Delay(25, timeout.Token);
        }
    }
    catch (OperationCanceledException) { }
    finally { await firstTransaction.CommitAsync(); }

    check(blocked, "revocation waits behind the in-flight protected write lock");
    var value = await task.WaitAsync(TimeSpan.FromSeconds(8));
    check(value is long generation && generation == actor.SecurityGeneration + 1,
        "revocation commits after the protected write and advances one generation");
    await revokeTask.DisposeAsync();
}

static async Task AssertProtectedWriteWaitsForRevocationAsync(
    NpgsqlDataSource monitor, NpgsqlDataSource backend, TrustedActorSession actor, Action<bool, string> check)
{
    await using var revoker = await backend.OpenConnectionAsync();
    await using var transaction = await revoker.BeginTransactionAsync();
    await using (var revoke = CreateActorCommand(revoker, transaction, actor,
        "SELECT v2_authz_api.revoke_my_sessions(@account_id,@provider_subject,@role,@credential_epoch,@security_generation)"))
        check(await revoke.ExecuteScalarAsync() is long next && next == actor.SecurityGeneration + 1,
            "revocation obtains the security lock before the competing write");

    await using var writer = await backend.OpenConnectionAsync();
    await using var write = CreateActorCommand(writer, null, actor,
        "SELECT v2_authz_api.update_customer_full_name(@account_id,@provider_subject,@role,@credential_epoch,@security_generation,@full_name)");
    write.Parameters.AddWithValue("full_name", "Must Not Persist");
    var task = write.ExecuteScalarAsync();
    var blocked = false;
    try
    {
        await using var monitorConnection = await monitor.OpenConnectionAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!timeout.IsCancellationRequested)
        {
            await using var probe = new NpgsqlCommand("SELECT @blocker = ANY(pg_blocking_pids(@waiter))", monitorConnection);
            probe.Parameters.AddWithValue("blocker", revoker.ProcessID);
            probe.Parameters.AddWithValue("waiter", writer.ProcessID);
            if (await probe.ExecuteScalarAsync(timeout.Token) is true) { blocked = true; break; }
            await Task.Delay(25, timeout.Token);
        }
    }
    catch (OperationCanceledException) { }
    finally { await transaction.CommitAsync(); }

    check(blocked, "protected write waits behind the in-flight revocation");
    check(await task.WaitAsync(TimeSpan.FromSeconds(8)) is false,
        "protected write using the old generation is denied after revocation commits");
}

static NpgsqlCommand CreateActorCommand(NpgsqlConnection connection, NpgsqlTransaction? transaction,
    TrustedActorSession actor, string sql)
{
    var command = new NpgsqlCommand(sql, connection, transaction);
    Bind(command, actor);
    return command;
}

static void Bind(NpgsqlCommand command, TrustedActorSession actor)
{
    command.Parameters.AddWithValue("account_id", actor.AccountId);
    command.Parameters.AddWithValue("provider_subject", actor.ProviderSubject);
    command.Parameters.AddWithValue("role", actor.Role);
    command.Parameters.AddWithValue("credential_epoch", actor.CredentialEpoch);
    command.Parameters.AddWithValue("security_generation", actor.SecurityGeneration);
}

static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
{
    await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
    await command.ExecuteNonQueryAsync();
}

static async Task ExecuteFileAsync(NpgsqlConnection connection, string path)
    => await ExecuteAsync(connection, await File.ReadAllTextAsync(path));

static string FindRepoRoot()
{
    foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        var current = new DirectoryInfo(start);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MakeupSystem.slnx"))) return current.FullName;
            current = current.Parent;
        }
    }
    throw new DirectoryNotFoundException("V2 repository root was not found.");
}
