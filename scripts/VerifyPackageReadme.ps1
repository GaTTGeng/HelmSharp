[CmdletBinding()]
param(
    [string]$PackageDirectory = "artifacts/packages",
    [string]$ReadmePath = "README.md"
)

$ErrorActionPreference = "Stop"

function Remove-InlineCodeSpans {
    param([string]$Content)

    $result = [System.Text.StringBuilder]::new()
    $index = 0
    while ($index -lt $Content.Length) {
        if ($Content[$index] -ne [char]96 -or (Test-EscapedMarkdownCharacter -Content $Content -Index $index)) {
            [void]$result.Append($Content[$index])
            $index++
            continue
        }

        $lineStart = if ($index -eq 0) { 0 } else { $Content.LastIndexOf("`n", $index - 1) + 1 }
        $indentLength = $index - $lineStart
        $delimiterLength = 1
        while ($index + $delimiterLength -lt $Content.Length -and $Content[$index + $delimiterLength] -eq [char]96) {
            $delimiterLength++
        }

        if ($indentLength -le 3 -and $delimiterLength -ge 3 -and $Content.Substring($lineStart, $indentLength) -match '^ *$') {
            [void]$result.Append([string]::new([char]96, $delimiterLength))
            $index += $delimiterLength
            continue
        }

        $closingIndex = -1
        $searchIndex = $index + $delimiterLength
        while ($searchIndex -lt $Content.Length) {
            $candidateIndex = $Content.IndexOf([char]96, $searchIndex)
            if ($candidateIndex -lt 0) {
                break
            }

            $candidateLength = 1
            while ($candidateIndex + $candidateLength -lt $Content.Length -and $Content[$candidateIndex + $candidateLength] -eq [char]96) {
                $candidateLength++
            }

            if ($candidateLength -eq $delimiterLength -and -not (Test-EscapedMarkdownCharacter -Content $Content -Index $candidateIndex)) {
                $closingIndex = $candidateIndex
                break
            }

            $searchIndex = $candidateIndex + $candidateLength
        }

        if ($closingIndex -lt 0) {
            [void]$result.Append([string]::new([char]96, $delimiterLength))
            $index += $delimiterLength
            continue
        }

        for ($spanIndex = $index; $spanIndex -lt $closingIndex + $delimiterLength; $spanIndex++) {
            $maskedCharacter = if ($Content[$spanIndex] -eq "`r" -or $Content[$spanIndex] -eq "`n") {
                $Content[$spanIndex]
            }
            else {
                ' '
            }

            [void]$result.Append($maskedCharacter)
        }
        $index = $closingIndex + $delimiterLength
    }

    return $result.ToString()
}

function Test-EscapedMarkdownCharacter {
    param(
        [string]$Content,
        [int]$Index
    )

    $backslashCount = 0
    for ($previousIndex = $Index - 1; $previousIndex -ge 0 -and $Content[$previousIndex] -eq [char]92; $previousIndex--) {
        $backslashCount++
    }

    return $backslashCount % 2 -eq 1
}

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

    $contentWithoutInlineCode = Remove-InlineCodeSpans -Content $Content
    foreach ($line in $contentWithoutInlineCode -split "`r?`n") {
        if ($null -eq $fenceMarker) {
            $openingFence = [regex]::Match($line, $openingFencePattern)
            if ($openingFence.Success) {
                $marker = $openingFence.Groups['marker'].Value
                $info = $line.Substring($openingFence.Groups['marker'].Index + $marker.Length)
                if ($marker[0] -ne [char]96 -or -not $info.Contains([string][char]96)) {
                    $fenceMarker = $marker[0]
                    $fenceLength = $marker.Length
                    $pendingHtmlTag = $null
                    continue
                }
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

        if ($null -ne $pendingHtmlTag) {
            $line = "$pendingHtmlTag`n$line"
            $pendingHtmlTag = $null
        }

        if ($line -match $unsupportedHtmlPattern) {
            throw "$SourceName contains raw HTML that NuGet.org may display as text: $line"
        }

        $unfinishedHtmlTag = [regex]::Match($line, $unfinishedHtmlTagPattern)
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
