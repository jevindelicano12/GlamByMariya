# Live Website Content Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On the live (database) site the SuperAdmin edits website text and sets the hero photo, the About portrait and gallery photos, then publishes them.

**Architecture:** Finish the existing database path: a migration creates the tables `PostgresWebsiteContentRepository` already expects, the repository gains an asset writer and image-aware publish, Program.cs registers the website services in database mode, and the editor gets a live upload/remove action that stores files through `IAppFileStore` (Supabase Storage).

**Tech Stack:** .NET 10 ASP.NET Core MVC, Npgsql, PostgreSQL 17 (Supabase), Magick.NET (`WebsiteImageValidator`).

**Spec:** `docs/superpowers/specs/2026-11-20-live-website-content-design.md`

## Global Constraints

- One branch per task, chained from the previous task's branch; run the listed checks before each commit; push only when the owner says "push na".
- SuperAdmin only for every write; ordinary Admin and customers never change public content.
- Limits come from `WebsiteContentRules`: hero headline 160, hero description 1000, About heading 160, biography 5000, home-service 2000, service area 1000, contact 2000, links 10 (label 100, URL 2048), FAQs 15 (question 300, answer 3000), gallery 20, image 5,000,000 bytes, alt text 250, caption 500.
- Object keys: `website-content/<32 lowercase hex>.jpg|png` (the regex in `WebsitePublicImageService`).
- Demo behaviour stays as it is.

## Review Focus

1. Upload while the storage key is broken → friendly error on the editor, no image row created (Task 4 test: failing file store).
2. Database step fails after the file was stored → the stored file is deleted (Task 4 test: recording file store sees `Delete`).
3. Two tabs editing the same draft → the stale save/upload is refused with "The draft changed" and nothing is lost silently (Task 1 and Task 2 tests: stale revision).
4. A gallery already at 20 photos → a 21st upload is refused with a clear message (Task 4 test).
5. A non-image file renamed `.jpg` → refused before anything is stored (Task 4 test: validator rejects, file store untouched).

---

### Task 1: Website content tables (migration 0012)

Branch: `phase-4/website-content-schema`.

**Files:**
- Create: `database/migrations/0012_website_content.sql`
- Test: `tests/DatabaseChecks/Program.cs` (new section `--website-content`)

**Interfaces:**
- Produces tables: `website_content_versions(id uuid pk, revision bigint not null default 1 check >=1, status text check in ('draft','published','archived'), hero_headline, hero_description, about_heading, about_biography, home_service_description, service_area_information, contact_details` — all `text not null default ''` with `char_length` checks at the rule limits — `created_by uuid, updated_by uuid, published_by uuid, created_at/updated_at timestamptz default now(), published_at timestamptz)`; unique partial indexes so at most one `draft` and one `published`; `website_content_links(id uuid pk, version_id fk cascade, label, url, sort_order int)`; `website_content_faqs(id, version_id fk cascade, question, answer, sort_order)`; `website_content_assets(id uuid pk, object_key text unique check matches '^website-content/[0-9a-f]{32}\.(jpg|png)$', content_type check in ('image/jpeg','image/png'), byte_length int check 1..5000000, uploaded_by uuid not null, created_at)`; `website_content_images(id uuid pk, version_id fk cascade, asset_id fk restrict, role check in ('hero','portrait','gallery'), alt_text 1..250, caption <=500, sort_order int >=0, visible bool default true)`.
- RLS on all five tables with a `makeup_backend` policy `USING (true) WITH CHECK (true)` and `GRANT SELECT, INSERT, UPDATE, DELETE` to `makeup_backend` (same `DO $$` pattern as `0003_email_codes.sql`).

- [ ] **Step 1: Write the failing test** — section `--website-content` in `tests/DatabaseChecks/Program.cs`, run as the app login (`appSource`) through `new PostgresWebsiteContentRepository(appSource)`:
  - `CreateDraft(owner)` returns revision 1 and status `draft`; a second `CreateDraft` returns the same id.
  - `SaveDraft` with HeroHeadline "Glam at home", AboutHeading "Meet Mariya", one link, one FAQ → `ReadDraft` returns them; a save with the old revision throws `InvalidOperationException`.
  - `Publish` → `ReadPublished().Text.AboutHeading == "Meet Mariya"`; a new `CreateDraft` copies the published text, links and FAQs.
  - Direct insert of a second `draft` row fails (unique index).
  - `schema_migrations` count equals the number of files (existing access check keeps passing).
