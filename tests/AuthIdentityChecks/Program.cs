using GlamByMariya.Infrastructure.Services;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }

Check(!AuthenticationIdentityPolicy.CanUseLegacyFallback(null), "Unknown identity cannot use the prepared legacy policy");
Check(!AuthenticationIdentityPolicy.CanUseLegacyFallback(AuthenticationIdentityPolicy.Unknown), "Unknown identity is not silently treated as legacy");
Check(AuthenticationIdentityPolicy.CanUseLegacyFallback(AuthenticationIdentityPolicy.LegacyOnly), "Only explicitly LegacyOnly identity may use the prepared fallback policy");
Check(!AuthenticationIdentityPolicy.CanUseLegacyFallback(AuthenticationIdentityPolicy.Supabase), "Supabase-linked identity cannot use the prepared fallback policy");
Check(AuthenticationIdentityPolicy.CanUseLegacyFallback(AuthenticationIdentityPolicy.Unknown, restrictUnknown: false), "Unknown identity keeps transitional fallback before activation");
Check(!AuthenticationIdentityPolicy.CanUseLegacyFallback(AuthenticationIdentityPolicy.Supabase, restrictUnknown: false), "Supabase identity never falls back even before broad restriction activation");
Check(!AuthenticationIdentityPolicy.CanUseLegacyFallback(AuthenticationIdentityPolicy.LegacyOnly, Guid.NewGuid(), false), "Inconsistent LegacyOnly row with Supabase ID fails closed");
var id = Guid.NewGuid();
AuthenticationIdentityPolicy.ValidateLink(null, AuthenticationIdentityPolicy.Unknown, id);
AuthenticationIdentityPolicy.ValidateLink(id, AuthenticationIdentityPolicy.Supabase, id);
var conflict = false;
try { AuthenticationIdentityPolicy.ValidateLink(Guid.NewGuid(), AuthenticationIdentityPolicy.Supabase, id); } catch (InvalidOperationException) { conflict = true; }
Check(conflict, "Conflicting Supabase identity linkage is rejected");
var source = await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "GlamByMariya.Web", "Controllers", "AccountController.cs"));
Check(source.Contains("EnableRateLimiting(RateLimitPolicies.Login)"), "Login action is rate limited");
var loginEnd = source.IndexOf("[HttpPost, EnableRateLimiting(RateLimitPolicies.Register)]", StringComparison.Ordinal);
var loginSection = source[source.IndexOf("public async Task<IActionResult> Login(string email", StringComparison.Ordinal)..loginEnd];
Check(!loginSection.Contains("LegacyFallbackAllowed", StringComparison.Ordinal), "Supabase rejection has no legacy fallback");
var changeStart = source.IndexOf("public async Task<IActionResult> ChangePassword", StringComparison.Ordinal);
var loginStart = source.IndexOf("public IActionResult Login(", changeStart, StringComparison.Ordinal);
var changeAction = source[changeStart..loginStart];
Check(changeAction.Contains("var synchronized = await AuthenticationPasswordCoordinator.UpdateSupabasePasswordAsync(", StringComparison.Ordinal)
    && changeAction.Contains("if (!synchronized)", StringComparison.Ordinal), "ChangePassword checks the coordinator's false result");
var changeFailure = changeAction.IndexOf("if (!synchronized)", StringComparison.Ordinal);
var changeFailureEnd = changeAction.IndexOf("TempData[\"Success\"] = \"Password changed.", changeFailure, StringComparison.Ordinal);
Check(changeFailure >= 0 && changeFailureEnd > changeFailure
    && changeAction[changeFailure..changeFailureEnd].Contains("await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)", StringComparison.Ordinal)
    && changeAction[changeFailure..changeFailureEnd].Contains("Supabase may have accepted the new password", StringComparison.Ordinal)
    && !changeAction[changeFailure..changeFailureEnd].Contains("password is unchanged", StringComparison.OrdinalIgnoreCase),
    "ChangePassword partial failure signs out locally, reports possible remote success, and does not claim the remote password is unchanged");
var resetStart = source.IndexOf("public async Task<IActionResult> ResetPassword(string email, string code, string newPassword,", StringComparison.Ordinal);
var inviteStart = source.IndexOf("public IActionResult AcceptInvite(", resetStart, StringComparison.Ordinal);
var resetAction = source[resetStart..inviteStart];
Check(resetAction.Contains("var synchronized = await AuthenticationPasswordCoordinator.UpdateSupabasePasswordAsync(", StringComparison.Ordinal)
    && resetAction.Contains("if (!synchronized)", StringComparison.Ordinal), "ResetPassword checks the coordinator's false result");
