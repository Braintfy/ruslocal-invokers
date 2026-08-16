[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Translations,

    [Parameter(Mandatory = $true)]
    [string] $OutputReport
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'path-safety.ps1')
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$translationsPath = (Resolve-Path -LiteralPath $Translations).Path
$outputPath = Assert-SafeNewOutputPath -Path $OutputReport -Label 'Audit output'
if (-not [IO.File]::Exists($translationsPath)) { throw "Translation catalog must be an existing regular file: $translationsPath" }
Assert-NoReparsePath -Path $translationsPath -Label 'Translation catalog'
Assert-OutsideProtectedRuntime -Path $translationsPath -Label 'Translation catalog'

$records = [Collections.Generic.List[object]]::new()
$lineNumber = 0
foreach ($line in [IO.File]::ReadAllLines($translationsPath, $strictUtf8)) {
    $lineNumber++
    if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }
    try {
        $record = $line | ConvertFrom-Json -Depth 20
    }
    catch {
        throw "Invalid JSONL at line $lineNumber`: $($_.Exception.Message)"
    }
    $records.Add($record)
}

$duplicateIds = @($records | Group-Object id | Where-Object Count -gt 1 | ForEach-Object Name)
$blankIds = @($records | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.translation) } | ForEach-Object id)
$ukrainianLetterIds = @($records | Where-Object { [string]$_.translation -match '[іїєґІЇЄҐ]' } | ForEach-Object id)
$invisibleControlIds = @($records | Where-Object { [string]$_.translation -match '[\u200B\u200C\u200D\u2060\u202A-\u202E\u2066-\u2069]' } | ForEach-Object id)
$suspectPatterns = [ordered]@{
    translated_stat_abbreviation = '(?i)(^|[^A-Za-zА-Яа-я])(АТК|ЗАЩ|ОЗ)([^A-Za-zА-Яа-я]|$)'
    titan_mana_drift = '(?i)(титаническ\w*\s+ман|мана\s+титанов)'
    manashock_resistance_drift = '(?i)сопротивлен\w*\s+(к\s+)?Манашок'
    disband_drift = '(?i)распуст(ить|ите|и)'
}
$suspects = [ordered]@{}
foreach ($name in $suspectPatterns.Keys) {
    $pattern = $suspectPatterns[$name]
    $suspects[$name] = @($records | Where-Object { [string]$_.translation -match $pattern } | ForEach-Object id)
}

$models = [ordered]@{}
foreach ($group in ($records | Group-Object model | Sort-Object Name)) { $models[[string]$group.Name] = $group.Count }
$statuses = [ordered]@{}
foreach ($group in ($records | Group-Object status | Sort-Object Name)) { $statuses[[string]$group.Name] = $group.Count }
$stages = [ordered]@{}
foreach ($group in ($records | Group-Object review_stage | Sort-Object Name)) { $stages[[string]$group.Name] = $group.Count }

$report = [ordered]@{
    schema = 1
    translations_file = [IO.Path]::GetFileName($translationsPath)
    translations_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $translationsPath).Hash
    records = $records.Count
    needs_review = @($records | Where-Object { $_.needs_review -eq $true }).Count
    contains_cyrillic = @($records | Where-Object { [string]$_.translation -match '[А-Яа-яЁё]' }).Count
    contains_yo = @($records | Where-Object { [string]$_.translation -match '[Ёё]' }).Count
    duplicate_ids = $duplicateIds
    blank_translation_ids = $blankIds
    ukrainian_letter_ids = $ukrainianLetterIds
    invisible_control_ids = $invisibleControlIds
    terminology_suspects = $suspects
    by_model = $models
    by_status = $statuses
    by_review_stage = $stages
    blocking_issue_count = $duplicateIds.Count + $blankIds.Count + $ukrainianLetterIds.Count + $invisibleControlIds.Count + (@($suspects.Values | ForEach-Object { $_ }).Count)
}

$parent = [IO.Path]::GetDirectoryName($outputPath)
if ($parent) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
Assert-NoReparsePath -Path $outputPath -Label 'Audit output'
$tempName = ".{0}.{1}.tmp" -f [IO.Path]::GetFileName($outputPath), [Guid]::NewGuid().ToString('N')
$temp = [IO.Path]::Combine($parent, $tempName)
try {
    $json = $report | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($temp, $json + "`n", $strictUtf8)
    Assert-NoReparsePath -Path $outputPath -Label 'Audit output'
    [IO.File]::Move($temp, $outputPath)
}
finally {
    if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
}

"Overlay audit: $($records.Count) records; needs_review=$($report.needs_review); blocking=$($report.blocking_issue_count)"
"Report: $outputPath"
