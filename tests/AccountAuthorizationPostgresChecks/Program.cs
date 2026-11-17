using System.Security.Claims;
using GlamByMariya.Core.BusinessRules;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;
using Npgsql;

// Separate explicit approval is required. Only two fixed test targets are accepted.
if (args.SequenceEqual(["--offline"]))
{
    foreach (var (arguments, database) in new[] {
        (new[] { "--exercise" }, "glam_authz_sprint3"),
        (new[] { "--after-restart" }, "glam_authz_sprint3"),
        (new[] { "--exercise", "--retry" }, "glam_authz_sprint3_retry"),
        (new[] { "--after-restart", "--retry" }, "glam_authz_sprint3_retry") })
    {
        if (TargetFor(arguments) != database) throw new InvalidOperationException("Incorrect test target.");
        var check = ConnectionSettings("synthetic", database);
        if (check.Host != "127.0.0.1" || check.Port != 5432 || check.Database != database ||
            check.Username != "glam_authz_test" || check.Pooling) throw new InvalidOperationException("Unsafe fixed test target.");
    }
    foreach (var bad in new[] { Array.Empty<string>(), new[] { "--retry" }, new[] { "--exercise", "production" },
        new[] { "--retry", "--exercise" }, new[] { "--exercise", "--retry", "--retry" } })
        if (TargetFor(bad) != null) throw new InvalidOperationException("Unexpected test target accepted.");
    try { ConnectionSettings("synthetic", "production"); throw new Exception("Unexpected database accepted."); }
    catch (InvalidOperationException) { }
    var keys = new Queue<ConsoleKeyInfo>([
        new('a', ConsoleKey.A, false, false, false), new('b', ConsoleKey.B, false, false, false),
        new('\b', ConsoleKey.Backspace, false, false, false), new('c', ConsoleKey.C, false, false, false),
        new('\r', ConsoleKey.Enter, false, false, false)]);
    if (InteractivePassword.Read(keys.Dequeue) != "ac") throw new InvalidOperationException("Password editing check failed.");
    try { InteractivePassword.Read(() => new('\r', ConsoleKey.Enter, false, false, false)); throw new Exception("Empty password accepted."); }
    catch (InvalidOperationException) { }
    try { InteractivePassword.Read(() => new('\x1b', ConsoleKey.Escape, false, false, false)); throw new Exception("Cancellation accepted."); }
    catch (InvalidOperationException) { }
    var sql = File.ReadAllText("tests/AccountAuthorizationPostgresChecks/schema.test-only.sql");
    foreach (var required in new[] { "d.datname=current_database() AND r.rolname=current_user", "NOT rolcreaterole", "NOT rolreplication",
        "pg_auth_members", "pg_type", "pg_collation", "pg_extension", "pg_default_acl", "ON_ERROR_STOP", "inet_server_addr()",
        "'glam_authz_sprint3','glam_authz_sprint3_retry'", "current_database() <> current_setting('glam.test_database', true)" })
        if (!sql.Contains(required, StringComparison.Ordinal)) throw new InvalidOperationException("Schema safety contract missing.");
    var expected = IsolatedPreflight.ExpectedColumns.ToArray();
    if (!IsolatedPreflight.MatchesColumns(expected.Reverse())) throw new InvalidOperationException("Valid table shapes denied.");
    foreach (var table in expected.Select(x => x.Table).Distinct())
        if (IsolatedPreflight.MatchesColumns(expected.Where(x => x.Table != table))) throw new InvalidOperationException("Missing table accepted.");
    if (IsolatedPreflight.MatchesColumns(expected.Append(expected[0])) ||
        IsolatedPreflight.MatchesColumns(expected.Append(new ColumnShape("customers", "unexpected", "text", true, "", ""))) ||
        IsolatedPreflight.MatchesColumns(expected.Select((x, i) => i == 0 ? x with { Required = false } : x)) ||
        IsolatedPreflight.MatchesColumns(expected.Select((x, i) => i == 0 ? x with { Type = "uuid" } : x)))
        throw new InvalidOperationException("Changed table shape accepted.");
    if (!IsolatedPreflight.IsExclusive(true, 0) || IsolatedPreflight.IsExclusive(false, 0) ||
        IsolatedPreflight.IsExclusive(true, 1) || IsolatedPreflight.IsExclusive(true, -1) ||
        !IsolatedPreflight.IsFixtureEmpty(0) || IsolatedPreflight.IsFixtureEmpty(1) || IsolatedPreflight.IsFixtureEmpty(-1))
        throw new InvalidOperationException("Preflight failure decision was accepted.");
    Console.WriteLine("PASS fixed isolated target and disabled pooling");
    Console.WriteLine("PASS interactive password editing, empty input and cancellation");
    Console.WriteLine("PASS schema ownership, role, membership and empty-object guard markers");
    Console.WriteLine("PASS five table shapes reject missing, extra and altered columns");
    Console.WriteLine("PASS lease, other-session and fixture-count failure decisions");
    return;
}
var target = TargetFor(args);
if (target == null || Environment.GetEnvironmentVariable("GLAM_AUTHZ_SPRINT3_WRITES") != "YES")
    throw new InvalidOperationException("Explicit approval and --exercise or --after-restart required.");