var resetFailure = resetAction.IndexOf("if (!synchronized)", StringComparison.Ordinal);
var resetSuccess = resetAction.IndexOf("TempData[\"Notice\"] = \"Password updated.", resetFailure, StringComparison.Ordinal);
Check(resetFailure >= 0 && resetSuccess > resetFailure
    && resetAction[resetFailure..resetSuccess].Contains("Supabase may have accepted the new password", StringComparison.Ordinal)
    && resetAction[resetFailure..resetSuccess].Contains("request another reset code", StringComparison.OrdinalIgnoreCase)
    && !resetAction[resetFailure..resetSuccess].Contains("Password updated.", StringComparison.Ordinal),
    "ResetPassword partial failure reports incomplete synchronization and possible remote success instead of success");
var limiter = new LoginAttemptLimiter();
var permits = Enumerable.Range(0, 11).Count(_ => limiter.TryAcquire("customer@example.test", "198.51.100.10"));
Check(permits == 10 && !limiter.TryAcquire("customer@example.test", "198.51.100.10"), "Account-aware login limiter stops the eleventh attempt without account lockout");
Check(limiter.TryAcquire("other@example.test", "198.51.100.10"), "Account-aware limiter distinguishes shared public-IP accounts");
var order = new List<string>();
var synchronized = await AuthenticationPasswordCoordinator.UpdateSupabasePasswordAsync(
    () => order.Add("link"), () => { order.Add("supabase"); return Task.CompletedTask; }, () => { order.Add("local"); throw new InvalidOperationException("simulated local failure"); });
Check(!synchronized && order.SequenceEqual(["link", "supabase", "local"]), "Local synchronization failure occurs only after identity linkage and Supabase update");
Check(!AuthenticationIdentityPolicy.CanUseLegacyFallback(AuthenticationIdentityPolicy.Supabase, false), "Stale local hash is unusable after successful Supabase reset/link even with restriction flag disabled");
var supabaseCalled = false;
try { await AuthenticationPasswordCoordinator.UpdateSupabasePasswordAsync(() => throw new InvalidOperationException("link conflict"), () => { supabaseCalled = true; return Task.CompletedTask; }, () => { }); } catch (InvalidOperationException) { }
Check(!supabaseCalled, "Identity-link conflict prevents Supabase password mutation");
var localCalled = false;
try { await AuthenticationPasswordCoordinator.UpdateSupabasePasswordAsync(() => { }, () => throw new InvalidOperationException("Supabase unavailable"), () => localCalled = true); } catch (InvalidOperationException) { }
Check(!localCalled, "Supabase password error does not update local hash or enable fallback");
var authHandler = new FakeAuthHandler();
var authConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Supabase:Url"] = "https://auth.example.test",
    ["Supabase:AnonKey"] = "test-only-key",
    ["Supabase:ServiceKey"] = "synthetic-service-key"
}).Build();
var auth = new SupabaseAuthService(new FakeHttpClientFactory(authHandler), authConfig, NullLogger<SupabaseAuthService>.Instance);
await auth.ResendSignupAsync("customer@example.test");
Check(authHandler.LastBody?.Contains("\"type\":\"signup\"") == true, "Resend uses the Supabase signup OTP type");
var invalidCode = false;
try { await auth.VerifyOtpAsync("customer@example.test", "000000", "signup"); } catch (InvalidOperationException e) { invalidCode = e.Message == "That code is incorrect or expired."; }
Check(invalidCode, "Invalid Supabase verification code has a recoverable generic message");
authHandler.ProvisioningResponse = "{\"id\":\"7b0e0f2a-8d25-4a42-9c52-1f6b2291f102\",\"email\":\"admin@example.test\"}";
var provisioned = await auth.CreateConfirmedUserWithResultAsync("admin@example.test", "synthetic-only-password");
Check(provisioned.IsConfirmed && provisioned.ProviderUserId == Guid.Parse("7b0e0f2a-8d25-4a42-9c52-1f6b2291f102"), "Raw GoTrue Admin response: direct user object with matching email and valid UUID is confirmed");
foreach (var invalidResponse in new[]
{
    "{\"id\":\"not-a-uuid\",\"email\":\"admin@example.test\"}",
    "{\"email\":\"admin@example.test\"}",
    "{\"id\":\"00000000-0000-0000-0000-000000000000\",\"email\":\"admin@example.test\"}",
    "{\"id\":\"7b0e0f2a-8d25-4a42-9c52-1f6b2291f102\",\"email\":\"other@example.test\"}",
    "{ malformed-json",
    "{\"user\":{\"id\":\"7b0e0f2a-8d25-4a42-9c52-1f6b2291f102\",\"email\":\"admin@example.test\"}}",
    "{\"id\":\"7b0e0f2a-8d25-4a42-9c52-1f6b2291f102\",\"email\":\"admin@example.test\",\"user\":{\"id\":\"00000000-0000-4000-8000-000000000001\",\"email\":\"admin@example.test\"}}",
    "{\"id\":\"7b0e0f2a-8d25-4a42-9c52-1f6b2291f102\",\"id\":\"00000000-0000-4000-8000-000000000001\",\"email\":\"admin@example.test\"}",
    "{\"id\":\"{7b0e0f2a-8d25-4a42-9c52-1f6b2291f102}\",\"email\":\"admin@example.test\"}"
})
{
    authHandler.ProvisioningResponse = invalidResponse;
    var result = await auth.CreateConfirmedUserWithResultAsync("admin@example.test", "synthetic-only-password");
    Check(!result.IsConfirmed && result.ProviderUserId == null && result.Failure == SupabaseProvisioningFailure.InvalidResponse,
        "Structured provisioning fails closed on malformed, wrapped, conflicting, or invalid identity response");
}
authHandler.ProvisioningStatusCode = HttpStatusCode.Conflict;
authHandler.ProvisioningResponse = "{\"msg\":\"synthetic-sensitive-provider-detail\"}";
var rejectedProvision = await auth.CreateConfirmedUserWithResultAsync("admin@example.test", "synthetic-only-password");
Check(rejectedProvision.Outcome == SupabaseProvisioningOutcome.Unconfirmed
    && rejectedProvision.Failure == SupabaseProvisioningFailure.HttpRejected
    && rejectedProvision.ProviderUserId == null
    && !rejectedProvision.ToString()!.Contains("synthetic-sensitive-provider-detail", StringComparison.Ordinal),
    "HTTP rejection is unconfirmed and provider body is not exposed");
