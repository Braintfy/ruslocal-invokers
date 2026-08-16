[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$AppVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$allowedFiles = @(
    'InvokersRu.Gui.exe',
    'InvokersRu.Cli.exe',
    'ru_RU.mvp.jsonl',
    'GUI-PUBLISH.json',
    'TRUSTED-COMPATIBILITY.json',
    'PREVIEW-BUILD-REPORT.json',
    'TRANSLATION-AUDIT.json',
    'SUPERVISED-PUBLISH.json',
    'README.md',
    'TEST-INSTRUCTIONS.md',
    'LICENSE.txt',
    'glossary.ru.json',
    'style-guide.ru.md'
)

function Get-AbsolutePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath(
        $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
}

function Assert-UnderDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $fullPath = Get-AbsolutePath -Path $Path
    $fullRoot = (Get-AbsolutePath -Path $Root).TrimEnd('\')
    $prefix = $fullRoot + '\'
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must stay below '$fullRoot': $fullPath"
    }
    return $fullPath
}

function Assert-NoExistingReparseComponents {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$StopAt,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $current = Get-AbsolutePath -Path $Path
    $stop = (Get-AbsolutePath -Path $StopAt).TrimEnd('\')
    while ($current.StartsWith($stop, [System.StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Label contains an existing reparse component: $current"
            }
        }

        if ([string]::Equals($current.TrimEnd('\'), $stop, [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $current) {
            break
        }
        $current = $parent
    }
}

function Assert-ExactPayload {
    param([Parameter(Mandatory = $true)][string]$Directory)

    Assert-NoExistingReparseComponents -Path $Directory -StopAt $script:repoRoot -Label 'Input directory'
    $items = @(Get-ChildItem -LiteralPath $Directory -Force)
    $directories = @($items | Where-Object { $_.PSIsContainer })
    if ($directories.Count -ne 0) {
        throw "Input directory must not contain subdirectories: $($directories.Name -join ', ')"
    }

    $actual = @($items | ForEach-Object { $_.Name })
    $difference = @(Compare-Object -ReferenceObject $allowedFiles -DifferenceObject $actual -CaseSensitive)
    if ($difference.Count -ne 0) {
        $details = $difference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }
        throw "Payload does not match the fixed allowlist: $($details -join '; ')"
    }

    foreach ($name in $allowedFiles) {
        $path = Join-Path $Directory $name
        Assert-NoExistingReparseComponents -Path $path -StopAt $script:repoRoot -Label "Payload file '$name'"
        $item = Get-Item -LiteralPath $path -Force
        if ($item.Length -le 0) {
            throw "Payload file must not be empty: $name"
        }
    }
}

if ($AppVersion -notmatch '^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}(-[A-Za-z0-9][A-Za-z0-9.-]{0,31})?$') {
    throw "AppVersion is not an accepted semantic version: $AppVersion"
}

$repoRoot = Get-AbsolutePath -Path (Join-Path $PSScriptRoot '..')
$workRoot = Join-Path $repoRoot 'work'
$inputFull = Assert-UnderDirectory -Path $InputDirectory -Root $repoRoot -Label 'Input directory'
if (-not (Test-Path -LiteralPath $inputFull -PathType Container)) {
    throw "Input directory does not exist: $inputFull"
}

$manifestFull = Assert-UnderDirectory -Path $ManifestPath -Root $workRoot -Label 'Manifest path'
if (Test-Path -LiteralPath $manifestFull) {
    throw "Manifest already exists; refusing to overwrite it: $manifestFull"
}

$manifestParent = Split-Path -Parent $manifestFull
Assert-NoExistingReparseComponents -Path $manifestParent -StopAt $repoRoot -Label 'Manifest parent directory'
[System.IO.Directory]::CreateDirectory($manifestParent) | Out-Null
Assert-NoExistingReparseComponents -Path $manifestParent -StopAt $repoRoot -Label 'Manifest parent directory'

Assert-ExactPayload -Directory $inputFull

$files = foreach ($name in $allowedFiles) {
    $path = Join-Path $inputFull $name
    $item = Get-Item -LiteralPath $path -Force
    [ordered]@{
        path = $name
        length = [long]$item.Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
    }
}

$manifest = [ordered]@{
    schema_version = 1
    app_version = $AppVersion
    files = @($files)
}

$json = $manifest | ConvertTo-Json -Depth 5
$encoding = New-Object System.Text.UTF8Encoding($false)
$stream = [System.IO.File]::Open(
    $manifestFull,
    [System.IO.FileMode]::CreateNew,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::None)
try {
    $writer = New-Object System.IO.StreamWriter($stream, $encoding)
    try {
        $writer.Write($json)
    }
    finally {
        $writer.Dispose()
    }
}
finally {
    if ($null -ne $stream) {
        $stream.Dispose()
    }
}

[PSCustomObject]@{
    status = 'created'
    manifest = $manifestFull
    app_version = $AppVersion
    file_count = $allowedFiles.Count
} | ConvertTo-Json