try
{
if (Console.IsInputRedirected || Console.IsOutputRedirected) throw new InvalidOperationException("An interactive terminal is required.");
Console.Write("Test-only PostgreSQL password (input hidden): ");
var password = InteractivePassword.Read(() => Console.ReadKey(intercept: true));
Console.WriteLine();
var builder = new NpgsqlDataSourceBuilder(ConnectionSettings(password, target).ConnectionString);
async Task Guard(NpgsqlConnection c)
{
    await using var cmd = new NpgsqlCommand("""
        SELECT current_database()=@expected_database
          AND current_database() IN ('glam_authz_sprint3','glam_authz_sprint3_retry') AND current_user='glam_authz_test'
          AND session_user='glam_authz_test' AND inet_server_addr()='127.0.0.1'::inet
          AND inet_client_addr()='127.0.0.1'::inet AND inet_server_port()=5432
          AND EXISTS(SELECT 1 FROM pg_database d JOIN pg_roles r ON r.oid=d.datdba
                     WHERE d.datname=current_database() AND r.rolname=current_user)
          AND EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user AND rolcanlogin
                     AND NOT rolsuper AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls)
          AND NOT EXISTS(SELECT 1 FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.member WHERE r.rolname=current_user)
          AND (SELECT count(*)=1 FROM public.authz_sprint3_sentinel WHERE name='synthetic-sprint3-only')
        """, c);
    cmd.Parameters.AddWithValue("expected_database", target);
    if ((bool?)await cmd.ExecuteScalarAsync() != true) throw new InvalidOperationException("Unsafe test target.");
}
builder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), c => Guard(c));
await using var source = builder.Build();
await using var leaseConnection = await source.OpenConnectionAsync();
await IsolatedPreflight.VerifyAsync(leaseConnection, requireEmpty: args[0] == "--exercise");
var owner = Guid.Parse("11000000-0000-4000-8000-000000000001");
var admin = Guid.Parse("11000000-0000-4000-8000-000000000002");
var admin2 = Guid.Parse("11000000-0000-4000-8000-000000000003");
var subject = Guid.Parse("22000000-0000-4000-8000-000000000001");
var hash = "synthetic-hash-not-a-password";
var passed = 0;
void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException("FAIL: " + label); passed++; Console.WriteLine("PASS " + label); }
async Task<(bool Active, bool Locked, long Generation)> State(Guid id)
{
    await using var c = await source.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("""
        SELECT u.is_active, u.locked_until IS NOT NULL AND u.locked_until > statement_timestamp(), g.generation
        FROM public.admin_users u JOIN public.account_security_generations g ON g.account_id=u.app_id WHERE u.app_id=@id
        """, c);
    cmd.Parameters.AddWithValue("id", id);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) throw new InvalidOperationException("Synthetic row missing.");
    return (r.GetBoolean(0), r.GetBoolean(1), r.GetInt64(2));
}
ClaimsPrincipal Ticket(Guid id, string role, Guid sub, long generation) => new(new ClaimsIdentity(
    [new(ClaimTypes.NameIdentifier, id.ToString()), new(ClaimTypes.Role, role),
     new(DurableSessionAuthorization.EpochClaim, "1"), new("account_version", AccountSession.Version(hash)),
     new(DurableSessionAuthorization.SubjectClaim, sub.ToString()), new(VersionedSessionAuthorization.SecurityVersionClaim, generation.ToString())], "synthetic-test"));