authHandler.ProvisioningStatusCode = HttpStatusCode.BadRequest;
authHandler.ProvisioningResponse = "{\"id\":\"7b0e0f2a-8d25-4a42-9c52-1f6b2291f102\",\"email\":\"admin@example.test\"}";
var nonSuccessWithUserBody = await auth.CreateConfirmedUserWithResultAsync("admin@example.test", "synthetic-only-password");
Check(!nonSuccessWithUserBody.IsConfirmed && nonSuccessWithUserBody.ProviderUserId == null,
    "A valid-looking direct user body cannot confirm a non-success HTTP response");
authHandler.ProvisioningStatusCode = HttpStatusCode.OK;
authHandler.ThrowTransport = true;
var transportResult = await auth.CreateConfirmedUserWithResultAsync("admin@example.test", "synthetic-only-password");
Check(transportResult.Outcome == SupabaseProvisioningOutcome.TransportUncertain && transportResult.ProviderUserId == null,
    "Connection failure is classified uncertain without a provider identity");
authHandler.ThrowTransport = false;
authHandler.ThrowTimeout = true;
var timeoutResult = await auth.CreateConfirmedUserWithResultAsync("admin@example.test", "synthetic-only-password");
Check(timeoutResult.Outcome == SupabaseProvisioningOutcome.TimedOut && timeoutResult.ProviderUserId == null,
    "Timeout is classified uncertain without a provider identity");
authHandler.ThrowTimeout = false;
using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    var canceledResult = await auth.CreateConfirmedUserWithResultAsync("admin@example.test", "synthetic-only-password", cancellationToken: canceled.Token);
    Check(canceledResult.Outcome == SupabaseProvisioningOutcome.Canceled && canceledResult.ProviderUserId == null,
        "Caller cancellation is classified uncertain without a provider identity");
}
var noServiceKey = new SupabaseAuthService(new FakeHttpClientFactory(authHandler),
    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["Supabase:Url"] = "https://auth.example.test", ["Supabase:AnonKey"] = "test-only-key" }).Build(),
    NullLogger<SupabaseAuthService>.Instance);
