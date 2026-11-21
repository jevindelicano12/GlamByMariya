-- Website content the owner edits on the live site: text versions (one draft, one published),
-- social links, FAQs, uploaded image assets and the images placed on each version.
-- Used by PostgresWebsiteContentRepository. Text limits match WebsiteContentRules.

CREATE TABLE public.website_content_versions (
    id                       uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    revision                 bigint      NOT NULL DEFAULT 1 CHECK (revision >= 1),
    status                   text        NOT NULL CHECK (status IN ('draft', 'published', 'archived')),
    hero_headline            text        NOT NULL DEFAULT '' CHECK (char_length(hero_headline) <= 160),
    hero_description         text        NOT NULL DEFAULT '' CHECK (char_length(hero_description) <= 1000),
    about_heading            text        NOT NULL DEFAULT '' CHECK (char_length(about_heading) <= 160),
    about_biography          text        NOT NULL DEFAULT '' CHECK (char_length(about_biography) <= 5000),
    home_service_description text        NOT NULL DEFAULT '' CHECK (char_length(home_service_description) <= 2000),
    service_area_information text        NOT NULL DEFAULT '' CHECK (char_length(service_area_information) <= 1000),
    contact_details          text        NOT NULL DEFAULT '' CHECK (char_length(contact_details) <= 2000),
    created_by               uuid,
    updated_by               uuid,
    published_by             uuid,
    created_at               timestamptz NOT NULL DEFAULT now(),
    updated_at               timestamptz NOT NULL DEFAULT now(),
    published_at             timestamptz
);
-- At most one draft and one published version at any time.
CREATE UNIQUE INDEX ux_website_content_one_draft ON public.website_content_versions (status) WHERE status = 'draft';
CREATE UNIQUE INDEX ux_website_content_one_published ON public.website_content_versions (status) WHERE status = 'published';

CREATE TABLE public.website_content_links (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    version_id uuid NOT NULL REFERENCES public.website_content_versions (id) ON DELETE CASCADE,
    label      text NOT NULL CHECK (char_length(label) BETWEEN 1 AND 100),
    url        text NOT NULL CHECK (char_length(url) BETWEEN 1 AND 2048),
    sort_order integer NOT NULL DEFAULT 0
);
CREATE INDEX ix_website_content_links_version ON public.website_content_links (version_id);

CREATE TABLE public.website_content_faqs (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    version_id uuid NOT NULL REFERENCES public.website_content_versions (id) ON DELETE CASCADE,
    question   text NOT NULL CHECK (char_length(question) BETWEEN 1 AND 300),
    answer     text NOT NULL CHECK (char_length(answer) BETWEEN 1 AND 3000),
    sort_order integer NOT NULL DEFAULT 0
);
CREATE INDEX ix_website_content_faqs_version ON public.website_content_faqs (version_id);

CREATE TABLE public.website_content_assets (
    id           uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    object_key   text        NOT NULL UNIQUE CHECK (object_key ~ '^website-content/[0-9a-f]{32}\.(jpg|png)$'),
    content_type text        NOT NULL CHECK (content_type IN ('image/jpeg', 'image/png')),
    byte_length  integer     NOT NULL CHECK (byte_length BETWEEN 1 AND 5000000),
    uploaded_by  uuid        NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE public.website_content_images (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    version_id uuid NOT NULL REFERENCES public.website_content_versions (id) ON DELETE CASCADE,
    asset_id   uuid NOT NULL REFERENCES public.website_content_assets (id) ON DELETE RESTRICT,
    role       text NOT NULL CHECK (role IN ('hero', 'portrait', 'gallery')),
    alt_text   text NOT NULL CHECK (char_length(alt_text) BETWEEN 1 AND 250),
    caption    text NOT NULL DEFAULT '' CHECK (char_length(caption) <= 500),
    sort_order integer NOT NULL DEFAULT 0 CHECK (sort_order >= 0),
    visible    boolean NOT NULL DEFAULT true
);
CREATE INDEX ix_website_content_images_version ON public.website_content_images (version_id);
CREATE INDEX ix_website_content_images_asset ON public.website_content_images (asset_id);

-- Server-only tables: no browser (anon/authenticated) access.
DO $$
DECLARE t text; backend_role text;
BEGIN
    FOREACH t IN ARRAY ARRAY['website_content_versions', 'website_content_links', 'website_content_faqs', 'website_content_assets', 'website_content_images'] LOOP
        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('REVOKE ALL ON public.%I FROM PUBLIC', t);
        FOREACH backend_role IN ARRAY ARRAY['makeup_backend', 'glam_v2_backend'] LOOP
            IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = backend_role) THEN
                EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON public.%I TO %I', t, backend_role);
                EXECUTE format('DROP POLICY IF EXISTS %I ON public.%I', t || '_' || backend_role, t);
                EXECUTE format('CREATE POLICY %I ON public.%I TO %I USING (true) WITH CHECK (true)', t || '_' || backend_role, t, backend_role);
            END IF;
        END LOOP;
    END LOOP;
END $$;
