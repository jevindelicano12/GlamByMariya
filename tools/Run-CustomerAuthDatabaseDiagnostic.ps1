param([switch]$OwnerApproved, [switch]$SelfTest, [switch]$DisableGss, [switch]$Consolidated, [switch]$SupervisedWorker)

$ErrorActionPreference = 'Stop'
$stages = 'Configuration|PsqlBaseline|ProtocolConnect|SslRequest|ProtocolTls|ColdPoolAcquire|PoolDispatch|PhysicalOpen|Dns|Tcp|Tls|PostTlsLoginOrBootstrap|DriverInternalCommand|WarmPoolAcquire|ReadOnlyBegin|RestrictedIdentity|ReadinessQuery|Rollback|Complete|Launcher|WorkerInitialization|OutputCollection'
$types = 'None|Exception|PostgresException|NpgsqlException|AuthenticationException|SocketException|TimeoutException|CryptographicException|UnauthorizedAccessException|DirectoryNotFoundException|FileNotFoundException|IOException|InvalidOperationException|ArgumentException|OperationCanceledException'
$categories = 'None|client-baseline-failure|startup-failure|database-authentication|database-access-denied|database-schema-or-query|database-capacity|database-query|database-or-https-tls|network|timeout|canceled|cryptography|local-access-denied'
$codes = 'None|Other|28P01|28000|42501|42P01|42703|42883|53300|3D000|57P03|XX000|HostNotFound|NoData|NetworkUnreachable|ConnectionRefused|TimedOut'
$safeLine = '^(DBDIAG\|stage=(' + $stages + ')\|status=(Started|Passed|Failed|Unfinished|Observed|Refused)\|elapsed_ms=[0-9]+\|duration_ms=[0-9]+\|category=(' + $categories + ')\|type=(' + $types + ')\|cause=(' + $types + ')\|code=(' + $codes + ')|DBCONFIG\|project_pins=Passed\|tls=VerifyFull\|timeout_s=15\|command_timeout_s=30\|pool_min=0\|pool_max=2\|gss=(Prefer|Disable|Require)\|ssl_negotiation=(Postgres|Direct)\|root=(SystemTrust|ExplicitFile))$'

function Test-NativeRecord([string]$Line) {
    if ($Line -ceq 'DBNATIVE|status=Failed|category=executable-start-failure|exit_code=None') { return $true }
    if ($Line -cnotmatch '^DBNATIVE\|status=(Passed|Failed)\|category=(None|process-exit-failure|output-validation-failure|timeout|output-collection-failure|canceled)\|exit_code=(0|-?[1-9][0-9]{0,9})$') { return $false }
    $status=$Matches[1]; $category=$Matches[2]; $exitCode=0
    if (![int]::TryParse($Matches[3],[ref]$exitCode)) { return $false }
    if ($status -ceq 'Passed') { return $category -ceq 'None' -and $exitCode -eq 0 }
    return ($category -ceq 'process-exit-failure' -and $exitCode -ne 0) -or
        ($category -in @('output-validation-failure','output-collection-failure') -and $exitCode -eq 0) -or
        $category -in @('timeout','canceled')
}

function New-ClearedProcessInfo([string]$FileName) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FileName
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.EnvironmentVariables.Clear()
    foreach ($name in @('PATH', 'SystemRoot', 'WINDIR', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA',
        'TEMP', 'TMP', 'DOTNET_ROOT', 'PROCESSOR_ARCHITECTURE', 'ProgramFiles', 'ProgramFiles(x86)', 'PROGRAMDATA', 'SystemDrive')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($null -ne $value) { $info.EnvironmentVariables[$name] = $value }
    }
    $info.EnvironmentVariables['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    return $info
}

