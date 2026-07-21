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

    $fenceMarker = $null
    $fenceLength = 0
    $openingFencePattern = '^(?: {0,3})(?<marker>`{3,}|~{3,}).*$'
    $closingFencePattern = '^(?: {0,3})(?<marker>`{3,}|~{3,})[ \t]*$'
    $unsupportedHtmlPattern = '</?[A-Za-z][A-Za-z0-9-]*(?:\s[^>]*)?/?>'
    $unfinishedHtmlTagPattern = '</?[A-Za-z][A-Za-z0-9-]*(?:\s[^>]*)?$'
    $pendingHtmlTag = $null

    foreach ($line in $Content -split "`r?`n") {
        if ($null -eq $fenceMarker) {
            $openingFence = [regex]::Match($line, $openingFencePattern)
            if ($openingFence.Success) {
                $fenceMarker = $openingFence.Groups['marker'].Value[0]
                $fenceLength = $openingFence.Groups['marker'].Value.Length
                $pendingHtmlTag = $null
                continue
            }
        }
        else {
            $closingFence = [regex]::Match($line, $closingFencePattern)
            if ($closingFence.Success -and
                $closingFence.Groups['marker'].Value[0] -eq $fenceMarker -and
                $closingFence.Groups['marker'].Value.Length -ge $fenceLength) {
                $fenceMarker = $null
                $fenceLength = 0
            }

            continue
        }

        $contentWithoutInlineCode = $line -replace '`[^`]*`', ''
        if ($null -ne $pendingHtmlTag) {
            $contentWithoutInlineCode = "$pendingHtmlTag`n$contentWithoutInlineCode"
            $pendingHtmlTag = $null
        }

        if ($contentWithoutInlineCode -match $unsupportedHtmlPattern) {
            throw "$SourceName contains raw HTML that NuGet.org may display as text: $line"
        }

        $unfinishedHtmlTag = [regex]::Match($contentWithoutInlineCode, $unfinishedHtmlTagPattern)
        if ($unfinishedHtmlTag.Success) {
            $pendingHtmlTag = $unfinishedHtmlTag.Value
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
