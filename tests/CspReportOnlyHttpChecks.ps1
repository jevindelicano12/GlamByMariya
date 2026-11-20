$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$base = 'http://localhost:5101'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Database__Provider = 'Demo'
$env:Supabase__AnonKey = ' '
$env:Email__SmtpHost = ' '
Remove-Item Env:B2__Endpoint -ErrorAction SilentlyContinue
$server = Start-Process dotnet -ArgumentList @('run','--no-build','-c','SecurityHeaders','--no-launch-profile','--urls',$base) -WorkingDirectory $root -PassThru -RedirectStandardOutput "$env:TEMP/csp-report-only-http.log" -RedirectStandardError "$env:TEMP/csp-report-only-http.err"
$script:checks = 0
function Check($condition, $message) { if (-not $condition) { throw "FAIL: $message" }; $script:checks++; Write-Host "PASS: $message" }
function Get-Page($path) {
    try {
        $r = Invoke-WebRequest "$base$path" -UseBasicParsing -TimeoutSec 10
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Headers = $r.Headers; Content = [string]$r.Content }
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        $status = 0
        try { $status = [int]$resp.StatusCode } catch { }
        $content = ''
        try {
            $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
            $content = $reader.ReadToEnd()
        } catch { }
        return [pscustomobject]@{ Status = $status; Headers = $resp.Headers; Content = $content }
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
function Directive($policy, $name) {
    if ([string]::IsNullOrWhiteSpace($policy)) { return $null }
    foreach ($part in ($policy -split ';')) {
        $value = $part.Trim()
        if ($value -eq $name) { return '' }
        if ($value.StartsWith("$name ", [System.StringComparison]::Ordinal)) { return $value.Substring($name.Length).Trim() }
    }
    return $null
}
function Tokens($value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return @() }
    return @($value -split '\s+' | Where-Object { $_ })
}
function InlineNonces($html) {
    if ([string]::IsNullOrEmpty($html)) { return @() }
    $matches = [regex]::Matches($html, '<script(?![^>]*\bsrc=)[^>]*\bnonce="([^"]+)"', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    return @($matches | ForEach-Object { $_.Groups[1].Value })
}
function Assert-CommonHeaders($label, $result, $expectedStatus) {
    Check ($result.Status -eq $expectedStatus) "$label returns HTTP $expectedStatus (got $($result.Status))"
    Check ((Header $result.Headers 'X-Content-Type-Options') -eq 'nosniff') "$label keeps X-Content-Type-Options: nosniff"
    Check ((Header $result.Headers 'Referrer-Policy') -eq 'strict-origin-when-cross-origin') "$label keeps Referrer-Policy: strict-origin-when-cross-origin"
    Check ((Header $result.Headers 'X-Frame-Options') -eq 'DENY') "$label keeps X-Frame-Options: DENY"
    Check ([string]::IsNullOrEmpty((Header $result.Headers 'Content-Security-Policy'))) "$label has no enforcing Content-Security-Policy"
    $reportOnly = Header $result.Headers 'Content-Security-Policy-Report-Only'
    Check (-not [string]::IsNullOrWhiteSpace($reportOnly)) "$label has Content-Security-Policy-Report-Only"
    Check ([string]::IsNullOrEmpty((Header $result.Headers 'Strict-Transport-Security'))) "$label sends no HSTS over local HTTP development"
    return $reportOnly
}
try {
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        try { Invoke-WebRequest "$base/healthz" -UseBasicParsing -TimeoutSec 2 | Out-Null; $ready = $true; break } catch { Start-Sleep -Milliseconds 500 }
    }
    Check $ready 'server started'

    $login = Get-Page '/Account/Login'
    $loginPolicy = Assert-CommonHeaders 'login page' $login 200
    $register = Get-Page '/Account/Register'
    $registerPolicy = Assert-CommonHeaders 'registration page' $register 200
    Assert-CommonHeaders 'CSS asset' (Get-Page '/css/site.css') 200 | Out-Null
    Assert-CommonHeaders 'JavaScript asset' (Get-Page '/js/site.js') 200 | Out-Null
    $zodAsset = Get-Page '/js/vendor/zod-3.23.8/index.umd.min.js'
    Assert-CommonHeaders 'Zod vendored asset' $zodAsset 200 | Out-Null
    Check ($zodAsset.Content.Contains('Minified by jsDelivr using Terser v5.39.0')) 'vendored Zod asset serves the verified minified build'
    Check ((Header $zodAsset.Headers 'Content-Type') -match 'javascript') 'vendored Zod asset is served as JavaScript'
    Assert-CommonHeaders 'favicon asset' (Get-Page '/favicon.svg') 200 | Out-Null
    Assert-CommonHeaders 'health check' (Get-Page '/healthz') 200 | Out-Null
    Assert-CommonHeaders 'unknown route' (Get-Page '/this-page-does-not-exist') 404 | Out-Null

    Check ((Directive $loginPolicy 'default-src') -eq "'self'") 'policy defaults to same-origin'
    Check ((Directive $loginPolicy 'frame-ancestors') -eq "'none'") 'policy declares frame-ancestors none'
    Check ((Directive $loginPolicy 'script-src-attr') -eq "'none'") 'inline event handler attributes are denied'
    Check ((Directive $loginPolicy 'connect-src') -eq "'self'") 'connect-src stays same-origin'
    Check ((Directive $loginPolicy 'form-action') -eq "'self'") 'forms can only post to this application'
    Check ((Directive $loginPolicy 'base-uri') -eq "'self'") 'base-uri stays same-origin'
    Check ((Directive $loginPolicy 'object-src') -eq "'none'") 'object-src is denied'
    Check ((Directive $loginPolicy 'frame-src') -eq "'none'") 'frame-src is denied'
    Check ((Directive $loginPolicy 'worker-src') -eq "'none'") 'worker-src is denied'
    Check ((Directive $loginPolicy 'media-src') -eq "'none'") 'media-src is denied'
    Check ((Directive $loginPolicy 'manifest-src') -eq "'none'") 'manifest-src is denied'

    $scriptSrc = Directive $loginPolicy 'script-src'
    $scriptTokens = Tokens $scriptSrc
    Check ($scriptTokens -contains "'self'") 'script-src allows same-origin scripts'
    Check (-not ($scriptTokens -contains 'https://cdn.jsdelivr.net')) 'script-src has no third-party Zod CDN'
    Check (-not ($scriptTokens -contains "'unsafe-inline'")) 'script-src does not allow unsafe-inline'
    Check (-not ($scriptTokens -contains "'unsafe-eval'")) 'script-src does not allow unsafe-eval'
    $nonceMatch = [regex]::Match([string]$scriptSrc, "'nonce-([A-Za-z0-9_-]{22})'")
    Check $nonceMatch.Success 'script-src contains a 128-bit base64url nonce'
    $nonce = $nonceMatch.Groups[1].Value
    $scriptElem = Directive $loginPolicy 'script-src-elem'
    Check ($scriptElem -match [regex]::Escape("'nonce-$nonce'")) 'script-src-elem carries the same nonce'
    Check (-not ((Tokens $scriptElem) -contains 'https://cdn.jsdelivr.net')) 'script-src-elem has no third-party Zod CDN'

    $styleTokens = Tokens (Directive $loginPolicy 'style-src')
    Check ($styleTokens -contains "'self'") 'style-src allows same-origin stylesheets'
    Check ($styleTokens -contains 'https://fonts.googleapis.com') 'style-src explicitly allows Google Fonts CSS'
    Check (-not ($styleTokens -contains "'unsafe-inline'")) 'style-src does not allow inline style elements'
    Check ((Directive $loginPolicy 'style-src-attr') -eq "'none'") 'style-src-attr denies inline style attributes'
    $fontTokens = Tokens (Directive $loginPolicy 'font-src')
    Check ($fontTokens -contains "'self'") 'font-src allows same-origin font files'
    Check ($fontTokens -contains 'https://fonts.gstatic.com') 'font-src explicitly allows Google Fonts files'
    $imgTokens = Tokens (Directive $loginPolicy 'img-src')
    Check ($imgTokens -contains "'self'") 'img-src allows same-origin images'
    Check ($imgTokens -contains 'https://independent-placeholder.supabase.co') 'img-src includes the configured Supabase origin'
    Check (-not ($imgTokens -contains '*')) 'img-src contains no wildcard'
    Check (-not [regex]::IsMatch($loginPolicy, '(^|[\s;])\*($|[\s;])')) 'policy contains no unrestricted wildcard'
    Check (-not $loginPolicy.Contains('unsafe-eval')) 'policy never permits unsafe-eval'

    $loginNonces = InlineNonces $login.Content
    Check ($loginNonces.Count -ge 2) "login page renders both inline scripts with nonces (found $($loginNonces.Count))"
    Check (($loginNonces | Select-Object -Unique).Count -eq 1) 'login inline scripts share one nonce'
    Check ($loginNonces[0] -eq $nonce) 'login body nonce matches the response header nonce'
    $registerNonceMatch = [regex]::Match([string](Directive $registerPolicy 'script-src'), "'nonce-([A-Za-z0-9_-]{22})'")
    Check $registerNonceMatch.Success 'registration response has a nonce'
    $registerNonce = $registerNonceMatch.Groups[1].Value
    $registerNonces = InlineNonces $register.Content
    Check ($registerNonces.Count -ge 2) "registration page renders both inline scripts with nonces (found $($registerNonces.Count))"
    Check (($registerNonces | Select-Object -Unique).Count -eq 1) 'registration inline scripts share one nonce'
    Check ($registerNonces[0] -eq $registerNonce) 'registration body nonce matches the response header nonce'

    Check ($login.Content.Contains('/js/vendor/zod-3.23.8/index.umd.min.js')) 'Zod is served from the local application on the login page'
    Check ($register.Content.Contains('/js/vendor/zod-3.23.8/index.umd.min.js')) 'Zod is served from the local application on the registration page'
    Check (-not $login.Content.Contains('cdn.jsdelivr.net')) 'login page does not request jsDelivr'
    Check (-not $register.Content.Contains('cdn.jsdelivr.net')) 'registration page does not request jsDelivr'
    Check ($login.Content.Contains("localStorage.getItem('glambymariya-sidebar')")) 'sidebar inline script remains present'
    Check ($login.Content.Contains('schema.safeParse')) 'Zod validation inline script remains present'
    Check (-not $login.Content.Contains('style=')) 'login page has no inline style attributes'
    Check (-not $register.Content.Contains('style=')) 'registration page has no inline style attributes'
    Check ($login.Content.Contains('eye-closed is-hidden')) 'eye-closed icon uses a CSS class instead of an inline style'

    Write-Host "ALL CSP REPORT-ONLY HTTP CHECKS PASSED ($($script:checks) checks)"
} finally {
    taskkill /PID $server.Id /T /F 2>&1 | Out-Null
}
