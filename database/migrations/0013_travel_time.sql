-- Travel-time-aware scheduling (built-in estimate, no external map service).
-- The artist arrives at the customer's location by the booked start time. A booking
-- blocks start..reserved_until (session + preparation buffer). Between two bookings the
-- artist also needs travel time from one location to the other:
--   new.start >= previous.reserved_until + travel(previous, new)
--   new.reserved_until + travel(new, next) <= next.start
-- Travel minutes = ceil(distance_km * road_factor / speed_kmh * 60 + margin), rounded up
-- to 5 minutes; 0 for the same spot (under 100 m) or when neither booking has a pin
-- (bookings made before pins existed); a fixed fallback when only one has a pin. Other customers' coordinates never leave the database: the
-- app receives only busy ranges and the travel minutes to each of them.

ALTER TABLE public.reservations
 ADD COLUMN IF NOT EXISTS service_lat numeric(9,6),
 ADD COLUMN IF NOT EXISTS service_lng numeric(9,6);
ALTER TABLE public.reservations DROP CONSTRAINT IF EXISTS reservations_service_point_check;
ALTER TABLE public.reservations ADD CONSTRAINT reservations_service_point_check CHECK(
 (service_lat IS NULL AND service_lng IS NULL) OR
 (service_lat BETWEEN -90 AND 90 AND service_lng BETWEEN -180 AND 180));

ALTER TABLE public.business_settings
 ADD COLUMN IF NOT EXISTS travel_speed_kmh numeric(5,1) NOT NULL DEFAULT 20,
 ADD COLUMN IF NOT EXISTS travel_road_factor numeric(3,2) NOT NULL DEFAULT 1.40,
 ADD COLUMN IF NOT EXISTS travel_margin_minutes integer NOT NULL DEFAULT 15,
 ADD COLUMN IF NOT EXISTS travel_unknown_minutes integer NOT NULL DEFAULT 60,
 ADD COLUMN IF NOT EXISTS service_center_lat numeric(9,6) NOT NULL DEFAULT 14.599500,
 ADD COLUMN IF NOT EXISTS service_center_lng numeric(9,6) NOT NULL DEFAULT 120.984200,
 ADD COLUMN IF NOT EXISTS service_radius_km numeric(5,1) NOT NULL DEFAULT 40;
ALTER TABLE public.business_settings DROP CONSTRAINT IF EXISTS business_settings_travel_check;
ALTER TABLE public.business_settings ADD CONSTRAINT business_settings_travel_check CHECK(
 travel_speed_kmh BETWEEN 5 AND 120 AND travel_road_factor BETWEEN 1 AND 3
 AND travel_margin_minutes BETWEEN 0 AND 120 AND travel_unknown_minutes BETWEEN 0 AND 240
 AND service_center_lat BETWEEN -90 AND 90 AND service_center_lng BETWEEN -180 AND 180
 AND service_radius_km BETWEEN 1 AND 500);

-- Great-circle distance in kilometres.
CREATE OR REPLACE FUNCTION public.mvc_distance_km(lat1 numeric, lng1 numeric, lat2 numeric, lng2 numeric)
RETURNS double precision LANGUAGE sql IMMUTABLE SET search_path=public,pg_catalog AS $$
 SELECT 2 * 6371.0088 * asin(least(1.0, sqrt(
   power(sin(radians((lat2 - lat1)::double precision) / 2), 2) +
   cos(radians(lat1::double precision)) * cos(radians(lat2::double precision)) *
   power(sin(radians((lng2 - lng1)::double precision) / 2), 2))))
$$;

CREATE OR REPLACE FUNCTION public.mvc_travel_minutes(lat1 numeric, lng1 numeric, lat2 numeric, lng2 numeric)
RETURNS integer LANGUAGE plpgsql STABLE SET search_path=public,pg_catalog AS $$
DECLARE s public.business_settings%ROWTYPE; km double precision; raw double precision;
BEGIN
 SELECT * INTO STRICT s FROM public.business_settings WHERE id=1;
 -- Two bookings made before pins existed keep the old rule (no travel added);
 -- one known and one unknown location use the fallback.
 IF (lat1 IS NULL OR lng1 IS NULL) AND (lat2 IS NULL OR lng2 IS NULL) THEN RETURN 0; END IF;
 IF lat1 IS NULL OR lng1 IS NULL OR lat2 IS NULL OR lng2 IS NULL THEN RETURN s.travel_unknown_minutes; END IF;
 km := public.mvc_distance_km(lat1, lng1, lat2, lng2);
 IF km < 0.1 THEN RETURN 0; END IF;
 raw := km * s.travel_road_factor::double precision / s.travel_speed_kmh::double precision * 60 + s.travel_margin_minutes;
 RETURN (ceil(ceil(raw) / 5.0) * 5)::integer;
END $$;

