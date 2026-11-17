using System.Security.Claims;
using GlamByMariya.Core.BusinessRules;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;

var ownerId = Guid.NewGuid(); var adminId = Guid.NewGuid(); var ownerSubject = Guid.NewGuid();
var version = AccountSession.Version("synthetic");
AccountSecuritySnapshot Account(Guid id, string role, Guid subject, bool active = true) =>
    new(id, active, false, role, SessionStoreKind.PublicPostgres, SessionAuthenticationKind.Supabase,
        1, id, id, SessionIdentityOwnerKind.Admin, true, AuthenticationIdentityPolicy.Supabase,
        subject, SessionCredentialState.Ready, 1, version);
var owner = new VersionedAccountSecurity(Account(ownerId, "SuperAdmin", ownerSubject), 1);
var admin = new VersionedAccountSecurity(Account(adminId, "Admin", Guid.NewGuid()), 1);
ClaimsPrincipal Ticket(VersionedAccountSecurity a, Guid subject) => new(new ClaimsIdentity(
    [new(ClaimTypes.NameIdentifier, a.Account.AccountId.ToString()), new(ClaimTypes.Role, a.Account.Role!),
     new(DurableSessionAuthorization.EpochClaim, "1"), new("account_version", version),
     new(DurableSessionAuthorization.SubjectClaim, subject.ToString()),
     new(VersionedSessionAuthorization.SecurityVersionClaim, a.SecurityVersion.ToString())], "synthetic"));
var principal = Ticket(owner, ownerSubject);
var store = new FakeFactory(owner, admin);
var service = new AccountAuthorizationTransactionService(store, TimeProvider.System);
var passed = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
async Task<bool> Change(AccountAuthorizationAction action, long generation = 1, ClaimsPrincipal? actor = null,
    Guid? target = null, CancellationToken ct = default) =>
    await service.ChangeAsync(actor ?? principal, target ?? adminId, generation, action,
        action == AccountAuthorizationAction.Lock ? DateTimeOffset.UtcNow.AddHours(1) : null, ct);
void Reset() => store.Reset(owner, admin);

Check(await Change(AccountAuthorizationAction.Disable) && store.Target!.Account.IsActive == false && store.Target.SecurityVersion == 2,
    "owner disables Admin and increments generation together");
Check(await Change(AccountAuthorizationAction.Reactivate, 2) && store.Target!.Account.IsActive == true && store.Target.SecurityVersion == 3,
    "owner reactivates inactive Admin with new generation");
Check(await Change(AccountAuthorizationAction.Lock, 3) && store.Target!.Account.IsLocked == true && store.Target.SecurityVersion == 4,
    "owner locks Admin and increments generation");
Reset();
Check(await Change(AccountAuthorizationAction.RevokeSessions) && store.Target!.Account.IsActive == true && store.Target.SecurityVersion == 2,
    "explicit revocation increments generation without changing status");
Check(!await Change(AccountAuthorizationAction.Disable) && store.Target!.Account.IsActive == true && store.Target.SecurityVersion == 2,
    "stale generation denied without mutation");
Reset();
Check(!await Change(AccountAuthorizationAction.Disable, actor: Ticket(admin, admin.Account.SupabaseSubject!.Value)) &&
    store.Target!.Account.IsActive == true && store.Target.SecurityVersion == 1, "Admin cannot manage another account");
Check(!await Change(AccountAuthorizationAction.Disable, target: ownerId) && store.Owner.SecurityVersion == 1,
    "SuperAdmin self and ownership protected");
