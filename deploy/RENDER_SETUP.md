# Deploying the demo: Supabase + Render + Xendit (test) + Brevo

Secrets go only into your own terminal or the Render dashboard, never into chat or the repo.

## 1. Supabase (database) — once
1. Apply the migrations as the database owner (type the `postgres` password when asked):
   `.\tools\supabase-command.ps1 -Command --database-migrate -Login postgres`
2. Claude then grants the app login (`glam_v2_backend`) membership of `makeup_backend`,
   creates the private `app-files` bucket and checks the security advisor.
3. Create the owner account (also the only makeup artist), with the app login's password:
   `.\tools\supabase-command.ps1 -Command --database-create-superadmin -Login glam_v2_backend`

## 2. Values you collect
| Render key | Where to get it |
|---|---|
| `Database__Password` | The `glam_v2_backend` password (the one already in your user-secrets as `Database:Password`) |
| `SupabaseStorage__ServiceKey` | Supabase → Project Settings → API keys → `service_role` (secret) |
| `Email__BrevoApiKey` | Brevo → SMTP & API → API keys |
| `Email__From` | A sender address verified in Brevo |
| `Xendit__SecretKey` | Xendit dashboard (**Test mode**) → Settings → API keys → secret key `xnd_development_...` with Money-in write access |
| `Xendit__CallbackToken` | Xendit (Test mode) → Settings → Webhooks → Verification token |
| `DataProtection__CertificatePfxBase64`, `DataProtection__CertificatePassword` | Run `.\tools\new-dataprotection-cert.ps1`; it writes both to `%LOCALAPPDATA%\GlamByMariya\render-dataprotection.txt` |
| `AllowedHosts` | Your Render hostname, e.g. `glam-by-mariya.onrender.com` |

## 3. Render
1. Push the branch to GitHub, then Render → **New → Blueprint** → pick the repository and branch.
   Render reads `render.yaml` and asks for every secret above.
2. Wait for the first deploy; `/healthz` must answer `ok`.

## 4. Xendit webhook
Xendit (Test mode) → Settings → Webhooks → **Invoices paid**:
`https://<your Render hostname>/payments/xendit/webhook`

## 5. Try it
1. Sign in as the owner. Register a customer (the email code comes from Brevo).
2. Book a service as the customer → you are sent to Xendit's test checkout → pay with a test method.
3. The booking turns **Confirmed** when the webhook arrives. An unpaid booking expires after 30 minutes.

Notes: the free Render plan sleeps after 15 minutes idle (first request is slow; Xendit retries
webhooks). Rate limits count all visitors together until Render's proxy addresses are configured
in `ForwardedHeaders__KnownProxies`.
