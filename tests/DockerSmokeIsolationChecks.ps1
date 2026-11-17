$ErrorActionPreference = 'Stop'
$checker = Join-Path $PSScriptRoot '..\tools\Test-DockerSmokeIsolation.ps1'
$image = 'sha256:' + ('a' * 64)
$valid = '[{"Image":"' + $image + '","State":{"Running":true},"HostConfig":{"NetworkMode":"none","AutoRemove":true},"Mounts":[]}]'
$cases = @(
    @{ Name='explicit empty mounts'; Json=$valid; Accept=$true },
    @{ Name='whitespace empty mounts'; Json=$valid.Replace('"Mounts":[]', '"Mounts": [  ]'); Accept=$true },
    @{ Name='escaped name with explicit empty mounts'; Json=$valid.Replace('"Mounts":[]', '"\u004dounts":[]'); Accept=$true },
    @{ Name='Docker Name field does not shadow XML element name'; Json=$valid.Replace('"Image":', '"Name":"/synthetic-smoke","Image":'); Accept=$true },
    @{ Name='Docker-shaped metadata with empty mounts'; Json=$valid.Replace('"Image":', '"Id":"synthetic-container","Name":"/synthetic-smoke","Path":"dotnet","Args":["GlamByMariya.Web.dll"],"Config":{"Env":["PORT=8080"],"Labels":{}},"NetworkSettings":{"Networks":{"none":{}}},"Image":'); Accept=$true },
    @{ Name='Docker Name field does not bypass actual mounts'; Json=$valid.Replace('"Image":', '"Name":"/synthetic-smoke","Image":').Replace('"Mounts":[]','"Mounts":[{"Type":"bind"}]') },
    @{ Name='Docker Name field does not bypass null mounts'; Json=$valid.Replace('"Image":', '"Name":"/synthetic-smoke","Image":').Replace('"Mounts":[]','"Mounts":null') },
    @{ Name='null mounts'; Json=$valid.Replace('"Mounts":[]', '"Mounts":null') },
    @{ Name='missing mounts'; Json=$valid.Replace(',"Mounts":[]', '') },
    @{ Name='one bind mount'; Json=$valid.Replace('"Mounts":[]', '"Mounts":[{"Type":"bind","Source":"/synthetic","Destination":"/app"}]') },
    @{ Name='one named volume'; Json=$valid.Replace('"Mounts":[]', '"Mounts":[{"Type":"volume"}]') },
    @{ Name='one tmpfs mount'; Json=$valid.Replace('"Mounts":[]', '"Mounts":[{"Type":"tmpfs"}]') },
    @{ Name='multiple mounts'; Json=$valid.Replace('"Mounts":[]', '"Mounts":[{},{}]') },
    @{ Name='null array entry is not an empty list'; Json=$valid.Replace('"Mounts":[]', '"Mounts":[null]') },
    @{ Name='string pretending to be mounts'; Json=$valid.Replace('"Mounts":[]', '"Mounts":"[]"') },
    @{ Name='object pretending to be mounts'; Json=$valid.Replace('"Mounts":[]', '"Mounts":{}') },
    @{ Name='boolean pretending to be mounts'; Json=$valid.Replace('"Mounts":[]', '"Mounts":false') },
    @{ Name='number pretending to be mounts'; Json=$valid.Replace('"Mounts":[]', '"Mounts":0') },
    @{ Name='incorrectly cased mounts field'; Json=$valid.Replace('"Mounts"', '"mounts"') },
    @{ Name='duplicate mounts field'; Json=$valid.Replace('"Mounts":[]', '"Mounts":[],"Mounts":[{}]') },
    @{ Name='escaped duplicate mounts field'; Json=$valid.Replace('"Mounts":[]', '"Mounts":[],"\u004dounts":[{}]') },
    @{ Name='wrong image identity'; Json=$valid.Replace($image, ('sha256:' + ('b' * 64))) },
    @{ Name='duplicate image identity'; Json=$valid.Replace('"Image":', '"Image":"' + $image + '","Image":') },
    @{ Name='stopped container'; Json=$valid.Replace('"Running":true', '"Running":false') },
    @{ Name='string running flag'; Json=$valid.Replace('"Running":true', '"Running":"true"') },
    @{ Name='external network mode'; Json=$valid.Replace('"NetworkMode":"none"', '"NetworkMode":"bridge"') },
    @{ Name='duplicate network mode'; Json=$valid.Replace('"NetworkMode":"none"', '"NetworkMode":"none","NetworkMode":"host"') },
    @{ Name='auto-remove disabled'; Json=$valid.Replace('"AutoRemove":true', '"AutoRemove":false') },
    @{ Name='string auto-remove flag'; Json=$valid.Replace('"AutoRemove":true', '"AutoRemove":"true"') },
    @{ Name='missing host configuration'; Json=$valid.Replace('"HostConfig"', '"OtherConfig"') },
    @{ Name='empty inspection list'; Json='[]' },
    @{ Name='multiple inspection objects'; Json=('[' + $valid.Substring(1,$valid.Length-2) + ',' + $valid.Substring(1,$valid.Length-2) + ']') },
    @{ Name='object instead of inspection list'; Json=$valid.Substring(1,$valid.Length-2) },
    @{ Name='null inspection entry'; Json='[null]' },
    @{ Name='null JSON'; Json='null' },
    @{ Name='null input'; Json=$null },
    @{ Name='blank input'; Json=' ' },
    @{ Name='truncated JSON'; Json=$valid.Substring(0,$valid.Length-2) },
    @{ Name='malformed JSON and redaction'; Json='PRIVATE-synthetic-inspection-not-json' },
    @{ Name='trailing non-JSON content'; Json=($valid + ' PRIVATE-synthetic-inspection') }
)
$checks = 0
foreach ($case in $cases) {
    $accepted = $false
    try {
        $output = @(& $checker -InspectionJson $case.Json -ExpectedImage $image)
        $accepted = $output.Count -eq 1 -and $output[0] -is [bool] -and $output[0]
    } catch {
        if ($_.Exception.Message -match 'PRIVATE-synthetic-inspection') { throw 'FAIL: inspection contents leaked.' }
    }
    if ($accepted -ne [bool]$case.Accept) { throw ('FAIL: ' + $case.Name) }
    $checks++
    Write-Output ('PASS: ' + $case.Name)
}
try {
    $null = & $checker -InspectionJson $valid -ExpectedImage 'unreviewed-tag'
    throw 'FAIL: non-pinned expected image accepted.'
} catch {
    if ($_.Exception.Message -eq 'FAIL: non-pinned expected image accepted.') { throw }
}
$checks++
Write-Output ("PASS: $checks offline Docker isolation checks; no Docker commands, processes, network or owner configuration accessed.")
