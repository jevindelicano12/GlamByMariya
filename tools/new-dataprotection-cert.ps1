# Creates the Data Protection certificate Render needs (it protects login cookies and keys).
# Writes the two Render secrets to a file OUTSIDE the repository:
#   %LOCALAPPDATA%\GlamByMariya\render-dataprotection.txt
# Copy them into Render (Environment), then delete that file.
$ErrorActionPreference = 'Stop'
$folder = Join-Path $env:LOCALAPPDATA 'GlamByMariya'
New-Item -ItemType Directory -Force -Path $folder | Out-Null
$target = Join-Path $folder 'render-dataprotection.txt'

$rsa = [System.Security.Cryptography.RSA]::Create(3072)
$request = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest(
    'CN=Glam by Mariya Data Protection', $rsa,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
$certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddYears(5))

$bytes = New-Object byte[] 24
$random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$random.GetBytes($bytes)
$password = [Convert]::ToBase64String($bytes)
$pfx = $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password)

Set-Content -Path $target -Encoding ascii -Value @(
    ('DataProtection__CertificatePfxBase64=' + [Convert]::ToBase64String($pfx)),
    ('DataProtection__CertificatePassword=' + $password))
$certificate.Dispose(); $rsa.Dispose()
Write-Host "Saved the two Render secrets to $target (valid 5 years). Paste them into Render, then delete the file."