authHandler.ResetProvisionRequestCount();
var unconfiguredResult = await noServiceKey.CreateConfirmedUserWithResultAsync("admin@example.test", "synthetic-only-password");
Check(unconfiguredResult.Outcome == SupabaseProvisioningOutcome.NotAttempted
    && unconfiguredResult.Failure == SupabaseProvisioningFailure.MissingConfiguration
    && authHandler.ProvisionRequestCount == 0, "Missing provisioning configuration is definitively not attempted");
var noAuthConfig = new SupabaseAuthService(new FakeHttpClientFactory(authHandler), new ConfigurationBuilder().Build(), NullLogger<SupabaseAuthService>.Instance);
var noAuthResult = await noAuthConfig.CreateConfirmedUserWithResultAsync("admin@example.test", "synthetic-only-password");
Check(noAuthResult.Outcome == SupabaseProvisioningOutcome.NotAttempted && noAuthResult.ProviderUserId == null,
    "Missing Supabase Auth configuration is definitively not attempted");
Check(source.Contains("RequireVerifiedBinding") && source.Contains("AuthenticationPasswordCoordinator"), "Password change/reset require an existing verified binding before Supabase mutation");
var storeSource = await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "Backend", "GlamByMariya.Infrastructure", "Data", "PublicStudioStore.cs"));
Check(!storeSource.Contains("security_stamp=gen_random_uuid(), is_active=true"), "Password reset SQL never silently reactivates an account");
var publicIntegrationSql = await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "Backend", "GlamByMariya.Infrastructure", "Data", "Sql", "PublicIntegration.sql"));
var identityTrackingSql = await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "Backend", "GlamByMariya.Infrastructure", "Data", "Sql", "AuthIdentityTracking.sql"));
var adminProvisioningSql = await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "Backend", "GlamByMariya.Infrastructure", "Data", "Sql", "AdminInvitationProvisioning.sql"));
var adminProvisioningMigration = await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "Backend", "GlamByMariya.Infrastructure", "Migrations", "Public", "20261027170000_AdminInvitationProvisioning.cs"));
var rolesMigration = await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "Backend", "GlamByMariya.Infrastructure", "Migrations", "Public", "20261017090000_RolesAndInvitations.cs"));
var identityTrackingMigration = await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "Backend", "GlamByMariya.Infrastructure", "Migrations", "Public", "20261019160000_AuthIdentityTracking.cs"));
Check(!publicIntegrationSql.Contains("admin_provisioning_state", StringComparison.Ordinal)
    && !identityTrackingSql.Contains("admin_provisioning_state", StringComparison.Ordinal)
    && !publicIntegrationSql.Contains("admin_invitation_id", StringComparison.Ordinal)
    && !identityTrackingSql.Contains("admin_invitation_id", StringComparison.Ordinal),
    "Source contract: provisioning columns remain separate from the existing base and identity-tracking migrations");
Check(identityTrackingSql.Contains("CREATE UNIQUE INDEX IF NOT EXISTS ux_account_identities_supabase_user", StringComparison.Ordinal),
    "Source contract: the provider-subject unique index is owned by AuthIdentityTracking");
Check(rolesMigration.Contains("[Migration(\"20261017090000_RolesAndInvitations\")]")
    && identityTrackingMigration.Contains("[Migration(\"20261019160000_AuthIdentityTracking\")]")
    && adminProvisioningMigration.Contains("[Migration(\"20261027170000_AdminInvitationProvisioning\")]")
    && adminProvisioningMigration.Contains("\"Data\", \"Sql\", \"AdminInvitationProvisioning.sql\"", StringComparison.Ordinal),
    "Source contract: later additive migration is ordered after both prerequisites and loads its SQL payload");
Check(adminProvisioningSql.Contains("DEFAULT 'NotApplicable'", StringComparison.Ordinal)
    && adminProvisioningSql.Contains("ADD COLUMN IF NOT EXISTS admin_invitation_id uuid", StringComparison.Ordinal),
    "Schema contract: legacy identities get an explicit safe default and nullable invitation correlation");
Check(adminProvisioningSql.Contains("Admin invitation provisioning prerequisites are incomplete", StringComparison.Ordinal)
    && adminProvisioningSql.Contains("do not match the reviewed definitions", StringComparison.Ordinal),
    "Schema contract: missing prerequisites and incompatible pre-existing columns fail closed");
