START TRANSACTION;
-- Additive ASP.NET Core Data Protection key store.
-- The migration and application use the same server-only PostgreSQL login;
-- that login owns the table. Browser-facing PostgreSQL roles have no grants.
CREATE SEQUENCE public.data_protection_keys_id_seq AS integer;

CREATE TABLE public.data_protection_keys
(
    "Id" integer NOT NULL DEFAULT nextval('public.data_protection_keys_id_seq'),
    "FriendlyName" text NULL,
    "Xml" text NULL,
    CONSTRAINT pk_data_protection_keys PRIMARY KEY ("Id")
);

ALTER SEQUENCE public.data_protection_keys_id_seq OWNED BY public.data_protection_keys."Id";
ALTER TABLE public.data_protection_keys ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON TABLE public.data_protection_keys FROM PUBLIC, anon, authenticated;
REVOKE ALL ON SEQUENCE public.data_protection_keys_id_seq FROM PUBLIC, anon, authenticated;


INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261017130000_DataProtectionKeys', '10.0.13');

COMMIT;

