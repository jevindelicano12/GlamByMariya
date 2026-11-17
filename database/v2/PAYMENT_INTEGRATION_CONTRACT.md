# V2 payment integration contract — blocked preparation

**Status:** design and isolated checks only. Do not apply 0003 or enable a
payment adapter from this document. No provider, storage bucket, credentials,
database role, Customer session or Admin account has been configured. The
current application, controllers, views and authentication gates are unchanged.

## Existing touchpoints and gaps

* `BookingService.Create` and `PublicStudioStore.SubmitBooking` are the existing
  booking/payment entry points; `BookingPricing.Calculate`,
  `ValidateExpectedQuote` and `PreparedPersistentBooking.ValidateClaim` contain
  the relevant pure pricing/claim rules. `PreparedPersistentBooking.Enabled`
  remains false. Migrations 0001–0002 do not provide the complete catalog,
  settings, snapshots, identity mappings, views or triggers those entry points
  require.
* `BookingsController.CorrectProof` retains Demo `paymentRevision` behavior.
  The persistent form now also sends `expectedPaymentRevision` and
  `expectedPaymentVersion`; the controller validates syntax before staging any
  proof and rejects every persistent correction. The service independently
  rejects persistent correction and payment verification/approval/correction
  requests. Neither gate checks a revision against a locked database row.
  `IStudioStore.CorrectPayment` and legacy persistent store implementations
  still do not receive an expected Customer revision and remain unsuitable
  for direct use. Do not connect them to 0003. A future verified path must
  carry the token into the transaction, lock, and compare authoritative state.
* Staff review currently passes `paymentId`, `reviewRevision` and
  `reviewVersion`; `CheckPaymentReview` locks the old mutable payment row and
  `ValidatePersistentPaymentReview` compares both revision and row version.
  0003 is a different, append-only model, has no equivalent existing read
  projection or `xmin` contract, and has no adapter. Define a consistent,
  non-reusable ledger revision token for both proof inspection and review;
  update the adapter and existing form's server binding together. Do not silently
  reinterpret the current token, narrow a bigint to int, or accept a missing
  version. Map stale conflicts to the existing reload/review flow.
* 0003's `payments` header is immutable and uniquely references one reservation;
  the current adapter expects mutable `payments` columns, a down-payment purpose,
  revision/history views and potentially later balance payments. 0003 cannot be
  substituted for those tables without reconciling balance, rescheduling,
  cancellation, adjustments and the existing projections. 0003 does not require
  an initial revision at commit, and its proof UUID has no ownership FK.

## Trusted pricing

For a **new submission**, the prospective authoritative source is the
server-side transaction's locked, active catalog/add-on rows and the locked
`business_settings` down-payment percentage—not the browser quote or 0003's
caller-supplied `minimum_snapshot`. The server must reconstruct the quote with
the existing `BookingPricing` rules, compare the user's displayed expected
total/minimum to that quote, validate peso precision and require
`required minimum <= claimed amount <= total`. The percentage is a whole
number 1–100 and `Money.Percentage` rounds away from zero to two decimal places.
Travel remains outside the online total. Mixed and later reschedule pricing must
use their separately approved snapshot/fee rules; do not infer them from 0003.

Create and seal the reservation's immutable price, percentage and required
minimum snapshots from that same authoritative quote in the same transaction.
For **later correction or review**, load those sealed snapshots under the
reservation lock; do not recalculate a past obligation from current settings.
0002 has only `total_price`, no authoritative minimum, sealed quote or protection
against a privileged update. 0003 only checks a *supplied* minimum and compares
the supplied total to the current reservation at insertion/revision time. Those
checks do not make either snapshot authoritative. Business-settings schema,
pricing snapshots and mutation protections are still required before activation.

## Identity and authority

The host must first validate a protected session issued after verified provider
authentication. Within the same database transaction as each sensitive action,
obtain and lock/revalidate authoritative account/security and identity mapping
by immutable ID, including provider subject, active/unlocked state, credential
epoch/version and role. Fail closed on missing, stale, ambiguous or unavailable
evidence. `AccountSessionSecurityGuard` and `DurableSessionAuthorization` are
preparation components, not registered proof that this boundary exists.

For Customer submission, correction and proof reads, derive the actor from that
validated server session; compare its authoritative Customer mapping with the
locked reservation owner. Never accept a Customer ID, role, object key or
authorization decision supplied by the browser as authority.

For staff review, resolve the actor's **current authoritative** Admin or
SuperAdmin role and active status under the same security boundary, then apply
the existing operation-specific rules. In particular, the existing
`CanManageBooking` allows ordinary Admin for non-mixed bookings and SuperAdmin
for mixed bookings; do not broaden that rule. A protected cookie's role claim
alone or a client-controlled `makeup.actor_id` is not sufficient. Confirm the
staff member inspected the exact current proof and submitted the corresponding
payment/revision token; a proof UUID by itself does not demonstrate inspection
or establish staff authority. None of these checks is implemented by 0003.

