-- 0020: flexible booking (Phase 6).
-- * Admin-set early-request start and latest booking end (NULL = opening/closing hour).
-- * Per-service limit in one package booking (NULL = no limit); Bridal Makeup starts at 1.
-- * Early booking requests: status 'Awaiting Approval', no payment deadline and no hold until the
--   admin approves; approval moves to 'Pending Payment' with the 15-minute deadline and the slot check.
-- * After-hours flag (the makeup session ends after closing).
-- * Per-person rows for mixed packages (written by Task 4).
-- Additive: every new column defaults to today's behaviour. Apply before deploying the code.

ALTER TABLE public.business_settings
  ADD COLUMN IF NOT EXISTS early_request_start_hour int,
  ADD COLUMN IF NOT EXISTS after_hours_end_hour int;
ALTER TABLE public.business_settings DROP CONSTRAINT IF EXISTS business_settings_flexible_hours_check;
ALTER TABLE public.business_settings ADD CONSTRAINT business_settings_flexible_hours_check CHECK (
  (early_request_start_hour IS NULL OR early_request_start_hour BETWEEN 0 AND open_hour)
  AND (after_hours_end_hour IS NULL OR after_hours_end_hour BETWEEN close_hour AND 24));

ALTER TABLE public.services ADD COLUMN IF NOT EXISTS max_per_package int;
ALTER TABLE public.services DROP CONSTRAINT IF EXISTS services_max_per_package_check;
ALTER TABLE public.services ADD CONSTRAINT services_max_per_package_check CHECK (max_per_package IS NULL OR max_per_package >= 1);
UPDATE public.services SET max_per_package = 1 WHERE name = 'Bridal Makeup' AND max_per_package IS NULL;
CREATE OR REPLACE VIEW public.mvc_services WITH(security_invoker=true) AS SELECT app_id AS id,category_id,name,coalesce(description,'') AS description,price,
 duration_minutes AS minutes,group_price,group_minimum_people,visual_word,is_active,is_archived,max_per_package FROM public.services;

ALTER TABLE public.reservations
  ADD COLUMN IF NOT EXISTS is_early_request boolean NOT NULL DEFAULT false,
  ADD COLUMN IF NOT EXISTS after_hours boolean NOT NULL DEFAULT false,
  -- A mixed package (booking_type mixed) carries its summed price and minutes; reservation_people must agree.
  ADD COLUMN IF NOT EXISTS package_total_snapshot numeric(14,2),
  ADD COLUMN IF NOT EXISTS package_minutes_snapshot int;
ALTER TABLE public.reservations DROP CONSTRAINT IF EXISTS reservations_status_check;
ALTER TABLE public.reservations ADD CONSTRAINT reservations_status_check CHECK(status IN
 ('Pending','Pending Payment','Awaiting Approval','Confirmed','InProgress','Completed','Cancelled','Rejected','NoShow','No-show','Incomplete','Expired'));

CREATE TABLE IF NOT EXISTS public.reservation_people(
  id bigserial PRIMARY KEY,
  reservation_id bigint NOT NULL REFERENCES public.reservations(id),
  position int NOT NULL CHECK (position BETWEEN 1 AND 20),
  service_id bigint NOT NULL REFERENCES public.services(id),
  service_name_snapshot text NOT NULL,
  price_snapshot numeric(14,2) NOT NULL CHECK (price_snapshot >= 0),
  minutes_snapshot int NOT NULL CHECK (minutes_snapshot > 0),
  UNIQUE (reservation_id, position));
CREATE TABLE IF NOT EXISTS public.reservation_people_addons(
  id bigserial PRIMARY KEY,
  person_id bigint NOT NULL REFERENCES public.reservation_people(id),
  addon_id uuid NOT NULL REFERENCES public.makeup_addons(id),
  name_snapshot text NOT NULL,
  price_snapshot numeric(14,2) NOT NULL CHECK (price_snapshot >= 0),
  minutes_snapshot int NOT NULL CHECK (minutes_snapshot >= 0));
