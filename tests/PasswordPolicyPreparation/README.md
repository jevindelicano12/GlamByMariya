# Password policy preparation (not enforcement)

Package-free console harness: compiles the actual `RegistrationRules.cs`, reads
controller/view source contracts, and compares explicitly labeled current-rule
mirrors. It never compiles/starts the MVC host, connects to a provider/database,
reads configuration/credentials, writes account data, or prints password inputs.
Read-only repository source inspection is the only runtime filesystem access.

## Current versus proposed

| Aspect | Registration / invitations | Change | Reset | Proposed decision |
| --- | --- | --- | --- | --- |
| Minimum | 8 UTF-16 units | 8 | 8 | Retain 8 for minimal alignment |
| Letters | ASCII | `char.IsLetter` | None | Explicit common class needs approval |
| Digits | .NET `\d` (Unicode decimal) | `char.IsDigit` | None | Explicit common class needs approval |
| Maximum | None | 128 UTF-16 units | None | Common cap needs Supabase character/byte compatibility decision |
| Confirmation | Absent | Exact equality | Absent | Add reset confirmation before OTP consumption |
| All whitespace | Rejected | Rejected | Passes controller minimum | Shared rejection before provider mutation |

Registration browser JavaScript requires ASCII letters AND ASCII digits, unlike
the server's Unicode digit support. Change/reset browser fields do not enforce
composition. Invitation fields provide minimum length only. Reset repositories
also reject all-whitespace strings, unlike the reset controller. The harness does
not exercise repositories; existing source audit established that difference.

The 127/128/129 cases characterize existing behavior, not approval of 128 as the
future universal maximum. UTF-16 units, Unicode scalars, user-perceived characters,
and UTF-8 bytes are different. BMP letters, non-ASCII decimal digits, supplementary
letters and emoji are covered. No trimming or Unicode normalization is proposed.

## Pending production tests

Seven checks remain unconditionally skipped. A class name, source token or fake
validator cannot satisfy them. Future implementation must add actual shared-helper
tests and fake-backed controller tests proving policy/confirmation rejection occurs
before recovery proof consumption and all remote/local mutation. Current source
ordering checks cannot prove branch behavior or absence of side effects.

Before changing production behavior, decide the common character classes and
maximum units/bytes, verify the applicable Supabase limit independently, preserve
existing login compatibility, and validate new passwords before OTP consumption.
Do not introduce stricter repository-only rules after a remote update has occurred.
Preserve Phase 1.6 coordinator ordering, result checks and partial-failure messaging.

No production shared password policy is implemented by this project.