Check(adminProvisioningSql.Contains("REFERENCES public.admin_invitations(id)", StringComparison.Ordinal)
    && adminProvisioningSql.Contains("ON DELETE RESTRICT", StringComparison.Ordinal)
    && adminProvisioningSql.Contains("ux_account_identities_admin_invitation", StringComparison.Ordinal),
    "Schema contract: invitation correlation is foreign-keyed and unique per local identity");
Check(adminProvisioningSql.Contains("account_identities_admin_provisioning_state_check", StringComparison.Ordinal)
    && adminProvisioningSql.Contains("'Pending','NotAttempted','Rejected','Unknown','Confirmed'", StringComparison.Ordinal)
    && adminProvisioningSql.Contains("admin_id IS NOT NULL", StringComparison.Ordinal)
    && adminProvisioningSql.Contains("supabase_user_id IS NOT NULL AND auth_state='Supabase'", StringComparison.Ordinal),
    "Schema contract: valid states, Admin correlation and confirmed identity linkage are checked");
Check(!adminProvisioningSql.Contains("ux_account_identities_supabase_user", StringComparison.Ordinal)
    && !System.Text.RegularExpressions.Regex.IsMatch(adminProvisioningSql, @"(?im)^\s*(INSERT|UPDATE|DELETE|DROP)\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
    && !adminProvisioningSql.Contains("ALTER TABLE public.admin_users", StringComparison.Ordinal)
    && !adminProvisioningSql.Contains("ALTER TABLE public.customers", StringComparison.Ordinal),
    "Schema contract: no duplicate provider index, DML, destructive SQL, or account-table changes");

// DemoStore runtime checks exercise only the in-memory implementation. They do
// not validate PostgreSQL locking, constraints, transaction rollback, or invite consumption.
var demoStore = new DemoStore();
var demoInvite = Guid.NewGuid();
var demoAdmin = demoStore.CreatePendingInvitedAdmin("Test", "Admin", "demo-admin@example.test",
    "Demo-only-password-123!", 30, "Others", "Test address", demoInvite);
var demoProvider = Guid.NewGuid();
var confirmedResult = SupabaseProvisioningResult.Confirmed(demoProvider);
demoStore.ConfirmAdminProvisioning(demoAdmin.Id, demoInvite, " DEMO-ADMIN@example.test ", confirmedResult);
Check(demoStore.GetAdminProvisioning(demoAdmin.Id) is { State: AdminProvisioningState.Confirmed, SupabaseUserId: var demoLinked } && demoLinked == demoProvider
    && demoAdmin.IsActive, "DemoStore runtime: matching pending Admin transitions to Confirmed and activates");
demoStore.ConfirmAdminProvisioning(demoAdmin.Id, demoInvite, demoAdmin.Email, confirmedResult);
Check(demoAdmin.IsActive && demoStore.GetAdminProvisioning(demoAdmin.Id)?.SupabaseUserId == demoProvider,
    "DemoStore runtime: exact confirmed repeat is idempotent");

var demoOther = demoStore.CreatePendingInvitedAdmin("Test", "Other", "demo-other@example.test",
    "Demo-only-password-123!", 30, "Others", "Test address", Guid.NewGuid());
var rejectedDemoRepeat = false;
try { demoStore.ConfirmAdminProvisioning(demoAdmin.Id, demoInvite, demoAdmin.Email, SupabaseProvisioningResult.Confirmed(Guid.NewGuid())); }
catch (InvalidOperationException) { rejectedDemoRepeat = true; }
Check(rejectedDemoRepeat && demoAdmin.IsActive, "DemoStore runtime: conflicting confirmed repeat is rejected");
var notConfirmedRejected = false;
try { demoStore.ConfirmAdminProvisioning(demoOther.Id, demoStore.GetAdminProvisioning(demoOther.Id)!.InvitationId,
    demoOther.Email, SupabaseProvisioningResult.Unconfirmed(SupabaseProvisioningOutcome.Unconfirmed, SupabaseProvisioningFailure.InvalidResponse)); }
catch (InvalidOperationException) { notConfirmedRejected = true; }
Check(notConfirmedRejected && !demoOther.IsActive
    && demoStore.GetAdminProvisioning(demoOther.Id)?.State == AdminProvisioningState.Pending,
    "DemoStore runtime: unconfirmed provider result leaves Pending account inactive");
var wrongInvitationRejected = false;
try { demoStore.ConfirmAdminProvisioning(demoOther.Id, Guid.NewGuid(), demoOther.Email, SupabaseProvisioningResult.Confirmed(Guid.NewGuid())); }
catch (InvalidOperationException) { wrongInvitationRejected = true; }
Check(wrongInvitationRejected && !demoOther.IsActive, "DemoStore runtime: mismatched invitation is rejected");
var demoWrongRole = demoStore.CreatePendingInvitedAdmin("Test", "WrongRole", "demo-role@example.test",
    "Demo-only-password-123!", 30, "Others", "Test address", Guid.NewGuid());
demoWrongRole.Role = "Customer";
var demoRoleRejected = false;
try { demoStore.ConfirmAdminProvisioning(demoWrongRole.Id, demoStore.GetAdminProvisioning(demoWrongRole.Id)!.InvitationId,
    demoWrongRole.Email, SupabaseProvisioningResult.Confirmed(Guid.NewGuid())); }
catch (InvalidOperationException) { demoRoleRejected = true; }
Check(demoRoleRejected && !demoWrongRole.IsActive, "DemoStore runtime: incorrect local role cannot be activated");

var demoConcurrentA = demoStore.CreatePendingInvitedAdmin("Test", "ParallelA", "demo-parallel-a@example.test",
    "Demo-only-password-123!", 30, "Others", "Test address", Guid.NewGuid());
var demoConcurrentB = demoStore.CreatePendingInvitedAdmin("Test", "ParallelB", "demo-parallel-b@example.test",
    "Demo-only-password-123!", 30, "Others", "Test address", Guid.NewGuid());
var demoSharedSubject = Guid.NewGuid();
var demoConcurrent = await Task.WhenAll(
    Task.Run(() => TryDemoConfirm(demoStore, demoConcurrentA, demoSharedSubject)),
    Task.Run(() => TryDemoConfirm(demoStore, demoConcurrentB, demoSharedSubject)));
Check(demoConcurrent.Count(x => x) == 1
    && (demoConcurrentA.IsActive ^ demoConcurrentB.IsActive),
    "DemoStore runtime: concurrent shared-subject confirmation has one winner");

var provisioningMethod = storeSource[storeSource.IndexOf("public void ConfirmAdminProvisioning", StringComparison.Ordinal)..];
Check(provisioningMethod.Contains("FOR UPDATE", StringComparison.Ordinal)
    && provisioningMethod.IndexOf("admin_invitations WHERE id", StringComparison.Ordinal)
       < provisioningMethod.IndexOf("account_identities WHERE id", StringComparison.Ordinal)
    && provisioningMethod.IndexOf("account_identities WHERE id", StringComparison.Ordinal)
       < provisioningMethod.IndexOf("admin_users WHERE app_id", StringComparison.Ordinal),
    "Static SQL contract only: persistent confirmation locks invitation, identity, then Admin in fixed order");
Check(provisioningMethod.Contains("accepted_at IS NOT NULL", StringComparison.Ordinal)
    && provisioningMethod.Contains("lower(btrim(inv.email))", StringComparison.Ordinal)
    && provisioningMethod.Contains("admin_id={adminId}", StringComparison.Ordinal)
    && provisioningMethod.Contains("role='Admin'", StringComparison.Ordinal)
    && provisioningMethod.Contains("is_active=false", StringComparison.Ordinal)
    && provisioningMethod.Contains("admin_provisioning_state='Pending'", StringComparison.Ordinal)
    && provisioningMethod.Contains("EnsureAdminProvisioningConstraints", StringComparison.Ordinal),
    "Static SQL contract only: persistent path checks accepted invite, normalized emails, Admin role, Pending/inactive state and installed constraints");
Check(provisioningMethod.Contains("!= 1", StringComparison.Ordinal)
    && provisioningMethod.Contains("ux_account_identities_supabase_user", StringComparison.Ordinal)
    && provisioningMethod.Contains("ux_account_identities_admin_invitation", StringComparison.Ordinal)
    && provisioningMethod.Contains("state == \"Confirmed\"", StringComparison.Ordinal),
    "Static SQL contract only: exact row counts, both uniqueness indexes and exact-repeat guard are present");
Check(!source.Contains("ConfirmAdminProvisioning(", StringComparison.Ordinal),
    "Static integration boundary: AccountController does not invoke Admin provisioning confirmation");

// Test-only transaction oracle. It validates the intended state-machine contract
// under one in-memory lock; it is NOT an actual store or PostgreSQL atomicity test.
var provisioningOracle = new ProvisioningConfirmationOracle();
var invited = provisioningOracle.AddPending("Admin", "admin@example.test");
var providerId = Guid.Parse("2baf3f44-7d27-4e8c-a531-0a9cdf7d50a1");
Check(provisioningOracle.Confirm(invited.AccountId, invited.InvitationId, invited.Email, providerId)
    && provisioningOracle.IsConfirmed(invited.AccountId, providerId),
    "Test-only oracle: matching pending Admin, invitation, email and provider UUID confirm together");
Check(provisioningOracle.Confirm(invited.AccountId, invited.InvitationId, invited.Email, providerId),
    "Test-only oracle: exact previously confirmed association is idempotent");
Check(!provisioningOracle.TryConfirm(invited.AccountId, invited.InvitationId, invited.Email, Guid.NewGuid()),
    "Test-only oracle: conflicting repeated provider identity is rejected");

var duplicateSubjectTarget = provisioningOracle.AddPending("Admin", "other@example.test");
var beforeDuplicate = provisioningOracle.Snapshot(duplicateSubjectTarget.AccountId);
Check(!provisioningOracle.TryConfirm(duplicateSubjectTarget.AccountId, duplicateSubjectTarget.InvitationId,
        duplicateSubjectTarget.Email, providerId)
    && provisioningOracle.Snapshot(duplicateSubjectTarget.AccountId) == beforeDuplicate,
    "Test-only oracle: a provider UUID linked to another local account is rejected without partial mutation");

var wrongInviteTarget = provisioningOracle.AddPending("Admin", "wrong-invite@example.test");
var beforeWrongInvite = provisioningOracle.Snapshot(wrongInviteTarget.AccountId);
Check(!provisioningOracle.TryConfirm(wrongInviteTarget.AccountId, Guid.NewGuid(), wrongInviteTarget.Email, Guid.NewGuid())
    && provisioningOracle.Snapshot(wrongInviteTarget.AccountId) == beforeWrongInvite,
    "Test-only oracle: incorrect invitation/account correlation leaves state unchanged");

var wrongRoleTarget = provisioningOracle.AddPending("Admin", "wrong-role@example.test");
provisioningOracle.ChangeRoleForTest(wrongRoleTarget.AccountId, "Customer");
var beforeWrongRole = provisioningOracle.Snapshot(wrongRoleTarget.AccountId);
Check(!provisioningOracle.TryConfirm(wrongRoleTarget.AccountId, wrongRoleTarget.InvitationId,
        wrongRoleTarget.Email, Guid.NewGuid())
    && provisioningOracle.Snapshot(wrongRoleTarget.AccountId) == beforeWrongRole,
    "Test-only oracle: a non-Admin role cannot be activated by provisioning confirmation");

var historical = provisioningOracle.AddHistorical("Admin", "historical@example.test", active: false);
Check(!provisioningOracle.TryConfirm(historical.AccountId, historical.InvitationId, historical.Email, Guid.NewGuid())
    && !provisioningOracle.IsConfirmed(historical.AccountId, Guid.Empty),
    "Test-only oracle: inactive historical Admin without invitation state cannot enter confirmation");

foreach (var outcome in new[] { "Rejected", "Unknown", "Canceled", "TimedOut" })
{
    var nonConfirmed = provisioningOracle.AddPending("Admin", outcome.ToLowerInvariant() + "@example.test");
    provisioningOracle.SetOutcomeForTest(nonConfirmed.AccountId, outcome);
    Check(!provisioningOracle.TryConfirm(nonConfirmed.AccountId, nonConfirmed.InvitationId,
            nonConfirmed.Email, Guid.NewGuid())
        && !provisioningOracle.IsConfirmed(nonConfirmed.AccountId, Guid.Empty),
        "Test-only oracle: " + outcome + " provisioning state cannot trigger activation");
}

var concurrentA = provisioningOracle.AddPending("Admin", "parallel-a@example.test");
var concurrentB = provisioningOracle.AddPending("Admin", "parallel-b@example.test");
var sharedProviderId = Guid.NewGuid();
var concurrentResults = await Task.WhenAll(
    Task.Run(() => provisioningOracle.TryConfirm(concurrentA.AccountId, concurrentA.InvitationId, concurrentA.Email, sharedProviderId)),
    Task.Run(() => provisioningOracle.TryConfirm(concurrentB.AccountId, concurrentB.InvitationId, concurrentB.Email, sharedProviderId)));
Check(concurrentResults.Count(x => x) == 1
    && provisioningOracle.IsConfirmed(concurrentA.AccountId, sharedProviderId)
        != provisioningOracle.IsConfirmed(concurrentB.AccountId, sharedProviderId),
    "Test-only oracle: concurrent claimants for one provider UUID produce one winner under the in-memory lock");
Console.WriteLine("All authentication identity preparation checks passed.");

static bool TryDemoConfirm(DemoStore store, AppUser user, Guid providerId)
{
    var invitationId = store.GetAdminProvisioning(user.Id)!.InvitationId;
    try
    {
        store.ConfirmAdminProvisioning(user.Id, invitationId, user.Email, SupabaseProvisioningResult.Confirmed(providerId));
        return true;
    }
    catch (InvalidOperationException) { return false; }
}

sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler);
}

