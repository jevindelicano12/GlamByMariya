"""Offline verifier fault tests. No listener, new roles, or migration execution.

Copies an existing STOPPED synthetic PG18 fixture under workspace/temp. Never
point this at hosted/real database files. Keeps the copy and logs for inspection.
Usage: python database/v2/tests/check_0004_verifier.py
"""
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[3]
BIN = Path(r"C:\Program Files\PostgreSQL\18\bin")
SOURCE = ROOT / "temp/v2-role-compatibility-20261116-3/data"
WORK = ROOT / "temp" / ("0004-verifier-checks-" + uuid.uuid4().hex[:8])
DATA = WORK / "data"
SQL = (ROOT / "database/v2/tests/0004_post_migration_verification.sql").read_text(encoding="utf-8")
CLEANUP = (ROOT / "database/v2/tests/0004_owner_membership_cleanup_verification.sql").read_text(encoding="utf-8")
PROCEDURE = (ROOT / "database/v2/0004_EXECUTION_READINESS.md").read_text(encoding="utf-8")
HASH = "7A089ACC1B64447B3ED5D24142A73C2D966EA7D9B572489267B28594987DE84A"
assert hashlib.sha256((ROOT / "database/v2/migrations/0004_customer_auth_compatibility.sql").read_bytes()).hexdigest().upper() == HASH
status = subprocess.run([str(BIN / "pg_ctl.exe"), "-D", str(SOURCE), "status"], capture_output=True, text=True)
assert status.returncode == 3 or "no server running" in status.stdout, status.stdout + status.stderr
assert not (SOURCE / "postmaster.pid").exists()
assert not any(p.is_symlink() for p in SOURCE.rglob("*"))
shutil.copytree(SOURCE, DATA)


def run(label, sql, expected_error=False):
    result = subprocess.run(
        [str(BIN / "postgres.exe"), "--single", "-D", str(DATA), "-j", "glam_v2_role_prerequisite_test"],
        input=sql + "\n\n", text=True, capture_output=True, timeout=30,
    )
    (WORK / (label + ".log")).write_text(result.stdout + result.stderr, encoding="utf-8")
    assert result.returncode == 0 and ("ERROR:" in result.stderr) == expected_error and "FATAL:" not in result.stderr, label + ": " + result.stderr[:1200]
    payloads = re.findall(r'payload = "([0-9a-f]+)"', result.stdout)
    return [json.loads(bytes.fromhex(p)) for p in payloads]


def encoded(query):
    return "SELECT encode(convert_to((" + query.rstrip().removesuffix(";") + ")::text,'UTF8'),'hex') AS payload;"


report = SQL[:SQL.rindex("SELECT section,evidence FROM report")]
report += encoded("SELECT jsonb_object_agg(section,evidence) FROM report")
cleanup = CLEANUP[:CLEANUP.rindex("SELECT to_jsonb(c)")]
cleanup += encoded("SELECT to_jsonb(c) FROM checks c")
# The retained fixture deliberately has a failure-injection trigger and fake MVC
# data. Remove these only inside each private-copy transaction, then roll back.
normalize = """
BEGIN;
DROP TRIGGER fixture_fail_customer_auth ON public.customers;
TRUNCATE public.customers,public.admin_users,public.account_identities,
 public.account_security_generations,public.auth_mvc_signup_attempts,public.reservations CASCADE;
SET SESSION AUTHORIZATION postgres;
"""


def verify(label, fault=""):
    return run(label, normalize + "SET SESSION AUTHORIZATION supabase_admin;\n" + fault
               + "\nSET SESSION AUTHORIZATION postgres;\n" + report + "\nROLLBACK;")[0]


baseline = verify("baseline")
assert len(baseline) == 10
migration = (ROOT / "database/v2/migrations/0004_customer_auth_compatibility.sql").read_text(encoding="utf-8")
for function in baseline["05_schema_and_function_ownership"]["function_definitions"]:
    match = re.search(r"CREATE FUNCTION " + re.escape(function["parent"] + "." + function["name"])
                      + r"\(.*?AS \$\$(.*?)\$\$;", migration, re.S)
    assert match and match[1] == function["definition"]["source"], function["name"]
assert len(re.findall(r"CREATE TRIGGER ", migration)) == 7
assert len(re.findall(r"CREATE POLICY ", migration)) == 17
for kind, pattern in (
    ("triggers", r"CREATE TRIGGER (\w+).*?ON public\.(\w+).*?;"),
    ("policies", r"CREATE POLICY (\w+) ON public\.(\w+)"),
):
    expected = {(table, name) for name, table in re.findall(pattern, migration, re.S)}
    actual = {(item["table"], item["name"]) for item in baseline["06_triggers_and_policies"][kind]}
    assert expected == actual, kind
