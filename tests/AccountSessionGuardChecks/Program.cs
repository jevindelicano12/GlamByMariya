using GlamByMariya.Core.BusinessRules;

// Actual application guard under test. Providers below are in-memory only;
// no application host, HTTP client, database, configuration, or filesystem I/O.
var passed = 0;
var id = Guid.NewGuid();
var version = AccountSession.Version("synthetic-test-input-only");
var snapshot = new AccountSecuritySnapshot(id, true, false, "Customer",
    SessionStoreKind.PublicPostgres, SessionAuthenticationKind.Supabase,
    1, id, id, SessionIdentityOwnerKind.Customer, true,
    AuthenticationIdentityPolicy.Supabase, Guid.NewGuid(), SessionCredentialState.Ready, 7, version);
var evidence = new AccountSessionEvidence(id, "Customer", "7", version);

void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + label);
    passed++;
    Console.WriteLine("PASS (actual guard): " + label);
}

async Task Expect(string label, AccountSecuritySnapshot? value, AccountSessionEvidence? session,
    AccountSessionEligibility expected)
{
    var fake = new FakeSnapshotProvider(value);
    var result = await new AccountSessionSecurityGuard(fake).EvaluateAsync(session);
    Check(result.Outcome == expected && result.IsEligible == (expected == AccountSessionEligibility.Eligible), label);
    if (session is { AccountId: var accountId } && accountId != Guid.Empty)
        Check(fake.RequestedId == accountId, label + " uses immutable lookup ID");
}

foreach (var role in new[] { "Customer", "Admin", "SuperAdmin" })
{
    var owner = role == "Customer" ? SessionIdentityOwnerKind.Customer : SessionIdentityOwnerKind.Admin;
    var value = snapshot with { Role = role, IdentityOwnerKind = owner };
    await Expect(role + " eligible", value, evidence with { Role = role }, AccountSessionEligibility.Eligible);
    await Expect(role + " disabled", value with { IsActive = false }, evidence with { Role = role }, AccountSessionEligibility.AccountInactive);
    Check(value.Role == role && value.IsActive == true, role + " immutable snapshot preserved");
}

await Expect("missing account", null, evidence, AccountSessionEligibility.AccountMissing);
await Expect("locked account", snapshot with { IsLocked = true }, evidence, AccountSessionEligibility.AccountLocked);
await Expect("unknown activity", snapshot with { IsActive = null }, evidence, AccountSessionEligibility.InvalidSecurityMetadata);
await Expect("unknown lock status", snapshot with { IsLocked = null }, evidence, AccountSessionEligibility.InvalidSecurityMetadata);
await Expect("missing identity", snapshot with { IdentityCount = 0, IdentityId = null }, evidence, AccountSessionEligibility.IdentityMissing);
await Expect("duplicate identity", snapshot with { IdentityCount = 2 }, evidence, AccountSessionEligibility.IdentityInconsistent);
await Expect("unknown identity count", snapshot with { IdentityCount = null }, evidence, AccountSessionEligibility.IdentityInconsistent);
await Expect("wrong identity UUID", snapshot with { IdentityId = Guid.NewGuid() }, evidence, AccountSessionEligibility.IdentityInconsistent);
await Expect("empty identity UUID", snapshot with { IdentityId = Guid.Empty }, evidence, AccountSessionEligibility.IdentityInconsistent);
await Expect("wrong identity owner", snapshot with { IdentityOwnerAccountId = Guid.NewGuid() }, evidence, AccountSessionEligibility.IdentityInconsistent);
await Expect("missing identity owner", snapshot with { IdentityOwnerAccountId = null }, evidence, AccountSessionEligibility.IdentityInconsistent);
await Expect("wrong owner kind", snapshot with { IdentityOwnerKind = SessionIdentityOwnerKind.Admin }, evidence, AccountSessionEligibility.IdentityInconsistent);
await Expect("inconsistent mapping", snapshot with { IdentityMappingConsistent = false }, evidence, AccountSessionEligibility.IdentityInconsistent);
await Expect("unknown mapping integrity", snapshot with { IdentityMappingConsistent = null }, evidence, AccountSessionEligibility.IdentityInconsistent);
foreach (var store in new[] { SessionStoreKind.Unknown, SessionStoreKind.LegacyPostgres, SessionStoreKind.Demo, (SessionStoreKind)999 })
{
    foreach (var auth in new[] { SessionAuthenticationKind.LocalPassword, SessionAuthenticationKind.Supabase })
        await Expect("unsupported store/authentication combination", snapshot with { Store = store, Authentication = auth }, evidence,
            AccountSessionEligibility.UnsupportedProviderAuthentication);
}
await Expect("unknown authentication", snapshot with { Authentication = SessionAuthenticationKind.Unknown }, evidence, AccountSessionEligibility.UnsupportedProviderAuthentication);
await Expect("invalid authentication enum", snapshot with { Authentication = (SessionAuthenticationKind)999 }, evidence, AccountSessionEligibility.UnsupportedProviderAuthentication);
await Expect("unknown provider classification", snapshot with { AuthState = AuthenticationIdentityPolicy.Unknown }, evidence, AccountSessionEligibility.IdentityInconsistent);
await Expect("missing provider classification", snapshot with { AuthState = null }, evidence, AccountSessionEligibility.IdentityInconsistent);
await Expect("missing remote subject", snapshot with { SupabaseSubject = null }, evidence, AccountSessionEligibility.IdentityInconsistent);
await Expect("empty remote subject", snapshot with { SupabaseSubject = Guid.Empty }, evidence, AccountSessionEligibility.IdentityInconsistent);
var legacy = snapshot with { Authentication = SessionAuthenticationKind.LocalPassword, AuthState = AuthenticationIdentityPolicy.LegacyOnly, SupabaseSubject = null };
await Expect("explicit public local-only identity", legacy, evidence, AccountSessionEligibility.Eligible);
await Expect("linked identity cannot use local fallback", legacy with { SupabaseSubject = Guid.NewGuid() }, evidence, AccountSessionEligibility.IdentityInconsistent);
await Expect("Unknown is not legacy permission", legacy with { AuthState = AuthenticationIdentityPolicy.Unknown }, evidence, AccountSessionEligibility.IdentityInconsistent);
await Expect("Supabase classification cannot use local fallback", legacy with { AuthState = AuthenticationIdentityPolicy.Supabase }, evidence, AccountSessionEligibility.IdentityInconsistent);
foreach (var state in new[] { SessionCredentialState.Pending, SessionCredentialState.ReconciliationRequired })
    await Expect("unresolved credential state", snapshot with { CredentialState = state }, evidence, AccountSessionEligibility.CredentialNotReady);
