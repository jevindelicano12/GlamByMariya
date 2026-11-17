// LOCAL-ONLY Phase 1.8 preparation suite.
// All accounts, cookie tickets, provider outcomes and state transitions below
// are fakes/specification tests. They do not instantiate the MVC host, open a
// database, contact Supabase/Render, or claim durable production enforcement.
using GlamByMariya.Infrastructure.Services;

var checks = 0;
var skipped = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    checks++;
    Console.WriteLine("PASS (fake/spec only): " + message);
}
void Skip(string message)
{
    skipped++;
    Console.WriteLine("SKIP (requires production implementation): " + message);
}

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var programSource = await File.ReadAllTextAsync(Path.Combine(root, "Frontend", "GlamByMariya.Web", "Program.cs"));
var accountSource = await File.ReadAllTextAsync(Path.Combine(root, "Frontend", "GlamByMariya.Web", "Controllers", "AccountController.cs"));
var migrationFiles = Directory.Exists(Path.Combine(root, "Backend", "GlamByMariya.Infrastructure", "Migrations", "Public"))
    ? Directory.GetFiles(Path.Combine(root, "Backend", "GlamByMariya.Infrastructure", "Migrations", "Public"), "*.cs")
    : [];

// Characterize the existing production coordinator with fake delegates only.
var successfulOrder = new List<string>();
var successful = await AuthenticationPasswordCoordinator.UpdateSupabasePasswordAsync(
    () => successfulOrder.Add("identity-linked"),
    () => { successfulOrder.Add("remote-updated"); return Task.CompletedTask; },
    () => successfulOrder.Add("local-synced"));
Check(successful && successfulOrder.SequenceEqual(["identity-linked", "remote-updated", "local-synced"]),
    "Existing coordinator orders link, Supabase update, local synchronization on success");

var remoteRejectedLocalCalled = false;
try
{
    await AuthenticationPasswordCoordinator.UpdateSupabasePasswordAsync(
        () => { },
        () => throw new InvalidOperationException("FAKE provider rejection"),
        () => remoteRejectedLocalCalled = true);
    Check(false, "Existing coordinator propagates a definite remote rejection");
}
catch (InvalidOperationException e) when (e.Message == "FAKE provider rejection")
{
    Check(!remoteRejectedLocalCalled, "Existing coordinator does not synchronize locally after a definite remote rejection");
}

var localSyncFailed = await AuthenticationPasswordCoordinator.UpdateSupabasePasswordAsync(
    () => { }, () => Task.CompletedTask,
    () => throw new InvalidOperationException("FAKE local sync failure"));
Check(!localSyncFailed, "Existing coordinator returns false after a simulated local synchronization failure");

var fakeRemoteChangedBeforeTimeout = false;
var localAfterAmbiguousTimeoutCalled = false;
var timeoutSynchronized = await AuthenticationPasswordCoordinator.UpdateSupabasePasswordAsync(
    () => { },
    () => { fakeRemoteChangedBeforeTimeout = true; throw new TimeoutException("FAKE response lost after remote commit"); },
    () => localAfterAmbiguousTimeoutCalled = true);
Check(!timeoutSynchronized && fakeRemoteChangedBeforeTimeout && !localAfterAmbiguousTimeoutCalled,
    "Ambiguous timeout returns incomplete without assuming remote outcome or syncing locally");

// Static assertions specifically protect Phase 1.6's controller result handling.
var changeStart = accountSource.IndexOf("public async Task<IActionResult> ChangePassword", StringComparison.Ordinal);
var loginStart = accountSource.IndexOf("public IActionResult Login(", changeStart, StringComparison.Ordinal);
var changeAction = accountSource[changeStart..loginStart];
Check(changeAction.Contains("var synchronized = await AuthenticationPasswordCoordinator.UpdateSupabasePasswordAsync(", StringComparison.Ordinal)
    && changeAction.Contains("if (!synchronized)", StringComparison.Ordinal)
    && changeAction.Contains("await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)", StringComparison.Ordinal),
    "Phase 1.6 ChangePassword checks false and signs out the current browser");

