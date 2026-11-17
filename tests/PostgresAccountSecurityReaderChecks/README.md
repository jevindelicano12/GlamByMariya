# Inactive PostgreSQL security reader preparation

The package-free harness compiles the actual reader, snapshot adapter and guard.
Only the SQL executor is faked. It opens no host, network or database connection.
SQL-contract assertions do not execute PostgreSQL; four database-validation groups
remain explicitly skipped. Existing guard/provider suites remain separate.

## Architecture and missing schema

`PostgresAccountSecurityReadSource` implements the existing
`IAccountSecurityReadSource`. Its fixed, parameterized SELECT reads only `public`
tables. Candidate identities are selected by account UUID or owning physical
account IDs, never email. Customer/admin ownership is resolved separately; orphaned
foreign keys retain an empty UUID sentinel so they cannot disappear as null owners.
Duplicate accounts/candidates are retained for the adapter/guard to deny.

The catalog capability check requires state/epoch and provider-link columns with
expected types. `to_jsonb` reads optional metadata without referencing nonexistent
columns. Since credential-sync columns do not exist yet, current-schema reads
must throw `MetadataUnavailable`, not return Ready, epoch 1, or account missing.
Missing tables, permissions or ordinary required columns also deny via failure.
No migration or fallback is provided. Raw password hashes stay inside the query
payload/parser; only the existing derived version leaves the reader. Nothing logs
payloads, provider exceptions or credentials.

Global login consistency checks cover identity logins AND underlying accounts.
Admin login canonicalization follows the existing identity trigger's email/username
rule. Unknown admin role values yield null and deny instead of mapping to Admin;
valid Customer/Admin/SuperAdmin roles and active flags are preserved. Null/empty
password hashes cannot become valid versions. Provider subject values are read
only from the identity row, never constructed from email.

## Consistency and future transport

`PostgresAccountSecurityQuery` accepts an explicitly supplied Npgsql-backed
`DbDataSource`; it does not create connection strings or register services. Future
callers must supply the existing validated TLS/pool configuration. Construction
does not open a connection. The offline harness does not instantiate this transport.
Execution uses a repeatable-read, read-only transaction and one SELECT for the
catalog, accounts, identities and global integrity facts. UUID binding and timeout
are explicit. Lockout uses PostgreSQL `statement_timestamp()` and timestamptz
comparison: equality means expired, no application-clock or timezone conversion.

MVCC consistency is not a lock on future mutations. State/epoch writes, issuance
races, in-flight requests and rollback compatibility still require the separately
approved synchronization architecture. No cached successful result is reused.

## Required before integration

With separate authorization, test SQL parsing/types, missing-column detection,
constraints, normalization/collations, role/status/lockout boundaries, cross-table
UUID collisions, real orphan/duplicate rows, MVCC concurrency, Npgsql binding,
timeouts, read-only enforcement and permissions/RLS against disposable PostgreSQL.
Read completeness requires a trusted backend role able to see all relevant rows;
partial RLS visibility can invalidate uniqueness claims. Do not activate against
live data or relax TLS, permissions, identity requirements or missing-state denial.

No DI, controller, cookie path, EF model, migration or database is changed.
