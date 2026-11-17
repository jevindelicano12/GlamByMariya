$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$base = 'http://localhost:5100'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Database__Provider = 'Demo'
$env:Supabase__AnonKey = ' '
$env:Email__SmtpHost = ' '
$server = Start-Process dotnet -ArgumentList @('run','--no-build','-c','SecurityHeaders','--no-launch-profile','--urls',$base) -WorkingDirectory $root -PassThru -RedirectStandardOutput "$env:TEMP/security-headers-http.log" -RedirectStandardError "$env:TEMP/security-headers-http.err"
$script:checks = 0
function Check($condition, $message) { if (-not $condition) { throw "FAIL: $message" }; $script:checks++; Write-Host "PASS: $message" }
function Get-Page($path) {
    try {
        $r = Invoke-WebRequest "$base$path" -UseBasicParsing -TimeoutSec 10
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Headers = $r.Headers }
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        $status = 0
        try { $status = [int]$resp.StatusCode } catch { }
        return [pscustomobject]@{ Status = $status; Headers = $resp.Headers }
    }
}
function Header($headers, $name) {
    if ($null -eq $headers) { return $null }
    try {
        $key = @($headers.Keys) | Where-Object { $_ -ieq $name } | Select-Object -First 1
        if ($key) {
            $value = $headers[$key]
            if ($value -is [string]) { return $value }
            $first = @($value) | Select-Object -First 1
            if ($null -ne $first) { return [string]$first }
        }
    } catch { }
    try {
        $values = @($headers.GetValues($name))
        if ($values.Count -gt 0) { return [string]$values[0] }
    } catch { }
    return $null
}
function Assert-Headers($label, $result, $expectedStatus) {
    Check ($result.Status -eq $expectedStatus) "$label returns HTTP $expectedStatus (got $($result.Status))"
    Check ((Header $result.Headers 'X-Content-Type-Options') -eq 'nosniff') "$label has X-Content-Type-Options: nosniff"
    Check ((Header $result.Headers 'Referrer-Policy') -eq 'strict-origin-when-cross-origin') "$label has Referrer-Policy: strict-origin-when-cross-origin"
    Check ((Header $result.Headers 'X-Frame-Options') -eq 'DENY') "$label has X-Frame-Options: DENY"
    Check ([string]::IsNullOrEmpty((Header $result.Headers 'Content-Security-Policy'))) "$label has no Content-Security-Policy (not implemented yet)"
    Check ([string]::IsNullOrEmpty((Header $result.Headers 'Strict-Transport-Security'))) "$label sends no HSTS over local HTTP development"
}
try {
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        try { Invoke-WebRequest "$base/healthz" -UseBasicParsing -TimeoutSec 2 | Out-Null; $ready = $true; break } catch { Start-Sleep -Milliseconds 500 }
    }
    Check $ready 'server started'

    Assert-Headers 'login page' (Get-Page '/Account/Login') 200
    Assert-Headers 'registration page' (Get-Page '/Account/Register') 200
    Assert-Headers 'CSS asset' (Get-Page '/css/site.css') 200
    Assert-Headers 'JavaScript asset' (Get-Page '/js/site.js') 200
    Assert-Headers 'favicon asset' (Get-Page '/favicon.svg') 200
    Assert-Headers 'health check' (Get-Page '/healthz') 200
    Assert-Headers 'unknown route' (Get-Page '/this-page-does-not-exist') 404

    Write-Host "ALL SECURITY HEADER HTTP CHECKS PASSED ($($script:checks) checks)"
} finally {
    taskkill /PID $server.Id /T /F 2>&1 | Out-Null
}
