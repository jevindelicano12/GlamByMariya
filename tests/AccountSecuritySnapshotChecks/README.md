# Standalone snapshot provider checks

This package-free harness compiles the actual application adapter and existing
guard source. Only `IAccountSecurityReadSource` is faked. It creates no host,
network client or database connection. No live repository is implemented.

## Existing provider contract

`IAccountSecuritySnapshotProvider.LoadAsync(accountId, cancellationToken)` remains
the sole snapshot interface consumed by the guard:

- `null`: a completed authoritative read found neither an account nor an identity.
- Immutable snapshot: metadata is preserved, not a declaration of eligibility.
  Disabled/locked accounts, unsupported configuration and invalid identity data
  are evaluated by the existing guard. Missing/unknown state is never Ready.
- `AccountSecurityReadException`: invalid request, invalid account mapping, or
  repository unavailable. No inner provider exception is exposed. The unchanged
  guard maps all such exceptions to RepositoryUnavailable and denies access.

Duplicate identity candidates produce IdentityInconsistent; duplicate account
rows cannot be represented faithfully by the single-account snapshot contract,
so they produce InvalidAccountMapping rather than selecting a row.

## Future authoritative repository obligations

`IAccountSecurityReadSource` is a read-side seam, not a competing session guard.
A future public PostgreSQL implementation must:

1. Read by immutable UUID across both account tables, without request-actor filters.
2. Return all identities whose UUID OR resolved owner matches, including conflicts.
3. Read account status, lockout, role, identity metadata, state, epoch and integrity
   facts from one consistent database snapshot. Do not join away missing identities.
4. Establish normalized-login correspondence/uniqueness and global Supabase-subject
   uniqueness in that same read. Unknown facts remain null, never true by default.
5. Resolve bigint owners to app UUIDs and preserve whether customer/admin owns the row.
6. Derive AccountVersion with the existing AccountSession.Version algorithm; never
   expose the password hash in the read contract or logs. Evaluate lockout against
   a single authoritative read time; null IsLocked means unknown, not unlocked.
7. Return explicit empty immutable arrays only for completed reads. Missing arrays,
   failures, malformed results and cancellation must not become empty success.
8. Never infer or modify provider ownership from email, backfill identity links,
   cache a Ready result after failure, or manufacture missing state/epoch values.

Configuration passed to the adapter is trusted host configuration, not session
evidence. Unsupported store/authentication values are retained for guard denial.
The adapter cannot prove the repository's completeness; integration tests against
an isolated PostgreSQL instance are required before trusting a live implementation.
Supabase proof verification remains a separate prerequisite for future issuance
and reconciliation; this adapter does not authenticate a remote user.

No existing DI, authentication path, schema, or guard is modified by this harness.
