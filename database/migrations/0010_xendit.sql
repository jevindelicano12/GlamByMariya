-- Xendit (test mode) down payments: a payment method, invoice fields on payments,
-- an idempotent log of webhook events, and the functions the webhook calls.

-- 'Xendit' as an accepted deposit method.
DO $$ DECLARE c text; BEGIN
 FOR c IN SELECT conname FROM pg_constraint WHERE conrelid='public.payment_methods'::regclass AND contype='c'
   AND pg_get_constraintdef(oid) LIKE '%GCash%' LOOP
  EXECUTE format('ALTER TABLE public.payment_methods DROP CONSTRAINT %I', c);
 END LOOP;
END $$;
ALTER TABLE public.payment_methods ADD CONSTRAINT payment_methods_name_check
 CHECK(name IN ('GCash','Maya','Bank Transfer','Cash','Xendit'));
INSERT INTO public.payment_methods(name, accepts_deposits) VALUES('Xendit', true) ON CONFLICT (name) DO NOTHING;

-- Invoice details on the deposit row.
ALTER TABLE public.payments ADD COLUMN IF NOT EXISTS provider text,
 ADD COLUMN IF NOT EXISTS provider_invoice_id text, ADD COLUMN IF NOT EXISTS provider_status text,
 ADD COLUMN IF NOT EXISTS checkout_url text;
CREATE UNIQUE INDEX IF NOT EXISTS ux_payments_provider_invoice ON public.payments(provider_invoice_id) WHERE provider_invoice_id IS NOT NULL;

-- One row per webhook event: a repeated delivery is recognised and ignored.
CREATE TABLE IF NOT EXISTS public.payment_webhook_events(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), event_key text NOT NULL UNIQUE, invoice_id text NOT NULL,
 status text NOT NULL, outcome text NOT NULL DEFAULT '', received_at timestamptz NOT NULL DEFAULT now());
ALTER TABLE public.payment_webhook_events ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS mvc_backend ON public.payment_webhook_events;
CREATE POLICY mvc_backend ON public.payment_webhook_events TO makeup_backend USING (true) WITH CHECK (true);
GRANT SELECT, INSERT, UPDATE ON public.payment_webhook_events TO makeup_backend;

CREATE OR REPLACE FUNCTION public.mvc_payment_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE r public.reservations%ROWTYPE; m public.payment_methods%ROWTYPE;
BEGIN
 SELECT * INTO STRICT r FROM public.reservations WHERE id=NEW.reservation_id FOR UPDATE;
 IF TG_OP='UPDATE' AND (OLD.status IN ('Paid','Refunded') OR ROW(NEW.reservation_id,NEW.app_id,NEW.app_managed,NEW.amount,NEW.method,NEW.purpose)
 IS DISTINCT FROM ROW(OLD.reservation_id,OLD.app_id,OLD.app_managed,OLD.amount,OLD.method,OLD.purpose)) THEN RAISE EXCEPTION 'Verified payment evidence is immutable; record a signed adjustment instead'; END IF;
 IF r.status NOT IN ('Pending','Pending Payment','Confirmed','InProgress') THEN RAISE EXCEPTION 'Booking is closed'; END IF;
 IF NEW.app_managed THEN
  SELECT * INTO STRICT m FROM public.payment_methods WHERE name=NEW.method;
  IF length(btrim(coalesce(NEW.reference,''))) NOT BETWEEN 1 AND 150 THEN RAISE EXCEPTION 'Provide the actual payment reference'; END IF;
  IF TG_OP='INSERT' AND NOT m.is_active THEN RAISE EXCEPTION 'Payment method is inactive'; END IF;
  IF NEW.purpose='down_payment' AND (NOT m.accepts_deposits OR NEW.amount<>coalesce(r.required_down_payment_snapshot,round(r.total_price*.5,2)) OR r.status NOT IN ('Pending','Pending Payment'))
   THEN RAISE EXCEPTION 'Deposit must equal the required down payment (the booking''s recorded percentage of the total) and use an accepted online method'; END IF;
  IF NEW.purpose='balance' AND (NEW.method<>'Cash' OR NEW.status<>'Paid' OR r.status NOT IN ('Confirmed','InProgress') OR r.end_at>clock_timestamp()
   OR NEW.amount<>r.total_price-public.mvc_paid(r.id)) THEN RAISE EXCEPTION 'Record the exact final Cash balance after the appointment'; END IF;
  IF NEW.purpose NOT IN ('down_payment','balance') OR NEW.purpose IS NULL THEN RAISE EXCEPTION 'Invalid payment purpose'; END IF;
  IF NEW.verification_status NOT IN ('Pending Verification','Correction Requested','Verified','Rejected') OR NEW.verification_status IS NULL THEN RAISE EXCEPTION 'Invalid payment review status'; END IF;
  IF (NEW.status='Paid')<>(NEW.verification_status='Verified') THEN RAISE EXCEPTION 'Payment verification states disagree'; END IF;
  IF NEW.status='Paid' THEN
   -- Xendit payments are verified by the signed webhook, not by an admin.
   IF NEW.method='Xendit' THEN
    IF NEW.provider_invoice_id IS NULL OR NEW.provider_status IS DISTINCT FROM 'PAID' THEN RAISE EXCEPTION 'Xendit payments are confirmed only by a paid Xendit invoice'; END IF;
   ELSE PERFORM public.mvc_assert_role(NEW.verified_by,'Admin'); END IF;
   IF NEW.verified_at IS NULL THEN RAISE EXCEPTION 'Verification time is required'; END IF;
  ELSIF NEW.verified_by IS NOT NULL OR NEW.verified_at IS NOT NULL THEN RAISE EXCEPTION 'Unverified submissions cannot contain verified evidence'; END IF;
  IF NEW.proof_asset_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM public.media_assets WHERE id=NEW.proof_asset_id AND purpose='payment_proof') THEN RAISE EXCEPTION 'Use a private proof asset'; END IF;
 END IF;
 IF TG_OP='UPDATE' THEN
  INSERT INTO public.payment_revisions(payment_id,old_reference,new_reference,old_proof_asset_id,new_proof_asset_id,old_status,new_status,note,actor_id)
  VALUES(NEW.id,OLD.reference,NEW.reference,OLD.proof_asset_id,NEW.proof_asset_id,OLD.verification_status,NEW.verification_status,NEW.review_note,public.mvc_actor());
 END IF;
 RETURN NEW;
