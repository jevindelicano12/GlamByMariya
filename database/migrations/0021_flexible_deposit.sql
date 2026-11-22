-- 0021: flexible down payment (Phase 7).
-- The customer may pay from the required down payment (the booking's recorded percentage of the total)
-- up to the full booking total; Xendit invoices the recorded amount and mvc_xendit_paid already
-- compares the paid amount with it. Only the down-payment amount rule changes (0020 version otherwise).

CREATE OR REPLACE FUNCTION public.mvc_payment_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE r public.reservations%ROWTYPE; m public.payment_methods%ROWTYPE;
BEGIN
 SELECT * INTO STRICT r FROM public.reservations WHERE id=NEW.reservation_id FOR UPDATE;
 IF TG_OP='UPDATE' AND (OLD.status IN ('Paid','Refunded') OR ROW(NEW.reservation_id,NEW.app_id,NEW.app_managed,NEW.amount,NEW.method,NEW.purpose)
 IS DISTINCT FROM ROW(OLD.reservation_id,OLD.app_id,OLD.app_managed,OLD.amount,OLD.method,OLD.purpose)) THEN RAISE EXCEPTION 'Verified payment evidence is immutable; record a signed adjustment instead'; END IF;
 IF r.status NOT IN ('Pending','Pending Payment','Awaiting Approval','Confirmed','InProgress') THEN RAISE EXCEPTION 'Booking is closed'; END IF;
 IF NEW.app_managed THEN
  SELECT * INTO STRICT m FROM public.payment_methods WHERE name=NEW.method;
  IF length(btrim(coalesce(NEW.reference,''))) NOT BETWEEN 1 AND 150 THEN RAISE EXCEPTION 'Provide the actual payment reference'; END IF;
  IF TG_OP='INSERT' AND NOT m.is_active THEN RAISE EXCEPTION 'Payment method is inactive'; END IF;
  IF NEW.purpose='down_payment' AND (NOT m.accepts_deposits OR NEW.amount<coalesce(r.required_down_payment_snapshot,round(r.total_price*.5,2))
   OR NEW.amount>r.total_price OR r.status NOT IN ('Pending','Pending Payment','Awaiting Approval'))
   THEN RAISE EXCEPTION 'The down payment must be at least the required down payment (the booking''s recorded percentage of the total), at most the booking total, and use an accepted online method'; END IF;
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
