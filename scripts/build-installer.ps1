[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$HashManifest,

    [Parameter(Mandatory = $true)]
    [string]$AppVersion,

    [string]$OutputDirectory,

    [string]$InstallerBaseName,

    [string]$IsccPath,

    [switch]$VerifyOnly
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
        [Parameter(Mandatory = $true)][string]$Label,
        [switch]$AllowRoot
    )

    $fullPath = Get-AbsolutePath -Path $Path
    $fullRoot = (Get-AbsolutePath -Path $Root).TrimEnd('\')
    $prefix = $fullRoot + '\'
    $isRoot = [string]::Equals($fullPath.TrimEnd('\'), $fullRoot, [System.StringComparison]::OrdinalIgnoreCase)
    if ((-not $AllowRoot -and $isRoot) -or
        (-not $isRoot -and -not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase))) {
        throw "$Label must stay below '$fullRoot': $fullPath"
    }
    return $fullPath
}

function Test-IsSameOrBelow {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $fullPath = (Get-AbsolutePath -Path $Path).TrimEnd('\')
    $fullRoot = (Get-AbsolutePath -Path $Root).TrimEnd('\')
    return [string]::Equals($fullPath, $fullRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith($fullRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)
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
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [switch]$IncludeHashManifest
    )

    Assert-NoExistingReparseComponents -Path $Directory -StopAt $script:repoRoot -Label 'Input directory'
    $items = @(Get-ChildItem -LiteralPath $Directory -Force)
    $directories = @($items | Where-Object { $_.PSIsContainer })
    if ($directories.Count -ne 0) {
        throw "Input directory must not contain subdirectories: $($directories.Name -join ', ')"
    }

    $expected = @($script:allowedFiles)
    if ($IncludeHashManifest) {
        $expected += 'PAYLOAD-SHA256.json'
    }

    $actual = @($items | ForEach-Object { $_.Name })
    $difference = @(Compare-Object -ReferenceObject $expected -DifferenceObject $actual -CaseSensitive)
    if ($difference.Count -ne 0) {
        $details = $difference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }
        throw "Payload does not match the fixed allowlist: $($details -join '; ')"
    }

    foreach ($name in $script:allowedFiles) {
        $path = Join-Path $Directory $name
        Assert-NoExistingReparseComponents -Path $path -StopAt $script:repoRoot -Label "Payload file '$name'"
        $item = Get-Item -LiteralPath $path -Force
        if ($item.PSIsContainer -or $item.Length -le 0) {
            throw "Payload entry must be a non-empty file: $name"
        }
    }

    if ($IncludeHashManifest) {
        $manifestPath = Join-Path $Directory 'PAYLOAD-SHA256.json'
        Assert-NoExistingReparseComponents -Path $manifestPath -StopAt $script:repoRoot -Label 'Staged hash manifest'
        $manifestItem = Get-Item -LiteralPath $manifestPath -Force
        if ($manifestItem.PSIsContainer -or $manifestItem.Length -le 0) {
            throw 'Staged hash manifest must be a non-empty file.'
        }
    }
}

function Read-AndVerifyManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$ManifestPath
    )

    Assert-NoExistingReparseComponents -Path $ManifestPath -StopAt $script:repoRoot -Label 'Hash manifest'
    $manifestItem = Get-Item -LiteralPath $ManifestPath -Force
    if ($manifestItem.PSIsContainer -or $manifestItem.Length -le 0) {
        throw "Hash manifest must be a non-empty regular file: $ManifestPath"
    }

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $rootProperties = @($manifest.PSObject.Properties.Name)
    $rootDifference = @(Compare-Object -ReferenceObject @('schema_version', 'app_version', 'files') -DifferenceObject $rootProperties -CaseSensitive)
    if ($rootDifference.Count -ne 0) {
        throw 'Hash manifest has missing or unknown top-level properties.'
    }
    if ([int]$manifest.schema_version -ne 1) {
        throw "Unsupported hash manifest schema: $($manifest.schema_version)"
    }
    if (-not [string]::Equals([string]$manifest.app_version, $script:AppVersion, [System.StringComparison]::Ordinal)) {
        throw "Manifest app_version '$($manifest.app_version)' does not match '$script:AppVersion'."
    }

    $records = @($manifest.files)
    if ($records.Count -ne $script:allowedFiles.Count) {
        throw "Hash manifest must contain exactly $($script:allowedFiles.Count) file records."
    }

    $recordNames = @($records | ForEach-Object { [string]$_.path })
    $nameDifference = @(Compare-Object -ReferenceObject $script:allowedFiles -DifferenceObject $recordNames -CaseSensitive)
    if ($nameDifference.Count -ne 0) {
        throw 'Hash manifest paths do not match the fixed allowlist.'
    }
    if (($recordNames | Select-Object -Unique).Count -ne $recordNames.Count) {
        throw 'Hash manifest contains duplicate paths.'
    }

    foreach ($record in $records) {
        $recordProperties = @($record.PSObject.Properties.Name)
        $propertyDifference = @(Compare-Object -ReferenceObject @('path', 'length', 'sha256') -DifferenceObject $recordProperties -CaseSensitive)
        if ($propertyDifference.Count -ne 0) {
            throw "Hash record '$($record.path)' has missing or unknown properties."
        }

        $name = [string]$record.path
        $expectedHash = [string]$record.sha256
        $expectedLength = [long]$record.length
        if ($expectedLength -le 0) {
            throw "Invalid length in hash manifest for '$name'."
        }
        if ($expectedHash -notmatch '^[A-Fa-f0-9]{64}$') {
            throw "Invalid SHA-256 in hash manifest for '$name'."
        }

        $path = Join-Path $Directory $name
        $item = Get-Item -LiteralPath $path -Force
        if ([long]$item.Length -ne $expectedLength) {
            throw "Length mismatch for '$name': expected $expectedLength, got $($item.Length)."
        }
        $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, $expectedHash, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "SHA-256 mismatch for '$name': expected $expectedHash, got $actualHash."
        }
    }

    return $manifest
}

