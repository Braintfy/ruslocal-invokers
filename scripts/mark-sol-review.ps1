param(
    [Parameter(Mandatory = $true)][string]$CheckpointPath,
    [Parameter(Mandatory = $true)][string]$SolResultPath,
    [Parameter(Mandatory = $true)][string]$ReviewPromptPath,
    [Parameter(Mandatory = $true)][string]$GlossaryPath,
    [string]$ExpectedModel = 'gpt-5.6-sol',
    [string]$ExpectedPromptVersion = 'ru-review-v1'
)

$ErrorActionPreference = 'Stop'
$strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)

function Get-Sha256Hex([string]$Path) {
    $stream = [System.IO.File]::OpenRead([System.IO.Path]::GetFullPath($Path))
    try {
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))
    }
    finally {
        $stream.Dispose()
    }
}

$checkpointFullPath = [System.IO.Path]::GetFullPath($CheckpointPath)
$solFullPath = [System.IO.Path]::GetFullPath($SolResultPath)
$checkpoint = [System.IO.File]::ReadAllText($checkpointFullPath, $strictUtf8) | ConvertFrom-Json
if ([string]$checkpoint.state -ne 'needs_sol') {
    throw "Checkpoint must be in needs_sol state, got: $($checkpoint.state)"
}
if (-not [System.IO.File]::Exists($solFullPath)) {
    throw "Sol result is missing: $solFullPath"
}

$expectedIds = @($checkpoint.escalation_ids)
$seenIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$actualIds = [Collections.Generic.List[string]]::new()
$remainingIds = [Collections.Generic.List[string]]::new()
foreach ($line in [System.IO.File]::ReadAllLines($solFullPath, $strictUtf8)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $result = $line | ConvertFrom-Json
    $id = [string]$result.job_id
    if (-not $seenIds.Add($id)) { throw "Duplicate Sol job_id: $id" }
    if ([string]$result.model -ne $ExpectedModel) { throw "Unexpected Sol model for $id" }
    if ([string]$result.prompt_version -ne $ExpectedPromptVersion) { throw "Unexpected Sol prompt version for $id" }
    if ([string]::IsNullOrWhiteSpace([string]$result.translation)) { throw "Empty Sol translation for $id" }
    $actualIds.Add($id)
    if ([bool]$result.needs_review -or [string]$result.confidence -ne 'high') {
        $remainingIds.Add($id)
    }
}

if ($actualIds.Count -ne $expectedIds.Count) {
    throw "Sol result contains $($actualIds.Count) ids; expected $($expectedIds.Count)."
}
for ($index = 0; $index -lt $expectedIds.Count; $index++) {
    if ([string]$actualIds[$index] -ne [string]$expectedIds[$index]) {
        throw "Sol result order/id mismatch at index $index."
    }
}

$checkpoint | Add-Member -NotePropertyName sol_review_file -NotePropertyValue ([System.IO.Path]::GetFileName($solFullPath)) -Force
$checkpoint | Add-Member -NotePropertyName sol_review_sha256 -NotePropertyValue (Get-Sha256Hex $solFullPath) -Force
$checkpoint | Add-Member -NotePropertyName sol_prompt_sha256 -NotePropertyValue (Get-Sha256Hex $ReviewPromptPath) -Force
$checkpoint | Add-Member -NotePropertyName sol_glossary_sha256 -NotePropertyValue (Get-Sha256Hex $GlossaryPath) -Force
$checkpoint | Add-Member -NotePropertyName sol_model -NotePropertyValue $ExpectedModel -Force
$checkpoint | Add-Member -NotePropertyName remaining_review_ids -NotePropertyValue @($remainingIds) -Force
$checkpoint.state = 'human_review'

$directory = [System.IO.Path]::GetDirectoryName($checkpointFullPath)
$tempPath = [System.IO.Path]::Combine($directory, ".$([System.IO.Path]::GetFileName($checkpointFullPath)).$([Guid]::NewGuid().ToString('N')).tmp")
$backupPath = "$checkpointFullPath.prev"
try {
    [System.IO.File]::WriteAllText($tempPath, (($checkpoint | ConvertTo-Json -Depth 10) + "`n"), $strictUtf8)
    [System.IO.File]::Replace($tempPath, $checkpointFullPath, $backupPath, $true)
    if ([System.IO.File]::Exists($backupPath)) { [System.IO.File]::Delete($backupPath) }
}
finally {
    if ([System.IO.File]::Exists($tempPath)) { [System.IO.File]::Delete($tempPath) }
}

Write-Host "Checkpoint $($checkpoint.chunk_id): human_review; remaining: $($remainingIds.Count); Sol SHA-256: $($checkpoint.sol_review_sha256)"
