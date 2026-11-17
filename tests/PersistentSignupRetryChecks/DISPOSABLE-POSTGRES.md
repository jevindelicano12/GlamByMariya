# Disposable PostgreSQL checks — approval required

Do not run during preparation. These commands create a **new local PostgreSQL
cluster**, database and restricted test role. They do not target the existing
development cluster, create a production schema or delete records. The fixture
is a reduced synthetic schema, not the Supabase public schema or a migration.
`disposable-fixture.sql` checks the database name, UUID marker, loopback target,
port, admin user, absent role and empty public schema before creating anything.

Prerequisites: separate owner authorization for creating this cluster and its
synthetic records; reviewed PostgreSQL binaries containing `initdb`, `pg_ctl`
and `psql`; .NET 10 SDK; an unused local port. Confirm the path is an approved
local temporary directory. Run from the repository root. Do not point `pgBin`
or the data directory at an existing cluster. All `-W`/password prompts are
interactive; do not paste or log passwords in commands.

```powershell
$pgBin = Read-Host 'Full path to approved PostgreSQL bin directory'
$port = 0
if (-not [int]::TryParse((Read-Host 'Unused local test port'), [ref]$port) -or
    $port -lt 1024 -or $port -gt 65535) { throw 'Invalid test port.' }
foreach ($tool in @('initdb.exe','pg_ctl.exe','psql.exe')) {
  if (-not (Test-Path -LiteralPath (Join-Path $pgBin $tool) -PathType Leaf)) {
    throw "Missing PostgreSQL tool: $tool"
  }
}
$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
try { $probe.Start() } finally { $probe.Stop() }
$marker = [guid]::NewGuid().ToString('N')
$base = "$env:LOCALAPPDATA\Temp\opencode"
$cluster = Join-Path $base "glam-retry-cluster-$marker"
$serverLog = Join-Path $base "glam-retry-server-$marker.log"
$db = "glam_retry_disposable_$marker"
$role = "glam_retry_runner_$marker"
if ((Test-Path -LiteralPath $cluster) -or (Test-Path -LiteralPath $serverLog)) {
  throw 'Fresh cluster path already exists; stop.'
}

& (Join-Path $pgBin 'initdb.exe') -D $cluster -U postgres `
  -A scram-sha-256 -W --encoding=UTF8
if ($LASTEXITCODE -ne 0) { throw 'Cluster initialization failed; investigate without deleting it.' }
& (Join-Path $pgBin 'pg_ctl.exe') -D $cluster -l $serverLog `
  -o "-h 127.0.0.1 -p $port" -w start
if ($LASTEXITCODE -ne 0) { throw 'Isolated server failed to start; inspect only this cluster.' }

& (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p $port -U postgres -W `
  -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $db TEMPLATE template0"
if ($LASTEXITCODE -ne 0) { throw 'Database creation failed; do not continue.' }
& (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p $port -U postgres -W `
  -d $db -v ON_ERROR_STOP=1 -v "marker=$marker" -v "port=$port" `
  -f 'tests/PersistentSignupRetryChecks/disposable-fixture.sql'
if ($LASTEXITCODE -ne 0) { throw 'Fixture failed; do not run the harness.' }
& (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p $port -U postgres -W `
  -d $db -v ON_ERROR_STOP=1 -c "\password $role"
if ($LASTEXITCODE -ne 0) { throw 'Test-role password setup failed.' }

$artifacts = Join-Path $base "glam-retry-build-$marker"
dotnet build tests/PersistentSignupRetryChecks/PersistentSignupRetryChecks.csproj `
  -c Release --artifacts-path $artifacts --ignore-failed-sources -m:1
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
$keys = @('GLAM_RETRY_DISPOSABLE_DB','GLAM_RETRY_PORT','GLAM_RETRY_TEST_WRITES','PGPASSWORD')
$previous = @{}
foreach ($key in $keys) { $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
try {
  $secure = Read-Host 'Password just assigned to the disposable test role' -AsSecureString
  $env:PGPASSWORD = [Net.NetworkCredential]::new('', $secure).Password
  $env:GLAM_RETRY_DISPOSABLE_DB = $db
  $env:GLAM_RETRY_PORT = "$port"
  $env:GLAM_RETRY_TEST_WRITES = 'YES'
  dotnet "$artifacts/bin/PersistentSignupRetryChecks/release/PersistentSignupRetryChecks.dll" --disposable-postgres
  if ($LASTEXITCODE -ne 0) { throw 'Disposable PostgreSQL checks failed.' }
} finally {
  foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
  $secure = $null
}
```

The harness checks the UUID-derived role is login-only, non-superuser,
non-inheriting, without role memberships, database/schema creation, temporary
tables, account deletion or staff mutation. The fixture grants synthetic
account insertion, identity/attempt writes and generation update only for this
test; generation update permits the synthetic revocation scenario and is not a
production privilege recommendation. Role authentication uses `PGPASSWORD`
only for the test process and is restored afterward; process environments may
be visible to the same OS user. Use only a disposable test password.

The check covers concurrent same-email attempts, ownership, cooldown, expiry,
fencing, store recreation, generation-collision rollback, replay and
reconciliation. Discarding a successful response does **not** inject a
driver-level lost commit acknowledgement; recreating a store does **not** prove
a process restart or durable key ring. No live provider or production schema is
tested.

Retain the database and logs for review. When independently authorized to stop
only this newly created cluster, use:

```powershell
& (Join-Path $pgBin 'pg_ctl.exe') -D $cluster -m smart -w stop
```

Do not delete the cluster, database, role, records or logs as part of this test.