sealed class FakeAuthHandler : HttpMessageHandler
{
    public string? LastBody { get; private set; }
    public string ProvisioningResponse { get; set; } = "{}";
    public HttpStatusCode ProvisioningStatusCode { get; set; } = HttpStatusCode.OK;
    public bool ThrowTransport { get; set; }
    public bool ThrowTimeout { get; set; }
    public int ProvisionRequestCount { get; private set; }
    public void ResetProvisionRequestCount() => ProvisionRequestCount = 0;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (request.RequestUri?.AbsolutePath.EndsWith("/admin/users", StringComparison.Ordinal) == true)
        {
            ProvisionRequestCount++;
            if (ThrowTimeout) throw new OperationCanceledException("synthetic timeout");
            if (ThrowTransport) throw new HttpRequestException("synthetic transport failure");
            return new HttpResponseMessage(ProvisioningStatusCode) { Content = new StringContent(ProvisioningResponse) };
        }
        if (request.RequestUri?.AbsolutePath.EndsWith("/verify", StringComparison.Ordinal) == true)
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"msg\":\"Invalid OTP\"}") };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
    }
}

sealed class ProvisioningConfirmationOracle
{
    public sealed record Candidate(Guid AccountId, Guid InvitationId, string Email);
    public sealed record Row(Guid AccountId, Guid InvitationId, string Email, string Role,
        bool IsActive, string State, Guid? ProviderId);
    private readonly object gate = new();
    private readonly Dictionary<Guid, Row> rows = [];

