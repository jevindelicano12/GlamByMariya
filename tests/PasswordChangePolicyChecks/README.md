# Authenticated password-change shared-policy checks

Executes the actual `AccountController.ChangePassword` action with the real
`PasswordPolicy`, `SupabaseAuthService`, `AuthenticationPasswordCoordinator`,
`PasswordHasher`, and in-memory DemoStore-derived test store. It does not start
the application host, create sockets, access a database, or use real Supabase.
Every possible provider request terminates at a fake `HttpMessageHandler` that
throws on any unexpected request; the only HTTP clients use that handler.
Authentication sign-outs are captured by an in-memory fake service. Credentials,
tokens, hashes and test email values are synthetic and never printed.

Invalid-policy/confirmation cases use the remote-configured branch to prove they
return before Supabase current-password verification, identity linking, password
update, local mutation, or sign-out. Valid cases prove the existing current
password is passed unchanged to the provider fake and the change completes. A
local-only case exercises the unchanged `PasswordHasher` current-password check.
The partial-sync case makes local storage fail only after the fake remote update,
then asserts the Phase 1.6 possible-remote-success message and current-cookie
sign-out behavior. The coordinator is referenced but not modified.

Direct action invocation does not test MVC routing, browser behavior, a real
authentication cookie, production DI, real Supabase policy, or PostgreSQL. The
fake accepts valid shared-policy inputs solely to test controller sequencing; it
does not establish provider compatibility.
