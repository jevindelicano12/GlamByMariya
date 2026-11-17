using System.Text.Json;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;

// Actual reader, adapter and guard source, with a fake query boundary. No live
// transport is instantiated. JSON contains only generated test account metadata.
var passed = 0;
var skipped = 0;
var id = Guid.NewGuid();
var subject = Guid.NewGuid();
const string syntheticHash = "synthetic-reader-input-only";
var evidence = new AccountSessionEvidence(id, "Customer", "3", AccountSession.Version(syntheticHash));

void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + label);
    passed++;
    Console.WriteLine("PASS (actual reader/adapter/guard): " + label);
}

Dictionary<string, object?> Account(string role = "Customer") => new()
{
    ["id"] = id, ["kind"] = role == "Customer" ? "Customer" : "Admin", ["role"] = role,
    ["active"] = true, ["locked"] = false, ["password_hash"] = syntheticHash
};
Dictionary<string, object?> Identity(string role = "Customer") => new()
{
    ["id"] = id, ["customer"] = role == "Customer" ? id : null, ["admin"] = role == "Customer" ? null : id,
    ["auth_state"] = "Supabase", ["subject"] = subject, ["state"] = "Ready", ["epoch"] = 3L
};
Dictionary<string, object?> Payload(string role = "Customer") => new()
{
    ["metadata_available"] = true, ["accounts"] = new[] { Account(role) },
    ["identities"] = new[] { Identity(role) }, ["login_integrity"] = true, ["subject_unique"] = true
};
PostgresAccountSecurityReadSource Reader(FakeQuery query) => new(query, "public");

async Task Expect(string label, Dictionary<string, object?> payload, AccountSessionEligibility expected, string role = "Customer")
{
    var query = new FakeQuery(JsonSerializer.Serialize(payload));
    var provider = new RepositoryAccountSecuritySnapshotProvider(Reader(query), SessionStoreKind.PublicPostgres, SessionAuthenticationKind.Supabase);
    var decision = await new AccountSessionSecurityGuard(provider).EvaluateAsync(evidence with { Role = role });
    Check(decision.Outcome == expected && decision.IsEligible == (expected == AccountSessionEligibility.Eligible), label);
    Check(query.Calls == 1 && query.AccountId == id && query.Sql == PostgresAccountSecurityReadSource.Sql,
        label + " uses single fixed query and UUID parameter");
}

async Task ExpectReaderFailure(string label, string payload, PostgresSecurityReadFailure expected)
{
    try { await Reader(new FakeQuery(payload)).ReadAsync(id, default); Check(false, label); }
    catch (PostgresSecurityReadException error)
    {
        Check(error.Failure == expected && error.InnerException == null
            && error.Message == "PostgreSQL account security read denied.", label);
    }
}

foreach (var role in new[] { "Customer", "Admin", "SuperAdmin" })
{
    await Expect(role + " valid owner mapping", Payload(role), AccountSessionEligibility.Eligible, role);
    var account = Account(role);
    account["active"] = false;
    var payload = Payload(role);
    payload["accounts"] = new[] { account };
    await Expect(role + " disabled status preserved", payload, AccountSessionEligibility.AccountInactive, role);
    account["active"] = true;
    account["locked"] = true;
    await Expect(role + " locked status preserved", payload, AccountSessionEligibility.AccountLocked, role);
}

var missing = Payload();
missing["accounts"] = Array.Empty<object>();
missing["identities"] = Array.Empty<object>();
await Expect("completed missing-account read", missing, AccountSessionEligibility.AccountMissing);
var missingIdentity = Payload();
missingIdentity["identities"] = Array.Empty<object>();
await Expect("missing identity denied", missingIdentity, AccountSessionEligibility.IdentityMissing);
var duplicate = Payload();
duplicate["identities"] = new[] { Identity(), Identity() };
await Expect("duplicate identities denied", duplicate, AccountSessionEligibility.IdentityInconsistent);
duplicate["accounts"] = new[] { Account(), Account() };
await Expect("duplicate accounts denied", duplicate, AccountSessionEligibility.RepositoryUnavailable);
var orphan = Payload();
orphan["accounts"] = Array.Empty<object>();
await Expect("orphan identity without account denied", orphan, AccountSessionEligibility.RepositoryUnavailable);

