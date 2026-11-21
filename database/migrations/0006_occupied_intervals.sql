-- Busy time for one artist, used by PublicStudioStore.OccupiedIntervals to build the
-- availability calendar. Only the time ranges leave the database: no customer,
-- address or block reason. A booking holds its time from start to reserved_until
-- (session + preparation buffer) while it is Confirmed/InProgress/Pending, or
-- Pending Payment inside its 30-minute window (same rule as HoldsReservation in C#).
CREATE OR REPLACE FUNCTION public.mvc_occupied_intervals(aid bigint, from_at timestamptz, to_at timestamptz, excluded uuid)
RETURNS TABLE(occupied_from timestamptz, occupied_until timestamptz)
LANGUAGE sql STABLE SET search_path=public,pg_catalog AS $$
 SELECT r.start_at, coalesce(r.reserved_until, r.end_at)
   FROM public.reservations r
  WHERE r.artist_id = aid AND r.app_id IS DISTINCT FROM excluded
    AND (r.status IN ('Pending','Confirmed','InProgress')
         OR (r.status = 'Pending Payment' AND (r.payment_expires_at IS NULL OR r.payment_expires_at > clock_timestamp())))
    AND r.start_at < to_at AND coalesce(r.reserved_until, r.end_at) > from_at
 UNION ALL
 SELECT b.start_at, b.end_at
   FROM public.blocked_slots b
  WHERE b.artist_id = aid AND b.start_at < to_at AND b.end_at > from_at
$$;
REVOKE ALL ON FUNCTION public.mvc_occupied_intervals(bigint, timestamptz, timestamptz, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.mvc_occupied_intervals(bigint, timestamptz, timestamptz, uuid) TO makeup_backend;
