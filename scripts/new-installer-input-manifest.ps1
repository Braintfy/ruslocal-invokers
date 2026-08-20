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
Set-StrictMode -Version 3.0

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'work'))
. (Join-Path $PSScriptRoot 'path-safety.ps1')
. (Join-Path $PSScriptRoot 'windows-payload-policy.ps1')

function Get-AbsolutePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [IO.Path]::GetFullPath(
        $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
}

function Get-RelativePayloadPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootPrefix = (Get-AbsolutePath -Path $Root).TrimEnd('\') + '\'
    $fullPath = Get-AbsolutePath -Path $Path
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Payload entry escaped the input root: $fullPath"
    }
    return $fullPath.Substring($rootPrefix.Length).Replace('\', '/')
}

function Assert-NoReparseTree {
    param([Parameter(Mandatory = $true)][string]$Root)

    Assert-NoReparsePath -Path $Root -Label 'Installer payload root'
    foreach ($item in @(Get-ChildItem -LiteralPath $Root -Force -Recurse)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Installer payload contains a reparse point: $($item.FullName)"
        }
    }
}

function Write-NewUtf8Text {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Text
    )

    $encoding = New-Object Text.UTF8Encoding($false)
    $stream = New-Object IO.FileStream($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = $encoding.GetBytes($Text)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

if ($AppVersion -notmatch '^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}(-[A-Za-z0-9][A-Za-z0-9.-]{0,31})?$') {
    throw "AppVersion is not an accepted semantic version: $AppVersion"
}

$inputFull = Get-AbsolutePath -Path $InputDirectory
$manifestFull = Get-AbsolutePath -Path $ManifestPath
if (-not (Test-CanonicalPathWithin -Path $inputFull -Directory $workRoot)) {
    throw "Input directory must stay below the repository work directory: $inputFull"
}
if (-not (Test-CanonicalPathWithin -Path $manifestFull -Directory $inputFull) -or
    [IO.Path]::GetFileName($manifestFull) -cne 'PAYLOAD-SHA256.json') {
    throw "Manifest must be the new PAYLOAD-SHA256.json at the payload root: $manifestFull"
}
if (-not (Test-Path -LiteralPath $inputFull -PathType Container)) {
    throw "Input directory does not exist: $inputFull"
}
if (Test-Path -LiteralPath $manifestFull) {
    throw "Manifest already exists; refusing to overwrite it: $manifestFull"
}

Assert-NoReparseTree -Root $inputFull
Assert-OutsideProtectedRuntime -Path $inputFull -Label 'Installer payload root'

$files = @(Get-ChildItem -LiteralPath $inputFull -Force -File -Recurse)
if ($files.Count -lt 12 -or $files.Count -gt 512) {
    throw "Self-contained multi-file payload must contain 12-512 files; found $($files.Count)."
}

$records = New-Object Collections.Generic.List[object]
$seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$totalBytes = [long]0
foreach ($file in @($files | Sort-Object FullName)) {
    if ($file.Length -le 0 -or $file.Length -gt 268435456) {
        throw "Payload files must be 1 byte through 256 MiB: $($file.FullName)"
    }
    $relative = Get-RelativePayloadPath -Root $inputFull -Path $file.FullName
    Assert-WindowsPayloadRelativePath -RelativePath $relative
    if (-not $seen.Add($relative)) {
        throw "Payload contains a case-insensitive duplicate path: $relative"
    }
    $totalBytes += [long]$file.Length
    if ($totalBytes -gt 1073741824) {
        throw 'Player payload exceeds the 1 GiB build limit.'
    }
    $records.Add([ordered]@{
        path = $relative
        length = [long]$file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
    })
}

$requiredPaths = @(Get-WindowsPayloadRequiredPaths)
foreach ($required in $requiredPaths) {
    if (-not $seen.Contains($required)) {
        throw "Player payload is missing required file: $required"
    }
}

$manifest = [ordered]@{
    schema_version = 2
    kind = 'invokers-ru-windows-player-payload'
    app_version = $AppVersion
    runtime_identifier = 'win-x64'
    self_contained = $true
    publish_single_file = $false
    file_count = $records.Count
    total_bytes = $totalBytes
    files = $records.ToArray()
}
Write-NewUtf8Text -Path $manifestFull -Text (($manifest | ConvertTo-Json -Depth 6) + "`r`n")

[ordered]@{
    status = 'created'
    manifest = $manifestFull
    app_version = $AppVersion
    file_count = $records.Count
    total_bytes = $totalBytes
} | ConvertTo-Json -Depth 4
