-- EF migration payload. No BEGIN/COMMIT: EF owns the transaction.
-- Extends the inspected public tables; never drops a table, row or existing PK/FK.
-- Legacy values are preserved. New snapshots are explicitly marked legacy/new.
SET LOCAL lock_timeout='10s';
SET LOCAL statement_timeout='120s';
CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

ALTER TABLE public.customers ADD COLUMN IF NOT EXISTS app_id uuid NOT NULL DEFAULT gen_random_uuid(),
 ADD COLUMN IF NOT EXISTS first_name varchar(50), ADD COLUMN IF NOT EXISTS last_name varchar(50), ADD COLUMN IF NOT EXISTS age_at_registration smallint,
 ADD COLUMN IF NOT EXISTS gender varchar(10), ADD COLUMN IF NOT EXISTS address varchar(500), ADD COLUMN IF NOT EXISTS password_hash text,
 ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT false, ADD COLUMN IF NOT EXISTS security_stamp uuid NOT NULL DEFAULT gen_random_uuid(),
 ADD COLUMN IF NOT EXISTS locked_until timestamptz, ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE public.customers ALTER COLUMN phone DROP NOT NULL;
DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='customers' AND column_name='email' AND (character_maximum_length IS NULL OR character_maximum_length <> 254)) THEN ALTER TABLE public.customers ALTER COLUMN email TYPE varchar(254); END IF; END $$;
ALTER TABLE public.admin_users ADD COLUMN IF NOT EXISTS app_id uuid NOT NULL DEFAULT gen_random_uuid(),
 ADD COLUMN IF NOT EXISTS email varchar(254), ADD COLUMN IF NOT EXISTS first_name varchar(50), ADD COLUMN IF NOT EXISTS last_name varchar(50),
 ADD COLUMN IF NOT EXISTS age_at_registration smallint, ADD COLUMN IF NOT EXISTS gender varchar(10), ADD COLUMN IF NOT EXISTS address varchar(500),
 ADD COLUMN IF NOT EXISTS security_stamp uuid NOT NULL DEFAULT gen_random_uuid(), ADD COLUMN IF NOT EXISTS locked_until timestamptz,
 ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now(), ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
CREATE UNIQUE INDEX IF NOT EXISTS ux_mvc_customer_uuid ON public.customers(app_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mvc_admin_uuid ON public.admin_users(app_id);
CREATE TABLE IF NOT EXISTS public.account_identities(
 id uuid PRIMARY KEY, customer_id bigint UNIQUE REFERENCES public.customers(id) ON DELETE RESTRICT,
 admin_id bigint UNIQUE REFERENCES public.admin_users(id) ON DELETE RESTRICT,
 login text, created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now(),
 CHECK ((customer_id IS NOT NULL)::int+(admin_id IS NOT NULL)::int=1));
CREATE UNIQUE INDEX IF NOT EXISTS ux_users_email ON public.account_identities(lower(btrim(login))) WHERE login IS NOT NULL;
INSERT INTO public.account_identities(id,customer_id,login) SELECT app_id,id,nullif(btrim(email),'') FROM public.customers ON CONFLICT (id) DO NOTHING;
INSERT INTO public.account_identities(id,admin_id,login) SELECT app_id,id,coalesce(nullif(btrim(email),''),username) FROM public.admin_users ON CONFLICT (id) DO NOTHING;
CREATE OR REPLACE FUNCTION public.mvc_account_sync() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
BEGIN
 IF TG_OP='UPDATE' AND NEW.app_id<>OLD.app_id THEN RAISE EXCEPTION 'Account lookup identity is immutable'; END IF;
 IF TG_TABLE_NAME='customers' THEN
  INSERT INTO public.account_identities(id,customer_id,login) VALUES(NEW.app_id,NEW.id,nullif(btrim(NEW.email),''))
  ON CONFLICT(id) DO UPDATE SET login=excluded.login;
 ELSE
  INSERT INTO public.account_identities(id,admin_id,login) VALUES(NEW.app_id,NEW.id,coalesce(nullif(btrim(NEW.email),''),NEW.username))
  ON CONFLICT(id) DO UPDATE SET login=excluded.login;
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS mvc_account ON public.customers;
CREATE TRIGGER mvc_account AFTER INSERT OR UPDATE ON public.customers FOR EACH ROW EXECUTE FUNCTION public.mvc_account_sync();
DROP TRIGGER IF EXISTS mvc_account ON public.admin_users;
CREATE TRIGGER mvc_account AFTER INSERT OR UPDATE ON public.admin_users FOR EACH ROW EXECUTE FUNCTION public.mvc_account_sync();

CREATE TABLE IF NOT EXISTS public.catalog_categories(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),name varchar(60) NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now(),CHECK(length(btrim(name))>0));
CREATE UNIQUE INDEX IF NOT EXISTS ux_mvc_categories ON public.catalog_categories(lower(name));
INSERT INTO public.catalog_categories(id,name) VALUES('30000000-0000-0000-0000-000000000004','Signature') ON CONFLICT (id) DO NOTHING;
ALTER TABLE public.services ADD COLUMN IF NOT EXISTS app_id uuid NOT NULL DEFAULT gen_random_uuid(),
 ADD COLUMN IF NOT EXISTS category_id uuid NOT NULL DEFAULT '30000000-0000-0000-0000-000000000004' REFERENCES public.catalog_categories(id),
 ADD COLUMN IF NOT EXISTS group_price numeric(14,2), ADD COLUMN IF NOT EXISTS group_minimum_people smallint NOT NULL DEFAULT 4,
 ADD COLUMN IF NOT EXISTS visual_word varchar(40) NOT NULL DEFAULT '', ADD COLUMN IF NOT EXISTS is_archived boolean NOT NULL DEFAULT false,
 ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now(),ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='mvc_service_group') THEN ALTER TABLE public.services ADD CONSTRAINT mvc_service_group CHECK(group_price IS NULL OR (group_price>0 AND group_price<=price)); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='mvc_service_threshold') THEN ALTER TABLE public.services ADD CONSTRAINT mvc_service_threshold CHECK(group_minimum_people BETWEEN 2 AND 20); END IF; END $$;
