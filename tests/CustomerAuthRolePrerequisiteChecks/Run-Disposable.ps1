param([switch]$Compatibility)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if([IO.Path]::GetFullPath((Get-Location).Path) -ne $workspace){throw 'Run from the active V2 workspace root'}
$root=Join-Path $workspace $(if($Compatibility){'temp\v2-role-compatibility-20261116-3'}else{'temp\v2-role-prerequisites-20261116-4'})
if(Test-Path -LiteralPath $root){throw 'Fresh path required; do not reuse or overwrite a cluster'}
if(Test-Path Env:PGPASSWORD){throw 'PGPASSWORD already set; refusing to overwrite it'}
$bin='C:\Program Files\PostgreSQL\18\bin'
if($Compatibility){
    dotnet build (Join-Path $workspace 'tests\CustomerAuthenticationIntegrationChecks\CustomerAuthenticationIntegrationChecks.csproj') --nologo -v:q
    if($LASTEXITCODE -ne 0){throw 'Compatibility host build failed'}
}
$probe=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,55435)
try{$probe.Start()}finally{$probe.Stop()}
New-Item -ItemType Directory -Path $root | Out-Null
$account=[Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls.exe $root /inheritance:r /grant:r "${account}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Private ACL failed'}
$bytes=New-Object byte[] 32
$rng=[Security.Cryptography.RandomNumberGenerator]::Create()
try{$rng.GetBytes($bytes)}finally{$rng.Dispose()}
$password=[BitConverter]::ToString($bytes).Replace('-','')
$passwordFile=Join-Path $root 'password.txt'
$bootstrapFile=Join-Path $root 'bootstrap.sql'
[IO.File]::WriteAllText($passwordFile,$password,[Text.UTF8Encoding]::new($false))
$started=$false
try{
    & "$bin\initdb.exe" -D (Join-Path $root 'data') -U supabase_admin -A scram-sha-256 "--pwfile=$passwordFile" --encoding=UTF8 --no-instructions | Out-Null
    if($LASTEXITCODE -ne 0){throw 'initdb failed'}
    & "$bin\pg_ctl.exe" -D (Join-Path $root 'data') -l (Join-Path $root 'server.log') -o '-h 127.0.0.1 -p 55435' -w start
    if($LASTEXITCODE -ne 0){throw 'PostgreSQL start failed'}
    $started=$true
    $env:PGPASSWORD=$password
    $safeData=(Join-Path $root 'data').Replace('\','/')
    $bootstrap=@"
\set ON_ERROR_STOP on
SELECT current_user='supabase_admin' AND current_database()='postgres'
  AND inet_server_addr()='127.0.0.1'::inet AND inet_server_port()=55435
  AND replace(current_setting('data_directory'),chr(92),'/')='$safeData'
  AND current_setting('server_version_num')::integer BETWEEN 180000 AND 189999
  AND NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname IN
    ('postgres','anon','authenticated','service_role','authenticator','glam_v2_backend','glam_v2_authz_owner'))
  AND NOT EXISTS(SELECT 1 FROM pg_database WHERE datname='glam_v2_role_prerequisite_test') AS safe \gset
\if :safe
CREATE ROLE postgres LOGIN INHERIT CREATEROLE NOSUPERUSER NOBYPASSRLS NOCREATEDB NOREPLICATION PASSWORD '$password';
CREATE ROLE anon NOLOGIN;
CREATE ROLE authenticated NOLOGIN;
CREATE ROLE service_role NOLOGIN BYPASSRLS;
CREATE ROLE authenticator NOLOGIN;
CREATE DATABASE glam_v2_role_prerequisite_test OWNER postgres TEMPLATE template0;
\else
\quit 3
\endif
"@
    [IO.File]::WriteAllText($bootstrapFile,$bootstrap,[Text.UTF8Encoding]::new($false))
    & "$bin\psql.exe" -X -h 127.0.0.1 -p 55435 -U supabase_admin -d postgres -f $bootstrapFile
    if($LASTEXITCODE -ne 0){throw 'Fresh-target bootstrap refused or failed'}
    Remove-Item -LiteralPath $bootstrapFile -Force
    if($Compatibility){
        # Local DDL owner must inspect FORCE RLS tables authoritatively for 0004.
        & "$bin\psql.exe" -X -h 127.0.0.1 -p 55435 -U supabase_admin -d glam_v2_role_prerequisite_test -c 'ALTER ROLE postgres BYPASSRLS'
        if($LASTEXITCODE -ne 0){throw 'Local migration-operator setup failed'}
        dotnet run --project (Join-Path $workspace 'tests\CustomerAuthenticationIntegrationChecks\CustomerAuthenticationIntegrationChecks.csproj') --no-build -- --role-compatibility
        if($LASTEXITCODE -ne 0){throw 'Role compatibility checks failed'}
    }else{
        & "$bin\psql.exe" -X -h 127.0.0.1 -p 55435 -U postgres -d glam_v2_role_prerequisite_test -f (Join-Path $PSScriptRoot 'checks.sql')
        if($LASTEXITCODE -ne 0){throw 'Role prerequisite checks failed'}
        & "$bin\psql.exe" -X -h 127.0.0.1 -p 55435 -U supabase_admin -d glam_v2_role_prerequisite_test -f (Join-Path $workspace 'database\v2\prerequisites\customer_auth_roles_provider_only.sql')
        if($LASTEXITCODE -ne 0){throw 'Provider-only role proposal failed'}
        & "$bin\psql.exe" -X -h 127.0.0.1 -p 55435 -U postgres -d glam_v2_role_prerequisite_test -f (Join-Path $PSScriptRoot 'provider-checks.sql')
        if($LASTEXITCODE -ne 0){throw 'Provider role verification failed'}
    }
}finally{
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    if($started){
        & "$bin\pg_ctl.exe" -D (Join-Path $root 'data') -m fast -w stop
        if($LASTEXITCODE -ne 0){throw 'Stop failed; private recovery files retained'}
    }
    Remove-Item -LiteralPath $passwordFile,$bootstrapFile -Force -ErrorAction SilentlyContinue
}