function New-PsqlBaselineInfo($Config, [string]$ProfileDirectory) {
    if ([string]::IsNullOrWhiteSpace($ProfileDirectory)) { throw 'Isolated native profile required.' }
    if ($Config.Deployment__V2ProjectId -cne 'hqllkbjhufhvyooacajy' -or
        $Config.Database__ProjectId -cne 'hqllkbjhufhvyooacajy' -or
        $Config.Supabase__Url -cne 'https://hqllkbjhufhvyooacajy.supabase.co' -or
        $Config.Deployment__Profile -cne 'v2-customer-auth-test' -or
        $Config.Database__Provider -cne 'PostgreSql' -or $Config.Database__Schema -cne 'public' -or
        $Config.Database__Username -cne 'glam_v2_backend.hqllkbjhufhvyooacajy' -or
        $Config.Database__Host -notmatch '^[a-z0-9.-]+\.pooler\.supabase\.com$' -or
        $Config.Database__Port -cne '5432' -or $Config.Database__Name -cne 'postgres' -or
        [string]::IsNullOrWhiteSpace($Config.Database__Password) -or
        ![string]::IsNullOrWhiteSpace($Config.ConnectionStrings__MakeupDatabase)) { throw 'Psql target pins failed.' }
    $info = New-ClearedProcessInfo 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
    $info.Arguments = '-X -w -qAt -v ON_ERROR_STOP=1'
    $info.RedirectStandardInput = $true
    $values = @{
        PGHOST=$Config.Database__Host; PGPORT='5432'; PGDATABASE='postgres'
        PGUSER=$Config.Database__Username; PGPASSWORD=$Config.Database__Password
        PGSSLMODE='verify-full'; PGSSLNEGOTIATION='postgres'; PGGSSENCMODE='disable'
        PGSSLSNI='1'; PGCHANNELBINDING='prefer'; PGCONNECT_TIMEOUT='15'
        PGPASSFILE='NUL'; PSQL_HISTORY='NUL'; PGAPPNAME='GlamByMariya'
        PGCLIENTENCODING='UTF8'
        PGSSLROOTCERT='system'
        HOME=$ProfileDirectory; USERPROFILE=$ProfileDirectory; APPDATA=$ProfileDirectory; LOCALAPPDATA=$ProfileDirectory
        PGSSLCERT=(Join-Path $ProfileDirectory 'no-client-certificate.crt')
        PGSSLKEY=(Join-Path $ProfileDirectory 'no-client-key.key')
        PGSSLCRL=(Join-Path $ProfileDirectory 'no-revocation-list.crl')
        PGSSLCRLDIR=$ProfileDirectory
    }
    if (![string]::IsNullOrWhiteSpace($Config.Database__RootCertificate)) {
        $values.PGSSLROOTCERT = [IO.Path]::GetFullPath($Config.Database__RootCertificate)
        if (!(Test-Path -LiteralPath $values.PGSSLROOTCERT -PathType Leaf)) { throw 'Configured public CA missing.' }
    }
    foreach ($key in $values.Keys) { $info.EnvironmentVariables[$key] = $values[$key] }
    return $info
}

function Get-RemainingBudget {
    if ($cancelSignal.WaitOne(0)) { throw 'Whole-run cancellation requested.' }
    $remaining = [Math]::Floor(($deadline - [Diagnostics.Stopwatch]::GetTimestamp()) * 1000.0 / [Diagnostics.Stopwatch]::Frequency)
    if ($remaining -lt 1) { throw 'Whole-run budget exhausted; stage refused.' }
    return [int]$remaining
}

function Wait-DiagnosticChild([int]$StageLimit) {
    $stageClock = [Diagnostics.Stopwatch]::StartNew()
    while (!$child.HasExited) {
        $remaining = Get-RemainingBudget
        if ($stageClock.ElapsedMilliseconds -ge $StageLimit) { return $false }
        $wait = [Math]::Min(100, [Math]::Min($remaining, $StageLimit-$stageClock.ElapsedMilliseconds))
        if ($wait -lt 1) { return $false }
        $null = $child.WaitForExit([int]$wait)
    }
    return $true
}

function Get-NativeOutcome([bool]$Started, [bool]$TimedOut, [bool]$Canceled,
    [Nullable[int]]$ExitCode, [bool]$OutputCollected, [AllowNull()][string]$Output) {
    $category = 'None'
    if (!$Started) { $category = 'executable-start-failure' }
    elseif ($Canceled) { $category = 'canceled' }
    elseif ($TimedOut) { $category = 'timeout' }
    elseif ($null -eq $ExitCode) { throw 'Native process termination was not established.' }
    elseif ($ExitCode -ne 0) { $category = 'process-exit-failure' }
    elseif (!$OutputCollected) { $category = 'output-collection-failure' }
    elseif ($Output.Trim() -cne '1') { $category = 'output-validation-failure' }
    [pscustomobject]@{Passed=($category -ceq 'None');Category=$category;ExitCode=$ExitCode}
}

