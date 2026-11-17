param(
    [AllowNull()][AllowEmptyString()][string]$InspectionJson,
    [AllowNull()][AllowEmptyString()][string]$ExpectedImage
)

$ErrorActionPreference = 'Stop'
$reader = $null
try {
    if ([string]::IsNullOrWhiteSpace($InspectionJson) -or $InspectionJson.Length -gt 1MB -or
        $ExpectedImage -cnotmatch '^sha256:[0-9a-f]{64}$') {
        throw 'Invalid inspection input.'
    }
    # ConvertFrom-Json in Windows PowerShell 5.1 loses the distinction between [] and null.
    # This framework reader preserves JSON types, empty arrays and duplicate property nodes.
    Add-Type -AssemblyName System.Runtime.Serialization
    $bytes = [Text.Encoding]::UTF8.GetBytes($InspectionJson)
    $reader = [Runtime.Serialization.Json.JsonReaderWriterFactory]::CreateJsonReader(
        $bytes, [Xml.XmlDictionaryReaderQuotas]::new())
    $document = [Xml.XmlDocument]::new()
    $document.Load($reader)
    $root = $document.DocumentElement
    if ($root.GetAttribute('type') -cne 'array' -or $root.ChildNodes.Count -ne 1) {
        throw 'Expected one inspection object.'
    }
    $container = $root.FirstChild
    # Docker's "Name" child shadows XmlElement.Name in PowerShell's XML adapter.
    if ($container.get_Name() -cne 'item' -or $container.GetAttribute('type') -cne 'object') {
        throw 'Expected a container object.'
    }
    $required = {
        param([Xml.XmlElement]$Parent, [string]$Name, [string]$Type)
        # Escaped JSON names can use the reader's mapped "item" element; count both representations.
        $nodes = $Parent.SelectNodes("$Name | *[namespace-uri()='item' and @item='$Name']")
        if ($nodes.Count -ne 1 -or $nodes[0].GetAttribute('type') -cne $Type) {
            throw 'Missing, ambiguous or incorrectly typed inspection field.'
        }
        return ,($nodes[0])
    }
    $image = & $required $container 'Image' 'string'
    $state = & $required $container 'State' 'object'
    $running = & $required $state 'Running' 'boolean'
    $hostConfig = & $required $container 'HostConfig' 'object'
    $network = & $required $hostConfig 'NetworkMode' 'string'
    $autoRemove = & $required $hostConfig 'AutoRemove' 'boolean'
    $mounts = & $required $container 'Mounts' 'array'
    if ($image.InnerText -cne $ExpectedImage -or $running.InnerText -cne 'true' -or
        $network.InnerText -cne 'none' -or $autoRemove.InnerText -cne 'true' -or
        $mounts.ChildNodes.Count -ne 0) {
        throw 'Container isolation mismatch.'
    }
    $true
} catch {
    # Inspection JSON can contain credentials. Never forward parser errors or input values.
    throw [InvalidOperationException]::new('Docker smoke isolation verification failed; inspection details withheld.')
} finally {
    if ($null -ne $reader) { $reader.Dispose() }
}