var resetStart = accountSource.IndexOf("public async Task<IActionResult> ResetPassword(string email, string code, string newPassword,", StringComparison.Ordinal);
var inviteStart = accountSource.IndexOf("public IActionResult AcceptInvite(", resetStart, StringComparison.Ordinal);
var resetAction = accountSource[resetStart..inviteStart];
Check(resetAction.Contains("var synchronized = await AuthenticationPasswordCoordinator.UpdateSupabasePasswordAsync(", StringComparison.Ordinal)
    && resetAction.Contains("if (!synchronized)", StringComparison.Ordinal)
    && resetAction.Contains("Supabase may have accepted the new password", StringComparison.Ordinal),
    "Phase 1.6 ResetPassword checks false and reports possible remote success");

// Specification-only fake model of the proposed durable synchronization gate.
// This does NOT exercise Program.OnValidatePrincipal or a PostgreSQL state field.
var roles = new[] { "Customer", "Admin", "SuperAdmin" };
foreach (var role in roles)
{
    var account = new FakeAccount(role, active: true, "hash-old");
    var oldCookie = FakeCookie.Issue(account);
    var remote = new FakeProvider("password-old");
    var ledger = new FakeSyncLedger(account);
    var completion = await FakeCoordinator.ChangeAsync(account, ledger, remote, "password-new", FakeOutcome.Success);
    Check(completion == FakeCompletion.Synchronized && account.Role == role && account.Active,
        $"Proposed success path preserves {role} role and active state");
    Check(!ledger.CookieMayAuthenticate(oldCookie), $"Proposed {role} password change rejects pre-change cookies");
    Check(ledger.CookieMayAuthenticate(FakeCookie.Issue(account)), $"Proposed {role} reconciled account can issue a new cookie");
}

{
    var account = new FakeAccount("Customer", active: true, "hash-old");
    var oldCookie = FakeCookie.Issue(account);
    var ledger = new FakeSyncLedger(account);
    var remote = new FakeProvider("password-old");
    var result = await FakeCoordinator.ChangeAsync(account, ledger, remote, "password-new", FakeOutcome.Rejected);
    Check(result == FakeCompletion.RemoteRejected && remote.Password == "password-old" && ledger.State == SyncState.Ready,
        "Proposed definite rejection restores Ready only when remote rejection is certain");
    Check(account.CredentialEpoch == oldCookie.CredentialEpoch + 1 && account.PasswordHash == oldCookie.AccountVersion,
        "Proposed definite rejection retains the incremented epoch without changing the local hash");
    Check(!ledger.CookieMayAuthenticate(oldCookie), "Proposed definite rejection never restores the prior valid session");
    var freshCookie = FakeCookie.Issue(account);
    Check(ledger.MayIssueCookie(account) && ledger.CookieMayAuthenticate(freshCookie),
        "Proposed Ready state after rejection permits a newly authenticated session at the current epoch");
    var retry = await FakeCoordinator.ChangeAsync(account, ledger, remote, "password-new", FakeOutcome.Rejected);
    Check(retry == FakeCompletion.RemoteRejected && ledger.State == SyncState.Ready
        && account.CredentialEpoch == oldCookie.CredentialEpoch + 2
        && !ledger.CookieMayAuthenticate(oldCookie) && !ledger.CookieMayAuthenticate(freshCookie),
        "Proposed repeated rejection advances the epoch again and never revives earlier cookies");
}

