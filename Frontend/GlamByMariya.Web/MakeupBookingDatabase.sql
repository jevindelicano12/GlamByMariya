-- Glam by Mariya Makeup Booking System -- PostgreSQL 15+ / Npgsql EF Core
-- Reviewed against Controllers/, Models/, Services/, Data/, Views/, JS and tests/.
-- Standalone design only: this file does NOT configure or connect the application.
-- Run ONCE in an EMPTY database using a migration/DDL owner. No DROP/CREATE DATABASE.
-- Example: psql -v ON_ERROR_STOP=1 -d your_empty_database -f MakeupBookingDatabase.sql
-- btree_gist is available on Supabase; extension installation needs owner permission.
-- All money is PHP, numeric(14,2). All instants are timestamptz (UTC via Npgsql).
-- Sections: tables, integrity/triggers, views, seed/demo, sample SELECTs.

BEGIN;
CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE SCHEMA makeup;
SET LOCAL search_path = makeup, public;

-- 1. Accounts: shared authentication for Customer and Admin (the artist is an Admin).
CREATE TABLE roles (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name varchar(30) NOT NULL UNIQUE CHECK (name IN ('Customer','Admin','SuperAdmin')),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE app_users (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    role_id uuid NOT NULL REFERENCES roles(id) ON DELETE RESTRICT,
    first_name varchar(50) NOT NULL CHECK (length(btrim(first_name)) > 0),
    last_name varchar(50) NOT NULL CHECK (length(btrim(last_name)) > 0),
    display_name text GENERATED ALWAYS AS (first_name || ' ' || last_name) STORED,
    age_at_registration smallint NOT NULL CHECK (age_at_registration BETWEEN 13 AND 120),
    gender varchar(10) NOT NULL DEFAULT '' CHECK (gender IN ('','Male','Female','Others')),
    address varchar(500) NOT NULL CHECK (length(btrim(address)) BETWEEN 8 AND 500),
    email varchar(254) NOT NULL CHECK (email = btrim(email) AND email LIKE '%_@_%'),
    password_hash text NOT NULL CHECK (length(password_hash) > 0),
    is_active boolean NOT NULL DEFAULT true,
    security_stamp uuid NOT NULL DEFAULT gen_random_uuid(),
    failed_login_count integer NOT NULL DEFAULT 0 CHECK (failed_login_count >= 0),
    locked_until timestamptz,
    last_login_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_users_email ON app_users (lower(btrim(email)));
CREATE INDEX ix_users_role ON app_users(role_id);

-- 2. Public catalog. Category is the editable card eyebrow, not a separate subtitle.
CREATE TABLE catalog_categories (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name varchar(60) NOT NULL CHECK (length(btrim(name)) > 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_category_name ON catalog_categories(lower(name));
CREATE TABLE makeup_services (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    category_id uuid NOT NULL REFERENCES catalog_categories(id) ON DELETE RESTRICT,
    name varchar(100) NOT NULL CHECK (length(btrim(name)) > 0),
    description text NOT NULL DEFAULT '',
    price numeric(14,2) NOT NULL CHECK (price > 0 AND price <= 1000000),
    minutes integer NOT NULL CHECK (minutes BETWEEN 1 AND 480),
    group_price numeric(14,2) CHECK (group_price > 0 AND group_price <= price),
    group_minimum_people smallint NOT NULL DEFAULT 4 CHECK (group_minimum_people BETWEEN 2 AND 20),
    visual_word varchar(40) NOT NULL DEFAULT '',
    is_active boolean NOT NULL DEFAULT true,
    is_archived boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (NOT is_archived OR NOT is_active)
);
CREATE TABLE makeup_addons (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    category_id uuid NOT NULL REFERENCES catalog_categories(id) ON DELETE RESTRICT,
    name varchar(100) NOT NULL CHECK (length(btrim(name)) > 0),
    description text NOT NULL DEFAULT '',
    price numeric(14,2) NOT NULL CHECK (price BETWEEN 0 AND 1000000),
    minutes integer NOT NULL CHECK (minutes BETWEEN 0 AND 480),
    visual_word varchar(40) NOT NULL DEFAULT '',
    is_active boolean NOT NULL DEFAULT true,
    is_archived boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (NOT is_archived OR NOT is_active)
);
CREATE INDEX ix_services_category ON makeup_services(category_id);
CREATE INDEX ix_addons_category ON makeup_addons(category_id);
CREATE INDEX ix_services_visible ON makeup_services(name) WHERE is_active AND NOT is_archived;
CREATE INDEX ix_addons_visible ON makeup_addons(name) WHERE is_active AND NOT is_archived;

-- No image bytes, host-specific absolute paths or expiring signed URLs in the DB.
CREATE TABLE media_assets (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    object_key text NOT NULL UNIQUE CHECK (length(btrim(object_key)) > 0),
    purpose varchar(20) NOT NULL CHECK (purpose IN ('catalog','payment_qr','payment_proof')),
    content_type varchar(20) NOT NULL CHECK (content_type IN ('image/png','image/jpeg')),
    byte_length integer NOT NULL CHECK (byte_length BETWEEN 1 AND 5000000),
    original_filename text,
    uploaded_by uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_media_uploader ON media_assets(uploaded_by);
CREATE TABLE catalog_images (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    service_id uuid REFERENCES makeup_services(id) ON DELETE CASCADE,
    addon_id uuid REFERENCES makeup_addons(id) ON DELETE CASCADE,
    slot varchar(10) NOT NULL CHECK (slot IN ('card','preview')),
    asset_id uuid NOT NULL REFERENCES media_assets(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK ((service_id IS NOT NULL)::integer + (addon_id IS NOT NULL)::integer = 1),
    UNIQUE(service_id,slot), UNIQUE(addon_id,slot)
);
CREATE INDEX ix_catalog_images_asset ON catalog_images(asset_id);

-- 3. Business settings. Opening/closing are integer hours to preserve 24:00 closing.
CREATE TABLE business_settings (
    id smallint PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    business_name varchar(100) NOT NULL DEFAULT 'Glam by Mariya',
    time_zone text NOT NULL DEFAULT 'Asia/Manila',
    open_hour smallint NOT NULL DEFAULT 8 CHECK (open_hour BETWEEN 0 AND 23),
    close_hour smallint NOT NULL DEFAULT 18 CHECK (close_hour BETWEEN 1 AND 24),
    buffer_minutes integer NOT NULL DEFAULT 45 CHECK (buffer_minutes BETWEEN 0 AND 180),
    travel_fee_informational numeric(14,2) NOT NULL DEFAULT 250 CHECK (travel_fee_informational >= 0),
    down_payment_percent numeric(5,2) NOT NULL DEFAULT 50 CHECK (down_payment_percent BETWEEN 1 AND 100 AND floor(down_payment_percent) = down_payment_percent),
    payment_instructions text NOT NULL DEFAULT '',
    policy_version varchar(40) NOT NULL DEFAULT 'booking-v1',
    travel_reminder_version varchar(40) NOT NULL DEFAULT 'travel-v1',
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (open_hour < close_hour)
);
CREATE TABLE service_areas (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name varchar(100) NOT NULL CHECK (length(btrim(name)) > 0),
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_areas_name ON service_areas(lower(name));
CREATE TABLE payment_methods (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name varchar(30) NOT NULL UNIQUE CHECK (name IN ('GCash','Maya','Bank Transfer','Cash')),
    accepts_deposits boolean NOT NULL,
    is_active boolean NOT NULL DEFAULT true,
    qr_asset_id uuid REFERENCES media_assets(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (accepts_deposits = (name <> 'Cash')),
    CHECK (name <> 'Cash' OR qr_asset_id IS NULL)
);
CREATE INDEX ix_method_qr ON payment_methods(qr_asset_id);

-- 4. Bookings: one lead customer, one service, 1--20 people, optional unique extras.
-- Group packages are a booking mode + the service's configured rate/threshold.
-- There is no separately sold package catalog or per-guest form in the actual UI.
CREATE TABLE bookings (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    reference varchar(12) GENERATED ALWAYS AS ('GBM-' || upper(substr(replace(id::text,'-',''),1,8))) STORED UNIQUE,
    customer_id uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
    artist_id uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
    customer_name_snapshot varchar(101) NOT NULL,
    service_id uuid NOT NULL REFERENCES makeup_services(id) ON DELETE RESTRICT,
    service_name_snapshot varchar(100) NOT NULL,
    regular_price_snapshot numeric(14,2) NOT NULL CHECK (regular_price_snapshot > 0),
    service_minutes_snapshot integer NOT NULL CHECK (service_minutes_snapshot BETWEEN 1 AND 480),
    group_price_snapshot numeric(14,2),
    group_minimum_snapshot smallint NOT NULL DEFAULT 4 CHECK (group_minimum_snapshot BETWEEN 2 AND 20),
    booking_type varchar(10) NOT NULL DEFAULT 'individual' CHECK (booking_type IN ('individual','package')),
    people smallint NOT NULL DEFAULT 1 CHECK (people BETWEEN 1 AND 20),
    applied_price_per_person numeric(14,2) NOT NULL CHECK (applied_price_per_person > 0),
    -- These two cross-row aggregates are fixed at creation and validated at COMMIT.
    addons_total_snapshot numeric(14,2) NOT NULL DEFAULT 0 CHECK (addons_total_snapshot >= 0),
    addons_minutes_snapshot integer NOT NULL DEFAULT 0 CHECK (addons_minutes_snapshot >= 0),
    original_total numeric(14,2) GENERATED ALWAYS AS (regular_price_snapshot * people) STORED,
    group_discount numeric(14,2) GENERATED ALWAYS AS ((regular_price_snapshot - applied_price_per_person) * people) STORED,
    final_total numeric(14,2) GENERATED ALWAYS AS (applied_price_per_person * people + addons_total_snapshot) STORED,
    -- Down payment policy is snapshot at creation and stays immutable afterwards.
    down_payment_percent numeric(5,2) NOT NULL DEFAULT 50 CHECK (down_payment_percent BETWEEN 1 AND 100),
    required_down_payment numeric(14,2) NOT NULL DEFAULT 0 CHECK (required_down_payment >= 0),
    balance_after_deposit numeric(14,2) NOT NULL DEFAULT 0 CHECK (balance_after_deposit >= 0),
    duration_minutes integer GENERATED ALWAYS AS (service_minutes_snapshot * people + addons_minutes_snapshot) STORED,
    starts_at timestamptz NOT NULL,
    ends_at timestamptz NOT NULL,
    reserved_until timestamptz NOT NULL,
    buffer_minutes_snapshot integer NOT NULL CHECK (buffer_minutes_snapshot BETWEEN 0 AND 180),
    time_zone_snapshot text NOT NULL DEFAULT 'Asia/Manila',
    address varchar(500) NOT NULL CHECK (length(btrim(address)) BETWEEN 8 AND 500),
    area_id uuid NOT NULL REFERENCES service_areas(id) ON DELETE RESTRICT,
    area_name_snapshot varchar(100) NOT NULL,
    travel_fee_informational_snapshot numeric(14,2) NOT NULL DEFAULT 0 CHECK (travel_fee_informational_snapshot >= 0),
    policy_accepted_at timestamptz NOT NULL,
    policy_version varchar(40) NOT NULL,
    travel_reminder_acknowledged_at timestamptz NOT NULL,
    travel_reminder_version varchar(40) NOT NULL,
    status varchar(20) NOT NULL DEFAULT 'Pending Payment'
        CHECK (status IN ('Pending Payment','Confirmed','Completed','Cancelled','Rejected','No-show','Incomplete')),
    note text NOT NULL DEFAULT '',
    reviewed_by uuid REFERENCES app_users(id) ON DELETE RESTRICT,
    reviewed_at timestamptz,
    cancellation_confirmed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (booking_type <> 'individual' OR people = 1),
    CHECK (booking_type <> 'package' OR group_price_snapshot IS NOT NULL),
    CHECK (group_price_snapshot IS NULL OR (group_price_snapshot > 0 AND group_price_snapshot <= regular_price_snapshot)),
    CHECK (applied_price_per_person = CASE WHEN booking_type = 'package' AND people >= group_minimum_snapshot
        THEN group_price_snapshot ELSE regular_price_snapshot END),
    CHECK (service_minutes_snapshot * people + addons_minutes_snapshot BETWEEN 1 AND 1440),
    CHECK (ends_at = starts_at + make_interval(mins => service_minutes_snapshot * people + addons_minutes_snapshot)),
    CHECK (reserved_until = ends_at + make_interval(mins => buffer_minutes_snapshot)),
    CHECK ((reviewed_by IS NULL) = (reviewed_at IS NULL))
);
CREATE INDEX ix_bookings_customer_history ON bookings(customer_id, starts_at DESC);
CREATE INDEX ix_bookings_admin_status ON bookings(status, starts_at);
CREATE INDEX ix_bookings_artist ON bookings(artist_id, starts_at);
CREATE INDEX ix_bookings_service ON bookings(service_id);
CREATE INDEX ix_bookings_area ON bookings(area_id);
CREATE INDEX ix_bookings_reviewer ON bookings(reviewed_by);
CREATE TABLE booking_addons (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id uuid NOT NULL REFERENCES bookings(id) ON DELETE RESTRICT,
    addon_id uuid NOT NULL REFERENCES makeup_addons(id) ON DELETE RESTRICT,
    name_snapshot varchar(100) NOT NULL,
    price_snapshot numeric(14,2) NOT NULL CHECK (price_snapshot >= 0),
    minutes_snapshot integer NOT NULL CHECK (minutes_snapshot BETWEEN 0 AND 480),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(booking_id,addon_id)
);
CREATE INDEX ix_booking_addons_catalog ON booking_addons(addon_id);

-- A unified physical calendar enforces conflicts between appointments AND blocks.
-- Only Confirmed bookings have an appointment row. Pending requests hold no time.
CREATE TABLE artist_schedule_entries (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    artist_id uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
    booking_id uuid UNIQUE REFERENCES bookings(id) ON DELETE RESTRICT,
    kind varchar(15) NOT NULL CHECK (kind IN ('appointment','block')),
    starts_at timestamptz NOT NULL,
    ends_at timestamptz NOT NULL,
    reason text NOT NULL DEFAULT '',
    created_by uuid REFERENCES app_users(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (starts_at < ends_at),
    CHECK ((kind = 'appointment' AND booking_id IS NOT NULL) OR
           (kind = 'block' AND booking_id IS NULL AND length(btrim(reason)) > 0)),
    CONSTRAINT no_artist_schedule_overlap EXCLUDE USING gist
        (artist_id WITH =, tstzrange(starts_at,ends_at,'[)') WITH &&)
        WHERE (kind = 'appointment')
);
-- Blocks may overlap one another (the demo allows that). A second exclusion
-- constraint would disallow overlapping blocks; instead block writes lock the
-- artist and check appointments, as do all booking schedule writes below.
CREATE INDEX ix_schedule_range ON artist_schedule_entries USING gist (artist_id, tstzrange(starts_at,ends_at,'[)'));
CREATE INDEX ix_schedule_creator ON artist_schedule_entries(created_by);

-- 5. Manual payment ledger, revisions, signed corrections, and refund transfers.
CREATE TABLE payments (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id uuid NOT NULL REFERENCES bookings(id) ON DELETE RESTRICT,
    method_id uuid NOT NULL REFERENCES payment_methods(id) ON DELETE RESTRICT,
    purpose varchar(15) NOT NULL CHECK (purpose IN ('down_payment','balance')),
    amount numeric(14,2) NOT NULL CHECK (amount > 0),
    reference varchar(150) NOT NULL CHECK (length(btrim(reference)) > 0),
    proof_asset_id uuid REFERENCES media_assets(id) ON DELETE RESTRICT,
    status varchar(20) NOT NULL DEFAULT 'Pending Verification'
        CHECK (status IN ('Pending Verification','Correction Requested','Verified','Rejected')),
    review_note text NOT NULL DEFAULT '',
    submitted_by uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
    verified_by uuid REFERENCES app_users(id) ON DELETE RESTRICT,
    verified_at timestamptz,
    idempotency_key uuid NOT NULL DEFAULT gen_random_uuid() UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK ((status = 'Verified' AND verified_by IS NOT NULL AND verified_at IS NOT NULL)
        OR (status <> 'Verified' AND verified_by IS NULL AND verified_at IS NULL)),
    CHECK (status NOT IN ('Correction Requested','Rejected') OR length(btrim(review_note)) > 0)
);
CREATE UNIQUE INDEX ux_payment_reference ON payments(method_id,lower(btrim(reference)));
CREATE UNIQUE INDEX ux_booking_deposit ON payments(booking_id) WHERE purpose = 'down_payment';
CREATE UNIQUE INDEX ux_booking_balance ON payments(booking_id) WHERE purpose = 'balance';
CREATE INDEX ix_payments_review ON payments(status,created_at);
CREATE INDEX ix_payments_method ON payments(method_id);
CREATE INDEX ix_payments_proof ON payments(proof_asset_id);
CREATE INDEX ix_payments_submitter ON payments(submitted_by);
CREATE INDEX ix_payments_verifier ON payments(verified_by);
CREATE TABLE payment_revisions (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    payment_id uuid NOT NULL REFERENCES payments(id) ON DELETE RESTRICT,
    old_reference varchar(150) NOT NULL,
    new_reference varchar(150) NOT NULL,
    old_proof_asset_id uuid REFERENCES media_assets(id) ON DELETE RESTRICT,
    new_proof_asset_id uuid REFERENCES media_assets(id) ON DELETE RESTRICT,
    old_status varchar(20) NOT NULL,
    new_status varchar(20) NOT NULL,
    review_note text NOT NULL DEFAULT '',
    actor_id uuid REFERENCES app_users(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_payment_revisions_payment ON payment_revisions(payment_id,created_at);
CREATE INDEX ix_payment_revisions_actor ON payment_revisions(actor_id);
CREATE INDEX ix_payment_revisions_old_proof ON payment_revisions(old_proof_asset_id);
CREATE INDEX ix_payment_revisions_new_proof ON payment_revisions(new_proof_asset_id);
CREATE TABLE payment_adjustments (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id uuid NOT NULL REFERENCES bookings(id) ON DELETE RESTRICT,
    payment_id uuid NOT NULL REFERENCES payments(id) ON DELETE RESTRICT,
    amount numeric(14,2) NOT NULL CHECK (amount <> 0),
    reason text NOT NULL CHECK (length(btrim(reason)) > 0),
    recorded_by uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
    idempotency_key uuid NOT NULL DEFAULT gen_random_uuid() UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_adjustments_booking ON payment_adjustments(booking_id);
CREATE INDEX ix_adjustments_payment ON payment_adjustments(payment_id);
CREATE INDEX ix_adjustments_actor ON payment_adjustments(recorded_by);
CREATE TABLE booking_closures (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id uuid NOT NULL UNIQUE REFERENCES bookings(id) ON DELETE RESTRICT,
    kind varchar(15) NOT NULL CHECK (kind IN ('cancellation','rejection')),
    reason text NOT NULL,
    verified_paid_snapshot numeric(14,2) NOT NULL CHECK (verified_paid_snapshot >= 0),
    cancellation_fee numeric(14,2) NOT NULL,
    refund_due numeric(14,2) GENERATED ALWAYS AS (greatest(0::numeric,verified_paid_snapshot - cancellation_fee)) STORED,
    actor_id uuid REFERENCES app_users(id) ON DELETE RESTRICT,
    confirmed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK ((kind = 'cancellation' AND cancellation_fee = 200 AND confirmed_at IS NOT NULL)
        OR (kind = 'rejection' AND cancellation_fee = 0 AND length(btrim(reason)) > 0))
);
CREATE INDEX ix_closures_actor ON booking_closures(actor_id);
CREATE TABLE refunds (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    closure_id uuid NOT NULL REFERENCES booking_closures(id) ON DELETE RESTRICT,
    method_id uuid NOT NULL REFERENCES payment_methods(id) ON DELETE RESTRICT,
    amount numeric(14,2) NOT NULL CHECK (amount > 0),
    transfer_reference varchar(150) NOT NULL CHECK (length(btrim(transfer_reference)) > 0),
    sent_at timestamptz NOT NULL,
    recorded_by uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
    idempotency_key uuid NOT NULL DEFAULT gen_random_uuid() UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_refund_transfer ON refunds(method_id,lower(btrim(transfer_reference)));
CREATE INDEX ix_refunds_closure ON refunds(closure_id);
CREATE INDEX ix_refunds_actor ON refunds(recorded_by);

-- 6. Workflow history, notifications/reminder outbox, and sanitized audit events.
CREATE TABLE reschedule_requests (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id uuid NOT NULL REFERENCES bookings(id) ON DELETE RESTRICT,
    requested_by uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
    original_start timestamptz NOT NULL,
    requested_start timestamptz NOT NULL,
    status varchar(15) NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending','Approved','Declined','Superseded','Withdrawn')),
    reason text NOT NULL DEFAULT '',
    reviewed_by uuid REFERENCES app_users(id) ON DELETE RESTRICT,
    reviewed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (original_start <> requested_start),
    CHECK ((reviewed_by IS NULL) = (reviewed_at IS NULL)),
    CHECK (status NOT IN ('Approved','Declined') OR reviewed_by IS NOT NULL),
    CHECK (status <> 'Declined' OR length(btrim(reason)) > 0)
);
CREATE UNIQUE INDEX ux_pending_reschedule ON reschedule_requests(booking_id) WHERE status = 'Pending';
CREATE INDEX ix_reschedule_history ON reschedule_requests(booking_id,created_at DESC);
CREATE INDEX ix_reschedule_requester ON reschedule_requests(requested_by);
CREATE INDEX ix_reschedule_reviewer ON reschedule_requests(reviewed_by);
CREATE TABLE booking_status_history (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id uuid NOT NULL REFERENCES bookings(id) ON DELETE RESTRICT,
    old_status varchar(20),
    new_status varchar(20) NOT NULL,
    note text NOT NULL DEFAULT '',
    actor_id uuid REFERENCES app_users(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_status_history ON booking_status_history(booking_id,created_at DESC);
CREATE INDEX ix_status_actor ON booking_status_history(actor_id);
CREATE TABLE notifications (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
    booking_id uuid REFERENCES bookings(id) ON DELETE RESTRICT,
    kind varchar(20) NOT NULL DEFAULT 'booking' CHECK (kind IN ('booking','payment','reschedule','cancellation','reminder','account')),
    channel varchar(10) NOT NULL DEFAULT 'in_app' CHECK (channel IN ('in_app','email','sms')),
    message text NOT NULL CHECK (length(btrim(message)) > 0),
    scheduled_at timestamptz NOT NULL DEFAULT now(),
    delivery_status varchar(10) NOT NULL DEFAULT 'Pending' CHECK (delivery_status IN ('Pending','Sent','Failed','Cancelled')),
    sent_at timestamptz,
    read_at timestamptz,
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    last_error text,
    deduplication_key text UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (delivery_status <> 'Sent' OR sent_at IS NOT NULL)
);
CREATE INDEX ix_notifications_inbox ON notifications(user_id,created_at DESC);
CREATE INDEX ix_notifications_booking ON notifications(booking_id);
CREATE INDEX ix_notifications_due ON notifications(scheduled_at) WHERE delivery_status = 'Pending';
CREATE TABLE audit_logs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    actor_id uuid REFERENCES app_users(id) ON DELETE RESTRICT,
    booking_id uuid REFERENCES bookings(id) ON DELETE RESTRICT,
    entity_type text NOT NULL,
    entity_id uuid,
    event text NOT NULL,
    details jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(details) = 'object'),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_audit_actor ON audit_logs(actor_id,created_at DESC);
CREATE INDEX ix_audit_booking ON audit_logs(booking_id,created_at DESC);
CREATE INDEX ix_audit_recent ON audit_logs(created_at DESC);
CREATE INDEX ix_audit_entity ON audit_logs(entity_type,entity_id);

-- 7. Timestamp and integrity helpers. SECURITY INVOKER: no privilege escalation.
CREATE FUNCTION touch_updated_at() RETURNS trigger LANGUAGE plpgsql SET search_path = makeup, public AS $$
BEGIN NEW.created_at := OLD.created_at; NEW.updated_at := clock_timestamp(); RETURN NEW; END $$;
CREATE FUNCTION current_actor() RETURNS uuid LANGUAGE sql STABLE AS $$
    SELECT nullif(current_setting('makeup.actor_id',true),'')::uuid
$$;
CREATE FUNCTION assert_role(p_user uuid, p_role text) RETURNS void LANGUAGE plpgsql SET search_path = makeup, public AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM app_users u JOIN roles r ON r.id=u.role_id
                   WHERE u.id=p_user AND r.name=p_role AND u.is_active) THEN
        RAISE EXCEPTION 'An active % account is required', p_role;
    END IF;
END $$;
CREATE FUNCTION verified_paid(p_booking uuid) RETURNS numeric LANGUAGE sql STABLE SET search_path = makeup, public AS $$
    SELECT coalesce((SELECT sum(amount) FROM payments WHERE booking_id=p_booking AND status='Verified'),0)
         + coalesce((SELECT sum(amount) FROM payment_adjustments WHERE booking_id=p_booking),0)
$$;
CREATE FUNCTION check_slot(p_artist uuid, p_start timestamptz, p_duration integer, p_buffer integer, p_exclude uuid DEFAULT NULL)
RETURNS void LANGUAGE plpgsql SET search_path = makeup, public AS $$
DECLARE s business_settings%ROWTYPE; local_start timestamp; local_end timestamp; finish timestamptz;
BEGIN
    -- Serializes block/appointment changes across processes. Always lock booking
    -- before artist in workflows; SERIALIZABLE transactions must retry 40001.
    PERFORM 1 FROM app_users WHERE id=p_artist FOR UPDATE;
    PERFORM assert_role(p_artist,'Admin');
    SELECT * INTO STRICT s FROM business_settings WHERE id=1;
    finish := p_start + make_interval(mins => p_duration+p_buffer);
    local_start := p_start AT TIME ZONE s.time_zone;
    local_end := finish AT TIME ZONE s.time_zone;
    IF p_start <= clock_timestamp() OR p_duration NOT BETWEEN 1 AND 1440
       OR local_start < date_trunc('day',local_start) + make_interval(hours => s.open_hour)
       OR local_end > date_trunc('day',local_start) + make_interval(hours => s.close_hour) THEN
        RAISE EXCEPTION 'Appointment must be future and fit service hours including travel buffer';
    END IF;
    IF EXISTS (SELECT 1 FROM artist_schedule_entries e WHERE e.artist_id=p_artist
               AND (e.booking_id IS NULL OR e.booking_id IS DISTINCT FROM p_exclude)
               AND tstzrange(e.starts_at,e.ends_at,'[)') && tstzrange(p_start,finish,'[)')) THEN
        RAISE EXCEPTION 'Artist is unavailable (appointment or availability block)';
    END IF;
END $$;

CREATE FUNCTION guard_booking() RETURNS trigger LANGUAGE plpgsql SET search_path = makeup, public AS $$
DECLARE svc makeup_services%ROWTYPE; s business_settings%ROWTYPE; area service_areas%ROWTYPE;
BEGIN
    IF TG_OP='INSERT' THEN
        IF NEW.status <> 'Pending Payment' THEN RAISE EXCEPTION 'New bookings must await payment review'; END IF;
        PERFORM assert_role(NEW.customer_id,'Customer');
        SELECT * INTO STRICT svc FROM makeup_services WHERE id=NEW.service_id AND is_active AND NOT is_archived;
        SELECT * INTO STRICT s FROM business_settings WHERE id=1;
        SELECT * INTO STRICT area FROM service_areas WHERE id=NEW.area_id AND is_active;
        -- Capture trusted catalog/profile/settings data, never client-submitted rates.
        NEW.customer_name_snapshot := (SELECT display_name FROM app_users WHERE id=NEW.customer_id);
        NEW.service_name_snapshot := svc.name; NEW.regular_price_snapshot := svc.price;
        NEW.service_minutes_snapshot := svc.minutes; NEW.group_price_snapshot := svc.group_price;
        NEW.group_minimum_snapshot := svc.group_minimum_people;
        NEW.applied_price_per_person := CASE WHEN NEW.booking_type='package' AND NEW.people>=svc.group_minimum_people
            THEN svc.group_price ELSE svc.price END;
        NEW.area_name_snapshot := area.name; NEW.time_zone_snapshot := s.time_zone;
        NEW.buffer_minutes_snapshot := s.buffer_minutes;
        NEW.travel_fee_informational_snapshot := 0;
        NEW.down_payment_percent := s.down_payment_percent;
        NEW.required_down_payment := round((NEW.applied_price_per_person*NEW.people+NEW.addons_total_snapshot) * s.down_payment_percent / 100.0, 2);
        NEW.balance_after_deposit := NEW.applied_price_per_person*NEW.people+NEW.addons_total_snapshot - NEW.required_down_payment;
        IF NEW.policy_version <> s.policy_version OR NEW.travel_reminder_version <> s.travel_reminder_version THEN
            RAISE EXCEPTION 'Acknowledge the current booking policies and travel reminder';
        END IF;
    ELSE
        IF ROW(NEW.customer_id,NEW.artist_id,NEW.customer_name_snapshot,NEW.service_id,NEW.service_name_snapshot,
               NEW.regular_price_snapshot,NEW.service_minutes_snapshot,NEW.group_price_snapshot,NEW.group_minimum_snapshot,
               NEW.booking_type,NEW.people,NEW.applied_price_per_person,NEW.addons_total_snapshot,NEW.addons_minutes_snapshot,
               NEW.down_payment_percent,NEW.required_down_payment,NEW.balance_after_deposit,
               NEW.buffer_minutes_snapshot,NEW.time_zone_snapshot,NEW.address,NEW.area_id,NEW.area_name_snapshot,
               NEW.travel_fee_informational_snapshot,NEW.policy_accepted_at,NEW.policy_version,
               NEW.travel_reminder_acknowledged_at,NEW.travel_reminder_version)
           IS DISTINCT FROM
           ROW(OLD.customer_id,OLD.artist_id,OLD.customer_name_snapshot,OLD.service_id,OLD.service_name_snapshot,
               OLD.regular_price_snapshot,OLD.service_minutes_snapshot,OLD.group_price_snapshot,OLD.group_minimum_snapshot,
               OLD.booking_type,OLD.people,OLD.applied_price_per_person,OLD.addons_total_snapshot,OLD.addons_minutes_snapshot,
               OLD.down_payment_percent,OLD.required_down_payment,OLD.balance_after_deposit,
               OLD.buffer_minutes_snapshot,OLD.time_zone_snapshot,OLD.address,OLD.area_id,OLD.area_name_snapshot,
               OLD.travel_fee_informational_snapshot,OLD.policy_accepted_at,OLD.policy_version,
               OLD.travel_reminder_acknowledged_at,OLD.travel_reminder_version) THEN
            RAISE EXCEPTION 'Booked customer, location, consent, duration and price snapshots are immutable';
        END IF;
        IF NEW.status IS DISTINCT FROM OLD.status THEN
            IF NOT ((OLD.status='Pending Payment' AND NEW.status IN ('Confirmed','Rejected')) OR
                    (OLD.status='Confirmed' AND NEW.status IN ('Cancelled','Completed','No-show','Incomplete'))) THEN
                RAISE EXCEPTION 'Invalid booking status transition';
            END IF;
            IF NEW.status='Cancelled' AND (OLD.starts_at<=clock_timestamp() OR NEW.cancellation_confirmed_at IS NULL) THEN
                RAISE EXCEPTION 'Only explicitly confirmed future cancellations are allowed';
            END IF;
            IF NEW.status='Completed' AND OLD.ends_at>clock_timestamp() THEN RAISE EXCEPTION 'Appointment has not ended'; END IF;
            IF NEW.status IN ('No-show','Incomplete') AND OLD.starts_at>clock_timestamp() THEN RAISE EXCEPTION 'Appointment has not started'; END IF;
            IF NEW.status IN ('Rejected','No-show','Incomplete') AND length(btrim(NEW.note))=0 THEN RAISE EXCEPTION 'A reason is required'; END IF;
        END IF;
        IF NEW.starts_at IS DISTINCT FROM OLD.starts_at AND NOT EXISTS
            (SELECT 1 FROM reschedule_requests WHERE booking_id=OLD.id AND status='Approved'
             AND original_start=OLD.starts_at AND requested_start=NEW.starts_at) THEN
            RAISE EXCEPTION 'An approved reschedule request is required';
        END IF;
    END IF;
    NEW.ends_at := NEW.starts_at + make_interval(mins => NEW.service_minutes_snapshot*NEW.people+NEW.addons_minutes_snapshot);
    NEW.reserved_until := NEW.ends_at + make_interval(mins => NEW.buffer_minutes_snapshot);
    IF TG_OP='INSERT' OR NEW.starts_at IS DISTINCT FROM OLD.starts_at OR
       (NEW.status='Confirmed' AND OLD.status IS DISTINCT FROM NEW.status) THEN
        IF NOT EXISTS (SELECT 1 FROM service_areas WHERE id=NEW.area_id AND is_active) THEN RAISE EXCEPTION 'Service area is inactive'; END IF;
        PERFORM check_slot(NEW.artist_id,NEW.starts_at,NEW.service_minutes_snapshot*NEW.people+NEW.addons_minutes_snapshot,NEW.buffer_minutes_snapshot,NEW.id);
    END IF;
    IF NEW.reviewed_by IS NOT NULL THEN PERFORM assert_role(NEW.reviewed_by,'Admin'); END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER booking_guard BEFORE INSERT OR UPDATE ON bookings FOR EACH ROW EXECUTE FUNCTION guard_booking();

CREATE FUNCTION guard_schedule_entry() RETURNS trigger LANGUAGE plpgsql SET search_path = makeup, public AS $$
BEGIN
    IF TG_OP='DELETE' THEN
        IF OLD.kind='appointment' AND EXISTS (SELECT 1 FROM bookings WHERE id=OLD.booking_id AND status='Confirmed') THEN
            RAISE EXCEPTION 'Release the reservation by changing the booking, not deleting the calendar entry';
        END IF;
        RETURN OLD;
    END IF;
    PERFORM 1 FROM app_users WHERE id=NEW.artist_id FOR UPDATE;
    PERFORM assert_role(NEW.artist_id,'Admin');
    IF TG_OP='UPDATE' AND ROW(NEW.artist_id,NEW.booking_id,NEW.kind) IS DISTINCT FROM ROW(OLD.artist_id,OLD.booking_id,OLD.kind) THEN
        RAISE EXCEPTION 'Calendar entry ownership and kind cannot change';
    END IF;
    IF NEW.kind='appointment' AND NOT EXISTS (SELECT 1 FROM bookings b WHERE b.id=NEW.booking_id
        AND b.status='Confirmed' AND b.artist_id=NEW.artist_id AND b.starts_at=NEW.starts_at AND b.reserved_until=NEW.ends_at) THEN
        RAISE EXCEPTION 'Appointment entry must match its confirmed booking';
    END IF;
    IF EXISTS (SELECT 1 FROM artist_schedule_entries e WHERE e.artist_id=NEW.artist_id AND e.id<>NEW.id
        AND (NEW.booking_id IS NULL OR e.booking_id IS DISTINCT FROM NEW.booking_id)
        AND (NEW.kind='appointment' OR e.kind='appointment')
        AND tstzrange(e.starts_at,e.ends_at,'[)') && tstzrange(NEW.starts_at,NEW.ends_at,'[)')) THEN
        RAISE EXCEPTION 'Calendar entry overlaps an appointment or block';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER schedule_guard BEFORE INSERT OR UPDATE OR DELETE ON artist_schedule_entries FOR EACH ROW EXECUTE FUNCTION guard_schedule_entry();

CREATE FUNCTION sync_booking() RETURNS trigger LANGUAGE plpgsql SET search_path = makeup, public AS $$
BEGIN
    IF NEW.status='Confirmed' THEN
        INSERT INTO artist_schedule_entries(artist_id,booking_id,kind,starts_at,ends_at,created_by)
        VALUES(NEW.artist_id,NEW.id,'appointment',NEW.starts_at,NEW.reserved_until,current_actor())
        ON CONFLICT (booking_id) DO UPDATE SET starts_at=excluded.starts_at,ends_at=excluded.ends_at;
    ELSE
        DELETE FROM artist_schedule_entries WHERE booking_id=NEW.id;
    END IF;
    IF TG_OP='INSERT' OR NEW.status IS DISTINCT FROM OLD.status THEN
        INSERT INTO booking_status_history(booking_id,old_status,new_status,note,actor_id)
        VALUES(NEW.id,CASE WHEN TG_OP='UPDATE' THEN OLD.status END,NEW.status,NEW.note,current_actor());
        INSERT INTO notifications(user_id,booking_id,message,delivery_status,sent_at)
        SELECT u.id,NEW.id,NEW.reference || ': ' || NEW.status || '. ' || NEW.note,'Sent',now()
        FROM app_users u JOIN roles r ON r.id=u.role_id WHERE u.id=NEW.customer_id OR (r.name='Admin' AND u.is_active);
        IF NEW.status IN ('Cancelled','Rejected') THEN
            INSERT INTO booking_closures(booking_id,kind,reason,verified_paid_snapshot,cancellation_fee,actor_id,confirmed_at)
            VALUES(NEW.id,CASE WHEN NEW.status='Cancelled' THEN 'cancellation' ELSE 'rejection' END,
                NEW.note,verified_paid(NEW.id),CASE WHEN NEW.status='Cancelled' THEN 200 ELSE 0 END,
                current_actor(),NEW.cancellation_confirmed_at);
            UPDATE reschedule_requests SET status='Withdrawn' WHERE booking_id=NEW.id AND status='Pending';
            UPDATE notifications SET delivery_status='Cancelled' WHERE booking_id=NEW.id AND kind='reminder' AND delivery_status='Pending';
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER booking_sync AFTER INSERT OR UPDATE ON bookings FOR EACH ROW EXECUTE FUNCTION sync_booking();

CREATE FUNCTION guard_addon_snapshot() RETURNS trigger LANGUAGE plpgsql SET search_path = makeup, public AS $$
DECLARE a makeup_addons%ROWTYPE;
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'Booking add-on snapshots cannot be edited or deleted'; END IF;
    PERFORM 1 FROM bookings WHERE id=NEW.booking_id FOR UPDATE;
    IF EXISTS (SELECT 1 FROM payments WHERE booking_id=NEW.booking_id) THEN RAISE EXCEPTION 'Add extras before submitting payment'; END IF;
    SELECT * INTO STRICT a FROM makeup_addons WHERE id=NEW.addon_id AND is_active AND NOT is_archived;
    NEW.name_snapshot:=a.name; NEW.price_snapshot:=a.price; NEW.minutes_snapshot:=a.minutes;
    RETURN NEW;
END $$;
CREATE TRIGGER addon_snapshot_guard BEFORE INSERT OR UPDATE OR DELETE ON booking_addons FOR EACH ROW EXECUTE FUNCTION guard_addon_snapshot();

CREATE FUNCTION guard_payment() RETURNS trigger LANGUAGE plpgsql SET search_path = makeup, public AS $$
DECLARE b bookings%ROWTYPE; m payment_methods%ROWTYPE;
BEGIN
    SELECT * INTO STRICT b FROM bookings WHERE id=NEW.booking_id FOR UPDATE;
    SELECT * INTO STRICT m FROM payment_methods WHERE id=NEW.method_id;
    IF TG_OP='UPDATE' THEN
        IF OLD.status='Verified' THEN RAISE EXCEPTION 'Verified payments are immutable; use an adjustment'; END IF;
        IF ROW(NEW.booking_id,NEW.method_id,NEW.amount,NEW.purpose,NEW.submitted_by,NEW.idempotency_key)
            IS DISTINCT FROM ROW(OLD.booking_id,OLD.method_id,OLD.amount,OLD.purpose,OLD.submitted_by,OLD.idempotency_key) THEN
            RAISE EXCEPTION 'Payment ownership, amount and method cannot change';
        END IF;
    END IF;
    IF b.status IN ('Cancelled','Rejected','Completed','No-show','Incomplete') THEN RAISE EXCEPTION 'Booking is closed'; END IF;
    IF NEW.purpose='down_payment' AND (NOT m.accepts_deposits OR NEW.amount<>b.required_down_payment OR b.status<>'Pending Payment') THEN
        RAISE EXCEPTION 'Deposit must equal the exact recorded down payment (%) and use GCash, Maya or Bank Transfer', b.required_down_payment;
    END IF;
    IF TG_OP='INSERT' AND NOT m.is_active THEN RAISE EXCEPTION 'Inactive payment method'; END IF;
    IF NEW.purpose='balance' AND (b.status<>'Confirmed' OR b.ends_at>clock_timestamp() OR NEW.amount<>b.final_total-verified_paid(b.id)
        OR NEW.status<>'Verified' OR m.name<>'Cash') THEN RAISE EXCEPTION 'Record the exact final Cash balance after the appointment'; END IF;
    IF NEW.status='Verified' THEN PERFORM assert_role(NEW.verified_by,'Admin'); END IF;
    IF NEW.proof_asset_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM media_assets WHERE id=NEW.proof_asset_id AND purpose='payment_proof') THEN
        RAISE EXCEPTION 'Proof must reference a private payment-proof asset'; END IF;
    IF TG_OP='UPDATE' THEN
        INSERT INTO payment_revisions(payment_id,old_reference,new_reference,old_proof_asset_id,new_proof_asset_id,old_status,new_status,review_note,actor_id)
        VALUES(NEW.id,OLD.reference,NEW.reference,OLD.proof_asset_id,NEW.proof_asset_id,OLD.status,NEW.status,NEW.review_note,current_actor());
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER payment_guard BEFORE INSERT OR UPDATE ON payments FOR EACH ROW EXECUTE FUNCTION guard_payment();

-- Immutable evidence/ledgers: correction entries are additive, never destructive.
CREATE FUNCTION deny_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION '% is append-only',TG_TABLE_NAME; END $$;
CREATE TRIGGER payments_no_delete BEFORE DELETE ON payments FOR EACH ROW EXECUTE FUNCTION deny_mutation();
DO $$ DECLARE t text; BEGIN
    FOREACH t IN ARRAY ARRAY['payment_revisions','payment_adjustments','booking_closures','refunds','booking_status_history','audit_logs'] LOOP
        EXECUTE format('CREATE TRIGGER immutable_rows BEFORE UPDATE OR DELETE ON makeup.%I FOR EACH ROW EXECUTE FUNCTION makeup.deny_mutation()',t);
    END LOOP;
END $$;

CREATE FUNCTION guard_finance_entry() RETURNS trigger LANGUAGE plpgsql SET search_path = makeup, public AS $$
DECLARE b bookings%ROWTYPE; c booking_closures%ROWTYPE;
BEGIN
    IF TG_TABLE_NAME='refunds' THEN
        SELECT * INTO STRICT c FROM booking_closures WHERE id=NEW.closure_id FOR UPDATE;
        PERFORM assert_role(NEW.recorded_by,'Admin');
        IF NEW.amount + coalesce((SELECT sum(amount) FROM refunds WHERE closure_id=c.id),0) > c.refund_due THEN
            RAISE EXCEPTION 'Refund exceeds the remaining obligation'; END IF;
    ELSE
        SELECT * INTO STRICT b FROM bookings WHERE id=NEW.booking_id FOR UPDATE;
        PERFORM assert_role(NEW.recorded_by,'Admin');
        IF b.status IN ('Cancelled','Rejected') THEN RAISE EXCEPTION 'Closure settlement is frozen; do not adjust after cancellation/rejection'; END IF;
        IF NOT EXISTS (SELECT 1 FROM payments WHERE id=NEW.payment_id AND booking_id=b.id AND status='Verified') THEN
            RAISE EXCEPTION 'Adjustment must reference a verified payment on this booking'; END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER refund_guard BEFORE INSERT ON refunds FOR EACH ROW EXECUTE FUNCTION guard_finance_entry();
CREATE TRIGGER adjustment_guard BEFORE INSERT ON payment_adjustments FOR EACH ROW EXECUTE FUNCTION guard_finance_entry();

CREATE FUNCTION guard_closure() RETURNS trigger LANGUAGE plpgsql SET search_path = makeup, public AS $$
DECLARE b bookings%ROWTYPE;
BEGIN
    SELECT * INTO STRICT b FROM bookings WHERE id=NEW.booking_id FOR UPDATE;
    IF NOT ((NEW.kind='cancellation' AND b.status='Cancelled' AND NEW.confirmed_at=b.cancellation_confirmed_at)
         OR (NEW.kind='rejection' AND b.status='Rejected'))
       OR NEW.verified_paid_snapshot<>verified_paid(b.id) THEN
        RAISE EXCEPTION 'Closure must match the booking decision and verified collections'; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER closure_guard BEFORE INSERT ON booking_closures FOR EACH ROW EXECUTE FUNCTION guard_closure();

-- Deferred constraints make verification + confirmation and balance + completion
-- atomic: intermediate states may exist ONLY inside the same transaction.
CREATE FUNCTION validate_booking_finance() RETURNS trigger LANGUAGE plpgsql SET search_path = makeup, public AS $$
DECLARE bid uuid; b bookings%ROWTYPE; paid numeric; n integer;
BEGIN
    IF TG_TABLE_NAME='bookings' THEN bid:=NEW.id; ELSE bid:=NEW.booking_id; END IF;
    SELECT * INTO b FROM bookings WHERE id=bid FOR UPDATE;
    IF NOT FOUND THEN RETURN NULL; END IF;
    IF b.addons_total_snapshot <> coalesce((SELECT sum(price_snapshot) FROM booking_addons WHERE booking_id=bid),0)
       OR b.addons_minutes_snapshot <> coalesce((SELECT sum(minutes_snapshot) FROM booking_addons WHERE booking_id=bid),0) THEN
        RAISE EXCEPTION 'Add-on aggregates do not match immutable line items'; END IF;
    SELECT count(*) INTO n FROM payments WHERE booking_id=bid AND purpose='down_payment';
    IF n<>1 THEN RAISE EXCEPTION 'Every submitted booking requires exactly one down-payment submission'; END IF;
    paid:=verified_paid(bid);
    IF paid<0 OR paid>b.final_total THEN RAISE EXCEPTION 'Verified collections must be between zero and booking total'; END IF;
    IF b.status IN ('Confirmed','Completed','No-show','Incomplete') AND NOT EXISTS
        (SELECT 1 FROM payments WHERE booking_id=bid AND purpose='down_payment' AND status='Verified') THEN
        RAISE EXCEPTION 'Verify the deposit before approval'; END IF;
    IF b.status='Pending Payment' AND paid<>0 THEN RAISE EXCEPTION 'Verify payment and confirm or reject in one transaction'; END IF;
    IF b.status IN ('Confirmed','No-show','Incomplete') AND paid<b.required_down_payment THEN RAISE EXCEPTION 'Verified deposit shortfall'; END IF;
    IF b.status='Completed' AND paid<>b.final_total THEN RAISE EXCEPTION 'Completion requires full verified balance'; END IF;
    IF b.status IN ('Cancelled','Rejected') AND NOT EXISTS
        (SELECT 1 FROM booking_closures WHERE booking_id=bid AND verified_paid_snapshot=paid) THEN
        RAISE EXCEPTION 'Closure must preserve the verified settlement'; END IF;
    RETURN NULL;
END $$;
DO $$ DECLARE t text; BEGIN
    FOREACH t IN ARRAY ARRAY['bookings','booking_addons','payments','payment_adjustments'] LOOP
        EXECUTE format('CREATE CONSTRAINT TRIGGER booking_finance_integrity AFTER INSERT OR UPDATE ON makeup.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION makeup.validate_booking_finance()',t);
    END LOOP;
END $$;

CREATE FUNCTION guard_reschedule() RETURNS trigger LANGUAGE plpgsql SET search_path = makeup, public AS $$
DECLARE b bookings%ROWTYPE;
BEGIN
    SELECT * INTO STRICT b FROM bookings WHERE id=NEW.booking_id FOR UPDATE;
    IF TG_OP='INSERT' THEN
        IF NEW.status<>'Pending' OR NEW.requested_by<>b.customer_id OR b.status<>'Confirmed' OR b.starts_at<=clock_timestamp() THEN
            RAISE EXCEPTION 'Only the owner of a future confirmed booking can request rescheduling'; END IF;
        NEW.original_start:=b.starts_at;
        PERFORM check_slot(b.artist_id,NEW.requested_start,b.duration_minutes,b.buffer_minutes_snapshot,b.id);
    ELSE
        IF OLD.status<>'Pending' OR ROW(NEW.booking_id,NEW.requested_by,NEW.original_start,NEW.requested_start)
            IS DISTINCT FROM ROW(OLD.booking_id,OLD.requested_by,OLD.original_start,OLD.requested_start) THEN
            RAISE EXCEPTION 'Resolved requests are immutable; create a new request'; END IF;
        IF NEW.status='Approved' THEN
            IF b.status<>'Confirmed' OR b.starts_at<=clock_timestamp() OR b.starts_at<>NEW.original_start THEN RAISE EXCEPTION 'Original slot is no longer reschedulable'; END IF;
            PERFORM check_slot(b.artist_id,NEW.requested_start,b.duration_minutes,b.buffer_minutes_snapshot,b.id);
        END IF;
        IF NEW.reviewed_by IS NOT NULL THEN PERFORM assert_role(NEW.reviewed_by,'Admin'); END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER reschedule_guard BEFORE INSERT OR UPDATE ON reschedule_requests FOR EACH ROW EXECUTE FUNCTION guard_reschedule();
CREATE TRIGGER reschedule_no_delete BEFORE DELETE ON reschedule_requests FOR EACH ROW EXECUTE FUNCTION deny_mutation();
CREATE FUNCTION apply_reschedule() RETURNS trigger LANGUAGE plpgsql SET search_path = makeup, public AS $$
BEGIN
    IF NEW.status='Approved' AND OLD.status='Pending' THEN
        UPDATE bookings SET starts_at=NEW.requested_start WHERE id=NEW.booking_id;
        -- Old reminders must be replaced by a future worker, not sent for the old slot.
        UPDATE notifications SET delivery_status='Cancelled' WHERE booking_id=NEW.booking_id AND kind='reminder' AND delivery_status='Pending';
    END IF;
    INSERT INTO notifications(user_id,booking_id,kind,message,delivery_status,sent_at)
    SELECT u.id,b.id,'reschedule',b.reference || ': reschedule ' || NEW.status || '. ' || NEW.reason,'Sent',now()
    FROM bookings b CROSS JOIN app_users u JOIN roles r ON r.id=u.role_id
    WHERE b.id=NEW.booking_id AND (u.id=b.customer_id OR (r.name='Admin' AND u.is_active));
    RETURN NEW;
END $$;
CREATE TRIGGER reschedule_apply AFTER UPDATE ON reschedule_requests FOR EACH ROW EXECUTE FUNCTION apply_reschedule();

-- Typed asset references and timezone validation beyond ordinary FKs.
CREATE FUNCTION guard_asset_usage() RETURNS trigger LANGUAGE plpgsql SET search_path = makeup, public AS $$
DECLARE asset uuid; expected text;
BEGIN
    IF TG_TABLE_NAME='catalog_images' THEN asset:=NEW.asset_id; expected:='catalog';
    ELSE asset:=NEW.qr_asset_id; expected:='payment_qr'; END IF;
    IF asset IS NOT NULL AND NOT EXISTS (SELECT 1 FROM media_assets WHERE id=asset AND purpose=expected) THEN
        RAISE EXCEPTION 'Incorrect asset purpose (expected %)',expected; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER catalog_asset_guard BEFORE INSERT OR UPDATE ON catalog_images FOR EACH ROW EXECUTE FUNCTION guard_asset_usage();
CREATE TRIGGER qr_asset_guard BEFORE INSERT OR UPDATE ON payment_methods FOR EACH ROW EXECUTE FUNCTION guard_asset_usage();
CREATE FUNCTION guard_settings() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_timezone_names WHERE name=NEW.time_zone) THEN RAISE EXCEPTION 'Unknown IANA timezone'; END IF;
    IF NEW.down_payment_percent IS NULL OR NEW.down_payment_percent < 1 OR NEW.down_payment_percent > 100
       OR round(NEW.down_payment_percent,0) <> NEW.down_payment_percent THEN
        RAISE EXCEPTION 'Down payment percentage must be a whole number between 1 and 100'; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER settings_guard BEFORE INSERT OR UPDATE ON business_settings FOR EACH ROW EXECUTE FUNCTION guard_settings();
CREATE FUNCTION guard_media_identity() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF ROW(NEW.object_key,NEW.purpose,NEW.content_type,NEW.byte_length,NEW.uploaded_by)
        IS DISTINCT FROM ROW(OLD.object_key,OLD.purpose,OLD.content_type,OLD.byte_length,OLD.uploaded_by) THEN
        RAISE EXCEPTION 'Asset identity is immutable; upload a new asset and replace the slot reference'; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER media_identity_guard BEFORE UPDATE ON media_assets FOR EACH ROW EXECUTE FUNCTION guard_media_identity();

-- Audit includes event/IDs and status only, not passwords, addresses or proof keys.
CREATE FUNCTION audit_change() RETURNS trigger LANGUAGE plpgsql SET search_path = makeup, public AS $$
DECLARE row_data jsonb; bid uuid; eid uuid;
BEGIN
    IF TG_OP='DELETE' THEN row_data:=to_jsonb(OLD); ELSE row_data:=to_jsonb(NEW); END IF;
    IF TG_TABLE_NAME='business_settings' THEN eid:=NULL; ELSE eid:=(row_data->>'id')::uuid; END IF;
    IF TG_TABLE_NAME='bookings' THEN bid:=eid; ELSE bid:=(row_data->>'booking_id')::uuid; END IF;
    INSERT INTO audit_logs(actor_id,booking_id,entity_type,entity_id,event,details)
    VALUES(current_actor(),bid,TG_TABLE_NAME,eid,TG_OP,jsonb_strip_nulls(jsonb_build_object('status',row_data->>'status')));
    RETURN NULL;
END $$;
DO $$ DECLARE t text; BEGIN
    FOREACH t IN ARRAY ARRAY['roles','app_users','catalog_categories','makeup_services','makeup_addons','media_assets','catalog_images',
        'business_settings','service_areas','payment_methods','bookings','booking_addons','artist_schedule_entries','payments',
        'payment_revisions','payment_adjustments','booking_closures','refunds','reschedule_requests','booking_status_history','notifications','audit_logs'] LOOP
        EXECUTE format('CREATE TRIGGER timestamps BEFORE UPDATE ON makeup.%I FOR EACH ROW EXECUTE FUNCTION makeup.touch_updated_at()',t);
    END LOOP;
    FOREACH t IN ARRAY ARRAY['app_users','makeup_services','makeup_addons','catalog_images','media_assets','business_settings','service_areas',
        'payment_methods','bookings','artist_schedule_entries','payments','payment_adjustments','booking_closures','refunds','reschedule_requests'] LOOP
        EXECUTE format('CREATE TRIGGER audit_event AFTER INSERT OR UPDATE OR DELETE ON makeup.%I FOR EACH ROW EXECUTE FUNCTION makeup.audit_change()',t);
    END LOOP;
END $$;

-- Idempotent manual deposit verification. Exclusion failure rolls back payment too.
CREATE FUNCTION confirm_booking(p_booking uuid, p_admin uuid) RETURNS void LANGUAGE plpgsql SET search_path = makeup, public AS $$
DECLARE b bookings%ROWTYPE;
BEGIN
    PERFORM assert_role(p_admin,'Admin');
    PERFORM set_config('makeup.actor_id',p_admin::text,true);
    SELECT * INTO STRICT b FROM bookings WHERE id=p_booking FOR UPDATE;
    IF b.status='Confirmed' AND verified_paid(b.id)>=b.required_down_payment THEN RETURN; END IF;
    IF b.status<>'Pending Payment' THEN RAISE EXCEPTION 'Only pending deposits can be verified'; END IF;
    UPDATE payments SET status='Verified',verified_by=p_admin,verified_at=clock_timestamp()
    WHERE booking_id=b.id AND purpose='down_payment' AND status<>'Verified';
    IF NOT FOUND THEN RAISE EXCEPTION 'No unverified deposit found'; END IF;
    UPDATE bookings SET status='Confirmed',reviewed_by=p_admin,reviewed_at=clock_timestamp(),
        note=to_char(b.down_payment_percent,'FM999999999')||'% down payment verified. Please have clean, moisturized skin and a well-lit space ready.' WHERE id=b.id;
END $$;

-- 8. Read models: never store duplicate mutable balances or payment status.
CREATE VIEW booking_financial_summary WITH (security_invoker=true) AS
SELECT b.*, v.verified_paid,
    b.down_payment_percent AS down_payment_percent_snapshot,
    b.required_down_payment AS required_down_payment_snapshot,
    greatest(0::numeric,b.final_total-v.verified_paid) AS remaining_balance,
    coalesce(c.cancellation_fee,0) AS cancellation_fee,
    coalesce(c.refund_due,0) AS refund_due,
    coalesce(r.refunded,0) AS refunded,
    greatest(0::numeric,coalesce(c.refund_due,0)-coalesce(r.refunded,0)) AS refund_outstanding,
    CASE WHEN c.refund_due>coalesce(r.refunded,0) THEN 'Refund Pending'
         WHEN c.refund_due>0 THEN 'Refunded'
         WHEN v.verified_paid>=b.final_total THEN 'Paid in Full'
         WHEN v.verified_paid>=b.required_down_payment THEN 'Down Payment Verified'
         ELSE 'Pending Verification' END::varchar(25) AS payment_status,
     q.requested_start,
     b.travel_reminder_acknowledged_at AS transportation_policy_acknowledged_at
FROM bookings b
CROSS JOIN LATERAL (SELECT verified_paid(b.id) AS verified_paid) v
LEFT JOIN booking_closures c ON c.booking_id=b.id
LEFT JOIN LATERAL (SELECT sum(amount) AS refunded FROM refunds WHERE closure_id=c.id) r ON true
LEFT JOIN reschedule_requests q ON q.booking_id=b.id AND q.status='Pending';
CREATE VIEW availability_blocks WITH (security_invoker=true) AS
SELECT id,artist_id,starts_at AS from_at,ends_at AS to_at,reason,created_by,created_at,updated_at
FROM artist_schedule_entries WHERE kind='block';

-- Restrict future Supabase clients: no PUBLIC schema/data/function access.
-- This is a trusted ASP.NET server schema, NOT Supabase Auth/auth.users.
-- Enable RLS without browser policies: any later accidental browser grant fails closed.
REVOKE ALL ON SCHEMA makeup FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA makeup FROM PUBLIC;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA makeup FROM PUBLIC;
ALTER DEFAULT PRIVILEGES IN SCHEMA makeup REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
DO $$ DECLARE t record; BEGIN
    FOR t IN SELECT tablename FROM pg_tables WHERE schemaname='makeup' LOOP
        EXECUTE format('ALTER TABLE makeup.%I ENABLE ROW LEVEL SECURITY',t.tablename);
    END LOOP;
END $$;

-- 9. Baseline settings/catalog and realistic demonstration records.
-- DEMONSTRATION ONLY: accounts are DISABLED, with deliberately unusable hashes.
-- No plaintext passwords or usable demo credentials are installed by this SQL.
-- Activate/replace hashes using ASP.NET PasswordHasher<AppUser> during integration.
INSERT INTO roles(id,name) VALUES
('10000000-0000-0000-0000-000000000001','Admin'),
('10000000-0000-0000-0000-000000000002','Customer'),
('10000000-0000-0000-0000-000000000003','SuperAdmin');
INSERT INTO app_users(id,role_id,first_name,last_name,age_at_registration,gender,address,email,password_hash) VALUES
('20000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000001','Glam by Mariya','Admin',35,'Others','Address on file','admin@glambymariya.demo','DISABLED-DEMO-NOT-A-PASSWORD-HASH'),
('20000000-0000-0000-0000-000000000002','10000000-0000-0000-0000-000000000002','Alex','Morgan',28,'Others','24 Sample Street, Demo Barangay','customer@glambymariya.demo','DISABLED-DEMO-NOT-A-PASSWORD-HASH');
INSERT INTO catalog_categories(id,name) VALUES
('30000000-0000-0000-0000-000000000001','The everyday edit'),
('30000000-0000-0000-0000-000000000002','The occasion edit'),
('30000000-0000-0000-0000-000000000003','The bridal edit'),
('30000000-0000-0000-0000-000000000004','Signature');
INSERT INTO makeup_services(id,category_id,name,description,price,minutes,group_price,visual_word) VALUES
('40000000-0000-0000-0000-000000000001','30000000-0000-0000-0000-000000000001','Soft Glam','Fresh, luminous skin and softly defined eyes. Your everyday beauty, elevated.',600,60,525,'soft.'),
('40000000-0000-0000-0000-000000000002','30000000-0000-0000-0000-000000000002','Glamour Makeup','Sculpted definition, statement eyes and a flawless finish that lasts all night.',750,90,NULL,'bold.'),
('40000000-0000-0000-0000-000000000003','30000000-0000-0000-0000-000000000003','Bridal Makeup','An exquisite, camera-ready look made for your most unforgettable day. Priced for one bride.',1500,120,NULL,'forever.');
INSERT INTO makeup_addons(id,category_id,name,description,price,minutes) VALUES
('50000000-0000-0000-0000-000000000001','30000000-0000-0000-0000-000000000004','Lash application','A little extra definition',250,15),
('50000000-0000-0000-0000-000000000002','30000000-0000-0000-0000-000000000004','Hair styling','Complete your look',800,45),
('50000000-0000-0000-0000-000000000003','30000000-0000-0000-0000-000000000004','Touch-up kit','Stay fresh throughout the day',350,0);
INSERT INTO business_settings(payment_instructions) VALUES
('Demo only — do not send real money. Contact the artist for verified GCash, Maya or bank account details.');
INSERT INTO service_areas(id,name) VALUES
('60000000-0000-0000-0000-000000000001','Quezon City'),
('60000000-0000-0000-0000-000000000002','Makati'),
('60000000-0000-0000-0000-000000000003','Manila'),
('60000000-0000-0000-0000-000000000004','Pasig'),
('60000000-0000-0000-0000-000000000005','Taguig'),
('60000000-0000-0000-0000-000000000006','Mandaluyong');
INSERT INTO payment_methods(id,name,accepts_deposits) VALUES
('70000000-0000-0000-0000-000000000001','GCash',true),
('70000000-0000-0000-0000-000000000002','Maya',true),
('70000000-0000-0000-0000-000000000003','Bank Transfer',true),
('70000000-0000-0000-0000-000000000004','Cash',false);

-- Samples created in one transaction: booking -> add-on snapshots -> deposit.
INSERT INTO bookings(id,customer_id,artist_id,service_id,starts_at,address,area_id,
    booking_type,people,addons_total_snapshot,addons_minutes_snapshot,
    policy_accepted_at,policy_version,travel_reminder_acknowledged_at,travel_reminder_version) VALUES
('81000001-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000002','20000000-0000-0000-0000-000000000001',
 '40000000-0000-0000-0000-000000000001',((current_timestamp AT TIME ZONE 'Asia/Manila')::date+2+time '10:00') AT TIME ZONE 'Asia/Manila',
 '24 Sample Street, Demo Barangay','60000000-0000-0000-0000-000000000001','individual',1,0,0,now(),'booking-v1',now(),'travel-v1'),
('81000002-0000-0000-0000-000000000002','20000000-0000-0000-0000-000000000002','20000000-0000-0000-0000-000000000001',
 '40000000-0000-0000-0000-000000000001',((current_timestamp AT TIME ZONE 'Asia/Manila')::date+3+time '08:00') AT TIME ZONE 'Asia/Manila',
 '24 Sample Street, Demo Barangay','60000000-0000-0000-0000-000000000001','package',4,250,15,now(),'booking-v1',now(),'travel-v1');
INSERT INTO booking_addons(booking_id,addon_id) VALUES
('81000002-0000-0000-0000-000000000002','50000000-0000-0000-0000-000000000001');
INSERT INTO payments(booking_id,method_id,purpose,amount,reference,submitted_by) VALUES
('81000001-0000-0000-0000-000000000001','70000000-0000-0000-0000-000000000001','down_payment',300,'DEMO-REFERENCE-001','20000000-0000-0000-0000-000000000002'),
('81000002-0000-0000-0000-000000000002','70000000-0000-0000-0000-000000000002','down_payment',1175,'DEMO-GROUP-002','20000000-0000-0000-0000-000000000002');
SELECT confirm_booking('81000002-0000-0000-0000-000000000002','20000000-0000-0000-0000-000000000001');
INSERT INTO reschedule_requests(booking_id,requested_by,original_start,requested_start)
SELECT id,customer_id,starts_at,starts_at+interval '1 day' FROM bookings WHERE id='81000002-0000-0000-0000-000000000002';
INSERT INTO notifications(user_id,booking_id,kind,message,scheduled_at,deduplication_key)
SELECT customer_id,id,'reminder','Your Glam by Mariya appointment is tomorrow. Please prepare a well-lit space.',starts_at-interval '24 hours',id::text||':24h:'||starts_at::text
FROM bookings WHERE id='81000002-0000-0000-0000-000000000002';
-- A third booking demonstrates a confirmed cancellation and actual manual refund.
INSERT INTO bookings(id,customer_id,artist_id,service_id,starts_at,address,area_id,
    policy_accepted_at,policy_version,travel_reminder_acknowledged_at,travel_reminder_version)
SELECT '81000003-0000-0000-0000-000000000003',customer_id,artist_id,service_id,starts_at+interval '4 days',address,area_id,
    now(),'booking-v1',now(),'travel-v1' FROM bookings WHERE id='81000001-0000-0000-0000-000000000001';
INSERT INTO payments(booking_id,method_id,purpose,amount,reference,submitted_by) VALUES
('81000003-0000-0000-0000-000000000003','70000000-0000-0000-0000-000000000001','down_payment',300,'DEMO-CANCEL-003','20000000-0000-0000-0000-000000000002');
SELECT confirm_booking('81000003-0000-0000-0000-000000000003','20000000-0000-0000-0000-000000000001');
SELECT set_config('makeup.actor_id','20000000-0000-0000-0000-000000000002',true);
UPDATE bookings SET status='Cancelled',cancellation_confirmed_at=now(),note='Customer confirmed cancellation and the PHP 200 deduction.'
WHERE id='81000003-0000-0000-0000-000000000003';
SELECT set_config('makeup.actor_id','20000000-0000-0000-0000-000000000001',true);
INSERT INTO refunds(closure_id,method_id,amount,transfer_reference,sent_at,recorded_by)
SELECT id,'70000000-0000-0000-0000-000000000001',100,'DEMO-REFUND-003',now(),'20000000-0000-0000-0000-000000000001'
FROM booking_closures WHERE booking_id='81000003-0000-0000-0000-000000000003';
INSERT INTO artist_schedule_entries(artist_id,kind,starts_at,ends_at,reason,created_by)
SELECT '20000000-0000-0000-0000-000000000001','block',
    ((current_timestamp AT TIME ZONE 'Asia/Manila')::date+5+time '12:00') AT TIME ZONE 'Asia/Manila',
    ((current_timestamp AT TIME ZONE 'Asia/Manila')::date+5+time '13:00') AT TIME ZONE 'Asia/Manila',
    'Artist lunch / personal appointment','20000000-0000-0000-0000-000000000001';
-- Confirmed records remain valid if the accounts are later disabled.
UPDATE app_users SET is_active=false;
COMMIT;

-- 10. Common read operations (safe, read-only examples; replace UUIDs with parameters).
-- Customer history + exact frozen pricing/financial status.
SELECT reference,service_name_snapshot,starts_at,people,booking_type,original_total,group_discount,
       addons_total_snapshot,final_total,required_down_payment,verified_paid,remaining_balance,status,payment_status,requested_start
FROM makeup.booking_financial_summary
WHERE customer_id='20000000-0000-0000-0000-000000000002' ORDER BY starts_at DESC;

-- Artist review queue and manual proof/reference review (server/admin only).
SELECT b.reference,b.customer_name_snapshot,b.starts_at,p.amount,m.name AS method,p.reference,p.status,p.proof_asset_id
FROM makeup.bookings b JOIN makeup.payments p ON p.booking_id=b.id
JOIN makeup.payment_methods m ON m.id=p.method_id WHERE b.status='Pending Payment' ORDER BY b.created_at;

-- Public catalog; choose preview -> card -> application default artwork.
SELECT s.id,s.name,s.description,c.name AS eyebrow,s.visual_word,s.price,s.minutes,s.group_price,s.group_minimum_people,
       coalesce(preview_asset.object_key,card_asset.object_key) AS booking_preview_key,card_asset.object_key AS card_key
FROM makeup.makeup_services s JOIN makeup.catalog_categories c ON c.id=s.category_id
LEFT JOIN makeup.catalog_images pi ON pi.service_id=s.id AND pi.slot='preview'
LEFT JOIN makeup.media_assets preview_asset ON preview_asset.id=pi.asset_id
LEFT JOIN makeup.catalog_images ci ON ci.service_id=s.id AND ci.slot='card'
LEFT JOIN makeup.media_assets card_asset ON card_asset.id=ci.asset_id
WHERE s.is_active AND NOT s.is_archived ORDER BY s.price;

-- 15-minute candidate starts for tomorrow (60-minute service + current buffer).
-- Return availability ONLY, never customer/address/reason details to the client.
WITH s AS (SELECT * FROM makeup.business_settings WHERE id=1),
d AS (SELECT s.*,((now() AT TIME ZONE time_zone)::date+1)::timestamp AS local_day FROM s),
slots AS (SELECT d.*,g AS local_start FROM d CROSS JOIN LATERAL generate_series(
    local_day+make_interval(hours=>open_hour),
    local_day+make_interval(hours=>close_hour)-make_interval(mins=>60+buffer_minutes),interval '15 minutes') g)
SELECT local_start,NOT EXISTS (SELECT 1 FROM makeup.artist_schedule_entries e
    WHERE e.artist_id='20000000-0000-0000-0000-000000000001'
      AND tstzrange(e.starts_at,e.ends_at,'[)') && tstzrange(local_start AT TIME ZONE time_zone,
          (local_start+make_interval(mins=>60+buffer_minutes)) AT TIME ZONE time_zone,'[)')) AS available
FROM slots WHERE local_start AT TIME ZONE time_zone > now() ORDER BY local_start;

-- Confirmed agenda (session end differs from travel-buffer reservation end).
SELECT reference,customer_name_snapshot,starts_at,ends_at,reserved_until,area_name_snapshot
FROM makeup.bookings WHERE status='Confirmed' ORDER BY starts_at;

-- Collection/refund report: aggregate independently to avoid join multiplication.
SELECT (SELECT coalesce(sum(amount),0) FROM makeup.payments WHERE status='Verified')
         +(SELECT coalesce(sum(amount),0) FROM makeup.payment_adjustments) AS verified_collections,
       (SELECT coalesce(sum(amount),0) FROM makeup.refunds) AS refunds_sent,
       (SELECT coalesce(sum(makeup.verified_paid(id)),0) FROM makeup.bookings)
         -(SELECT coalesce(sum(amount),0) FROM makeup.refunds) AS net_collected,
       (SELECT coalesce(sum(final_total),0) FROM makeup.bookings WHERE status='Completed') AS completed_service_value;
SELECT reference,cancellation_fee,refund_due,refunded,refund_outstanding FROM makeup.booking_financial_summary WHERE refund_due>0;
SELECT * FROM makeup.reschedule_requests WHERE status='Pending' ORDER BY created_at;
SELECT * FROM makeup.notifications WHERE user_id='20000000-0000-0000-0000-000000000002' ORDER BY created_at DESC LIMIT 6;
SELECT * FROM makeup.notifications WHERE delivery_status='Pending' AND scheduled_at<=now() ORDER BY scheduled_at;
SELECT * FROM makeup.audit_logs ORDER BY created_at DESC LIMIT 30;

-- Transactional write recipes and complete ERD/field mapping:
-- docs/database-schema.md. Do not run this design against the application yet.