CREATE INDEX IF NOT EXISTS ix_reservation_people_reservation ON public.reservation_people(reservation_id);
CREATE INDEX IF NOT EXISTS ix_reservation_people_addons_person ON public.reservation_people_addons(person_id);
ALTER TABLE public.reservation_people ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.reservation_people_addons ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS mvc_backend ON public.reservation_people;
CREATE POLICY mvc_backend ON public.reservation_people TO makeup_backend USING (true) WITH CHECK (true);
DROP POLICY IF EXISTS mvc_backend ON public.reservation_people_addons;
CREATE POLICY mvc_backend ON public.reservation_people_addons TO makeup_backend USING (true) WITH CHECK (true);
REVOKE ALL ON public.reservation_people, public.reservation_people_addons FROM PUBLIC, anon, authenticated;
GRANT SELECT, INSERT ON public.reservation_people, public.reservation_people_addons TO makeup_backend;
GRANT USAGE, SELECT ON SEQUENCE public.reservation_people_id_seq, public.reservation_people_addons_id_seq TO makeup_backend;

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
  coalesce(r.required_down_payment_snapshot,round(r.total_price*.5,2)) AS required_down_payment_snapshot,
  r.travel_reminder_acknowledged_at AS transportation_policy_acknowledged_at,
  r.location_lat,r.location_lng,r.is_early_request,r.after_hours
 FROM public.reservations r JOIN public.customers c ON c.id=r.customer_id JOIN public.makeup_artists a ON a.id=r.artist_id JOIN public.services s ON s.id=r.service_id
 CROSS JOIN LATERAL(SELECT public.mvc_paid(r.id) AS paid) v LEFT JOIN public.reservation_closures z ON z.reservation_id=r.id
 LEFT JOIN LATERAL(SELECT sum(amount) AS refunded FROM public.refunds WHERE closure_id=z.id) f ON true
 LEFT JOIN public.reschedule_requests q ON q.reservation_id=r.id AND q.status='Pending';

-- The slot check now allows the admin-set early start and latest end (0019 version otherwise).
CREATE OR REPLACE FUNCTION public.mvc_check_slot(aid bigint,starts timestamptz,ends timestamptz,buffer int,excluded bigint DEFAULT NULL)
RETURNS void LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE s public.business_settings%ROWTYPE; local_start timestamp; local_end timestamp; finish timestamptz;
BEGIN
 PERFORM 1 FROM public.makeup_artists WHERE id=aid AND is_active FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Choose an active makeup artist'; END IF;
 PERFORM public.mvc_expire_overdue(excluded);
 SELECT * INTO STRICT s FROM public.business_settings WHERE id=1;
 finish:=ends+make_interval(mins=>buffer);local_start:=starts AT TIME ZONE s.time_zone;local_end:=finish AT TIME ZONE s.time_zone;
 IF starts<=clock_timestamp() OR ends<=starts OR ends-starts>interval '24 hours' OR buffer NOT BETWEEN 0 AND 180
 OR local_start<date_trunc('day',local_start)+make_interval(hours=>coalesce(s.early_request_start_hour,s.open_hour))
 OR local_end>date_trunc('day',local_start)+make_interval(hours=>coalesce(s.after_hours_end_hour,s.close_hour)) THEN RAISE EXCEPTION 'Choose a future session that fits the studio hours including travel time'; END IF;
 IF EXISTS(SELECT 1 FROM public.artist_availability WHERE artist_id=aid) AND NOT EXISTS
 (SELECT 1 FROM public.artist_availability WHERE artist_id=aid AND weekday=extract(dow FROM local_start)::int
 AND local_start>=local_start::date+start_time AND local_end<=local_start::date+end_time) THEN RAISE EXCEPTION 'The artist is outside weekly availability'; END IF;
 IF EXISTS(SELECT 1 FROM public.blocked_slots WHERE artist_id=aid AND starts<end_at AND finish>start_at)
 OR EXISTS(SELECT 1 FROM public.reservations WHERE artist_id=aid AND id IS DISTINCT FROM excluded AND status IN ('Pending','Pending Payment','Confirmed','InProgress')
 AND starts<coalesce(reserved_until,end_at) AND finish>start_at) THEN RAISE EXCEPTION 'This slot overlaps a reserved appointment. Please choose another time.'; END IF;
END $$;

