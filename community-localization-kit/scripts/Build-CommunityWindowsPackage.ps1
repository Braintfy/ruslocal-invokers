[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LanguageConfig,
    [Parameter(Mandatory = $true)][string]$Catalog,
    [Parameter(Mandatory = $true)][string]$CertifiedProfile,
    [Parameter(Mandatory = $true)][string]$BuildReceipt,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Version = '1.0.0',
    [string]$DotNetPath,
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$kitRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $kitRoot '..'))
$workRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'work'))

function Get-RegularFile {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Label)
    $full = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "$Label is missing: $full" }
    $item = Get-Item -LiteralPath $full -Force
    if ($item.Length -le 0 -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must be a non-empty regular file, not a reparse point: $full"
    }
    return $item.FullName
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )
    if ($null -eq $Value) { throw "$Label must be a JSON object." }
    $actual = @($Value.PSObject.Properties | ForEach-Object { $_.Name })
    $missing = @($Expected | Where-Object { $_ -cnotin $actual })
    $unknown = @($actual | Where-Object { $_ -cnotin $Expected })
    if ($missing.Count -ne 0 -or $unknown.Count -ne 0 -or $actual.Count -ne $Expected.Count) {
        throw "$Label members differ from schema; missing=$($missing -join ','); unknown=$($unknown -join ',')."
    }
}

function Assert-NoReparseComponents {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Boundary,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $full = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $root = [IO.Path]::GetFullPath($Boundary).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not [string]::Equals($full, $root, [StringComparison]::OrdinalIgnoreCase) -and
        -not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label leaves its fixed boundary: $full"
    }
    $current = $full
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Label contains a reparse-point component: $current"
            }
        }
        if ([string]::Equals($current, $root, [StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or [string]::Equals($parent, $current, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Label could not be confined to its fixed boundary."
        }
        $current = $parent.TrimEnd([IO.Path]::DirectorySeparatorChar)
    }
}

function Write-NewUtf8Json {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)]$Value)
    if (Test-Path -LiteralPath $Path) { throw "Refusing to overwrite output: $Path" }
    $text = $Value | ConvertTo-Json -Depth 12
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text + "`n")
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}

if ($Version -notmatch '^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}(?:-[A-Za-z0-9][A-Za-z0-9.-]{0,31})?$') {
    throw "Version is not an accepted semantic version: $Version"
}