function Assert-ManagedPreflight([string]$Dotnet, [string]$Dll) {
    $info = New-ClearedProcessInfo $Dotnet
    $info.Arguments = '"'+$Dll+'" --check-local'
    $info.WorkingDirectory = $workspace
    $child = $null
    try {
        $null = Get-RemainingBudget
        $child = [Diagnostics.Process]::Start($info)
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
        if (!(Wait-DiagnosticChild 75000)) { throw 'Local safety preflight exceeded budget.' }
        if ($child.ExitCode -ne 0 -or !$stdout.Wait(1000) -or !$stderr.Wait(1000)) { throw 'Local safety preflight refused.' }
        $record = $stdout.GetAwaiter().GetResult().Trim()
        if ($record -cnotmatch '^DBCONFIG\|project_pins=Passed\|tls=VerifyFull\|timeout_s=15\|command_timeout_s=30\|pool_min=0\|pool_max=2\|gss=(Prefer|Disable|Require)\|ssl_negotiation=Postgres\|root=(SystemTrust|ExplicitFile)$') { throw 'Local safety preflight output refused.' }
    } finally {
        if ($child) {
            if (!$child.HasExited) {
                $child.Kill()
                if (!$child.WaitForExit(1000)) { throw 'Local preflight cleanup incomplete.' }
            }
            $child.Dispose()
        }
    }
}

function Invoke-NativeBaseline($Info) {
    $null = Get-RemainingBudget
    $child = $null
    try {
        try { $child = [Diagnostics.Process]::Start($Info) }
        catch { return Get-NativeOutcome $false $false $false $null $false $null }
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
        $ioFailed = $false
        try {
            $child.StandardInput.WriteLine("BEGIN READ ONLY; SET LOCAL statement_timeout=30000; SELECT (current_user='glam_v2_backend' AND session_user='glam_v2_backend' AND current_database()='postgres' AND current_setting('transaction_read_only')='on')::int; ROLLBACK;")
            $child.StandardInput.Close()
        } catch { $ioFailed = $true }
        $finished = $false; $canceled = $false
        try { $finished = Wait-DiagnosticChild 45000 }
        catch {
            if ($cancelSignal.WaitOne(0) -or [Diagnostics.Stopwatch]::GetTimestamp() -ge $deadline) { $canceled = $true }
            else { throw }
        }
        if (!$finished) {
            if (!$child.HasExited) { $child.Kill() }
            if (!$child.WaitForExit(1000)) { throw 'Native child termination pending; sequence stopped.' }
        }
        $exitCode = $child.ExitCode
        $collected = $false; $output = $null
        try {
            $collected = !$ioFailed -and $stdout.Wait(1000) -and $stderr.Wait(1000)
            if ($collected) { $output = $stdout.GetAwaiter().GetResult(); $stderr.GetAwaiter().GetResult() }
        } catch { $collected = $false }
        return Get-NativeOutcome $true (!$finished -and !$canceled) $canceled $exitCode $collected $output
    } finally {
        if ($child) {
            if (!$child.HasExited) {
                $child.Kill()
                if (!$child.WaitForExit(1000)) { throw 'Native cleanup incomplete; sequence stopped.' }
            }
            $child.Dispose()
        }
    }
}

