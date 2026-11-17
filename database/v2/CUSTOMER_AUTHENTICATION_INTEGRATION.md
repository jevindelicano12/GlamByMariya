# Customer authentication application integration

Status: implemented in the actual MVC controller and application services,
**not publicly activated**. Owner now confirms hosted 0004 revision 2 passed all
ten checks and temporary membership was removed. No hosted verification/request
was performed by this preparation task. Normal durable-auth startup still throws;
only the separately configured, loopback-HTTPS `v2-customer-auth-test` profile in
the `V2CustomerAuthTest` environment can run the isolated Customer surface.
See `CONTROLLED_CUSTOMER_AUTH_TEST.md` for exact configuration and authorization.

## Application behavior

- `AddPersistentCustomerAuthentication` registers the application service for
  configured public-schema PostgreSQL. `AccountController` calls it for
  registration, OTP verification/resend, password login, logout, recovery and
  password change. The local test host uses that same registration, not
  `TestOnlyDurableSignup` or `TestOnlySignupRetry`.
- Registration validates the existing form rules, derives age from birthday,
  forces Customer/active, and protects the profile and password **hash** at rest.
  A protected HttpOnly, SameSite=Strict `/Account` cookie holds a random browser
  secret and attempt ID; the database stores the secret hash. Production cookies
  require HTTPS. No provider token or plaintext password is put in that cookie.
- Existing attempt/retry adapters serialize verification/resend, enforce cooldown
  and limit verification claims. Repeat registration cannot replace the original
  protected profile or automatically repeat provider signup.
- OTP verification resolves the returned access token through Supabase
  `GET /auth/v1/user`. Confirmed email, provider subject and expected email must
  match. Customer insert, identity initialization, operation consumption and
  initial generation commit in one serializable transaction. Completion redirects
  to password login; it does not issue an authentication cookie.
- Password login independently calls `/user`, then requires an existing active,
  unlocked, uniquely owned Customer mapping in Ready state. It never binds an
  existing provider account by email or falls back to a local password hash.
  Authoritative account/identity/epoch/generation validation precedes cookie
  issuance and runs again on authenticated requests.
- Logout uses `TrustedRequestSessionAccessor`, not browser claims or
  `HttpContext.User` alone, to advance the account generation through the existing
  revocation function. This conservatively invalidates **all local account
  cookies**, including copied tickets. It does not revoke Supabase tokens.
  The browser cookie is removed even if database revocation cannot be confirmed;
  that failure is reported rather than claimed successful.
- Password change requires the trusted request plus fresh provider password proof;
  recovery uses a provider recovery OTP. Both independently resolve `/user` and
  update only an existing Customer binding. A committed Pending/epoch transition
  invalidates prior cookies before provider mutation. Completion synchronizes the
  local hash/stamp and returns Ready. Lock order is generation, Customer, staff,
  identity; password change compares the original trusted request generation.
- Public registration never accepts a role or provisions staff. Owner-only
  private SuperAdmin provisioning and the administrator invitation containment
  gate are unchanged. This task does not implement persistent staff login.

## Exact database dependencies

Applied foundations 0001/0002 alone are insufficient. The locally validated
`migrations/0004_customer_auth_compatibility.sql` now supplies the auth contract.
Actual MVC tests use **0001 → 0002 → 0004**, with neither the identity candidate
nor `auth_fixture.sql`. Payment 0003 remains unapplied. Hosted 0004 success is
owner-confirmed: do not rerun it; retain the approved execution/cleanup evidence.

The 0004 contract includes:

1. `customers`: `app_id` UUID with uniqueness, `is_active`, `locked_until`,
   `first_name`, `last_name`, `age_at_registration`, `gender`, `address`,
   `password_hash`, `security_stamp`, `birthday`, `suffix`, and normalized-email
   uniqueness, alongside foundation `id`, `email`, `full_name`.
2. `admin_users`: numeric `id`, unique `app_id`, `username`, `email`, constrained
   `role`, `is_active`, `locked_until`, `password_hash`, `security_stamp`.
   Authoritative security reads include staff rows to detect ambiguous ownership.
3. `account_identities`: UUID `id`; exactly one unique `customer_id`/`admin_id`;
   owner/UUID composite foreign keys; unique normalized `login`; constrained
   `auth_state`; uniquely bound nullable `supabase_user_id`; positive bigint
   `credential_epoch`; constrained `credential_sync_state`
   (Unknown/Ready/Pending/ReconciliationRequired).
4. A reviewed Customer identity initialization trigger: Customer insertion must
   create the matching Unknown identity with epoch 1. Registration only promotes
   that exact owner/UUID/email row to the verified Supabase subject.
5. `account_security_generations(account_id,generation)`: retained unique identity
   reference and positive bigint generation. Never reset or reuse a generation.
