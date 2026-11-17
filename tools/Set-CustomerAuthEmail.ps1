param([switch]$SelfTest)

$ErrorActionPreference = 'Stop'
$script:stage = 'preflight'
$entry = $null

function Get-PrivateAcl([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse point.' }
    $acl = Get-Acl -LiteralPath $Path
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne 2 -or @($rules | Where-Object {
        $_.IdentityReference.Value -notin @($owner, 'S-1-5-18') -or
        $_.AccessControlType -ne 'Allow' -or
        ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne
            [Security.AccessControl.FileSystemRights]::FullControl
    }).Count) { throw 'Private ACL required.' }
    return $acl.Sddl
}

function Set-EmptyAllowlist([string]$Path, [string]$Email) {
    $temporary = $null
    $created = $false
    try {
        $script:stage = 'private-path-acl'
        $parent = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($Path))
        for ($ancestor = $parent; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
            if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse point.' }
        }
        $null = Get-PrivateAcl $parent.FullName
        $acl = Get-PrivateAcl $Path
        $script:stage = 'json-structure'
        $bytes = [IO.File]::ReadAllBytes($Path)
        $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191
        $offset = 0
        if ($bom) { $offset = 3 }
        $encoding = [Text.UTF8Encoding]::new($bom, $true)
        $raw = $encoding.GetString($bytes, $offset, $bytes.Length - $offset)
        $cfg = ConvertFrom-Json -InputObject $raw
        $script:stage = 'project-profile'
        foreach ($name in @('Deployment__V2ProjectId', 'Database__ProjectId', 'Deployment__Profile', 'Supabase__Url')) {
            $pattern = '(?m)^[ \t]*"' + [regex]::Escape($name) + '"[ \t]*:[ \t]*("(?:\\.|[^"\\])*")'
            if ([regex]::Matches($raw, $pattern).Count -ne 1) { throw 'Missing or ambiguous pin.' }
        }
        if ($cfg.Deployment__V2ProjectId -cne 'hqllkbjhufhvyooacajy' -or
            $cfg.Database__ProjectId -cne 'hqllkbjhufhvyooacajy' -or
            $cfg.Supabase__Url -cne 'https://hqllkbjhufhvyooacajy.supabase.co' -or
            $cfg.Deployment__Profile -cne 'v2-customer-auth-test') { throw 'Configuration mismatch.' }

        $script:stage = 'allowlist-empty'
        $pattern = '(?m)^[ \t]*"Authentication__IntegrationCustomerEmail"[ \t]*:[ \t]*("(?:\\.|[^"\\])*")'
        $hits = [regex]::Matches($raw, $pattern)
        if ($hits.Count -ne 1 -or $cfg.Authentication__IntegrationCustomerEmail -isnot [string] -or
            $cfg.Authentication__IntegrationCustomerEmail.Length -ne 0) { throw 'Not exactly one empty allowlist.' }
        $script:stage = 'email-format'
        $mail = [Net.Mail.MailAddress]::new($Email)
        if ($mail.Address -cne $Email -or $Email.Trim() -cne $Email -or $Email.Length -gt 254) { throw 'Invalid email.' }

        $script:stage = 'json-replacement'
        $value = $hits[0].Groups[1]
        $encoded = ConvertTo-Json -InputObject $Email -Compress
        $updated = $raw.Remove($value.Index, $value.Length).Insert($value.Index, $encoded)
        $candidate = ConvertFrom-Json -InputObject $updated
        if ($candidate.Authentication__IntegrationCustomerEmail -cne $Email) { throw 'Candidate mismatch.' }
        $script:stage = 'private-staging'
        $temporary = Join-Path $parent.FullName ('.email-stage-' + [guid]::NewGuid().ToString('N'))
        $stream = [IO.File]::Open($temporary, 'CreateNew', 'Write', 'None')
        $created = $true
        try {
            $null = Get-PrivateAcl $temporary
            $preamble = $encoding.GetPreamble()
            $payload = $encoding.GetBytes($updated)
            $stream.Write($preamble, 0, $preamble.Length)
            $stream.Write($payload, 0, $payload.Length)
            $stream.Flush($true)
        } finally { $stream.Dispose() }
        $script:stage = 'concurrent-change'
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) -cne [Convert]::ToBase64String($bytes) -or
            (Get-PrivateAcl $Path) -cne $acl) { throw 'Original changed.' }
        $script:stage = 'atomic-replace'
        # Windows PowerShell 5.1 converts $null to ""; pass a genuine null string.
        [IO.File]::Replace($temporary, $Path, [System.Management.Automation.Language.NullString]::Value)
        $script:stage = 'post-commit-verification-stop-if-failed'
        if ([IO.File]::ReadAllText($Path) -cne $updated -or (Get-PrivateAcl $Path) -cne $acl) { throw 'Verification failed.' }
    } finally {
        if ($created -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

try {
    if ($SelfTest) {
        $directory = Join-Path (Join-Path $PSScriptRoot '..\temp') ('email-allowlist-check-' + [guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $directory
        $path = Join-Path $directory 'synthetic.json'
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true, $false)
        $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl.SetOwner($owner)
        foreach ($sid in @($owner, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                $sid, 'FullControl', 'ObjectInherit,ContainerInherit', 'None', 'Allow'))
        }
        Set-Acl -LiteralPath $directory -AclObject $acl
        $fixture = @'
{
  "Deployment__V2ProjectId": "hqllkbjhufhvyooacajy",
  "Database__ProjectId": "hqllkbjhufhvyooacajy",
  "Deployment__Profile": "v2-customer-auth-test",
  "Supabase__Url": "https://hqllkbjhufhvyooacajy.supabase.co",
  "Authentication__IntegrationCustomerEmail": "",
  "Unrelated": "synthetic-unchanged-\"\\"
}
'@
        try {
            foreach ($bom in @($false, $true)) {
                [IO.File]::WriteAllText($path, $fixture, [Text.UTF8Encoding]::new($bom))
                $targetAcl = ([IO.FileInfo]::new($path)).GetAccessControl([Security.AccessControl.AccessControlSections]::Access)
                $targetAcl.SetAccessRuleProtection($true, $true)
                ([IO.FileInfo]::new($path)).SetAccessControl($targetAcl)
                $before = Get-PrivateAcl $path
                Set-EmptyAllowlist $path 'synthetic+v2@gmail.com'
                $expected = $fixture.Replace('"Authentication__IntegrationCustomerEmail": ""',
                    '"Authentication__IntegrationCustomerEmail": "synthetic+v2@gmail.com"')
                if ([IO.File]::ReadAllText($path) -cne $expected -or (Get-PrivateAcl $path) -cne $before) { throw 'Preservation test.' }
                $output = [IO.File]::ReadAllBytes($path)
                if (($output[0] -eq 239) -ne $bom) { throw 'BOM test.' }
                $saved = [Convert]::ToBase64String($output)
                foreach ($attempt in @('synthetic+v2@gmail.com', 'synthetic+different@gmail.com')) {
                    $rejected = $false
                    try { Set-EmptyAllowlist $path $attempt } catch { $rejected = $script:stage -eq 'allowlist-empty' }
                    if (!$rejected -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -cne $saved) { throw 'Nonempty guard test.' }
                }
            }
            foreach ($test in @(
                @($fixture, 'not-an-email', 'email-format'),
                @($fixture.Replace('v2-customer-auth-test', 'wrong-profile'), 'synthetic+v2@gmail.com', 'project-profile'),
                @($fixture.Replace('hqllkbjhufhvyooacajy', 'wrong-project'), 'synthetic+v2@gmail.com', 'project-profile'),
                @($fixture.Replace('  "Unrelated"', '  "Authentication__IntegrationCustomerEmail": "",' + "`n" + '  "Unrelated"'), 'synthetic+v2@gmail.com', 'allowlist-empty')
            )) {
                [IO.File]::WriteAllText($path, $test[0])
                $saved = [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))
                $rejected = $false
                try { Set-EmptyAllowlist $path $test[1] } catch { $rejected = $script:stage -eq $test[2] }
                if (!$rejected -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -cne $saved) { throw 'Rejection test.' }
            }
            Write-Output 'PASS: synthetic plus-address, JSON/text/BOM/ACL preservation and nonempty/format/project/profile/duplicate guards. No owner configuration accessed.'
        } finally {
            if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
            [IO.Directory]::Delete($directory)
        }
    } else {
        $path = Join-Path $env:LOCALAPPDATA 'GlamByMariyaV2\CustomerAuthOwner\config\secrets.json'
        $script:stage = 'allowlist-empty'
        $cfg = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($path))
        if ($cfg.Authentication__IntegrationCustomerEmail -isnot [string] -or
            $cfg.Authentication__IntegrationCustomerEmail.Length -ne 0) { throw 'Existing allowlist; separate authorization required.' }
        $script:stage = 'private-input'
        $entry = Read-Host 'One unused owner-controlled synthetic email (hidden)' -AsSecureString
        $email = ([Net.NetworkCredential]::new('', $entry)).Password.Trim()
        Set-EmptyAllowlist $path $email
        Write-Output 'PASS: email saved; unrelated configuration and ACL preserved. No environment import, launch or provider contact.'
    }
} catch {
    if ($SelfTest) { Write-Output ('Synthetic check line=' + $_.InvocationInfo.ScriptLineNumber + '; type=' + $_.Exception.GetType().Name) }
    Write-Output ('FAIL: stage=' + $script:stage + '; stop. Do not share private values or raw diagnostics; do not retry a post-commit failure.')
    exit 1
} finally {
    if ($entry) { $entry.Dispose() }
    $entry = $email = $cfg = $null
}
