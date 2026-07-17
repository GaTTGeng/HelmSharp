[CmdletBinding()]
param(
    [string]$PackageDirectory = "artifacts/packages",
    [string]$ReadmePath = "README.md"
)

$ErrorActionPreference = "Stop"

function Assert-NuGetCompatibleMarkdown {
    param(
        [string]$Content,
        [string]$SourceName
    )

    $inCodeFence = $false
    $unsupportedHtmlPattern = '^\s*</?(?:address|article|aside|blockquote|caption|center|col|colgroup|details|dialog|dir|div|dl|dt|dd|fieldset|figcaption|figure|footer|form|frame|frameset|h[1-6]|head|header|hr|html|iframe|img|input|legend|li|link|main|menu|nav|ol|p|pre|script|section|style|summary|table|tbody|td|tfoot|th|thead|title|tr|ul)\b[^>]*>\s*$'

    foreach ($line in $Content -split "`r?`n") {
        if ($line -match '^\s*(```|~~~)') {
            $inCodeFence = -not $inCodeFence
            continue
        }

        if (-not $inCodeFence -and $line -match $unsupportedHtmlPattern) {
            throw "$SourceName contains raw HTML that NuGet.org may display as text: $line"
        }
    }
}

if (-not (Test-Path -LiteralPath $ReadmePath -PathType Leaf)) {
    throw "Package README source was not found: $ReadmePath"
}

$sourceReadme = Get-Content -LiteralPath $ReadmePath -Raw
Assert-NuGetCompatibleMarkdown -Content $sourceReadme -SourceName $ReadmePath

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

        Assert-NuGetCompatibleMarkdown -Content $packagedReadme -SourceName "$($package.Name)/README.md"
    }
    finally {
        $archive.Dispose()
    }
}

Write-Host "Validated NuGet-compatible README.md in $($packages.Count) package(s)."