if (args[0] == "--after-restart")
{
    Check(await State(admin) == (true, false, 6L), "synthetic generation retained across separately confirmed database restart");
    Console.WriteLine($"{passed} post-restart checks passed; operator must independently verify restart occurred.");
    return;
}
await using (var c = await source.OpenConnectionAsync())
await using (var tx = await c.BeginTransactionAsync())
{
    await using var empty = new NpgsqlCommand("SELECT (SELECT count(*) FROM public.customers)+(SELECT count(*) FROM public.admin_users)+(SELECT count(*) FROM public.account_identities)+(SELECT count(*) FROM public.account_security_generations)", c, tx);
    if ((long)(await empty.ExecuteScalarAsync())! != 0) throw new InvalidOperationException("Synthetic fixture is not empty; do not reset it.");
    foreach (var (id, role, sub, email) in new[] {
        (owner,"SuperAdmin",subject,"owner@synthetic.invalid"),
        (admin,"Admin",Guid.Parse("22000000-0000-4000-8000-000000000002"),"admin@synthetic.invalid"),
        (admin2,"Admin",Guid.Parse("22000000-0000-4000-8000-000000000003"),"admin2@synthetic.invalid") })
    {
        await using var cmd = new NpgsqlCommand("""
            WITH u AS (INSERT INTO public.admin_users(app_id,username,email,role,is_active,password_hash)
                       VALUES(@id,@email,@email,@role,true,@hash) RETURNING id)
            INSERT INTO public.account_identities(id,admin_id,login,auth_state,supabase_user_id,credential_epoch,credential_sync_state)
            SELECT @id,u.id,@email,'Supabase',@subject,1,'Ready' FROM u;
            INSERT INTO public.account_security_generations(account_id,generation) VALUES(@id,1);
            """, c, tx);
        cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("email", email); cmd.Parameters.AddWithValue("role", role);
        cmd.Parameters.AddWithValue("hash", hash); cmd.Parameters.AddWithValue("subject", sub);
        await cmd.ExecuteNonQueryAsync();
    }
    await tx.CommitAsync();
}
var factory = new PostgresAccountAuthorizationTransactionFactory(source);
var service = new AccountAuthorizationTransactionService(factory, TimeProvider.System);
var principal = Ticket(owner, "SuperAdmin", subject, 1);
async Task<bool> Change(Guid id, long generation, AccountAuthorizationAction action, ClaimsPrincipal? actor = null) =>
    await service.ChangeAsync(actor ?? principal, id, generation, action,
        action == AccountAuthorizationAction.Lock ? DateTimeOffset.UtcNow.AddMinutes(10) : null);
Check(await Change(admin, 1, AccountAuthorizationAction.Disable) && await State(admin) == (false, false, 2L), "disable and generation");
Check(!await Change(admin, 1, AccountAuthorizationAction.Reactivate) && await State(admin) == (false, false, 2L), "stale denied without writes");
Check(await Change(admin, 2, AccountAuthorizationAction.Reactivate) && await State(admin) == (true, false, 3L), "reactivate and generation");
Check(await Change(admin, 3, AccountAuthorizationAction.RevokeSessions) && await State(admin) == (true, false, 4L), "session revocation");
foreach (var fault in new[] { "after-apply", "advance-error" })
{
    var faulty = new AccountAuthorizationTransactionService(new FaultFactory(factory, fault), TimeProvider.System);
    Check(!await faulty.ChangeAsync(principal, admin, 4, AccountAuthorizationAction.Disable) &&
        await State(admin) == (true, false, 4L), fault + " rolls back both changes");
}
var concurrent = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Change(admin, 4, AccountAuthorizationAction.RevokeSessions)));
Check(concurrent.Count(x => x) == 1 && await State(admin) == (true, false, 5L), "concurrent stale update has one winner");
Check(!await Change(owner, 1, AccountAuthorizationAction.Disable) && await State(owner) == (true, false, 1L), "protected SuperAdmin denied");
var adminTicket = Ticket(admin2, "Admin", Guid.Parse("22000000-0000-4000-8000-000000000003"), 1);
Check(!await Change(admin, 5, AccountAuthorizationAction.Disable, adminTicket) && await State(admin) == (true, false, 5L), "unauthorized Admin denied");
await using (var c = await source.OpenConnectionAsync())
await using (var tx = await c.BeginTransactionAsync())
{
    await using var cmd = new NpgsqlCommand("""
        UPDATE public.admin_users SET is_active=false WHERE app_id=@id;
        UPDATE public.account_security_generations SET generation=2 WHERE account_id=@id AND generation=1
        """, c, tx);
    cmd.Parameters.AddWithValue("id", owner); await cmd.ExecuteNonQueryAsync(); await tx.CommitAsync();
}
var disabledTicket = Ticket(owner, "SuperAdmin", subject, 2);
Check(!await Change(admin, 5, AccountAuthorizationAction.Disable, disabledTicket) &&
    await State(admin) == (true, false, 5L) && await State(owner) == (false, false, 2L), "disabled actor denied without target changes");
