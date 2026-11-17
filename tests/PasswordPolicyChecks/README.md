# Standalone password policy checks

Compiles and invokes actual application `PasswordPolicy.cs` directly, with no
packages or application project reference. `RegistrationRules.cs` is also compiled
solely to demonstrate compatibility differences. No host, credentials, source-file
reads, database, network, hashing, or persistent test storage is used at runtime.
Inputs and returned error values are never printed, including on assertion failure.

The policy is INACTIVE. It requires 8–128 UTF-16 code units, an ASCII letter and
an ASCII digit. Additional Unicode, spaces and symbols are allowed unchanged.
The maximum is provisional; no Supabase character/byte compatibility is claimed.
New-password validation and required ordinal confirmation are separate operations:
callers needing both must perform both before consuming OTPs or mutating credentials.

The test suite covers boundaries, ASCII versus Unicode composition, supplementary
characters, differing UTF-8 lengths, unchanged input, deterministic fixed errors,
and exact confirmation without trimming/normalization/case folding/truncation.
All runtime checks exercise actual pure validators, not predicate mirrors.

Existing `PasswordPolicyPreparation` characterization/source-contract checks remain
untouched. Its historical skip descriptions predate this standalone helper and must
not be interpreted as an up-to-date implementation inventory. Cross-flow enforcement,
reset confirmation, controller side effects and Supabase verification remain pending.
No existing authentication flow calls the new policy.
