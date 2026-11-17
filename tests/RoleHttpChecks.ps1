$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$base = 'http://127.0.0.1:5096'
$credentialKeys = @('GLAM_DEMO_ADMIN_PASSWORD', 'GLAM_DEMO_OWNER_PASSWORD', 'GLAM_DEMO_CUSTOMER_PASSWORD', 'PORT')
$previousCredentials = @{}
foreach ($key in $credentialKeys) { $previousCredentials[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
$env:GLAM_DEMO_ADMIN_PASSWORD = 'fixtureAdmin' + [guid]::NewGuid().ToString('N')
$env:GLAM_DEMO_OWNER_PASSWORD = 'fixtureOwner' + [guid]::NewGuid().ToString('N')
$env:GLAM_DEMO_CUSTOMER_PASSWORD = 'fixtureCustomer' + [guid]::NewGuid().ToString('N')
$env:PORT = ''
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Database__Provider = 'Demo'
$env:Supabase__AnonKey = ' '
$env:Email__SmtpHost = ' '
$server = $null
function Check($condition, $message) { if (-not $condition) { throw "FAIL: $message" }; Write-Host "PASS: $message" }
function Get-Page($path, $session) { Invoke-WebRequest "$base$path" -WebSession $session -UseBasicParsing }
function Submit($action, $page, $values, $session) {
    $html = (Get-Page $page $session).Content
    $match = [regex]::Match($html, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"')
    Check $match.Success "antiforgery present for $action"
    $values['__RequestVerificationToken'] = [System.Net.WebUtility]::HtmlDecode($match.Groups[1].Value)
    Invoke-WebRequest "$base$action" -Method Post -Body $values -WebSession $session -UseBasicParsing
}
try {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 5096)
    try { $probe.Start() } finally { $probe.Stop() }
    $server = Start-Process dotnet -ArgumentList @('run','--project','Frontend/GlamByMariya.Web/GlamByMariya.Web.csproj','--no-build','-c','Release','--no-launch-profile','--',"--urls=$base") -WorkingDirectory $root -PassThru -RedirectStandardOutput "$env:TEMP/role-http-test.log" -RedirectStandardError "$env:TEMP/role-http-test.err"
    $ready = $false
    for ($i=0; $i -lt 40; $i++) {
        if ($server.HasExited) { throw 'The isolated test server exited before becoming ready.' }
        try { Invoke-WebRequest "$base/Account/Login" -UseBasicParsing -TimeoutSec 2 | Out-Null; $ready=$true; break } catch { Start-Sleep -Milliseconds 500 }
    }
    Check $ready 'server started'
    $owner = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $customer = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $admin = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Submit '/Account/Login' '/Account/Login' @{email='owner@lumiere.demo';password=$env:GLAM_DEMO_OWNER_PASSWORD} $owner | Out-Null
    Submit '/Account/Login' '/Account/Login' @{email='customer@lumiere.demo';password=$env:GLAM_DEMO_CUSTOMER_PASSWORD} $customer | Out-Null
    Submit '/Account/Login' '/Account/Login' @{email='admin@lumiere.demo';password=$env:GLAM_DEMO_ADMIN_PASSWORD} $admin | Out-Null
    Check ((Get-Page '/Admin/Users' $owner).StatusCode -eq 200) 'SuperAdmin opens Manage Administrators'
    Check ((Get-Page '/Admin/Users' $owner).Content -match 'Invite an administrator') 'invite form renders'
    function Denied($session, $path) {
        try {
            $response = Get-Page $path $session
            return ($response.BaseResponse.ResponseUri.AbsolutePath -ne $path)
        } catch { return $true }
    }
    Check (Denied $admin '/Admin/Users') 'plain Admin denied from Manage Administrators'
    Check (Denied $customer '/Admin/Users') 'customer denied from Manage Administrators'

    # Existing account: duplicate invitation must not change role/password/auth.
    $duplicateResult = (Submit '/Admin/Invite' '/Admin/Users' @{email='customer@lumiere.demo'} $owner).Content
    Check ($duplicateResult -match 'already registered') 'existing account invitation is rejected clearly'
    Check ($duplicateResult -notmatch 'Invitation sent to customer@lumiere.demo') 'existing account receives no invitation'
    $customerAgain = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Submit '/Account/Login' '/Account/Login' @{email='customer@lumiere.demo';password=$env:GLAM_DEMO_CUSTOMER_PASSWORD} $customerAgain | Out-Null
    Check ((Get-Page '/Bookings' $customerAgain).BaseResponse.ResponseUri -match '/Bookings') 'existing customer still logs in after rejected invitation'

    # Tampered registration role is ignored: everyone becomes Customer.
    $evil = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Submit '/Account/Register' '/Account/Register' @{firstName='Evil';lastName='User';email='evil@example.test';age='25';gender='Others';address='123 Evil Street';password='Evil1234!';role='SuperAdmin'} $evil | Out-Null
    $verify = Get-Page '/Account/Verify?email=evil%40example.test' $evil
    Check ($verify.Content -match 'six-digit code') 'tampered registration still gated by email code'
    $safe = 'evil_example_test'
    $code = [regex]::Match((Get-Content (Join-Path $root "Frontend/GlamByMariya.Web/App_Data/EmailCodes/$safe.txt") -Raw),'\b\d{6}\b').Value
    Submit '/Account/Verify' '/Account/Verify?email=evil%40example.test' @{email='evil@example.test';code=$code} $evil | Out-Null
    $bookings = Get-Page '/Bookings' $evil
    Check ($bookings.BaseResponse.ResponseUri -match '/Bookings') 'tampered registration lands on customer workspace'

    # Full invite round-trip through the real emailed outbox file.
    $inviteEmail = 'invited.admin@example.test'
    Submit '/Admin/Invite' '/Admin/Users' @{email=$inviteEmail} $owner | Out-Null
    $users = (Get-Page '/Admin/Users' $owner).Content
    Check ($users -match $inviteEmail) 'invitation listed as pending'
    $inviteSafe = 'invited_admin_example_test'
    $inviteBody = Get-Content (Join-Path $root "Frontend/GlamByMariya.Web/App_Data/EmailCodes/$inviteSafe.txt") -Raw
    Check ($inviteBody -match 'You are invited to administer Glam by Mariya' -and $inviteBody -match 'A Super Admin invited you to administer Glam by Mariya') 'administrator invitation email uses Glam by Mariya branding'
    $link = [regex]::Match($inviteBody,'https?://\S+').Value
    Check ($link -match 'AcceptInvite' -and $link -notmatch 'SuperAdmin') 'invitation link carries token, never a role'
    $qs = [regex]::Match($link,'email=([^&]+)&token=(\S+)')
    $newAdmin = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $form = Get-Page ("/Account/AcceptInvite?email=$($qs.Groups[1].Value)&token=$($qs.Groups[2].Value)") $newAdmin
    Check ($form.Content -match 'Set up your account') 'invitation link opens setup form'
    Submit '/Account/AcceptInvite' ("/Account/AcceptInvite?email=$($qs.Groups[1].Value)&token=$($qs.Groups[2].Value)") @{email=$inviteEmail;token=$qs.Groups[2].Value;firstName='Invited';lastName='Admin';age='30';gender='Others';address='123 Admin Street';password='Invited123!'} $newAdmin | Out-Null
    $landing = Get-Page '/Admin' $newAdmin
    Check ($landing.BaseResponse.ResponseUri -match '/Admin') 'accepted invitation signs in as Admin'
    $replay = Submit '/Account/AcceptInvite' ("/Account/AcceptInvite?email=$($qs.Groups[1].Value)&token=$($qs.Groups[2].Value)") @{email=$inviteEmail;token=$qs.Groups[2].Value;firstName='Invited';lastName='Admin';age='30';gender='Others';address='123 Admin Street';password='Invited123!'} (New-Object Microsoft.PowerShell.Commands.WebRequestSession)
    Check ($replay.Content -match 'already used|invalid') 'invitation token cannot be replayed'
    $bad = Submit '/Account/AcceptInvite' '/Account/AcceptInvite?email=nobody@example.test&token=deadbeef' @{email='nobody@example.test';token='deadbeef';firstName='No';lastName='Body';age='30';gender='Others';address='123 Nowhere Street';password='Nobody123!'} (New-Object Microsoft.PowerShell.Commands.WebRequestSession)
    Check ($bad.Content -match 'invalid|expired') 'forged invitation rejected'
    Write-Host 'ALL ROLE HTTP CHECKS PASSED'
} finally {
    if ($server -and -not $server.HasExited) { taskkill /PID $server.Id /T /F 2>&1 | Out-Null }
    foreach ($key in $credentialKeys) { [Environment]::SetEnvironmentVariable($key, $previousCredentials[$key], 'Process') }
}
