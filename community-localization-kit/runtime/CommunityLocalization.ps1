[CmdletBinding()]
param(
    [ValidateSet('menu', 'status', 'apply', 'restore', 'recover')]
    [string]$Action = 'menu'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$appRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$cli = Join-Path $appRoot 'InvokersRu.Cli.exe'
$configPath = Join-Path $appRoot 'language-config.json'
$catalogPath = Join-Path $appRoot 'translations\ru_RU.jsonl'
$profilePath = Join-Path $appRoot 'profiles\certified-runtime-profile.json'
$sourceReceiptPath = Join-Path $appRoot 'profiles\community-build-receipt.json'
$packageReceiptPath = Join-Path $appRoot 'BUILD-RECEIPT.json'
function Require-RegularPackagedFile([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label is missing: $Path" }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Length -le 0 -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must be a non-empty regular file: $Path"
    }
}
function Get-PackageSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}
Require-RegularPackagedFile $cli 'Packaged CLI'
Require-RegularPackagedFile $configPath 'Language config'
Require-RegularPackagedFile $catalogPath 'Translation catalog'
Require-RegularPackagedFile $profilePath 'Certified profile'
Require-RegularPackagedFile $sourceReceiptPath 'Exact build receipt'
Require-RegularPackagedFile $packageReceiptPath 'Package receipt'
$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$packageReceipt = Get-Content -LiteralPath $packageReceiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([int]$config.schema -ne 1 -or [string]$config.injection_slot.locale -cne 'uk_UA' -or
    [string]$config.injection_slot.file -cne 'dl_uk_UA.bin' -or [int]$config.injection_slot.locale_id -ne 8) {
    throw 'This package does not describe the audited uk_UA injection slot.'
}
if ([int]$packageReceipt.schema -ne 2 -or [string]$packageReceipt.kind -cne 'invokers-community-windows-package' -or
    [string]$packageReceipt.pack_id -cne [string]$config.pack_id -or
    [string]$packageReceipt.target_language.name -cne [string]$config.target_language.name -or
    [string]$packageReceipt.target_language.bcp47 -cne [string]$config.target_language.bcp47 -or
    -not [string]::Equals([string]$packageReceipt.language_config_sha256, (Get-PackageSha256 $configPath), [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals([string]$packageReceipt.source_build_receipt_sha256, (Get-PackageSha256 $sourceReceiptPath), [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals([string]$packageReceipt.profile_sha256, (Get-PackageSha256 $profilePath), [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals([string]$packageReceipt.catalog_sha256, (Get-PackageSha256 $catalogPath), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Packaged config, catalog, profile, and build receipt are not one content-bound language pack.'
}

function Invoke-Plan {
    $text = & $script:cli cache-plan --json 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    try { $plan = $text | ConvertFrom-Json }
    catch { throw "The CLI returned an unreadable status response (exit $exitCode): $text" }
    return $plan
}

function Show-Plan {
    param([Parameter(Mandatory = $true)]$Plan)
    Write-Host ''
    Write-Host "Target language: $($script:config.target_language.name) [$($script:config.target_language.bcp47)]"
    Write-Host 'Injection slot:  Ukrainian (uk_UA)'
    Write-Host "Game version:   $($Plan.observed.game_version)"
    Write-Host "Status:         $($Plan.status)"
    Write-Host "Decision:       $($Plan.plan)"
    Write-Host "Details:        $($Plan.message)"
    Write-Host "Translations:   $($Plan.profile.applied_translations)"
    Write-Host "English fallback: $($Plan.profile.english_fallbacks)"
    if ($Plan.process_conflicts.Count -gt 0) {
        Write-Host "Running processes: $($Plan.process_conflicts -join ', ')" -ForegroundColor Yellow
    }
    Write-Host ''
}

function Confirm-ExactAction {
    param([Parameter(Mandatory = $true)][string]$Word)
    Write-Host 'Before continuing, select Ukrainian in the game, wait for its text to download,' -ForegroundColor Yellow
    Write-Host 'then fully quit both the game and its launcher (including the tray icon).' -ForegroundColor Yellow
    $answer = Read-Host "Type $Word to continue"
    if ([string]$answer -cne $Word) { throw 'Confirmation did not match; nothing was changed.' }
}

function Invoke-SelectedAction {
    param([Parameter(Mandatory = $true)][string]$Name)
    switch ($Name) {
        'status' {
            Show-Plan -Plan (Invoke-Plan)
        }
        'apply' {
            $plan = Invoke-Plan
            Show-Plan -Plan $plan
            if ([string]$plan.plan -notin @('READY_TO_APPLY', 'READY_TO_UPDATE_TRANSLATION', 'READY_TO_REAPPLY_AFTER_GAME_UPDATE')) {
                throw "Fail-closed: the exact plan does not authorize installation ($($plan.plan))."
            }
            Confirm-ExactAction -Word 'APPLY'
            & $script:cli cache-apply --acknowledge-risk I_ACCEPT_LOCAL_MODIFICATION
            if ($LASTEXITCODE -ne 0) { throw "Installation failed with exit code $LASTEXITCODE." }
        }
        'restore' {
            $plan = Invoke-Plan
            Show-Plan -Plan $plan
            if ($plan.can_restore -ne $true) { throw 'Fail-closed: no exact verified backup can be restored.' }
            Confirm-ExactAction -Word 'RESTORE'
            & $script:cli cache-restore --acknowledge-risk I_ACCEPT_LOCAL_MODIFICATION
            if ($LASTEXITCODE -ne 0) { throw "Restore failed with exit code $LASTEXITCODE." }
        }
        'recover' {
            $plan = Invoke-Plan
            Show-Plan -Plan $plan
            if ($plan.can_recover -ne $true) { throw 'Fail-closed: there is no authenticated interrupted transaction to recover.' }
            Confirm-ExactAction -Word 'RECOVER'
            & $script:cli cache-recover --acknowledge-risk I_ACCEPT_LOCAL_MODIFICATION
            if ($LASTEXITCODE -ne 0) { throw "Recovery failed with exit code $LASTEXITCODE." }
        }
        default { throw "Unsupported action: $Name" }
    }
}

if ($Action -ne 'menu') {
    Invoke-SelectedAction -Name $Action
    exit 0
}

while ($true) {
    Clear-Host
    Write-Host "Community localization: $($config.target_language.name)" -ForegroundColor Cyan
    Write-Host '1. Check compatibility and status'
    Write-Host '2. Install localization'
    Write-Host '3. Restore exact original'
    Write-Host '4. Recover an interrupted transaction'
    Write-Host '0. Exit'
    $choice = Read-Host 'Choose'
    if ($choice -eq '0') { break }
    $selected = switch ($choice) { '1' { 'status' } '2' { 'apply' } '3' { 'restore' } '4' { 'recover' } default { $null } }
    if ($null -eq $selected) { continue }
    try { Invoke-SelectedAction -Name $selected }
    catch { Write-Host $_.Exception.Message -ForegroundColor Red }
    $null = Read-Host 'Press Enter to continue'
}