    public Candidate AddPending(string role, string email)
    {
        lock (gate)
        {
            var account = Guid.NewGuid();
            var invitation = Guid.NewGuid();
            rows.Add(account, new(account, invitation, email, role, false, "Pending", null));
            return new(account, invitation, email);
        }
    }

    public Candidate AddHistorical(string role, string email, bool active)
    {
        lock (gate)
        {
            var account = Guid.NewGuid();
            rows.Add(account, new(account, Guid.Empty, email, role, active, "NotApplicable", null));
            return new(account, Guid.Empty, email);
        }
    }

    public void ChangeRoleForTest(Guid accountId, string role)
    {
        lock (gate) rows[accountId] = rows[accountId] with { Role = role };
    }

    public void SetOutcomeForTest(Guid accountId, string state)
    {
        lock (gate) rows[accountId] = rows[accountId] with { State = state };
    }

    public bool Confirm(Guid accountId, Guid invitationId, string email, Guid providerId)
    {
        lock (gate)
        {
            if (!rows.TryGetValue(accountId, out var row)) return false;
            if (row.State == "Confirmed")
                return row.InvitationId == invitationId && row.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase)
                    && row.ProviderId == providerId && row.Role == "Admin" && row.IsActive;
            if (providerId == Guid.Empty || row.State != "Pending" || row.InvitationId != invitationId
                || row.Role != "Admin" || row.IsActive
                || !row.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase)
                || rows.Values.Any(other => other.AccountId != accountId && other.ProviderId == providerId))
                return false;
            rows[accountId] = row with { State = "Confirmed", ProviderId = providerId, IsActive = true };
            return true;
        }
    }

    public bool TryConfirm(Guid accountId, Guid invitationId, string email, Guid providerId) =>
        Confirm(accountId, invitationId, email, providerId);

    public bool IsConfirmed(Guid accountId, Guid providerId)
    {
        lock (gate) return rows.TryGetValue(accountId, out var row)
            && row.State == "Confirmed" && row.ProviderId == providerId && row.IsActive;
    }

    public Row? Snapshot(Guid accountId)
    {
        lock (gate) return rows.GetValueOrDefault(accountId);
    }
}
