-- 0022: paid bookings wait for the artist (Phase 9).
-- A Xendit payment keeps the booking Confirmed (it holds its time) but the artist still accepts it:
-- artist_accepted_at, set once by staff; no action for 24 hours after payment counts as accepted.
-- A customer cancelling a paid booking pays the ₱200 fee only from 1 hour after acceptance, and
-- cannot cancel online within 3 hours of the start (was 24 hours). A paid booking closes every
-- overlapping early request still waiting for the artist. Early requests show "Not due yet".

ALTER TABLE public.reservations ADD COLUMN IF NOT EXISTS artist_accepted_at timestamptz;
-- Bookings confirmed before this change were accepted already.
UPDATE public.reservations SET artist_accepted_at=coalesce(reviewed_at,updated_at,now())
 WHERE status IN ('Confirmed','InProgress') AND artist_accepted_at IS NULL;

-- When the artist accepted (or, without action, 24 hours after the down payment was paid).
CREATE OR REPLACE FUNCTION public.mvc_artist_accepted(p_reservation bigint) RETURNS timestamptz
LANGUAGE sql STABLE SET search_path=public,pg_catalog AS $$
 SELECT coalesce(r.artist_accepted_at,
  (SELECT min(p.verified_at) FROM public.payments p WHERE p.reservation_id=r.id AND p.purpose='down_payment' AND p.status='Paid')+interval '24 hours')
 FROM public.reservations r WHERE r.id=p_reservation
$$;
REVOKE ALL ON FUNCTION public.mvc_artist_accepted(bigint) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.mvc_artist_accepted(bigint) TO makeup_backend;

CREATE OR REPLACE FUNCTION public.mvc_reservation_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE s public.business_settings%ROWTYPE; svc public.services%ROWTYPE; coverage public.service_areas%ROWTYPE;
BEGIN
 IF TG_OP='INSERT' AND NEW.artist_accepted_at IS NOT NULL THEN RAISE EXCEPTION 'A new booking has not been accepted by the artist yet'; END IF;
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
  IF NEW.is_early_request THEN PERFORM public.mvc_check_request_window(NEW.artist_id,NEW.start_at,NEW.end_at,NEW.buffer_minutes_snapshot);
  ELSE PERFORM public.mvc_check_slot(NEW.artist_id,NEW.start_at,NEW.end_at,NEW.buffer_minutes_snapshot,NULL); END IF;
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
   IF NEW.status='Cancelled' AND OLD.status='Confirmed' AND NEW.staff_cancelled_by IS NULL AND OLD.start_at-clock_timestamp()<interval '3 hours' THEN
    RAISE EXCEPTION 'Cancellations close 3 hours before the appointment. Please contact the artist.'; END IF;
   IF NEW.status='Confirmed' THEN
    IF NEW.app_managed AND NOT EXISTS(SELECT 1 FROM public.service_areas WHERE id=NEW.area_id AND is_active) THEN RAISE EXCEPTION 'Service area is inactive'; END IF;
    PERFORM public.mvc_check_slot(NEW.artist_id,NEW.start_at,NEW.end_at,NEW.buffer_minutes_snapshot,NEW.id);
   END IF;
   IF NEW.status='Cancelled' AND (OLD.start_at<=clock_timestamp() OR NEW.cancellation_confirmed_at IS NULL) THEN RAISE EXCEPTION 'Explicitly confirm cancellation of a future appointment'; END IF;
   IF NEW.status='Completed' AND OLD.end_at>clock_timestamp() THEN RAISE EXCEPTION 'Appointment has not ended'; END IF;
   IF NEW.status IN ('No-show','Incomplete') AND OLD.start_at>clock_timestamp() THEN RAISE EXCEPTION 'Appointment has not started'; END IF;
   IF NEW.status IN ('Rejected','No-show','Incomplete') AND length(btrim(coalesce(NEW.notes,'')))=0 THEN RAISE EXCEPTION 'A reason is required'; END IF;
  END IF;
  -- The artist accepts a paid booking once, while it is Confirmed, as an active staff member (0022).
  IF NEW.artist_accepted_at IS DISTINCT FROM OLD.artist_accepted_at THEN
   IF OLD.artist_accepted_at IS NOT NULL OR NEW.artist_accepted_at IS NULL OR OLD.status<>'Confirmed' OR NEW.status<>'Confirmed'
    OR NOT EXISTS(SELECT 1 FROM public.admin_users WHERE app_id=public.mvc_actor() AND is_active)
    THEN RAISE EXCEPTION 'Only active staff can accept a confirmed booking, once'; END IF;
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

