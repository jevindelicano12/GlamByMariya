START TRANSACTION;
-- Transportation Fee Policy additive delta for databases where 20261017140000_DownPaymentPolicy is already applied.
-- No BEGIN/COMMIT: EF owns the transaction.
-- Reuses reservations.travel_reminder_acknowledged_at; no acknowledgment column is created.
SET LOCAL lock_timeout='10s';
SET LOCAL statement_timeout='120s';

-- New reservations retain scheduling buffers but do not snapshot the legacy flat fee.
CREATE OR REPLACE FUNCTION public.mvc_reservation_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE s public.business_settings%ROWTYPE; svc public.services%ROWTYPE; coverage public.service_areas%ROWTYPE;
BEGIN
 IF TG_OP='INSERT' AND NEW.app_managed THEN
  IF NEW.status<>'Pending Payment' THEN RAISE EXCEPTION 'New requests must await payment verification'; END IF;
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
    (OLD.status IN ('Confirmed','InProgress') AND NEW.status IN ('Cancelled','Completed','No-show','Incomplete'))) THEN RAISE EXCEPTION 'Invalid booking status transition'; END IF;
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
DROP TRIGGER IF EXISTS mvc_reservation_guard ON public.reservations;
CREATE TRIGGER mvc_reservation_guard BEFORE INSERT OR UPDATE ON public.reservations FOR EACH ROW EXECUTE FUNCTION public.mvc_reservation_guard();

-- Append the existing acknowledgment timestamp to the deployed view without reordering columns.
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
  r.travel_reminder_acknowledged_at AS transportation_policy_acknowledged_at
 FROM public.reservations r JOIN public.customers c ON c.id=r.customer_id JOIN public.makeup_artists a ON a.id=r.artist_id JOIN public.services s ON s.id=r.service_id
 CROSS JOIN LATERAL(SELECT public.mvc_paid(r.id) AS paid) v LEFT JOIN public.reservation_closures z ON z.reservation_id=r.id
 LEFT JOIN LATERAL(SELECT sum(amount) AS refunded FROM public.refunds WHERE closure_id=z.id) f ON true
 LEFT JOIN public.reschedule_requests q ON q.reservation_id=r.id AND q.status='Pending';


INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261018100000_TransportationFeePolicy', '10.0.13');

COMMIT;