- [ ] **Step 2: Run** `bash tools/test-db.sh tests/DatabaseChecks --website-content` — expected FAIL: relation "website_content_versions" does not exist.
- [ ] **Step 3: Write `0012_website_content.sql`** with the tables, indexes, checks, RLS and grants above (no BEGIN/COMMIT).
- [ ] **Step 4: Run** the section, then `bash tools/test-db.sh tests/DatabaseChecks` — expected: all sections pass.
- [ ] **Step 5: Commit** migration and test.

### Task 2: Images in the database repository

Branch: `phase-4/website-content-images-repo` (from Task 1).

**Files:**
- Modify: `Backend/GlamByMariya.Infrastructure/Data/PostgresWebsiteContentRepository.cs`
- Modify: `Backend/GlamByMariya.Infrastructure/Services/WebsiteContentService.cs` (interface only)
- Test: `tests/DatabaseChecks/Program.cs` (`--website-content`)

**Interfaces:**
- Produces in `WebsiteContentService.cs`: `public interface IWebsiteAssetWriter { Task<Guid> AddAsset(Guid actor, string objectKey, string contentType, int byteLength, CancellationToken ct); }` implemented by `PostgresWebsiteContentRepository`.
- Changes `Publish`: removes the "image publishing is not available" refusal; instead refuses (`InvalidOperationException("A website image is missing. Remove it and publish again.")`) when any draft image's `asset_id` has no asset row (defensive; FK makes this rare).
- Consumes: existing `ReplaceDraftImages`, `ReadDraftImages`, `ReadPublishedImages`, `FindPublishedImage`, `FindDraftImage`.

- [ ] **Step 1: Failing tests** (extend `--website-content`):
  - `AddAsset(owner, "website-content/" + 32 hex + ".jpg", "image/jpeg", 1234)` returns a new id; a bad key (`"other/x.jpg"`) throws `PostgresException` (check constraint).
  - `ReplaceDraftImages` with one hero and two gallery images → `ReadDraftImages` returns 3; `Publish` succeeds; `ReadPublishedImages` returns 3; `FindPublishedImage(heroId)` returns the key; `FindDraftImage(heroId)` returns null after publish.
  - `ReplaceDraftImages` with a stale revision throws `InvalidOperationException`.
- [ ] **Step 2: Run** `--website-content` — expected FAIL: `AddAsset` does not exist / publish refuses images.
- [ ] **Step 3: Implement** `AddAsset` (single INSERT into `website_content_assets`, returns `Guid.NewGuid()` id) and the `Publish` change.
- [ ] **Step 4: Run** `--website-content` and `tests/WebsiteImageChecks`, `tests/WebsiteContentRulesChecks` (`dotnet run --project ...`) — expected PASS.
- [ ] **Step 5: Commit.**

### Task 3: Turn on website editing in database mode

Branch: `phase-4/website-content-register` (from Task 2).

**Files:**
- Modify: `Frontend/GlamByMariya.Web/Program.cs` (the `if (connection != null)` block near line 223)
- Test: `tests/Program.cs` (new section `--website-content-live-only`)

**Interfaces:**
- Registers, only when `connection != null && schema == "public"`: `PostgresWebsiteContentRepository` singleton (from `NpgsqlDataSource`) exposed as `IWebsiteContentRepository`, `IWebsiteImageRepository`, `IWebsitePublishedAssetLookup`, `IWebsiteDraftAssetLookup`, `IWebsiteAssetWriter`; `WebsiteContentService`, `WebsiteImageService` singletons; `WebsitePublicImageService`, `WebsiteDraftImageService` scoped (they use scoped `IAppFileStore`).
- Consumes: Task 2's `IWebsiteAssetWriter`.

- [ ] **Step 1: Failing test** — source checks in `--website-content-live-only`: Program.cs registers `PostgresWebsiteContentRepository` and each interface above inside the database branch; the demo registration lines are unchanged.
- [ ] **Step 2: Run** `dotnet run --project tests -- --website-content-live-only` — expected FAIL.
- [ ] **Step 3: Add the registrations.**
- [ ] **Step 4: Run** the section plus `--accounts-only`, `--xendit-only`, `--boundary-regression-only`; and `tests/WebsiteContentFormChecks` — expected PASS.
- [ ] **Step 5: Commit.**

