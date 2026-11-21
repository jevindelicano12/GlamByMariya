-- 0016: remove the manual payment QR feature. Deposits are paid through Xendit,
-- so payment methods no longer carry an uploaded QR image.
-- Apply AFTER deploying code that no longer maps payment_methods.qr_asset_id.
-- Safe to re-run.

ALTER TABLE public.payment_methods DROP CONSTRAINT IF EXISTS payment_methods_check1;
ALTER TABLE public.payment_methods DROP CONSTRAINT IF EXISTS payment_methods_qr_asset_id_fkey;
ALTER TABLE public.payment_methods DROP COLUMN IF EXISTS qr_asset_id;

-- Any leftover QR image records (none expected) go before the purpose list narrows.
DELETE FROM public.media_assets WHERE purpose = 'payment_qr';
ALTER TABLE public.media_assets DROP CONSTRAINT IF EXISTS media_assets_purpose_check;
ALTER TABLE public.media_assets ADD CONSTRAINT media_assets_purpose_check
    CHECK (purpose IN ('catalog', 'payment_proof'));
