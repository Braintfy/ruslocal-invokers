[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PublishedDirectory,

    [Parameter(Mandatory = $true)]
    [string] $Translations,

    [Parameter(Mandatory = $true)]
    [string] $TrustedManifest,

    [Parameter(Mandatory = $true)]
    [string] $BuildReport,

    [Parameter(Mandatory = $true)]
    [string] $AuditReport,

    [Parameter(Mandatory = $true)]
    [string] $StageDirectory,

    [Parameter(Mandatory = $true)]
    [string] $OutputZip
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'path-safety.ps1')
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$projectRoot = Split-Path -Parent $PSScriptRoot
$publishedPath = (Resolve-Path -LiteralPath $PublishedDirectory).Path
$translationsPath = (Resolve-Path -LiteralPath $Translations).Path
$manifestPath = (Resolve-Path -LiteralPath $TrustedManifest).Path
$buildReportPath = (Resolve-Path -LiteralPath $BuildReport).Path
$auditReportPath = (Resolve-Path -LiteralPath $AuditReport).Path
$stagePath = Assert-SafeNewOutputPath -Path $StageDirectory -Label 'Package stage'
$zipPath = Assert-SafeNewOutputPath -Path $OutputZip -Label 'Package zip'
if (-not [IO.Directory]::Exists($publishedPath)) { throw "Published input must be an existing directory: $publishedPath" }
Assert-NoReparsePath -Path $publishedPath -Label 'Published input'
Assert-OutsideProtectedRuntime -Path $publishedPath -Label 'Published input'
if (Test-CanonicalPathWithin -Path $stagePath -Directory $publishedPath) { throw 'Package stage must not mutate the published input directory.' }
if (Test-CanonicalPathWithin -Path $zipPath -Directory $publishedPath) { throw 'Package zip must not mutate the published input directory.' }
if (Test-CanonicalPathWithin -Path $zipPath -Directory $stagePath) { throw 'Package zip must remain outside the package stage directory.' }

$inputFiles = [ordered]@{
    'Translation catalog' = $translationsPath
    'Trusted manifest' = $manifestPath
    'Build report' = $buildReportPath
    'Audit report' = $auditReportPath
    'Test instructions' = (Join-Path $projectRoot 'docs\supervised-test.md')
    'Glossary' = (Join-Path $projectRoot 'localization\glossary.ru.json')
    'Style guide' = (Join-Path $projectRoot 'localization\style-guide.ru.md')
}
foreach ($input in $inputFiles.GetEnumerator()) {
    if (-not [IO.File]::Exists($input.Value)) { throw "$($input.Key) must be an existing regular file: $($input.Value)" }
    Assert-NoReparsePath -Path $input.Value -Label $input.Key
    Assert-OutsideProtectedRuntime -Path $input.Value -Label $input.Key
}

$exe = Join-Path $publishedPath 'InvokersRu.Cli.exe'
$publishReceiptPath = Join-Path $publishedPath 'SUPERVISED-PUBLISH.json'
foreach ($publishedInput in ([ordered]@{ 'Published executable' = $exe; 'Publish receipt' = $publishReceiptPath }).GetEnumerator()) {
    if (-not [IO.File]::Exists($publishedInput.Value)) { throw "$($publishedInput.Key) is missing: $($publishedInput.Value)" }
    Assert-NoReparsePath -Path $publishedInput.Value -Label $publishedInput.Key
}

