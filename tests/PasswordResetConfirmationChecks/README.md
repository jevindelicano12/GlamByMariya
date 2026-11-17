# Reset confirmation safety checks

References the actual application assembly and directly invokes its ResetPassword
POST action, real Supabase client, real local OTP service and existing coordinator.
The application host is never started. All HTTP clients use a terminal in-memory
HttpMessageHandler, with no socket handler or network fallback. Email delivery is
also fake HTTP; SMTP and filesystem outbox paths are unreachable with this setup.
Account storage derives from the in-memory DemoStore; no database is constructed.
Synthetic OTPs, passwords, hashes, keys and session values are never printed.

For both recovery branches, invalid confirmation is followed by a corrected call
using the SAME proof. Invalid shared-policy inputs also assert no verification,
identity link, update, local store access, password change, or sign-out; local proof
and attempts remain intact. Positive tests cover policy boundaries, Unicode and
symbols with ASCII composition, role/active preservation, and Phase 1.6 partial-
failure messaging. The real controller's unrelated dependencies are null
sentinels, so any unexpected access fails rather than constructing external services.

Direct controller execution does not test routing, MVC model binding, rendered
HTML, antiforgery, cookies, actual Supabase policy or PostgreSQL. The distinct
PasswordResetMvcChecks harness covers isolated rendering/binding. Repository
validation is source-contract checked only; no database-backed repository test runs.
No claim of provider compatibility is made.

Restore/run only from already-cached dependencies with network sources disabled.
Do not run application commands, migrations, or database-oriented test projects.