-- Guard: 0019 plus the early-request insert path and the Awaiting Approval transitions.
CREATE OR REPLACE FUNCTION public.mvc_reservation_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE s public.business_settings%ROWTYPE; svc public.services%ROWTYPE; coverage public.service_areas%ROWTYPE;
BEGIN
 IF TG_OP='INSERT' AND NEW.app_managed THEN
  -- An early booking request waits for the artist's approval; everything else waits for payment.
  IF NEW.is_early_request THEN
   IF NEW.status<>'Awaiting Approval' THEN RAISE EXCEPTION 'An early booking request must await the artist''s approval'; END IF;
  ELSIF NEW.status<>'Pending Payment' THEN RAISE EXCEPTION 'New requests must await payment verification'; END IF;
  -- Every new app booking carries the customer's exact pin (the Metro Manila box is the table CHECK from 0013).
  IF NEW.location_lat IS NULL OR NEW.location_lng IS NULL THEN RAISE EXCEPTION 'Pin your exact location inside Metro Manila.'; END IF;
  -- A request has no payment deadline and holds no time until it is approved.
  NEW.payment_expires_at:=CASE WHEN NEW.is_early_request THEN NULL ELSE clock_timestamp()+interval '15 minutes' END;
  SELECT * INTO STRICT s FROM public.business_settings WHERE id=1;
  SELECT * INTO STRICT svc FROM public.services WHERE id=NEW.service_id AND is_active AND NOT is_archived;
  SELECT * INTO STRICT coverage FROM public.service_areas WHERE id=NEW.area_id AND is_active;
  IF NOT EXISTS(SELECT 1 FROM public.customers WHERE id=NEW.customer_id AND is_active) THEN RAISE EXCEPTION 'An active customer is required'; END IF;
  IF NEW.people NOT BETWEEN 1 AND 20 OR NEW.booking_type NOT IN ('individual','package','mixed') OR (NEW.booking_type='individual' AND NEW.people<>1)
  OR (NEW.booking_type='mixed' AND (NEW.people<2 OR NEW.package_total_snapshot IS NULL OR NEW.package_total_snapshot<=0 OR NEW.package_minutes_snapshot IS NULL OR NEW.package_minutes_snapshot<=0))
  OR (NEW.booking_type='package' AND svc.group_price IS NULL) THEN RAISE EXCEPTION 'Invalid party size or package selection'; END IF;
  IF NEW.policy_accepted_at IS NULL OR NEW.travel_reminder_acknowledged_at IS NULL OR NEW.policy_version IS DISTINCT FROM s.policy_version
  OR NEW.travel_reminder_version IS DISTINCT FROM s.travel_reminder_version THEN RAISE EXCEPTION 'Accept the current policies and travel fee reminder'; END IF;
  IF length(btrim(coalesce(NEW.location,''))) NOT BETWEEN 8 AND 500 OR svc.price<=0 THEN RAISE EXCEPTION 'Provide a valid address and active priced service'; END IF;
  NEW.snapshot_source:='booking_creation';NEW.customer_name_snapshot:=(SELECT full_name FROM public.customers WHERE id=NEW.customer_id);
  NEW.service_name_snapshot:=svc.name;NEW.regular_price_snapshot:=svc.price;NEW.service_minutes_snapshot:=svc.duration_minutes;
  NEW.group_price_snapshot:=svc.group_price;NEW.group_minimum_snapshot:=svc.group_minimum_people;
  NEW.applied_price_per_person:=CASE WHEN NEW.booking_type='package' AND NEW.people>=svc.group_minimum_people THEN svc.group_price ELSE svc.price END;
  NEW.buffer_minutes_snapshot:=s.buffer_minutes;NEW.time_zone_snapshot:=s.time_zone;NEW.travel_fee_informational_snapshot:=0;
  NEW.area_name_snapshot:=coverage.name;
  IF NEW.addons_total_snapshot<0 OR NEW.addons_minutes_snapshot<0 THEN RAISE EXCEPTION 'Invalid extras quote'; END IF;
  NEW.total_price:=CASE WHEN NEW.booking_type='mixed' THEN NEW.package_total_snapshot ELSE NEW.applied_price_per_person*NEW.people END+NEW.addons_total_snapshot;
  NEW.end_at:=NEW.start_at+make_interval(mins=>CASE WHEN NEW.booking_type='mixed' THEN NEW.package_minutes_snapshot ELSE NEW.service_minutes_snapshot*NEW.people END+NEW.addons_minutes_snapshot);
  NEW.reserved_until:=NEW.end_at+make_interval(mins=>NEW.buffer_minutes_snapshot);
  IF NOT NEW.is_early_request THEN PERFORM public.mvc_check_slot(NEW.artist_id,NEW.start_at,NEW.end_at,NEW.buffer_minutes_snapshot,NULL); END IF;
 END IF;
 IF TG_OP='UPDATE' THEN
  IF ROW(NEW.app_id,NEW.app_managed,NEW.customer_id,NEW.artist_id,NEW.service_id,NEW.reference,NEW.location,NEW.total_price,
   NEW.regular_price_snapshot,NEW.applied_price_per_person,NEW.people,NEW.booking_type,NEW.service_name_snapshot,NEW.service_minutes_snapshot,
   NEW.group_price_snapshot,NEW.group_minimum_snapshot,NEW.addons_total_snapshot,NEW.addons_minutes_snapshot,NEW.buffer_minutes_snapshot,
   NEW.area_id,NEW.area_name_snapshot,NEW.travel_fee_informational_snapshot,NEW.policy_accepted_at,NEW.policy_version,
   NEW.travel_reminder_acknowledged_at,NEW.travel_reminder_version,NEW.time_zone_snapshot,NEW.customer_name_snapshot,NEW.package_total_snapshot,NEW.package_minutes_snapshot)
  IS DISTINCT FROM ROW(OLD.app_id,OLD.app_managed,OLD.customer_id,OLD.artist_id,OLD.service_id,OLD.reference,OLD.location,OLD.total_price,
   OLD.regular_price_snapshot,OLD.applied_price_per_person,OLD.people,OLD.booking_type,OLD.service_name_snapshot,OLD.service_minutes_snapshot,
   OLD.group_price_snapshot,OLD.group_minimum_snapshot,OLD.addons_total_snapshot,OLD.addons_minutes_snapshot,OLD.buffer_minutes_snapshot,
   OLD.area_id,OLD.area_name_snapshot,OLD.travel_fee_informational_snapshot,OLD.policy_accepted_at,OLD.policy_version,
   OLD.travel_reminder_acknowledged_at,OLD.travel_reminder_version,OLD.time_zone_snapshot,OLD.customer_name_snapshot,OLD.package_total_snapshot,OLD.package_minutes_snapshot) THEN RAISE EXCEPTION 'Historical booking identities, prices, location and consent are immutable'; END IF;
  IF NEW.start_at<>OLD.start_at THEN
   IF NOT EXISTS(SELECT 1 FROM public.reschedule_requests WHERE reservation_id=OLD.id AND status='Approved' AND original_start=OLD.start_at AND requested_start=NEW.start_at)
   THEN RAISE EXCEPTION 'An approved reschedule request is required'; END IF;
   NEW.end_at:=NEW.start_at+(OLD.end_at-OLD.start_at);NEW.reserved_until:=NEW.end_at+make_interval(mins=>NEW.buffer_minutes_snapshot);
   PERFORM public.mvc_check_slot(NEW.artist_id,NEW.start_at,NEW.end_at,NEW.buffer_minutes_snapshot,NEW.id);
  ELSIF NEW.end_at<>OLD.end_at OR NEW.reserved_until IS DISTINCT FROM OLD.reserved_until THEN RAISE EXCEPTION 'Reserved duration is immutable'; END IF;
  IF NEW.status<>OLD.status THEN
   IF NOT ((OLD.status IN ('Pending','Pending Payment') AND NEW.status IN ('Confirmed','Rejected')) OR
    (OLD.status='Awaiting Approval' AND NEW.status IN ('Pending Payment','Rejected','Cancelled')) OR
    (OLD.status='Pending Payment' AND NEW.status IN ('Cancelled','Expired')) OR
    (OLD.status IN ('Confirmed','InProgress') AND NEW.status IN ('Cancelled','Completed','No-show','Incomplete'))) THEN RAISE EXCEPTION 'Invalid booking status transition'; END IF;
   IF NEW.status='Expired' AND (OLD.payment_expires_at IS NULL OR OLD.payment_expires_at>clock_timestamp()) THEN RAISE EXCEPTION 'The payment window is still open'; END IF;
   -- Approving an early request starts the 15-minute payment hold; the time must still be free.
   IF OLD.status='Awaiting Approval' AND NEW.status='Pending Payment' THEN
    NEW.payment_expires_at:=clock_timestamp()+interval '15 minutes';
    PERFORM public.mvc_check_slot(NEW.artist_id,NEW.start_at,NEW.end_at,NEW.buffer_minutes_snapshot,NEW.id);
   END IF;
   IF NEW.status='Cancelled' AND OLD.status='Confirmed' AND NEW.staff_cancelled_by IS NULL AND OLD.start_at-clock_timestamp()<interval '24 hours' THEN
    RAISE EXCEPTION 'Cancellations close 24 hours before the appointment. Please contact the artist.'; END IF;
   IF NEW.status='Confirmed' THEN
    IF NEW.app_managed AND NOT EXISTS(SELECT 1 FROM public.service_areas WHERE id=NEW.area_id AND is_active) THEN RAISE EXCEPTION 'Service area is inactive'; END IF;
    PERFORM public.mvc_check_slot(NEW.artist_id,NEW.start_at,NEW.end_at,NEW.buffer_minutes_snapshot,NEW.id);
   END IF;
   IF NEW.status='Cancelled' AND (OLD.start_at<=clock_timestamp() OR NEW.cancellation_confirmed_at IS NULL) THEN RAISE EXCEPTION 'Explicitly confirm cancellation of a future appointment'; END IF;
   IF NEW.status='Completed' AND OLD.end_at>clock_timestamp() THEN RAISE EXCEPTION 'Appointment has not ended'; END IF;
   IF NEW.status IN ('No-show','Incomplete') AND OLD.start_at>clock_timestamp() THEN RAISE EXCEPTION 'Appointment has not started'; END IF;
   IF NEW.status IN ('Rejected','No-show','Incomplete') AND length(btrim(coalesce(NEW.notes,'')))=0 THEN RAISE EXCEPTION 'A reason is required'; END IF;
  END IF;
  -- Business cancellation: set once, only while cancelling, by the acting active SuperAdmin, with a reason.
  IF ROW(NEW.staff_cancelled_by,NEW.staff_cancelled_at,NEW.staff_cancellation_reason) IS DISTINCT FROM ROW(OLD.staff_cancelled_by,OLD.staff_cancelled_at,OLD.staff_cancellation_reason) THEN
   IF OLD.staff_cancelled_by IS NOT NULL OR NEW.status<>'Cancelled' OR OLD.status NOT IN ('Pending Payment','Confirmed')
    OR NEW.staff_cancelled_by IS NULL OR NEW.staff_cancelled_by IS DISTINCT FROM public.mvc_actor() OR NEW.staff_cancelled_at IS NULL
    OR NOT EXISTS(SELECT 1 FROM public.admin_users WHERE app_id=NEW.staff_cancelled_by AND role='SuperAdmin' AND is_active)
    THEN RAISE EXCEPTION 'Only the active SuperAdmin cancelling this appointment can record a business cancellation'; END IF;
   IF length(btrim(coalesce(NEW.staff_cancellation_reason,'')))=0 THEN RAISE EXCEPTION 'A reason is required'; END IF;
  END IF;
  -- Completion: recorded once, by the acting active SuperAdmin, together with the change to Completed.
  IF ROW(NEW.completed_by,NEW.completed_at) IS DISTINCT FROM ROW(OLD.completed_by,OLD.completed_at)
   OR (NEW.status='Completed' AND OLD.status<>'Completed') THEN
   IF OLD.completed_by IS NOT NULL OR NEW.status<>'Completed' OR OLD.status='Completed'
    OR NEW.completed_by IS NULL OR NEW.completed_by IS DISTINCT FROM public.mvc_actor() OR NEW.completed_at IS NULL
    OR NOT EXISTS(SELECT 1 FROM public.admin_users WHERE app_id=NEW.completed_by AND role='SuperAdmin' AND is_active)
    THEN RAISE EXCEPTION 'Only the active SuperAdmin can mark an appointment completed'; END IF;
  END IF;
 END IF;
 RETURN NEW;