$manifest = [IO.File]::ReadAllText($manifestPath, $strictUtf8) | ConvertFrom-Json -Depth 30
$report = [IO.File]::ReadAllText($buildReportPath, $strictUtf8) | ConvertFrom-Json -Depth 30
$audit = [IO.File]::ReadAllText($auditReportPath, $strictUtf8) | ConvertFrom-Json -Depth 30
$publishReceipt = [IO.File]::ReadAllText($publishReceiptPath, $strictUtf8) | ConvertFrom-Json -Depth 20
$build = $manifest.builds[0]
$translationsHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $translationsPath).Hash
$manifestHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash
$exeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash
$reportedLocaleRevision = [Convert]::ToUInt32(([string]$report.target.locale_revision), 16)
if ($manifest.schema -ne 1 -or @($manifest.builds).Count -ne 1 -or
    $build.readiness -ne 'ready' -or $build.certified -ne $true -or $build.patch_mode -ne 'supervised_preview' -or
    $build.exclude_needs_review -ne $true -or [int]$build.minimum_applied_translations -le 0 -or
    [string]$build.translation_catalog_sha256 -ne $translationsHash -or
    $report.schema -ne 1 -or $report.kind -ne 'invokers-ru-preview-build' -or $report.validation.profile -ne 'preview' -or
    [string]$report.source.translations_sha256 -ne $translationsHash -or [int]$report.validation.errors -ne 0 -or
    $audit.schema -ne 1 -or [string]$audit.translations_sha256 -ne $translationsHash -or [int]$audit.blocking_issue_count -ne 0) {
    throw 'Package inputs do not agree on a clean, exact supervised preview catalog.'
}
if ([string]$build.content_guid -ne [string]$report.content_guid -or
    [string]$build.content_version -ne [string]$report.content_version -or
    [string]$build.english_sha256 -ne [string]$report.source.english_container_sha256 -or
    [string]$build.english_raw_sha256 -ne [string]$report.source.english_raw_sha256 -or
    [string]$build.base_sha256 -ne [string]$report.source.base_container_sha256 -or
    [string]$build.base_raw_sha256 -ne [string]$report.source.base_raw_sha256 -or
    [uint32]$build.base_locale_id -ne [uint32]$report.target.locale_id -or
    [uint32]$build.base_locale_revision -ne $reportedLocaleRevision -or
    [int]$build.entry_count -ne [int]$report.target.entries -or
    [int]$build.minimum_applied_translations -ne [int]$report.composition.applied_ru -or
    [int]$report.composition.needs_review_fallback -le 0 -or
    [string]$build.expected_output_sha256 -ne [string]$report.output.container_sha256 -or
    [string]$build.expected_output_raw_sha256 -ne [string]$report.output.raw_sha256) {
    throw 'Trusted manifest and preview report do not describe the same exact build tuple and output.'
}
if ($publishReceipt.schema -ne 1 -or $publishReceipt.kind -ne 'invokers-ru-supervised-publish' -or
    [string]$publishReceipt.cli_sha256 -ne $exeHash -or
    [string]$publishReceipt.trusted_manifest_sha256 -ne $manifestHash -or
    [string]$publishReceipt.build_id -ne [string]$build.id) {
    throw 'Published executable or trusted manifest no longer matches its publish receipt.'
}
$manifestInfoJson = & $exe trusted-manifest-info 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw 'Published executable did not expose trusted manifest metadata.' }
$manifestInfo = $manifestInfoJson | ConvertFrom-Json -Depth 30
if ($manifestInfo.schema -ne 1 -or $manifestInfo.installation_writes_enabled -ne $true -or
    $manifestInfo.embedded_manifest -ne $true -or [string]$manifestInfo.manifest_sha256 -ne $manifestHash -or @($manifestInfo.builds).Count -ne 1 -or
    [string]$manifestInfo.builds[0].id -ne [string]$build.id) {
    throw 'Published executable does not embed the trusted manifest selected for this package.'
}

$stageParent = [IO.Path]::GetDirectoryName($stagePath)
$zipParent = [IO.Path]::GetDirectoryName($zipPath)
if ([string]::IsNullOrWhiteSpace($stageParent) -or [string]::IsNullOrWhiteSpace($zipParent)) { throw 'Package outputs must have parent directories.' }
[IO.Directory]::CreateDirectory($stageParent) | Out-Null
[IO.Directory]::CreateDirectory($zipParent) | Out-Null
Assert-NoReparsePath -Path $stagePath -Label 'Package stage'
Assert-NoReparsePath -Path $zipPath -Label 'Package zip'
$temporaryStageName = ".{0}.{1}.tmp" -f [IO.Path]::GetFileName($stagePath), [Guid]::NewGuid().ToString('N')
$temporaryZipName = ".{0}.{1}.tmp.zip" -f [IO.Path]::GetFileNameWithoutExtension($zipPath), [Guid]::NewGuid().ToString('N')
$temporaryStage = Assert-SafeNewOutputPath -Path ([IO.Path]::Combine($stageParent, $temporaryStageName)) -Label 'Temporary package stage'
$temporaryZip = Assert-SafeNewOutputPath -Path ([IO.Path]::Combine($zipParent, $temporaryZipName)) -Label 'Temporary package zip'
[IO.Directory]::CreateDirectory($temporaryStage) | Out-Null

