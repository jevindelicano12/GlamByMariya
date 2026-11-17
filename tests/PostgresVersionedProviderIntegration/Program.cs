using System.Data;
using System.Security.Claims;
using GlamByMariya.Core.BusinessRules;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;
using Npgsql;

const string database = "glam_provider_snapshot_test";
const string owner = "glam_authz_test";
const string denied = "glam_provider_denied";
const string hash = "synthetic-provider-hash-only";
var ids = Enumerable.Range(1, 5).Select(i => Guid.Parse($"33000000-0000-4000-8000-{i:000000000000}")).ToArray();
var subjects = Enumerable.Range(1, 5).Select(i => Guid.Parse($"44000000-0000-4000-8000-{i:000000000000}")).ToArray();

NpgsqlConnectionStringBuilder Settings(string role, string password)
{
    if (role is not (owner or denied)) throw new InvalidOperationException("Unexpected role.");
    return new() { Host = "127.0.0.1", Port = 5432, Database = database, Username = role,
        Password = password, Pooling = false, Timeout = 5 };
}

if (args.SequenceEqual(["--offline"]))
{
    foreach (var role in new[] { owner, denied })
    {
        var settings = Settings(role, "synthetic");
        if (settings.Host != "127.0.0.1" || settings.Port != 5432 || settings.Database != database ||
            settings.Username != role || settings.Pooling) throw new Exception("Unsafe settings.");
    }
    try { Settings("other", "synthetic"); throw new Exception("Unexpected role accepted."); }
    catch (InvalidOperationException) { }
    var keys = new Queue<ConsoleKeyInfo>([new('a', ConsoleKey.A, false, false, false),
        new('\b', ConsoleKey.Backspace, false, false, false), new('b', ConsoleKey.B, false, false, false),
        new('\r', ConsoleKey.Enter, false, false, false)]);
    if (InteractivePassword.Read(keys.Dequeue) != "b") throw new Exception("Password input failed.");
    try { InteractivePassword.Read(() => new('\r', ConsoleKey.Enter, false, false, false)); throw new Exception("Empty password accepted."); }
    catch (InvalidOperationException) { }
    var sql = File.ReadAllText("tests/PostgresVersionedProviderIntegration/schema.test-only.sql");
    foreach (var marker in new[] { "current_database() <> 'glam_provider_snapshot_test'", "session_user <> 'glam_authz_test'",
        "inet_server_addr()", "pg_auth_members", "pg_default_acl", "ON_ERROR_STOP", "glam_provider_denied",
        "REVOKE ALL ON ALL TABLES", "public.account_security_generations" })
        if (!sql.Contains(marker, StringComparison.Ordinal)) throw new Exception("Schema safety marker missing.");
    var columns = IsolatedPreflight.ExpectedColumns.ToArray();
    if (!IsolatedPreflight.MatchesColumns(columns) || IsolatedPreflight.MatchesColumns(columns.Skip(1)) ||
        IsolatedPreflight.IsExclusive(true, 1) || IsolatedPreflight.IsFixtureEmpty(1)) throw new Exception("Preflight failed.");
    if (!Allowed(["--diagnose-readonly"], null, "YES", null) || Allowed(["--diagnose-readonly"], "YES", null, null) ||
        Allowed(["--diagnose-25006"], null, "YES", "YES") ||
        Allowed(["--exercise"], null, "YES", "YES") || !Allowed(["--exercise"], "YES", null, null) ||
        !Allowed(["--continue-readonly"], null, null, "YES") || Allowed(["--continue-readonly"], "YES", "YES", null) ||
        Allowed(["--exercise", "--diagnose-readonly"], "YES", "YES", "YES"))
        throw new Exception("Mode opt-in failed.");
    var pgError = new PostgresException("sensitive message", "ERROR", "ERROR", "42501");
    var before = Report(Stage.DeniedInitializer, PermissionPhase.Before, pgError);
    var during = Report(Stage.PermissionQuery, PermissionPhase.During, pgError);
    var after = Report(Stage.DeniedInitializer, PermissionPhase.After, new IOException("sensitive details"));
    if (!before.Contains("before") || !before.Contains("42501") || !during.Contains("during") ||
        !during.Contains("42501") || !after.Contains("after") || !after.Contains("unavailable") ||
        (before + during + after).Contains("sensitive") || !ExpectedDenial(pgError) ||
        ExpectedDenial(new PostgresException("sensitive", "ERROR", "ERROR", "42P01")))
        throw new Exception("Safe diagnostic formatting failed.");
    if (ExpectedProbe(new PostgresException("sensitive", "ERROR", "ERROR", "25006")) ||
        !ExpectedProbe(new PostgresException("sensitive", "ERROR", "ERROR", "22012")))
        throw new Exception("Unexpected probe SQLSTATE decision.");
    var observed = new ObservedQuery(new OfflineFailingQuery());
    try { await observed.ExecuteAsync("synthetic", Guid.NewGuid(), default); throw new Exception("Failure expected."); }
    catch (PostgresSecurityReadException) { }
    if (observed.Calls != 1 || observed.Sql != "synthetic" || !observed.RepositoryUnavailable)
        throw new Exception("Query observation failed.");
    Console.WriteLine("8 grouped offline checks passed: targets, hidden input, schema markers, preflight, mode opt-in, safe diagnostics, probe SQLSTATE decisions, query observation.");
    return;
}
var diagnose = args.SequenceEqual(["--diagnose-readonly"]);
var continuation = args.SequenceEqual(["--continue-readonly"]);
if (!Allowed(args, Environment.GetEnvironmentVariable("GLAM_PROVIDER_SNAPSHOT_WRITES"),
        Environment.GetEnvironmentVariable("GLAM_PROVIDER_SNAPSHOT_READS"),
        Environment.GetEnvironmentVariable("GLAM_PROVIDER_SNAPSHOT_CONTINUE")))
    throw new InvalidOperationException("Explicit approval and matching mode opt-in required.");

