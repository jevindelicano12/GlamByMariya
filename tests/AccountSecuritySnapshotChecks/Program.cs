using System.Collections.Immutable;
using GlamByMariya.Infrastructure.Services;

// Actual adapter + guard source, in-memory read source only. No host, HTTP,
// database, configuration, filesystem reads, packages or persistent state.
var passed = 0;
var id = Guid.NewGuid();
var subject = Guid.NewGuid();
var version = AccountSession.Version("synthetic-snapshot-test-only");
var account = new AccountSecurityAccountRow(id, true, false, "Customer", SessionIdentityOwnerKind.Customer, version);
var identity = new AccountSecurityIdentityRow(id, id, null, AuthenticationIdentityPolicy.Supabase, subject, "Ready", 4);
var read = new AccountSecurityRead([account], [identity], true, true);
var evidence = new AccountSessionEvidence(id, "Customer", "4", version);

void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + label);
    passed++;
    Console.WriteLine("PASS (actual adapter/guard): " + label);
}

RepositoryAccountSecuritySnapshotProvider Provider(FakeReadSource source,
    SessionStoreKind store = SessionStoreKind.PublicPostgres,
    SessionAuthenticationKind auth = SessionAuthenticationKind.Supabase) => new(source, store, auth);

async Task Expect(string label, AccountSecurityRead value, AccountSessionEligibility expected,
    AccountSessionEvidence? session = null, SessionStoreKind store = SessionStoreKind.PublicPostgres,
    SessionAuthenticationKind auth = SessionAuthenticationKind.Supabase)
{
    var source = new FakeReadSource(value);
    var result = await new AccountSessionSecurityGuard(Provider(source, store, auth)).EvaluateAsync(session ?? evidence);
    Check(result.Outcome == expected && result.IsEligible == (expected == AccountSessionEligibility.Eligible), label);
    Check(source.RequestedId == id, label + " uses immutable ID");
}

foreach (var role in new[] { "Customer", "Admin", "SuperAdmin" })
{
    var owner = role == "Customer" ? SessionIdentityOwnerKind.Customer : SessionIdentityOwnerKind.Admin;
    var row = account with { Role = role, OwnerKind = owner };
    var link = role == "Customer" ? identity : identity with { CustomerAccountId = null, AdminAccountId = id };
    var value = read with { Accounts = [row], Identities = [link] };
    var loaded = await Provider(new FakeReadSource(value)).LoadAsync(id, default);
    Check(loaded is not null && loaded.Role == role && loaded.IdentityOwnerKind == owner
        && loaded.SupabaseSubject == subject && loaded.CredentialEpoch == 4 && loaded.AccountVersion == version,
        role + " snapshot preserves authoritative metadata");
    await Expect(role + " eligible", value, AccountSessionEligibility.Eligible, evidence with { Role = role });
    await Expect(role + " disabled", value with { Accounts = [row with { IsActive = false }] }, AccountSessionEligibility.AccountInactive, evidence with { Role = role });
    await Expect(role + " locked", value with { Accounts = [row with { IsLocked = true }] }, AccountSessionEligibility.AccountLocked, evidence with { Role = role });
}