CREATE UNIQUE INDEX IF NOT EXISTS ux_mvc_service_uuid ON public.services(app_id);
CREATE TABLE IF NOT EXISTS public.makeup_addons(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),category_id uuid NOT NULL REFERENCES public.catalog_categories(id),
 name varchar(100) NOT NULL,description text NOT NULL DEFAULT '',price numeric(14,2) NOT NULL CHECK(price>=0),minutes int NOT NULL CHECK(minutes BETWEEN 0 AND 480),
 visual_word varchar(40) NOT NULL DEFAULT '',is_active boolean NOT NULL DEFAULT true,is_archived boolean NOT NULL DEFAULT false,
 created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now(),CHECK(NOT is_archived OR NOT is_active));
INSERT INTO public.makeup_addons(id, category_id, name, description, price, minutes) VALUES
 ('50000000-0000-0000-0000-000000000001','30000000-0000-0000-0000-000000000004','Lash application','A little extra definition',250,15),
 ('50000000-0000-0000-0000-000000000002','30000000-0000-0000-0000-000000000004','Hair styling','Complete your look',800,45),
 ('50000000-0000-0000-0000-000000000003','30000000-0000-0000-0000-000000000004','Touch-up kit','Stay fresh throughout the day',350,0)
 ON CONFLICT (id) DO NOTHING;
ALTER TABLE public.makeup_artists ADD COLUMN IF NOT EXISTS app_id uuid NOT NULL DEFAULT gen_random_uuid(),
 ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
CREATE UNIQUE INDEX IF NOT EXISTS ux_mvc_artist_uuid ON public.makeup_artists(app_id);
CREATE TABLE IF NOT EXISTS public.business_settings(id smallint PRIMARY KEY DEFAULT 1 CHECK(id=1),time_zone text NOT NULL DEFAULT 'Asia/Manila',
 open_hour smallint NOT NULL DEFAULT 8,close_hour smallint NOT NULL DEFAULT 18,buffer_minutes int NOT NULL DEFAULT 45,
 travel_fee_informational numeric(14,2) NOT NULL DEFAULT 250 CHECK(travel_fee_informational>=0),
 down_payment_percent numeric(5,2) NOT NULL DEFAULT 50 CHECK(down_payment_percent>=1 AND down_payment_percent<=100 AND floor(down_payment_percent)=down_payment_percent),
 payment_instructions text NOT NULL DEFAULT 'Contact the artist for verified payment details. Verify the recipient before sending money.',
 policy_version varchar(40) NOT NULL DEFAULT 'booking-v1',travel_reminder_version varchar(40) NOT NULL DEFAULT 'travel-v1',
 selected_artist_id bigint REFERENCES public.makeup_artists(id),created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now(),
 CHECK(open_hour BETWEEN 0 AND 23 AND close_hour BETWEEN 1 AND 24 AND open_hour<close_hour),CHECK(buffer_minutes BETWEEN 0 AND 180));
-- Configurable down payment for pre-existing installs of this same migration (no-ops on fresh installs).
ALTER TABLE public.business_settings ADD COLUMN IF NOT EXISTS down_payment_percent numeric(5,2) NOT NULL DEFAULT 50;
ALTER TABLE public.business_settings DROP CONSTRAINT IF EXISTS business_settings_down_payment_percent_check;
ALTER TABLE public.business_settings ADD CONSTRAINT business_settings_down_payment_percent_check CHECK(down_payment_percent>=1 AND down_payment_percent<=100 AND floor(down_payment_percent)=down_payment_percent);
INSERT INTO public.business_settings(id) VALUES(1) ON CONFLICT (id) DO NOTHING;
CREATE TABLE IF NOT EXISTS public.service_areas(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),name varchar(100) NOT NULL,is_active boolean NOT NULL DEFAULT true,
 created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now());
CREATE UNIQUE INDEX IF NOT EXISTS ux_mvc_area_name ON public.service_areas(lower(name));
INSERT INTO public.service_areas(name) VALUES('Quezon City'),('Makati'),('Manila'),('Pasig'),('Taguig'),('Mandaluyong') ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS public.media_assets(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),object_key text NOT NULL UNIQUE,
 purpose varchar(20) NOT NULL CHECK(purpose IN ('catalog','payment_qr','payment_proof')),
 content_type varchar(20) NOT NULL CHECK(content_type IN ('image/png','image/jpeg')),byte_length int NOT NULL CHECK(byte_length BETWEEN 1 AND 5000000),
 uploaded_by uuid NOT NULL REFERENCES public.account_identities(id),created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS public.catalog_images(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),service_id bigint REFERENCES public.services(id) ON DELETE CASCADE,
 addon_id uuid REFERENCES public.makeup_addons(id) ON DELETE CASCADE,slot varchar(10) NOT NULL CHECK(slot IN ('card','preview')),
 asset_id uuid NOT NULL REFERENCES public.media_assets(id),created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now(),
 CHECK((service_id IS NOT NULL)::int+(addon_id IS NOT NULL)::int=1),UNIQUE(service_id,slot),UNIQUE(addon_id,slot));
CREATE TABLE IF NOT EXISTS public.payment_methods(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),name varchar(30) NOT NULL UNIQUE,
 accepts_deposits boolean NOT NULL,is_active boolean NOT NULL DEFAULT true,qr_asset_id uuid REFERENCES public.media_assets(id),
 created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now(),
 CHECK(name IN ('GCash','Maya','Bank Transfer','Cash')),CHECK(accepts_deposits=(name<>'Cash')),CHECK(name<>'Cash' OR qr_asset_id IS NULL));
INSERT INTO public.payment_methods(name,accepts_deposits) VALUES('GCash',true),('Maya',true),('Bank Transfer',true),('Cash',false) ON CONFLICT (name) DO NOTHING;

