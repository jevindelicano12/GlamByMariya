# Customer MVC/PostgreSQL integration checks

Uses the real `AccountController`, `AddPersistentCustomerAuthentication`, Razor
views, antiforgery, durable cookie validation, trusted request accessor and SQL
adapters. Supabase transport is fake and rejects any host other than
`auth.example.test`; Data Protection now persists certificate-encrypted synthetic
keys in ignored `temp/` folders, and actual PublicStudioStore/controller DI replaces
the display-store shell and custom controller activator. Application rate-limit
policies are bypassed in this host; its login limiter uses a synthetic clock.
HTTPS uses a generated untrusted test certificate pinned only by the test client.
Windows imports a temporary OS-backed TLS key, removed on disposal, without
trust-store changes or writing a PFX. This does not test real provider delivery,
owner-managed production keys, full normal startup,
or rate-limit timing.

Last result: **68 MVC/HTTPS/persistence checks + 120 migration/permission checks passed** on fresh
private PostgreSQL 18 databases, using actual migrations **0001 → 0002 → 0004**.
The earlier role-compatibility run passed **29 checks + the then-current 46 MVC checks**
using roles actually created by a non-superuser SQL Editor-style operator, against
0004 local revision 2. Only PostgreSQL 18.6 was available; no hosted result implied.
No identity candidate or auth fixture is used; payment 0003 is not applied.
Covers Customer-only signup, browser-bound OTP, immutable binding, DOB/suffix,
duplicates/concurrency, forged/tampered identity, role escalation, logout,
disablement/lock expiry, stale cookies, passwords/recovery, resend, subject reuse,
rollback, uncertainty, permission/schema denial, revocation during fresh password
proof, cookie expiry, persisted keys/cookies/profiles across restart, and the
single-email isolated endpoint boundary. No extra MVC compatibility SQL is used.

Configuration/provider/HTTPS smoke checks (no PostgreSQL or real provider):

```powershell
dotnet run --project tests/CustomerAuthenticationIntegrationChecks -- --profile
```

**45** configuration/provider assertions plus real local HTTPS/Razor smoke passed.

## Run only on a new isolated cluster

From the active V2 workspace:

```powershell
.\tests\CustomerAuthenticationIntegrationChecks\Run-Disposable.ps1
```

Before rerunning:

1. Select a **new** workspace-local cluster directory and update the exact
   allowlisted `data` path in `Program.cs` and `Run-Disposable.ps1`. The last path,
   `temp/v2-customer-auth-integration-20261116-4/data`, is retained/stopped and must
   not be reused, deleted or overwritten. Earlier validation clusters are also
   retained; do not reset them.
2. The script creates a private directory ACL and fresh 64-character uppercase
   hexadecimal password, initializes SCRAM PostgreSQL 18, and starts only on
   loopback `127.0.0.1:55434` after checking the port.
3. It sets `GLAM_V2_CUSTOMER_AUTH_TEST_PASSWORD` in this process only.
   The runner independently checks the exact data directory, loopback, version,
   absent target database and absent test roles before creating anything.
4. Its `finally` block stops **that new cluster**, unsets the environment
   variable and removes only its temporary password file after a successful stop.
   Do not stop or use the original Demo or another retained test cluster.

The C# runner creates `glam_v2_customer_auth_test` plus
`glam_v2_auth_order_test`/`glam_v2_auth_repeat_test` and local-only test roles.
It simulates permissive creator defaults, inherited/SET ROLE leaks, API roles
(including BYPASSRLS service_role) and a non-superuser migration operator.
It tests safe rejection of a missing foundation, populated Customers and
reapplication; the second successful database retains catalog data. Actual role
connections verify table/column/sequence/function denial. Concurrent revocation,
duplicate provider binding and reservation-buffer rollback are also checked.
Synthetic data is retained; no database is dropped or reset.

`auth_fixture.sql` is historical scaffolding only and is no longer applied by
this harness. The C# program never initializes/stops PostgreSQL itself; use the
PowerShell wrapper for that lifecycle.

## Role compatibility

`.\tests\CustomerAuthRolePrerequisiteChecks\Run-Disposable.ps1 -Compatibility`
uses a separate private cluster on loopback port 55435. Before rerunning, select
a new exact data-directory allowlist in that wrapper and
`RoleMembershipCompatibilityChecks.cs`; the retained/stopped last cluster is
`temp/v2-role-compatibility-20261116-3/data`.

It creates a local bootstrap named supabase_admin, makes a real separate
non-superuser postgres connection, runs the exact role-only SQL Editor proposal,
and tests invalid flags/grantors/members and API ADMIN/SET/INHERIT escalation.
It then applies the local foundations and revised migration and executes
`CustomerMvcChecks` against that schema. Only the local operator has BYPASSRLS
for authoritative migration preflight; the backend/definer never do.

See `database/v2/CUSTOMER_AUTHENTICATION_INTEGRATION.md` for exact dependencies,
permissions, reconciliation limits and activation blockers. Hosted Supabase
requires separate approval; do not use this runner there.
For owner configuration and the one-synthetic-Customer procedure, use
`database/v2/CONTROLLED_CUSTOMER_AUTH_TEST.md`. Do not activate that profile here.