await using (var c = await source.OpenConnectionAsync())
await using (var tx = await c.BeginTransactionAsync())
{
    await using var activate = new NpgsqlCommand(
        "UPDATE public.admin_users SET is_active=true WHERE app_id=@id AND role='SuperAdmin' AND is_active=false", c, tx);
    activate.Parameters.AddWithValue("id", owner);
    if (await activate.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("Synthetic owner state changed.");
    await using var generation = new NpgsqlCommand(
        "UPDATE public.account_security_generations SET generation=3 WHERE account_id=@id AND generation=2", c, tx);
    generation.Parameters.AddWithValue("id", owner);
    if (await generation.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("Synthetic owner generation changed.");
    await tx.CommitAsync();
}
Check(await State(owner) == (true, false, 3L), "synthetic owner restored with advanced generation");
principal = Ticket(owner, "SuperAdmin", subject, 3);
Check(await Change(admin2, 1, AccountAuthorizationAction.Lock) && await State(admin2) == (true, true, 2L), "lock and generation");
var unknownCommit = new AccountAuthorizationTransactionService(new FaultFactory(factory, "after-commit"), TimeProvider.System);
Check(!await unknownCommit.ChangeAsync(principal, admin, 5, AccountAuthorizationAction.RevokeSessions) &&
    await State(admin) == (true, false, 6L), "lost commit acknowledgement is not reported as success");
Console.WriteLine($"{passed} checks passed; synthetic records retained. No provider, crash or restart test performed.");
}
catch (Exception)
{
    Console.Error.WriteLine("The isolated test could not be confirmed. Review the test target and logs without exposing credentials.");
    Environment.ExitCode = 1;
}

static string? TargetFor(string[] arguments)
{
    if (arguments.Length is < 1 or > 2 || arguments[0] is not ("--exercise" or "--after-restart")) return null;
    if (arguments.Length == 1) return "glam_authz_sprint3";
    return arguments[1] == "--retry" ? "glam_authz_sprint3_retry" : null;
}

static NpgsqlConnectionStringBuilder ConnectionSettings(string password, string database)
{
    if (database is not ("glam_authz_sprint3" or "glam_authz_sprint3_retry"))
        throw new InvalidOperationException("Unsafe test database.");
    return new() { Host = "127.0.0.1", Port = 5432, Database = database, Username = "glam_authz_test",
        Password = password, Pooling = false, Timeout = 5 };
}

sealed class FaultFactory(IAccountAuthorizationTransactionFactory inner, string fault) : IAccountAuthorizationTransactionFactory
{
    public async Task<IAccountAuthorizationTransaction> BeginAsync(CancellationToken ct) => new FaultTransaction(await inner.BeginAsync(ct), fault);
    private sealed class FaultTransaction(IAccountAuthorizationTransaction inner, string fault) : IAccountAuthorizationTransaction
    {
        public Task<(VersionedAccountSecurity? Actor, VersionedAccountSecurity? Target)> LockAccountsAsync(Guid actor, Guid target, CancellationToken ct) => inner.LockAccountsAsync(actor, target, ct);
        public Task<long?> AdvanceAsync(Guid id, long generation, CancellationToken ct) =>
            fault == "advance-error" ? throw new IOException("synthetic database failure") : inner.AdvanceAsync(id, generation, ct);
        public async Task<bool> ApplyAsync(Guid target, AccountAuthorizationAction action, DateTimeOffset? until, CancellationToken ct)
        {
            var result = await inner.ApplyAsync(target, action, until, ct);
            return fault == "after-apply" ? false : result;
        }
        public async Task CommitAsync(CancellationToken ct)
        {
            await inner.CommitAsync(ct);
            if (fault == "after-commit") throw new IOException("synthetic lost acknowledgement");
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