ALTER TABLE public.reservations ADD COLUMN IF NOT EXISTS app_id uuid NOT NULL DEFAULT gen_random_uuid(),
 ADD COLUMN IF NOT EXISTS app_managed boolean NOT NULL DEFAULT false,ADD COLUMN IF NOT EXISTS snapshot_source text NOT NULL DEFAULT 'legacy_total_and_interval',
 ADD COLUMN IF NOT EXISTS customer_name_snapshot text,ADD COLUMN IF NOT EXISTS service_name_snapshot text,ADD COLUMN IF NOT EXISTS regular_price_snapshot numeric(14,2),
 ADD COLUMN IF NOT EXISTS service_minutes_snapshot int,ADD COLUMN IF NOT EXISTS group_price_snapshot numeric(14,2),ADD COLUMN IF NOT EXISTS group_minimum_snapshot smallint NOT NULL DEFAULT 4,
 ADD COLUMN IF NOT EXISTS booking_type varchar(10) NOT NULL DEFAULT 'individual',ADD COLUMN IF NOT EXISTS people smallint NOT NULL DEFAULT 1,
 ADD COLUMN IF NOT EXISTS applied_price_per_person numeric(14,2),ADD COLUMN IF NOT EXISTS addons_total_snapshot numeric(14,2) NOT NULL DEFAULT 0,
 ADD COLUMN IF NOT EXISTS addons_minutes_snapshot int NOT NULL DEFAULT 0,ADD COLUMN IF NOT EXISTS buffer_minutes_snapshot int NOT NULL DEFAULT 0,
 ADD COLUMN IF NOT EXISTS reserved_until timestamptz,ADD COLUMN IF NOT EXISTS time_zone_snapshot text NOT NULL DEFAULT 'Asia/Manila',
 ADD COLUMN IF NOT EXISTS area_id uuid REFERENCES public.service_areas(id),ADD COLUMN IF NOT EXISTS area_name_snapshot text,
 ADD COLUMN IF NOT EXISTS travel_fee_informational_snapshot numeric(14,2) NOT NULL DEFAULT 0,
 ADD COLUMN IF NOT EXISTS down_payment_percent_snapshot numeric(5,2) NOT NULL DEFAULT 50,
 ADD COLUMN IF NOT EXISTS required_down_payment_snapshot numeric(14,2),
 ADD COLUMN IF NOT EXISTS policy_accepted_at timestamptz,ADD COLUMN IF NOT EXISTS policy_version text,ADD COLUMN IF NOT EXISTS travel_reminder_acknowledged_at timestamptz,
 ADD COLUMN IF NOT EXISTS travel_reminder_version text,ADD COLUMN IF NOT EXISTS reviewed_by uuid REFERENCES public.account_identities(id),ADD COLUMN IF NOT EXISTS reviewed_at timestamptz,
 ADD COLUMN IF NOT EXISTS cancellation_confirmed_at timestamptz,ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
-- New metadata only: original IDs, totals, references, dates/statuses remain unchanged.
UPDATE public.reservations r SET customer_name_snapshot=c.full_name,service_name_snapshot=s.name,regular_price_snapshot=r.total_price,
 applied_price_per_person=r.total_price,service_minutes_snapshot=greatest(1,extract(epoch FROM(r.end_at-r.start_at))::int/60),reserved_until=r.end_at
 FROM public.customers c,public.services s WHERE c.id=r.customer_id AND s.id=r.service_id AND r.customer_name_snapshot IS NULL;
ALTER TABLE public.reservations DROP CONSTRAINT IF EXISTS reservations_status_check;
ALTER TABLE public.reservations ADD CONSTRAINT reservations_status_check CHECK(status IN
 ('Pending','Pending Payment','Confirmed','InProgress','Completed','Cancelled','Rejected','NoShow','No-show','Incomplete'));
CREATE UNIQUE INDEX IF NOT EXISTS ux_mvc_reservation_uuid ON public.reservations(app_id);
DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='mvc_travel_overlap') THEN ALTER TABLE public.reservations ADD CONSTRAINT mvc_travel_overlap EXCLUDE USING gist
 (artist_id WITH =,tstzrange(start_at,coalesce(reserved_until,end_at),'[)') WITH &&)
 WHERE(status IN ('Pending','Confirmed','InProgress')); END IF; END $$;
CREATE INDEX IF NOT EXISTS ix_mvc_customer_history ON public.reservations(customer_id,start_at DESC);
ALTER TABLE public.blocked_slots ADD COLUMN IF NOT EXISTS app_id uuid NOT NULL DEFAULT gen_random_uuid(),
 ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now(),ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
CREATE UNIQUE INDEX IF NOT EXISTS ux_mvc_block_uuid ON public.blocked_slots(app_id);
CREATE TABLE IF NOT EXISTS public.reservation_addons(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),reservation_id bigint NOT NULL REFERENCES public.reservations(id),
 addon_id uuid NOT NULL REFERENCES public.makeup_addons(id),name_snapshot varchar(100) NOT NULL,price_snapshot numeric(14,2) NOT NULL CHECK(price_snapshot>=0),
 minutes_snapshot int NOT NULL CHECK(minutes_snapshot BETWEEN 0 AND 480),created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now(),UNIQUE(reservation_id,addon_id));
ALTER TABLE public.payments ADD COLUMN IF NOT EXISTS app_id uuid NOT NULL DEFAULT gen_random_uuid(),ADD COLUMN IF NOT EXISTS app_managed boolean NOT NULL DEFAULT false,
 ADD COLUMN IF NOT EXISTS purpose varchar(15),ADD COLUMN IF NOT EXISTS reference varchar(150),ADD COLUMN IF NOT EXISTS proof_asset_id uuid REFERENCES public.media_assets(id),
 ADD COLUMN IF NOT EXISTS review_note text NOT NULL DEFAULT '',ADD COLUMN IF NOT EXISTS verification_status varchar(20),
 ADD COLUMN IF NOT EXISTS submitted_by uuid REFERENCES public.account_identities(id),ADD COLUMN IF NOT EXISTS verified_by uuid REFERENCES public.account_identities(id),
 ADD COLUMN IF NOT EXISTS verified_at timestamptz,ADD COLUMN IF NOT EXISTS idempotency_key uuid NOT NULL DEFAULT gen_random_uuid(),
 ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now(),ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