CREATE OR REPLACE FUNCTION public.mvc_reservation_event() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
BEGIN
 IF TG_OP='INSERT' OR NEW.status IS DISTINCT FROM OLD.status THEN
  INSERT INTO public.reservation_history(reservation_id,changed_by,old_status,new_status,actor_id,note)
  VALUES(NEW.id,coalesce(public.mvc_actor()::text,'system'),CASE WHEN TG_OP='UPDATE' THEN OLD.status END,NEW.status,public.mvc_actor(),NEW.notes);
  INSERT INTO public.notifications(user_id,reservation_id,message)
  SELECT a.id,NEW.id,NEW.reference||': '||NEW.status||'. '||coalesce(NEW.notes,'') FROM public.account_identities a
  WHERE a.customer_id=NEW.customer_id OR EXISTS(SELECT 1 FROM public.admin_users u WHERE u.id=a.admin_id AND u.is_active);
  IF TG_OP='UPDATE' AND NEW.status IN ('Cancelled','Rejected') THEN
   INSERT INTO public.reservation_closures(reservation_id,kind,verified_paid_snapshot,cancellation_fee,reason,actor_id,confirmed_at)
   VALUES(NEW.id,CASE WHEN NEW.status='Cancelled' THEN 'cancellation' ELSE 'rejection' END,public.mvc_paid(NEW.id),CASE WHEN NEW.status='Cancelled' AND OLD.status IN ('Confirmed','InProgress') AND NEW.staff_cancelled_by IS NULL
    AND clock_timestamp()>=public.mvc_artist_accepted(OLD.id)+interval '1 hour' THEN 200 ELSE 0 END,
    coalesce(NEW.notes,''),public.mvc_actor(),NEW.cancellation_confirmed_at);
   UPDATE public.reschedule_requests SET status='Withdrawn' WHERE reservation_id=NEW.id AND status='Pending';
   UPDATE public.notifications SET delivery_status='Cancelled' WHERE reservation_id=NEW.id AND kind='reminder' AND delivery_status='Pending';
  END IF;
 END IF;RETURN NEW;
END $$;

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
 UPDATE public.reservations SET status='Confirmed', notes='Down payment received through Xendit. The artist will confirm your booking within 24 hours.' WHERE id=r.id;
 -- The paid time is taken: close every early request waiting for the artist that overlaps it (0022).
 UPDATE public.reservations x SET status='Rejected', notes='This time is already booked. Please choose another date or time.'
  WHERE x.status='Awaiting Approval' AND x.artist_id=r.artist_id AND x.id<>r.id
    AND x.start_at < r.end_at+make_interval(mins=>coalesce(r.buffer_minutes_snapshot,0))
    AND r.start_at < x.end_at+make_interval(mins=>coalesce(x.buffer_minutes_snapshot,0));
 UPDATE public.payment_webhook_events SET outcome='confirmed' WHERE payment_webhook_events.event_key=p_event;
 RETURN 'confirmed';
END $$;

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
 CASE WHEN r.status='Awaiting Approval' THEN 'Not due yet' WHEN z.refund_due>coalesce(f.refunded,0) THEN 'Refund Pending' WHEN z.refund_due>0 THEN 'Refunded'
  WHEN v.paid>=r.total_price THEN 'Paid in Full' WHEN v.paid>=coalesce(r.required_down_payment_snapshot,round(r.total_price*.5,2)) THEN 'Down Payment Verified' ELSE 'Pending Verification' END::text AS payment_status,
  coalesce(r.down_payment_percent_snapshot,50) AS down_payment_percent_snapshot,
  coalesce(r.required_down_payment_snapshot,round(r.total_price*.5,2)) AS required_down_payment_snapshot,
  r.travel_reminder_acknowledged_at AS transportation_policy_acknowledged_at,
  r.location_lat,r.location_lng,r.is_early_request,r.after_hours,
  r.artist_accepted_at,(SELECT min(p.verified_at) FROM public.payments p WHERE p.reservation_id=r.id AND p.purpose='down_payment' AND p.status='Paid') AS paid_at
 FROM public.reservations r JOIN public.customers c ON c.id=r.customer_id JOIN public.makeup_artists a ON a.id=r.artist_id JOIN public.services s ON s.id=r.service_id
 CROSS JOIN LATERAL(SELECT public.mvc_paid(r.id) AS paid) v LEFT JOIN public.reservation_closures z ON z.reservation_id=r.id
 LEFT JOIN LATERAL(SELECT sum(amount) AS refunded FROM public.refunds WHERE closure_id=z.id) f ON true
 LEFT JOIN public.reschedule_requests q ON q.reservation_id=r.id AND q.status='Pending';
