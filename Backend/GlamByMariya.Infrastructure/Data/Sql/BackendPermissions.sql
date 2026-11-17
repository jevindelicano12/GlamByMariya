-- Optional DBA step AFTER production schema initialization, using an owner account.
-- Creates a NOLOGIN group, not a password-bearing login. Never grant it to
-- Supabase anon/authenticated. Grant membership to a server-only login separately.
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='makeup_backend') THEN
        CREATE ROLE makeup_backend NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
    END IF;
END $$;
GRANT USAGE ON SCHEMA makeup TO makeup_backend;
GRANT SELECT ON ALL TABLES IN SCHEMA makeup TO makeup_backend;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA makeup TO makeup_backend;
GRANT INSERT ON makeup.app_users,makeup.catalog_categories,makeup.media_assets,
    makeup.booking_addons,makeup.payment_revisions,makeup.payment_adjustments,
    makeup.booking_closures,makeup.refunds,makeup.booking_status_history,makeup.audit_logs TO makeup_backend;
GRANT INSERT,UPDATE ON makeup.service_areas,makeup.bookings,makeup.payments,
    makeup.reschedule_requests,makeup.notifications TO makeup_backend;
GRANT UPDATE ON makeup.business_settings,makeup.payment_methods TO makeup_backend;
-- SELECT ... FOR UPDATE requires UPDATE on at least one column, even when the
-- artist/closure lock is the only purpose. Closures remain trigger-immutable.
GRANT UPDATE(updated_at) ON makeup.app_users,makeup.booking_closures TO makeup_backend;
GRANT INSERT,UPDATE,DELETE ON makeup.makeup_services,makeup.makeup_addons,
    makeup.catalog_images,makeup.artist_schedule_entries TO makeup_backend;
DO $$ DECLARE t record; BEGIN
    FOR t IN SELECT tablename FROM pg_tables WHERE schemaname='makeup' LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE schemaname='makeup' AND tablename=t.tablename AND policyname='trusted_mvc_backend') THEN
            EXECUTE format('CREATE POLICY trusted_mvc_backend ON makeup.%I TO makeup_backend USING (true) WITH CHECK (true)',t.tablename);
        END IF;
    END LOOP;
END $$;
-- DBA example (choose your own dedicated login; never put its password here):
-- GRANT makeup_backend TO your_server_login;
-- Login must not own the tables, have BYPASSRLS or be used in the browser.