{
    var account = new FakeAccount("Admin", active: true, "hash-old");
    var oldCookie = FakeCookie.Issue(account);
    var ledger = new FakeSyncLedger(account);
    ledger.Begin();
    Check(ledger.State == SyncState.Pending && !ledger.CookieMayAuthenticate(oldCookie),
        "Proposed Pending state rejects existing application cookies");
    Check(!ledger.MayIssueCookie(account), "Proposed Pending state blocks new application cookies");
    var remote = new FakeProvider("password-old");
    var result = await FakeCoordinator.FinishAfterPendingLocalFailureAsync(account, ledger, remote, "password-new");
    Check(result == FakeCompletion.ReconciliationRequired && remote.Password == "password-new"
        && account.PasswordHash == "hash-old" && ledger.State == SyncState.ReconciliationRequired,
        "Proposed local-write failure records remote success and requires reconciliation");
    Check(!ledger.CookieMayAuthenticate(oldCookie) && !ledger.MayIssueCookie(account),
        "Proposed reconciliation state rejects old cookies and blocks new application sessions");
    Check(ledger.ReconcileAfterVerifiedRemotePassword(remote, "password-new"), "Proposed verified Supabase login reconciles the local hash");
    Check(ledger.State == SyncState.Ready && ledger.CookieMayAuthenticate(FakeCookie.Issue(account)),
        "Proposed reconciliation restores eligibility and permits a new cookie");
}

{
    var account = new FakeAccount("SuperAdmin", active: true, "hash-old");
    var ledger = new FakeSyncLedger(account);
    var remote = new FakeProvider("password-old");
    var result = await FakeCoordinator.ChangeAsync(account, ledger, remote, "password-new", FakeOutcome.TimeoutAfterRemoteCommit);
    Check(result == FakeCompletion.ReconciliationRequired && remote.Password == "password-new"
        && ledger.State == SyncState.ReconciliationRequired,
        "Proposed ambiguous timeout remains pending without assuming remote failure");
    Check(!ledger.MayIssueCookie(account), "Proposed ambiguous SuperAdmin state is fail-closed");
}

{
    var account = new FakeAccount("Customer", active: false, "hash-old");
    var ledger = new FakeSyncLedger(account);
    var remote = new FakeProvider("password-old");
    _ = await FakeCoordinator.ChangeAsync(account, ledger, remote, "password-new", FakeOutcome.LocalWriteFailure);
    Check(!account.Active && account.Role == "Customer", "Proposed pending state never reactivates or changes role for a disabled account");
    Check(!ledger.MayIssueCookie(account), "Proposed pending state continues to deny disabled accounts");
}

// These are deliberately skipped, not treated as passing security tests: only
// production middleware plus durable storage can prove enforcement across hosts.
var hasProductionState = programSource.Contains("CredentialSyncState", StringComparison.Ordinal)
    || programSource.Contains("PasswordSyncState", StringComparison.Ordinal);
var hasStateMigration = migrationFiles.Any(path =>
{
    var text = File.ReadAllText(path);
    return text.Contains("CredentialSyncState", StringComparison.Ordinal) || text.Contains("credential_sync_state", StringComparison.Ordinal)
        || text.Contains("password_sync_state", StringComparison.Ordinal);
});
if (!hasProductionState || !hasStateMigration)
{
    Skip("Production middleware does not yet validate a durable credential-sync state; fake cookie-gate checks above are specification tests only.");
    Skip("No credential-sync-state migration exists; cross-instance pending/reconciliation persistence cannot yet be tested against production code.");
}
else
{
    Skip("Production state integration exists; add a dedicated non-database integration harness to exercise actual middleware/store wiring before claiming enforcement.");
}

Console.WriteLine($"Preparation suite complete: {checks} fake/spec checks passed; {skipped} production-enforcement checks intentionally skipped.");

enum SyncState { Ready, Pending, ReconciliationRequired }
enum FakeOutcome { Success, Rejected, LocalWriteFailure, TimeoutAfterRemoteCommit }
enum FakeCompletion { Synchronized, RemoteRejected, ReconciliationRequired }

sealed class FakeAccount(string role, bool active, string passwordHash)
{
    public string Role { get; } = role;
    public bool Active { get; } = active;
    public string PasswordHash { get; set; } = passwordHash;
    public long CredentialEpoch { get; private set; } = 1;
    public void AdvanceEpoch() => CredentialEpoch = checked(CredentialEpoch + 1);
}

