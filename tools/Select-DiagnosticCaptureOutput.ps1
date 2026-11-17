# External stdout selection only. No process launch, configuration read or stderr forwarding.
function Select-DiagnosticCaptureOutput {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline=$true)][AllowNull()][AllowEmptyString()][string]$Output)

    begin {
        $stages = 'Configuration|PsqlBaseline|ProtocolConnect|SslRequest|ProtocolTls|ColdPoolAcquire|PoolDispatch|PhysicalOpen|Dns|Tcp|Tls|PostTlsLoginOrBootstrap|DriverInternalCommand|WarmPoolAcquire|ReadOnlyBegin|RestrictedIdentity|ReadinessQuery|Rollback|Complete|Launcher|WorkerInitialization|OutputCollection'
        $types = 'None|Exception|PostgresException|NpgsqlException|AuthenticationException|SocketException|TimeoutException|CryptographicException|UnauthorizedAccessException|DirectoryNotFoundException|FileNotFoundException|IOException|InvalidOperationException|ArgumentException|OperationCanceledException'
        $categories = 'None|client-baseline-failure|startup-failure|database-authentication|database-access-denied|database-schema-or-query|database-capacity|database-query|database-or-https-tls|network|timeout|canceled|cryptography|local-access-denied'
        $codes = 'None|Other|28P01|28000|42501|42P01|42703|42883|53300|3D000|57P03|XX000|HostNotFound|NoData|NetworkUnreachable|ConnectionRefused|TimedOut'
        $record = '^DBDIAG\|stage=(' + $stages + ')\|status=(Started|Passed|Failed|Unfinished|Observed|Refused)\|elapsed_ms=[0-9]+\|duration_ms=[0-9]+\|category=(' + $categories + ')\|type=(' + $types + ')\|cause=(' + $types + ')\|code=(' + $codes + ')$'
        $config = '^DBCONFIG\|project_pins=Passed\|tls=VerifyFull\|timeout_s=15\|command_timeout_s=30\|pool_min=0\|pool_max=2\|gss=(Prefer|Disable|Require)\|ssl_negotiation=(Postgres|Direct)\|root=(SystemTrust|ExplicitFile)$'
        $summary = '^(Diagnostic child stopped; exit code=[0-9]+; hard deadline reached=(True|False)\. No automatic retry\.|Independent supervisor stopped; exit code=[0-9]+\. No automatic retry\.)$'
        $native = '^DBNATIVE\|status=(Passed|Failed)\|category=(None|process-exit-failure|output-validation-failure|timeout|output-collection-failure|canceled)\|exit_code=(0|-?[1-9][0-9]{0,9})$'
    }
    process {
        foreach ($line in ($Output -split '\r?\n')) {
            if ($line -cmatch $record -or $line -cmatch $config -or $line -cmatch $summary) { Write-Output $line }
            elseif ($line -ceq 'DBNATIVE|status=Failed|category=executable-start-failure|exit_code=None') { Write-Output $line }
            elseif ($line -cmatch $native) {
                $status=$Matches[1]; $category=$Matches[2]; $exitCode=0
                if ([int]::TryParse($Matches[3],[ref]$exitCode) -and
                    (($status -ceq 'Passed' -and $category -ceq 'None' -and $exitCode -eq 0) -or
                     ($status -ceq 'Failed' -and
                      (($category -ceq 'process-exit-failure' -and $exitCode -ne 0) -or
                       ($category -in @('output-validation-failure','output-collection-failure') -and $exitCode -eq 0) -or
                       $category -in @('timeout','canceled'))))) { Write-Output $line }
            }
        }
    }
}
