[CmdletBinding()]
param(
    [string]$PackageDirectory = "artifacts/packages",
    [string]$ReadmePath = "README.md"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ReadmePath -PathType Leaf)) {
    throw "Package README source was not found: $ReadmePath"
}

$sourceReadme = Get-Content -LiteralPath $ReadmePath -Raw
if ($sourceReadme -notmatch '^!\[HelmSharp\]\([^\r\n)]+\)\r?\n') {
    throw "$ReadmePath must begin with the Markdown HelmSharp wordmark."
}

$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' -File)
if ($packages.Count -eq 0) {
    throw "No .nupkg files found in $PackageDirectory"
}

Add-Type -AssemblyName System.IO.Compression
foreach ($package in $packages) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $readmeEntry = $archive.GetEntry('README.md')
        if ($null -eq $readmeEntry) {
            throw "$($package.Name) does not contain README.md at the package root"
        }

        $reader = [System.IO.StreamReader]::new($readmeEntry.Open())
        try {
            $packagedReadme = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        if ($packagedReadme -ne $sourceReadme) {
            throw "$($package.Name) contains a README.md that differs from $ReadmePath"
        }

    }
    finally {
        $archive.Dispose()
    }
}

Write-Host "Validated NuGet-compatible README.md in $($packages.Count) package(s)."