function Find-Iscc {
    param([string]$ExplicitPath)

    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $candidates.Add((Get-AbsolutePath -Path $ExplicitPath))
    }

    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        $candidates.Add($command.Source)
    }

    foreach ($candidate in @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            $candidates.Add($candidate)
        }
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $item = Get-Item -LiteralPath $candidate -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "ISCC.exe must not be a reparse point: $candidate"
            }
            return $item.FullName
        }
    }

    throw 'Inno Setup 6 compiler (ISCC.exe) was not found. Install the official Inno Setup 6 compiler, then rerun this command; the script performs no downloads.'
}

function Write-NewUtf8Text {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Text
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $writer = New-Object System.IO.StreamWriter($stream, $encoding)
        try {
            $writer.Write($Text)
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
}

if ($AppVersion -notmatch '^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}(-[A-Za-z0-9][A-Za-z0-9.-]{0,31})?$') {
    throw "AppVersion is not an accepted semantic version: $AppVersion"
}

$repoRoot = Get-AbsolutePath -Path (Join-Path $PSScriptRoot '..')
$workRoot = Join-Path $repoRoot 'work'
$inputFull = Assert-UnderDirectory -Path $InputDirectory -Root $repoRoot -Label 'Input directory'
$manifestFull = Assert-UnderDirectory -Path $HashManifest -Root $repoRoot -Label 'Hash manifest'
if (-not (Test-Path -LiteralPath $inputFull -PathType Container)) {
    throw "Input directory does not exist: $inputFull"
}
if (-not (Test-Path -LiteralPath $manifestFull -PathType Leaf)) {
    throw "Hash manifest does not exist: $manifestFull"
}

Assert-ExactPayload -Directory $inputFull
$manifest = Read-AndVerifyManifest -Directory $inputFull -ManifestPath $manifestFull

if ($VerifyOnly) {
    [PSCustomObject]@{
        status = 'verified'
        app_version = $AppVersion
        input_directory = $inputFull
        manifest = $manifestFull
        file_count = $allowedFiles.Count
    } | ConvertTo-Json
    return
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $workRoot 'installer-output'
}
$outputFull = Assert-UnderDirectory -Path $OutputDirectory -Root $workRoot -Label 'Output directory'
if ((Test-IsSameOrBelow -Path $outputFull -Root $inputFull) -or
    (Test-IsSameOrBelow -Path $inputFull -Root $outputFull)) {
    throw 'Input and output directories must not overlap.'
}
Assert-NoExistingReparseComponents -Path $outputFull -StopAt $repoRoot -Label 'Output directory'
[System.IO.Directory]::CreateDirectory($outputFull) | Out-Null
Assert-NoExistingReparseComponents -Path $outputFull -StopAt $repoRoot -Label 'Output directory'

if ([string]::IsNullOrWhiteSpace($InstallerBaseName)) {
    $InstallerBaseName = "InvokersRu-Setup-$AppVersion"
}
if ($InstallerBaseName -notmatch '^InvokersRu-[A-Za-z0-9._-]{1,80}$') {
    throw "InstallerBaseName is unsafe: $InstallerBaseName"
}

$installerPath = Join-Path $outputFull ($InstallerBaseName + '.exe')
$hashSidecarPath = $installerPath + '.sha256'
if ((Test-Path -LiteralPath $installerPath) -or (Test-Path -LiteralPath $hashSidecarPath)) {
    throw "Installer output already exists; refusing to overwrite: $installerPath"
}

$compiler = Find-Iscc -ExplicitPath $IsccPath
$stageRoot = Join-Path $workRoot 'installer-stage'
if ((Test-IsSameOrBelow -Path $outputFull -Root $stageRoot) -or
    (Test-IsSameOrBelow -Path $stageRoot -Root $outputFull)) {
    throw "Output directory must not overlap the reserved stage root: $stageRoot"
}
[System.IO.Directory]::CreateDirectory($stageRoot) | Out-Null
Assert-NoExistingReparseComponents -Path $stageRoot -StopAt $repoRoot -Label 'Stage root'
$stage = Join-Path $stageRoot ([Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($stage) | Out-Null

foreach ($name in $allowedFiles) {
    [System.IO.File]::Copy((Join-Path $inputFull $name), (Join-Path $stage $name), $false)
}
[System.IO.File]::Copy($manifestFull, (Join-Path $stage 'PAYLOAD-SHA256.json'), $false)
Assert-ExactPayload -Directory $stage -IncludeHashManifest
$manifest = Read-AndVerifyManifest -Directory $stage -ManifestPath (Join-Path $stage 'PAYLOAD-SHA256.json')

$issPath = Join-Path $repoRoot 'installer\InvokersRu.iss'
$compilerArguments = @(
    "/DSourceDir=$stage",
    "/DOutputDir=$outputFull",
    "/DAppVersion=$AppVersion",
    "/DInstallerBaseName=$InstallerBaseName",
    $issPath
)

& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe failed with exit code $LASTEXITCODE."
}

Assert-ExactPayload -Directory $stage -IncludeHashManifest
$manifest = Read-AndVerifyManifest -Directory $stage -ManifestPath (Join-Path $stage 'PAYLOAD-SHA256.json')
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "ISCC.exe reported success but the installer is missing: $installerPath"
}
$installerItem = Get-Item -LiteralPath $installerPath -Force
if ($installerItem.Length -le 0) {
    throw "Compiled installer is empty: $installerPath"
}

$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToUpperInvariant()
Write-NewUtf8Text -Path $hashSidecarPath -Text "$installerHash *$($installerItem.Name)`r`n"

[PSCustomObject]@{
    status = 'built'
    app_version = $AppVersion
    installer = $installerPath
    sha256 = $installerHash
    sha256_file = $hashSidecarPath
    staged_payload = $stage
    file_count = $allowedFiles.Count
    compiler = $compiler
} | ConvertTo-Json
