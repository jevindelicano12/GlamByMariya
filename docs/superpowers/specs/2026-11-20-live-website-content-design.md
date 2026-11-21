# Live website content (text and photos) — design

Date: 2026-11-20. Approved by the owner in chat ("simulan mo na").

## Intent

The owner (SuperAdmin) must be able to change, on the live site, the words
and photos visitors see — for example the "Meet Mariya" heading, the About
biography, the homepage hero photo, Mariya's portrait and the gallery —
without a programmer. Today this only works in the local demo; on the live
(database) site the Website Content Management screen is disabled, so the
homepage shows "Hero photo not yet available" and "Artist portrait not yet
available".

## What exists already

- `WebsiteContentService` (draft → preview → publish, SuperAdmin only) and
  `WebsiteContentRules` (validation of the text fields, links, FAQs, images).
- Text fields: hero headline, hero description, About heading, About
  biography, home-service description, service areas, contact details,
  links, FAQs. Home, About, Gallery and Contact already read the published
  text and images, with built-in defaults when nothing is published.
- `PostgresWebsiteContentRepository`: a database version of the text and
  image repositories, written but never registered; it expects tables that
  no migration creates. Its `Publish` refuses drafts that have images.
- `WebsiteImageService`, `WebsitePublicImageService`,
  `WebsiteDraftImageService` (read published/draft images from
  `IAppFileStore`, object keys `website-content/<32 hex>.jpg|png`).
- The editor view and controller: the text form works with any
  repository; image upload (`SaveDemoImage`) is demo-only.
- Live file storage is Supabase Storage (`app-files` bucket) through
  `IAppFileStore`, the same path service photos use.

## Goals

1. On the live site, the owner can create a draft, edit all existing text
   fields, preview, and publish; the public pages show the published text.
2. The owner can set or replace the homepage hero photo and the About
   portrait, with alternative text.
3. The owner can add gallery photos (alt text, caption; up to 20) and
   remove them.
4. Publishing a draft with photos works; published photos are served
   publicly; draft photos only to the SuperAdmin.

## Non-goals

- Editing small fixed labels ("ABOUT THE ARTIST", "MAKEUP SERVICES"), page
  layout, or adding new pages.
- Reordering gallery photos or hiding them without removing (can come
  later; the tables already carry `sort_order` and `visible`).
- Deleting old image files from storage when replaced (kept; harmless).

## Anti-goals

- A customer or ordinary Admin changing public content.
- A half-saved state: a photo row pointing at a file that was not stored,
  or a stored file the page can't show.
- Losing the owner's typed text when a save fails.

## Approach

Picked: finish the existing database path (register the Postgres
repository, add its tables, add a live upload action), reusing the editor
and services already written and tested in the demo.

Considered and not taken:
- A separate simple "Website photos" page outside the editor: less work for
  photos but leaves text uneditable and duplicates the editor.
- Storing content in JSON files: Render's free plan has no persistent disk.

## Design

1. **Migration `0012_website_content.sql`** creates
   `website_content_versions` (id, revision, status draft/published/
   archived with at most one draft and one published, the seven text
   columns, created/updated/published by and at), `website_content_links`,
   `website_content_faqs`, `website_content_assets` (id, object_key,
   content_type, byte_length, uploaded_by, created_at) and
   `website_content_images` (id, version_id, asset_id, role
   hero/portrait/gallery, alt_text, caption, sort_order, visible), with
   foreign keys, checks matching `WebsiteContentRules`, RLS and grants for
   `makeup_backend` like the other app tables.
2. **Registration** in database mode: `PostgresWebsiteContentRepository` as
   `IWebsiteContentRepository`, `IWebsiteImageRepository`,
   `IWebsitePublishedAssetLookup`, `IWebsiteDraftAssetLookup`; plus
   `WebsiteContentService`, `WebsiteImageService`,
   `WebsitePublicImageService`, `WebsiteDraftImageService`.
3. **Upload** (`SaveImage` action, live): validate and normalise the image
   (`WebsiteImageValidator`), store it at `website-content/<id>.<ext>`, insert
   the asset row, then replace the draft's image list (hero/portrait
   replaced, gallery appended) through `ReplaceDraftImages` with the
   draft's revision. If the database step fails, delete the stored file.
   **Remove** gallery photo: replace the list without it.
4. **Publish** allows images, but only when every image's asset row exists.
5. **Editor view**: in database mode show, under the text form, the hero
   and portrait cards and a gallery card (current photos with Remove, and
   an add form), using the draft image URL for previews.

Each numbered step is its own branch, chained, tested before commit.

## Testing

- Database checks (throwaway postgres, as the app login): migration
  applies; draft create/save/publish round trip; one draft/one published
  enforced; images replace/append/remove; publish with images; published
  image lookup hides drafts; revision conflicts refused.
- Workflow checks: editor shows image forms in database mode; upload
  action refuses non-SuperAdmin, oversized and non-image files; file is
  deleted when the database step fails (recording file store).
- Live: after deploy, owner publishes text + hero/portrait; Home and About
  show them.

## Left to the builder

Exact column sizes beyond the rules' limits, index names, and the gallery
card's layout details.

## Dependency

Uploads need the Render `SupabaseStorage__ServiceKey` to hold a single-line
secret key; the owner is fixing it. Text editing does not depend on it.
