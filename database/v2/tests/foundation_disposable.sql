-- Run only AFTER 0001, in a disposable local PostgreSQL database named exactly
-- glam_v2_disposable with synthetic anon/authenticated/service_role roles. Never on Supabase.
-- Run as a disposable local superuser (FORCE RLS deliberately denies ordinary roles).
BEGIN;
DO $$
BEGIN
    IF current_database() <> 'glam_v2_disposable'
       OR (inet_server_addr() IS NOT NULL AND NOT (inet_server_addr() <<= inet '127.0.0.0/8' OR inet_server_addr() = inet '::1')) THEN
        RAISE EXCEPTION 'Disposable local database required';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public' AND c.relname IN ('makeup_artists','services','artist_availability','blocked_slots')
          AND (NOT c.relrowsecurity OR NOT c.relforcerowsecurity)
    ) OR (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
          WHERE n.nspname = 'public' AND c.relkind = 'r'
            AND c.relname IN ('makeup_artists','services','artist_availability','blocked_slots')) <> 4 THEN
        RAISE EXCEPTION 'Foundation tables or forced RLS missing';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_policies WHERE schemaname = 'public'
          AND tablename IN ('makeup_artists','services','artist_availability','blocked_slots')
    ) THEN RAISE EXCEPTION 'Unexpected policy'; END IF;
    IF EXISTS (
        SELECT 1
        FROM unnest(ARRAY['anon', 'authenticated', 'service_role']) AS roles(role_name)
        CROSS JOIN unnest(ARRAY['makeup_artists', 'services', 'artist_availability', 'blocked_slots']) AS tables(table_name)
        CROSS JOIN unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS privileges(privilege_name)
        WHERE has_table_privilege(role_name, format('public.%I', table_name), privilege_name)
    ) OR EXISTS (
        SELECT 1
        FROM unnest(ARRAY['anon', 'authenticated', 'service_role']) AS roles(role_name)
        CROSS JOIN unnest(ARRAY['makeup_artists_id_seq', 'services_id_seq', 'artist_availability_id_seq', 'blocked_slots_id_seq']) AS sequences(sequence_name)
        CROSS JOIN unnest(ARRAY['USAGE', 'SELECT', 'UPDATE']) AS privileges(privilege_name)
        WHERE has_sequence_privilege(role_name, format('public.%I', sequence_name), privilege_name)
    ) THEN
        RAISE EXCEPTION 'Unexpected API role privilege';
    END IF;
END $$;
INSERT INTO public.makeup_artists (full_name) VALUES ('Synthetic Artist');
INSERT INTO public.services (name, price, duration_minutes) VALUES ('Synthetic Service', 600, 60);
INSERT INTO public.artist_availability (artist_id, weekday, start_time, end_time)
    SELECT id, 1, '08:00', '18:00' FROM public.makeup_artists WHERE full_name = 'Synthetic Artist';
INSERT INTO public.blocked_slots (artist_id, start_at, end_at)
    SELECT id, '2035-01-01 09:00+00', '2035-01-01 10:00+00' FROM public.makeup_artists WHERE full_name = 'Synthetic Artist';
DO $$
BEGIN
    BEGIN
        INSERT INTO public.services(name, price, duration_minutes) VALUES ('Invalid', -1, 60);
        RAISE EXCEPTION 'Negative price unexpectedly accepted';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN
        INSERT INTO public.artist_availability(artist_id, weekday, start_time, end_time)
        SELECT id, 7, '08:00', '18:00' FROM public.makeup_artists WHERE full_name = 'Synthetic Artist';
        RAISE EXCEPTION 'Invalid weekday unexpectedly accepted';
    EXCEPTION WHEN check_violation THEN NULL; END;
END $$;
ROLLBACK;