store.Target = admin with { Account = admin.Account with { Role = "SuperAdmin" } };
Check(!await Change(AccountAuthorizationAction.Disable), "SuperAdmin target denied");
Reset();
store.Owner = owner with { Account = owner.Account with { IsActive = false } };
Check(!await Change(AccountAuthorizationAction.Disable) && store.Target!.SecurityVersion == 1, "disabled actor denied");
Reset();
store.Owner = owner with { Account = owner.Account with { IsLocked = true } };
Check(!await Change(AccountAuthorizationAction.Disable) && store.Target!.SecurityVersion == 1, "locked actor denied");
Reset();
store.Owner = owner with { SecurityVersion = 2 };
Check(!await Change(AccountAuthorizationAction.Disable) && store.Target!.SecurityVersion == 1, "stale actor session denied");
Reset();
store.Owner = owner with { Account = owner.Account with { IsActive = false }, SecurityVersion = 2 };
var disabledOwnerTicket = Ticket(store.Owner, ownerSubject);
Check(!await Change(AccountAuthorizationAction.Lock, actor: disabledOwnerTicket) && store.Target!.SecurityVersion == 1,
    "disabled owner with current generation cannot lock");
store.Owner = owner with { SecurityVersion = 3 };
Check(!await Change(AccountAuthorizationAction.Lock, actor: principal) && store.Target!.SecurityVersion == 1,
    "old owner ticket stays invalid after reactivation");
var currentOwnerTicket = Ticket(store.Owner, ownerSubject);
Check(await Change(AccountAuthorizationAction.Lock, actor: currentOwnerTicket) &&
    store.Target!.Account.IsLocked == true && store.Target.SecurityVersion == 2,
    "reactivated owner with current ticket can lock");
Reset();
Check(!await service.ChangeAsync(principal, adminId, 1, (AccountAuthorizationAction)999) && store.Target!.SecurityVersion == 1,
    "unsupported role-changing or unknown action denied");
Check(!await service.ChangeAsync(principal, adminId, long.MaxValue, AccountAuthorizationAction.RevokeSessions) && store.Target!.SecurityVersion == 1,
    "generation overflow denied");
using (var alreadyCanceled = new CancellationTokenSource())
{
    alreadyCanceled.Cancel();
    Check(!await Change(AccountAuthorizationAction.Disable, ct: alreadyCanceled.Token) && store.Target!.SecurityVersion == 1,
        "pre-cancelled request denied");
}
Reset();
store.Target = admin with { Account = admin.Account with { CredentialState = SessionCredentialState.Pending } };
Check(!await Change(AccountAuthorizationAction.Disable) && store.Target!.SecurityVersion == 1, "unresolved target credential state denied");
Reset();
var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Change(AccountAuthorizationAction.Disable))));
Check(concurrent.Count(x => x) == 1 && store.Target!.SecurityVersion == 2 && store.Target.Account.IsActive == false,
    "concurrent fake transactions have one winner");
Reset(); store.FailApply = true;
Check(!await Change(AccountAuthorizationAction.Disable) && store.Target!.SecurityVersion == 1 && store.Target.Account.IsActive == true,
    "failed account update rolls back staged generation");
Reset(); store.FailAdvance = true;
Check(!await Change(AccountAuthorizationAction.Disable) && store.Target!.SecurityVersion == 1 && store.Target.Account.IsActive == true,
    "failed revocation leaves both values unchanged");
Reset(); store.FailCommit = true;
Check(!await Change(AccountAuthorizationAction.Disable) && store.Target!.SecurityVersion == 1 && store.Target.Account.IsActive == true,
    "fake database exception rolls back state and generation");
Reset(); store.FailAfterCommit = true;
Check(!await Change(AccountAuthorizationAction.Disable) && store.Target!.SecurityVersion == 2 && store.Target.Account.IsActive == false,
    "unknown commit result never reports success even when fake commit took effect");
Reset();
using (var cts = new CancellationTokenSource())
{
    store.CancelOnApply = cts;
    Check(!await Change(AccountAuthorizationAction.Disable, ct: cts.Token) && store.Target!.SecurityVersion == 1 && store.Target.Account.IsActive == true,
        "cancellation before commit rolls back both changes");
}
Reset(); store.Target = null;
Check(!await Change(AccountAuthorizationAction.Disable), "missing account denied");
Reset();
var clock = new TestClock();
var timedService = new AccountAuthorizationTransactionService(store, clock);
store.OnLock = () => clock.Now = clock.Now.AddMinutes(2);
Check(!await timedService.ChangeAsync(principal, adminId, 1, AccountAuthorizationAction.Lock, clock.Now.AddMinutes(1)) &&
    store.Target!.SecurityVersion == 1 && store.Target.Account.IsLocked == false, "expired lock deadline after transaction wait is denied");
