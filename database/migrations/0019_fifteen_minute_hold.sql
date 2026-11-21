-- 0019: an unpaid booking holds its slot for 15 minutes (was 30). Same guard as
-- 0014_booking_location_required.sql; only the payment window changes. The Xendit
-- checkout expires one minute earlier (14 minutes), so a payment always lands while
-- the slot is held. Bookings already waiting keep the deadline they were given.
CREATE OR REPLACE FUNCTION public.mvc_reservation_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE s public.business_settings%ROWTYPE; svc public.services%ROWTYPE; coverage public.service_areas%ROWTYPE;
BEGIN
 IF TG_OP='INSERT' AND NEW.app_managed THEN
  IF NEW.status<>'Pending Payment' THEN RAISE EXCEPTION 'New requests must await payment verification'; END IF;
  -- Every new app booking carries the customer's exact pin (the Metro Manila box is the table CHECK from 0013).
  IF NEW.location_lat IS NULL OR NEW.location_lng IS NULL THEN RAISE EXCEPTION 'Pin your exact location inside Metro Manila.'; END IF;
  NEW.payment_expires_at:=clock_timestamp()+interval '15 minutes';
  SELECT * INTO STRICT s FROM public.business_settings WHERE id=1;
  SELECT * INTO STRICT svc FROM public.services WHERE id=NEW.service_id AND is_active AND NOT is_archived;
  SELECT * INTO STRICT coverage FROM public.service_areas WHERE id=NEW.area_id AND is_active;
  IF NOT EXISTS(SELECT 1 FROM public.customers WHERE id=NEW.customer_id AND is_active) THEN RAISE EXCEPTION 'An active customer is required'; END IF;
  IF NEW.people NOT BETWEEN 1 AND 20 OR NEW.booking_type NOT IN ('individual','package') OR (NEW.booking_type='individual' AND NEW.people<>1)
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
  NEW.total_price:=NEW.applied_price_per_person*NEW.people+NEW.addons_total_snapshot;
  NEW.end_at:=NEW.start_at+make_interval(mins=>NEW.service_minutes_snapshot*NEW.people+NEW.addons_minutes_snapshot);
  NEW.reserved_until:=NEW.end_at+make_interval(mins=>NEW.buffer_minutes_snapshot);
  PERFORM public.mvc_check_slot(NEW.artist_id,NEW.start_at,NEW.end_at,NEW.buffer_minutes_snapshot,NULL);
 END IF;
 IF TG_OP='UPDATE' THEN
  IF ROW(NEW.app_id,NEW.app_managed,NEW.customer_id,NEW.artist_id,NEW.service_id,NEW.reference,NEW.location,NEW.total_price,
   NEW.regular_price_snapshot,NEW.applied_price_per_person,NEW.people,NEW.booking_type,NEW.service_name_snapshot,NEW.service_minutes_snapshot,
   NEW.group_price_snapshot,NEW.group_minimum_snapshot,NEW.addons_total_snapshot,NEW.addons_minutes_snapshot,NEW.buffer_minutes_snapshot,
   NEW.area_id,NEW.area_name_snapshot,NEW.travel_fee_informational_snapshot,NEW.policy_accepted_at,NEW.policy_version,
   NEW.travel_reminder_acknowledged_at,NEW.travel_reminder_version,NEW.time_zone_snapshot,NEW.customer_name_snapshot)
  IS DISTINCT FROM ROW(OLD.app_id,OLD.app_managed,OLD.customer_id,OLD.artist_id,OLD.service_id,OLD.reference,OLD.location,OLD.total_price,
   OLD.regular_price_snapshot,OLD.applied_price_per_person,OLD.people,OLD.booking_type,OLD.service_name_snapshot,OLD.service_minutes_snapshot,
   OLD.group_price_snapshot,OLD.group_minimum_snapshot,OLD.addons_total_snapshot,OLD.addons_minutes_snapshot,OLD.buffer_minutes_snapshot,
   OLD.area_id,OLD.area_name_snapshot,OLD.travel_fee_informational_snapshot,OLD.policy_accepted_at,OLD.policy_version,
   OLD.travel_reminder_acknowledged_at,OLD.travel_reminder_version,OLD.time_zone_snapshot,OLD.customer_name_snapshot) THEN RAISE EXCEPTION 'Historical booking identities, prices, location and consent are immutable'; END IF;
  IF NEW.start_at<>OLD.start_at THEN
   IF NOT EXISTS(SELECT 1 FROM public.reschedule_requests WHERE reservation_id=OLD.id AND status='Approved' AND original_start=OLD.start_at AND requested_start=NEW.start_at)
   THEN RAISE EXCEPTION 'An approved reschedule request is required'; END IF;
   NEW.end_at:=NEW.start_at+(OLD.end_at-OLD.start_at);NEW.reserved_until:=NEW.end_at+make_interval(mins=>NEW.buffer_minutes_snapshot);
   PERFORM public.mvc_check_slot(NEW.artist_id,NEW.start_at,NEW.end_at,NEW.buffer_minutes_snapshot,NEW.id);
  ELSIF NEW.end_at<>OLD.end_at OR NEW.reserved_until IS DISTINCT FROM OLD.reserved_until THEN RAISE EXCEPTION 'Reserved duration is immutable'; END IF;
  IF NEW.status<>OLD.status THEN
   IF NOT ((OLD.status IN ('Pending','Pending Payment') AND NEW.status IN ('Confirmed','Rejected')) OR
    (OLD.status='Pending Payment' AND NEW.status IN ('Cancelled','Expired')) OR
    (OLD.status IN ('Confirmed','InProgress') AND NEW.status IN ('Cancelled','Completed','No-show','Incomplete'))) THEN RAISE EXCEPTION 'Invalid booking status transition'; END IF;
   IF NEW.status='Expired' AND (OLD.payment_expires_at IS NULL OR OLD.payment_expires_at>clock_timestamp()) THEN RAISE EXCEPTION 'The payment window is still open'; END IF;
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
