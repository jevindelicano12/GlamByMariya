-- Travel-aware availability (Phase 5): travel settings, the artist's home and current pins,
-- the SuperAdmin travel override on a booking, and the neighbour list the travel rule reads.

-- Travel speed (km/h) and the minimum travel between two places (minutes).
ALTER TABLE public.business_settings
  ADD COLUMN IF NOT EXISTS travel_speed_kmh numeric(4,1) NOT NULL DEFAULT 15,
  ADD COLUMN IF NOT EXISTS travel_min_minutes int NOT NULL DEFAULT 30;
ALTER TABLE public.business_settings DROP CONSTRAINT IF EXISTS business_settings_travel_speed_check;
ALTER TABLE public.business_settings ADD CONSTRAINT business_settings_travel_speed_check CHECK (travel_speed_kmh BETWEEN 5 AND 60);
ALTER TABLE public.business_settings DROP CONSTRAINT IF EXISTS business_settings_travel_min_check;
ALTER TABLE public.business_settings ADD CONSTRAINT business_settings_travel_min_check CHECK (travel_min_minutes BETWEEN 0 AND 180);

-- Where the artist starts the day (home) and where she is now (current; none = at home).
-- Same Metro Manila box and both-or-none rule as the booking pin (0013).
ALTER TABLE public.makeup_artists
  ADD COLUMN IF NOT EXISTS home_lat numeric(9,6),
  ADD COLUMN IF NOT EXISTS home_lng numeric(9,6),
  ADD COLUMN IF NOT EXISTS current_lat numeric(9,6),
  ADD COLUMN IF NOT EXISTS current_lng numeric(9,6),
  ADD COLUMN IF NOT EXISTS current_updated_at timestamptz;
ALTER TABLE public.makeup_artists DROP CONSTRAINT IF EXISTS makeup_artists_home_pin_check;
ALTER TABLE public.makeup_artists ADD CONSTRAINT makeup_artists_home_pin_check CHECK (
  (home_lat IS NULL) = (home_lng IS NULL)
  AND (home_lat IS NULL OR (home_lat BETWEEN 14.20 AND 14.95 AND home_lng BETWEEN 120.85 AND 121.25)));
ALTER TABLE public.makeup_artists DROP CONSTRAINT IF EXISTS makeup_artists_current_pin_check;
ALTER TABLE public.makeup_artists ADD CONSTRAINT makeup_artists_current_pin_check CHECK (
  (current_lat IS NULL) = (current_lng IS NULL)
  AND (current_lat IS NULL OR (current_lat BETWEEN 14.20 AND 14.95 AND current_lng BETWEEN 120.85 AND 121.25)));

-- The SuperAdmin's "I'm sure the artist can make it" override, recorded on the booking.
ALTER TABLE public.reservations
  ADD COLUMN IF NOT EXISTS travel_override_by uuid,
  ADD COLUMN IF NOT EXISTS travel_override_at timestamptz;
ALTER TABLE public.reservations DROP CONSTRAINT IF EXISTS reservations_travel_override_check;
ALTER TABLE public.reservations ADD CONSTRAINT reservations_travel_override_check CHECK ((travel_override_by IS NULL) = (travel_override_at IS NULL));

-- The bookings that hold a slot in a time range (same status and expiry rule as
-- mvc_occupied_intervals, 0006), with their pins: the travel rule's neighbours.
-- Only times, buffers and pins leave the database: no customer, address or reference.
CREATE OR REPLACE FUNCTION public.mvc_travel_stops(p_from timestamptz, p_until timestamptz, p_excluded uuid)
RETURNS TABLE(start_at timestamptz, end_at timestamptz, buffer_minutes int, location_lat numeric, location_lng numeric)
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=public,pg_catalog AS $$
 SELECT r.start_at, r.end_at, r.buffer_minutes_snapshot, r.location_lat, r.location_lng
   FROM public.reservations r
  WHERE r.app_id IS DISTINCT FROM p_excluded
    AND (r.status IN ('Pending','Confirmed','InProgress')
         OR (r.status = 'Pending Payment' AND (r.payment_expires_at IS NULL OR r.payment_expires_at > clock_timestamp())))
    AND r.start_at < p_until AND coalesce(r.reserved_until, r.end_at) > p_from
  ORDER BY r.start_at
$$;
REVOKE ALL ON FUNCTION public.mvc_travel_stops(timestamptz, timestamptz, uuid) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.mvc_travel_stops(timestamptz, timestamptz, uuid) TO makeup_backend;
