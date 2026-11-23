-- 0023: the owner sets the most people one booking may have (1 to 20; 20 keeps today's limit).
-- A new app booking with more people than that is refused.

ALTER TABLE public.business_settings ADD COLUMN IF NOT EXISTS max_people int NOT NULL DEFAULT 20;
ALTER TABLE public.business_settings DROP CONSTRAINT IF EXISTS business_settings_max_people_check;
ALTER TABLE public.business_settings ADD CONSTRAINT business_settings_max_people_check CHECK (max_people BETWEEN 1 AND 20);

CREATE OR REPLACE FUNCTION public.mvc_people_limit() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE most int;
BEGIN
 IF NEW.app_managed THEN
  SELECT max_people INTO STRICT most FROM public.business_settings WHERE id=1;
  IF NEW.people > most THEN RAISE EXCEPTION 'The most you can book is % people.', most; END IF;
 END IF;
 RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION public.mvc_people_limit() FROM PUBLIC, anon, authenticated;
DROP TRIGGER IF EXISTS mvc_people_limit ON public.reservations;
CREATE TRIGGER mvc_people_limit BEFORE INSERT ON public.reservations FOR EACH ROW EXECUTE FUNCTION public.mvc_people_limit();