$stageInputs = [ordered]@{
    'InvokersRu.Cli.exe' = $exe
    'ru_RU.mvp.jsonl' = $translationsPath
    'TRUSTED-COMPATIBILITY.json' = $manifestPath
    'PREVIEW-BUILD-REPORT.json' = $buildReportPath
    'TRANSLATION-AUDIT.json' = $auditReportPath
    'SUPERVISED-PUBLISH.json' = $publishReceiptPath
    'TEST-INSTRUCTIONS.md' = $inputFiles['Test instructions']
    'glossary.ru.json' = $inputFiles['Glossary']
    'style-guide.ru.md' = $inputFiles['Style guide']
}
$stageInputHashes = @{}
foreach ($entry in $stageInputs.GetEnumerator()) {
    $stageInputHashes[$entry.Key] = (Get-FileHash -Algorithm SHA256 -LiteralPath $entry.Value).Hash
}
if ($stageInputHashes['InvokersRu.Cli.exe'] -ne $exeHash -or
    $stageInputHashes['ru_RU.mvp.jsonl'] -ne $translationsHash -or
    $stageInputHashes['TRUSTED-COMPATIBILITY.json'] -ne $manifestHash) {
    throw 'A trust-critical package input changed after validation.'
}
foreach ($entry in $stageInputs.GetEnumerator()) {
    $destination = Join-Path $temporaryStage $entry.Key
    [IO.File]::Copy($entry.Value, $destination, $false)
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $destination).Hash -ne $stageInputHashes[$entry.Key]) {
        throw "Staged copy hash mismatch: $($entry.Key)"
    }
}

$checksums = $stageInputs.Keys | Sort-Object | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $temporaryStage $_)).Hash, $_
}
[IO.File]::WriteAllLines((Join-Path $temporaryStage 'SHA256SUMS.txt'), [string[]]$checksums, [Text.UTF8Encoding]::new($false))
$expectedNames = @($stageInputs.Keys) + 'SHA256SUMS.txt'
$actualNames = @(Get-ChildItem -LiteralPath $temporaryStage -File | ForEach-Object Name)
if (@(Compare-Object -ReferenceObject $expectedNames -DifferenceObject $actualNames -CaseSensitive).Count -ne 0 -or
    @(Get-ChildItem -LiteralPath $temporaryStage -Directory).Count -ne 0) {
    throw 'Temporary package stage contains unexpected files or directories.'
}

$archiveSources = $expectedNames | Sort-Object | ForEach-Object { Join-Path $temporaryStage $_ }
Compress-Archive -LiteralPath $archiveSources -DestinationPath $temporaryZip -CompressionLevel Optimal
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($temporaryZip)
try {
    $archiveNames = @($archive.Entries | ForEach-Object FullName)
    if (@(Compare-Object -ReferenceObject $expectedNames -DifferenceObject $archiveNames -CaseSensitive).Count -ne 0 -or
        @($archiveNames | Group-Object | Where-Object Count -ne 1).Count -ne 0) {
        throw 'Package archive entries do not exactly match the audited stage.'
    }
    $forbidden = @($archive.Entries | Where-Object {
        $_.FullName -match '(?i)(en_US|uk_UA)\.bin(\.br)?$' -or
        $_.FullName -match '(?i)manifest\.dat$' -or
        $_.FullName -match '(?i)\.bin\.br$' -or
        $_.FullName -match '(?i)private\.jsonl$'
    })
    if ($forbidden.Count -gt 0) { throw "Forbidden game/private artifact entered package: $($forbidden.FullName -join ', ')" }
}
finally {
    $archive.Dispose()
}

Assert-NoReparsePath -Path $stagePath -Label 'Package stage'
Assert-NoReparsePath -Path $zipPath -Label 'Package zip'
[IO.Directory]::Move($temporaryStage, $stagePath)
[IO.File]::Move($temporaryZip, $zipPath)
Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath
