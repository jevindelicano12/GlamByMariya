using System.Text.RegularExpressions;

// Static source-contract checks only. This project deliberately has no database
// provider, connection string, app configuration, or environment lookup.
var root = FindRepositoryRoot(Directory.GetCurrentDirectory());
string Read(string path) => File.ReadAllText(Path.Combine(root, path));
var passed = 0;
var failed = 0;

void Check(bool value, string message)
{
    if (value) { passed++; Console.WriteLine("PASS (static only): " + message); }
    else { failed++; Console.WriteLine("FAIL (static only): " + message); }
}

var provisioningSql = Read("Backend/GlamByMariya.Infrastructure/Data/Sql/AdminInvitationProvisioning.sql");
var identitySql = Read("Backend/GlamByMariya.Infrastructure/Data/Sql/AuthIdentityTracking.sql");
var publicSql = Read("Backend/GlamByMariya.Infrastructure/Data/Sql/PublicIntegration.sql");
var rolesSql = Read("Backend/GlamByMariya.Infrastructure/Data/Sql/RolesAndInvitations.sql");
var migration = Read("Backend/GlamByMariya.Infrastructure/Migrations/Public/20261027170000_AdminInvitationProvisioning.cs");
var identityMigration = Read("Backend/GlamByMariya.Infrastructure/Migrations/Public/20261019160000_AuthIdentityTracking.cs");
var rolesMigration = Read("Backend/GlamByMariya.Infrastructure/Migrations/Public/20261017090000_RolesAndInvitations.cs");
var store = Read("Backend/GlamByMariya.Infrastructure/Data/PublicStudioStore.cs");
var interfaceSource = Read("Backend/GlamByMariya.Infrastructure/Data/IStudioStore.cs");
var fixture = Read("tests/AdminProvisioningPostgresHarnessPreparation/SyntheticFixture.sql");
var pendingFixture = Read("tests/AdminProvisioningPostgresHarnessPreparation/SyntheticPendingData.sql");
var plan = Read("tests/AdminProvisioningPostgresHarnessPreparation/README.md");
var harnessProject = Read("tests/AdminProvisioningPostgresHarnessPreparation/AdminProvisioningPostgresHarnessPreparation.csproj");
var harnessSource = Read("tests/AdminProvisioningPostgresHarnessPreparation/Program.cs");

Check(MigrationId(rolesMigration) == "20261017090000_RolesAndInvitations"
    && MigrationId(identityMigration) == "20261019160000_AuthIdentityTracking"
    && MigrationId(migration) == "20261027170000_AdminInvitationProvisioning",
    "prerequisite migration IDs are in Roles/Invitations -> IdentityTracking -> provisioning order");
Check(publicSql.Contains("CREATE TABLE IF NOT EXISTS public.account_identities", StringComparison.Ordinal)
    && publicSql.Contains("admin_id bigint UNIQUE REFERENCES public.admin_users(id)", StringComparison.Ordinal)
    && rolesSql.Contains("CREATE TABLE IF NOT EXISTS public.admin_invitations", StringComparison.Ordinal)
    && rolesSql.Contains("accepted_at timestamptz", StringComparison.Ordinal),
    "checked-in public definitions establish identity/Admin and consumed-invitation relationships");
Check(identitySql.Contains("supabase_user_id uuid", StringComparison.Ordinal)
    && identitySql.Contains("auth_state varchar(20) NOT NULL DEFAULT 'Unknown'", StringComparison.Ordinal)
    && identitySql.Contains("ux_account_identities_supabase_user", StringComparison.Ordinal)
    && !provisioningSql.Contains("ux_account_identities_supabase_user", StringComparison.Ordinal),
    "provider subject index is a prerequisite and is not duplicated by provisioning SQL");
Check(provisioningSql.Contains("DEFAULT 'NotApplicable'", StringComparison.Ordinal)
    && provisioningSql.Contains("admin_invitation_id uuid", StringComparison.Ordinal)
    && provisioningSql.Contains("REFERENCES public.admin_invitations(id)", StringComparison.Ordinal)
    && provisioningSql.Contains("ux_account_identities_admin_invitation", StringComparison.Ordinal),
    "provisioning migration's columns, default, FK and invitation uniqueness match the planned fixture");
Check(provisioningSql.Contains("'NotApplicable','Pending','NotAttempted','Rejected','Unknown','Confirmed'", StringComparison.Ordinal)
    && store.Contains("admin_provisioning_state='Pending'", StringComparison.Ordinal)
    && store.Contains("admin_provisioning_state='Confirmed'", StringComparison.Ordinal)
    && store.Contains("auth_state='Supabase'", StringComparison.Ordinal),
    "SQL state domain agrees with persistent-store transitions");
Check(store.Contains("accepted_at IS NOT NULL", StringComparison.Ordinal)
    && store.Contains("admin_id={adminId}", StringComparison.Ordinal)
    && store.Contains("role='Admin'", StringComparison.Ordinal)
    && store.Contains("lower(btrim(inv.email))", StringComparison.Ordinal)
    && store.Contains("FOR UPDATE", StringComparison.Ordinal)
    && store.Contains("SET supabase_user_id={supabaseUserId}", StringComparison.Ordinal),
    "persistent store source contains accepted-invite, role, normalized-email, locking and linkage checks");
Check(interfaceSource.Contains("SupabaseProvisioningResult provisioningResult", StringComparison.Ordinal)
    && store.Contains("state == \"Confirmed\"", StringComparison.Ordinal)
    && store.Contains("!= 1", StringComparison.Ordinal),
    "confirmation contract requires structured provider proof, exact repeats and exact affected-row counts");

var requiredScenarios = new[]
{
    "Historical account preservation", "Default provisioning state", "Foreign-key enforcement",
    "Unique invitation association", "Unique provider UUID", "Pending-to-confirmed activation",
    "Incorrect invitation/account correlation", "Wrong Admin role", "Unconsumed invitation",
    "Mismatched email", "Duplicate provider subjects", "Exact-repeat idempotency",
    "Conflicting repeated confirmation", "Transaction rollback", "Concurrent confirmation attempts"
};
Check(requiredScenarios.All(scenario => plan.Contains(scenario, StringComparison.OrdinalIgnoreCase)),
    "verification plan specifies every required runtime scenario");
Check(fixture.Contains("glam-phase217f-synthetic-only", StringComparison.Ordinal)
    && fixture.Contains("@example.invalid", StringComparison.Ordinal)
    && fixture.Contains("NotApplicable", StringComparison.Ordinal)
    && fixture.Contains("SuperAdmin", StringComparison.Ordinal)
    && pendingFixture.Contains("Pending", StringComparison.Ordinal),
    "fixture is explicitly synthetic and includes historical state-preservation rows");
Check(!harnessProject.Contains("PackageReference", StringComparison.Ordinal)
    && !Regex.IsMatch(harnessSource, @"(?m)^\s*using\s+System\.Data\b"),
    "static harness has no database-driver dependency or database API namespace");

Console.WriteLine($"Static preparation checks: {passed} passed; {failed} failed.");
Console.WriteLine("No SQL was executed and no database connection was opened.");
return failed == 0 ? 0 : 1;

static string MigrationId(string source) =>
    Regex.Match(source, @"\[Migration\(""(?<id>\d{14}_[^""]+)""\)\]").Groups["id"].Value;

static string FindRepositoryRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "MakeupSystem.slnx"))) return directory.FullName;
    throw new DirectoryNotFoundException("Run the static harness from within the repository tree.");
}
