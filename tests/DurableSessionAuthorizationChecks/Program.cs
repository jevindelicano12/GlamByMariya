using System.Security.Claims;
using GlamByMariya.Infrastructure.Services;

var id = Guid.NewGuid(); var subject = Guid.NewGuid(); var version = AccountSession.Version("synthetic");
var state = new FakeProvider();
var a = new DurableSessionAuthorization(state); var b = new DurableSessionAuthorization(state);
void Check(bool result, string label) { if (!result) throw new Exception(label); Console.WriteLine("PASS " + label); }
ClaimsPrincipal Ticket(AccountSessionEvidence evidence, bool duplicate = false) => new(new ClaimsIdentity(
    [new(ClaimTypes.NameIdentifier, evidence.AccountId.ToString()), new(ClaimTypes.Role, evidence.Role!),
     new(DurableSessionAuthorization.EpochClaim, evidence.CredentialEpoch!), new("account_version", evidence.AccountVersion!),
     new(DurableSessionAuthorization.SubjectClaim, subject.ToString()),
     .. (duplicate ? new[] { new Claim(DurableSessionAuthorization.EpochClaim, evidence.CredentialEpoch!) } : [])], "synthetic"));

foreach (var role in new[] { "Customer", "Admin", "SuperAdmin" })
{
    state.Value = new(id, true, false, role, SessionStoreKind.PublicPostgres, SessionAuthenticationKind.Supabase,
        1, id, id, role == "Customer" ? SessionIdentityOwnerKind.Customer : SessionIdentityOwnerKind.Admin,
        true, AuthenticationIdentityPolicy.Supabase, subject, SessionCredentialState.Ready, 1, version);
    var login = await a.PrepareSupabaseLogin(id, subject, role);
    Check(login != null, role + " verified-subject login prepares evidence");
    var first = Ticket(login!); var second = Ticket(login!);
    Check(await a.ValidateCookie(first) && await b.ValidateCookie(second), role + " two simulated sessions accepted");
    Check(!await a.ValidateCookie(Ticket(login!, true)), role + " duplicate epoch denied");
    var missing = Ticket(login!); ((ClaimsIdentity)missing.Identity!).RemoveClaim(missing.FindFirst(DurableSessionAuthorization.SubjectClaim)!);
    Check(!await a.ValidateCookie(missing), role + " missing subject denied");
    var mismatch = Ticket(login!); var identity = (ClaimsIdentity)mismatch.Identity!; identity.RemoveClaim(mismatch.FindFirst(DurableSessionAuthorization.SubjectClaim)!); identity.AddClaim(new(DurableSessionAuthorization.SubjectClaim, Guid.NewGuid().ToString()));
    Check(!await a.ValidateCookie(mismatch), role + " mismatched subject denied");
    state.Value = state.Value with { SupabaseSubject = Guid.NewGuid() };
    Check(!await a.ValidateCookie(first), role + " subject changed after login denies old session");
    state.Value = state.Value with { SupabaseSubject = subject };
    Check(await a.PrepareSupabaseLogin(id, Guid.NewGuid(), role) == null, role + " subject mismatch denied");
    state.Value = state.Value with { AuthState = AuthenticationIdentityPolicy.Unknown };
    Check(await a.PrepareSupabaseLogin(id, subject, role) == null, role + " unknown identity denied");
    state.Value = state.Value with { AuthState = AuthenticationIdentityPolicy.Supabase, CredentialState = SessionCredentialState.Pending, CredentialEpoch = 2 };
    Check(!await a.ValidateCookie(first) && !await b.ValidateCookie(second) && await a.PrepareSupabaseLogin(id, subject, role) == null, role + " pending denies sessions and login");
    state.Value = state.Value with { CredentialState = SessionCredentialState.ReconciliationRequired };
    Check(!await b.ValidateCookie(second) && await b.PrepareSupabaseLogin(id, subject, role) == null, role + " reconciliation denies sessions and login");
    state.Value = state.Value with { CredentialState = SessionCredentialState.Ready };
    Check(!await a.ValidateCookie(first) && !await b.ValidateCookie(second), role + " old epochs remain denied");
    var next = await b.PrepareSupabaseLogin(id, subject, role);
    Check(next != null && await a.ValidateCookie(Ticket(next)), role + " new current epoch accepted");
    state.Value = state.Value with { IsActive = false };
    Check(!await a.ValidateCookie(Ticket(next!)) && await a.PrepareSupabaseLogin(id, subject, role) == null, role + " disabled account denied");
    state.Value = state.Value with { IsActive = true, Role = role == "Admin" ? "SuperAdmin" : "Admin", IdentityOwnerKind = SessionIdentityOwnerKind.Admin };
    Check(!await a.ValidateCookie(Ticket(next!)) && await a.PrepareSupabaseLogin(id, subject, role) == null, role + " changed role rejects old session and stale login role");
}
state.Fail = true;
Check(!await a.ValidateCookie(Ticket(new(id, "Customer", "1", version))) && await a.PrepareSupabaseLogin(id, subject, "Customer") == null,
    "snapshot failure denies cookie and login");
Check(!await a.ValidateCookie(new ClaimsPrincipal()), "anonymous cookie denied");
Console.WriteLine("Fake snapshots and principals only; no MVC, provider proof, database or cross-process checks.");

sealed class FakeProvider : IAccountSecuritySnapshotProvider
{
    public AccountSecuritySnapshot? Value { get; set; }
    public bool Fail { get; set; }
    public Task<AccountSecuritySnapshot?> LoadAsync(Guid accountId, CancellationToken ct) => Fail ? throw new IOException("synthetic") : Task.FromResult(Value);
}