Reset();
Check(await store.ProtectedWrite(1), "fake protected write accepts current generation");
Check(await Change(AccountAuthorizationAction.RevokeSessions), "fake session revocation succeeds");
Check(!await store.ProtectedWrite(1) && store.Writes == 1, "fake protected write rechecks generation after revocation");
Console.WriteLine($"{passed} passed; fake transactions only, no PostgreSQL, actual cookie middleware or real protected write tested.");

sealed class FakeFactory(VersionedAccountSecurity owner, VersionedAccountSecurity target) : IAccountAuthorizationTransactionFactory
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public VersionedAccountSecurity Owner { get; set; } = owner;
    public VersionedAccountSecurity? Target { get; set; } = target;
    public bool FailApply { get; set; }
    public bool FailAdvance { get; set; }
    public bool FailCommit { get; set; }
    public bool FailAfterCommit { get; set; }
    public CancellationTokenSource? CancelOnApply { get; set; }
    public Action? OnLock { get; set; }
    public int Writes { get; private set; }
    public void Reset(VersionedAccountSecurity o, VersionedAccountSecurity t)
    { Owner = o; Target = t; FailApply = false; FailAdvance = false; FailCommit = false; FailAfterCommit = false; CancelOnApply = null; OnLock = null; Writes = 0; }
    public async Task<IAccountAuthorizationTransaction> BeginAsync(CancellationToken ct)
    { await gate.WaitAsync(ct); return new FakeTransaction(this); }
    public async Task<bool> ProtectedWrite(long generation)
    {
        await gate.WaitAsync();
        try { if (Target?.SecurityVersion != generation || Target.Account.IsActive != true || Target.Account.IsLocked != false) return false; Writes++; return true; }
        finally { gate.Release(); }
    }

    private sealed class FakeTransaction(FakeFactory store) : IAccountAuthorizationTransaction
    {
        private VersionedAccountSecurity? staged;
        public Task<(VersionedAccountSecurity? Actor, VersionedAccountSecurity? Target)> LockAccountsAsync(Guid actor, Guid target, CancellationToken ct)
        {
            store.OnLock?.Invoke();
            staged = store.Target?.Account.AccountId == target ? store.Target : null;
            return Task.FromResult<(VersionedAccountSecurity?, VersionedAccountSecurity?)>((store.Owner.Account.AccountId == actor ? store.Owner : null, staged));
        }
        public Task<long?> AdvanceAsync(Guid id, long expectedGeneration, CancellationToken ct)
        {
            if (store.FailAdvance || staged?.Account.AccountId != id || staged.SecurityVersion != expectedGeneration) return Task.FromResult<long?>(null);
            staged = staged with { SecurityVersion = checked(expectedGeneration + 1) };
            return Task.FromResult<long?>(staged.SecurityVersion);
        }
        public Task<bool> ApplyAsync(Guid id, AccountAuthorizationAction action, DateTimeOffset? lockUntil, CancellationToken ct)
        {
            if (store.FailApply || staged?.Account.AccountId != id) return Task.FromResult(false);
            staged = staged with { Account = action switch {
                AccountAuthorizationAction.Disable => staged.Account with { IsActive = false },
                AccountAuthorizationAction.Reactivate => staged.Account with { IsActive = true },
                AccountAuthorizationAction.Lock => staged.Account with { IsLocked = true },
                _ => staged.Account } };
            store.CancelOnApply?.Cancel();
            return Task.FromResult(true);
        }
        public Task CommitAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (store.FailCommit) throw new IOException("synthetic");
            store.Target = staged;
            if (store.FailAfterCommit) throw new IOException("synthetic unknown commit");
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { store.gate.Release(); return ValueTask.CompletedTask; }
    }
}

sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}