### Task 4: Upload and remove photos from the live editor

Branch: `phase-4/website-content-uploads` (from Task 3).

**Files:**
- Create: `Backend/GlamByMariya.Infrastructure/Integrations/WebsiteImageUploadService.cs`
- Modify: `Frontend/GlamByMariya.Web/Controllers/WebsiteContentController.cs` (actions `SaveImage`, `RemoveImage`; Index passes draft images)
- Modify: `Frontend/GlamByMariya.Web/Views/WebsiteContent/Index.cshtml` (live image cards)
- Test: `tests/Program.cs` (`--website-content-live-only`)

**Interfaces:**
- Produces: `public sealed class WebsiteImageUploadService(IWebsiteImageRepository images, IWebsiteAssetWriter assets, IAppFileStore files)` with
  - `Task Save(ClaimsPrincipal owner, Guid draftId, long revision, string role, string? altText, string? caption, byte[] bytes, CancellationToken ct)` — role `hero`/`portrait` replaces that role, `gallery` appends with `sort_order` = current max + 1; refuses a 21st gallery photo with `ArgumentException("The gallery already has 20 photos.")`; normalises with `WebsiteImageValidator.Normalize` before storing; key `website-content/{Guid.NewGuid():N}{ext}`; on any failure after `files.Save`, calls `files.Delete(key)` and rethrows.
  - `Task Remove(ClaimsPrincipal owner, Guid draftId, long revision, Guid imageId, CancellationToken ct)` — replaces the list without that image.
  - SuperAdmin check identical to `WebsiteImageService.RequireOwner` (throws `UnauthorizedAccessException`).
- Controller: `[HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(5_100_000)] SaveImage(Guid draftId, long revision, string role, string? altText, string? caption, IFormFile? image, CancellationToken)` and `RemoveImage(Guid draftId, long revision, Guid imageId, CancellationToken)`; both redirect to `Index` with `TempData["Success"]`/`TempData["Error"]`; storage failure (`InvalidOperationException` from the file store) shows "Photos can't be saved right now. Text changes still work."
- View: when not demo and `ViewData["DraftImages"]` is set, show Hero and Portrait cards (preview via `DraftImage`, form to `SaveImage`) and a Gallery card (list with Remove buttons, add form with alt text and caption).

- [ ] **Step 1: Failing tests** in `--website-content-live-only`, using fakes (in-memory `IWebsiteImageRepository` + `IWebsiteAssetWriter`, recording `IAppFileStore`):
  - Owner saves a hero from `wwwroot/images/looks/soft-glam.jpg` → one `files.Save` with a key matching the object-key regex, one hero in the fake list.
  - Second hero replaces the first (still one hero).
  - Admin (not SuperAdmin) → `UnauthorizedAccessException`, no `Save` call.
  - Bytes `"not an image"u8` → exception, no `Save` call.
  - Fake repository throws on `ReplaceDraftImages` → `files.Delete` called with the same key.
  - 21st gallery photo → `ArgumentException` "The gallery already has 20 photos.", no `Save` call.
  - `Remove` drops only the chosen image.
  - View source contains `asp-action="SaveImage"` and `asp-action="RemoveImage"` outside the `DemoPersistentText` block.
- [ ] **Step 2: Run** the section — expected FAIL (type missing).
- [ ] **Step 3: Implement** the service, register it scoped in the Task 3 block, add the actions and the view cards.
- [ ] **Step 4: Run** the section, `--accounts-only`, `--xendit-only`, `tests/WebsiteImageChecks`, `tests/WebsiteContentFormChecks`, and the full `bash tools/test-db.sh tests/DatabaseChecks` — expected PASS.
- [ ] **Step 5: Commit.**

### Task 5: Release

- [ ] Owner says "push na" → push the four branches; apply migration 0012 on Supabase (same way as 0011, recording its checksum); point Render at `phase-4/website-content-uploads`.
- [ ] Live check as owner: create draft, set About heading "Meet Mariya", publish → Home shows it. Once the storage key is fixed: upload hero and portrait, publish → Home and About show them.
