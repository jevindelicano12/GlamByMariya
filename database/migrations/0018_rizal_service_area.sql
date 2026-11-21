-- 0018: the service area is Quezon City, Marikina and Rizal. The pin box used by
-- 0013 (bookings) and 0015 (artist home/current) stopped at longitude 121.25, which
-- cuts off most of Rizal (Antipolo east, Tanay, Baras, Pililla, Jala-Jala). Widen the
-- east edge to 121.65; the other edges stay. Existing pins are all inside the old box,
-- so they pass the wider check. Safe to re-run.
ALTER TABLE public.reservations DROP CONSTRAINT IF EXISTS reservations_location_pin_check;
ALTER TABLE public.reservations ADD CONSTRAINT reservations_location_pin_check CHECK (
  (location_lat IS NULL) = (location_lng IS NULL)
  AND (location_lat IS NULL OR (location_lat BETWEEN 14.20 AND 14.95 AND location_lng BETWEEN 120.85 AND 121.65)));
ALTER TABLE public.makeup_artists DROP CONSTRAINT IF EXISTS makeup_artists_home_pin_check;
ALTER TABLE public.makeup_artists ADD CONSTRAINT makeup_artists_home_pin_check CHECK (
  (home_lat IS NULL) = (home_lng IS NULL)
  AND (home_lat IS NULL OR (home_lat BETWEEN 14.20 AND 14.95 AND home_lng BETWEEN 120.85 AND 121.65)));
ALTER TABLE public.makeup_artists DROP CONSTRAINT IF EXISTS makeup_artists_current_pin_check;
ALTER TABLE public.makeup_artists ADD CONSTRAINT makeup_artists_current_pin_check CHECK (
  (current_lat IS NULL) = (current_lng IS NULL)
  AND (current_lat IS NULL OR (current_lat BETWEEN 14.20 AND 14.95 AND current_lng BETWEEN 120.85 AND 121.65)));
