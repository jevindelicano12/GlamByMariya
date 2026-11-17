using System.Security.Claims;
using GlamByMariya.Infrastructure.Services;

var id = Guid.NewGuid(); var subject = Guid.NewGuid();
var hashVersion = AccountSession.Version("synthetic");
var account = new AccountSecuritySnapshot(id, true, false, "Customer", SessionStoreKind.PublicPostgres,
    SessionAuthenticationKind.Supabase, 1, id, id, SessionIdentityOwnerKind.Customer, true,
    AuthenticationIdentityPolicy.Supabase, subject, SessionCredentialState.Ready, 1, hashVersion);
var store = new FakeStore(new(account, 1));
var guard = new VersionedSessionAuthorization(store);
var count = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
ClaimsPrincipal Ticket(AccountSessionEvidence evidence, long generation) => new(new ClaimsIdentity(
    [new(ClaimTypes.NameIdentifier, evidence.AccountId.ToString()), new(ClaimTypes.Role, evidence.Role!),
     new(DurableSessionAuthorization.EpochClaim, evidence.CredentialEpoch!), new("account_version", evidence.AccountVersion!),
     new(DurableSessionAuthorization.SubjectClaim, subject.ToString()), new(VersionedSessionAuthorization.SecurityVersionClaim, generation.ToString())], "test"));

var prepared = await guard.PrepareSupabaseLogin(id, subject, "Customer");
Check(prepared != null, "valid provider subject prepares evidence and generation");
var ticket = Ticket(prepared!.Value.Evidence, prepared.Value.SecurityVersion);
Check(await guard.ValidateCookie(ticket), "current session accepted");
store.Value = new(account with { IsActive = false }, 1);
Check(!await guard.ValidateCookie(ticket), "disabled denied");
store.Value = new(account with { IsLocked = true }, 1);
Check(!await guard.ValidateCookie(ticket), "locked denied");
store.Value = new(account with { Role = "Admin", IdentityOwnerKind = SessionIdentityOwnerKind.Admin }, 1);
Check(!await guard.ValidateCookie(ticket), "role change denies stale session");
store.Value = new(account with { SupabaseSubject = Guid.NewGuid() }, 1);
Check(!await guard.ValidateCookie(ticket) && await guard.PrepareSupabaseLogin(id, subject, "Customer") == null, "provider mismatch denied");
store.Value = new(account with { CredentialState = SessionCredentialState.Pending }, 1);
Check(!await guard.ValidateCookie(ticket), "credential guard retained");
store.Value = new(account, 0);
Check(await guard.PrepareSupabaseLogin(id, subject, "Customer") == null, "zero generation cannot issue login evidence");
store.Value = new(account, -1);
Check(!await guard.ValidateCookie(ticket), "negative generation cannot validate a cookie");
store.Value = new(account with { AccountVersion = AccountSession.Version("changed") }, 1);
Check(!await guard.ValidateCookie(ticket) && store.Value.SecurityVersion == 1, "password hash remains checked without resetting generation");
store.Value = new(account, 1);
var missingClaim = Ticket(prepared.Value.Evidence, 1); ((ClaimsIdentity)missingClaim.Identity!).RemoveClaim(missingClaim.FindFirst(VersionedSessionAuthorization.SecurityVersionClaim)!);
Check(!await guard.ValidateCookie(missingClaim), "missing generation denied");
var duplicate = Ticket(prepared.Value.Evidence, 1); ((ClaimsIdentity)duplicate.Identity!).AddClaim(new(VersionedSessionAuthorization.SecurityVersionClaim, "1"));
Check(!await guard.ValidateCookie(duplicate), "duplicate generation denied");
Check(store.Revoke(1), "fake revocation increments generation");
Check(!await guard.ValidateCookie(ticket), "revocation invalidates old session");
Check(!store.Revoke(1), "stale generation update rejected");
var competing = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => store.Revoke(2))));
Check(competing.Count(x => x) == 1 && store.Value!.SecurityVersion == 3, "fake concurrent compare-and-set has one winner");
Check(!store.ProtectedWrite(2) && store.Writes == 0 && store.ProtectedWrite(3) && store.Writes == 1, "fake protected write rechecks generation under same lock");
store.Value = null;
Check(!await guard.ValidateCookie(ticket) && await guard.PrepareSupabaseLogin(id, subject, "Customer") == null, "missing account denied");
store.Recreate(account);
Check(store.Value!.SecurityVersion == 4 && !await guard.ValidateCookie(ticket), "fake retained registry advances generation on recreation");
store.Fail = true;
Check(!await guard.ValidateCookie(ticket) && await guard.PrepareSupabaseLogin(id, subject, "Customer") == null, "repository failure denied");
Console.WriteLine($"{count} passed; fake store only, no cookie expiry, PostgreSQL, protected application write or provider tested.");

sealed class FakeStore(VersionedAccountSecurity initial) : IVersionedAccountSecurityProvider
{
    private readonly object gate = new();
    private long registry = initial.SecurityVersion;
    public VersionedAccountSecurity? Value { get; set; } = initial;
    public bool Fail { get; set; }
    public int Writes { get; private set; }
    public Task<VersionedAccountSecurity?> LoadAsync(Guid accountId, CancellationToken ct)
    { lock (gate) { if (Fail) throw new IOException(); return Task.FromResult(Value); } }
    public bool Revoke(long expected)
    { lock (gate) { if (Value == null || registry != expected || registry == long.MaxValue) return false; registry++; Value = Value with { SecurityVersion = registry }; return true; } }
    public void Recreate(AccountSecuritySnapshot account)
    { lock (gate) { registry = checked(registry + 1); Value = new(account, registry); } }
    public bool ProtectedWrite(long expected)
    { lock (gate) { if (Value == null || Value.SecurityVersion != expected || Value.Account.IsActive != true || Value.Account.IsLocked != false) return false; Writes++; return true; } }
}
