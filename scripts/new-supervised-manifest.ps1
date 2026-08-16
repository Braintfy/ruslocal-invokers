[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $TemplateManifest,

    [Parameter(Mandatory = $true)]
    [string] $Translations,

    [Parameter(Mandatory = $true)]
    [string] $Preview,

    [Parameter(Mandatory = $true)]
    [string] $BuildReport,

    [Parameter(Mandatory = $true)]
    [string] $OutputManifest
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'path-safety.ps1')
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$templatePath = (Resolve-Path -LiteralPath $TemplateManifest).Path
$translationsPath = (Resolve-Path -LiteralPath $Translations).Path
$previewPath = (Resolve-Path -LiteralPath $Preview).Path
$reportPath = (Resolve-Path -LiteralPath $BuildReport).Path
$outputPath = Assert-SafeNewOutputPath -Path $OutputManifest -Label 'Manifest output'
foreach ($input in ([ordered]@{
    'Template manifest' = $templatePath
    'Translation catalog' = $translationsPath
    'Preview artifact' = $previewPath
    'Build report' = $reportPath
}).GetEnumerator()) {
    if (-not [IO.File]::Exists($input.Value)) { throw "$($input.Key) must be an existing regular file: $($input.Value)" }
    Assert-NoReparsePath -Path $input.Value -Label $input.Key
    Assert-OutsideProtectedRuntime -Path $input.Value -Label $input.Key
}

$template = [IO.File]::ReadAllText($templatePath, $strictUtf8) | ConvertFrom-Json -Depth 30
$report = [IO.File]::ReadAllText($reportPath, $strictUtf8) | ConvertFrom-Json -Depth 30
if ($template.schema -ne 1 -or @($template.builds).Count -ne 1) {
    throw 'Supervised manifest generation requires a schema-1 template with exactly one build.'
}
if ($report.schema -ne 1 -or $report.kind -ne 'invokers-ru-preview-build' -or $report.validation.errors -ne 0) {
    throw 'Build report is not a clean schema-1 preview report.'
}
if ($null -eq $report.PSObject.Properties['build_options'] -or
    $report.build_options.include_draft -ne $true -or
    $report.build_options.exclude_needs_review -ne $true -or
    $report.build_options.release -ne $false) {
    throw 'Supervised preview report must prove --include-draft and --exclude-needs-review without --release.'
}

$build = $template.builds[0]
$translationsHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $translationsPath).Hash
$previewHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $previewPath).Hash
$reportedLocaleRevision = [Convert]::ToUInt32(([string]$report.target.locale_revision), 16)
if ($translationsHash -ne [string]$report.source.translations_sha256) {
    throw 'Translation catalog hash does not match the build report.'
}
if ($previewHash -ne [string]$report.output.container_sha256) {
    throw 'Preview container hash does not match the build report.'
}
if ([string]$build.content_guid -ne [string]$report.content_guid -or
    [string]$build.content_version -ne [string]$report.content_version -or
    [string]$build.english_sha256 -ne [string]$report.source.english_container_sha256 -or
    [string]$build.english_raw_sha256 -ne [string]$report.source.english_raw_sha256 -or
    [string]$build.base_sha256 -ne [string]$report.source.base_container_sha256 -or
    [string]$build.base_raw_sha256 -ne [string]$report.source.base_raw_sha256 -or
    [uint32]$build.base_locale_id -ne [uint32]$report.target.locale_id -or
    [uint32]$build.base_locale_revision -ne $reportedLocaleRevision -or
    [int]$build.entry_count -ne [int]$report.target.entries) {
    throw 'Preview report does not match the exact build tuple in the template manifest.'
}
if ([int]$report.composition.applied_ru -le 0 -or [int]$report.composition.needs_review_fallback -le 0) {
    throw 'The first supervised artifact must be a non-empty conservative preview with explicit needs-review fallback.'
}

$build.readiness = 'ready'
$build.certified = $true
$build.blocked_reason = $null
$build | Add-Member -NotePropertyName patch_mode -NotePropertyValue 'supervised_preview' -Force
$build | Add-Member -NotePropertyName exclude_needs_review -NotePropertyValue $true -Force
$build | Add-Member -NotePropertyName translation_catalog_sha256 -NotePropertyValue $translationsHash -Force
$build | Add-Member -NotePropertyName minimum_applied_translations -NotePropertyValue ([int]$report.composition.applied_ru) -Force
$build | Add-Member -NotePropertyName expected_output_sha256 -NotePropertyValue ([string]$report.output.container_sha256) -Force
$build | Add-Member -NotePropertyName expected_output_raw_sha256 -NotePropertyValue ([string]$report.output.raw_sha256) -Force

$parent = [IO.Path]::GetDirectoryName($outputPath)
if ($parent) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
Assert-NoReparsePath -Path $outputPath -Label 'Manifest output'
$tempName = ".{0}.{1}.tmp" -f [IO.Path]::GetFileName($outputPath), [Guid]::NewGuid().ToString('N')
$temp = [IO.Path]::Combine($parent, $tempName)
try {
    $json = $template | ConvertTo-Json -Depth 30
    [IO.File]::WriteAllText($temp, $json + "`n", $strictUtf8)
    Assert-NoReparsePath -Path $outputPath -Label 'Manifest output'
    [IO.File]::Move($temp, $outputPath)
}
finally {
    if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
}
"Trusted supervised manifest: $outputPath"
"Translations SHA-256: $translationsHash"
"Preview SHA-256: $previewHash"
