$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if([IO.Path]::GetFullPath((Get-Location).Path) -ne $workspace){throw 'Run from the active V2 workspace root'}
$root=Join-Path $workspace 'temp\v2-customer-auth-integration-20261116-4'
if(Test-Path -LiteralPath $root){throw 'Cluster path exists; select a new path in this script and Program.cs, never reuse or overwrite'}
if(Test-Path Env:GLAM_V2_CUSTOMER_AUTH_TEST_PASSWORD){throw 'Test password variable is already set'}
$bin='C:\Program Files\PostgreSQL\18\bin'
dotnet build (Join-Path $PSScriptRoot 'CustomerAuthenticationIntegrationChecks.csproj') --nologo -v:q
if($LASTEXITCODE -ne 0){throw 'Build failed'}
$probe=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,55434)
try{$probe.Start()}finally{$probe.Stop()}
New-Item -ItemType Directory -Path $root | Out-Null
$account=[Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls.exe $root /inheritance:r /grant:r "${account}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Private directory ACL failed'}
$bytes=New-Object byte[] 32
$rng=[Security.Cryptography.RandomNumberGenerator]::Create()
try{$rng.GetBytes($bytes)}finally{$rng.Dispose()}
$passwordFile=Join-Path $root 'password.txt'
[IO.File]::WriteAllText($passwordFile,[BitConverter]::ToString($bytes).Replace('-',''),[Text.UTF8Encoding]::new($false))
$started=$false
try{
    & "$bin\initdb.exe" -D (Join-Path $root 'data') -U postgres -A scram-sha-256 "--pwfile=$passwordFile" --encoding=UTF8 --no-instructions | Out-Null
    if($LASTEXITCODE -ne 0){throw 'initdb failed'}
    & "$bin\pg_ctl.exe" -D (Join-Path $root 'data') -l (Join-Path $root 'server.log') -o '-h 127.0.0.1 -p 55434' -w start
    if($LASTEXITCODE -ne 0){throw 'PostgreSQL start failed'}
    $started=$true
    $env:GLAM_V2_CUSTOMER_AUTH_TEST_PASSWORD=[IO.File]::ReadAllText($passwordFile)
    dotnet run --project (Join-Path $PSScriptRoot 'CustomerAuthenticationIntegrationChecks.csproj') --no-build -- --exercise
    if($LASTEXITCODE -ne 0){throw 'Customer-auth migration/MVC checks failed'}
}finally{
    Remove-Item Env:GLAM_V2_CUSTOMER_AUTH_TEST_PASSWORD -ErrorAction SilentlyContinue
    if($started){
        & "$bin\pg_ctl.exe" -D (Join-Path $root 'data') -m fast -w stop
        if($LASTEXITCODE -ne 0){throw 'Cluster stop failed; password file retained for recovery'}
    }
    Remove-Item -LiteralPath $passwordFile -Force -ErrorAction SilentlyContinue
}