CREATE UNIQUE INDEX IF NOT EXISTS ux_mvc_payment_uuid ON public.payments(app_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mvc_payment_idempotency ON public.payments(idempotency_key);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mvc_payment_reference ON public.payments(method,lower(btrim(reference))) WHERE reference IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_mvc_deposit ON public.payments(reservation_id) WHERE purpose='down_payment';
CREATE UNIQUE INDEX IF NOT EXISTS ux_mvc_balance ON public.payments(reservation_id) WHERE purpose='balance';
CREATE TABLE IF NOT EXISTS public.payment_revisions(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),payment_id bigint NOT NULL REFERENCES public.payments(id),
 old_reference text,new_reference text,old_proof_asset_id uuid REFERENCES public.media_assets(id),new_proof_asset_id uuid REFERENCES public.media_assets(id),
 old_status text,new_status text,note text NOT NULL DEFAULT '',actor_id uuid REFERENCES public.account_identities(id),
 created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS public.payment_adjustments(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),reservation_id bigint NOT NULL REFERENCES public.reservations(id),
 payment_id bigint NOT NULL REFERENCES public.payments(id),amount numeric(14,2) NOT NULL CHECK(amount<>0),reason text NOT NULL CHECK(length(btrim(reason))>0),
 recorded_by uuid NOT NULL REFERENCES public.account_identities(id),idempotency_key uuid NOT NULL UNIQUE DEFAULT gen_random_uuid(),
 created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS public.reservation_closures(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),reservation_id bigint NOT NULL UNIQUE REFERENCES public.reservations(id),
 kind varchar(15) NOT NULL CHECK(kind IN ('cancellation','rejection')),verified_paid_snapshot numeric(14,2) NOT NULL CHECK(verified_paid_snapshot>=0),
 cancellation_fee numeric(14,2) NOT NULL,refund_due numeric(14,2) GENERATED ALWAYS AS(greatest(0::numeric,verified_paid_snapshot-cancellation_fee)) STORED,
 reason text NOT NULL,actor_id uuid REFERENCES public.account_identities(id),confirmed_at timestamptz,
 created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now(),
 CHECK((kind='cancellation' AND cancellation_fee=200 AND confirmed_at IS NOT NULL) OR(kind='rejection' AND cancellation_fee=0 AND length(btrim(reason))>0)));
CREATE TABLE IF NOT EXISTS public.refunds(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),closure_id uuid NOT NULL REFERENCES public.reservation_closures(id),
 method_id uuid NOT NULL REFERENCES public.payment_methods(id),amount numeric(14,2) NOT NULL CHECK(amount>0),transfer_reference varchar(150) NOT NULL CHECK(length(btrim(transfer_reference))>0),
 sent_at timestamptz NOT NULL,recorded_by uuid NOT NULL REFERENCES public.account_identities(id),idempotency_key uuid NOT NULL UNIQUE DEFAULT gen_random_uuid(),
 created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now());
CREATE UNIQUE INDEX IF NOT EXISTS ux_mvc_refund_reference ON public.refunds(method_id,lower(btrim(transfer_reference)));
CREATE TABLE IF NOT EXISTS public.reschedule_requests(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),reservation_id bigint NOT NULL REFERENCES public.reservations(id),
 requested_by uuid NOT NULL REFERENCES public.account_identities(id),original_start timestamptz NOT NULL,requested_start timestamptz NOT NULL,
 status varchar(15) NOT NULL DEFAULT 'Pending' CHECK(status IN ('Pending','Approved','Declined','Superseded','Withdrawn')),
 reason text NOT NULL DEFAULT '',reviewed_by uuid REFERENCES public.account_identities(id),reviewed_at timestamptz,
 created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now(),CHECK(original_start<>requested_start),
 CHECK(status NOT IN ('Approved','Declined') OR(reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL)),CHECK(status<>'Declined' OR length(btrim(reason))>0));
CREATE UNIQUE INDEX IF NOT EXISTS ux_mvc_reschedule_pending ON public.reschedule_requests(reservation_id) WHERE status='Pending';
ALTER TABLE public.reservation_history ADD COLUMN IF NOT EXISTS actor_id uuid REFERENCES public.account_identities(id),ADD COLUMN IF NOT EXISTS note text,
 ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
CREATE TABLE IF NOT EXISTS public.notifications(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),user_id uuid NOT NULL REFERENCES public.account_identities(id),
 reservation_id bigint REFERENCES public.reservations(id),kind varchar(20) NOT NULL DEFAULT 'booking',message text NOT NULL,
 channel varchar(10) NOT NULL DEFAULT 'in_app' CHECK(channel IN ('in_app','email','sms')),scheduled_at timestamptz NOT NULL DEFAULT now(),
 delivery_status varchar(10) NOT NULL DEFAULT 'Sent' CHECK(delivery_status IN ('Pending','Sent','Failed','Cancelled')),sent_at timestamptz DEFAULT now(),read_at timestamptz,
 deduplication_key text UNIQUE,created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now());
CREATE INDEX IF NOT EXISTS ix_mvc_inbox ON public.notifications(user_id,created_at DESC);
CREATE INDEX IF NOT EXISTS ix_mvc_reminders ON public.notifications(scheduled_at) WHERE delivery_status='Pending';
CREATE TABLE IF NOT EXISTS public.audit_logs(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),actor_id uuid REFERENCES public.account_identities(id),
 reservation_id bigint REFERENCES public.reservations(id),entity_type text NOT NULL,event text NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now());
CREATE INDEX IF NOT EXISTS ix_mvc_audit ON public.audit_logs(created_at DESC);

CREATE OR REPLACE FUNCTION public.mvc_actor() RETURNS uuid LANGUAGE sql STABLE AS $$ SELECT nullif(current_setting('makeup.actor_id',true),'')::uuid $$;
CREATE OR REPLACE FUNCTION public.mvc_assert_role(who uuid,wanted text) RETURNS void LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
BEGIN
 IF wanted='SuperAdmin' AND EXISTS(SELECT 1 FROM public.admin_users WHERE app_id=who AND role='SuperAdmin' AND is_active) THEN RETURN; END IF;
 IF wanted='Admin' AND EXISTS(SELECT 1 FROM public.admin_users WHERE app_id=who AND is_active) THEN RETURN; END IF;
 IF wanted='Customer' AND EXISTS(SELECT 1 FROM public.customers WHERE app_id=who AND is_active) THEN RETURN; END IF;
 RAISE EXCEPTION 'An active % account is required',wanted;
