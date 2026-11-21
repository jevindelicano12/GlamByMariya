-- 0017: pin the search_path of three small helper functions (Supabase security
-- advisor "Function Search Path Mutable"). Their bodies only call built-ins
-- (current_setting, clock_timestamp, RAISE), so behaviour does not change.
ALTER FUNCTION public.mvc_actor() SET search_path = pg_catalog, public;
ALTER FUNCTION public.mvc_immutable() SET search_path = pg_catalog, public;
ALTER FUNCTION public.mvc_touch() SET search_path = pg_catalog, public;
