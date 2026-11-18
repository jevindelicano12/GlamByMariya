-- Scheduled jobs (Supabase pg_cron). On a database without pg_cron (local Docker tests)
-- this does nothing: bookings still expire before each slot check and when screens load.
DO $outer$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'pg_cron') THEN
    RAISE NOTICE 'pg_cron is not available here; scheduled jobs were not created.';
    RETURN;
  END IF;
  CREATE EXTENSION IF NOT EXISTS pg_cron;
  -- Re-running replaces the jobs instead of adding copies.
  PERFORM cron.unschedule(jobid) FROM cron.job WHERE jobname IN ('glam-expire-unpaid', 'glam-clean-email-codes');
  -- Every minute: unpaid bookings past their 30-minute window become Expired.
  PERFORM cron.schedule('glam-expire-unpaid', '* * * * *', 'SELECT public.mvc_expire_overdue()');
  -- Daily 03:00 UTC: remove email codes and cooldowns that ended more than a day ago.
  PERFORM cron.schedule('glam-clean-email-codes', '0 3 * * *', $job$
    DELETE FROM public.email_codes WHERE expires_at < now() - interval '1 day';
    DELETE FROM public.email_code_cooldowns WHERE until_at < now() - interval '1 day';
  $job$);
END
$outer$;