## Private proof lifecycle

Reuse `PaymentProofImageValidator.Normalize`: decode and re-encode JPEG/PNG,
strip metadata, and enforce 5 MB input/output, 8,000-pixel dimension and
40-million-pixel limits. Reject malformed data; never trust filename or MIME
type alone. `BookingsController.SaveProof` stages an opaque server-generated
name. A production design must use private server-controlled storage and a
server-created asset record tied to the uploader, reservation and revision;
verify the asset's existence, purpose and ownership before attaching it. 0003's
nullable `proof_asset_id` is **not** that record, FK or proof of storage.

Proof reads must revalidate the Customer owner or authorized staff and the
specific current revision before returning bytes; no public URL or browser-held
service credential. Storage and SQL do not share a transaction: retain the old
evidence until commit; on rejection, stale write or rollback clean up only the
new unreferenced object using an idempotent, retryable process. Do not delete an
existing proof on uncertainty or disclose object keys/errors to other users.
Storage setup and cleanup reliability still require isolated integration tests.

## Transaction and concurrency boundary

Submission: revalidate actor and lock settings/catalog, calculate and validate
quote, acquire reservation/slot protection, insert reservation, payment claim,
and initial proof revision atomically. No booking should commit without its
required claim/revision. A failed/stale request rolls back all SQL changes and
does not release a valid pre-existing slot.

Correction: lock and authorize the owning Customer and pending reservation,
lock the claim, verify the **expected** current revision, validate/attach the
new proof and append exactly one revision. Two corrections based on the same
version must yield at most one success. Preserve the old revision and slot.

Staff review: lock/revalidate account and role, reservation, claim and current
revision in a documented consistent order. Require the exact payment and
inspected evidence/version; append the verification and transition that same
reservation to Confirmed in **one database transaction**. Failure, stale proof,
revocation or a conflicting slot must roll back both. A retry must not verify
different evidence or duplicate a confirmation. Cross-table transitions,
identity proof, idempotency and lock ordering require implementation and
adversarial PostgreSQL tests; a unique revision number alone is insufficient.

0003 currently serializes append attempts and rejects stale numeric revisions;
it does not validate the writer's authority, ensure evidence is actually in
private storage, or atomically confirm a reservation. Its trigger permits
privileged writers to append `Verified` without an Admin identity. Deny runtime
table, sequence and function access until a separately reviewed trust boundary
and minimal grants exist. FORCE RLS does not constrain superusers/BYPASSRLS.

## Verification already feasible

`dotnet run --project tests/PaymentIntegrationChecks/PaymentIntegrationChecks.csproj`
exercises the real pure quote, amount and staff-review validators plus the
standalone security guard using synthetic in-memory snapshots. It does **not**
test an authenticated session, ownership, SQL adapter or atomic transaction.
`tests/ProofStorageChecks` exercises image validation and controller/storage
test doubles in an isolated in-memory Demo instance; it does not prove real
provider, cookie, durable database or cloud storage authorization. Prior 0003
tests used disposable PostgreSQL, not the Supabase target.

Next development task: implement a fail-closed transaction boundary for
verified Customer/staff identity and owner-scoped operations on a disposable
database, including revocation and concurrent correction/review tests, before
wiring the existing UI or enabling the ledger.

### Identity decision needed before implementation

Choose and approve the trust boundary that binds a host-verified provider session
to an immutable local Customer/staff identity during each database transaction:
either a verified, server-protected session used by a narrowly privileged
backend, or an independently reviewed database-verifiable session mechanism.
The choice must specify how the database prevents other writers from bypassing
ownership, role and generation checks, and how provider revocation/credential
uncertainty fails closed. A browser-provided account ID or role is not proof.

The existing `PostgresAccountAuthorizationTransactionFactory` is an inactive
staff-account administration transaction, not a Customer booking/payment
boundary. It locks generations, then `admin_users`, then identities; it does
not lock Customer rows or reservation ownership. The deployed 0002 Customer
table lacks `app_id`, provider identity and security-generation relationships.
Before reusing that order, approve the shared ordering and database privileges
for **every** security writer and protected mutation, including Customer rows,
identity mapping and reservation locks. Revocation must serialize with an
in-flight operation: whichever transaction acquires the security lock first
has a defined outcome; no operation beginning after committed revocation may
use the old generation.

The `AccountAuthorizationPostgresChecks` fixture is intentionally restricted
to named databases on port 5432 and a specific test role, and its runner
requires an interactive terminal. That target is not the separate disposable
cluster used for V2 migration tests. Do not weaken its guards or connect to an
existing server merely to satisfy a test; approve or create an independently
isolated fixture with equivalent safeguards for the chosen design. Its existing
fake-transaction checks and the synthetic `VersionedSessionAuthorization`
tests do not prove PostgreSQL lock behavior, cookie issuance, cross-process
revocation or transaction-scoped booking ownership.