$configPath = Get-RegularFile -Path $LanguageConfig -Label 'Language config'
$catalogPath = Get-RegularFile -Path $Catalog -Label 'Source-free translation catalog'
$profilePath = Get-RegularFile -Path $CertifiedProfile -Label 'Certified local runtime profile'
$sourceReceiptPath = Get-RegularFile -Path $BuildReceipt -Label 'Exact community build receipt'
$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$profile = Get-Content -LiteralPath $profilePath -Raw -Encoding UTF8 | ConvertFrom-Json
$sourceReceipt = Get-Content -LiteralPath $sourceReceiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-ExactProperties -Value $config -Expected @(
    'schema', 'pack_id', 'target_language', 'injection_slot', 'catalog_policy', 'fallback',
    'allow_per_locale_content_version'
) -Label 'Language config'
Assert-ExactProperties -Value $config.target_language -Expected @('name', 'bcp47') -Label 'Language config target_language'
Assert-ExactProperties -Value $config.injection_slot -Expected @('locale', 'file', 'stamp_file', 'locale_id') -Label 'Language config injection_slot'
if ([int]$config.schema -ne 1 -or [string]$config.pack_id -cnotmatch '^[a-z0-9][a-z0-9._-]{1,63}$' -or
    [string]::IsNullOrWhiteSpace([string]$config.target_language.name) -or [string]$config.target_language.name -cne ([string]$config.target_language.name).Trim() -or
    ([string]$config.target_language.name).Length -gt 80 -or [string]$config.target_language.name -match '[\x00-\x1F\x7F]' -or
    [string]$config.target_language.bcp47 -cnotmatch '^[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*$' -or
    [string]$config.injection_slot.locale -cne 'uk_UA' -or [string]$config.injection_slot.file -cne 'dl_uk_UA.bin' -or
    [string]$config.injection_slot.stamp_file -cne 'dl_uk_UA.bin.ver' -or [int]$config.injection_slot.locale_id -ne 8 -or
    [string]$config.catalog_policy -cnotin @('preview-drafts', 'release-approved') -or
    [string]$config.fallback -cne 'english' -or $config.allow_per_locale_content_version -isnot [bool]) {
    throw 'Language config is not the audited schema-1 EN -> uk_UA-slot configuration.'
}
if ([int]$profile.schema -ne 1 -or $profile.certified -ne $true -or [string]$profile.readiness -cne 'ready' -or
    [int]$profile.english_locale_id -ne 1 -or [int]$profile.base_locale_id -ne 8 -or
    [string]$profile.translation_policy -cnotin @('community-preview-all-drafts', 'release-approved')) {
    throw 'Runtime profile is not an exact ready/certified EN -> uk_UA community profile.'
}
$receiptMembers = @(
    'schema', 'kind', 'pack_id', 'target_language', 'injection_slot', 'catalog_policy', 'fallback',
    'allow_per_locale_content_version', 'language_config_sha256', 'profile_id', 'game_version',
    'catalog_sha256', 'output_raw_sha256', 'profile_sha256', 'entry_count', 'applied_translations',
    'english_fallbacks', 'base_fallbacks', 'needs_review_fallbacks', 'policy', 'officially_signed'
)
Assert-ExactProperties -Value $sourceReceipt -Expected $receiptMembers -Label 'Exact community build receipt'
Assert-ExactProperties -Value $sourceReceipt.target_language -Expected @('name', 'bcp47') -Label 'Build receipt target_language'
Assert-ExactProperties -Value $sourceReceipt.injection_slot -Expected @('locale', 'file', 'stamp_file', 'locale_id') -Label 'Build receipt injection_slot'
$configHash = Get-Sha256 $configPath
$profileHash = Get-Sha256 $profilePath
$catalogHash = Get-Sha256 $catalogPath
if (-not [string]::Equals($catalogHash, [string]$profile.translation_catalog_sha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Catalog does not match the exact profile pin. Expected $($profile.translation_catalog_sha256), got $catalogHash."
}
$expectedProfilePolicy = if ([string]$config.catalog_policy -ceq 'preview-drafts') { 'community-preview-all-drafts' } else { 'release-approved' }
$identityMatches = (
    [int]$sourceReceipt.schema -eq 2 -and [string]$sourceReceipt.kind -ceq 'community-localization-exact-build' -and
    [string]$sourceReceipt.pack_id -ceq [string]$config.pack_id -and
    [string]$sourceReceipt.target_language.name -ceq [string]$config.target_language.name -and
    [string]$sourceReceipt.target_language.bcp47 -ceq [string]$config.target_language.bcp47 -and
    [string]$sourceReceipt.injection_slot.locale -ceq [string]$config.injection_slot.locale -and
    [string]$sourceReceipt.injection_slot.file -ceq [string]$config.injection_slot.file -and
    [string]$sourceReceipt.injection_slot.stamp_file -ceq [string]$config.injection_slot.stamp_file -and
    [int]$sourceReceipt.injection_slot.locale_id -eq [int]$config.injection_slot.locale_id -and
    [string]$sourceReceipt.catalog_policy -ceq [string]$config.catalog_policy -and
    [string]$sourceReceipt.fallback -ceq [string]$config.fallback -and
    $sourceReceipt.allow_per_locale_content_version -is [bool] -and
    $sourceReceipt.allow_per_locale_content_version -eq $config.allow_per_locale_content_version -and
    [string]::Equals([string]$sourceReceipt.language_config_sha256, $configHash, [StringComparison]::OrdinalIgnoreCase) -and
    [string]$sourceReceipt.profile_id -ceq [string]$profile.id -and
    [string]$sourceReceipt.game_version -ceq [string]$profile.game_version -and
    [string]::Equals([string]$sourceReceipt.catalog_sha256, $catalogHash, [StringComparison]::OrdinalIgnoreCase) -and
    [string]::Equals([string]$sourceReceipt.output_raw_sha256, [string]$profile.expected_output_sha256, [StringComparison]::OrdinalIgnoreCase) -and
    [string]::Equals([string]$sourceReceipt.profile_sha256, $profileHash, [StringComparison]::OrdinalIgnoreCase) -and
    [int64]$sourceReceipt.entry_count -eq [int64]$profile.entry_count -and
    [int64]$sourceReceipt.applied_translations -eq [int64]$profile.expected_applied_translations -and
    [int64]$sourceReceipt.english_fallbacks -eq [int64]$profile.expected_english_fallbacks -and
    [int64]$sourceReceipt.base_fallbacks -eq [int64]$profile.expected_base_fallbacks -and
    [int64]$sourceReceipt.needs_review_fallbacks -eq [int64]$profile.expected_needs_review_fallbacks -and
    [string]$sourceReceipt.policy -ceq [string]$profile.translation_policy -and
    [string]$profile.translation_policy -ceq $expectedProfilePolicy -and
    $sourceReceipt.officially_signed -is [bool] -and $sourceReceipt.officially_signed -eq $false
)
$receiptCountSum = [int64]$sourceReceipt.applied_translations + [int64]$sourceReceipt.english_fallbacks + [int64]$sourceReceipt.base_fallbacks
if (-not $identityMatches -or $receiptCountSum -ne [int64]$sourceReceipt.entry_count) {
    throw 'Language config, catalog, certified profile, and exact build receipt are not one content-bound language pack.'
}

$outputCandidate = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory))
if (-not ($outputCandidate.StartsWith($workRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) {
    throw "OutputDirectory must be a new directory below the repository work directory: $workRoot"
}
if (Test-Path -LiteralPath $outputCandidate) { throw "OutputDirectory already exists: $outputCandidate" }
$zipPath = $outputCandidate + '.zip'
$zipHashPath = $zipPath + '.sha256'
if (Test-Path -LiteralPath $zipPath) { throw "ZIP output already exists: $zipPath" }
if (Test-Path -LiteralPath $zipHashPath) { throw "ZIP hash output already exists: $zipHashPath" }
Assert-NoReparseComponents -Path $outputCandidate -Boundary $workRoot -Label 'OutputDirectory'
[IO.Directory]::CreateDirectory($outputCandidate) | Out-Null
Assert-NoReparseComponents -Path $outputCandidate -Boundary $workRoot -Label 'Created OutputDirectory'

$dotnetCandidate = Join-Path $repoRoot 'work\dotnet-10\dotnet.exe'
if (-not [string]::IsNullOrWhiteSpace($DotNetPath)) {
    $dotnet = Get-RegularFile -Path $DotNetPath -Label '.NET host'
}
elseif (Test-Path -LiteralPath $dotnetCandidate -PathType Leaf) {
    $dotnet = Get-RegularFile -Path $dotnetCandidate -Label '.NET host'
}
else {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
$sdkVersion = (& $dotnet --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') { throw "The SDK pinned by global.json is required; detected '$sdkVersion'." }

$legacyCompatibility = Get-RegularFile -Path (Join-Path $repoRoot 'config\compatibility.v1.json') -Label 'Legacy compatibility resource'
foreach ($path in @($profilePath, $legacyCompatibility)) {
    if ($path.IndexOfAny([char[]]@(';', '%')) -ge 0) { throw "MSBuild input path contains a reserved character: $path" }
}

$temporary = Join-Path $workRoot ('.community-win-' + [Guid]::NewGuid().ToString('N') + '.tmp')
[IO.Directory]::CreateDirectory($temporary) | Out-Null
$publish = Join-Path $temporary 'publish'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
try {
    Push-Location $repoRoot
    try {
        $properties = @(
            '-p:EnableSupervisedInstallWrites=true',
            "-p:TrustedCompatibilityPath=$legacyCompatibility",
            "-p:TrustedRuntimeCacheCompatibilityPath=$profilePath",
            '-p:SignedUpdateChannelConfigPath=',
            "-p:Version=$Version",
            '-p:PublishSingleFile=false',
            '-p:PublishTrimmed=false',
            '-p:PublishReadyToRun=false',
            '-p:DebugType=None',
            '-p:DebugSymbols=false'
        )
        & $dotnet restore 'src\InvokersRu.Cli\InvokersRu.Cli.csproj' --runtime win-x64 `
            --configfile NuGet.Config --source https://api.nuget.org/v3/index.json @properties
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
        & $dotnet publish 'src\InvokersRu.Cli\InvokersRu.Cli.csproj' --configuration Release `
            --runtime win-x64 --self-contained true --no-restore --output $publish @properties
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    }
    finally { Pop-Location }

    foreach ($item in Get-ChildItem -LiteralPath $publish -Force) {
        if ($item.Extension -ne '.pdb') { Copy-Item -LiteralPath $item.FullName -Destination $outputCandidate -Recurse }
    }
    $createdump = Join-Path $outputCandidate 'createdump.exe'
    if (Test-Path -LiteralPath $createdump -PathType Leaf) { [IO.File]::Delete($createdump) }
    $translations = Join-Path $outputCandidate 'translations'
    $profiles = Join-Path $outputCandidate 'profiles'
    [IO.Directory]::CreateDirectory($translations) | Out-Null
    [IO.Directory]::CreateDirectory($profiles) | Out-Null
    [IO.File]::Copy($catalogPath, (Join-Path $translations 'ru_RU.jsonl'), $false)
    [IO.File]::Copy($profilePath, (Join-Path $profiles 'certified-runtime-profile.json'), $false)
    [IO.File]::Copy($sourceReceiptPath, (Join-Path $profiles 'community-build-receipt.json'), $false)
    [IO.File]::Copy($configPath, (Join-Path $outputCandidate 'language-config.json'), $false)
    [IO.File]::Copy((Join-Path $kitRoot 'runtime\CommunityLocalization.ps1'), (Join-Path $outputCandidate 'CommunityLocalization.ps1'), $false)
    [IO.File]::Copy((Join-Path $kitRoot 'runtime\CommunityLocalization.cmd'), (Join-Path $outputCandidate 'CommunityLocalization.cmd'), $false)
    [IO.File]::Copy((Join-Path $kitRoot 'runtime\PACKAGE-README.txt'), (Join-Path $outputCandidate 'README.txt'), $false)
    [IO.File]::Copy((Join-Path $repoRoot 'LICENSE'), (Join-Path $outputCandidate 'LICENSE.txt'), $false)

    $cliPath = Join-Path $outputCandidate 'InvokersRu.Cli.exe'
    if (-not (Test-Path -LiteralPath $cliPath -PathType Leaf)) { throw 'Published CLI executable is missing.' }
    $trustedText = & $cliPath trusted-runtime-cache-info 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'Published CLI does not expose its embedded exact profile.' }
    $trusted = $trustedText | ConvertFrom-Json
    if ($trusted.installation_writes_enabled -ne $true -or $trusted.embedded_runtime_cache_profile -ne $true -or
        -not [string]::Equals([string]$trusted.profile_sha256, (Get-Sha256 $profilePath), [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$trusted.profile.translation_catalog_sha256, $catalogHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Published CLI does not embed the requested profile/catalog pins.'
    }
    $updateStatusText = & $cliPath update-status --json 2>&1 | Out-String
    $updateStatusExit = $LASTEXITCODE
    try { $updateStatus = $updateStatusText | ConvertFrom-Json }
    catch { throw "Published CLI returned invalid update-status JSON: $updateStatusText" }
    if ($updateStatusExit -ne 5 -or $updateStatus.configured -ne $false -or
        [string]$updateStatus.network_status -cne 'not-configured' -or $null -ne $updateStatus.channel) {
        throw 'Community CLI unexpectedly contains an official or ambient signed-update channel.'
    }

    $receipt = [ordered]@{
        schema = 2
        kind = 'invokers-community-windows-package'
        pack_id = [string]$config.pack_id
        target_language = $config.target_language
        injection_slot = 'uk_UA'
        game_version = [string]$profile.game_version
        app_version = $Version
        dotnet_sdk = $sdkVersion
        catalog_sha256 = $catalogHash
        profile_sha256 = $profileHash
        language_config_sha256 = $configHash
        source_build_receipt_sha256 = (Get-Sha256 $sourceReceiptPath)
        expected_output_raw_sha256 = [string]$profile.expected_output_sha256
        officially_signed = $false
        contains_original_game_files = $false
    }
    Write-NewUtf8Json -Path (Join-Path $outputCandidate 'BUILD-RECEIPT.json') -Value $receipt
    $files = @()
    foreach ($file in Get-ChildItem -LiteralPath $outputCandidate -File -Recurse | Sort-Object FullName) {
        $relative = $file.FullName.Substring($outputCandidate.TrimEnd('\').Length + 1).Replace('\', '/')
        $files += [ordered]@{ path = $relative; bytes = $file.Length; sha256 = (Get-Sha256 $file.FullName) }
    }
    Write-NewUtf8Json -Path (Join-Path $outputCandidate 'SHA256SUMS.json') -Value ([ordered]@{ schema = 1; files = $files })
    Compress-Archive -Path (Join-Path $outputCandidate '*') -DestinationPath $zipPath -CompressionLevel Optimal
    [IO.File]::WriteAllText($zipHashPath, ((Get-Sha256 $zipPath) + "  " + [IO.Path]::GetFileName($zipPath) + "`n"), [Text.UTF8Encoding]::new($false))

    Write-Host "Portable Windows package: $zipPath"
    if (-not [string]::IsNullOrWhiteSpace($IsccPath)) {
        $iscc = Get-RegularFile -Path $IsccPath -Label 'Inno Setup compiler'
        if ([IO.Path]::GetFileName($iscc) -cne 'ISCC.exe') { throw 'IsccPath must point to ISCC.exe.' }
        $installerOutput = $outputCandidate + '-installer'
        if (Test-Path -LiteralPath $installerOutput) { throw "Installer output already exists: $installerOutput" }
        Assert-NoReparseComponents -Path $installerOutput -Boundary $workRoot -Label 'Installer output'
        [IO.Directory]::CreateDirectory($installerOutput) | Out-Null
        Assert-NoReparseComponents -Path $installerOutput -Boundary $workRoot -Label 'Created installer output'
        $identityBytes = [Text.Encoding]::UTF8.GetBytes("$($config.pack_id)|$($config.target_language.bcp47)")
        $identityHash = [Security.Cryptography.SHA256]::Create().ComputeHash($identityBytes)
        $guidBytes = [byte[]]::new(16); [Array]::Copy($identityHash, $guidBytes, 16)
        $appId = (New-Object Guid (,$guidBytes)).ToString('B').ToUpperInvariant()
        $appName = "Invokers Community Localization $($config.target_language.bcp47)"
        $baseName = "Invokers-Community-$($config.pack_id)-$Version-win-x64"
        $iss = Join-Path $kitRoot 'templates\windows-installer.iss'
        & $iscc "/DSourceDir=$outputCandidate" "/DOutputDir=$installerOutput" "/DAppVersion=$Version" `
            "/DAppName=$appName" "/DAppId=$appId" "/DInstallerBaseName=$baseName" `
            "/DInstallLeaf=$($config.pack_id)" $iss
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
        $installerPath = Join-Path $installerOutput ($baseName + '.exe')
        if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) { throw "Inno Setup did not produce the expected installer: $installerPath" }
        if (Test-Path -LiteralPath ($installerPath + '.sha256')) { throw "Installer hash output already exists: $($installerPath + '.sha256')" }
        [IO.File]::WriteAllText(($installerPath + '.sha256'), ((Get-Sha256 $installerPath) + "  " + [IO.Path]::GetFileName($installerPath) + "`n"), [Text.UTF8Encoding]::new($false))
        Write-Host "Unsigned local installer: $installerPath"
    }
    else {
        Write-Host 'ISCC.exe was not supplied; the verified ZIP is complete. Pass -IsccPath to also create an unsigned local Setup EXE.'
    }
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        $resolvedTemporary = [IO.Path]::GetFullPath($temporary)
        if ($resolvedTemporary.StartsWith($workRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedTemporary) -like '.community-win-*.tmp') {
            Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
        }
    }
}