END $$;

-- Webhook PAID / SETTLED: confirm the booking when the paid amount equals the deposit.
-- Returns 'confirmed', 'already' (paid before), 'duplicate' (same event again) or 'late'
-- (paid after the booking expired or was cancelled: recorded for a manual refund).
CREATE OR REPLACE FUNCTION public.mvc_xendit_paid(p_invoice text, p_paid numeric, p_event text) RETURNS text
LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE p public.payments%ROWTYPE; r public.reservations%ROWTYPE;
BEGIN
 INSERT INTO public.payment_webhook_events(event_key, invoice_id, status) VALUES(p_event, p_invoice, 'PAID') ON CONFLICT (event_key) DO NOTHING;
 IF NOT FOUND THEN RETURN 'duplicate'; END IF;
 SELECT * INTO p FROM public.payments WHERE provider_invoice_id=p_invoice FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown Xendit invoice'; END IF;
 IF p_paid<>p.amount THEN RAISE EXCEPTION 'Paid amount does not match the required down payment'; END IF;
 SELECT * INTO r FROM public.reservations WHERE id=p.reservation_id FOR UPDATE;
 IF p.status='Paid' THEN UPDATE public.payment_webhook_events SET outcome='already' WHERE payment_webhook_events.event_key=p_event; RETURN 'already'; END IF;
 IF r.status<>'Pending Payment' THEN
  UPDATE public.payment_webhook_events SET outcome='late' WHERE payment_webhook_events.event_key=p_event;
  RETURN 'late';
 END IF;
 UPDATE public.payments SET status='Paid', verification_status='Verified', verified_at=clock_timestamp(),
  provider_status='PAID', review_note='Paid through Xendit' WHERE id=p.id;
 UPDATE public.reservations SET status='Confirmed', notes='Down payment received through Xendit.' WHERE id=r.id;
 UPDATE public.payment_webhook_events SET outcome='confirmed' WHERE payment_webhook_events.event_key=p_event;
 RETURN 'confirmed';
END $$;

-- Webhook EXPIRED: note it; the unpaid booking expires through mvc_expire_overdue.
CREATE OR REPLACE FUNCTION public.mvc_xendit_expired(p_invoice text, p_event text) RETURNS text
LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
BEGIN
 INSERT INTO public.payment_webhook_events(event_key, invoice_id, status, outcome) VALUES(p_event, p_invoice, 'EXPIRED', 'noted') ON CONFLICT (event_key) DO NOTHING;
 IF NOT FOUND THEN RETURN 'duplicate'; END IF;
 PERFORM public.mvc_expire_overdue();
 RETURN 'noted';
END $$;

REVOKE ALL ON FUNCTION public.mvc_xendit_paid(text, numeric, text), public.mvc_xendit_expired(text, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.mvc_xendit_paid(text, numeric, text), public.mvc_xendit_expired(text, text), public.mvc_payment_guard() TO makeup_backend;