foreach (var state in new[] { SessionCredentialState.Unknown, (SessionCredentialState)999 })
    await Expect("unknown credential state never becomes Ready", snapshot with { CredentialState = state }, evidence, AccountSessionEligibility.InvalidSecurityMetadata);
await Expect("missing authoritative epoch", snapshot with { CredentialEpoch = null }, evidence, AccountSessionEligibility.CredentialEpochMissing);
await Expect("missing ticket epoch", snapshot, evidence with { CredentialEpoch = null }, AccountSessionEligibility.CredentialEpochMissing);
foreach (var epoch in new long[] { 0, -1 })
    await Expect("invalid authoritative epoch", snapshot with { CredentialEpoch = epoch }, evidence, AccountSessionEligibility.CredentialEpochInvalid);
foreach (var epoch in new[] { "", " ", " 7", "+7", "-1", "0", "7.0", "abc", "9223372036854775808" })
    await Expect("malformed ticket epoch", snapshot, evidence with { CredentialEpoch = epoch }, AccountSessionEligibility.CredentialEpochInvalid);
await Expect("stale epoch", snapshot, evidence with { CredentialEpoch = "6" }, AccountSessionEligibility.CredentialEpochMismatch);
await Expect("future epoch", snapshot, evidence with { CredentialEpoch = "8" }, AccountSessionEligibility.CredentialEpochMismatch);
await Expect("wrong lookup result ID", snapshot with { AccountId = Guid.NewGuid() }, evidence, AccountSessionEligibility.AccountIdMismatch);
await Expect("empty lookup result ID", snapshot with { AccountId = Guid.Empty }, evidence, AccountSessionEligibility.AccountIdMismatch);
await Expect("empty requested ID", snapshot, evidence with { AccountId = Guid.Empty }, AccountSessionEligibility.AccountIdMismatch);
await Expect("missing evidence", snapshot, null, AccountSessionEligibility.AccountIdMismatch);
await Expect("role mismatch", snapshot, evidence with { Role = "SuperAdmin" }, AccountSessionEligibility.RoleMismatch);
await Expect("missing ticket role", snapshot, evidence with { Role = null }, AccountSessionEligibility.RoleMismatch);
await Expect("unknown authoritative role", snapshot with { Role = "Unrecognized" }, evidence, AccountSessionEligibility.InvalidSecurityMetadata);
await Expect("missing authoritative role", snapshot with { Role = null }, evidence, AccountSessionEligibility.InvalidSecurityMetadata);
await Expect("version mismatch", snapshot, evidence with { AccountVersion = AccountSession.Version("another-synthetic-input") }, AccountSessionEligibility.AccountVersionMismatch);
await Expect("missing authoritative version", snapshot with { AccountVersion = null }, evidence, AccountSessionEligibility.InvalidSecurityMetadata);
await Expect("missing ticket version", snapshot, evidence with { AccountVersion = null }, AccountSessionEligibility.InvalidSecurityMetadata);
await Expect("malformed version", snapshot, evidence with { AccountVersion = "invalid" }, AccountSessionEligibility.InvalidSecurityMetadata);