sealed record FakeCookie(string Role, bool Active, string AccountVersion, long CredentialEpoch)
{
    public static FakeCookie Issue(FakeAccount account) => new(account.Role, account.Active, account.PasswordHash, account.CredentialEpoch);
}

sealed class FakeProvider(string password)
{
    public string Password { get; private set; } = password;
    public async Task<bool> UpdateAsync(string newPassword, FakeOutcome outcome)
    {
        await Task.Yield();
        switch (outcome)
        {
            case FakeOutcome.Success: Password = newPassword; return true;
            case FakeOutcome.Rejected: return false;
            case FakeOutcome.LocalWriteFailure: Password = newPassword; return true;
            case FakeOutcome.TimeoutAfterRemoteCommit: Password = newPassword; throw new TimeoutException("Fake response lost after simulated remote commit.");
            default: throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }
}

sealed class FakeSyncLedger(FakeAccount account)
{
    public SyncState State { get; private set; } = SyncState.Ready;
    private string? PendingRemotePassword { get; set; }

    public bool CookieMayAuthenticate(FakeCookie cookie) => State == SyncState.Ready && account.Active && cookie.Active
        && cookie.Role == account.Role && cookie.AccountVersion == account.PasswordHash
        && cookie.CredentialEpoch == account.CredentialEpoch;

    public bool MayIssueCookie(FakeAccount candidate) => State == SyncState.Ready && candidate.Active && account.Active && candidate.Role == account.Role;

    public void Begin()
    {
        account.AdvanceEpoch();
        State = SyncState.Pending;
    }
    public void MarkReconciliationRequired(string candidateRemotePassword)
    {
        PendingRemotePassword = candidateRemotePassword;
        State = SyncState.ReconciliationRequired;
    }
    public void Complete(string newLocalHash)
    {
        account.PasswordHash = newLocalHash;
        PendingRemotePassword = null;
        State = SyncState.Ready;
    }
    // A definite rejection restores eligibility, never the previous session epoch.
    public void RestoreReady() => State = SyncState.Ready;

    public bool ReconcileAfterVerifiedRemotePassword(FakeProvider remote, string verifiedPassword)
    {
        if (State != SyncState.ReconciliationRequired || remote.Password != verifiedPassword || PendingRemotePassword != verifiedPassword) return false;
        Complete("hash-of-reconciled-password");
        return true;
    }
}

static class FakeCoordinator
{
    public static async Task<FakeCompletion> FinishAfterPendingLocalFailureAsync(FakeAccount account, FakeSyncLedger ledger, FakeProvider remote, string newPassword)
    {
        var updated = await remote.UpdateAsync(newPassword, FakeOutcome.LocalWriteFailure);
        if (!updated) { ledger.RestoreReady(); return FakeCompletion.RemoteRejected; }
        ledger.MarkReconciliationRequired(newPassword);
        return FakeCompletion.ReconciliationRequired;
    }

    public static async Task<FakeCompletion> ChangeAsync(FakeAccount account, FakeSyncLedger ledger, FakeProvider remote, string newPassword, FakeOutcome outcome)
    {
        var previousRemote = remote.Password;
        ledger.Begin();
        try
        {
            var updated = await remote.UpdateAsync(newPassword, outcome);
            if (!updated)
            {
                ledger.RestoreReady(); // fake definite rejection means provider guarantees no mutation
                return FakeCompletion.RemoteRejected;
            }
            if (outcome == FakeOutcome.LocalWriteFailure)
            {
                ledger.MarkReconciliationRequired(newPassword);
                return FakeCompletion.ReconciliationRequired;
            }
            ledger.Complete("hash-of-new-password");
            return FakeCompletion.Synchronized;
        }
        catch (TimeoutException)
        {
            // A timeout is ambiguous: fake provider may already have committed.
            ledger.MarkReconciliationRequired(newPassword);
            return FakeCompletion.ReconciliationRequired;
        }
        catch
        {
            if (remote.Password == previousRemote) ledger.RestoreReady();
            else ledger.MarkReconciliationRequired(newPassword);
            throw;
        }
    }
}
