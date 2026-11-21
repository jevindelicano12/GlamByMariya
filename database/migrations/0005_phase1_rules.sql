-- Phase 1 booking rules in the database, matching the C# rules:
-- PaymentWindow (30 minutes), CancellationPolicy (PHP 200, 24-hour cut-off, unpaid
-- cancels free) and BookingService.HoldsReservation (Pending Payment holds the slot).

-- 30-minute payment window and the Expired status.
ALTER TABLE public.reservations ADD COLUMN IF NOT EXISTS payment_expires_at timestamptz;
ALTER TABLE public.reservations DROP CONSTRAINT IF EXISTS reservations_status_check;
ALTER TABLE public.reservations ADD CONSTRAINT reservations_status_check CHECK(status IN
 ('Pending','Pending Payment','Confirmed','InProgress','Completed','Cancelled','Rejected','NoShow','No-show','Incomplete','Expired'));

-- Pending Payment holds the artist's time like Confirmed does.
ALTER TABLE public.reservations DROP CONSTRAINT IF EXISTS mvc_travel_overlap;
ALTER TABLE public.reservations ADD CONSTRAINT mvc_travel_overlap EXCLUDE USING gist
 (artist_id WITH =, tstzrange(start_at, coalesce(reserved_until, end_at), '[)') WITH &&)
 WHERE (status IN ('Pending','Pending Payment','Confirmed','InProgress'));

-- A cancelled unpaid booking closes with no fee; a cancelled Confirmed booking with PHP 200.
DO $$ DECLARE c text; BEGIN
 FOR c IN SELECT conname FROM pg_constraint WHERE conrelid='public.reservation_closures'::regclass AND contype='c'
   AND pg_get_constraintdef(oid) LIKE '%cancellation_fee%' LOOP
  EXECUTE format('ALTER TABLE public.reservation_closures DROP CONSTRAINT %I', c);
 END LOOP;
END $$;
ALTER TABLE public.reservation_closures ADD CONSTRAINT reservation_closures_fee_check CHECK(
 (kind='cancellation' AND cancellation_fee IN (0,200) AND confirmed_at IS NOT NULL)
 OR (kind='rejection' AND cancellation_fee=0 AND length(btrim(reason))>0));

-- Marks unpaid bookings whose window ended as Expired. Called every minute by the job
-- (0007) and before every slot check, so a stale unpaid booking never blocks a new one.
-- `excluded` skips the row whose own trigger is running.
CREATE OR REPLACE FUNCTION public.mvc_expire_overdue(excluded bigint DEFAULT NULL) RETURNS int
LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE n int;
BEGIN
 UPDATE public.reservations SET status='Expired', notes='Payment window ended; the slot was released.'
  WHERE status='Pending Payment' AND payment_expires_at<=clock_timestamp() AND id IS DISTINCT FROM excluded;
 GET DIAGNOSTICS n = ROW_COUNT;
 RETURN n;
END $$;

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
 OR local_start<date_trunc('day',local_start)+make_interval(hours=>s.open_hour)
 OR local_end>date_trunc('day',local_start)+make_interval(hours=>s.close_hour) THEN RAISE EXCEPTION 'Choose a future session that fits the studio hours including travel time'; END IF;
 IF EXISTS(SELECT 1 FROM public.artist_availability WHERE artist_id=aid) AND NOT EXISTS
 (SELECT 1 FROM public.artist_availability WHERE artist_id=aid AND weekday=extract(dow FROM local_start)::int
 AND local_start>=local_start::date+start_time AND local_end<=local_start::date+end_time) THEN RAISE EXCEPTION 'The artist is outside weekly availability'; END IF;
 IF EXISTS(SELECT 1 FROM public.blocked_slots WHERE artist_id=aid AND starts<end_at AND finish>start_at)
 OR EXISTS(SELECT 1 FROM public.reservations WHERE artist_id=aid AND id IS DISTINCT FROM excluded AND status IN ('Pending','Pending Payment','Confirmed','InProgress')
 AND starts<coalesce(reserved_until,end_at) AND finish>start_at) THEN RAISE EXCEPTION 'This slot overlaps a reserved appointment. Please choose another time.'; END IF;
END $$;

CREATE OR REPLACE FUNCTION public.mvc_block_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
BEGIN
 PERFORM 1 FROM public.makeup_artists WHERE id=NEW.artist_id FOR UPDATE;
 IF EXISTS(SELECT 1 FROM public.reservations WHERE artist_id=NEW.artist_id AND status IN ('Pending','Pending Payment','Confirmed','InProgress')
 AND NEW.start_at<coalesce(reserved_until,end_at) AND NEW.end_at>start_at) THEN RAISE EXCEPTION 'Availability block overlaps a reserved appointment'; END IF;
 RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION public.mvc_reservation_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE s public.business_settings%ROWTYPE; svc public.services%ROWTYPE; coverage public.service_areas%ROWTYPE;
BEGIN
 IF TG_OP='INSERT' AND NEW.app_managed THEN
  IF NEW.status<>'Pending Payment' THEN RAISE EXCEPTION 'New requests must await payment verification'; END IF;
  NEW.payment_expires_at:=clock_timestamp()+interval '30 minutes';
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
   IF NEW.status='Cancelled' AND OLD.status='Confirmed' AND OLD.start_at-clock_timestamp()<interval '24 hours' THEN
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
   VALUES(NEW.id,CASE WHEN NEW.status='Cancelled' THEN 'cancellation' ELSE 'rejection' END,public.mvc_paid(NEW.id),CASE WHEN NEW.status='Cancelled' AND OLD.status IN ('Confirmed','InProgress') THEN 200 ELSE 0 END,
    coalesce(NEW.notes,''),public.mvc_actor(),NEW.cancellation_confirmed_at);
   UPDATE public.reschedule_requests SET status='Withdrawn' WHERE reservation_id=NEW.id AND status='Pending';
   UPDATE public.notifications SET delivery_status='Cancelled' WHERE reservation_id=NEW.id AND kind='reminder' AND delivery_status='Pending';
  END IF;
 END IF;RETURN NEW;
END $$;