structures = baseline["09_constraints_and_indexes"]["complete_structural_evidence"]
assert {kind: sum(s["kind"] == kind for s in structures) for kind in ("columns", "constraints", "indexes")} == {
    "columns": 82, "constraints": 68, "indexes": 28,
}
for section, evidence in baseline.items():
    for key, value in evidence.items():
        if key.endswith("_ok") or key.startswith("all_") or key == "exactly_six_auth_routines" or key == "required_api_roles_present":
            assert value is True, (section, key, value)
        if "mismatches" in key or "violations" in key or key == "api_administrative_paths":
            assert value == [], (section, key, value)
assert all(baseline["04_roles_and_cleanup"][k] is False for k in (
    "postgres_inherits_backend", "postgres_can_set_backend", "postgres_inherits_owner", "postgres_can_set_owner"))

cases = [
    ("table_missing", "DROP TABLE public.account_security_generations CASCADE;", "02_tables_rls_and_owners", "definition_mismatches"),
    ("rls", "ALTER TABLE public.admin_users NO FORCE ROW LEVEL SECURITY;", "02_tables_rls_and_owners", "definition_mismatches"),
    ("owner", "ALTER TABLE public.admin_users OWNER TO glam_v2_backend;", "02_tables_rls_and_owners", "definition_mismatches"),
    ("function_missing", "DROP FUNCTION v2_authz_private.guard_owner() CASCADE;", "05_schema_and_function_ownership", "definition_mismatches"),
    ("function_body", "CREATE OR REPLACE FUNCTION v2_authz_private.guard_owner() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$ BEGIN RETURN NEW; END $$;", "05_schema_and_function_ownership", "definition_mismatches"),
    ("trigger_missing", "DROP TRIGGER customers_auth_owner ON public.customers;", "06_triggers_and_policies", "definition_mismatches"),
    ("trigger_disabled", "ALTER TABLE public.customers DISABLE TRIGGER customers_auth_owner;", "06_triggers_and_policies", "definition_mismatches"),
    ("policy_missing", "DROP POLICY auth_backend_customer_read ON public.customers;", "06_triggers_and_policies", "definition_mismatches"),
    ("policy_changed", "ALTER POLICY auth_backend_customer_read ON public.customers USING(false);", "06_triggers_and_policies", "definition_mismatches"),
    ("policy_extra", "CREATE POLICY unexpected ON public.services USING(true);", "06_triggers_and_policies", "definition_mismatches"),
    ("backend_missing", "REVOKE INSERT(email) ON public.customers FROM glam_v2_backend;", "07_backend_effective_privileges", "backend_and_owner_column_mismatches"),
    ("backend_extra", "GRANT UPDATE(email) ON public.customers TO glam_v2_backend;", "07_backend_effective_privileges", "backend_and_owner_column_mismatches"),
    ("owner_missing", "REVOKE UPDATE(generation) ON public.account_security_generations FROM glam_v2_authz_owner;", "07_backend_effective_privileges", "backend_and_owner_column_mismatches"),
    ("api_leak", "GRANT SELECT ON public.customers TO anon;", "08_untrusted_access", "table_or_column_violations"),
    ("api_set_path", "GRANT SELECT ON public.customers TO glam_v2_authz_owner; GRANT glam_v2_authz_owner TO anon WITH INHERIT FALSE, SET TRUE;", "08_untrusted_access", "table_or_column_violations"),
    ("constraint_missing", "ALTER TABLE public.account_security_generations DROP CONSTRAINT account_security_generations_generation_check;", "09_constraints_and_indexes", "definition_mismatches"),
    ("constraint_changed", "ALTER TABLE public.account_security_generations DROP CONSTRAINT account_security_generations_generation_check; ALTER TABLE public.account_security_generations ADD CONSTRAINT account_security_generations_generation_check CHECK(generation>=0);", "09_constraints_and_indexes", "definition_mismatches"),
    ("index_missing", "DROP INDEX public.auth_signup_unresolved_email_key;", "09_constraints_and_indexes", "definition_mismatches"),
    ("index_changed", "DROP INDEX public.auth_signup_unresolved_email_key; CREATE UNIQUE INDEX auth_signup_unresolved_email_key ON public.auth_mvc_signup_attempts(normalized_email) WHERE state='Initiated';", "09_constraints_and_indexes", "definition_mismatches"),
    ("not_null", "ALTER TABLE public.auth_mvc_signup_attempts ALTER COLUMN state DROP NOT NULL;", "09_constraints_and_indexes", "definition_mismatches"),
    ("default", "ALTER TABLE public.account_identities ALTER COLUMN credential_epoch SET DEFAULT 2;", "09_constraints_and_indexes", "definition_mismatches"),
    ("exclusion", "ALTER TABLE public.reservations DROP CONSTRAINT reservations_artist_reserved_overlap;", "09_constraints_and_indexes", "definition_mismatches"),
    ("exclusion_changed", """ALTER TABLE public.reservations DROP CONSTRAINT reservations_artist_reserved_overlap;
     ALTER TABLE public.reservations ADD CONSTRAINT reservations_artist_reserved_overlap EXCLUDE USING gist
     (artist_id WITH =, tstzrange(start_at,end_at,'[)') WITH &&)
     WHERE(status IN ('Pending','Pending Payment','Confirmed','InProgress'));""", "09_constraints_and_indexes", "definition_mismatches"),
    ("fk_trigger_disabled", "ALTER TABLE public.reservations DISABLE TRIGGER ALL;", "09_constraints_and_indexes", "disabled_constraint_trigger_violations"),
]
for label, fault, section, key in cases:
    evidence = verify(label, fault)
    assert evidence[section][key], label + " was not detected"