var stage = Stage.Input;
var phase = PermissionPhase.Before;
var deniedInitializers = 0;
PostgresException? initializerError = null;
try
{
    if (Console.IsInputRedirected || Console.IsOutputRedirected) throw new InvalidOperationException("Interactive terminal required.");
    Console.Write("Owner test-role password (hidden): ");
    var ownerPassword = InteractivePassword.Read(() => Console.ReadKey(true));
    Console.WriteLine();
    Console.Write("Denied test-role password (hidden): ");
    var deniedPassword = InteractivePassword.Read(() => Console.ReadKey(true));
    Console.WriteLine();

    async Task Guard(NpgsqlConnection connection, string role)
    {
        var previousStage = stage;
        stage = role == denied ? Stage.DeniedInitializer : Stage.OwnerInitializer;
        await using var cmd = new NpgsqlCommand("""
            SELECT current_database()='glam_provider_snapshot_test'
              AND current_user=@role AND session_user=@role
              AND inet_server_addr()='127.0.0.1'::inet AND inet_client_addr()='127.0.0.1'::inet
              AND inet_server_port()=5432
              AND EXISTS(SELECT 1 FROM pg_database d JOIN pg_roles r ON r.oid=d.datdba
                         WHERE d.datname=current_database() AND r.rolname='glam_authz_test')
              AND EXISTS(SELECT 1 FROM pg_roles WHERE rolname=@role AND rolcanlogin
                         AND NOT rolsuper AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls
                         AND (@role <> 'glam_provider_denied' OR NOT rolcreatedb))
              AND NOT EXISTS(SELECT 1 FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.member WHERE r.rolname=@role)
              AND (SELECT count(*)=1 AND bool_and(name='synthetic-sprint3-only') FROM public.authz_sprint3_sentinel)
            """, connection);
        cmd.Parameters.AddWithValue("role", role);
        bool? guardResult;
        try { guardResult = (bool?)await cmd.ExecuteScalarAsync(); }
        catch (PostgresException error) { initializerError = error; throw; }
        if (guardResult != true) throw new InvalidOperationException("Unsafe test target.");
        if (role == denied) deniedInitializers++;
        stage = previousStage;
    }
    NpgsqlDataSource Build(string role, string password)
    {
        var builder = new NpgsqlDataSourceBuilder(Settings(role, password).ConnectionString);
        builder.UsePhysicalConnectionInitializer(c => Guard(c, role).GetAwaiter().GetResult(), c => Guard(c, role));
        return builder.Build();
    }

    stage = Stage.OwnerConnection;
    await using var ownerSource = Build(owner, ownerPassword);
    await using var lease = await ownerSource.OpenConnectionAsync();
    stage = Stage.Preflight;
    await IsolatedPreflight.VerifyAsync(lease, requireEmpty: !(diagnose || continuation));

    async Task PermissionDenialCheck()
    {
        stage = Stage.DeniedConnection;
        await using var deniedSource = Build(denied, deniedPassword);
        await using (var connection = await deniedSource.OpenConnectionAsync())
        {
            stage = Stage.PermissionQuery;
            phase = PermissionPhase.During;
            try
            {
                await using var command = new NpgsqlCommand("SELECT generation FROM public.account_security_generations", connection);
                await command.ExecuteScalarAsync();
                throw new InvalidOperationException("Expected permission denial.");
            }
            catch (PostgresException error) when (ExpectedDenial(error))
            {
                Console.WriteLine("PASS real PostgreSQL permission denial");
            }
            phase = PermissionPhase.After;
            stage = Stage.AfterPermissionQuery;
        }
        if (diagnose) return;
        stage = Stage.ProviderDenial;
        var deniedGuard = new VersionedSessionAuthorization(new PostgresVersionedAccountSecurityProvider(new PostgresAccountSecurityQuery(deniedSource), "public"));
        if (await deniedGuard.ValidateCookie(Ticket(0))) throw new InvalidOperationException("Provider accepted denied read.");
        Console.WriteLine("PASS provider fails closed on permission denial");
    }

    ClaimsPrincipal Ticket(int index, long generation = 7) => new(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, ids[index].ToString()), new(ClaimTypes.Role, "Customer"),
         new(DurableSessionAuthorization.EpochClaim, "3"), new("account_version", AccountSession.Version(hash)),
         new(DurableSessionAuthorization.SubjectClaim, subjects[index].ToString()),
         new(VersionedSessionAuthorization.SecurityVersionClaim, generation.ToString())], "synthetic"));

    if (continuation)
    {
        stage = Stage.FixtureVerify;
        await using (var transaction = await lease.BeginTransactionAsync(IsolationLevel.RepeatableRead))
        {
            await using var set = new NpgsqlCommand("SET TRANSACTION READ ONLY", lease, transaction);
            await set.ExecuteNonQueryAsync();
            await using var verify = new NpgsqlCommand("""
                WITH expected AS (
                  SELECT id, subject, n FROM unnest(@ids::uuid[], @subjects::uuid[]) WITH ORDINALITY AS e(id,subject,n)
                )
                SELECT (SELECT count(*) FROM public.customers)=5
                  AND (SELECT count(*) FROM public.admin_users)=0
                  AND (SELECT count(*) FROM public.account_identities)=5
                  AND (SELECT count(*) FROM public.account_security_generations)=4
                  AND has_schema_privilege('glam_provider_denied','public','USAGE')
                  AND has_table_privilege('glam_provider_denied','public.authz_sprint3_sentinel','SELECT')
                  AND has_table_privilege('glam_provider_denied','public.customers','SELECT')
                  AND has_table_privilege('glam_provider_denied','public.admin_users','SELECT')
                  AND has_table_privilege('glam_provider_denied','public.account_identities','SELECT')
                  AND NOT has_column_privilege('glam_provider_denied','public.account_security_generations','generation','SELECT')
                  AND NOT EXISTS (
                    SELECT 1 FROM expected e
                    LEFT JOIN public.customers c ON c.app_id=e.id
                    LEFT JOIN public.account_identities i ON i.id=e.id
                    LEFT JOIN public.account_security_generations g ON g.account_id=e.id
                    WHERE c.id IS NULL OR i.id IS NULL
                      OR c.email IS DISTINCT FROM ('provider-' || (e.n-1)::text || '@example.invalid')
                      OR c.is_active IS DISTINCT FROM (e.n<>2)
                      OR (CASE WHEN e.n=3 THEN c.locked_until IS NOT NULL AND c.locked_until>statement_timestamp()
                               ELSE c.locked_until IS NULL END) IS NOT TRUE
                      OR c.password_hash IS DISTINCT FROM @hash
                      OR i.customer_id IS DISTINCT FROM c.id OR i.admin_id IS NOT NULL
                      OR i.login IS DISTINCT FROM (CASE WHEN e.n=5 THEN 'mismatch@example.invalid'
                           ELSE 'provider-' || (e.n-1)::text || '@example.invalid' END)
                      OR i.auth_state IS DISTINCT FROM 'Supabase'
                      OR i.supabase_user_id IS DISTINCT FROM e.subject
                      OR i.credential_epoch IS DISTINCT FROM 3
                      OR i.credential_sync_state IS DISTINCT FROM 'Ready'
                      OR (CASE WHEN e.n=4 THEN g.account_id IS NULL ELSE g.generation=7 END) IS NOT TRUE
                  )
                """, lease, transaction);
            verify.Parameters.AddWithValue("ids", ids);
            verify.Parameters.AddWithValue("subjects", subjects);
            verify.Parameters.AddWithValue("hash", hash);
            if ((bool?)await verify.ExecuteScalarAsync() != true) throw new InvalidOperationException("Fixture mismatch.");
            await transaction.CommitAsync();
        }
        stage = Stage.ProviderChecks;
        var ownerGuard = new VersionedSessionAuthorization(new PostgresVersionedAccountSecurityProvider(new PostgresAccountSecurityQuery(ownerSource), "public"));
        if (!await ownerGuard.ValidateCookie(Ticket(0))) throw new InvalidOperationException("Baseline denied.");

        stage = Stage.DeniedConnection;
        await using var deniedSource = Build(denied, deniedPassword);
        await using (var connection = await deniedSource.OpenConnectionAsync()) { }
        var observed = new ObservedQuery(new PostgresAccountSecurityQuery(deniedSource));
        var deniedGuard = new VersionedSessionAuthorization(new PostgresVersionedAccountSecurityProvider(observed, "public"));
        stage = Stage.ProviderDenial;
        var accepted = await deniedGuard.ValidateCookie(Ticket(0));
        if (initializerError != null) throw initializerError;
        if (accepted || observed.Calls != 1 || !observed.RepositoryUnavailable ||
            deniedInitializers != 2 || observed.Sql == null) throw new InvalidOperationException("Denied provider check failed.");

        stage = Stage.PermissionQuery;
        phase = PermissionPhase.During;
        await using (var connection = await deniedSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead))
        {
            await using var set = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
            await set.ExecuteNonQueryAsync();
            try
            {
                await using var query = new NpgsqlCommand(observed.Sql, connection, transaction);
                query.Parameters.AddWithValue("account_id", ids[0]);
                await query.ExecuteScalarAsync();
                throw new InvalidOperationException("Expected permission denial.");
            }
            catch (PostgresException error) when (ExpectedDenial(error)) { }
            await transaction.RollbackAsync();
        }
        phase = PermissionPhase.After;
        Console.WriteLine("PASS provider fails closed and its exact query receives PostgreSQL permission denial");

        stage = Stage.UnavailableProvider;
        var disposedSource = Build(owner, ownerPassword);
        var unavailableQuery = new ObservedQuery(new PostgresAccountSecurityQuery(disposedSource));
        var disposedGuard = new VersionedSessionAuthorization(new PostgresVersionedAccountSecurityProvider(unavailableQuery, "public"));
        await disposedSource.DisposeAsync();
        if (await disposedGuard.ValidateCookie(Ticket(0)) || unavailableQuery.Calls != 1 || !unavailableQuery.RepositoryUnavailable)
            throw new InvalidOperationException("Unavailable provider check failed.");
        Console.WriteLine("PASS disposed data source fails closed");
        Console.WriteLine("Continuation complete; fixtures unchanged.");
        return;
    }

    if (diagnose)
    {
        stage = Stage.ReadonlyProbe;
        await using (var transaction = await lease.BeginTransactionAsync(IsolationLevel.RepeatableRead))
        {
            await using var set = new NpgsqlCommand("SET TRANSACTION READ ONLY", lease, transaction);
            await set.ExecuteNonQueryAsync();
            try
            {
                await using var command = new NpgsqlCommand("SELECT 1 / 0", lease, transaction);
                await command.ExecuteScalarAsync();
                throw new InvalidOperationException("Expected read-only diagnostic error.");
            }
            catch (PostgresException error) when (ExpectedProbe(error)) { }
            stage = Stage.ReadonlyRollback;
            await transaction.RollbackAsync();
            stage = Stage.ReadonlyDispose;
        }
        Console.WriteLine("PASS rollback after read-only transaction error");
        await PermissionDenialCheck();
        Console.WriteLine("Read-only diagnostic complete; fixtures unchanged.");
        return;
    }

    stage = Stage.FixtureWrite;
    await using (var transaction = await lease.BeginTransactionAsync())
    {
        for (var i = 0; i < ids.Length; i++)
        {
            await using var command = new NpgsqlCommand("""
                WITH customer AS (
                  INSERT INTO public.customers(app_id,email,is_active,locked_until,password_hash)
                  VALUES (@id,@email,@active,CASE WHEN @locked THEN statement_timestamp()+interval '1 day' ELSE NULL END,@hash)
                  RETURNING id
                ), identity AS (
                  INSERT INTO public.account_identities(id,customer_id,login,auth_state,supabase_user_id,credential_epoch,credential_sync_state)
                  SELECT @id,id,@identity_login,'Supabase',@subject,3,'Ready' FROM customer RETURNING id
                )
                INSERT INTO public.account_security_generations(account_id,generation)
                SELECT id,7 FROM identity WHERE @has_generation
                """, lease, transaction);
            command.Parameters.AddWithValue("id", ids[i]);
            command.Parameters.AddWithValue("email", $"provider-{i}@example.invalid");
            command.Parameters.AddWithValue("identity_login", i == 4 ? "mismatch@example.invalid" : $"provider-{i}@example.invalid");
            command.Parameters.AddWithValue("active", i != 1);
            command.Parameters.AddWithValue("locked", i == 2);
            command.Parameters.AddWithValue("has_generation", i != 3);
            command.Parameters.AddWithValue("hash", hash);
            command.Parameters.AddWithValue("subject", subjects[i]);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }
    var count = 0;
    void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException("Check failed: " + label); count++; Console.WriteLine("PASS " + label); }
    stage = Stage.ProviderChecks;
    var provider = new PostgresVersionedAccountSecurityProvider(new PostgresAccountSecurityQuery(ownerSource), "public");
    var guard = new VersionedSessionAuthorization(provider);
    var snapshot = await provider.LoadAsync(ids[0], default);
    Check(snapshot is { SecurityVersion: 7 } && snapshot.Account.AccountId == ids[0] &&
          snapshot.Account.AccountVersion == AccountSession.Version(hash) && snapshot.Account.SupabaseSubject == subjects[0],
        "real query and snapshot projection");
    Check(await guard.ValidateCookie(Ticket(0)), "active session accepted");
    Check(!await guard.ValidateCookie(Ticket(1)), "disabled denied");
    Check(!await guard.ValidateCookie(Ticket(2)), "locked denied");
    Check(!await guard.ValidateCookie(Ticket(0, 6)), "stale generation denied");
    Check(!await guard.ValidateCookie(Ticket(3)), "missing generation denied");
    Check(!await guard.ValidateCookie(Ticket(4)), "inconsistent identity denied");
    Check(await guard.PrepareSupabaseLogin(ids[0], subjects[0], "Customer") != null, "verified subject prepares evidence");

    stage = Stage.ReadonlyProbe;
    await using (var readonlyTransaction = await lease.BeginTransactionAsync(IsolationLevel.RepeatableRead))
    {
        await using var set = new NpgsqlCommand("SET TRANSACTION READ ONLY", lease, readonlyTransaction);
        await set.ExecuteNonQueryAsync();
        try
        {
            await using var write = new NpgsqlCommand("INSERT INTO public.customers(app_id,email,is_active,password_hash) VALUES (@id,'probe@example.invalid',true,'synthetic')", lease, readonlyTransaction);
            write.Parameters.AddWithValue("id", Guid.NewGuid());
            await write.ExecuteNonQueryAsync();
            throw new InvalidOperationException("Read-only transaction unexpectedly allowed a write.");
        }
        catch (PostgresException error) when (error.SqlState == "25006") { Check(true, "PostgreSQL read-only transaction rejects write"); }
        stage = Stage.ReadonlyRollback;
        await readonlyTransaction.RollbackAsync();
        stage = Stage.ReadonlyDispose;
    }
    await PermissionDenialCheck();
    count += 2;
    stage = Stage.UnavailableProvider;
    var unavailable = Build(owner, ownerPassword);
    var unavailableGuard = new VersionedSessionAuthorization(new PostgresVersionedAccountSecurityProvider(new PostgresAccountSecurityQuery(unavailable), "public"));
    await unavailable.DisposeAsync();
    Check(!await unavailableGuard.ValidateCookie(Ticket(0)), "disposed data source fails closed");
    Console.WriteLine($"{count} checks passed; synthetic fixtures retained.");
}
catch (Exception error)
{
    Console.Error.WriteLine(Report(stage, phase, error));
    Environment.ExitCode = 1;
}