foreach (var failure in new Exception[] { new InvalidOperationException("synthetic"), new TimeoutException("synthetic"), new OperationCanceledException() })
{
    var result = await new AccountSessionSecurityGuard(new ThrowingSnapshotProvider(failure)).EvaluateAsync(evidence);
    Check(!result.IsEligible && result.Outcome == AccountSessionEligibility.RepositoryUnavailable, "provider failure denies without exposing exception");
}
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    var fake = new FakeSnapshotProvider(snapshot);
    var result = await new AccountSessionSecurityGuard(fake).EvaluateAsync(evidence, cancellation.Token);
    Check(!result.IsEligible && result.Outcome == AccountSessionEligibility.RepositoryUnavailable && fake.RequestedId == null,
        "cancelled lookup denies without provider access");
}
Check(!default(AccountSessionDecision).IsEligible, "default decision denies");

// Exercise real guard across fake authoritative snapshots, not a fake cookie gate.
var transitions = new FakeSnapshotProvider(snapshot);
var guard = new AccountSessionSecurityGuard(transitions);
Check((await guard.EvaluateAsync(evidence)).IsEligible, "Ready current epoch eligible");
transitions.Snapshot = snapshot with { CredentialState = SessionCredentialState.Pending, CredentialEpoch = 8 };
Check((await guard.EvaluateAsync(evidence)).Outcome == AccountSessionEligibility.CredentialNotReady, "Pending denies existing evidence");
transitions.Snapshot = transitions.Snapshot with { CredentialState = SessionCredentialState.Ready };
Check((await guard.EvaluateAsync(evidence)).Outcome == AccountSessionEligibility.CredentialEpochMismatch, "Ready after rejection does not revive old epoch");
Check((await guard.EvaluateAsync(evidence with { CredentialEpoch = "8" })).IsEligible, "current epoch eligible after rejection");
transitions.Snapshot = transitions.Snapshot with { CredentialState = SessionCredentialState.ReconciliationRequired };
Check((await guard.EvaluateAsync(evidence with { CredentialEpoch = "8" })).Outcome == AccountSessionEligibility.CredentialNotReady, "reconciliation state denies current epoch too");

// Separate guard objects simulate instances reading one shared authoritative source.
// This does not implement the durable store or cookie middleware.
foreach (var role in new[] { "Customer", "Admin", "SuperAdmin" })
{
    var owner = role == "Customer" ? SessionIdentityOwnerKind.Customer : SessionIdentityOwnerKind.Admin;
    var state = new FakeSnapshotProvider(snapshot with { Role = role, IdentityOwnerKind = owner });
    var instanceA = new AccountSessionSecurityGuard(state);
    var instanceB = new AccountSessionSecurityGuard(state);
    var firstSession = evidence with { Role = role };
    var secondSession = evidence with { Role = role };
    var before = await Task.WhenAll(instanceA.EvaluateAsync(firstSession), instanceB.EvaluateAsync(secondSession));
    Check(before.All(result => result.IsEligible), role + " simulated instances accept two current sessions");
    state.Snapshot = state.Snapshot! with { CredentialState = SessionCredentialState.Pending, CredentialEpoch = 8 };
    var pending = await Task.WhenAll(instanceA.EvaluateAsync(firstSession), instanceB.EvaluateAsync(secondSession));
    Check(pending.All(result => result.Outcome == AccountSessionEligibility.CredentialNotReady), role + " simulated instances deny both sessions during uncertainty");
    state.Snapshot = state.Snapshot with { CredentialState = SessionCredentialState.Ready };
    var after = await Task.WhenAll(instanceA.EvaluateAsync(firstSession), instanceB.EvaluateAsync(secondSession));
    Check(after.All(result => result.Outcome == AccountSessionEligibility.CredentialEpochMismatch), role + " old sessions stay denied after simulated reconciliation");
    Check((await instanceB.EvaluateAsync(firstSession with { CredentialEpoch = "8" })).IsEligible,
        role + " simulated new session uses current epoch");
}

Console.WriteLine($"Actual guard checks: {passed} passed; 0 failed; 0 skipped.");
Console.WriteLine("Cookie middleware, issuance, durable storage and provider proof are NOT exercised; guard remains unregistered.");

sealed class FakeSnapshotProvider(AccountSecuritySnapshot? snapshot) : IAccountSecuritySnapshotProvider
{
    public AccountSecuritySnapshot? Snapshot { get; set; } = snapshot;
    public Guid? RequestedId { get; private set; }
    public Task<AccountSecuritySnapshot?> LoadAsync(Guid accountId, CancellationToken cancellationToken)
    {
        RequestedId = accountId;
        return Task.FromResult(Snapshot);
    }
}

sealed class ThrowingSnapshotProvider(Exception failure) : IAccountSecuritySnapshotProvider
{
    public Task<AccountSecuritySnapshot?> LoadAsync(Guid accountId, CancellationToken cancellationToken) => throw failure;
}
