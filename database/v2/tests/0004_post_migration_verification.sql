-- READ-ONLY, after separately approved 0004 revision-2 execution AND owner-grant cleanup.
-- NEW glam-by-mariya-v2 only. One SELECT; no role creation, mutation or Auth calls.
-- Do not invoke revoke_my_sessions as a verification query: it is a write operation.
-- Export every section/evidence row. Compare ACL/constraint/policy evidence to
-- CUSTOMER_AUTH_MIGRATION_PROCEDURE.md and the exact approved migration.
-- Fingerprints below are comparison checks, NOT artifact approval hashes.
-- Reviewed against 0001/0002 + approved 0004 revision 2 on PostgreSQL 18.6.
-- Approved 0004 SHA256: 7A089ACC1B64447B3ED5D24142A73C2D966EA7D9B572489267B28594987DE84A
-- NOT NULL is checked via attnotnull, not PostgreSQL 18-only contype='n'.
-- pg_get_* rendering/search_path differences on 17 MUST stop for definition
-- review, never silently regenerate expectations from the hosted database.
WITH
expected_tables(name) AS (VALUES
    ('makeup_artists'),('services'),('artist_availability'),('blocked_slots'),
    ('customers'),('reservations'),('admin_users'),('account_identities'),
    ('account_security_generations'),('auth_mvc_signup_attempts')
),
private_names(name) AS (VALUES
    ('customers'),('admin_users'),('account_identities'),('account_security_generations'),('auth_mvc_signup_attempts')
),
objects AS (
    SELECT e.name,c.oid,c.relkind,c.relowner,c.relrowsecurity,c.relforcerowsecurity
    FROM expected_tables e LEFT JOIN pg_namespace n ON n.nspname='public'
    LEFT JOIN pg_class c ON c.relnamespace=n.oid AND c.relname=e.name
),
private_tables AS (SELECT o.* FROM objects o JOIN private_names p USING(name)),
expected_manifest(kind,parent,total,digest) AS (VALUES
    ('columns','account_identities',8,'c014bbe7d157ec7c3c17dcc08e3c81a2'),
    ('columns','account_security_generations',2,'b3966a5a5e5fd929c974fc5fab218d75'),
    ('columns','admin_users',10,'2956d7b3f41f0ecb73411be19a2e3e5e'),
    ('columns','artist_availability',5,'d6ab4a1d890ebd0cbf03a84835c43ef8'),
    ('columns','auth_mvc_signup_attempts',12,'841f6828efb8f97ccf4e1c541b7c8f35'),
    ('columns','blocked_slots',5,'e1acd1a50dc28a4ea8517574b3cc5705'),
    ('columns','customers',17,'134f8544bf076f445d31f4fa00f84919'),
    ('columns','makeup_artists',4,'89dc171f3173590ed51bc1ce2ec50425'),
    ('columns','reservations',13,'9a71e8419a52432682ae0ff470682caf'),
    ('columns','services',6,'56f37dfae75e4f7352b5a33ca7d204cf'),
    ('constraints','account_identities',12,'1eb30d439368ed7e325914e0a106eb3e'),
    ('constraints','account_security_generations',3,'186274915187f21cc1ecad677c0a7d7e'),
    ('constraints','admin_users',7,'f16b8ae2f39657df1a263869a83e21fc'),
    ('constraints','artist_availability',4,'75fff72bfd364e2b2a73fedd371f3dd4'),
    ('constraints','auth_mvc_signup_attempts',13,'ceba2e76860289459ca69e7e67c65976'),
    ('constraints','blocked_slots',3,'5dd11135c71de2a656c3c860c36cdff1'),
    ('constraints','customers',13,'e0a0072dbf6b5261f4f104b8bda811b4'),
    ('constraints','makeup_artists',1,'fa458ce60a100ba2187827910f705236'),
    ('constraints','reservations',9,'3194bc972a3350928762e2dde28318b5'),
    ('constraints','services',3,'1f3263bfd8153e54aecb309307f58326'),
    ('functions','v2_authz_api',1,'de242904d9edbb49997350ecaffc0d04'),
    ('functions','v2_authz_private',5,'d1e56c15739fe41399983391498e73f1'),
    ('indexes','account_identities',5,'05c17c811161b9d38754966382655439'),
    ('indexes','account_security_generations',1,'974fde11871c273bb8a36a44ff013ed9'),
    ('indexes','admin_users',4,'1c8f2ded3de2538166b18c86e73dbb5d'),
    ('indexes','artist_availability',1,'1cdf129b88defe239cdf6650d7db4fa9'),
    ('indexes','auth_mvc_signup_attempts',4,'c0176d2a389b4a4232d7db63dde57987'),
    ('indexes','blocked_slots',1,'7a4283aabcfb215ab1399e2edf6cd131'),
    ('indexes','customers',4,'3ee5fe867ec9323c3c48cfb4129a6a80'),
    ('indexes','makeup_artists',1,'498add323ded66c95cc450c93ec21383'),
    ('indexes','reservations',6,'69f1b32d9352f90712b3a90fad1a13b2'),
    ('indexes','services',1,'a41bbe58c3fa48b30443928aa285fb4f'),
    ('policies','account_identities',3,'476ed2eae31d2dfc3e3dbfb40e7d2dd8'),
    ('policies','account_security_generations',4,'b6aba318bb3e8a53ee0c8e5464e0e10a'),
    ('policies','admin_users',3,'d132c05e141f371fa416e94a9d6b9e5c'),
    ('policies','auth_mvc_signup_attempts',3,'d1b445af3ce6b218e9030d72910c8141'),
    ('policies','customers',4,'d3b0a863942bddacbde1b7f879d90724'),
    ('tables','account_identities',1,'9c7dce8f98aba68b2fdae76d636aee13'),
    ('tables','account_security_generations',1,'53bf4faf52591914621b8fab8c0d72c7'),
    ('tables','admin_users',1,'921502350eff3d00ad56af55228ba5aa'),
    ('tables','artist_availability',1,'40d7165faf857c85f8c267d42063625c'),
    ('tables','auth_mvc_signup_attempts',1,'1dee808245fef398898236f3e160dbf2'),
    ('tables','blocked_slots',1,'261bbbfe26c42f1da79506649eb50ec2'),
    ('tables','customers',1,'6fc7a3573abce613ec6cca0c1360aa0b'),
    ('tables','makeup_artists',1,'a61c0e388ad15993cd10d3a5f5253d7b'),
    ('tables','reservations',1,'8ae0ce9698085cf92a2766c2c5e0ba81'),
    ('tables','services',1,'80c8007c8faac322256b33955a351221'),
    ('triggers','account_identities',1,'d6b9cd1b2f0a4d987c3c4bd641eeb76f'),
    ('triggers','account_security_generations',1,'12752b6cc782d13133cad771cb79a04e'),
    ('triggers','admin_users',2,'47a45f2feb2b4bc67f4c096517d87993'),
    ('triggers','auth_mvc_signup_attempts',1,'9e5577be5a696f2ea79a5a0a5d429806'),
    ('triggers','customers',2,'63148f85533d77f623424251698c9c59')
),
-- Full structural evidence, including foundation preservation and function
-- bodies. No database-specific OIDs are fingerprinted. Missing, changed AND
-- extra objects fail (builtin argument-type OIDs are stable PostgreSQL types).
structural_evidence(kind,parent,name,definition) AS (
    SELECT 'tables',c.name,c.name,jsonb_build_object(
      'owner',pg_get_userbyid(c.relowner),'rls',c.relrowsecurity,'force',c.relforcerowsecurity)
    FROM objects c WHERE c.oid IS NOT NULL
    UNION ALL SELECT 'columns',c.name,a.attname,jsonb_build_object(
      'type',format_type(a.atttypid,a.atttypmod),'not_null',a.attnotnull,'identity',a.attidentity,
      'default',pg_get_expr(d.adbin,d.adrelid))
    FROM objects c JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
    LEFT JOIN pg_attrdef d ON d.adrelid=c.oid AND d.adnum=a.attnum
    UNION ALL SELECT 'constraints',t.name,c.conname,jsonb_build_object(
      'type',c.contype,'validated',c.convalidated,'deferrable',c.condeferrable,'deferred',c.condeferred,
      'definition',pg_get_constraintdef(c.oid,false))
    FROM objects t JOIN pg_constraint c ON c.conrelid=t.oid WHERE c.contype<>'n'
    UNION ALL SELECT 'indexes',t.name,c.relname,jsonb_build_object(
      'valid',i.indisvalid,'ready',i.indisready,'live',i.indislive,'definition',pg_get_indexdef(c.oid))
    FROM objects t JOIN pg_index i ON i.indrelid=t.oid JOIN pg_class c ON c.oid=i.indexrelid
    UNION ALL SELECT 'triggers',t.name,g.tgname,jsonb_build_object(
      'enabled',g.tgenabled,'definition',pg_get_triggerdef(g.oid,false))
    FROM objects t JOIN pg_trigger g ON g.tgrelid=t.oid WHERE NOT g.tgisinternal
    UNION ALL SELECT 'policies',t.name,p.polname,jsonb_build_object(
      'command',p.polcmd,'permissive',p.polpermissive,
      'roles',(SELECT jsonb_agg(pg_get_userbyid(x) ORDER BY pg_get_userbyid(x)) FROM unnest(p.polroles) x),
      'using',pg_get_expr(p.polqual,p.polrelid,false),'check',pg_get_expr(p.polwithcheck,p.polrelid,false))
    FROM objects t JOIN pg_policy p ON p.polrelid=t.oid
    UNION ALL SELECT 'functions',n.nspname,p.proname,jsonb_build_object(
      'args',p.proargtypes::text,'arg_names',p.proargnames,'arg_modes',p.proargmodes,'arg_defaults',p.pronargdefaults,
      'source',replace(p.prosrc,chr(13),''),
      'owner',pg_get_userbyid(p.proowner),'security_definer',p.prosecdef,
      'config',p.proconfig,'returns',format_type(p.prorettype,NULL),'language',l.lanname,
      'kind',p.prokind,'strict',p.proisstrict,'volatility',p.provolatile,'parallel',p.proparallel,
      'leakproof',p.proleakproof)
    FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace JOIN pg_language l ON l.oid=p.prolang
    WHERE n.nspname IN ('v2_authz_private','v2_authz_api')
),
actual_manifest AS (
    SELECT kind,parent,count(*) AS total,
      md5(jsonb_agg(jsonb_build_object('name',name,'definition',definition) ORDER BY name COLLATE "C")::text) AS digest
    FROM structural_evidence GROUP BY kind,parent
),
manifest_mismatches AS (
    SELECT coalesce(e.kind,a.kind) AS kind,coalesce(e.parent,a.parent) AS parent,
      e.total AS expected_count,a.total AS actual_count,e.digest AS expected_digest,a.digest AS actual_digest
    FROM expected_manifest e FULL JOIN actual_manifest a USING(kind,parent)
    WHERE e.total IS DISTINCT FROM a.total OR e.digest IS DISTINCT FROM a.digest
),
expected_functions(schema_name,name,security_definer,argument_types,return_type) AS (VALUES
    ('v2_authz_private','initialize_identity',true,'','trigger'),
    ('v2_authz_private','guard_owner',false,'','trigger'),
    ('v2_authz_private','guard_identity_security',false,'','trigger'),
    ('v2_authz_private','guard_generation',false,'','trigger'),
    ('v2_authz_private','guard_signup_attempt',false,'','trigger'),
    ('v2_authz_api','revoke_my_sessions',true,'2950 2950 25 20 20','bigint')
),
functions AS (
    SELECT e.*,p.oid,pg_get_userbyid(p.proowner) AS owner,p.prosecdef,p.proconfig,
      coalesce(p.oid IS NOT NULL AND pg_get_userbyid(p.proowner)='glam_v2_authz_owner'
        AND p.prosecdef=e.security_definer AND format_type(p.prorettype,NULL)=e.return_type
        AND p.proconfig=ARRAY['search_path=pg_catalog']::text[],false) AS matches
    FROM expected_functions e LEFT JOIN pg_namespace n ON n.nspname=e.schema_name
    LEFT JOIN pg_proc p ON p.pronamespace=n.oid AND p.proname=e.name
      AND p.proargtypes::text=e.argument_types AND p.prokind='f'
),
all_auth_functions AS (
    SELECT p.oid,n.nspname AS schema_name,p.proname AS name,p.prokind,
      pg_get_function_identity_arguments(p.oid) AS arguments,
      pg_get_userbyid(p.proowner) AS owner,p.prosecdef,p.proconfig
    FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
    WHERE n.nspname IN ('v2_authz_private','v2_authz_api')
),
api_roles AS (
    SELECT oid,rolname,rolsuper,rolcreaterole FROM pg_roles
    WHERE rolname IN ('anon','authenticated','service_role','authenticator')
),
api_effective_roles AS (
    SELECT a.rolname AS api_role,r.oid,r.rolname
    FROM api_roles a CROSS JOIN pg_roles r WHERE pg_has_role(a.oid,r.oid,'SET')
),
api_paths AS (
    WITH RECURSIVE reachable(api_role,role_id) AS (
      SELECT oid,oid FROM api_roles
      UNION
      SELECT p.api_role,m.roleid FROM reachable p JOIN pg_auth_members m ON m.member=p.role_id
      WHERE m.inherit_option OR m.set_option OR m.admin_option
    )
    SELECT pg_get_userbyid(p.api_role) AS api_role,t.rolname AS reachable_role
    FROM reachable p JOIN pg_roles t ON t.oid=p.role_id
    WHERE t.rolname IN ('postgres','glam_v2_backend','glam_v2_authz_owner')
),
api_rights AS (
    SELECT a.api_role,a.rolname,t.name,
      has_table_privilege(a.oid,t.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,MAINTAIN')
      OR has_any_column_privilege(a.oid,t.oid,'SELECT,INSERT,UPDATE,REFERENCES') AS any_private_right
    FROM api_effective_roles a CROSS JOIN private_tables t WHERE t.oid IS NOT NULL
),
api_function_rights AS (
    SELECT a.api_role,a.rolname,f.schema_name,f.name,has_function_privilege(a.oid,f.oid,'EXECUTE') AS can_execute
    FROM api_effective_roles a CROSS JOIN all_auth_functions f
),
backend_columns AS (
    SELECT t.name,a.attname,p.privilege
    FROM private_tables t JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum>0 AND NOT a.attisdropped
    CROSS JOIN (VALUES('SELECT'),('INSERT'),('UPDATE'),('REFERENCES')) p(privilege)
    WHERE has_column_privilege('glam_v2_backend',t.oid,a.attnum,p.privilege)
),
expected_column_grants(role_name,table_name,privilege,columns) AS (VALUES
    ('glam_v2_backend','customers','INSERT','app_id,email,first_name,last_name,full_name,age_at_registration,gender,address,password_hash,is_active,suffix,birthday'),
    ('glam_v2_backend','customers','UPDATE','password_hash,security_stamp'),
    ('glam_v2_backend','admin_users','UPDATE','app_id'),
    ('glam_v2_backend','account_identities','UPDATE','auth_state,supabase_user_id,credential_epoch,credential_sync_state'),
    ('glam_v2_backend','account_security_generations','INSERT','account_id,generation'),
    ('glam_v2_backend','account_security_generations','UPDATE','generation'),
    ('glam_v2_backend','auth_mvc_signup_attempts','INSERT','attempt_id,browser_secret_hash,normalized_email,encrypted_profile,state,expires_at'),
    ('glam_v2_backend','auth_mvc_signup_attempts','UPDATE','state,provider_subject,next_resend_at,operation_id,consumed_operation_id,verification_attempts'),
    ('glam_v2_authz_owner','account_identities','INSERT','id,customer_id,admin_id,login'),
    ('glam_v2_authz_owner','account_identities','UPDATE','id'),
    ('glam_v2_authz_owner','customers','UPDATE','security_stamp'),
    ('glam_v2_authz_owner','admin_users','UPDATE','app_id'),
    ('glam_v2_authz_owner','account_security_generations','UPDATE','generation')
),
column_right_mismatches AS (
    SELECT r.rolname,t.name,a.attname,p.privilege,
      has_column_privilege(r.oid,t.oid,a.attnum,p.privilege) AS actual,
      (p.privilege='SELECT' AND (r.rolname='glam_v2_backend' OR t.name<>'auth_mvc_signup_attempts'))
        OR EXISTS(SELECT 1 FROM expected_column_grants e WHERE e.role_name=r.rolname
          AND e.table_name=t.name AND e.privilege=p.privilege AND a.attname=ANY(string_to_array(e.columns,','))) AS expected
    FROM pg_roles r CROSS JOIN private_tables t
    JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum>0 AND NOT a.attisdropped
    CROSS JOIN (VALUES('SELECT'),('INSERT'),('UPDATE'),('REFERENCES')) p(privilege)
    WHERE r.rolname IN ('glam_v2_backend','glam_v2_authz_owner')
),
target_memberships AS (
    SELECT m.*,pg_get_userbyid(m.roleid) AS target_role,
      pg_get_userbyid(m.member) AS member_name,pg_get_userbyid(m.grantor) AS grantor_name
    FROM pg_auth_members m
    WHERE m.roleid IN (SELECT oid FROM pg_roles WHERE rolname IN ('glam_v2_backend','glam_v2_authz_owner'))
       OR m.member IN (SELECT oid FROM pg_roles WHERE rolname IN ('glam_v2_backend','glam_v2_authz_owner'))
),
count_targets AS (
    SELECT t.*,coalesce(t.relkind='r' AND NOT row_security_active(t.oid)
      AND has_table_privilege(current_user,t.oid,'SELECT'),false) AS authoritative
    FROM objects t WHERE t.name IN (SELECT name FROM private_names) OR t.name='reservations'
),
counts AS (
    SELECT t.name,t.authoritative,x.row_count
    FROM count_targets t LEFT JOIN LATERAL XMLTABLE('/table/row' PASSING
      CASE WHEN t.authoritative THEN query_to_xml(
        format('SELECT count(*) AS row_count FROM public.%I',t.name),false,false,'')
        ELSE '<table/>'::xml END COLUMNS row_count bigint PATH 'row_count') x ON true
),
report(section,evidence) AS (
    SELECT '01_context',jsonb_build_object(
      'current_user',current_user,'session_user',session_user,'database',current_database(),
      'server_version',current_setting('server_version'),
      'operator_and_version_ok',current_user='postgres' AND session_user='postgres'
        AND current_setting('server_version_num')::integer>=170000,
      'scope','metadata only; verify new V2 project reference separately; no runtime activation')
    UNION ALL SELECT '02_tables_rls_and_owners',jsonb_build_object(
      'all_ten_force_rls',coalesce(bool_and(oid IS NOT NULL AND relkind='r' AND relrowsecurity AND relforcerowsecurity),false),
      'definition_mismatches',(SELECT coalesce(jsonb_agg(to_jsonb(m)),'[]'::jsonb) FROM manifest_mismatches m WHERE kind='tables'),
      'tables',jsonb_agg(to_jsonb(t)||jsonb_build_object('owner',pg_get_userbyid(t.relowner)) ORDER BY name))
    FROM objects t
    UNION ALL SELECT '03_empty_counts',jsonb_build_object(
      'all_six_authoritatively_empty',coalesce(bool_and(authoritative AND row_count=0),false),
      'counts',jsonb_agg(to_jsonb(c) ORDER BY name)) FROM counts c
    UNION ALL SELECT '04_roles_and_cleanup',jsonb_build_object(
      'restricted_roles_ok',(SELECT count(*)=2 AND bool_and(NOT rolsuper AND NOT rolbypassrls
        AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolinherit
        AND rolcanlogin=(rolname='glam_v2_backend')) FROM pg_roles WHERE rolname IN ('glam_v2_backend','glam_v2_authz_owner')),
      'bootstrap_ok',EXISTS(SELECT 1 FROM pg_roles WHERE oid=10 AND rolname='supabase_admin' AND rolsuper),
      'exactly_bootstrap_rows_ok',(SELECT count(*)=2 AND bool_and(member_name='postgres' AND grantor=10
        AND grantor_name='supabase_admin' AND admin_option AND NOT inherit_option AND NOT set_option) FROM target_memberships),
      'no_temporary_rows_ok',NOT EXISTS(SELECT 1 FROM target_memberships WHERE grantor_name='postgres'),
      'roles',(SELECT jsonb_agg(jsonb_build_object('name',r.rolname,'login',r.rolcanlogin,'inherit',r.rolinherit,
        'superuser',r.rolsuper,'bypassrls',r.rolbypassrls,'createdb',r.rolcreatedb,
        'createrole',r.rolcreaterole,'replication',r.rolreplication))
        FROM pg_roles r WHERE r.rolname IN ('glam_v2_backend','glam_v2_authz_owner')),
      'memberships',(SELECT coalesce(jsonb_agg(jsonb_build_object('role',pg_get_userbyid(m.roleid),
        'member',pg_get_userbyid(m.member),'grantor',pg_get_userbyid(m.grantor),
        'admin',m.admin_option,'inherit',m.inherit_option,'set',m.set_option)),'[]'::jsonb)
        FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.member OR r.oid=m.roleid
        WHERE r.rolname IN ('glam_v2_backend','glam_v2_authz_owner')),
      'postgres_inherits_backend',pg_has_role('postgres','glam_v2_backend','USAGE'),
      'postgres_can_set_backend',pg_has_role('postgres','glam_v2_backend','SET'),
      'postgres_inherits_owner',pg_has_role('postgres','glam_v2_authz_owner','USAGE'),
      'postgres_can_set_owner',pg_has_role('postgres','glam_v2_authz_owner','SET'))
    UNION ALL SELECT '05_schema_and_function_ownership',jsonb_build_object(
      'both_schema_owners_ok',(SELECT count(*)=2 AND bool_and(pg_get_userbyid(nspowner)='glam_v2_authz_owner')
        FROM pg_namespace WHERE nspname IN ('v2_authz_private','v2_authz_api')),
      'definition_mismatches',(SELECT coalesce(jsonb_agg(to_jsonb(m)),'[]'::jsonb) FROM manifest_mismatches m WHERE kind='functions'),
      'function_definitions',(SELECT jsonb_agg(to_jsonb(s) ORDER BY parent,name) FROM structural_evidence s WHERE kind='functions'),
      'schemas',(SELECT jsonb_agg(jsonb_build_object('name',nspname,'owner',pg_get_userbyid(nspowner),'acl',nspacl))
        FROM pg_namespace WHERE nspname IN ('v2_authz_private','v2_authz_api')),
      'all_six_functions_match',coalesce(bool_and(f.matches),false),
      'exactly_six_auth_routines',(SELECT count(*)=6 FROM all_auth_functions),
      'routine_inventory',(SELECT jsonb_agg(to_jsonb(a) ORDER BY schema_name,name) FROM all_auth_functions a),
      'functions',jsonb_agg(to_jsonb(f) ORDER BY schema_name,name)) FROM functions f
    UNION ALL SELECT '06_triggers_and_policies',jsonb_build_object(
      'definition_mismatches',(SELECT coalesce(jsonb_agg(to_jsonb(m)),'[]'::jsonb) FROM manifest_mismatches m WHERE kind IN ('triggers','policies')),
      'triggers',(SELECT jsonb_agg(jsonb_build_object('table',t.name,'name',g.tgname,
        'enabled',g.tgenabled,'definition',pg_get_triggerdef(g.oid)) ORDER BY t.name,g.tgname)
        FROM objects t JOIN pg_trigger g ON g.tgrelid=t.oid WHERE NOT g.tgisinternal),
      'policies',(SELECT jsonb_agg(jsonb_build_object('table',t.name,'name',p.polname,'command',p.polcmd,
        'permissive',p.polpermissive,
        'roles',(SELECT jsonb_agg(pg_get_userbyid(x)) FROM unnest(p.polroles) x),
        'using',pg_get_expr(p.polqual,p.polrelid),'check',pg_get_expr(p.polwithcheck,p.polrelid))
        ORDER BY t.name,p.polname) FROM objects t JOIN pg_policy p ON p.polrelid=t.oid))
    UNION ALL SELECT '07_backend_effective_privileges',jsonb_build_object(
      'backend_and_owner_column_mismatches',(SELECT coalesce(jsonb_agg(to_jsonb(c)),'[]'::jsonb)
        FROM column_right_mismatches c WHERE actual IS DISTINCT FROM expected),
      'owner_broad_right_violations',(SELECT coalesce(jsonb_agg(t.name),'[]'::jsonb) FROM private_tables t
        WHERE has_table_privilege('glam_v2_authz_owner',t.oid,'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,MAINTAIN')),
      'owner_select_ok',(SELECT bool_and(has_table_privilege('glam_v2_authz_owner',t.oid,'SELECT')=(t.name<>'auth_mvc_signup_attempts')) FROM private_tables t),
      'backend_select_ok',(SELECT bool_and(has_table_privilege('glam_v2_backend',t.oid,'SELECT')) FROM private_tables t),
      'columns',(SELECT jsonb_agg(to_jsonb(c) ORDER BY name,attname,privilege) FROM backend_columns c),
      'broad_mutation_violations',(SELECT coalesce(jsonb_agg(t.name),'[]'::jsonb) FROM private_tables t
        WHERE has_table_privilege('glam_v2_backend',t.oid,'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,MAINTAIN')),
      'customer_sequence_usage',has_sequence_privilege('glam_v2_backend','public.customers_id_seq','USAGE'),
      'customer_sequence_select_or_update',has_sequence_privilege('glam_v2_backend','public.customers_id_seq','SELECT,UPDATE'),
      'admin_sequence_any',has_sequence_privilege('glam_v2_backend','public.admin_users_id_seq','USAGE,SELECT,UPDATE'),
      'owner_sequence_any',has_sequence_privilege('glam_v2_authz_owner','public.customers_id_seq','USAGE,SELECT,UPDATE')
        OR has_sequence_privilege('glam_v2_authz_owner','public.admin_users_id_seq','USAGE,SELECT,UPDATE'),
      'required_schema_usage_ok',has_schema_privilege('glam_v2_backend','public','USAGE')
        AND has_schema_privilege('glam_v2_backend','v2_authz_api','USAGE')
        AND has_schema_privilege('glam_v2_authz_owner','public','USAGE'),
      'only_revocation_execute_ok',(SELECT count(*)=6 AND bool_and(
        has_function_privilege('glam_v2_backend',f.oid,'EXECUTE')
          =(f.schema_name='v2_authz_api' AND f.name='revoke_my_sessions')) FROM all_auth_functions f),
      'functions',(SELECT jsonb_agg(jsonb_build_object('schema',f.schema_name,'name',f.name,
        'execute',has_function_privilege('glam_v2_backend',f.oid,'EXECUTE'))) FROM all_auth_functions f),
      'public_schema_create',has_schema_privilege('glam_v2_backend','public','CREATE'),
      'private_schema_access',has_schema_privilege('glam_v2_backend','v2_authz_private','USAGE,CREATE'),
      'api_schema_create',has_schema_privilege('glam_v2_backend','v2_authz_api','CREATE'))
    UNION ALL SELECT '08_untrusted_access',jsonb_build_object(
      'required_api_roles_present',(SELECT count(*)=4 FROM api_roles),
      'api_administrative_paths',(SELECT coalesce(jsonb_agg(to_jsonb(p)),'[]'::jsonb) FROM api_paths p),
      'api_elevated_roles',(SELECT coalesce(jsonb_agg(rolname),'[]'::jsonb) FROM api_roles WHERE rolsuper OR rolcreaterole),
      'table_or_column_violations',(SELECT coalesce(jsonb_agg(to_jsonb(a)),'[]'::jsonb) FROM api_rights a WHERE any_private_right),
      'function_violations',(SELECT coalesce(jsonb_agg(to_jsonb(f)),'[]'::jsonb) FROM api_function_rights f WHERE can_execute),
      'schema_violations',(SELECT coalesce(jsonb_agg(jsonb_build_object('role',a.rolname,'schema',n.nspname)),'[]'::jsonb)
        FROM api_effective_roles a CROSS JOIN pg_namespace n WHERE n.nspname IN ('v2_authz_private','v2_authz_api')
          AND has_schema_privilege(a.oid,n.oid,'USAGE,CREATE')),
      'public_schema_create_violations',(SELECT coalesce(jsonb_agg(to_jsonb(a)),'[]'::jsonb)
        FROM api_effective_roles a WHERE has_schema_privilege(a.oid,'public','CREATE')),
      'sequence_violations',(SELECT coalesce(jsonb_agg(jsonb_build_object('role',a.rolname,'sequence',c.relname)),'[]'::jsonb)
        FROM api_effective_roles a CROSS JOIN pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public' AND c.relkind='S' AND c.relname IN ('customers_id_seq','admin_users_id_seq')
          AND has_sequence_privilege(a.oid,c.oid,'USAGE,SELECT,UPDATE')))
    UNION ALL SELECT '09_constraints_and_indexes',jsonb_build_object(
      'disabled_constraint_trigger_violations',(SELECT coalesce(jsonb_agg(jsonb_build_object(
        'table',t.name,'trigger',g.tgname,'enabled',g.tgenabled)),'[]'::jsonb)
        FROM objects t JOIN pg_trigger g ON g.tgrelid=t.oid WHERE g.tgisinternal AND g.tgconstraint<>0 AND g.tgenabled<>'O'),
      'definition_mismatches',(SELECT coalesce(jsonb_agg(to_jsonb(m)),'[]'::jsonb)
        FROM manifest_mismatches m WHERE kind IN ('columns','constraints','indexes')),
      'complete_structural_evidence',(SELECT jsonb_agg(to_jsonb(s) ORDER BY kind,parent,name)
        FROM structural_evidence s WHERE kind IN ('columns','constraints','indexes')),
      'columns',(SELECT jsonb_agg(jsonb_build_object('table',t.name,'column',a.attname,
        'type',format_type(a.atttypid,a.atttypmod),'not_null',a.attnotnull,
        'default',pg_get_expr(d.adbin,d.adrelid),'identity',a.attidentity) ORDER BY t.name,a.attnum)
        FROM private_tables t JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum>0 AND NOT a.attisdropped
        LEFT JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum),
      'constraints',(SELECT jsonb_agg(jsonb_build_object('table',t.name,'name',c.conname,'validated',c.convalidated,
        'definition',pg_get_constraintdef(c.oid,true)) ORDER BY t.name,c.conname)
        FROM private_tables t JOIN pg_constraint c ON c.conrelid=t.oid),
      'indexes',(SELECT jsonb_agg(jsonb_build_object('table',t.name,'valid',i.indisvalid,'ready',i.indisready,
        'definition',pg_get_indexdef(i.indexrelid)) ORDER BY t.name,i.indexrelid)
        FROM private_tables t JOIN pg_index i ON i.indrelid=t.oid),
      'reservation_exclusion',(SELECT jsonb_build_object('validated',c.convalidated,'type',c.contype,
        'definition',pg_get_constraintdef(c.oid,true)) FROM pg_constraint c JOIN objects t ON t.oid=c.conrelid
        WHERE t.name='reservations' AND c.conname='reservations_artist_reserved_overlap'),
      'required_comparison','Compare the COMPLETE reservation exclusion with its pre-migration snapshot, not just its name.')
    UNION ALL SELECT '10_payment_absence_and_history',jsonb_build_object(
      'payment_objects_absent',to_regclass('public.payments') IS NULL AND to_regclass('public.payment_revisions') IS NULL,
      'payment_tables',jsonb_build_object('payments',to_regclass('public.payments')::text,
        'payment_revisions',to_regclass('public.payment_revisions')::text),
      'history_note','0004 does not write Supabase/EF history. Record approval, hash, successful commit, cleanup and verification in the agreed deployment ledger separately.')
)
SELECT section,evidence FROM report ORDER BY section;