6. `auth_mvc_signup_attempts`: `attempt_id`, unique 32-byte
   `browser_secret_hash`, `normalized_email`, `encrypted_profile`,
   `provider_subject`, `state`, `operation_id`, `consumed_operation_id`,
   `verification_attempts`, `next_resend_at`, `created_at`, `expires_at`.
   Includes state/operation checks, operation uniqueness, unresolved
   normalized-email uniqueness, expiry and attempt limits, plus retention policy.
7. `v2_authz_api.revoke_my_sessions(uuid,uuid,text,bigint,bigint)`, with the
   candidate's locked authoritative validation and monotonic generation update.
   The SQL function does not itself prove a protected HTTP session; only the
   trusted server boundary may supply its evidence.

### Runtime permissions and RLS

The application preflight rejects elevated/bypass/role-member database logins,
public-schema CREATE rights, missing capabilities, and any of the five auth
tables without enabled and forced RLS. This is a prerequisite check, **not**
certification of trigger behavior, uniqueness, policies or complete schema safety.

The adapters now accept column-scoped INSERT/UPDATE capabilities rather than
demanding broad table grants. The backend requires:

| Object | Required rights |
| --- | --- |
| `customers` | SELECT; column INSERT for registration fields; UPDATE on `password_hash`,`security_stamp`; USAGE on `customers_id_seq` |
| `admin_users` | SELECT; UPDATE on `app_id` for row locking, not staff provisioning |
| `account_identities` | SELECT; UPDATE on `auth_state`,`supabase_user_id`,`credential_epoch`,`credential_sync_state`; initialization INSERT belongs only to the definer trigger owner |
| `account_security_generations` | SELECT; INSERT on `account_id`,`generation`; UPDATE on `generation` for shared locking, not direct backend mutation |
| `auth_mvc_signup_attempts` | SELECT; column-scoped attempt creation and state/operation updates |
| `v2_authz_api` | schema USAGE and EXECUTE on the revocation function |

Matching forced-RLS policies must expose all ownership/identity rows needed for
global uniqueness checks, not hide collisions. Browser `anon`/`authenticated` roles
must not gain these trusted-backend rights. 0004 implements this contract without
reusing the fixture's blanket mutation grants. Immutable owner/binding, monotonic
security and consumed-attempt triggers constrain the lock/update capabilities.
Exact columns, definer-owner rights and effective API/PUBLIC denial are documented
in `CUSTOMER_AUTH_MIGRATION_PROCEDURE.md`.

Full normal application startup also needs the existing public MVC compatibility
views/settings and the production Data Protection key table, protected persistent
key ring and certificate configuration; auth 0004 does not provide them.
The isolated auth profile instead uses an external certificate-encrypted file
key ring and explicit Asia/Manila footer clock. It creates no views/settings or
grants and exposes no legacy booking/profile/settings routes.

## Interrupted operations and verification limits

Unknown signup outcomes retain Initiated attempts. Unknown/rejected OTP outcomes
remain claimed/fenced; even an expired/incorrect provider OTP is not assumed safe
to replay until real rejection semantics are verified. A provider-consumed OTP
followed by failed local commit requires reconciliation. Password uncertainty
leaves Pending or ReconciliationRequired and denies cookies. There is no automatic
provider retry, self-service repair, operator reconciliation endpoint or cleanup
job in this change.

Local evidence:

- 68 real MVC/HTTPS + disposable PostgreSQL 18 checks with fake Supabase,
  actual controller/store DI, locked account behavior and persistent-key restart.
- 45 synthetic isolated-profile/provider checks plus a local HTTPS/Razor smoke.
- 120 auth migration/permission checks against 0004, including non-superuser DDL,
  inherited/SET ROLE access, API/BYPASSRLS denial and non-destructive repeat rejection.
- 248 existing isolated HTTP checks with fake provider/query (no PostgreSQL).
- 56 signup-retry checks with in-memory store and synthetic HTTP provider.
- Web/integration builds succeeded. These are not full normal-startup or hosted proofs.
- 0004 revision 2 also passed 29 role-compatibility checks and the same 46 MVC
   checks with SQL Editor-style creator ADMIN-only memberships; this is earlier
   evidence, not a rerun during this preparation.

Legacy `DurableCredentialPostgresChecks` and `DurableMvcPostgresChecks` seed a
generation for credential tests now; they require the corresponding generation
schema in their separately approved fixtures. They were not run against retained
databases during this task.

Still required: separately approved Supabase signup/duplicate-signup responses,
email OTP/template delivery, `/user` shape, OTP consumption/rejection and resend
semantics, recovery delivery/single use, password update outcomes, HTTPS/proxy
cookies and persisted-key behavior across restart/instances.

**Readiness:** application code is ready for owner configuration and separate
controlled real-Customer test approval. The isolated profile removes unrelated
MVC/key-table prerequisites without expanding the 0004 permission contract.
Real delivery/provider behavior and owner-managed keys/credentials/TLS must still
be established and verified; public authentication/booking/payment stay closed.
Prepare the owner-only configuration from `CONTROLLED_CUSTOMER_AUTH_TEST.md`
without launching or contacting hosted services until separately authorized.