foreach (var mutation in new Action<Dictionary<string, object?>>[]
{
    row => row["id"] = Guid.NewGuid(),
    row => row["customer"] = Guid.NewGuid(),
    row => row["customer"] = Guid.Empty,
    row => row["admin"] = id,
    row => row["customer"] = null,
    row => row["subject"] = null,
    row => row["subject"] = Guid.Empty,
    row => row["auth_state"] = "Unknown"
})
{
    var identity = Identity();
    mutation(identity);
    var payload = Payload();
    payload["identities"] = new[] { identity };
    await Expect("malformed ownership/link denied", payload, AccountSessionEligibility.IdentityInconsistent);
}
foreach (var key in new[] { "login_integrity", "subject_unique" })
{
    foreach (var flag in new bool?[] { false, null })
    {
        var payload = Payload();
        payload[key] = flag;
        await Expect("unverified global integrity denied", payload, AccountSessionEligibility.IdentityInconsistent);
    }
}
foreach (var state in new[] { "Pending", "ReconciliationRequired" })
{
    var identity = Identity();
    identity["state"] = state;
    var payload = Payload();
    payload["identities"] = new[] { identity };
    await Expect("unresolved state preserved and denied", payload, AccountSessionEligibility.CredentialNotReady);
}
foreach (var state in new string?[] { null, "", "ready", "invalid" })
{
    var identity = Identity();
    identity["state"] = state;
    var payload = Payload();
    payload["identities"] = new[] { identity };
    await Expect("missing/unknown state never Ready", payload, AccountSessionEligibility.InvalidSecurityMetadata);
}
foreach (var epoch in new long?[] { null, 0, 4 })
{
    var identity = Identity();
    identity["epoch"] = epoch;
    var payload = Payload();
    payload["identities"] = new[] { identity };
    var expected = epoch == null ? AccountSessionEligibility.CredentialEpochMissing
        : epoch == 0 ? AccountSessionEligibility.CredentialEpochInvalid : AccountSessionEligibility.CredentialEpochMismatch;
    await Expect("epoch is never invented", payload, expected);
}
foreach (var key in new[] { "active", "locked", "password_hash" })
{
    var account = Account();
    account[key] = null;
    var payload = Payload();
    payload["accounts"] = new[] { account };
    await Expect("missing account metadata denied", payload, AccountSessionEligibility.InvalidSecurityMetadata);
}
var metadataMissing = Payload();
metadataMissing["metadata_available"] = false;
// No accounts/identities need be parsed when the schema lacks required columns.
await ExpectReaderFailure("absent sync columns explicitly unavailable", "{\"metadata_available\":false}", PostgresSecurityReadFailure.MetadataUnavailable);
await Expect("missing sync schema cannot produce Ready snapshot", metadataMissing, AccountSessionEligibility.RepositoryUnavailable);
foreach (var malformed in new[] { "not-json", "null", "{}", "{\"metadata_available\":true}", "{\"metadata_available\":\"true\"}" })
    await ExpectReaderFailure("malformed query result denied", malformed, PostgresSecurityReadFailure.InvalidResult);
var wrongId = Account();
wrongId["id"] = Guid.NewGuid();
var wrongResult = Payload();
wrongResult["accounts"] = new[] { wrongId };
await ExpectReaderFailure("wrong account result ID denied", JsonSerializer.Serialize(wrongResult), PostgresSecurityReadFailure.InvalidResult);
var invalidEpoch = Identity();
invalidEpoch["epoch"] = "3";
var invalidResult = Payload();
invalidResult["identities"] = new[] { invalidEpoch };
await ExpectReaderFailure("incorrect epoch result type denied", JsonSerializer.Serialize(invalidResult), PostgresSecurityReadFailure.InvalidResult);