END $$;
CREATE OR REPLACE FUNCTION public.mvc_paid(rid bigint) RETURNS numeric LANGUAGE sql STABLE SET search_path=public,pg_catalog AS $$
 SELECT coalesce((SELECT sum(amount) FROM public.payments WHERE reservation_id=rid AND status='Paid'),0)
 +coalesce((SELECT sum(amount) FROM public.payment_adjustments WHERE reservation_id=rid),0)
$$;
CREATE OR REPLACE FUNCTION public.mvc_check_slot(aid bigint,starts timestamptz,ends timestamptz,buffer int,excluded bigint DEFAULT NULL)
RETURNS void LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE s public.business_settings%ROWTYPE; local_start timestamp; local_end timestamp; finish timestamptz;
BEGIN
 PERFORM 1 FROM public.makeup_artists WHERE id=aid AND is_active FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Choose an active makeup artist'; END IF;
 SELECT * INTO STRICT s FROM public.business_settings WHERE id=1;
 finish:=ends+make_interval(mins=>buffer);local_start:=starts AT TIME ZONE s.time_zone;local_end:=finish AT TIME ZONE s.time_zone;
 IF starts<=clock_timestamp() OR ends<=starts OR ends-starts>interval '24 hours' OR buffer NOT BETWEEN 0 AND 180
 OR local_start<date_trunc('day',local_start)+make_interval(hours=>s.open_hour)
 OR local_end>date_trunc('day',local_start)+make_interval(hours=>s.close_hour) THEN RAISE EXCEPTION 'Choose a future session that fits the studio hours including travel time'; END IF;
 IF EXISTS(SELECT 1 FROM public.artist_availability WHERE artist_id=aid) AND NOT EXISTS
 (SELECT 1 FROM public.artist_availability WHERE artist_id=aid AND weekday=extract(dow FROM local_start)::int
 AND local_start>=local_start::date+start_time AND local_end<=local_start::date+end_time) THEN RAISE EXCEPTION 'The artist is outside weekly availability'; END IF;
 IF EXISTS(SELECT 1 FROM public.blocked_slots WHERE artist_id=aid AND starts<end_at AND finish>start_at)
 OR EXISTS(SELECT 1 FROM public.reservations WHERE artist_id=aid AND id IS DISTINCT FROM excluded AND status IN ('Pending','Confirmed','InProgress')
 AND starts<coalesce(reserved_until,end_at) AND finish>start_at) THEN RAISE EXCEPTION 'Artist is unavailable: appointment or travel/availability block overlaps'; END IF;
END $$;
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
CREATE OR REPLACE FUNCTION public.mvc_block_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
BEGIN
 PERFORM 1 FROM public.makeup_artists WHERE id=NEW.artist_id FOR UPDATE;
 IF EXISTS(SELECT 1 FROM public.reservations WHERE artist_id=NEW.artist_id AND status IN ('Pending','Confirmed','InProgress')
 AND NEW.start_at<coalesce(reserved_until,end_at) AND NEW.end_at>start_at) THEN RAISE EXCEPTION 'Availability block overlaps a reserved appointment'; END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS mvc_block_guard ON public.blocked_slots;
CREATE TRIGGER mvc_block_guard BEFORE INSERT OR UPDATE ON public.blocked_slots FOR EACH ROW EXECUTE FUNCTION public.mvc_block_guard();
CREATE OR REPLACE FUNCTION public.mvc_addon_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE a public.makeup_addons%ROWTYPE;
BEGIN
 IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'Booked extras are immutable'; END IF;
 PERFORM 1 FROM public.reservations WHERE id=NEW.reservation_id FOR UPDATE;
 IF EXISTS(SELECT 1 FROM public.payments WHERE reservation_id=NEW.reservation_id) THEN RAISE EXCEPTION 'Add extras before submitting payment'; END IF;
 SELECT * INTO STRICT a FROM public.makeup_addons WHERE id=NEW.addon_id AND is_active AND NOT is_archived;
 NEW.name_snapshot:=a.name;NEW.price_snapshot:=a.price;NEW.minutes_snapshot:=a.minutes;RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS mvc_addon_guard ON public.reservation_addons;
