-- Run only against a verified disposable local database after 0001, 0002, 0003.
BEGIN;
DO $$
DECLARE a bigint; s bigint; c bigint; r bigint; p bigint; r_min bigint; r_full bigint;
BEGIN
    IF current_database()<>'glam_v2_disposable'
       OR (inet_server_addr() IS NOT NULL AND NOT (inet_server_addr() <<= inet '127.0.0.0/8' OR inet_server_addr()=inet '::1')) THEN
        RAISE EXCEPTION 'Disposable local database required';
    END IF;
    IF EXISTS (
        SELECT 1 FROM unnest(ARRAY['anon','authenticated','service_role']) roles(n)
        CROSS JOIN unnest(ARRAY['payments','payment_revisions']) tables(t)
        CROSS JOIN unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER']) privileges(p)
        WHERE has_table_privilege(roles.n,format('public.%I',tables.t),privileges.p)
    ) OR EXISTS (
        SELECT 1 FROM unnest(ARRAY['anon','authenticated','service_role']) roles(n)
        CROSS JOIN unnest(ARRAY['payments_id_seq','payment_revisions_id_seq']) sequences(s)
        CROSS JOIN unnest(ARRAY['USAGE','SELECT','UPDATE']) privileges(p)
        WHERE has_sequence_privilege(roles.n,format('public.%I',sequences.s),privileges.p)
    ) OR EXISTS (
        SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public' AND c.relname IN ('payments','payment_revisions')
          AND (NOT c.relrowsecurity OR NOT c.relforcerowsecurity)
    ) OR EXISTS (
        SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename IN ('payments','payment_revisions')
    ) OR EXISTS (
        SELECT 1 FROM unnest(ARRAY['anon','authenticated','service_role']) roles(n)
        CROSS JOIN unnest(ARRAY['public.v2_payment_claim_guard()','public.v2_payment_revision_guard()','public.v2_payment_immutable()']) funcs(f)
        WHERE has_function_privilege(roles.n,funcs.f,'EXECUTE')
    ) THEN RAISE EXCEPTION 'Unexpected ledger access'; END IF;
    INSERT INTO public.makeup_artists(full_name) VALUES ('Synthetic artist') RETURNING id INTO a;
    INSERT INTO public.services(name,price,duration_minutes) VALUES ('Synthetic service',1000,60) RETURNING id INTO s;
    INSERT INTO public.customers(full_name) VALUES ('Synthetic customer') RETURNING id INTO c;
    INSERT INTO public.reservations(reference,customer_id,service_id,artist_id,start_at,end_at,reserved_until,total_price)
        VALUES ('synthetic',c,s,a,'2035-01-01 09:00+00','2035-01-01 10:00+00','2035-01-01 10:45+00',1000) RETURNING id INTO r;
    INSERT INTO public.reservations(reference,customer_id,service_id,artist_id,start_at,end_at,reserved_until,total_price)
        VALUES ('synthetic-min',c,s,a,'2035-01-02 09:00+00','2035-01-02 10:00+00','2035-01-02 10:45+00',1000) RETURNING id INTO r_min;
    INSERT INTO public.reservations(reference,customer_id,service_id,artist_id,start_at,end_at,reserved_until,total_price)
        VALUES ('synthetic-full',c,s,a,'2035-01-03 09:00+00','2035-01-03 10:00+00','2035-01-03 10:45+00',1000) RETURNING id INTO r_full;
    INSERT INTO public.payments(reservation_id,amount,minimum_snapshot,total_snapshot,method) VALUES(r_min,500,500,1000,'GCash');
    INSERT INTO public.payments(reservation_id,amount,minimum_snapshot,total_snapshot,method) VALUES(r_full,1000,500,1000,'GCash');
    BEGIN
        INSERT INTO public.payments(reservation_id,amount,minimum_snapshot,total_snapshot,method) VALUES(r,499,500,1000,'GCash');
        RAISE EXCEPTION 'Below-minimum claim accepted';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN
        INSERT INTO public.payments(reservation_id,amount,minimum_snapshot,total_snapshot,method) VALUES(r,1001,500,1000,'GCash');
        RAISE EXCEPTION 'Above-total claim accepted';
    EXCEPTION WHEN check_violation THEN NULL; END;
    INSERT INTO public.payments(reservation_id,amount,minimum_snapshot,total_snapshot,method)
        VALUES(r,750,500,1000,'GCash') RETURNING id INTO p;
    INSERT INTO public.payment_revisions(payment_id,revision,reference,verification_status)
        VALUES(p,0,'SYNTHETIC-1','Pending Verification');
    BEGIN
        UPDATE public.payment_revisions SET reference='CHANGED' WHERE payment_id=p;
        RAISE EXCEPTION 'Ledger update accepted';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM <> 'Payment ledger is append-only' THEN RAISE; END IF;
    END;
    INSERT INTO public.payment_revisions(payment_id,revision,reference,proof_asset_id,verification_status)
        VALUES(p,1,'SYNTHETIC-2','00000000-0000-0000-0000-000000000001','Pending Verification');
    BEGIN
        INSERT INTO public.payment_revisions(payment_id,revision,reference,verification_status)
            VALUES(p,1,'STALE','Pending Verification');
        RAISE EXCEPTION 'Stale revision accepted';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM <> 'Stale payment revision' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO public.payment_revisions(payment_id,revision,reference,proof_asset_id,verification_status)
            VALUES(p,2,'FORGED','00000000-0000-0000-0000-000000000001','Verified');
        RAISE EXCEPTION 'Verification changed evidence';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM <> 'Verification must use the existing evidence' THEN RAISE; END IF;
    END;
    INSERT INTO public.payment_revisions(payment_id,revision,reference,proof_asset_id,verification_status)
        VALUES(p,2,'SYNTHETIC-2','00000000-0000-0000-0000-000000000001','Verified');
    BEGIN
        INSERT INTO public.payment_revisions(payment_id,revision,reference,verification_status)
            VALUES(p,3,'LATE','Pending Verification');
        RAISE EXCEPTION 'Verified evidence changed';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM <> 'Verified evidence is immutable' THEN RAISE; END IF;
    END;
    IF (SELECT status FROM public.reservations WHERE id=r)<>'Pending Payment' THEN
        RAISE EXCEPTION 'Payment revision changed reservation slot status';
    END IF;
END $$;
ROLLBACK;
