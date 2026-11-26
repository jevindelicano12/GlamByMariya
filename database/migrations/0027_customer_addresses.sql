-- 0027: Customer saved addresses junction table (3NF: customers <-> service_areas).
-- Supports multiple saved addresses per customer and saving new booking addresses back to the customer's profile.

CREATE TABLE IF NOT EXISTS public.customer_addresses (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    customer_id bigint NOT NULL REFERENCES public.customers(id) ON DELETE CASCADE,
    area_id uuid REFERENCES public.service_areas(id) ON DELETE SET NULL,
    label varchar(60) NOT NULL DEFAULT 'Saved address',
    address_text text NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    is_default boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customer_addresses_text_check CHECK (length(btrim(address_text)) > 0)
);

CREATE INDEX IF NOT EXISTS ix_customer_addresses_customer
    ON public.customer_addresses(customer_id, is_default DESC, updated_at DESC);

CREATE UNIQUE INDEX IF NOT EXISTS ux_customer_addresses_default
    ON public.customer_addresses(customer_id)
    WHERE is_default;

CREATE UNIQUE INDEX IF NOT EXISTS ux_customer_addresses_unique_text
    ON public.customer_addresses(customer_id, lower(btrim(address_text)));

ALTER TABLE public.customer_addresses ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'makeup_backend') THEN
        DROP POLICY IF EXISTS mvc_backend ON public.customer_addresses;
        CREATE POLICY mvc_backend ON public.customer_addresses TO makeup_backend USING (true) WITH CHECK (true);
        REVOKE ALL ON public.customer_addresses FROM PUBLIC, anon, authenticated;
        GRANT SELECT, INSERT, UPDATE, DELETE ON public.customer_addresses TO makeup_backend;
    END IF;
END $$;

-- Backfill existing non-empty customer profile addresses as their default saved address.
INSERT INTO public.customer_addresses (customer_id, address_text, is_default)
SELECT c.id, btrim(c.address), true
FROM public.customers c
WHERE c.address IS NOT NULL
  AND length(btrim(c.address)) > 0
  AND btrim(c.address) <> 'Address on file'
ON CONFLICT DO NOTHING;