# Additional sections with boolean outcomes; no roles or migrations are created.
assert not verify("nonempty", """INSERT INTO public.auth_mvc_signup_attempts
 (attempt_id,browser_secret_hash,normalized_email,encrypted_profile,state,expires_at)
 VALUES(gen_random_uuid(),decode(repeat('01',32),'hex'),'synthetic@example.test',decode('01','hex'),'Initiated',now()+interval '10 minutes');""")["03_empty_counts"]["all_six_authoritatively_empty"]
assert not run("context", normalize + "SET SESSION AUTHORIZATION supabase_admin; SET SESSION AUTHORIZATION glam_v2_backend;\n"
               + report + "\nROLLBACK;")[0]["01_context"]["operator_and_version_ok"]
assert not verify("payment", "CREATE TABLE public.payments(id integer);")["10_payment_absence_and_history"]["payment_objects_absent"]
assert run("cleanup_baseline", "SET SESSION AUTHORIZATION postgres;\n" + cleanup)[0] == {
    "operator_ok": True, "restricted_roles_ok": True, "bootstrap_ok": True,
    "exactly_bootstrap_rows_ok": True, "no_temporary_rows_ok": True, "no_runtime_capability_ok": True,
}
inactive = run("cleanup_inactive_self_grant", """SET SESSION AUTHORIZATION postgres; BEGIN;
 GRANT glam_v2_authz_owner TO postgres WITH ADMIN FALSE, INHERIT FALSE, SET FALSE;
""" + cleanup + "\nROLLBACK;")[0]
assert inactive["no_runtime_capability_ok"] and not inactive["no_temporary_rows_ok"] and not inactive["exactly_bootstrap_rows_ok"]
# Grant commits independently; a failed/rolled-back migration does not clean it.
grant_block = re.search(r"### A\..*?```sql\n(.*?)```", PROCEDURE, re.S)[1]
cleanup_block = re.search(r"### C\..*?```sql\n(.*?)```", PROCEDURE, re.S)[1]
grant = "SET SESSION AUTHORIZATION postgres;\n" + grant_block
run("grant", grant)
dirty = run("after_disconnect", "SET SESSION AUTHORIZATION postgres;\n" + cleanup)[0]
assert not dirty["exactly_bootstrap_rows_ok"] and not dirty["no_temporary_rows_ok"] and not dirty["no_runtime_capability_ok"]
run("migration_rollback", "SET SESSION AUTHORIZATION postgres;\n\nBEGIN;\n\nSELECT 1/0;\n\nROLLBACK;", expected_error=True)
assert not run("still_dirty_after_rollback", "SET SESSION AUTHORIZATION postgres;\n" + cleanup)[0]["no_runtime_capability_ok"]
clean = run("cleanup_reconnect", "SET SESSION AUTHORIZATION postgres;\n" + cleanup_block + "\n" + cleanup)[0]
assert all(clean.values()), clean
# Verification still works with all auth objects removed in a rolled-back copy.
no_auth = run("cleanup_without_auth_objects", "BEGIN; DROP SCHEMA v2_authz_private,v2_authz_api CASCADE; DROP TABLE public.auth_mvc_signup_attempts,public.account_security_generations,public.account_identities,public.admin_users CASCADE; SET SESSION AUTHORIZATION postgres;\n" + cleanup + "\nROLLBACK;")[0]
assert all(no_auth.values()), no_auth
assert hashlib.sha256((ROOT / "database/v2/migrations/0004_customer_auth_compatibility.sql").read_bytes()).hexdigest().upper() == HASH
print(f"PASS: ten-section baseline, {len(cases)} fault cases, independent cleanup across sessions/rollback/absent auth objects.")
print("Local PostgreSQL 18.6 single-user copy only; original fixture untouched; no roles/migrations created.")
print("Evidence: " + str(WORK.relative_to(ROOT)))