await Expect("missing account", read with { Accounts = [], Identities = [] }, AccountSessionEligibility.AccountMissing);
await Expect("missing identity", read with { Identities = [] }, AccountSessionEligibility.IdentityMissing);
await Expect("duplicate identity", read with { Identities = [identity, identity] }, AccountSessionEligibility.IdentityInconsistent);
await Expect("wrong identity UUID", read with { Identities = [identity with { IdentityId = Guid.NewGuid() }] }, AccountSessionEligibility.IdentityInconsistent);
await Expect("wrong owner UUID", read with { Identities = [identity with { CustomerAccountId = Guid.NewGuid() }] }, AccountSessionEligibility.IdentityInconsistent);
await Expect("both owner columns populated", read with { Identities = [identity with { AdminAccountId = id }] }, AccountSessionEligibility.IdentityInconsistent);
await Expect("neither owner column populated", read with { Identities = [identity with { CustomerAccountId = null }] }, AccountSessionEligibility.IdentityInconsistent);
await Expect("empty owner UUID", read with { Identities = [identity with { CustomerAccountId = Guid.Empty }] }, AccountSessionEligibility.IdentityInconsistent);
await Expect("wrong physical account kind", read with { Accounts = [account with { OwnerKind = SessionIdentityOwnerKind.Admin }] }, AccountSessionEligibility.IdentityInconsistent);
await Expect("unknown physical account kind", read with { Accounts = [account with { OwnerKind = SessionIdentityOwnerKind.Unknown }] }, AccountSessionEligibility.IdentityInconsistent);
foreach (var fact in new bool?[] { false, null })
{
    await Expect("login integrity not established", read with { NormalizedLoginConsistentAndUnique = fact }, AccountSessionEligibility.IdentityInconsistent);
    await Expect("remote subject uniqueness not established", read with { SupabaseSubjectUnique = fact }, AccountSessionEligibility.IdentityInconsistent);
}
await Expect("unknown activity", read with { Accounts = [account with { IsActive = null }] }, AccountSessionEligibility.InvalidSecurityMetadata);
await Expect("unknown lock status", read with { Accounts = [account with { IsLocked = null }] }, AccountSessionEligibility.InvalidSecurityMetadata);
foreach (var state in new[] { "Pending", "ReconciliationRequired" })
{
    var value = read with { Identities = [identity with { CredentialState = state }] };
    var loaded = await Provider(new FakeReadSource(value)).LoadAsync(id, default);
    Check(loaded?.CredentialState != SessionCredentialState.Ready, "unresolved state preserved");
    await Expect("unresolved state denied", value, AccountSessionEligibility.CredentialNotReady);
}
foreach (var state in new string?[] { null, "", "ready", "Unknown", "unexpected" })
{
    var value = read with { Identities = [identity with { CredentialState = state }] };
    Check((await Provider(new FakeReadSource(value)).LoadAsync(id, default))?.CredentialState == SessionCredentialState.Unknown,
        "missing or malformed state never defaults to Ready");
    await Expect("unknown state denied", value, AccountSessionEligibility.InvalidSecurityMetadata);
}
await Expect("missing epoch", read with { Identities = [identity with { CredentialEpoch = null }] }, AccountSessionEligibility.CredentialEpochMissing);
await Expect("invalid epoch", read with { Identities = [identity with { CredentialEpoch = 0 }] }, AccountSessionEligibility.CredentialEpochInvalid);
await Expect("stale session epoch", read, AccountSessionEligibility.CredentialEpochMismatch, evidence with { CredentialEpoch = "3" });
await Expect("missing version", read with { Accounts = [account with { AccountVersion = null }] }, AccountSessionEligibility.InvalidSecurityMetadata);
foreach (var store in new[] { SessionStoreKind.Demo, SessionStoreKind.LegacyPostgres, SessionStoreKind.Unknown })
    await Expect("unsupported provider denied", read, AccountSessionEligibility.UnsupportedProviderAuthentication, store: store);
await Expect("unknown authentication denied", read, AccountSessionEligibility.UnsupportedProviderAuthentication, auth: SessionAuthenticationKind.Unknown);

await Expect("email cannot supply missing subject", read with { Identities = [identity with { SupabaseSubject = null }] }, AccountSessionEligibility.IdentityInconsistent);
await Expect("Unknown never inferred as verified ownership", read with { Identities = [identity with { AuthState = AuthenticationIdentityPolicy.Unknown }] }, AccountSessionEligibility.IdentityInconsistent);
var local = read with { Identities = [identity with { AuthState = AuthenticationIdentityPolicy.LegacyOnly, SupabaseSubject = null }] };
await Expect("explicit local identity", local, AccountSessionEligibility.Eligible, auth: SessionAuthenticationKind.LocalPassword);
await Expect("linked identity cannot use local fallback", read, AccountSessionEligibility.IdentityInconsistent, auth: SessionAuthenticationKind.LocalPassword);