The inactive `identity_authorization_candidate.sql` and
`PostgresTrustedIdentityBoundary` are a separate profile/revocation experiment.
Local PostgreSQL checks exercised a Customer profile write and revocation lock,
not a reservation or payment mutation. `ClaimsIdentity.IsAuthenticated` is not
cryptographic proof: matching fabricated in-process claims pass the isolated
candidate. Its SQL accepts caller-supplied actor evidence from the backend and
cannot independently prove session provenance. The public adapter is
hard-gated; its internal test hook is not an authorization boundary and must
never be called from runtime code.
Do not register it, grant runtime access, or apply the candidate until a
reviewed host-to-database trust binding, staff/domain operations, privileges,
all security-writer locks and real-provider/cookie validation are implemented.

An isolated-only loopback HTTP fixture now exercises ASP.NET protected-cookie
validation against the synthetic PostgreSQL identity candidate. Its login
provider is fake and it calls the internal fixture-only adapter; it is not an
application session issuer, a database-verifiable identity protocol or proof
that production provider identities are safely bound. The public adapter and
normal durable-auth startup gates remain closed.

`TrustedRequestSessionAccessor` is registered in the application but is not
connected to any mutation. It asks ASP.NET's registered cookie handler for its
ticket rather than accepting `HttpContext.User` or controller-supplied claims;
it requires the durable setting, the expected durable validation callback, an
unexpired ticket and strict actor fields. The resulting session is bound to
the originating `HttpContext`, a per-request marker, response completion, its
cancellation token and ticket expiration; `TryGetActor` refuses a different,
completed, expired or cancelled request.
Normal startup still rejects the
durable setting. A result is request-time evidence only: it is not a lock or
authorization to mutate. Any future operation must revalidate account,
credential epoch, generation, ownership and current role while holding the
reviewed database locks in the same transaction.

The chosen trusted-backend model assumes the application host, cookie keys,
authentication services and server-only database credential are not
compromised. PostgreSQL does not cryptographically authenticate end-user
claims in the current candidate. Its backend role has EXECUTE on the candidate
session validation, Customer-name update and self-revocation functions and
USAGE on their API schema; explicit direct table and sequence rights are
revoked. This is a candidate only, not a deployed privilege inventory: audit
effective PUBLIC/default grants and memberships before any runtime use.

There is no production-ready booking/payment transaction adapter yet. An isolated
test-only Customer proof-correction candidate now exercises transactional
identity and ownership checks against synthetic payment and proof-asset tables;
it is not migration 0003, is not registered in production, and its validation
flag does not establish actual private-storage ownership or file validation.
The separate test-only staging candidate normalizes an image, generates an object
key and compares stored bytes on readback, but does not bind it to a Customer,
reservation or revision and cannot make it correction-eligible. The existing
cloud store uses a server-wide credential and its delete method suppresses errors;
upload, database commit and cleanup can have uncertain outcomes. Durable staging
records and reconciliation are needed before linking storage to a transaction.
The separate `IProofObjectOutcomeStore` restricts keys to generated staging-key
syntax and reports present, absent or unknown without changing the legacy API.
Cloud 404 and delete acknowledgements remain unknown as evidence of absence:
missing buckets, denied access, and provider consistency have not been verified.
Even a present read is not image, hash, owner, or revision verification. These
low-level operations do not authorize a caller or protect referenced objects;
do not connect deletion to a reconciler without transactional coordination.
The isolated staging candidate now also has a database-backed intent operation:
it rechecks identity generation, ownership and expected revision under locks and
records a pending, server-keyed intent and normalized-content hash. This is not
an upload or validated asset, and has no promotion, correction consumption or
reconciliation worker. The test-only SQL can quarantine a pending intent as
reconciliation_required after an explicit trusted-backend request, but is not
wired to storage outcomes. All other transitions, including verification,
consumption and retirement, remain blocked. The older standalone fake-object staging method still does
not recheck the generation; a static test cookie snapshot can use it after
database revocation. Both remain hard-gated outside isolated tests and are not
wired to the correction candidate or UI.
The existing identity
candidate offers session validation, Customer profile update and self-revocation
only; it has no reservation-owner or staff payment command. Its standalone
`validate_session` must not be treated as an authorization check for a later
transaction: its locks end at transaction completion. Candidate 0003 also
lacks an authoritative minimum, proof-ownership mapping, verified staff actor,
atomic reservation confirmation and an adapter compatible with existing
payment views. The Customer/staff mutation entry points remain gated. Implement
and review operation-specific database commands with a common identity-to-domain
lock order and restricted privileges before any integration; do not expose an
arbitrary SQL/callback execution facility.
