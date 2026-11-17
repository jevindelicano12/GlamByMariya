START TRANSACTION;
-- EF migration payload for owner-configurable down payments. No BEGIN/COMMIT: EF owns the transaction.
-- Additive only: no table, row, primary key or foreign key is dropped. Legacy reservations keep
-- their stored totals; the snapshot columns are nullable/coalesced so pre-migration bookings keep
-- their exact historical 50% obligation and no payment record is touched.
SET LOCAL lock_timeout='10s';
SET LOCAL statement_timeout='120s';

-- 1. Configurable down payment percentage (1–100, whole numbers enforced by the application).
ALTER TABLE public.business_settings ADD COLUMN IF NOT EXISTS down_payment_percent numeric(5,2) NOT NULL DEFAULT 50;
ALTER TABLE public.business_settings DROP CONSTRAINT IF EXISTS business_settings_down_payment_percent_check;
ALTER TABLE public.business_settings ADD CONSTRAINT business_settings_down_payment_percent_check CHECK(down_payment_percent>=1 AND down_payment_percent<=100 AND floor(down_payment_percent)=down_payment_percent);

-- 2. Booking-time policy snapshots. NULL snapshots mean "legacy booking": every reader coalesces
--    back to the historical rounded 50% rule, so existing obligations never change silently.
ALTER TABLE public.reservations ADD COLUMN IF NOT EXISTS down_payment_percent_snapshot numeric(5,2) NOT NULL DEFAULT 50;
ALTER TABLE public.reservations ADD COLUMN IF NOT EXISTS required_down_payment_snapshot numeric(14,2);

-- 3. Deposit validation now uses the booking's recorded down payment instead of a hardcoded 50%.
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
   PERFORM public.mvc_assert_role(NEW.verified_by,'Admin');IF NEW.verified_at IS NULL THEN RAISE EXCEPTION 'Verification time is required'; END IF;
  ELSIF NEW.verified_by IS NOT NULL OR NEW.verified_at IS NOT NULL THEN RAISE EXCEPTION 'Unverified submissions cannot contain verified evidence'; END IF;
  IF NEW.proof_asset_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM public.media_assets WHERE id=NEW.proof_asset_id AND purpose='payment_proof') THEN RAISE EXCEPTION 'Use a private proof asset'; END IF;
 END IF;
 IF TG_OP='UPDATE' THEN
  INSERT INTO public.payment_revisions(payment_id,old_reference,new_reference,old_proof_asset_id,new_proof_asset_id,old_status,new_status,note,actor_id)
  VALUES(NEW.id,OLD.reference,NEW.reference,OLD.proof_asset_id,NEW.proof_asset_id,OLD.verification_status,NEW.verification_status,NEW.review_note,public.mvc_actor());
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS mvc_payment_guard ON public.payments;
CREATE TRIGGER mvc_payment_guard BEFORE INSERT OR UPDATE ON public.payments FOR EACH ROW EXECUTE FUNCTION public.mvc_payment_guard();

-- 4. Deferred finance integrity uses the same snapshot-aware requirement.
CREATE OR REPLACE FUNCTION public.mvc_finance_integrity() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE rid bigint;r public.reservations%ROWTYPE;paid numeric;
BEGIN
 IF TG_TABLE_NAME='reservations' THEN rid:=NEW.id; ELSE rid:=NEW.reservation_id; END IF;
 SELECT * INTO STRICT r FROM public.reservations WHERE id=rid FOR UPDATE;
 IF NOT r.app_managed THEN RETURN NULL; END IF;
 IF r.addons_total_snapshot<>coalesce((SELECT sum(price_snapshot) FROM public.reservation_addons WHERE reservation_id=rid),0)
 OR r.addons_minutes_snapshot<>coalesce((SELECT sum(minutes_snapshot) FROM public.reservation_addons WHERE reservation_id=rid),0) THEN RAISE EXCEPTION 'Quoted extras disagree with immutable line items'; END IF;
 IF (SELECT count(*) FROM public.payments WHERE reservation_id=rid AND purpose='down_payment')<>1 THEN RAISE EXCEPTION 'Every submitted booking needs exactly one deposit submission'; END IF;
 paid:=public.mvc_paid(rid);
 IF paid<0 OR paid>r.total_price THEN RAISE EXCEPTION 'Recognized payments must stay between zero and booking total'; END IF;
 IF r.status='Pending Payment' AND paid<>0 THEN RAISE EXCEPTION 'Verify and approve/reject in one transaction'; END IF;
 IF r.status IN ('Confirmed','Completed','No-show','Incomplete') AND (paid<coalesce(r.required_down_payment_snapshot,round(r.total_price*.5,2)) OR NOT EXISTS
 (SELECT 1 FROM public.payments WHERE reservation_id=rid AND purpose='down_payment' AND status='Paid')) THEN RAISE EXCEPTION 'Verify the exact required deposit before confirmation'; END IF;
 IF r.status='Completed' AND paid<>r.total_price THEN RAISE EXCEPTION 'Full verified balance is required for completion'; END IF;
 RETURN NULL;