END $$;

-- Unpaid bookings expire as before; an early request nobody answered for 7 days, or whose time has
-- come, is closed as Rejected so it never lingers.
CREATE OR REPLACE FUNCTION public.mvc_expire_overdue(excluded bigint DEFAULT NULL) RETURNS int
LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE n int; m int;
BEGIN
 UPDATE public.reservations SET status='Expired', notes='Payment window ended; the slot was released.'
  WHERE status='Pending Payment' AND payment_expires_at<=clock_timestamp() AND id IS DISTINCT FROM excluded;
 GET DIAGNOSTICS n = ROW_COUNT;
 UPDATE public.reservations SET status='Rejected', notes='The artist could not confirm this early time.'
  WHERE status='Awaiting Approval' AND (created_at<=clock_timestamp()-interval '7 days' OR start_at<=clock_timestamp())
    AND id IS DISTINCT FROM excluded;
 GET DIAGNOSTICS m = ROW_COUNT;
 RETURN n + m;
END $$;

-- Payments may be recorded on an early request (its deposit row exists from the start; it is paid after approval).
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
  IF NEW.purpose='down_payment' AND (NOT m.accepts_deposits OR NEW.amount<>coalesce(r.required_down_payment_snapshot,round(r.total_price*.5,2)) OR r.status NOT IN ('Pending','Pending Payment','Awaiting Approval'))
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

