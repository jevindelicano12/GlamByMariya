#!/bin/bash
# Runs one database test project against a throwaway postgres:17 container.
#   tools/test-db.sh <dotnet-project-path> [extra args...]
# The container listens on 127.0.0.1 only, uses a random port and a random marker
# (glam.payment_review_marker), and is removed afterwards whatever the result.
# The test project receives: <port> <marker> <empty temp dir with owner-marker> [extra args].
set -u
if [ $# -lt 1 ]; then echo "usage: tools/test-db.sh <dotnet-project-path> [extra args]" >&2; exit 2; fi
project="$1"; shift
cd "$(dirname "$0")/.." || exit 2

marker=$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')
port=$(( 30000 + $(od -An -N2 -tu2 /dev/urandom | tr -d ' ') % 20000 ))
name="glam-test-pg-$marker"
temp=$(mktemp -d)
printf '%s' "$marker" > "$temp/owner-marker"

cleanup() { docker rm -f "$name" >/dev/null 2>&1; rm -rf "$temp"; }
trap cleanup EXIT

docker run -d --name "$name" -p "127.0.0.1:$port:5432" -e POSTGRES_HOST_AUTH_METHOD=trust \
  postgres:17 -c "glam.payment_review_marker=$marker" -c "glam.docker_test=on" >/dev/null || exit 2

for _ in $(seq 1 60); do
  if docker exec "$name" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1; then break; fi
  sleep 1
done
# pg_isready can answer during the image's init restart; wait for the final server.
sleep 2
docker exec "$name" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 || { echo "PostgreSQL did not start" >&2; exit 2; }

# Same built-in roles as a Supabase project, so migrations can revoke from them.
docker exec "$name" psql -q -U postgres -c "CREATE ROLE anon NOLOGIN; CREATE ROLE authenticated NOLOGIN; CREATE ROLE service_role NOLOGIN BYPASSRLS;" >/dev/null || exit 2
# The app's own login, as on the hosted project: not the owner, no superuser, no RLS bypass,
# NOINHERIT like Supabase logins, so the backend role is granted WITH INHERIT TRUE.
docker exec "$name" psql -q -U postgres -c "CREATE ROLE makeup_backend NOLOGIN NOBYPASSRLS; CREATE ROLE glam_app LOGIN NOINHERIT NOBYPASSRLS; GRANT makeup_backend TO glam_app WITH INHERIT TRUE;" >/dev/null || exit 2

temp_arg="$temp"
if command -v cygpath >/dev/null 2>&1; then temp_arg=$(cygpath -w "$temp"); fi
dotnet run --project "$project" -- "$port" "$marker" "$temp_arg" "$@"
