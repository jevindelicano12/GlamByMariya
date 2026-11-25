-- 0025: Allow bookings to start before closing/latest hour as long as they finish by midnight (24:00).
-- Matches BookingWindow.Fits / BookingService.SlotIssue so multi-person bookings starting at midday (e.g., 12:00 PM) are not hidden.
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
 OR local_start>=date_trunc('day',local_start)+make_interval(hours=>coalesce(s.after_hours_end_hour,s.close_hour))
 OR local_end>date_trunc('day',local_start)+interval '24 hours' THEN RAISE EXCEPTION 'Choose a future session that fits the studio hours including travel time'; END IF;
 IF EXISTS(SELECT 1 FROM public.artist_availability WHERE artist_id=aid) AND NOT EXISTS
 (SELECT 1 FROM public.artist_availability WHERE artist_id=aid AND weekday=extract(dow FROM local_start)::int
 AND local_start>=local_start::date+start_time AND local_end<=local_start::date+end_time) THEN RAISE EXCEPTION 'The artist is outside weekly availability'; END IF;
 IF EXISTS(SELECT 1 FROM public.blocked_slots WHERE artist_id=aid AND starts<end_at AND finish>start_at)
 OR EXISTS(SELECT 1 FROM public.reservations WHERE artist_id=aid AND id IS DISTINCT FROM excluded AND status IN ('Pending','Pending Payment','Confirmed','InProgress')
 AND starts<coalesce(reserved_until,end_at) AND finish>start_at) THEN RAISE EXCEPTION 'This slot overlaps a reserved appointment. Please choose another time.'; END IF;
END $$;
