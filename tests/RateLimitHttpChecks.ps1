param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$base = 'http://localhost:5099'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Database__Provider = 'Demo'
$env:Supabase__AnonKey = ' '
$env:Email__SmtpHost = ' '
$server = Start-Process dotnet -ArgumentList @('run','--no-build','-c','RateLimitHttp','--no-launch-profile','--urls',$base) -WorkingDirectory $root -PassThru -RedirectStandardOutput "$env:TEMP/rate-limit-http.log" -RedirectStandardError "$env:TEMP/rate-limit-http.err"
$script:checks = 0
$script:loginPosts = 0
function Check($condition, $message) { if (-not $condition) { throw "FAIL: $message" }; $script:checks++; Write-Host "PASS: $message" }
function Get-Token($path, $session) {
    $html = (Invoke-WebRequest "$base$path" -WebSession $session -UseBasicParsing).Content
    $m = [regex]::Match($html, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"')
    if (-not $m.Success) { throw "No antiforgery token found at $path" }
    return [System.Net.WebUtility]::HtmlDecode($m.Groups[1].Value)
}
function Post($path, $values, $session, $token) {
    $values['__RequestVerificationToken'] = $token
    try {
        $r = Invoke-WebRequest "$base$path" -Method Post -Body $values -WebSession $session -UseBasicParsing
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Content = $r.Content; Headers = $r.Headers }
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        $status = 0
        try { $status = [int]$resp.StatusCode } catch { }
        $content = $_.ErrorDetails.Message
        if ([string]::IsNullOrEmpty($content)) {
            try { $content = (New-Object System.IO.StreamReader($resp.GetResponseStream())).ReadToEnd() } catch { $content = '' }
        }
        return [pscustomobject]@{ Status = $status; Content = $content; Headers = $resp.Headers }
    }
}
function Assert-Limit($name, $limit, [scriptblock]$post) {
    for ($i = 1; $i -le $limit; $i++) {
        $r = & $post
        if ($r.Status -eq 429) { throw "FAIL: $name was throttled at request $i of $limit" }
        if ($r.Status -ne 200) { throw "FAIL: $name request $i returned $($r.Status) instead of 200" }
    }
    $blocked = & $post
    Check ($blocked.Status -eq 429) "$name returns 429 on request $($limit + 1)"
    Check (($blocked.Headers -ne $null) -and ($blocked.Headers.ToString() -match 'Retry-After')) "$name 429 includes a Retry-After header"
    Check ($blocked.Content -match 'Too many requests') "$name 429 body is friendly"
    Check ($blocked.Content -notmatch '@') "$name 429 does not echo an email address"
    return $blocked
}
function Denied($session, $path) {
    try {
        $r = Invoke-WebRequest "$base$path" -WebSession $session -UseBasicParsing
        return ($r.BaseResponse.ResponseUri.AbsolutePath -ne $path)
    } catch { return $true }
}
try {
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        try { Invoke-WebRequest "$base/Account/Login" -UseBasicParsing -TimeoutSec 2 | Out-Null; $ready = $true; break } catch { Start-Sleep -Milliseconds 500 }
    }
    Check $ready 'server started'

    $owner = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $admin = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $customer = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $anon = New-Object Microsoft.PowerShell.Commands.WebRequestSession

    # Role permissions first (each login also consumes the shared login window).
    Post '/Account/Login' @{email='owner@glambymariya.demo';password='Owner123!'} $owner (Get-Token '/Account/Login' $owner) | Out-Null; $script:loginPosts++
    Post '/Account/Login' @{email='admin@glambymariya.demo';password='Admin123!'} $admin (Get-Token '/Account/Login' $admin) | Out-Null; $script:loginPosts++
    Post '/Account/Login' @{email='customer@glambymariya.demo';password='Customer123!'} $customer (Get-Token '/Account/Login' $customer) | Out-Null; $script:loginPosts++
    Check ((Invoke-WebRequest "$base/Admin/Users" -WebSession $owner -UseBasicParsing).StatusCode -eq 200) 'SuperAdmin can open Manage Administrators'
    Check (Denied $admin '/Admin/Users') 'plain Admin is denied Manage Administrators'
    Check (Denied $customer '/Admin/Users') 'Customer is denied Manage Administrators'
    Check ((Invoke-WebRequest "$base/Bookings" -WebSession $customer -UseBasicParsing).StatusCode -eq 200) 'Customer keeps booking access'

    # Login: the account-aware limiter still fires on the 11th attempt, then the outer IP window 429s.
    $bogus = "bogus-$([guid]::NewGuid().ToString('N').Substring(0,10))@example.test"
    $bogusToken = Get-Token '/Account/Login' $anon
    $processed = 0
    $eleventh = $null
    for ($i = 1; $i -le 11; $i++) {
        $r = Post '/Account/Login' @{email=$bogus;password='Wrong123!'} $anon $bogusToken
        if ($r.Status -eq 429) { throw 'FAIL: login outer limit throttled before the account-aware limit' }
        $processed++
        if ($i -eq 11) { $eleventh = $r }
    }
    Check ($eleventh.Content -match 'Too many attempts') 'account-aware login limiter still refuses the 11th attempt'
    $blockedLogin = $null
    for ($i = 0; $i -lt 80; $i++) {
        $r = Post '/Account/Login' @{email=$bogus;password='Wrong123!'} $anon $bogusToken
        if ($r.Status -eq 429) { $blockedLogin = $r; break }
        $processed++
    }
    Check ($blockedLogin -ne $null) 'login endpoint returns 429 once its per-IP window is exhausted'
    Check (($script:loginPosts + $processed) -eq 60) "login allows exactly 60 processed requests per minute (processed $($script:loginPosts + $processed))"
    Check ($blockedLogin.Content -match 'Too many requests') 'login 429 body is friendly'

    # Administrator invitation issuance.
    $inviteToken = Get-Token '/Admin/Users' $owner
    $inviteCount = 0
    $inviteBlocked = Assert-Limit 'administrator invitation issuance' 10 {
        $script:inviteCount++
        $email = if ($script:inviteCount -eq 1) { "invite-$([guid]::NewGuid().ToString('N').Substring(0,10))@example.test" } else { 'customer@glambymariya.demo' }
        Post '/Admin/Invite' @{email=$email} $owner $inviteToken
    }

    # Invitation acceptance (anonymous, single-use token).
    $acceptToken = Get-Token '/Account/AcceptInvite?email=nobody%40example.test&token=deadbeef' $anon
    Assert-Limit 'invitation acceptance' 10 {
        $email = "accept-$([guid]::NewGuid().ToString('N').Substring(0,10))@example.test"
        Post '/Account/AcceptInvite' @{email=$email;token='deadbeef';firstName='Rate';lastName='Limit';age='25';gender='Others';address='123 Rate Limit Street';password='RateLimit123!'} $anon $acceptToken
    } | Out-Null

    # Registration.
    $registerToken = Get-Token '/Account/Register' $anon
    Assert-Limit 'registration' 5 {
        $email = "reg-$([guid]::NewGuid().ToString('N').Substring(0,10))@example.test"
        Post '/Account/Register' @{firstName='Rate';lastName='Limit';email=$email;age='25';gender='Others';address='123 Rate Limit Street';password='RateLimit123!'} $anon $registerToken
    } | Out-Null

    # Email verification (registration OTP).
    $verifyToken = Get-Token '/Account/Verify?email=nobody%40example.test' $anon
    Assert-Limit 'email verification' 15 {
        Post '/Account/Verify' @{email='nobody@example.test';code='000000'} $anon $verifyToken
    } | Out-Null

    # Verification resend.
    $resendToken = Get-Token '/Account/Verify?email=nobody%40example.test' $anon
    Assert-Limit 'verification resend' 5 {
        Post '/Account/Resend' @{email='nobody@example.test'} $anon $resendToken
    } | Out-Null

    # Forgot password.
    $forgotToken = Get-Token '/Account/ForgotPassword' $anon
    Assert-Limit 'forgot password' 5 {
        $email = "forgot-$([guid]::NewGuid().ToString('N').Substring(0,10))@example.test"
        Post '/Account/ForgotPassword' @{email=$email} $anon $forgotToken
    } | Out-Null

    # Reset password.
    $resetToken = Get-Token '/Account/ResetPassword?email=nobody%40example.test' $anon
    Assert-Limit 'reset password' 10 {
        $email = "reset-$([guid]::NewGuid().ToString('N').Substring(0,10))@example.test"
        Post '/Account/ResetPassword' @{email=$email;code='000000';newPassword='Changed123!'} $anon $resetToken
    } | Out-Null

    Write-Host "ALL RATE-LIMIT HTTP CHECKS PASSED ($($script:checks) checks)"
} finally {
    taskkill /PID $server.Id /T /F 2>&1 | Out-Null
}