CREATE TRIGGER mvc_addon_guard BEFORE INSERT OR UPDATE OR DELETE ON public.reservation_addons FOR EACH ROW EXECUTE FUNCTION public.mvc_addon_guard();
CREATE OR REPLACE FUNCTION public.mvc_payment_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE r public.reservations%ROWTYPE; m public.payment_methods%ROWTYPE;
BEGIN
 SELECT * INTO STRICT r FROM public.reservations WHERE id=NEW.reservation_id FOR UPDATE;
 IF TG_OP='UPDATE' AND (OLD.status IN ('Paid','Refunded') OR ROW(NEW.reservation_id,NEW.app_id,NEW.app_managed,NEW.amount,NEW.method,NEW.purpose)
 IS DISTINCT FROM ROW(OLD.reservation_id,OLD.app_id,OLD.app_managed,OLD.amount,OLD.method,OLD.purpose)) THEN RAISE EXCEPTION 'Verified payment evidence is immutable; record a signed adjustment instead'; END IF;
 IF r.status NOT IN ('Pending','Pending Payment','Confirmed','InProgress') THEN RAISE EXCEPTION 'Booking is closed'; END IF;
 IF NEW.app_managed THEN
  SELECT * INTO STRICT m FROM public.payment_methods WHERE name=NEW.method;
  IF length(btrim(coalesce(NEW.reference,''))) NOT BETWEEN 1 AND 150 THEN RAISE EXCEPTION 'Provide the actual payment reference'; END IF;
  IF TG_OP='INSERT' AND NOT m.is_active THEN RAISE EXCEPTION 'Payment method is inactive'; END IF;
  IF NEW.purpose='down_payment' AND (NOT m.accepts_deposits OR NEW.amount<>coalesce(r.required_down_payment_snapshot,round(r.total_price*.5,2)) OR r.status NOT IN ('Pending','Pending Payment'))
   THEN RAISE EXCEPTION 'Deposit must equal the required down payment (the booking''s recorded percentage of the total) and use an accepted online method'; END IF;
  IF NEW.purpose='balance' AND (NEW.method<>'Cash' OR NEW.status<>'Paid' OR r.status NOT IN ('Confirmed','InProgress') OR r.end_at>clock_timestamp()
   OR NEW.amount<>r.total_price-public.mvc_paid(r.id)) THEN RAISE EXCEPTION 'Record the exact final Cash balance after the appointment'; END IF;
  IF NEW.purpose NOT IN ('down_payment','balance') OR NEW.purpose IS NULL THEN RAISE EXCEPTION 'Invalid payment purpose'; END IF;
  IF NEW.verification_status NOT IN ('Pending Verification','Correction Requested','Verified','Rejected') OR NEW.verification_status IS NULL THEN RAISE EXCEPTION 'Invalid payment review status'; END IF;
  IF (NEW.status='Paid')<>(NEW.verification_status='Verified') THEN RAISE EXCEPTION 'Payment verification states disagree'; END IF;
  IF NEW.status='Paid' THEN
   PERFORM public.mvc_assert_role(NEW.verified_by,'Admin');IF NEW.verified_at IS NULL THEN RAISE EXCEPTION 'Verification time is required'; END IF;
  ELSIF NEW.verified_by IS NOT NULL OR NEW.verified_at IS NOT NULL THEN RAISE EXCEPTION 'Unverified submissions cannot contain verified evidence'; END IF;
  IF NEW.proof_asset_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM public.media_assets WHERE id=NEW.proof_asset_id AND purpose='payment_proof') THEN RAISE EXCEPTION 'Use a private proof asset'; END IF;
 END IF;
 IF TG_OP='UPDATE' THEN
  INSERT INTO public.payment_revisions(payment_id,old_reference,new_reference,old_proof_asset_id,new_proof_asset_id,old_status,new_status,note,actor_id)
  VALUES(NEW.id,OLD.reference,NEW.reference,OLD.proof_asset_id,NEW.proof_asset_id,OLD.verification_status,NEW.verification_status,NEW.review_note,public.mvc_actor());
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS mvc_payment_guard ON public.payments;
CREATE TRIGGER mvc_payment_guard BEFORE INSERT OR UPDATE ON public.payments FOR EACH ROW EXECUTE FUNCTION public.mvc_payment_guard();
CREATE OR REPLACE FUNCTION public.mvc_finance_integrity() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE rid bigint;r public.reservations%ROWTYPE;paid numeric;
BEGIN
 IF TG_TABLE_NAME='reservations' THEN rid:=NEW.id; ELSE rid:=NEW.reservation_id; END IF;
 SELECT * INTO STRICT r FROM public.reservations WHERE id=rid FOR UPDATE;
 IF NOT r.app_managed THEN RETURN NULL; END IF;
 IF r.addons_total_snapshot<>coalesce((SELECT sum(price_snapshot) FROM public.reservation_addons WHERE reservation_id=rid),0)
 OR r.addons_minutes_snapshot<>coalesce((SELECT sum(minutes_snapshot) FROM public.reservation_addons WHERE reservation_id=rid),0) THEN RAISE EXCEPTION 'Quoted extras disagree with immutable line items'; END IF;
 IF (SELECT count(*) FROM public.payments WHERE reservation_id=rid AND purpose='down_payment')<>1 THEN RAISE EXCEPTION 'Every submitted booking needs exactly one deposit submission'; END IF;
 paid:=public.mvc_paid(rid);
 IF paid<0 OR paid>r.total_price THEN RAISE EXCEPTION 'Recognized payments must stay between zero and booking total'; END IF;
 IF r.status='Pending Payment' AND paid<>0 THEN RAISE EXCEPTION 'Verify and approve/reject in one transaction'; END IF;
 IF r.status IN ('Confirmed','Completed','No-show','Incomplete') AND (paid<coalesce(r.required_down_payment_snapshot,round(r.total_price*.5,2)) OR NOT EXISTS
 (SELECT 1 FROM public.payments WHERE reservation_id=rid AND purpose='down_payment' AND status='Paid')) THEN RAISE EXCEPTION 'Verify the exact required deposit before confirmation'; END IF;
 IF r.status='Completed' AND paid<>r.total_price THEN RAISE EXCEPTION 'Full verified balance is required for completion'; END IF;
 RETURN NULL;
END $$;
DO $$ DECLARE t text;BEGIN FOREACH t IN ARRAY ARRAY['reservations','reservation_addons','payments','payment_adjustments'] LOOP
 EXECUTE format('DROP TRIGGER IF EXISTS mvc_finance ON public.%I',t); EXECUTE format('CREATE CONSTRAINT TRIGGER mvc_finance AFTER INSERT OR UPDATE ON public.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.mvc_finance_integrity()',t);
END LOOP;END $$;
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
   VALUES(NEW.id,CASE WHEN NEW.status='Cancelled' THEN 'cancellation' ELSE 'rejection' END,public.mvc_paid(NEW.id),CASE WHEN NEW.status='Cancelled' THEN 200 ELSE 0 END,
    coalesce(NEW.notes,''),public.mvc_actor(),NEW.cancellation_confirmed_at);
   UPDATE public.reschedule_requests SET status='Withdrawn' WHERE reservation_id=NEW.id AND status='Pending';
   UPDATE public.notifications SET delivery_status='Cancelled' WHERE reservation_id=NEW.id AND kind='reminder' AND delivery_status='Pending';
  END IF;
 END IF;RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS mvc_reservation_event ON public.reservations;
