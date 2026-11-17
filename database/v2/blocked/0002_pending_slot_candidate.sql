-- REVIEW ONLY: intentionally aborts before DDL. This is not an executable migration.
-- Reservations, artist IDs, reserved_until and statuses must first be approved and
-- created by a separate migration. Requires btree_gist in an approved schema.
BEGIN;
DO $$ BEGIN RAISE EXCEPTION 'BLOCKED: booking schema and trusted authorization are not approved'; END $$;

-- Design fragment only: existing no_artist_overlap/mvc_travel_overlap constraints
-- and availability/block concurrency must be reconciled in the final migration.
ALTER TABLE public.reservations ADD CONSTRAINT v2_artist_reserved_overlap
    EXCLUDE USING gist
    (artist_id WITH =, tstzrange(start_at, coalesce(reserved_until, end_at), '[)') WITH &&)
    WHERE (status IN ('Pending', 'Pending Payment', 'Confirmed', 'InProgress'));
ROLLBACK;