-- A mixed package's people must agree with the booking at commit: count, summed price and minutes
-- (service + per-person add-ons), and each service's per-package limit (e.g. one Bridal Makeup).
-- Only a mixed booking may have package people.
CREATE OR REPLACE FUNCTION public.mvc_package_integrity() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE rid bigint; r public.reservations%ROWTYPE; n int; total numeric; minutes int; over record;
BEGIN
 IF TG_TABLE_NAME='reservations' THEN rid:=NEW.id;
 ELSIF TG_TABLE_NAME='reservation_people' THEN rid:=NEW.reservation_id;
 ELSE SELECT reservation_id INTO rid FROM public.reservation_people WHERE id=NEW.person_id; END IF;
 SELECT * INTO STRICT r FROM public.reservations WHERE id=rid;
 IF r.booking_type<>'mixed' THEN
  IF EXISTS(SELECT 1 FROM public.reservation_people WHERE reservation_id=rid) THEN RAISE EXCEPTION 'Only a mixed package booking can list package people'; END IF;
  RETURN NULL;
 END IF;
 SELECT count(*), coalesce(sum(p.price_snapshot+coalesce(a.price,0)),0), coalesce(sum(p.minutes_snapshot+coalesce(a.minutes,0)),0)
   INTO n, total, minutes
   FROM public.reservation_people p
   LEFT JOIN (SELECT person_id, sum(price_snapshot) AS price, sum(minutes_snapshot) AS minutes FROM public.reservation_people_addons GROUP BY person_id) a ON a.person_id=p.id
  WHERE p.reservation_id=rid;
 IF n<>r.people OR total<>r.package_total_snapshot OR minutes<>r.package_minutes_snapshot THEN
  RAISE EXCEPTION 'The package people disagree with the booking'; END IF;
 SELECT s.name, s.max_per_package INTO over FROM public.reservation_people p JOIN public.services s ON s.id=p.service_id
  WHERE p.reservation_id=rid GROUP BY s.name, s.max_per_package HAVING s.max_per_package IS NOT NULL AND count(*)>s.max_per_package LIMIT 1;
 IF FOUND THEN RAISE EXCEPTION 'Only % % allowed per package booking', over.max_per_package, over.name; END IF;
 RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS mvc_package_integrity ON public.reservations;
CREATE CONSTRAINT TRIGGER mvc_package_integrity AFTER INSERT OR UPDATE ON public.reservations DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.mvc_package_integrity();
DROP TRIGGER IF EXISTS mvc_package_integrity ON public.reservation_people;
CREATE CONSTRAINT TRIGGER mvc_package_integrity AFTER INSERT OR UPDATE ON public.reservation_people DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.mvc_package_integrity();
DROP TRIGGER IF EXISTS mvc_package_integrity ON public.reservation_people_addons;
CREATE CONSTRAINT TRIGGER mvc_package_integrity AFTER INSERT OR UPDATE ON public.reservation_people_addons DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.mvc_package_integrity();
REVOKE ALL ON FUNCTION public.mvc_package_integrity() FROM PUBLIC, anon, authenticated;