-- Busy ranges plus the travel minutes from (lat,lng) to each booking. Blocks need no travel.
-- Bookings up to 6 hours outside the window are included so travel at the edges counts.
CREATE OR REPLACE FUNCTION public.mvc_occupied_travel(aid bigint, from_at timestamptz, to_at timestamptz, excluded uuid, lat numeric, lng numeric)
RETURNS TABLE(occupied_from timestamptz, occupied_until timestamptz, travel_minutes integer)
LANGUAGE sql STABLE SET search_path=public,pg_catalog AS $$
 SELECT r.start_at, coalesce(r.reserved_until, r.end_at), public.mvc_travel_minutes(lat, lng, r.service_lat, r.service_lng)
   FROM public.reservations r
  WHERE r.artist_id = aid AND r.app_id IS DISTINCT FROM excluded
    AND (r.status IN ('Pending','Confirmed','InProgress')
         OR (r.status = 'Pending Payment' AND (r.payment_expires_at IS NULL OR r.payment_expires_at > clock_timestamp())))
    AND r.start_at < to_at + interval '6 hours' AND coalesce(r.reserved_until, r.end_at) > from_at - interval '6 hours'
 UNION ALL
 SELECT b.start_at, b.end_at, 0
   FROM public.blocked_slots b
  WHERE b.artist_id = aid AND b.start_at < to_at AND b.end_at > from_at
$$;

-- Final authority under concurrency. Runs after mvc_reservation_guard (BEFORE triggers
-- fire in name order), which has set reserved_until; the artist row lock serialises
-- competing bookings for the same artist.
CREATE OR REPLACE FUNCTION public.mvc_travel_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
BEGIN
 IF TG_OP='UPDATE' AND (NEW.service_lat IS DISTINCT FROM OLD.service_lat OR NEW.service_lng IS DISTINCT FROM OLD.service_lng) THEN
  RAISE EXCEPTION 'The service location of a booking cannot be changed'; END IF;
 IF NOT (NEW.status IN ('Pending','Confirmed','InProgress')
         OR (NEW.status='Pending Payment' AND (NEW.payment_expires_at IS NULL OR NEW.payment_expires_at > clock_timestamp()))) THEN RETURN NEW; END IF;
 IF TG_OP='UPDATE' AND NEW.start_at = OLD.start_at THEN RETURN NEW; END IF;
 PERFORM 1 FROM public.makeup_artists WHERE id=NEW.artist_id FOR UPDATE;
 IF EXISTS(SELECT 1 FROM public.reservations r
  WHERE r.artist_id=NEW.artist_id AND r.id IS DISTINCT FROM NEW.id
    AND (r.status IN ('Pending','Confirmed','InProgress')
         OR (r.status='Pending Payment' AND (r.payment_expires_at IS NULL OR r.payment_expires_at > clock_timestamp())))
    AND NEW.start_at < coalesce(r.reserved_until, r.end_at) + make_interval(mins=>public.mvc_travel_minutes(NEW.service_lat, NEW.service_lng, r.service_lat, r.service_lng))
    AND coalesce(NEW.reserved_until, NEW.end_at) + make_interval(mins=>public.mvc_travel_minutes(NEW.service_lat, NEW.service_lng, r.service_lat, r.service_lng)) > r.start_at)
 THEN RAISE EXCEPTION 'This time does not leave enough travel time between appointments. Please choose another time.'; END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS mvc_travel_guard ON public.reservations;
CREATE TRIGGER mvc_travel_guard BEFORE INSERT OR UPDATE ON public.reservations FOR EACH ROW EXECUTE FUNCTION public.mvc_travel_guard();

-- Owners see the pinned spot on the booking (directions link). Columns appended at the end.
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
 r.service_lat,r.service_lng
 FROM public.reservations r JOIN public.customers c ON c.id=r.customer_id JOIN public.makeup_artists a ON a.id=r.artist_id JOIN public.services s ON s.id=r.service_id
 CROSS JOIN LATERAL(SELECT public.mvc_paid(r.id) AS paid) v LEFT JOIN public.reservation_closures z ON z.reservation_id=r.id
 LEFT JOIN LATERAL(SELECT sum(amount) AS refunded FROM public.refunds WHERE closure_id=z.id) f ON true
 LEFT JOIN public.reschedule_requests q ON q.reservation_id=r.id AND q.status='Pending';

REVOKE ALL ON FUNCTION public.mvc_distance_km(numeric,numeric,numeric,numeric) FROM PUBLIC;
REVOKE ALL ON FUNCTION public.mvc_travel_minutes(numeric,numeric,numeric,numeric) FROM PUBLIC;
REVOKE ALL ON FUNCTION public.mvc_occupied_travel(bigint,timestamptz,timestamptz,uuid,numeric,numeric) FROM PUBLIC;
REVOKE ALL ON FUNCTION public.mvc_travel_guard() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.mvc_distance_km(numeric,numeric,numeric,numeric) TO makeup_backend;
GRANT EXECUTE ON FUNCTION public.mvc_travel_minutes(numeric,numeric,numeric,numeric) TO makeup_backend;
GRANT EXECUTE ON FUNCTION public.mvc_occupied_travel(bigint,timestamptz,timestamptz,uuid,numeric,numeric) TO makeup_backend;
GRANT SELECT ON public.mvc_booking_summary TO makeup_backend;