CREATE TRIGGER mvc_reservation_event AFTER INSERT OR UPDATE ON public.reservations FOR EACH ROW EXECUTE FUNCTION public.mvc_reservation_event();
CREATE OR REPLACE FUNCTION public.mvc_reschedule_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE r public.reservations%ROWTYPE;
BEGIN
 SELECT * INTO STRICT r FROM public.reservations WHERE id=NEW.reservation_id FOR UPDATE;
 IF TG_OP='INSERT' THEN
  IF NEW.status<>'Pending' OR r.status NOT IN ('Confirmed','InProgress') OR r.start_at<=clock_timestamp()
  OR NOT EXISTS(SELECT 1 FROM public.customers WHERE id=r.customer_id AND app_id=NEW.requested_by) THEN RAISE EXCEPTION 'Only the owner of a future confirmed booking can reschedule'; END IF;
  NEW.original_start:=r.start_at;PERFORM public.mvc_check_slot(r.artist_id,NEW.requested_start,NEW.requested_start+(r.end_at-r.start_at),r.buffer_minutes_snapshot,r.id);
 ELSE
  IF OLD.status<>'Pending' OR ROW(NEW.reservation_id,NEW.original_start,NEW.requested_start,NEW.requested_by) IS DISTINCT FROM ROW(OLD.reservation_id,OLD.original_start,OLD.requested_start,OLD.requested_by)
  THEN RAISE EXCEPTION 'Resolved reschedule history is immutable'; END IF;
  IF NEW.status IN ('Approved','Declined') THEN PERFORM public.mvc_assert_role(NEW.reviewed_by,'Admin'); END IF;
  IF NEW.status='Approved' THEN
   IF r.status NOT IN ('Confirmed','InProgress') OR r.start_at<=clock_timestamp() OR r.start_at<>NEW.original_start THEN RAISE EXCEPTION 'The original slot is no longer reschedulable'; END IF;
   PERFORM public.mvc_check_slot(r.artist_id,NEW.requested_start,NEW.requested_start+(r.end_at-r.start_at),r.buffer_minutes_snapshot,r.id);
  END IF;
 END IF;RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS mvc_reschedule_guard ON public.reschedule_requests;
CREATE TRIGGER mvc_reschedule_guard BEFORE INSERT OR UPDATE ON public.reschedule_requests FOR EACH ROW EXECUTE FUNCTION public.mvc_reschedule_guard();
CREATE OR REPLACE FUNCTION public.mvc_reschedule_apply() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
BEGIN
 IF NEW.status='Approved' AND OLD.status='Pending' THEN
  UPDATE public.reservations SET start_at=NEW.requested_start WHERE id=NEW.reservation_id;
  UPDATE public.notifications SET delivery_status='Cancelled' WHERE reservation_id=NEW.reservation_id AND kind='reminder' AND delivery_status='Pending';
 END IF;RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS mvc_reschedule_apply ON public.reschedule_requests;
CREATE TRIGGER mvc_reschedule_apply AFTER UPDATE ON public.reschedule_requests FOR EACH ROW EXECUTE FUNCTION public.mvc_reschedule_apply();
CREATE OR REPLACE FUNCTION public.mvc_money_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
DECLARE c public.reservation_closures%ROWTYPE;
BEGIN
 PERFORM public.mvc_assert_role(NEW.recorded_by,'Admin');
 IF TG_TABLE_NAME='refunds' THEN
  SELECT * INTO STRICT c FROM public.reservation_closures WHERE id=NEW.closure_id FOR UPDATE;
  IF NEW.amount+coalesce((SELECT sum(amount) FROM public.refunds WHERE closure_id=c.id),0)>c.refund_due THEN RAISE EXCEPTION 'Refund exceeds remaining obligation'; END IF;
 ELSE
  PERFORM 1 FROM public.reservations WHERE id=NEW.reservation_id FOR UPDATE;
  IF EXISTS(SELECT 1 FROM public.reservation_closures WHERE reservation_id=NEW.reservation_id) OR NOT EXISTS
  (SELECT 1 FROM public.payments WHERE id=NEW.payment_id AND reservation_id=NEW.reservation_id AND status='Paid') THEN RAISE EXCEPTION 'Adjust an actual verified payment before settlement only'; END IF;
 END IF;RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS mvc_refund_guard ON public.refunds;
CREATE TRIGGER mvc_refund_guard BEFORE INSERT ON public.refunds FOR EACH ROW EXECUTE FUNCTION public.mvc_money_guard();
DROP TRIGGER IF EXISTS mvc_adjustment_guard ON public.payment_adjustments;
CREATE TRIGGER mvc_adjustment_guard BEFORE INSERT ON public.payment_adjustments FOR EACH ROW EXECUTE FUNCTION public.mvc_money_guard();
CREATE OR REPLACE FUNCTION public.mvc_immutable() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN RAISE EXCEPTION '% is append-only',TG_TABLE_NAME;END $$;
DO $$ DECLARE t text;BEGIN FOREACH t IN ARRAY ARRAY['reservation_history','payment_revisions','payment_adjustments','reservation_closures','refunds','audit_logs'] LOOP
 EXECUTE format('DROP TRIGGER IF EXISTS mvc_immutable ON public.%I',t);
 EXECUTE format('CREATE TRIGGER mvc_immutable BEFORE UPDATE OR DELETE ON public.%I FOR EACH ROW EXECUTE FUNCTION public.mvc_immutable()',t);
END LOOP;END $$;
DROP TRIGGER IF EXISTS mvc_payment_no_delete ON public.payments;
CREATE TRIGGER mvc_payment_no_delete BEFORE DELETE ON public.payments FOR EACH ROW EXECUTE FUNCTION public.mvc_immutable();
DROP TRIGGER IF EXISTS mvc_reschedule_no_delete ON public.reschedule_requests;
CREATE TRIGGER mvc_reschedule_no_delete BEFORE DELETE ON public.reschedule_requests FOR EACH ROW EXECUTE FUNCTION public.mvc_immutable();
CREATE OR REPLACE FUNCTION public.mvc_touch() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN NEW.updated_at:=clock_timestamp();RETURN NEW;END $$;
CREATE OR REPLACE FUNCTION public.mvc_audit() RETURNS trigger LANGUAGE plpgsql SET search_path=public,pg_catalog AS $$
BEGIN
 INSERT INTO public.audit_logs(actor_id,entity_type,event) VALUES(public.mvc_actor(),TG_TABLE_NAME,TG_OP);RETURN NULL;
END $$;
DO $$ DECLARE t text;BEGIN FOREACH t IN ARRAY ARRAY['customers','admin_users','makeup_artists','services','blocked_slots','reservations','payments','catalog_categories','makeup_addons',
 'business_settings','service_areas','media_assets','catalog_images','payment_methods','reschedule_requests','notifications','account_identities'] LOOP
 EXECUTE format('DROP TRIGGER IF EXISTS mvc_touch ON public.%I',t);
 EXECUTE format('CREATE TRIGGER mvc_touch BEFORE UPDATE ON public.%I FOR EACH ROW EXECUTE FUNCTION public.mvc_touch()',t);
 EXECUTE format('DROP TRIGGER IF EXISTS mvc_audit ON public.%I',t);
 EXECUTE format('CREATE TRIGGER mvc_audit AFTER INSERT OR UPDATE OR DELETE ON public.%I FOR EACH ROW EXECUTE FUNCTION public.mvc_audit()',t);
