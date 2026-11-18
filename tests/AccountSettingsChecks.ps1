$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$base = 'http://127.0.0.1:5097'
$credentialKeys = @('GLAM_DEMO_ADMIN_PASSWORD', 'GLAM_DEMO_OWNER_PASSWORD', 'GLAM_DEMO_CUSTOMER_PASSWORD', 'DemoAccounts__Presentation', 'PORT')
$previousCredentials = @{}
foreach ($key in $credentialKeys) { $previousCredentials[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
$env:GLAM_DEMO_ADMIN_PASSWORD = 'fixtureAdmin' + [guid]::NewGuid().ToString('N')
$env:GLAM_DEMO_OWNER_PASSWORD = 'fixtureOwner' + [guid]::NewGuid().ToString('N')
$env:GLAM_DEMO_CUSTOMER_PASSWORD = 'fixtureCustomer' + [guid]::NewGuid().ToString('N')
$env:PORT = ''
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Database__Provider = 'Demo'
$env:DemoAccounts__Presentation = 'true'
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
    Invoke-WebRequest "$base/Account/$action" -Method Post -Body $values -WebSession $session -UseBasicParsing
}
try {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 5097)
    try { $probe.Start() } finally { $probe.Stop() }
    $server = Start-Process dotnet -ArgumentList @('run','--project','Frontend/GlamByMariya.Web/GlamByMariya.Web.csproj','--no-build','-c','Release','--no-launch-profile','--',"--urls=$base") -WorkingDirectory $root -PassThru -RedirectStandardOutput "$env:TEMP/account-settings-test.log" -RedirectStandardError "$env:TEMP/account-settings-test.err"
    $ready = $false
    for ($i=0; $i -lt 40; $i++) {
        if ($server.HasExited) { throw 'The isolated test server exited before becoming ready.' }
        try { Invoke-WebRequest "$base/Account/Login" -UseBasicParsing -TimeoutSec 2 | Out-Null; $ready=$true; break } catch { Start-Sleep -Milliseconds 500 }
    }
    Check $ready 'server started'
    $a = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $b = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $guest = Get-Page '/Account/Settings' $a
    Check ($guest.Content -match 'id="auth-form"') 'settings requires authentication'
    foreach ($s in @($a,$b)) { Submit 'Login' '/Account/Login' @{email='presentation-customer@example.test';password=$env:GLAM_DEMO_CUSTOMER_PASSWORD} $s | Out-Null }
    $page = Get-Page '/Account/Settings' $a
    Check ($page.Content -match 'Your profile' -and $page.Content -match 'Notifications') 'settings sections render'
    $bad = Submit 'SaveProfile' '/Account/Settings' @{firstName='Alex';lastName='Morgan';age='5';gender='Others';address='123 Demo Street'} $a
    Check ($bad.Content -match '13') 'invalid profile rejected'
    $saved = Submit 'SaveProfile' '/Account/Settings' @{firstName='Alex';lastName='Updated';age='28';gender='Others';address='123 Updated Street'} $a
    Check ($saved.Content -match '123 Updated Street' -and $saved.Content -match 'Profile saved') 'profile saved and displayed'
    Submit 'SaveNotifications' '/Account/Settings' @{notificationsEnabled='false'} $a | Out-Null
    $prefs = (Get-Page '/Account/Settings' $a).Content
    $toggle = [regex]::Match($prefs,'<input[^>]*name="notificationsEnabled"[^>]*>').Value
    Check ($toggle -notmatch 'checked') 'notifications opt out persisted'
    Submit 'SendEmailVerification' '/Account/Settings' @{} $a | Out-Null
    $codeFile = Join-Path $root 'Frontend/GlamByMariya.Web/App_Data/EmailCodes/presentation_customer_example_test.txt'
    $code = [regex]::Match((Get-Content $codeFile -Raw),'\b\d{6}\b').Value
    $wrong = Submit 'ConfirmEmail' '/Account/Settings' @{code='000000'} $a
    Check ($wrong.Content -match 'Incorrect code') 'wrong verification code rejected'
    $verified = Submit 'ConfirmEmail' '/Account/Settings' @{code=$code} $a
    Check ($verified.Content -match 'Email verified') 'email code marks account verified'
    $replay = Submit 'ConfirmEmail' '/Account/Settings' @{code=$code} $a
    Check ($replay.Content -match 'Code expired') 'verification code cannot be replayed'
    $wrongPassword = Submit 'ChangePassword' '/Account/Settings' @{currentPassword='Wrong123';newPassword='Updated123!';confirmPassword='Updated123!'} $a
    Check ($wrongPassword.Content -match 'Current password is incorrect') 'current password required'
    Submit 'ChangePassword' '/Account/Settings' @{currentPassword=$env:GLAM_DEMO_CUSTOMER_PASSWORD;newPassword='Updated123!';confirmPassword='Updated123!'} $a | Out-Null
    Check ((Get-Page '/Account/Settings' $b).Content -match 'id="auth-form"') 'password change invalidates other sessions'
    $old = Submit 'Login' '/Account/Login' @{email='presentation-customer@example.test';password=$env:GLAM_DEMO_CUSTOMER_PASSWORD} $a
    Check ($old.Content -match 'Email or password is incorrect') 'old password no longer works'
    Submit 'Login' '/Account/Login' @{email='presentation-customer@example.test';password='Updated123!'} $a | Out-Null
    Check ((Get-Page '/Account/Settings' $a).Content -match 'Your profile') 'new password signs in'
    $logoutPage = Submit 'Logout' '/Account/Settings' @{} $a
    Check ($logoutPage.BaseResponse.ResponseUri.AbsolutePath -eq '/Account/Login') 'POST logout redirects to public login GET'
    Check ($logoutPage.Content -match 'Sign in|Welcome back') 'logout destination is public'
    try {
        $bookingsAfterLogout = Get-Page '/Bookings' $a
        Check ($bookingsAfterLogout.BaseResponse.ResponseUri.AbsolutePath -eq '/Account/Login' -and $bookingsAfterLogout.Content -notmatch 'UPCOMING MOMENTS') 'logged-out session cannot access bookings'
    } catch { Check ($_.Exception.Response.StatusCode.value__ -in 302, 403) 'logged-out session cannot access bookings' }
    try { Invoke-WebRequest "$base/Account/Logout" -Method Get -WebSession $a -UseBasicParsing -MaximumRedirection 0 | Out-Null; Check $false 'GET logout is not allowed' }
    catch { Check ($_.Exception.Response.StatusCode.value__ -eq 405) 'GET logout is not allowed' }
    Write-Host 'ALL ACCOUNT SETTINGS CHECKS PASSED'
} finally {
    if ($server -and -not $server.HasExited) { taskkill /PID $server.Id /T /F 2>&1 | Out-Null }
    foreach ($key in $credentialKeys) { [Environment]::SetEnvironmentVariable($key, $previousCredentials[$key], 'Process') }
}
