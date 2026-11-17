# Isolated MVC reset binding and compiled-view verification

Uses actual MVC binder services, the actual four-parameter action and compiled
production reset view/layout/FormTagHelper. No application Program/Main, server,
HTTP clients, persistent store, external configuration, production DI or network
listener is used. MVC services are built in a standalone container. Data Protection
is explicitly ephemeral; synthetic antiforgery cookies/tokens remain in memory.
Static-file providers are empty and store/TempData are in-memory only.

The form binder reads a real FormCollection/FormValueProvider. This is actual
simple-type MVC binding, not raw HTTP-body parsing or a complete action-invoker
pipeline. Missing/empty/whitespace-only strings become null with MVC defaults;
leading/trailing whitespace on non-whitespace inputs is retained. Bound invalid
confirmation is passed to the real action with null collaborator sentinels: any
OTP/provider/store access would fail. Matching confirmation intentionally uses an
invalid code to prove progression past confirmation without recovery access.
Full valid-proof success is tested separately in PasswordResetConfirmationChecks.

Rendering includes attempted values in ModelState and verifies blank password
fields, fixed errors, retained email/code fields and generated antiforgery input.
The real antiforgery service validates the generated token/cookie pair and rejects
missing token. Production global-filter registration is source evidence only.
URL generation uses an explicit test-only router, not production route discovery.

No browser is launched; HTML required/type attributes are checked, not browser
constraint-validation UI. No submitted secrets, HTML, cookies or tokens are printed.
These limits must not be reported as full HTTP/browser end-to-end verification.