END LOOP;END $$;

-- EF read projections only. Commands write the real existing numeric-key tables.
CREATE OR REPLACE VIEW public.mvc_roles WITH(security_invoker=true) AS
 SELECT '10000000-0000-0000-0000-000000000001'::uuid AS id,'Admin'::text AS name UNION ALL
 SELECT '10000000-0000-0000-0000-000000000002'::uuid,'Customer'::text UNION ALL
 SELECT '10000000-0000-0000-0000-000000000003'::uuid,'SuperAdmin'::text;
CREATE OR REPLACE VIEW public.mvc_users WITH(security_invoker=true) AS
 SELECT c.app_id AS id,'10000000-0000-0000-0000-000000000002'::uuid AS role_id,coalesce(c.first_name,c.full_name)::text AS first_name,
 coalesce(c.last_name,'')::text AS last_name,coalesce(c.age_at_registration,0)::smallint AS age_at_registration,
 coalesce(c.gender,'')::text AS gender,coalesce(c.address,'')::text AS address,coalesce(c.email,'')::text AS email,
 coalesce(c.password_hash,'')::text AS password_hash,c.is_active,c.security_stamp,c.locked_until FROM public.customers c
 UNION ALL SELECT u.app_id,
 CASE WHEN u.role='SuperAdmin' THEN '10000000-0000-0000-0000-000000000003'::uuid ELSE '10000000-0000-0000-0000-000000000001'::uuid END,
 coalesce(u.first_name,u.username)::text,coalesce(u.last_name,'')::text,
 coalesce(u.age_at_registration,0)::smallint,coalesce(u.gender,'')::text,coalesce(u.address,'')::text,coalesce(u.email,u.username)::text,
 u.password_hash,u.is_active,u.security_stamp,u.locked_until FROM public.admin_users u;
CREATE OR REPLACE VIEW public.mvc_services WITH(security_invoker=true) AS SELECT app_id AS id,category_id,name,coalesce(description,'') AS description,price,
 duration_minutes AS minutes,group_price,group_minimum_people,visual_word,is_active,is_archived FROM public.services;
CREATE OR REPLACE VIEW public.mvc_catalog_images WITH(security_invoker=true) AS SELECT i.id,s.app_id AS service_id,i.addon_id,i.slot,i.asset_id
 FROM public.catalog_images i LEFT JOIN public.services s ON s.id=i.service_id;
CREATE OR REPLACE VIEW public.mvc_payments WITH(security_invoker=true) AS SELECT p.app_id AS id,r.app_id AS booking_id,m.id AS method_id,p.amount,
 coalesce(p.reference,'Legacy payment #'||p.id::text) AS reference,coalesce(p.purpose,'legacy') AS purpose,
 coalesce(p.verification_status,CASE p.status WHEN 'Paid' THEN 'Verified' WHEN 'Failed' THEN 'Rejected' ELSE 'Pending Verification' END) AS status,
 p.proof_asset_id,p.verified_by,coalesce(p.verified_at,p.paid_at) AS verified_at
 FROM public.payments p JOIN public.reservations r ON r.id=p.reservation_id JOIN public.payment_methods m ON m.name=p.method;
CREATE OR REPLACE VIEW public.mvc_booking_addons WITH(security_invoker=true) AS SELECT a.id,r.app_id AS booking_id,a.addon_id,a.name_snapshot,a.price_snapshot,a.minutes_snapshot
 FROM public.reservation_addons a JOIN public.reservations r ON r.id=a.reservation_id;
CREATE OR REPLACE VIEW public.mvc_blocks WITH(security_invoker=true) AS SELECT b.app_id AS id,a.app_id AS artist_id,b.start_at AS from_at,b.end_at AS to_at,coalesce(b.reason,'') AS reason
 FROM public.blocked_slots b JOIN public.makeup_artists a ON a.id=b.artist_id;
CREATE OR REPLACE VIEW public.mvc_reschedules WITH(security_invoker=true) AS SELECT q.id,r.app_id AS booking_id,q.status FROM public.reschedule_requests q JOIN public.reservations r ON r.id=q.reservation_id;
CREATE OR REPLACE VIEW public.mvc_closures WITH(security_invoker=true) AS SELECT c.id,r.app_id AS booking_id FROM public.reservation_closures c JOIN public.reservations r ON r.id=c.reservation_id;
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

-- Browser clients get no permissive policy. Server-only role policies are scoped.
DO $$BEGIN IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='makeup_backend') THEN CREATE ROLE makeup_backend NOLOGIN NOBYPASSRLS;END IF;END $$;
GRANT USAGE ON SCHEMA public TO makeup_backend;
DO $$ DECLARE t text;BEGIN FOREACH t IN ARRAY ARRAY['customers','admin_users','makeup_artists','services','reservations','payments','blocked_slots','artist_availability','reservation_history',
 'account_identities','catalog_categories','makeup_addons','business_settings','service_areas','media_assets','catalog_images','payment_methods','reservation_addons',
 'payment_revisions','payment_adjustments','reservation_closures','refunds','reschedule_requests','notifications','audit_logs'] LOOP
 EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY',t);
 EXECUTE format('DROP POLICY IF EXISTS mvc_backend ON public.%I',t); EXECUTE format('CREATE POLICY mvc_backend ON public.%I TO makeup_backend USING(true) WITH CHECK(true)',t);
 EXECUTE format('GRANT SELECT,INSERT,UPDATE,DELETE ON public.%I TO makeup_backend',t);
END LOOP;END $$;
GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA public TO makeup_backend;
DO $$ DECLARE x record;BEGIN
 FOR x IN SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind='v' AND c.relname LIKE 'mvc_%' LOOP
  EXECUTE format('GRANT SELECT ON public.%I TO makeup_backend',x.relname);
 END LOOP;
 FOR x IN SELECT p.oid::regprocedure AS signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public' AND p.proname LIKE 'mvc_%' LOOP
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',x.signature);
  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO makeup_backend',x.signature);
 END LOOP;
END $$;
