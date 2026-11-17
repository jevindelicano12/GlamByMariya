-- READ-ONLY PREFLIGHT. Run ONLY in the NEW glam-by-mariya-v2 SQL Editor.
-- Before running, verify BOTH project name and project reference in the dashboard.
-- PostgreSQL's database name (usually "postgres") does NOT identify the project.
-- Reviewed file: migrations/0004_customer_auth_compatibility.sql
-- Revision 2 SHA256: 7A089ACC1B64447B3ED5D24142A73C2D966EA7D9B572489267B28594987DE84A
--
-- This file contains one SELECT statement (WITH CTEs), no migration, DDL, DML,
-- SET ROLE, settings changes, explicit locks or Auth API calls. query_to_xml below runs
-- only fixed/generated SELECT counts and migration version/name projections.
-- It never reads Customer records, auth.users, passwords, keys or migration SQL.
-- Return ALL section/evidence rows. Any error or inaccessible evidence is BLOCKED,
-- not proof that a table is empty, a role is safe, or migration 0004 is absent.
WITH
server AS (
    SELECT current_setting('server_version_num')::integer AS version_num,
           r.oid AS role_oid,r.rolname,r.rolsuper,r.rolbypassrls,r.rolcreaterole,r.rolcreatedb
    FROM pg_catalog.pg_roles r WHERE r.rolname=current_user
),
foundation(table_name) AS (VALUES
    ('makeup_artists'),('services'),('artist_availability'),
    ('blocked_slots'),('customers'),('reservations')
),
expected_columns(table_name,column_name,expected_type,required,default_kind) AS (VALUES
    ('makeup_artists','id','bigint',true,'sequence'),
    ('makeup_artists','full_name','character varying(150)',true,'none'),
    ('makeup_artists','phone','character varying(20)',false,'none'),
    ('makeup_artists','is_active','boolean',true,'true'),
    ('services','id','bigint',true,'sequence'),
    ('services','name','character varying(100)',true,'none'),
    ('services','description','text',false,'none'),
    ('services','price','numeric(10,2)',true,'none'),
    ('services','duration_minutes','integer',true,'none'),
    ('services','is_active','boolean',true,'true'),
    ('artist_availability','id','bigint',true,'sequence'),
    ('artist_availability','artist_id','bigint',true,'none'),
    ('artist_availability','weekday','integer',true,'none'),
    ('artist_availability','start_time','time without time zone',true,'none'),
    ('artist_availability','end_time','time without time zone',true,'none'),
    ('blocked_slots','id','bigint',true,'sequence'),
    ('blocked_slots','artist_id','bigint',true,'none'),
    ('blocked_slots','start_at','timestamp with time zone',true,'none'),
    ('blocked_slots','end_at','timestamp with time zone',true,'none'),
    ('blocked_slots','reason','text',false,'none'),
    ('customers','id','bigint',true,'sequence'),
    ('customers','full_name','character varying(150)',true,'none'),
    ('customers','phone','character varying(20)',false,'none'),
    ('customers','email','character varying(254)',false,'none'),
    ('customers','created_at','timestamp with time zone',true,'now'),
    ('reservations','id','bigint',true,'sequence'),
    ('reservations','reference','character varying(30)',true,'none'),
    ('reservations','customer_id','bigint',true,'none'),
    ('reservations','service_id','bigint',true,'none'),
    ('reservations','artist_id','bigint',true,'none'),
    ('reservations','start_at','timestamp with time zone',true,'none'),
    ('reservations','end_at','timestamp with time zone',true,'none'),
    ('reservations','reserved_until','timestamp with time zone',true,'none'),
    ('reservations','location','text',false,'none'),
    ('reservations','notes','text',false,'none'),
    ('reservations','total_price','numeric(10,2)',true,'none'),
    ('reservations','status','character varying(20)',true,'pending'),
    ('reservations','created_at','timestamp with time zone',true,'now')
),
tables AS (
    SELECT f.table_name,c.oid,c.relkind,c.relowner,c.relrowsecurity,c.relforcerowsecurity,
           pg_catalog.pg_get_userbyid(c.relowner) AS owner,
           CASE WHEN c.oid IS NOT NULL THEN pg_catalog.row_security_active(c.oid) END AS rls_applies_to_inspector
    FROM foundation f LEFT JOIN pg_catalog.pg_namespace n ON n.nspname='public'
    LEFT JOIN pg_catalog.pg_class c ON c.relnamespace=n.oid AND c.relname=f.table_name
),
actual_columns AS (
    SELECT t.table_name,a.attname AS column_name,pg_catalog.format_type(a.atttypid,a.atttypmod) AS actual_type,
           a.attnotnull,pg_catalog.pg_get_expr(d.adbin,d.adrelid) AS default_expression,
           a.attidentity,a.attgenerated
    FROM tables t JOIN pg_catalog.pg_attribute a ON a.attrelid=t.oid AND a.attnum>0 AND NOT a.attisdropped
    LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
),
columns_review AS (
    SELECT coalesce(e.table_name,a.table_name) AS table_name,coalesce(e.column_name,a.column_name) AS column_name,
           e.expected_type,a.actual_type,e.required,a.attnotnull,e.default_kind,a.default_expression,
           CASE
             WHEN e.column_name IS NULL THEN 'BLOCKED: unexpected column'
             WHEN a.column_name IS NULL THEN 'BLOCKED: missing column'
             WHEN e.expected_type<>a.actual_type OR e.required<>a.attnotnull OR a.attidentity<>'' OR a.attgenerated<>'' THEN 'BLOCKED: type/nullability/generation drift'
             WHEN NOT coalesce(CASE e.default_kind
                 WHEN 'none' THEN a.default_expression IS NULL
                 WHEN 'sequence' THEN coalesce(a.default_expression LIKE 'nextval(%',false)
                 WHEN 'true' THEN a.default_expression='true'
                 WHEN 'now' THEN a.default_expression='now()'
                 WHEN 'pending' THEN a.default_expression='''Pending Payment''::character varying'
                 ELSE false END,false) THEN 'BLOCKED: default drift'
             ELSE 'MATCH'
           END AS finding
    FROM expected_columns e FULL JOIN actual_columns a USING(table_name,column_name)
),
expected_constraints(table_name,constraint_name,kind,expected_rule) AS (VALUES
    ('makeup_artists','makeup_artists_pkey','p','PRIMARY KEY(id)'),
    ('services','services_pkey','p','PRIMARY KEY(id)'),
    ('services','services_duration_minutes_check','c','duration_minutes > 0'),
    ('services','services_price_check','c','price >= 0'),
    ('artist_availability','artist_availability_pkey','p','PRIMARY KEY(id)'),
    ('artist_availability','artist_availability_artist_id_fkey','f','artist_id -> public.makeup_artists(id)'),
    ('artist_availability','artist_availability_check','c','end_time > start_time'),
    ('artist_availability','artist_availability_weekday_check','c','weekday BETWEEN 0 AND 6'),
    ('blocked_slots','blocked_slots_pkey','p','PRIMARY KEY(id)'),
    ('blocked_slots','blocked_slots_artist_id_fkey','f','artist_id -> public.makeup_artists(id)'),
    ('blocked_slots','blocked_slots_check','c','end_at > start_at'),
    ('customers','customers_pkey','p','PRIMARY KEY(id)'),
    ('reservations','reservations_pkey','p','PRIMARY KEY(id)'),
    ('reservations','reservations_reference_key','u','UNIQUE(reference)'),
    ('reservations','reservations_customer_id_fkey','f','customer_id -> public.customers(id)'),
    ('reservations','reservations_service_id_fkey','f','service_id -> public.services(id)'),
    ('reservations','reservations_artist_id_fkey','f','artist_id -> public.makeup_artists(id)'),
    ('reservations','reservations_interval_check','c','end_at > start_at AND reserved_until >= end_at'),
    ('reservations','reservations_total_price_check','c','total_price >= 0'),
    ('reservations','reservations_status_check','c','Pending,Pending Payment,Confirmed,InProgress,Completed,Cancelled,Rejected,NoShow,No-show,Incomplete'),
    ('reservations','reservations_artist_reserved_overlap','x','GiST artist_id WITH =; tstzrange(start_at,reserved_until,''[)'') WITH &&; statuses Pending/Pending Payment/Confirmed/InProgress')
),
constraint_review AS (
    SELECT e.*,c.contype,c.convalidated,c.condeferrable,c.condeferred,
           pg_catalog.pg_get_constraintdef(c.oid,true) AS actual_definition,
           CASE WHEN c.oid IS NULL OR c.contype::text<>e.kind OR NOT c.convalidated
             THEN 'BLOCKED: missing/type/unvalidated'
             ELSE 'REVIEW: compare complete definition with expected_rule' END AS finding
    FROM expected_constraints e LEFT JOIN tables t USING(table_name)
    LEFT JOIN pg_catalog.pg_constraint c ON c.conrelid=t.oid AND c.conname=e.constraint_name
),
target_objects(object_name) AS (VALUES
    ('admin_users'),('admin_users_id_seq'),('admin_users_pkey'),('admin_users_app_id_key'),
    ('admin_users_app_identity_key'),('admin_users_auth_email_key'),
    ('account_identities'),('account_identities_pkey'),('account_identities_customer_id_key'),
    ('account_identities_admin_id_key'),('account_identities_login_key'),('account_identities_supabase_subject_key'),
    ('account_security_generations'),('account_security_generations_pkey'),
    ('auth_mvc_signup_attempts'),('auth_mvc_signup_attempts_pkey'),('auth_mvc_signup_attempts_browser_secret_hash_key'),
    ('auth_signup_operation_key'),('auth_signup_unresolved_email_key'),
    ('customers_auth_email_key'),('customers_app_id_key'),('customers_app_identity_key')
),
count_targets AS (
    SELECT t.*,coalesce(t.relkind='r' AND NOT t.rls_applies_to_inspector
      AND pg_catalog.has_table_privilege(current_user,t.oid,'SELECT'),false) AS authoritative
    FROM tables t WHERE t.table_name IN ('customers','reservations')
),
row_counts AS (
    SELECT t.table_name,t.authoritative,x.row_count,
      CASE WHEN NOT t.authoritative THEN 'BLOCKED: missing table, SELECT or authoritative RLS visibility'
           WHEN x.row_count>0 THEN 'BLOCKED: occupied Customer/reservation foundation'
           WHEN x.row_count=0 THEN 'EMPTY (authoritative at query time)'
           ELSE 'BLOCKED: count unavailable' END AS finding
    FROM count_targets t
    LEFT JOIN LATERAL XMLTABLE('/table/row' PASSING
        CASE WHEN t.authoritative THEN pg_catalog.query_to_xml(
          format('SELECT count(*) AS row_count FROM public.%I',t.table_name),false,false,'')
          ELSE '<table/>'::xml END
        COLUMNS row_count bigint PATH 'row_count') x ON true
),
role_targets(role_name) AS (VALUES
    ('anon'),('authenticated'),('service_role'),('glam_v2_backend'),('glam_v2_authz_owner')
),
roles AS (
    SELECT e.role_name,r.oid,r.rolsuper,r.rolbypassrls,r.rolcanlogin,r.rolinherit,
           r.rolcreatedb,r.rolcreaterole,r.rolreplication
    FROM role_targets e LEFT JOIN pg_catalog.pg_roles r ON r.rolname=e.role_name
),
api_reachable AS (
    SELECT r.role_name AS api_role,e.oid,e.rolname AS reachable_role
    FROM roles r CROSS JOIN pg_catalog.pg_roles e CROSS JOIN server s
    WHERE r.role_name IN ('anon','authenticated','service_role') AND r.oid IS NOT NULL
      AND CASE WHEN s.version_num>=160000 THEN pg_catalog.pg_has_role(r.oid,e.oid,'SET')
               ELSE pg_catalog.pg_has_role(r.oid,e.oid,'MEMBER') END
),
inspected_relations AS (
    SELECT c.oid,n.nspname,c.relname,c.relkind,c.relowner,c.relacl
    FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
    WHERE n.nspname='public' AND
      (c.relname IN (SELECT table_name FROM foundation)
       OR c.relname IN ('admin_users','account_identities','account_security_generations','auth_mvc_signup_attempts')
       OR c.relname IN ('makeup_artists_id_seq','services_id_seq','artist_availability_id_seq',
                       'blocked_slots_id_seq','customers_id_seq','reservations_id_seq','admin_users_id_seq'))
),
table_privileges AS (
    SELECT a.api_role,a.reachable_role,t.relname,
      (SELECT jsonb_agg(p.privilege ORDER BY p.privilege)
       FROM (VALUES('SELECT'),('INSERT'),('UPDATE'),('DELETE'),('TRUNCATE'),('REFERENCES'),('TRIGGER'),('MAINTAIN')) p(privilege)
       WHERE CASE WHEN p.privilege='MAINTAIN' AND s.version_num<170000 THEN false
             ELSE pg_catalog.has_table_privilege(a.oid,t.oid,p.privilege) END) AS effective_table_rights,
      (SELECT jsonb_agg(jsonb_build_object('column',c.attname,'privilege',p.privilege) ORDER BY c.attnum,p.privilege)
       FROM pg_catalog.pg_attribute c CROSS JOIN (VALUES('SELECT'),('INSERT'),('UPDATE'),('REFERENCES')) p(privilege)
       WHERE c.attrelid=t.oid AND c.attnum>0 AND NOT c.attisdropped
         AND pg_catalog.has_column_privilege(a.oid,t.oid,c.attnum,p.privilege)) AS effective_column_rights
    FROM api_reachable a CROSS JOIN inspected_relations t CROSS JOIN server s
    WHERE t.relkind IN ('r','p','v','m','f')
),
history_targets(schema_name,table_name,version_column,name_column) AS (VALUES
    ('supabase_migrations','schema_migrations','version','name'),
    ('public','__EFMigrationsHistory','MigrationId','ProductVersion')
),
history_capabilities AS (
    SELECT h.*,c.oid,
      coalesce(c.relkind IN ('r','p') AND NOT pg_catalog.row_security_active(c.oid)
        AND pg_catalog.has_table_privilege(current_user,c.oid,'SELECT')
        AND EXISTS(SELECT 1 FROM pg_catalog.pg_attribute a WHERE a.attrelid=c.oid
          AND a.attname=h.version_column AND a.attnum>0 AND NOT a.attisdropped),false) AS readable,
      EXISTS(SELECT 1 FROM pg_catalog.pg_attribute a WHERE a.attrelid=c.oid
          AND a.attname=h.name_column AND a.attnum>0 AND NOT a.attisdropped) AS has_name
    FROM history_targets h LEFT JOIN pg_catalog.pg_namespace n ON n.nspname=h.schema_name
    LEFT JOIN pg_catalog.pg_class c ON c.relnamespace=n.oid AND c.relname=h.table_name
),
history AS (
    SELECT h.schema_name,h.table_name,h.oid IS NOT NULL AS exists,h.readable,x.payload::jsonb AS version_names_only,
      'History absence alone cannot prove manual SQL was never applied; cross-check object conflicts and owner deployment records.' AS caveat
    FROM history_capabilities h LEFT JOIN LATERAL XMLTABLE('/table/row' PASSING
      CASE WHEN h.readable THEN pg_catalog.query_to_xml(
        format('SELECT coalesce(jsonb_agg(jsonb_build_object(''version'',%1$I::text,''name'',%2$s) ORDER BY %1$I::text),''[]''::jsonb)::text AS payload FROM %3$I.%4$I',
          h.version_column,CASE WHEN h.has_name THEN format('%I::text',h.name_column) ELSE 'NULL::text' END,
          h.schema_name,h.table_name),false,false,'') ELSE '<table/>'::xml END
      COLUMNS payload text PATH 'payload') x ON true
),
report(section,evidence) AS (
    SELECT '01_connection_and_version',jsonb_build_object(
      'required_dashboard_project','glam-by-mariya-v2 (owner must verify project reference separately)',
      'database',current_database(),'current_user',current_user,'session_user',session_user,
      'server_version',current_setting('server_version'),'version_num',s.version_num,
      'migration_17_plus_syntax',CASE WHEN s.version_num>=170000 THEN 'COMPATIBLE SYNTAX; local proof was PostgreSQL 18' ELSE 'BLOCKED: 0004 uses MAINTAIN privilege syntax (17+)' END,
      'inspector_superuser',s.rolsuper,'inspector_bypassrls',s.rolbypassrls,
      'can_create_roles_attribute',s.rolcreaterole,'can_create_database_attribute',s.rolcreatedb,
      'can_create_schemas',pg_catalog.has_database_privilege(current_user,current_database(),'CREATE'),
      'database_owner',(SELECT pg_catalog.pg_get_userbyid(datdba) FROM pg_catalog.pg_database WHERE datname=current_database()),
      'target_identity_from_sql','NOT PROVEN: a database named postgres is not a project identifier',
      'provider_admin_restrictions','Catalog flags are evidence, not a test of managed role provisioning or DDL execution')
    FROM server s
    UNION ALL SELECT '02_schema_ownership',coalesce(jsonb_agg(jsonb_build_object(
      'schema',n.nspname,'owner',pg_catalog.pg_get_userbyid(n.nspowner),'acl',n.nspacl,
      'inspector_usage',pg_catalog.has_schema_privilege(current_user,n.oid,'USAGE'),
      'inspector_create',pg_catalog.has_schema_privilege(current_user,n.oid,'CREATE'),
      'inspector_owns_or_inherits_owner',pg_catalog.pg_has_role(s.role_oid,n.nspowner,'USAGE'))
      ORDER BY n.nspname),'[]'::jsonb)
    FROM pg_catalog.pg_namespace n CROSS JOIN server s
    WHERE n.nspname IN ('public','extensions','v2_authz_api','v2_authz_private','supabase_migrations')
    UNION ALL SELECT '03_extensions_and_language',jsonb_build_object(
      'extensions',(SELECT coalesce(jsonb_agg(jsonb_build_object('extension',e.extname,'version',e.extversion,
        'schema',n.nspname,'owner',pg_catalog.pg_get_userbyid(e.extowner))),'[]'::jsonb)
        FROM pg_catalog.pg_extension e JOIN pg_catalog.pg_namespace n ON n.oid=e.extnamespace WHERE e.extname='btree_gist'),
      'plpgsql',(SELECT jsonb_build_object('trusted',l.lanpltrusted,'owner',pg_catalog.pg_get_userbyid(l.lanowner),
        'inspector_usage',pg_catalog.has_language_privilege(current_user,l.oid,'USAGE')) FROM pg_catalog.pg_language l WHERE l.lanname='plpgsql'),
      'gen_random_uuid',pg_catalog.to_regprocedure('pg_catalog.gen_random_uuid()')::text,
      'required_extension_location','btree_gist in public or extensions; do not relocate or reinstall during preflight')
    UNION ALL SELECT '04_foundation_tables_and_rls',jsonb_agg(to_jsonb(t)||jsonb_build_object(
      'inspector_can_alter_as_owner',coalesce(s.rolsuper OR pg_catalog.pg_has_role(s.role_oid,t.relowner,'USAGE'),false),
      'finding',CASE WHEN t.oid IS NULL OR t.relkind<>'r' OR NOT t.relrowsecurity OR NOT t.relforcerowsecurity
        THEN 'BLOCKED: missing/wrong kind/RLS drift' ELSE 'PRESENT: inspect columns/constraints' END)
      ORDER BY t.table_name) FROM tables t CROSS JOIN server s
    UNION ALL SELECT '05_foundation_column_comparison',jsonb_agg(to_jsonb(c) ORDER BY table_name,column_name) FROM columns_review c
    UNION ALL SELECT '06_required_constraints_compare_definitions',jsonb_agg(to_jsonb(c) ORDER BY table_name,constraint_name) FROM constraint_review c
    UNION ALL SELECT '07_all_foundation_constraints_and_indexes',jsonb_build_object(
      'constraints',(SELECT jsonb_agg(jsonb_build_object('table',t.table_name,'name',c.conname,
        'definition',pg_catalog.pg_get_constraintdef(c.oid,true),'validated',c.convalidated)
        ORDER BY t.table_name,c.conname) FROM tables t JOIN pg_catalog.pg_constraint c ON c.conrelid=t.oid),
      'indexes',(SELECT jsonb_agg(jsonb_build_object('table',t.table_name,'name',ic.relname,
        'valid',i.indisvalid,'ready',i.indisready,'definition',pg_catalog.pg_get_indexdef(i.indexrelid))
        ORDER BY t.table_name,ic.relname) FROM tables t JOIN pg_catalog.pg_index i ON i.indrelid=t.oid
        JOIN pg_catalog.pg_class ic ON ic.oid=i.indexrelid),
      '0004_reservation_effect','SOURCE REVIEW: no reservation ALTER/DROP, policy or grant; existing exclusion is preserved, not replaced')
    UNION ALL SELECT '08_authoritative_row_counts',jsonb_agg(to_jsonb(r) ORDER BY table_name) FROM row_counts r
    UNION ALL SELECT '09_conflicts',jsonb_build_object(
      'relations',(SELECT coalesce(jsonb_agg(jsonb_build_object('name',o.object_name,'kind',c.relkind,
        'owner',pg_catalog.pg_get_userbyid(c.relowner)) ORDER BY o.object_name),'[]'::jsonb)
        FROM target_objects o JOIN pg_catalog.pg_class c ON c.relname=o.object_name
        JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace AND n.nspname='public'),
      'schemas',(SELECT coalesce(jsonb_agg(nspname ORDER BY nspname),'[]'::jsonb) FROM pg_catalog.pg_namespace
        WHERE nspname IN ('v2_authz_private','v2_authz_api')),
      'added_customer_columns',(SELECT coalesce(jsonb_agg(column_name ORDER BY column_name),'[]'::jsonb)
        FROM actual_columns WHERE table_name='customers' AND column_name IN
          ('app_id','is_active','locked_until','first_name','last_name','age_at_registration','gender','address','password_hash','security_stamp','birthday','suffix')),
      'existing_customer_auth_constraints',(SELECT coalesce(jsonb_agg(c.conname),'[]'::jsonb)
        FROM pg_catalog.pg_constraint c JOIN tables t ON t.oid=c.conrelid AND t.table_name='customers'
        WHERE c.conname IN ('customers_auth_email_check','customers_app_id_key','customers_app_identity_key')),
      'auth_functions',(SELECT coalesce(jsonb_agg(jsonb_build_object('schema',n.nspname,'name',p.proname,
        'signature',pg_catalog.pg_get_function_identity_arguments(p.oid),'owner',pg_catalog.pg_get_userbyid(p.proowner),
        'security_definer',p.prosecdef)),'[]'::jsonb) FROM pg_catalog.pg_proc p
        JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname IN ('v2_authz_api','v2_authz_private')),
      'rule','Any target auth object/column/schema collision blocks 0004; do not overwrite it')
    UNION ALL SELECT '10_policies_and_noninternal_triggers',jsonb_build_object(
      'policies',(SELECT coalesce(jsonb_agg(jsonb_build_object('table',t.table_name,'name',p.polname,
        'command',p.polcmd,'roles',p.polroles::text,'using',pg_catalog.pg_get_expr(p.polqual,p.polrelid),
        'check',pg_catalog.pg_get_expr(p.polwithcheck,p.polrelid))),'[]'::jsonb)
        FROM tables t JOIN pg_catalog.pg_policy p ON p.polrelid=t.oid),
      'triggers',(SELECT coalesce(jsonb_agg(jsonb_build_object('table',t.table_name,'name',g.tgname,
        'enabled',g.tgenabled,'definition',pg_catalog.pg_get_triggerdef(g.oid))),'[]'::jsonb)
        FROM tables t JOIN pg_catalog.pg_trigger g ON g.tgrelid=t.oid WHERE NOT g.tgisinternal),
      'rule','0001/0002 create no policies or noninternal triggers; any Customer policy blocks 0004')
    UNION ALL SELECT '11_role_prerequisites',jsonb_agg(to_jsonb(r)||jsonb_build_object(
      'finding',CASE WHEN r.oid IS NULL THEN 'BLOCKED: missing role; no provisioning authorized in this preflight'
        WHEN r.role_name IN ('glam_v2_backend','glam_v2_authz_owner') AND
          (r.rolsuper OR r.rolbypassrls OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolinherit
            OR r.rolcanlogin<>(r.role_name='glam_v2_backend')) THEN 'BLOCKED: incompatible restricted role attributes'
        ELSE 'ATTRIBUTES PRESENT: also review memberships/ownership' END) ORDER BY r.role_name) FROM roles r
    UNION ALL SELECT '12_relevant_memberships',coalesce(jsonb_agg(jsonb_build_object(
      'member',pg_catalog.pg_get_userbyid(m.member),'role',pg_catalog.pg_get_userbyid(m.roleid),
      'grantor',pg_catalog.pg_get_userbyid(m.grantor),'admin_option',m.admin_option,
      'inherit_option',to_jsonb(m)->'inherit_option','set_option',to_jsonb(m)->'set_option')),'[]'::jsonb)
    FROM pg_catalog.pg_auth_members m WHERE m.member IN (SELECT oid FROM roles WHERE oid IS NOT NULL)
      OR m.roleid IN (SELECT oid FROM roles WHERE oid IS NOT NULL)
      OR m.member=(SELECT role_oid FROM server)
      OR m.roleid=(SELECT role_oid FROM server)
    UNION ALL SELECT '13_api_set_role_reachability',coalesce(jsonb_agg(to_jsonb(a) ORDER BY api_role,reachable_role),'[]'::jsonb) FROM api_reachable a
    UNION ALL SELECT '14_effective_api_and_reachable_table_column_privileges',coalesce(jsonb_agg(to_jsonb(p)
      ORDER BY api_role,reachable_role,relname),'[]'::jsonb)
    FROM table_privileges p WHERE effective_table_rights IS NOT NULL OR effective_column_rights IS NOT NULL
    UNION ALL SELECT '15_relation_and_column_acls_including_public',jsonb_build_object(
      'relations',(SELECT coalesce(jsonb_agg(jsonb_build_object('object',t.relname,'kind',t.relkind,
        'owner',pg_catalog.pg_get_userbyid(t.relowner),'acl',t.relacl)),'[]'::jsonb) FROM inspected_relations t),
      'columns',(SELECT coalesce(jsonb_agg(jsonb_build_object('object',t.relname,'column',a.attname,'acl',a.attacl)),'[]'::jsonb)
        FROM inspected_relations t JOIN pg_catalog.pg_attribute a ON a.attrelid=t.oid
        WHERE a.attnum>0 AND NOT a.attisdropped AND a.attacl IS NOT NULL),
      'PUBLIC_note','PUBLIC is grantee 0 / empty grantee before = in ACL text; table REVOKE does not itself remove column ACLs')
    UNION ALL SELECT '16_sequences_effective_api_privileges',coalesce(jsonb_agg(jsonb_build_object(
      'api_role',a.api_role,'reachable_role',a.reachable_role,'sequence',t.relname,
      'usage',pg_catalog.has_sequence_privilege(a.oid,t.oid,'USAGE'),
      'select',pg_catalog.has_sequence_privilege(a.oid,t.oid,'SELECT'),
      'update',pg_catalog.has_sequence_privilege(a.oid,t.oid,'UPDATE'))
      ORDER BY a.api_role,a.reachable_role,t.relname),'[]'::jsonb)
    FROM api_reachable a CROSS JOIN inspected_relations t WHERE t.relkind='S'
    UNION ALL SELECT '17_creator_default_privileges',coalesce(jsonb_agg(jsonb_build_object(
      'creator',pg_catalog.pg_get_userbyid(d.defaclrole),'schema',coalesce(n.nspname,'<global>'),
      'object_kind',d.defaclobjtype,'grantee',CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee) END,
      'grantor',pg_catalog.pg_get_userbyid(a.grantor),'privilege',a.privilege_type,'grantable',a.is_grantable)),'[]'::jsonb)
    FROM pg_catalog.pg_default_acl d LEFT JOIN pg_catalog.pg_namespace n ON n.oid=d.defaclnamespace
    CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) a
    WHERE d.defaclnamespace=0 OR n.nspname IN ('public','v2_authz_api','v2_authz_private')
    UNION ALL SELECT '18_migration_history_versions_names_only',jsonb_agg(to_jsonb(h) ORDER BY schema_name,table_name) FROM history h
    UNION ALL SELECT '19_other_migration_ledgers_and_payment_markers',jsonb_build_object(
      'possible_ledgers',(SELECT coalesce(jsonb_agg(jsonb_build_object('schema',n.nspname,'name',c.relname,'kind',c.relkind)),'[]'::jsonb)
        FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname IN ('public','supabase_migrations') AND c.relname ILIKE '%migration%'),
      'payment_relations',(SELECT coalesce(jsonb_agg(c.relname),'[]'::jsonb)
        FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public' AND c.relname IN ('payments','payment_revisions')),
      'payment_functions',(SELECT coalesce(jsonb_agg(p.proname),'[]'::jsonb)
        FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
        WHERE n.nspname='public' AND p.proname IN ('v2_payment_claim_guard','v2_payment_revision_guard','v2_payment_immutable')),
      'rule','0003 must stay unapplied; inspect any marker without executing or undoing anything')
    UNION ALL SELECT '20_sql_editor_operator_capabilities',jsonb_build_object(
      'operator',current_user,'session_user',session_user,'is_superuser',s.rolsuper,
      'bypassrls',s.rolbypassrls,'createrole_attribute',s.rolcreaterole,
      'can_create_schemas',pg_catalog.has_database_privilege(current_user,current_database(),'CREATE'),
      'can_alter_customers_as_owner',(SELECT coalesce(s.rolsuper OR pg_catalog.pg_has_role(s.role_oid,t.relowner,'USAGE'),false)
        FROM tables t WHERE t.table_name='customers'),
      'can_manage_customer_sequence_as_owner',(SELECT coalesce(s.rolsuper OR pg_catalog.pg_has_role(s.role_oid,t.relowner,'USAGE'),false)
        FROM inspected_relations t WHERE t.relname='customers_id_seq' AND t.relkind='S'),
      'can_manage_public_schema_as_owner',(SELECT s.rolsuper OR pg_catalog.pg_has_role(s.role_oid,n.nspowner,'USAGE')
        FROM pg_catalog.pg_namespace n WHERE n.nspname='public'),
      'inherits_definer_owner',(SELECT pg_catalog.pg_has_role(s.role_oid,r.oid,'USAGE') FROM roles r
        WHERE r.role_name='glam_v2_authz_owner' AND r.oid IS NOT NULL),
      'can_set_definer_owner',(SELECT CASE WHEN s.version_num>=160000 THEN pg_catalog.pg_has_role(s.role_oid,r.oid,'SET')
        ELSE pg_catalog.pg_has_role(s.role_oid,r.oid,'MEMBER') END FROM roles r
        WHERE r.role_name='glam_v2_authz_owner' AND r.oid IS NOT NULL),
      'api_potential_administrative_paths',(
        WITH RECURSIVE reachable(api_role,role_id) AS (
          SELECT oid,oid FROM pg_catalog.pg_roles WHERE rolname IN ('anon','authenticated','service_role','authenticator')
          UNION
          SELECT p.api_role,m.roleid FROM reachable p JOIN pg_catalog.pg_auth_members m ON m.member=p.role_id
          WHERE coalesce((to_jsonb(m)->>'inherit_option')::boolean,true)
             OR coalesce((to_jsonb(m)->>'set_option')::boolean,true) OR m.admin_option
        )
        SELECT coalesce(jsonb_agg(jsonb_build_object('api_role',pg_catalog.pg_get_userbyid(p.api_role),
          'reachable_trusted_role',t.rolname)),'[]'::jsonb)
        FROM reachable p JOIN pg_catalog.pg_roles t ON t.oid=p.role_id
        WHERE t.rolname IN ('postgres','glam_v2_backend','glam_v2_authz_owner')
      ),
      'interpretation','Non-superuser migration needs owner DDL/ACL control, authoritative count visibility, schema CREATE, PL/pgSQL USAGE, and explicit INHERIT/SET capability for the definer owner. Role provisioning is separate; managed restrictions cannot be proven by SELECT flags alone.')
    FROM server s
)
SELECT section,evidence FROM report ORDER BY section;
