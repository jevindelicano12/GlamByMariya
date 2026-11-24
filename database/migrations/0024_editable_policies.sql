-- 0024: owner-editable customer-facing booking policies (null uses the app's default wording).

ALTER TABLE public.business_settings ADD COLUMN IF NOT EXISTS payment_policy text;
ALTER TABLE public.business_settings ADD COLUMN IF NOT EXISTS cancellation_policy text;
ALTER TABLE public.business_settings ADD COLUMN IF NOT EXISTS rescheduling_policy text;
ALTER TABLE public.business_settings ADD COLUMN IF NOT EXISTS transportation_policy text;
ALTER TABLE public.business_settings ADD COLUMN IF NOT EXISTS after_hours_policy text;

ALTER TABLE public.business_settings DROP CONSTRAINT IF EXISTS business_settings_policy_lengths_check;
ALTER TABLE public.business_settings ADD CONSTRAINT business_settings_policy_lengths_check CHECK (
    (payment_policy IS NULL OR char_length(payment_policy) <= 2000) AND
    (cancellation_policy IS NULL OR char_length(cancellation_policy) <= 2000) AND
    (rescheduling_policy IS NULL OR char_length(rescheduling_policy) <= 2000) AND
    (transportation_policy IS NULL OR char_length(transportation_policy) <= 2000) AND
    (after_hours_policy IS NULL OR char_length(after_hours_policy) <= 2000)
);