static bool Allowed(string[] arguments, string? writes, string? reads, string? continuation) =>
    arguments.SequenceEqual(["--exercise"]) && writes == "YES" ||
    arguments.SequenceEqual(["--diagnose-readonly"]) && reads == "YES" ||
    arguments.SequenceEqual(["--continue-readonly"]) && continuation == "YES";

static bool ExpectedDenial(PostgresException error) => error.SqlState == "42501";
static bool ExpectedProbe(PostgresException error) => error.SqlState == "22012";

static string Report(Stage stage, PermissionPhase phase, Exception error)
{
    var pg = error as PostgresException ?? error.InnerException as PostgresException;
    var sqlState = pg != null && pg.SqlState.Length == 5 &&
        pg.SqlState.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z')
        ? pg.SqlState : "unavailable";
    return $"Test stopped; stage={stage}; permission-query={phase.ToString().ToLowerInvariant()}; SQLSTATE={sqlState}.";
}

enum PermissionPhase { Before, During, After }
enum Stage
{
    Input, OwnerConnection, OwnerInitializer, Preflight, FixtureVerify, FixtureWrite, ProviderChecks,
    ReadonlyProbe, ReadonlyRollback, ReadonlyDispose, DeniedConnection, DeniedInitializer, PermissionQuery,
    AfterPermissionQuery, ProviderDenial, UnavailableProvider
}

sealed class ObservedQuery(IPostgresAccountSecurityQuery inner) : IPostgresAccountSecurityQuery
{
    public int Calls { get; private set; }
    public string? Sql { get; private set; }
    public bool RepositoryUnavailable { get; private set; }
    public async Task<string> ExecuteAsync(string sql, Guid accountId, CancellationToken cancellationToken)
    {
        Calls++;
        Sql = sql;
        try { return await inner.ExecuteAsync(sql, accountId, cancellationToken); }
        catch (PostgresSecurityReadException error) when (error.Failure == PostgresSecurityReadFailure.RepositoryUnavailable)
        {
            RepositoryUnavailable = true;
            throw;
        }
    }
}

sealed class OfflineFailingQuery : IPostgresAccountSecurityQuery
{
    public Task<string> ExecuteAsync(string sql, Guid accountId, CancellationToken cancellationToken)
        => throw new PostgresSecurityReadException(PostgresSecurityReadFailure.RepositoryUnavailable);
}
