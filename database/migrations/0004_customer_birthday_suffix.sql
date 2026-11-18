-- 0006: store the registration birthday and name suffix for customers created by the app.
-- Needed by PublicStudioStore.AddCustomerWithPasswordHash (local registration with email OTP).
-- The 0004 lineage already has these columns (birthday NOT NULL); this adds them, nullable,
-- to databases built from PublicIntegration.sql. Older customers keep NULL.
-- NOT yet executed anywhere. Phase 2 decides where this runs alongside the other migrations.
-- Safe to re-run.
SET LOCAL lock_timeout = '10s';
SET LOCAL statement_timeout = '60s';
SET LOCAL search_path = pg_catalog, public;

ALTER TABLE public.customers ADD COLUMN IF NOT EXISTS birthday date;
ALTER TABLE public.customers ADD COLUMN IF NOT EXISTS suffix varchar(10);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public.customers'::regclass AND conname = 'customers_birthday_not_future') THEN
        ALTER TABLE public.customers ADD CONSTRAINT customers_birthday_not_future
            CHECK (birthday IS NULL OR (birthday >= DATE '1900-01-01' AND birthday <= (statement_timestamp() AT TIME ZONE 'Asia/Manila')::date)) NOT VALID;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public.customers'::regclass AND conname = 'customers_suffix_allowed') THEN
        ALTER TABLE public.customers ADD CONSTRAINT customers_suffix_allowed
            CHECK (suffix IS NULL OR suffix IN ('', 'Jr.', 'Sr.', 'II', 'III', 'IV')) NOT VALID;
    END IF;
END $$;

