[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot '..' 'HelmSharp.sln'

Write-Host "Auditing direct and transitive packages in $solution"
# NuGetAuditMode=all and NuGetAuditLevel=high are also set in Directory.Build.props.
# Keep the restore in this script so local and CI validation use the same command.
& dotnet restore $solution --property:Configuration=$Configuration --property:NuGetAudit=true --property:NuGetAuditMode=all --property:NuGetAuditLevel=high
if ($LASTEXITCODE -ne 0) {
    throw "NuGet restore/audit failed with exit code $LASTEXITCODE. Review the advisory and use a time-bounded entry in security/nuget-audit-exceptions.md only with maintainer approval."
}

Write-Host 'NuGet vulnerability audit passed.'
