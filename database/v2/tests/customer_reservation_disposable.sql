-- Synthetic checks only: run after 0001 and 0002 on the disposable local DB.
BEGIN;
DO $$
BEGIN
    IF current_database() <> 'glam_v2_disposable'
       OR (inet_server_addr() IS NOT NULL AND NOT (inet_server_addr() <<= inet '127.0.0.0/8' OR inet_server_addr() = inet '::1')) THEN
        RAISE EXCEPTION 'Disposable local database required';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public' AND c.relname IN ('customers','reservations')
          AND (NOT c.relrowsecurity OR NOT c.relforcerowsecurity)
    ) OR EXISTS (
        SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename IN ('customers','reservations')
    ) THEN RAISE EXCEPTION 'Unexpected RLS configuration'; END IF;
    IF EXISTS (
        SELECT 1 FROM unnest(ARRAY['anon','authenticated','service_role']) roles(r)
        CROSS JOIN unnest(ARRAY['customers','reservations']) tables(t)
        CROSS JOIN unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER']) privileges(p)
        WHERE has_table_privilege(r, format('public.%I',t), p)
    ) OR EXISTS (
        SELECT 1 FROM unnest(ARRAY['anon','authenticated','service_role']) roles(r)
        CROSS JOIN unnest(ARRAY['customers_id_seq','reservations_id_seq']) sequences(s)
        CROSS JOIN unnest(ARRAY['USAGE','SELECT','UPDATE']) privileges(p)
        WHERE has_sequence_privilege(r, format('public.%I',s), p)
    ) THEN RAISE EXCEPTION 'Unexpected role privilege'; END IF;
END $$;

INSERT INTO public.makeup_artists(full_name) VALUES ('Synthetic artist');
INSERT INTO public.services(name,price,duration_minutes) VALUES ('Synthetic service',1000,60);
INSERT INTO public.customers(full_name) VALUES ('Synthetic customer');

DO $$
DECLARE a bigint; c bigint; s bigint; r bigint;
BEGIN
    SELECT id INTO STRICT a FROM public.makeup_artists WHERE full_name='Synthetic artist';
    SELECT id INTO STRICT c FROM public.customers WHERE full_name='Synthetic customer';
    SELECT id INTO STRICT s FROM public.services WHERE name='Synthetic service';
    INSERT INTO public.reservations(reference,customer_id,service_id,artist_id,start_at,end_at,reserved_until,total_price,status)
    VALUES ('test-pending',c,s,a,'2035-01-01 09:00+00','2035-01-01 10:00+00','2035-01-01 10:45+00',1000,'Pending Payment') RETURNING id INTO r;
    BEGIN
        INSERT INTO public.reservations(reference,customer_id,service_id,artist_id,start_at,end_at,reserved_until,total_price,status)
        VALUES ('test-overlap',c,s,a,'2035-01-01 10:30+00','2035-01-01 11:30+00','2035-01-01 11:30+00',1000,'Pending');
        RAISE EXCEPTION 'Pending Payment buffer did not hold';
    EXCEPTION WHEN exclusion_violation THEN NULL; END;
    UPDATE public.reservations SET status='Rejected' WHERE id=r;
    INSERT INTO public.reservations(reference,customer_id,service_id,artist_id,start_at,end_at,reserved_until,total_price,status)
    VALUES ('test-new-pending',c,s,a,'2035-01-01 09:00+00','2035-01-01 10:00+00','2035-01-01 10:45+00',1000,'Pending');
    BEGIN
        UPDATE public.reservations SET status='Pending Payment' WHERE id=r;
        RAISE EXCEPTION 'Pending reservation did not hold';
    EXCEPTION WHEN exclusion_violation THEN NULL; END;
    INSERT INTO public.reservations(reference,customer_id,service_id,artist_id,start_at,end_at,reserved_until,total_price,status)
    VALUES ('test-adjacent',c,s,a,'2035-01-01 10:45+00','2035-01-01 11:45+00','2035-01-01 11:45+00',1000,'Pending Payment');
    BEGIN
        INSERT INTO public.reservations(reference,customer_id,service_id,artist_id,start_at,end_at,reserved_until,total_price)
        VALUES ('test-invalid',c,s,a,'2035-01-02 09:00+00','2035-01-02 10:00+00','2035-01-02 09:59+00',1000);
        RAISE EXCEPTION 'Invalid buffer accepted';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN
        INSERT INTO public.reservations(reference,customer_id,service_id,artist_id,start_at,end_at,reserved_until,total_price)
        VALUES ('test-bad-owner',-1,s,a,'2035-01-02 09:00+00','2035-01-02 10:00+00','2035-01-02 10:00+00',1000);
        RAISE EXCEPTION 'Unknown customer accepted';
    EXCEPTION WHEN foreign_key_violation THEN NULL; END;
    BEGIN
        INSERT INTO public.reservations(reference,customer_id,service_id,artist_id,start_at,end_at,reserved_until,total_price)
        VALUES ('test-adjacent',c,s,a,'2035-01-03 09:00+00','2035-01-03 10:00+00','2035-01-03 10:00+00',1000);
        RAISE EXCEPTION 'Duplicate reference accepted';
    EXCEPTION WHEN unique_violation THEN NULL; END;
END $$;
ROLLBACK;