foreach (var value in new[]
{
    read with { Accounts = [account, account] },
    read with { Accounts = [account with { AccountId = Guid.NewGuid() }] },
    read with { Accounts = [] }
})
{
    var provider = Provider(new FakeReadSource(value));
    try { await provider.LoadAsync(id, default); Check(false, "invalid account mapping must fail"); }
    catch (AccountSecurityReadException error)
    {
        Check(error.Failure == AccountSecurityReadFailure.InvalidAccountMapping && error.InnerException == null,
            "invalid or ambiguous account mapping has explicit sanitized failure");
    }
    Check((await new AccountSessionSecurityGuard(provider).EvaluateAsync(evidence)).Outcome == AccountSessionEligibility.RepositoryUnavailable,
        "invalid account read cannot produce eligible snapshot");
}

foreach (var value in new[]
{
    read with { Accounts = default }, read with { Identities = default },
    read with { Accounts = [null!] }, read with { Identities = [null!] }
})
    await Expect("incomplete repository result denies", value, AccountSessionEligibility.RepositoryUnavailable);

var failingSource = new FakeReadSource(read);
var failingProvider = Provider(failingSource);
var guard = new AccountSessionSecurityGuard(failingProvider);
Check((await guard.EvaluateAsync(evidence)).IsEligible, "initial successful read");
foreach (var failure in new Exception[] { new InvalidOperationException("synthetic failure"), new TimeoutException(), new OperationCanceledException() })
{
    failingSource.Failure = failure;
    try { await failingProvider.LoadAsync(id, default); Check(false, "repository failure must throw"); }
    catch (AccountSecurityReadException error)
    {
        Check(error.Failure == AccountSecurityReadFailure.RepositoryUnavailable && error.InnerException == null
            && error.Message == "Account security read denied.", "repository failure sanitized without fallback");
    }
    Check((await guard.EvaluateAsync(evidence)).Outcome == AccountSessionEligibility.RepositoryUnavailable,
        "failure after success never reuses Ready snapshot");
}
failingSource.Failure = null;
failingSource.Value = null!;
Check((await guard.EvaluateAsync(evidence)).Outcome == AccountSessionEligibility.RepositoryUnavailable, "null read is failure not account missing");
try { await failingProvider.LoadAsync(Guid.Empty, default); Check(false, "empty request must fail"); }
catch (AccountSecurityReadException error) { Check(error.Failure == AccountSecurityReadFailure.InvalidRequest, "empty ID rejected explicitly"); }
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    var source = new FakeReadSource(read);
    try { await Provider(source).LoadAsync(id, cancellation.Token); Check(false, "cancelled read must fail"); }
    catch (AccountSecurityReadException error)
    {
        Check(error.Failure == AccountSecurityReadFailure.RepositoryUnavailable && source.RequestedId == null, "cancelled read denies before source access");
    }
}

var immutableSource = new FakeReadSource(read);
var immutableProvider = Provider(immutableSource);
var before = await immutableProvider.LoadAsync(id, default);
immutableSource.Value = read with { Identities = [identity with { CredentialState = "Pending", CredentialEpoch = 5 }] };
var after = await immutableProvider.LoadAsync(id, default);
Check(before?.CredentialState == SessionCredentialState.Ready && before.CredentialEpoch == 4
    && after?.CredentialState == SessionCredentialState.Pending && after.CredentialEpoch == 5,
    "new reads cannot mutate earlier immutable snapshots");

Console.WriteLine($"Actual adapter/guard checks: {passed} passed; 0 failed; 0 skipped.");
Console.WriteLine("No PostgreSQL repository, cookie middleware, remote proof or durable enforcement exercised.");

sealed class FakeReadSource(AccountSecurityRead value) : IAccountSecurityReadSource
{
    public AccountSecurityRead Value { get; set; } = value;
    public Exception? Failure { get; set; }
    public Guid? RequestedId { get; private set; }
    public Task<AccountSecurityRead> ReadAsync(Guid accountId, CancellationToken cancellationToken)
    {
        RequestedId = accountId;
        if (Failure != null) throw Failure;
        return Task.FromResult(Value);
    }
}
