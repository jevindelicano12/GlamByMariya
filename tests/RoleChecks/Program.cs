using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Npgsql;

// Direct service checks for the three-role system. No server, no live database.
// Exit code is nonzero on the first failure.
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) { Console.WriteLine($"FAIL: {name}"); Environment.Exit(1); }
    checks++;
    Console.WriteLine($"PASS: {name}");
}
void Reject(Action action, string name)
{
    try { action(); Console.WriteLine($"FAIL (no refusal): {name}"); Environment.Exit(1); }
    catch (InvalidOperationException) { checks++; Console.WriteLine($"PASS: {name}"); }
}

var store = new DemoStore();
var admin = store.Users.Single(u => u.Email == "admin@lumiere.demo");
Check(admin.Role == Roles.Admin, "demo seeds an Admin");
var owner = store.Users.Single(u => u.Email == "owner@lumiere.demo");
Check(owner.Role == Roles.SuperAdmin, "demo seeds a SuperAdmin");
Check(store.Users.Single(u => u.Email == "customer@lumiere.demo").Role == Roles.Customer, "demo seeds a Customer");
var hasher = new PasswordHasher<AppUser>();
var unrelatedPassword = "fixture-" + Guid.NewGuid().ToString("N");
Check(hasher.VerifyHashedPassword(admin, admin.PasswordHash, unrelatedPassword) == PasswordVerificationResult.Failed, "default synthetic Admin rejects an unrelated password");
var secondStore = new DemoStore();
Check(hasher.VerifyHashedPassword(secondStore.Users.Single(u => u.Role == Roles.Admin), secondStore.Users.Single(u => u.Role == Roles.Admin).PasswordHash, unrelatedPassword) == PasswordVerificationResult.Failed, "another synthetic store also rejects an unrelated password");
var supplied = new[] { "fixtureAdmin1", "fixtureOwner2", "fixtureCustomer3" };
foreach (var presentation in new[] { false, true })
{
    var configured = new DemoStore(passwords: supplied, presentationAccounts: presentation);
    foreach (var (role, password) in new[] { (Roles.Admin, supplied[0]), (Roles.SuperAdmin, supplied[1]), (Roles.Customer, supplied[2]) })
    {
        var user = configured.Users.Single(u => u.Role == role);
        Check(hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed, $"{(presentation ? "presentation" : "standard")} {role} accepts its supplied password");
    }
}
var presentationStore = new DemoStore(passwords: supplied, presentationAccounts: true);
var presentationAvatars = new GlamByMariya.Infrastructure.Integrations.DemoAvatarStore("unused", true);
foreach (var role in new[] { Roles.Admin, Roles.SuperAdmin, Roles.Customer })
{
    var user = presentationStore.Users.Single(u => u.Role == role);
    Check(user.Name.StartsWith("Demo ", StringComparison.Ordinal) && presentationAvatars.Supports(user), $"presentation {role} keeps its existing avatar mapping with a synthetic display name");
}
Reject(() => DatabaseConfiguration.ValidateTarget(new NpgsqlConnectionStringBuilder { Host = "db.abcdefghijklmnopqrst.supabase.co" }, null), "Supabase target without project ID is rejected");
Reject(() => DatabaseConfiguration.ValidateTarget(new NpgsqlConnectionStringBuilder { Host = "db.abcdefghijklmnopqrst.supabase.co" }, "bcdefghijklmnopqrstu"), "mismatched Supabase project is rejected");
DatabaseConfiguration.ValidateTarget(new NpgsqlConnectionStringBuilder { Host = "db.abcdefghijklmnopqrst.supabase.co" }, "abcdefghijklmnopqrst");
Check(true, "matching Supabase project is accepted without connecting");
ClaimsPrincipal Principal(AppUser user) => new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, user.Role) }, "synthetic"));
Check(AdminPermissionCatalog.CanManage(Principal(owner), admin) && !AdminPermissionCatalog.CanManage(Principal(admin), admin) && !AdminPermissionCatalog.CanManage(Principal(owner), owner), "prepared permission management is SuperAdmin-only and excludes self and SuperAdmin targets");
Check(!AdminPermissionCatalog.Has(Principal(admin), admin, "payments", null) && !AdminPermissionCatalog.Has(Principal(admin), admin, "reports", new[] { "payments" }) && AdminPermissionCatalog.Has(Principal(admin), admin, "payments", new[] { "payments" }) && AdminPermissionCatalog.Has(Principal(owner), owner, "payments", null), "prepared grant checks deny absent grants and allow only explicit Admin grants or owner access");

// Elevated creation gates.
var elevated = store.CreateElevatedUser("New", "Admin", "elevated@example.test", "fixtureElevated1", Roles.Admin, 30, "", "123 Demo Street");
Check(elevated.Role == Roles.Admin, "CreateElevatedUser mints Admin");
var super = store.CreateElevatedUser("New", "Owner", "super@example.test", "fixtureElevated1", Roles.SuperAdmin, 30, "", "123 Demo Street");
Check(super.Role == Roles.SuperAdmin, "CreateElevatedUser mints SuperAdmin");
Reject(() => store.CreateElevatedUser("No", "Way", "nope@example.test", "fixtureElevated1", Roles.Customer, 30, "", "123 Demo Street"), "CreateElevatedUser rejects Customer");
Reject(() => store.CreateElevatedUser("No", "Way", "nope2@example.test", "fixtureElevated1", "Manager", 30, "", "123 Demo Street"), "CreateElevatedUser rejects unknown role");
Reject(() => store.CreateElevatedUser("Dup", "Admin", "elevated@example.test", "fixtureElevated1", Roles.Admin, 30, "", "123 Demo Street"), "CreateElevatedUser rejects duplicate email");

// Invitations: issue, accept once, replay/wrong/expiry denied, revoke works.
var clock = new TestClock(DateTime.UtcNow);
var invites = new MemoryInvitationService(clock);
var (invite, token) = invites.InviteWithToken("future.admin@example.test", owner.Id);
Check(invites.Pending().Count == 1, "invitation is pending");
var accepted = invites.Accept("future.admin@example.test", token);
Check(accepted.Id == invite.Id, "valid token is accepted");
Reject(() => invites.Accept("future.admin@example.test", token), "invitation token is single-use");
Reject(() => invites.Accept("future.admin@example.test", "deadbeef"), "wrong token rejected");
Reject(() => invites.Accept("other@example.test", token), "token bound to invited email");
var created = store.CreateElevatedUser("Future", "Admin", accepted.Email, "fixtureElevated1", Roles.Admin, 30, "", "123 Demo Street");
Check(created.Role == Roles.Admin, "accepted invitation yields Admin, never SuperAdmin");

var (second, secondToken) = invites.InviteWithToken("second.admin@example.test", owner.Id);
clock.Advance(TimeSpan.FromHours(73));
Reject(() => invites.Accept("second.admin@example.test", secondToken), "expired invitation rejected");
Check(invites.Pending().Count == 0, "expired invitation leaves the pending list");

var (third, _) = invites.InviteWithToken("third.admin@example.test", owner.Id);
invites.Revoke(third.Id, owner.Id);
Check(invites.Pending().Count == 0, "revoked invitation leaves the pending list");
Reject(() => invites.Revoke(third.Id, owner.Id), "double revoke rejected");

Console.WriteLine($"All {checks} role and invitation checks passed.");

sealed class TestClock(DateTime start) : TimeProvider
{
    private DateTime current = start;
    public void Advance(TimeSpan span) => current += span;
    public override DateTimeOffset GetUtcNow() => new(current, TimeSpan.Zero);
}
