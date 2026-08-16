[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GuiPublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$SupportPackageDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$HashManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$AppVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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
    if (-not $fullPath.StartsWith($fullRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
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

function Get-VerifiedSourceFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Assert-NoExistingReparseComponents -Path $Path -StopAt $script:repoRoot -Label $Label
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label is missing: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Length -le 0) {
        throw "$Label must not be empty: $Path"
    }
    return $item.FullName
}

function Assert-ReceiptHash {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($Expected -notmatch '^[A-Fa-f0-9]{64}$') {
        throw "GUI publish receipt contains an invalid $Label SHA-256."
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if (-not [string]::Equals($actual, $Expected, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label SHA-256 does not match GUI-PUBLISH.json: expected $Expected, got $actual."
    }
}

if ($AppVersion -notmatch '^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}(-[A-Za-z0-9][A-Za-z0-9.-]{0,31})?$') {
    throw "AppVersion is not an accepted semantic version: $AppVersion"
}

$repoRoot = Get-AbsolutePath -Path (Join-Path $PSScriptRoot '..')
$workRoot = Join-Path $repoRoot 'work'
$guiRoot = Assert-UnderDirectory -Path $GuiPublishDirectory -Root $workRoot -Label 'GUI publish directory'
$supportRoot = Assert-UnderDirectory -Path $SupportPackageDirectory -Root $workRoot -Label 'Support package directory'
$outputFull = Assert-UnderDirectory -Path $OutputDirectory -Root $workRoot -Label 'Output directory'
$manifestFull = Assert-UnderDirectory -Path $HashManifestPath -Root $workRoot -Label 'Hash manifest path'

foreach ($sourceRoot in @($guiRoot, $supportRoot)) {
    if ((Test-IsSameOrBelow -Path $outputFull -Root $sourceRoot) -or
        (Test-IsSameOrBelow -Path $sourceRoot -Root $outputFull)) {
        throw 'Installer output directory must not overlap a source directory.'
    }
}
if (Test-IsSameOrBelow -Path $manifestFull -Root $outputFull) {
    throw 'Hash manifest must be outside the fixed-allowlist payload directory.'
}

foreach ($sourceRoot in @($guiRoot, $supportRoot)) {
    Assert-NoExistingReparseComponents -Path $sourceRoot -StopAt $repoRoot -Label 'Source directory'
    if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
        throw "Source directory is missing: $sourceRoot"
    }
}
Assert-NoExistingReparseComponents -Path $outputFull -StopAt $repoRoot -Label 'Output directory'
Assert-NoExistingReparseComponents -Path $manifestFull -StopAt $repoRoot -Label 'Hash manifest path'
if ((Test-Path -LiteralPath $outputFull) -or (Test-Path -LiteralPath $manifestFull)) {
    throw 'Output directory and hash manifest must both be new; refusing to overwrite existing paths.'
}

$guiPath = Get-VerifiedSourceFile -Path (Join-Path $guiRoot 'InvokersRu.Gui.exe') -Label 'GUI executable'
$cliPath = Get-VerifiedSourceFile -Path (Join-Path $guiRoot 'InvokersRu.Cli.exe') -Label 'CLI executable'
$translationPath = Get-VerifiedSourceFile -Path (Join-Path $guiRoot 'ru_RU.mvp.jsonl') -Label 'Translation catalog'
$receiptPath = Get-VerifiedSourceFile -Path (Join-Path $guiRoot 'GUI-PUBLISH.json') -Label 'GUI publish receipt'
$receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json

$requiredReceiptProperties = @(
    'schema',
    'kind',
    'version',
    'mode',
    'gui_apply_enabled',
    'runtime_loader_validated',
    'gui_sha256',
    'cli_sha256',
    'translations_sha256',
    'trusted_manifest_sha256',
    'build_id'
)
$receiptProperties = @($receipt.PSObject.Properties.Name)
$missingReceiptProperties = @($requiredReceiptProperties | Where-Object { $receiptProperties -cnotcontains $_ })
if ($missingReceiptProperties.Count -ne 0) {
    throw "GUI-PUBLISH.json lacks diagnostic safety properties: $($missingReceiptProperties -join ', ')"
}
if ([int]$receipt.schema -ne 1 -or [string]$receipt.kind -ne 'invokers-ru-gui-publish') {
    throw 'GUI-PUBLISH.json is not a supported GUI publish receipt.'
}
if (-not ($receipt.gui_apply_enabled -is [bool]) -or
    -not ($receipt.runtime_loader_validated -is [bool])) {
    throw 'GUI-PUBLISH.json diagnostic safety flags must be JSON booleans.'
}
if ([string]$receipt.mode -ne 'diagnostic-preview' -or
    [bool]$receipt.gui_apply_enabled -ne $false -or
    [bool]$receipt.runtime_loader_validated -ne $false) {
    throw 'Only a diagnostic GUI with apply disabled and an unvalidated runtime loader may enter this installer.'
}

Assert-ReceiptHash -Path $guiPath -Expected ([string]$receipt.gui_sha256) -Label 'GUI executable'
Assert-ReceiptHash -Path $cliPath -Expected ([string]$receipt.cli_sha256) -Label 'CLI executable'
Assert-ReceiptHash -Path $translationPath -Expected ([string]$receipt.translations_sha256) -Label 'Translation catalog'

$supportCli = Get-VerifiedSourceFile -Path (Join-Path $supportRoot 'InvokersRu.Cli.exe') -Label 'Support CLI executable'
$supportTranslation = Get-VerifiedSourceFile -Path (Join-Path $supportRoot 'ru_RU.mvp.jsonl') -Label 'Support translation catalog'
Assert-ReceiptHash -Path $supportCli -Expected ([string]$receipt.cli_sha256) -Label 'Support CLI executable'
Assert-ReceiptHash -Path $supportTranslation -Expected ([string]$receipt.translations_sha256) -Label 'Support translation catalog'

$trustedManifest = Get-VerifiedSourceFile -Path (Join-Path $supportRoot 'TRUSTED-COMPATIBILITY.json') -Label 'Trusted compatibility manifest'
Assert-ReceiptHash -Path $trustedManifest -Expected ([string]$receipt.trusted_manifest_sha256) -Label 'Trusted compatibility manifest'

$mapping = [ordered]@{
    'InvokersRu.Gui.exe' = $guiPath
    'InvokersRu.Cli.exe' = $cliPath
    'ru_RU.mvp.jsonl' = $translationPath
    'GUI-PUBLISH.json' = $receiptPath
    'TRUSTED-COMPATIBILITY.json' = $trustedManifest
    'PREVIEW-BUILD-REPORT.json' = Get-VerifiedSourceFile -Path (Join-Path $supportRoot 'PREVIEW-BUILD-REPORT.json') -Label 'Preview build report'
    'TRANSLATION-AUDIT.json' = Get-VerifiedSourceFile -Path (Join-Path $supportRoot 'TRANSLATION-AUDIT.json') -Label 'Translation audit'
    'SUPERVISED-PUBLISH.json' = Get-VerifiedSourceFile -Path (Join-Path $supportRoot 'SUPERVISED-PUBLISH.json') -Label 'Supervised publish receipt'
    'README.md' = Get-VerifiedSourceFile -Path (Join-Path $repoRoot 'installer\DIAGNOSTIC-README.md') -Label 'Diagnostic README'
    'TEST-INSTRUCTIONS.md' = Get-VerifiedSourceFile -Path (Join-Path $repoRoot 'installer\DIAGNOSTIC-TEST-INSTRUCTIONS.md') -Label 'Diagnostic test instructions'
    'LICENSE.txt' = Get-VerifiedSourceFile -Path (Join-Path $repoRoot 'LICENSE') -Label 'Project license'
    'glossary.ru.json' = Get-VerifiedSourceFile -Path (Join-Path $supportRoot 'glossary.ru.json') -Label 'Russian glossary'
    'style-guide.ru.md' = Get-VerifiedSourceFile -Path (Join-Path $supportRoot 'style-guide.ru.md') -Label 'Russian style guide'
}

[System.IO.Directory]::CreateDirectory($outputFull) | Out-Null
Assert-NoExistingReparseComponents -Path $outputFull -StopAt $repoRoot -Label 'Output directory'
foreach ($entry in $mapping.GetEnumerator()) {
    [System.IO.File]::Copy([string]$entry.Value, (Join-Path $outputFull ([string]$entry.Key)), $false)
}

$manifestScript = Join-Path $repoRoot 'scripts\new-installer-input-manifest.ps1'
$manifestOutput = @(& $manifestScript -InputDirectory $outputFull -ManifestPath $manifestFull -AppVersion $AppVersion)
$manifestResult = ($manifestOutput -join [Environment]::NewLine) | ConvertFrom-Json

[PSCustomObject]@{
    status = 'staged-diagnostic-preview'
    app_version = $AppVersion
    output_directory = $outputFull
    hash_manifest = $manifestFull
    hash_manifest_status = [string]$manifestResult.status
    file_count = $mapping.Count
    gui_apply_enabled = $false
    runtime_loader_validated = $false
} | ConvertTo-Json