var failingQuery = new FakeQuery(JsonSerializer.Serialize(Payload()));
var failingReader = Reader(failingQuery);
Check((await failingReader.ReadAsync(id, default)).Accounts.Length == 1, "initial fake-query read succeeds");
foreach (var error in new Exception[] { new TimeoutException(), new InvalidOperationException("synthetic"), new OperationCanceledException() })
{
    failingQuery.Failure = error;
    try { await failingReader.ReadAsync(id, default); Check(false, "query failure denied"); }
    catch (PostgresSecurityReadException failure)
    {
        Check(failure.Failure == PostgresSecurityReadFailure.RepositoryUnavailable && failure.InnerException == null,
            "query failure never reuses successful data or exposes exception");
    }
}
foreach (var schema in new[] { "makeup", "PUBLIC", "", "other" })
{
    var query = new FakeQuery("");
    try { _ = new PostgresAccountSecurityReadSource(query, schema); Check(false, "unsupported schema rejected"); }
    catch (PostgresSecurityReadException failure)
    {
        Check(failure.Failure == PostgresSecurityReadFailure.UnsupportedSchema && query.Calls == 0, "non-public schema rejected without query");
    }
}
var cancelledQuery = new FakeQuery("");
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try { await Reader(cancelledQuery).ReadAsync(id, cancellation.Token); Check(false, "cancelled read denied"); }
    catch (PostgresSecurityReadException failure)
    {
        Check(failure.Failure == PostgresSecurityReadFailure.RepositoryUnavailable && cancelledQuery.Calls == 0, "cancellation denies before query");
    }
}
var emptyQuery = new FakeQuery("");
try { await Reader(emptyQuery).ReadAsync(Guid.Empty, default); Check(false, "empty request denied"); }
catch (AccountSecurityReadException failure)
{
    Check(failure.Failure == AccountSecurityReadFailure.InvalidRequest && emptyQuery.Calls == 0, "empty UUID never queried");
}

// SQL-contract assertions are static evidence, NOT database execution evidence.
var sql = PostgresAccountSecurityReadSource.Sql;
Check(sql.Contains("FROM public.customers") && sql.Contains("FROM public.admin_users") && sql.Contains("FROM public.account_identities"), "SQL scopes all account tables to public");
Check(sql.Contains("WHERE app_id=@account_id") && !sql.Contains(id.ToString()), "SQL parameterizes immutable account UUID");
Check(sql.Contains("WHERE i.id=@account_id") && sql.Contains("OR i.customer_id IN") && sql.Contains("OR i.admin_id IN"), "candidate selection uses identity/physical-owner IDs");
Check(!sql.Contains("JOIN public.account_identities i ON") && !sql.Contains("WHERE i.login=@"), "candidate selection is not an email join");
Check(sql.Contains("lower(btrim(i.login))") && sql.Contains("FROM all_accounts a WHERE a.login=") && sql.Contains("FROM public.account_identities x WHERE lower"), "global normalized-login checks included");
Check(sql.Contains("to_jsonb(x)->>'supabase_user_id'=to_jsonb(i)->>'supabase_user_id'"), "global linked subject uniqueness checked");
Check(sql.Contains("locked_until > statement_timestamp()") && !sql.Contains("AT TIME ZONE"), "lockout uses server timestamptz instant and strict expiry boundary");
Check(sql.Contains("information_schema.columns") && sql.Contains("data_type='bigint'") && sql.Contains("to_jsonb(i)->>'credential_sync_state'"), "metadata capability detection avoids direct absent-column reference");
Check(!sql.Contains("'Ready'") && !sql.Contains("FOR UPDATE") && !sql.Contains("LIMIT 1"), "SQL never invents Ready, locks rows or hides duplicates");
Check(!System.Text.RegularExpressions.Regex.IsMatch(sql, @"\b(INSERT|UPDATE|DELETE|ALTER|CREATE|DROP|TRUNCATE)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "fixed statement contains no write or DDL keywords");
Check(sql.Contains("CASE WHEN u.role IN ('Admin','SuperAdmin')") && sql.Contains("ELSE NULL END"), "unknown admin roles are not silently elevated");

foreach (var pending in new[]
{
    "PostgreSQL syntax/types and real catalog capability detection",
    "Actual SQL identity joins, normalization, orphan/duplicate and uniqueness cases",
    "Actual timestamptz expiry, time zones and concurrent MVCC updates",
    "Npgsql parameter binding, read-only transaction, permissions/RLS and transport failures"
})
{
    skipped++;
    Console.WriteLine("SKIP (requires separately authorized isolated PostgreSQL): " + pending);
}
Console.WriteLine($"Reader preparation checks: {passed} passed; 0 failed; {skipped} database-enforcement groups skipped.");
Console.WriteLine("No database connection or application host opened. SQL assertions are not SQL execution tests.");

sealed class FakeQuery(string json) : IPostgresAccountSecurityQuery
{
    public int Calls { get; private set; }
    public string? Sql { get; private set; }
    public Guid? AccountId { get; private set; }
    public Exception? Failure { get; set; }
    public Task<string> ExecuteAsync(string sql, Guid accountId, CancellationToken cancellationToken)
    {
        Calls++;
        Sql = sql;
        AccountId = accountId;
        if (Failure != null) throw Failure;
        return Task.FromResult(json);
    }
}