if ($SelfTest) {
    $valid = 'DBDIAG|stage=ColdPoolAcquire|status=Failed|elapsed_ms=15000|duration_ms=15000|category=timeout|type=NpgsqlException|cause=TimeoutException|code=None'
    if ($valid -cnotmatch $safeLine) { throw 'Diagnostic filter rejected valid synthetic evidence.' }
    foreach ($invalid in @(
        'PRIVATE-host-email-password-token',
        $valid.Replace('category=timeout', 'category=PRIVATE'),
        $valid.Replace('type=NpgsqlException', 'type=PRIVATE'),
        $valid.Replace('code=None', 'code=PRIVATE'),
        ($valid + '|password=PRIVATE')
    )) {
        if ($invalid -cmatch $safeLine) { throw 'Diagnostic filter accepted private synthetic evidence.' }
    }
    $config = 'DBCONFIG|project_pins=Passed|tls=VerifyFull|timeout_s=15|command_timeout_s=30|pool_min=0|pool_max=2|gss=Prefer|ssl_negotiation=Postgres|root=SystemTrust'
    if ($config -cnotmatch $safeLine) { throw 'Configuration filter rejected valid synthetic evidence.' }
    if ($config.Replace('gss=Prefer', 'gss=Disable') -cnotmatch $safeLine) { throw 'GSS-disabled filter check failed.' }
    $fixture = [pscustomobject]@{
        Deployment__V2ProjectId='hqllkbjhufhvyooacajy'; Database__ProjectId='hqllkbjhufhvyooacajy'
        Supabase__Url='https://hqllkbjhufhvyooacajy.supabase.co'; Deployment__Profile='v2-customer-auth-test'
        Database__Provider='PostgreSql'; Database__Schema='public'; Database__Username='glam_v2_backend.hqllkbjhufhvyooacajy'
        Database__Host='offline.pooler.supabase.com'; Database__Port='5432'; Database__Name='postgres'
        Database__Password='PRIVATE-synthetic-password'; Database__RootCertificate=''; ConnectionStrings__MakeupDatabase=''
    }
    $testInfo = New-PsqlBaselineInfo $fixture 'C:\synthetic-no-files'
    if ($testInfo.Arguments.Contains($fixture.Database__Password) -or
        $testInfo.EnvironmentVariables['PGPASSWORD'] -cne $fixture.Database__Password -or
        $testInfo.EnvironmentVariables['PGSSLMODE'] -cne 'verify-full' -or
        $testInfo.EnvironmentVariables['PGSSLROOTCERT'] -cne 'system' -or
        $testInfo.EnvironmentVariables['PGCONNECT_TIMEOUT'] -cne '15' -or
        $testInfo.EnvironmentVariables['PGGSSENCMODE'] -cne 'disable' -or
        $testInfo.EnvironmentVariables['PGSSLNEGOTIATION'] -cne 'postgres' -or
        $testInfo.EnvironmentVariables.ContainsKey('PGSERVICE') -or
        $testInfo.EnvironmentVariables.ContainsKey('DOTNET_STARTUP_HOOKS') -or
        $testInfo.EnvironmentVariables['APPDATA'] -cne 'C:\synthetic-no-files' -or
        $testInfo.EnvironmentVariables['PGSSLCERT'] -cne 'C:\synthetic-no-files\no-client-certificate.crt' -or
        $testInfo.EnvironmentVariables.ContainsKey('PGOPTIONS') -or
        $testInfo.EnvironmentVariables['PGCLIENTENCODING'] -cne 'UTF8') { throw 'Psql isolation test failed.' }
    $fixture.Database__ProjectId = 'wrong'
    $rejected = $false
    try { $null = New-PsqlBaselineInfo $fixture 'C:\synthetic-no-files' } catch { $rejected = $true }
    if (!$rejected) { throw 'Psql target mismatch accepted.' }
    foreach ($stage in @('PsqlBaseline', 'ProtocolConnect', 'SslRequest', 'ProtocolTls')) {
        if ($valid.Replace('stage=ColdPoolAcquire', ('stage='+$stage)) -cnotmatch $safeLine) { throw 'Consolidated stage filter failed.' }
    }
    $cancelSignal = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset)
    try {
        $deadline = [Diagnostics.Stopwatch]::GetTimestamp() + 5 * [Diagnostics.Stopwatch]::Frequency
        if ((Get-RemainingBudget) -le 0) { throw 'Positive budget refused.' }
        $deadline = [Diagnostics.Stopwatch]::GetTimestamp() - 1
        $rejected = $false
        try { $null = Get-RemainingBudget } catch { $rejected = $true }
        if (!$rejected) { throw 'Exhausted stage budget accepted.' }
        $deadline = [Diagnostics.Stopwatch]::GetTimestamp() + 5 * [Diagnostics.Stopwatch]::Frequency
        $null = $cancelSignal.Set()
        $rejected = $false
        try { $null = Get-RemainingBudget } catch { $rejected = $true }
        if (!$rejected) { throw 'Cancelled stage budget accepted.' }
    } finally { $cancelSignal.Dispose() }
    $fixture = $testInfo = $null
    Write-Output 'PASS: supervisor filters, synthetic psql isolation, positive/exhausted/cancelled stage budgets; no process, owner configuration or network accessed.'
    return
}
if (!$OwnerApproved) {
    Write-Output 'REFUSED: separate owner approval for the documented one-run read-only hosted diagnostic is required.'
    exit 2
}
if (!$SupervisedWorker) {
    # No configuration is read in this launcher. The independent guard contains the worker and descendants.
    $guard = $null
    $launcherClock = [Diagnostics.Stopwatch]::StartNew()
    $launcherStage = 'Launcher'
    try {
        $dll = Join-Path $PSScriptRoot 'CustomerAuthDatabaseDiagnostic\bin\Debug\net10.0\CustomerAuthDatabaseDiagnostic.dll'
        if (!(Test-Path -LiteralPath $dll -PathType Leaf)) {
            Write-Output ('DBDIAG|stage=Launcher|status=Refused|elapsed_ms='+$launcherClock.ElapsedMilliseconds+'|duration_ms=0|category=local-access-denied|type=FileNotFoundException|cause=None|code=None')
            exit 2
        }
        $info = New-ClearedProcessInfo (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source
        $mode = 'original'
        if ($DisableGss) { $mode = 'disable-gss' }
        if ($Consolidated) { $mode = 'consolidated' }
        $info.Arguments = '"'+$dll+'" --supervise-owner-approved "'+$PSCommandPath+'" '+$mode
        $guard = [Diagnostics.Process]::Start($info)
        $stdout = $guard.StandardOutput.ReadToEndAsync()
        $stderr = $guard.StandardError.ReadToEndAsync()
        $remainingHard = 90000-$launcherClock.ElapsedMilliseconds
        $finished = $remainingHard -gt 0 -and $guard.WaitForExit([int]$remainingHard)
        if (!$finished) {
            # Closing the guard's job handle kills the contained worker tree, including on guard interruption.
            if (!$guard.HasExited) { $guard.Kill() }
            $null = $guard.WaitForExit(1000)
        }
        $launcherStage = 'OutputCollection'
        $safeRecords = 0
        if ($stdout.Wait(1000)) {
            foreach ($line in ($stdout.GetAwaiter().GetResult() -split '\r?\n')) {
                if ($line -cmatch $safeLine -or (Test-NativeRecord $line)) { Write-Output $line; $safeRecords++ }
                elseif ($line -cmatch '^Diagnostic child stopped; exit code=[0-9]+; hard deadline reached=(True|False)\. No automatic retry\.$') { Write-Output $line }
            }
        }
        $result = 124
        if ($finished) { $result = $guard.ExitCode }
        if ($safeRecords -eq 0) {
            Write-Output ('DBDIAG|stage=OutputCollection|status=Failed|elapsed_ms='+$launcherClock.ElapsedMilliseconds+'|duration_ms=0|category=startup-failure|type=Exception|cause=None|code=None')
            if ($result -eq 0) { $result = 1 }
        }
        Write-Output ('Independent supervisor stopped; exit code='+$result+'. No automatic retry.')
        exit $result
    } catch {
        Write-Output ('DBDIAG|stage='+$launcherStage+'|status=Failed|elapsed_ms='+$launcherClock.ElapsedMilliseconds+'|duration_ms=0|category=startup-failure|type=Exception|cause=None|code=None')
        exit 1
    } finally {
        if ($guard) {
            if (!$guard.HasExited) { $guard.Kill(); $null = $guard.WaitForExit(1000) }
            $guard.Dispose()
        }
    }
}
$child = $null
$sandbox = $null
$cancelSignal = $null
$clock = [Diagnostics.Stopwatch]::StartNew()
$workerStage = 'WorkerInitialization'
$nativeFailed = $false
try {
    $deadline = [long]::Parse($env:GLAM_V2_DIAGNOSTIC_DEADLINE)
    $cancelSignal = [Threading.EventWaitHandle]::OpenExisting($env:GLAM_V2_DIAGNOSTIC_CANCEL)
    if ((Get-RemainingBudget) -gt 75000) { throw 'Invalid supervisor deadline.' }
    $workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $dll = Join-Path $PSScriptRoot 'CustomerAuthDatabaseDiagnostic\bin\Debug\net10.0\CustomerAuthDatabaseDiagnostic.dll'
    if (!(Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'Build the diagnostic offline first.' }
    if (@([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
        Where-Object Port -eq 7144).Count) { throw 'Customer listener present; diagnostic refused.' }
    $dotnet = (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source
    if ($Consolidated) {
        $root = Join-Path $env:LOCALAPPDATA 'GlamByMariyaV2\CustomerAuthOwner'
        $path = Join-Path $root 'config\secrets.json'
        for ($parent = [IO.DirectoryInfo]::new($root); $null -ne $parent; $parent = $parent.Parent) {
            if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse ancestor.' }
        }
        $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        foreach ($item in @($root, (Join-Path $root 'config'), $path)) {
            if ((Get-Item -LiteralPath $item -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse path.' }
            $rules = @((Get-Acl -LiteralPath $item).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
            if ($rules.Count -ne 2 -or @($rules | Where-Object {
                $_.IdentityReference.Value -notin @($owner, 'S-1-5-18') -or $_.AccessControlType -ne 'Allow' -or
                ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne [Security.AccessControl.FileSystemRights]::FullControl
            }).Count) { throw 'Private ACL required.' }
        }
        $configSnapshot = [IO.File]::ReadAllText($path)
        $configAcl = (Get-Acl -LiteralPath $path).Sddl
        foreach ($name in @('Deployment__V2ProjectId','Database__ProjectId','Supabase__Url','Deployment__Profile',
            'Database__Provider','Database__Schema','Database__Host','Database__Port','Database__Name','Database__Username','Database__Password')) {
            $pattern = '(?m)^[ \t]*"' + [regex]::Escape($name) + '"[ \t]*:[ \t]*("(?:\\.|[^"\\])*")'
            if ([regex]::Matches($configSnapshot, $pattern).Count -ne 1) { throw 'Missing or ambiguous native-client setting.' }
        }
        $cfg = ConvertFrom-Json -InputObject $configSnapshot
        Assert-ManagedPreflight $dotnet $dll # Network-free; all managed safety pins must pass before any native connection.
        $sandbox = Join-Path $env:LOCALAPPDATA ('Temp\opencode\glam-v2-native-profile-'+[guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $sandbox
        $privateAcl = [Security.AccessControl.DirectorySecurity]::new()
        $privateAcl.SetAccessRuleProtection($true, $false)
        $ownerSid = [Security.Principal.SecurityIdentifier]::new($owner)
        $privateAcl.SetOwner($ownerSid)
        foreach ($sid in @($ownerSid, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
            $privateAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                $sid, 'FullControl', 'ObjectInherit,ContainerInherit', 'None', 'Allow'))
        }
        Set-Acl -LiteralPath $sandbox -AclObject $privateAcl
        $psqlInfo = New-PsqlBaselineInfo $cfg $sandbox
        $psqlInfo.WorkingDirectory = $workspace
        $workerStage = 'PsqlBaseline'
        $stageStart = $clock.ElapsedMilliseconds
        Write-Output ('DBDIAG|stage=PsqlBaseline|status=Started|elapsed_ms='+$stageStart+'|duration_ms=0|category=None|type=None|cause=None|code=None')
        $native = Invoke-NativeBaseline $psqlInfo # One native control; no retry.
        $nativeFailed = !$native.Passed
        $status = 'Failed'; $category = 'client-baseline-failure'
        if ($native.Passed) { $status = 'Passed'; $category = 'None' }
        if ($native.Category -in @('timeout','canceled')) { $category = $native.Category }
        Write-Output ('DBDIAG|stage=PsqlBaseline|status='+$status+'|elapsed_ms='+$clock.ElapsedMilliseconds+'|duration_ms='+($clock.ElapsedMilliseconds-$stageStart)+'|category='+$category+'|type=None|cause=None|code=None')
        $nativeExit = 'None'
        if ($null -ne $native.ExitCode) { $nativeExit = $native.ExitCode.ToString([Globalization.CultureInfo]::InvariantCulture) }
        Write-Output ('DBNATIVE|status='+$status+'|category='+$native.Category+'|exit_code='+$nativeExit)
        $cfg = $psqlInfo = $null
        $null = Get-RemainingBudget # Cancellation and exhausted budgets still stop all later stages.
        if ($native.Category -ceq 'canceled') { throw 'Whole-run cancellation requested.' }
        if ([IO.File]::ReadAllText($path) -cne $configSnapshot -or (Get-Acl -LiteralPath $path).Sddl -cne $configAcl) {
            throw 'Owner configuration changed between clients; managed diagnostic refused.'
        }
    }
    $workerStage = 'WorkerInitialization'
    $info = New-ClearedProcessInfo $dotnet
    $info.Arguments = '"' + $dll + '" --run-once-owner-approved'
    if ($DisableGss) { $info.Arguments += ' --disable-gss' }
    if ($Consolidated) { $info.Arguments = '"' + $dll + '" --consolidated-owner-approved' }
    $info.EnvironmentVariables['GLAM_V2_DIAGNOSTIC_DEADLINE'] = $env:GLAM_V2_DIAGNOSTIC_DEADLINE
    $info.EnvironmentVariables['GLAM_V2_DIAGNOSTIC_CANCEL'] = $env:GLAM_V2_DIAGNOSTIC_CANCEL
    $info.WorkingDirectory = $workspace
    $null = Get-RemainingBudget
    $child = [Diagnostics.Process]::Start($info) # One managed child; no retry or concurrent children.
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    $finished = Wait-DiagnosticChild 75000
    if (!$finished) { $child.Kill(); if (!$child.WaitForExit(1000)) { throw 'Managed child termination pending.' } }
    $workerStage = 'OutputCollection'
    foreach ($line in ($stdout.GetAwaiter().GetResult() -split '\r?\n')) {
        if ($line -cmatch $safeLine -or (Test-NativeRecord $line)) { Write-Output $line }
    }
    $null = $stderr.GetAwaiter().GetResult() # Discard raw stderr; never persist it.
    if ($Consolidated -and ([IO.File]::ReadAllText($path) -cne $configSnapshot -or (Get-Acl -LiteralPath $path).Sddl -cne $configAcl)) {
        throw 'Owner configuration changed during the consolidated run.'
    }
    Write-Output ('Diagnostic child stopped; exit code=' + $child.ExitCode + '; hard deadline reached=' + (!$finished) + '. No automatic retry.')
    if ($nativeFailed -and $child.ExitCode -eq 0) { exit 1 }
    exit $child.ExitCode
} catch {
    if ($cancelSignal -and ($cancelSignal.WaitOne(0) -or [Diagnostics.Stopwatch]::GetTimestamp() -ge $deadline)) {
        Write-Output ('DBDIAG|stage='+$workerStage+'|status=Failed|elapsed_ms='+$clock.ElapsedMilliseconds+'|duration_ms=0|category=canceled|type=OperationCanceledException|cause=None|code=None')
        exit 125
    }
    Write-Output ('DBDIAG|stage='+$workerStage+'|status=Failed|elapsed_ms='+$clock.ElapsedMilliseconds+'|duration_ms=0|category=startup-failure|type=Exception|cause=None|code=None')
    exit 1
} finally {
    if ($child) {
        if (!$child.HasExited) { $child.Kill(); $null = $child.WaitForExit(1000) }
        $child.Dispose()
    }
    if ($cancelSignal) { $cancelSignal.Dispose() }
    if ($sandbox -and [IO.Directory]::Exists($sandbox)) {
        if (@(Get-ChildItem -LiteralPath $sandbox -Force).Count -eq 0) { [IO.Directory]::Delete($sandbox) }
        else { Write-Output 'Private native-client sandbox retained because it was not empty; no file contents displayed.' }
    }
    $info = $cfg = $psqlInfo = $stdout = $stderr = $line = $configSnapshot = $configAcl = $null
}
