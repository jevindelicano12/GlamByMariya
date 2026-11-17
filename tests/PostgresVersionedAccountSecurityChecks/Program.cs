using System.Security.Claims;
using System.Text.Json;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;

var id = Guid.NewGuid();
var subject = Guid.NewGuid();
var count = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL " + label);
    count++;
    Console.WriteLine("PASS " + label);
}
Dictionary<string, object?> Account(string role = "Customer") => new()
{
    ["id"] = id, ["kind"] = role == "Customer" ? "Customer" : "Admin", ["role"] = role,
    ["active"] = true, ["locked"] = false, ["password_hash"] = "synthetic"
};
Dictionary<string, object?> Identity(string role = "Customer") => new()
{
    ["id"] = id, ["customer"] = role == "Customer" ? id : null, ["admin"] = role == "Customer" ? null : id,
    ["auth_state"] = "Supabase", ["subject"] = subject, ["state"] = "Ready", ["epoch"] = 3L
};
Dictionary<string, object?> Payload(string role = "Customer") => new()
{
    ["metadata_available"] = true, ["accounts"] = new[] { Account(role) }, ["identities"] = new[] { Identity(role) },
    ["login_integrity"] = true, ["subject_unique"] = true, ["generations"] = new long[] { 7 }
};
var query = new FakeQuery();
var provider = new PostgresVersionedAccountSecurityProvider(query, "public");
var guard = new VersionedSessionAuthorization(provider);
ClaimsPrincipal Ticket(string role = "Customer", long generation = 7, string hash = "synthetic") => new(new ClaimsIdentity(
    [new(ClaimTypes.NameIdentifier, id.ToString()), new(ClaimTypes.Role, role),
     new(DurableSessionAuthorization.EpochClaim, "3"), new("account_version", AccountSession.Version(hash)),
     new(DurableSessionAuthorization.SubjectClaim, subject.ToString()),
     new(VersionedSessionAuthorization.SecurityVersionClaim, generation.ToString())], "test"));
void Set(Dictionary<string, object?> payload) => query.Json = JsonSerializer.Serialize(payload);

foreach (var role in new[] { "Customer", "Admin", "SuperAdmin" })
{
    Set(Payload(role));
    var before = query.Calls;
    var snapshot = await provider.LoadAsync(id, default);
    Check(snapshot is { SecurityVersion: 7 } && snapshot.Account.Role == role &&
        snapshot.Account.AccountVersion == AccountSession.Version("synthetic"), role + " snapshot");
    Check(query.Calls == before + 1, role + " uses one query");
    Check(await guard.ValidateCookie(Ticket(role)), role + " session accepted");
    Check(await guard.PrepareSupabaseLogin(id, subject, role) != null, role + " login evidence");
}
Check(query.Sql == PostgresAccountSecurityReadSource.VersionedSql && query.AccountId == id &&
    query.Sql.Contains("g.account_id=@account_id") && query.Sql.Contains(PostgresAccountSecurityReadSource.Sql),
    "one fixed parameterized statement includes account and generation reads");
Check(!System.Text.RegularExpressions.Regex.IsMatch(query.Sql!, @"\b(INSERT|UPDATE|DELETE|ALTER|CREATE|DROP|TRUNCATE)\b",
    System.Text.RegularExpressions.RegexOptions.IgnoreCase), "versioned statement contains no writes or DDL");

Set(Payload());
Check(!await guard.ValidateCookie(Ticket(generation: 6)), "stale generation and revoked session denied");
Check(!await guard.ValidateCookie(Ticket(hash: "old")), "stale password hash denied");
var payload = Payload();
var account = Account(); account["active"] = false; payload["accounts"] = new[] { account }; Set(payload);
Check(!await guard.ValidateCookie(Ticket()), "disabled denied");
account = Account(); account["locked"] = true; payload["accounts"] = new[] { account }; Set(payload);
Check(!await guard.ValidateCookie(Ticket()), "locked denied");
payload = Payload(); var identity = Identity(); identity["customer"] = Guid.NewGuid(); payload["identities"] = new[] { identity }; Set(payload);
Check(!await guard.ValidateCookie(Ticket()), "inconsistent owner denied");
payload = Payload(); payload["identities"] = new[] { Identity(), Identity() }; Set(payload);
Check(!await guard.ValidateCookie(Ticket()), "duplicate identities denied");
payload = Payload(); payload["subject_unique"] = false; Set(payload);
Check(!await guard.ValidateCookie(Ticket()), "nonunique subject denied");
payload = Payload(); identity = Identity(); identity["state"] = "Pending"; payload["identities"] = new[] { identity }; Set(payload);
Check(!await guard.ValidateCookie(Ticket()), "pending credentials denied");
payload = Payload(); payload["accounts"] = Array.Empty<object>(); payload["identities"] = Array.Empty<object>(); Set(payload);
Check(!await guard.ValidateCookie(Ticket()), "missing account denied");
foreach (var generations in new object[] { Array.Empty<long>(), new long[] { 0 }, new long[] { -1 }, new long[] { 7, 8 }, new string[] { "7" } })
{
    payload = Payload(); payload["generations"] = generations; Set(payload);
    Check(!await guard.ValidateCookie(Ticket()), "invalid or ambiguous generation denied");
}
payload = Payload(); payload.Remove("generations"); Set(payload);
Check(!await guard.ValidateCookie(Ticket()), "missing generation metadata denied");
payload = Payload(); payload["metadata_available"] = false; Set(payload);
Check(!await guard.ValidateCookie(Ticket()), "unavailable identity metadata denied");
query.Json = "invalid";
Check(!await guard.ValidateCookie(Ticket()), "malformed result denied");
Set(Payload());
Check(await guard.ValidateCookie(Ticket()), "valid state before database failure");
query.Failure = new IOException("sensitive connection details");
Check(!await guard.ValidateCookie(Ticket()) && await guard.PrepareSupabaseLogin(id, subject, "Customer") == null,
    "database failure denies without cached snapshot");
try { await provider.LoadAsync(id, default); throw new Exception("failure expected"); }
catch (PostgresSecurityReadException error)
{
    Check(error.Failure == PostgresSecurityReadFailure.RepositoryUnavailable && error.InnerException == null &&
        !error.Message.Contains("sensitive"), "database failure sanitized");
}
var calls = query.Calls;
try { await provider.LoadAsync(Guid.Empty, default); throw new Exception("failure expected"); }
catch (AccountSecurityReadException) { Check(query.Calls == calls, "empty id never queried"); }
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try { await provider.LoadAsync(id, cancellation.Token); throw new Exception("failure expected"); }
    catch (PostgresSecurityReadException) { Check(query.Calls == calls, "cancelled read never queried"); }
}
try { _ = new PostgresVersionedAccountSecurityProvider(query, "makeup"); throw new Exception("failure expected"); }
catch (PostgresSecurityReadException error) { Check(error.Failure == PostgresSecurityReadFailure.UnsupportedSchema, "unsupported schema denied"); }
Console.WriteLine($"{count} passed; fake query only; no PostgreSQL connection.");

sealed class FakeQuery : IPostgresAccountSecurityQuery
{
    public string Json { get; set; } = "";
    public string? Sql { get; private set; }
    public Guid AccountId { get; private set; }
    public int Calls { get; private set; }
    public Exception? Failure { get; set; }
    public Task<string> ExecuteAsync(string sql, Guid accountId, CancellationToken cancellationToken)
    {
        Calls++; Sql = sql; AccountId = accountId;
        if (Failure != null) throw Failure;
        return Task.FromResult(Json);
    }
}