END $$;
DO $$ DECLARE t text;BEGIN FOREACH t IN ARRAY ARRAY['reservations','reservation_addons','payments','payment_adjustments'] LOOP
 EXECUTE format('DROP TRIGGER IF EXISTS mvc_finance ON public.%I',t); EXECUTE format('CREATE CONSTRAINT TRIGGER mvc_finance AFTER INSERT OR UPDATE ON public.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.mvc_finance_integrity()',t);
END LOOP;END $$;

-- 5. Booking summary view exposes the snapshot (coalesced for legacy rows).
CREATE OR REPLACE VIEW public.mvc_booking_summary WITH(security_invoker=true) AS
 SELECT r.app_id AS id,r.reference,c.app_id AS customer_id,a.app_id AS artist_id,s.app_id AS service_id,
 coalesce(r.customer_name_snapshot,c.full_name) AS customer_name_snapshot,coalesce(r.service_name_snapshot,s.name) AS service_name_snapshot,
 r.regular_price_snapshot,r.service_minutes_snapshot,r.group_price_snapshot,r.group_minimum_snapshot,r.booking_type,r.people,r.applied_price_per_person,
 r.total_price AS final_total,coalesce(r.required_down_payment_snapshot,round(r.total_price*.5,2)) AS required_down_payment,
 r.service_minutes_snapshot*r.people+r.addons_minutes_snapshot AS duration_minutes,r.start_at AS starts_at,r.end_at AS ends_at,
 r.buffer_minutes_snapshot,r.time_zone_snapshot,coalesce(r.location,'') AS address,coalesce(r.area_name_snapshot,'Legacy location') AS area_name_snapshot,
 r.travel_fee_informational_snapshot,CASE r.status WHEN 'Pending' THEN 'Pending Payment' WHEN 'InProgress' THEN 'Confirmed' WHEN 'NoShow' THEN 'No-show' ELSE r.status END::text AS status,
 coalesce(r.notes,'') AS note,v.paid AS verified_paid,coalesce(z.cancellation_fee,0)::numeric(14,2) AS cancellation_fee,
 coalesce(z.refund_due,0)::numeric(14,2) AS refund_due,coalesce(f.refunded,0)::numeric(14,2) AS refunded,q.requested_start,
 CASE WHEN z.refund_due>coalesce(f.refunded,0) THEN 'Refund Pending' WHEN z.refund_due>0 THEN 'Refunded'
 WHEN v.paid>=r.total_price THEN 'Paid in Full' WHEN v.paid>=coalesce(r.required_down_payment_snapshot,round(r.total_price*.5,2)) THEN 'Down Payment Verified' ELSE 'Pending Verification' END::text AS payment_status,
 coalesce(r.down_payment_percent_snapshot,50) AS down_payment_percent_snapshot,
 coalesce(r.required_down_payment_snapshot,round(r.total_price*.5,2)) AS required_down_payment_snapshot
 FROM public.reservations r JOIN public.customers c ON c.id=r.customer_id JOIN public.makeup_artists a ON a.id=r.artist_id JOIN public.services s ON s.id=r.service_id
 CROSS JOIN LATERAL(SELECT public.mvc_paid(r.id) AS paid) v LEFT JOIN public.reservation_closures z ON z.reservation_id=r.id
 LEFT JOIN LATERAL(SELECT sum(amount) AS refunded FROM public.refunds WHERE closure_id=z.id) f ON true
 LEFT JOIN public.reschedule_requests q ON q.reservation_id=r.id AND q.status='Pending';


INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
SELECT '20261017140000_DownPaymentPolicy', '10.0.13'
WHERE NOT EXISTS (SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId"='20261017140000_DownPaymentPolicy');

COMMIT;

