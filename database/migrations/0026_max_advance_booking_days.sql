-- 0026: Configurable advance booking window (default 30 days; e.g., 15 or 30 days).
ALTER TABLE public.business_settings
  ADD COLUMN IF NOT EXISTS max_advance_booking_days int NOT NULL DEFAULT 30
  CHECK (max_advance_booking_days BETWEEN 1 AND 365);
