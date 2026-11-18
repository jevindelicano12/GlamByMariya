# Runs one database command of the app against the Supabase project glam-by-mariya-v2.
#   .\tools\supabase-command.ps1 -Command --database-migrate -Login postgres
#   .\tools\supabase-command.ps1 -Command --database-create-superadmin -Login glam_v2_backend
# The password is typed here, in this terminal; it is never saved or printed.
param(
    [Parameter(Mandatory = $true)][string]$Command,
    [ValidateSet('postgres', 'glam_v2_backend')][string]$Login = 'postgres'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = 'hqllkbjhufhvyooacajy'

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Database__Provider = 'PostgreSql'
$env:Database__Schema = 'public'
$env:Database__ProjectId = $project
$env:Database__Host = 'aws-1-ap-northeast-1.pooler.supabase.com'
$env:Database__Port = '5432'
$env:Database__Name = 'postgres'
$env:Database__Username = "$Login.$project"
$env:Database__RootCertificate = Join-Path $root 'Frontend\GlamByMariya.Web\App_Data\Certificates\supabase-prod-ca-2021.crt'
Remove-Item Env:ConnectionStrings__MakeupDatabase -ErrorAction SilentlyContinue

$secure = Read-Host "Password for $Login (Supabase database)" -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try { $env:Database__Password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }

try
{
    if ($Command -eq '--database-create-superadmin')
    {
        $env:Bootstrap__AllowSuperAdminSetup = 'true'
        $env:Bootstrap__OwnerFirstName = Read-Host 'Owner first name'
        $env:Bootstrap__OwnerLastName = Read-Host 'Owner last name'
        $env:Bootstrap__OwnerEmail = Read-Host 'Owner email (login)'
        $ownerSecure = Read-Host 'Owner password for the app (8-128 characters, a letter and a number)' -AsSecureString
        $ownerPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ownerSecure)
        try { $env:Bootstrap__OwnerPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ownerPointer) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ownerPointer) }
    }
    dotnet run --project (Join-Path $root 'Frontend\GlamByMariya.Web') --no-launch-profile -- $Command
}
finally
{
    foreach ($name in 'Database__Password', 'Bootstrap__OwnerPassword', 'Bootstrap__AllowSuperAdminSetup') { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
}
