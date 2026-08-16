param(
    [Parameter(Mandatory = $true)][string]$CheckpointPath,
    [Parameter(Mandatory = $true)][string]$PromptPath,
    [string]$ReviewPromptPath,
    [Parameter(Mandatory = $true)][string]$GlossaryPath,
    [Parameter(Mandatory = $true)][ValidateRange(0, 1000000)][int]$ValidationErrors
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
. (Join-Path $PSScriptRoot 'path-safety.ps1')

$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$terraIssueCodes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($code in @('ambiguous_context', 'terminology', 'lore', 'ui_length', 'grammar', 'mechanics')) { $null = $terraIssueCodes.Add($code) }
$solIssueCodes = [Collections.Generic.HashSet[string]]::new($terraIssueCodes, [StringComparer]::Ordinal)
$null = $solIssueCodes.Add('source_problem')
$allowedRiskFlags = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($flag in @('legal_or_privacy', 'account_or_payment', 'long_text', 'protected_tokens', 'numeric', 'legacy_control', 'context_required')) { $null = $allowedRiskFlags.Add($flag) }

function Get-Sha256Bytes([byte[]]$Bytes) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function Get-StrictSnapshot([string]$Path, [string]$Label) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not [IO.File]::Exists($fullPath)) { throw "$Label is missing: $fullPath" }
    Assert-NoReparsePath -Path $fullPath -Label $Label
    Assert-OutsideProtectedRuntime -Path $fullPath -Label $Label
    $bytes = [IO.File]::ReadAllBytes($fullPath)
    try { $text = $strictUtf8.GetString($bytes) }
    catch { throw "$Label is not strict UTF-8: $($_.Exception.Message)" }
    return [pscustomobject]@{ Path = $fullPath; Bytes = $bytes; Text = $text; Sha256 = Get-Sha256Bytes $bytes }
}

function Resolve-PrivateLeaf([string]$Root, [string]$Name, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Name) -or
        $Name.IndexOfAny([char[]]@('/', '\', ':')) -ge 0 -or
        -not [string]::Equals([IO.Path]::GetFileName($Name), $Name, [StringComparison]::Ordinal)) {
        throw "$Label must be a plain file name."
    }
    $fullPath = [IO.Path]::GetFullPath([IO.Path]::Combine($Root, $Name))
    if (-not (Test-CanonicalPathWithin -Path $fullPath -Directory $Root)) { throw "$Label leaves the checkpoint directory." }
    return Assert-PrivateWorkflowPath -Path $fullPath -RepoRoot $repoRoot -Label $Label
}

function Assert-StringArray($Value, [string]$Label, [Collections.Generic.HashSet[string]]$Allowed) {
    if ($null -eq $Value -or -not ($Value -is [Array])) { throw "$Label must be an exact JSON array." }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $Value) {
        if (-not ($item -is [string]) -or [string]::IsNullOrWhiteSpace([string]$item) -or -not $Allowed.Contains([string]$item)) {
            throw "$Label contains a non-string, empty, or unsupported value."
        }
        if (-not $seen.Add([string]$item)) { throw "$Label contains a duplicate value: $item" }
    }
}

function Read-JsonLines([string]$Text, [string]$Label) {
    $records = [Collections.Generic.List[object]]::new()
    $reader = [IO.StringReader]::new($Text)
    try {
        $lineNumber = 0
        while ($null -ne ($line = $reader.ReadLine())) {
            $lineNumber++
            if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }
            try { $records.Add(($line | ConvertFrom-Json)) }
            catch { throw "Invalid $Label JSONL at line ${lineNumber}: $($_.Exception.Message)" }
        }
    }
    finally { $reader.Dispose() }
    return $records.ToArray()
}

$checkpointFullPath = [IO.Path]::GetFullPath($CheckpointPath)
$checkpointFullPath = Assert-PrivateWorkflowPath -Path $checkpointFullPath -RepoRoot $repoRoot -Label 'Checkpoint'
$checkpointSnapshot = Get-StrictSnapshot $checkpointFullPath 'Checkpoint'
try { $checkpoint = $checkpointSnapshot.Text | ConvertFrom-Json }
catch { throw "Invalid checkpoint JSON: $($_.Exception.Message)" }
if ([int]$checkpoint.schema -ne 1 -or [string]$checkpoint.chunk_id -notmatch '^chunk-[0-9]{4,8}$') {
    throw 'Checkpoint schema or chunk_id is invalid.'
}

$root = [IO.Path]::GetDirectoryName($checkpointFullPath)
$jobsPath = Resolve-PrivateLeaf $root ([string]$checkpoint.jobs_file) 'Checkpoint jobs'
$resultPath = Resolve-PrivateLeaf $root ([string]$checkpoint.result_file) 'Checkpoint result'
$jobsSnapshot = Get-StrictSnapshot $jobsPath 'Checkpoint jobs'
$resultSnapshot = Get-StrictSnapshot $resultPath 'Checkpoint result'
$promptSnapshot = Get-StrictSnapshot ([IO.Path]::GetFullPath($PromptPath)) 'Translation prompt'
$glossarySnapshot = Get-StrictSnapshot ([IO.Path]::GetFullPath($GlossaryPath)) 'Translation glossary'

if (-not [string]::Equals($jobsSnapshot.Sha256, [string]$checkpoint.jobs_sha256, [StringComparison]::OrdinalIgnoreCase)) { throw 'Checkpoint jobs hash mismatch.' }
if (-not [string]::Equals($promptSnapshot.Sha256, [string]$checkpoint.prompt_sha256, [StringComparison]::OrdinalIgnoreCase)) { throw 'Checkpoint prompt hash mismatch.' }
if (-not [string]::Equals($glossarySnapshot.Sha256, [string]$checkpoint.glossary_sha256, [StringComparison]::OrdinalIgnoreCase)) { throw 'Checkpoint glossary hash mismatch.' }

$jobs = @(Read-JsonLines $jobsSnapshot.Text 'jobs')
$results = @(Read-JsonLines $resultSnapshot.Text 'results')
$jobIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$jobsById = @{}
foreach ($job in $jobs) {
    if (-not ($job.job_id -is [string]) -or [string]::IsNullOrWhiteSpace([string]$job.job_id) -or -not $jobIds.Add([string]$job.job_id)) {
        throw "Missing or duplicate job_id: $($job.job_id)"
    }
    Assert-StringArray $job.risk_flags "Job $($job.job_id) risk_flags" $allowedRiskFlags
    $jobsById[[string]$job.job_id] = $job
}
if ($jobIds.Count -eq 0) { throw 'Jobs file contains no work items.' }

$resultIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$escalations = [Collections.Generic.List[string]]::new()
$models = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($result in $results) {
    if (-not ($result.job_id -is [string]) -or [string]::IsNullOrWhiteSpace([string]$result.job_id)) { throw 'Result has a missing/non-string job_id.' }
    $id = [string]$result.job_id
    if (-not $resultIds.Add($id) -or -not $jobIds.Contains($id)) { throw "Unknown or duplicate result job_id: $id" }
    if (-not ($result.model -is [string]) -or -not ($result.prompt_version -is [string]) -or
        -not ($result.translation -is [string]) -or -not ($result.confidence -is [string]) -or
        -not ($result.needs_review -is [bool])) {
        throw "Result $id has invalid model/prompt/translation/confidence/needs_review JSON types."
    }
    $model = [string]$result.model
    $promptVersion = [string]$result.prompt_version
    if (($model -eq 'gpt-5.6-terra' -and $promptVersion -ne 'ru-v2') -or
        ($model -eq 'gpt-5.6-sol' -and $promptVersion -ne 'ru-review-v1') -or
        $model -notin @('gpt-5.6-terra', 'gpt-5.6-sol')) {
        throw "Result $id violates the exact model-to-prompt mapping."
    }
    if ([string]$result.confidence -notin @('high', 'medium', 'low')) {
        throw "Result $id has unsupported confidence."
    }
    $issueAllowlist = if ($model -eq 'gpt-5.6-sol') { $solIssueCodes } else { $terraIssueCodes }
    Assert-StringArray $result.issue_codes "Result $id issue_codes" $issueAllowlist
    $null = $models.Add($model)
    $riskFlags = @($jobsById[$id].risk_flags)
    if ([bool]$result.needs_review -or [string]$result.confidence -ne 'high' -or
        $riskFlags -contains 'context_required' -or $riskFlags -contains 'long_text') {
        $escalations.Add($id)
    }
}
if ($jobIds.Count -ne $resultIds.Count) { throw "Incomplete result: $($resultIds.Count) of $($jobIds.Count) jobs." }
if ($ValidationErrors -ne 0) { throw "Validator reported $ValidationErrors error(s); checkpoint state was not advanced." }

[string[]]$sortedModels = @($models)
[Array]::Sort($sortedModels, [StringComparer]::Ordinal)
$modelSummary = [string]::Join(',', $sortedModels)
$hasSol = $models.Contains('gpt-5.6-sol')
$reviewPromptSha = $null
if ($hasSol) {
    if ([string]::IsNullOrWhiteSpace($ReviewPromptPath)) { throw 'ReviewPromptPath is required when results contain gpt-5.6-sol.' }
    $reviewPromptSnapshot = Get-StrictSnapshot ([IO.Path]::GetFullPath($ReviewPromptPath)) 'Translation review prompt'
    $reviewPromptSha = $reviewPromptSnapshot.Sha256
}
elseif (-not [string]::IsNullOrWhiteSpace($ReviewPromptPath)) {
    $null = Get-StrictSnapshot ([IO.Path]::GetFullPath($ReviewPromptPath)) 'Translation review prompt'
}

$checkpoint.result_sha256 = $resultSnapshot.Sha256
$checkpoint.model = $modelSummary
$checkpoint.validation_errors = 0
$checkpoint.escalation_ids = @($escalations)
$checkpoint.state = if ($escalations.Count -gt 0) { 'needs_sol' } else { 'validated' }
$checkpoint | Add-Member -NotePropertyName review_prompt_sha256 -NotePropertyValue $reviewPromptSha -Force
$content = ($checkpoint | ConvertTo-Json -Depth 8) + "`n"
$contentBytes = $strictUtf8.GetBytes($content)
$expectedNewHash = Get-Sha256Bytes $contentBytes

foreach ($dependency in @($jobsSnapshot, $resultSnapshot, $promptSnapshot, $glossarySnapshot)) {
    $current = Get-StrictSnapshot $dependency.Path 'Checkpoint dependency'
    if (-not [string]::Equals($current.Sha256, [string]$dependency.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A checkpoint dependency changed during validation.'
    }
}
if ($hasSol) {
    $currentReviewPrompt = Get-StrictSnapshot $reviewPromptSnapshot.Path 'Translation review prompt'
    if (-not [string]::Equals($currentReviewPrompt.Sha256, $reviewPromptSha, [StringComparison]::OrdinalIgnoreCase)) { throw 'Review prompt changed during validation.' }
}

$tempPath = [IO.Path]::Combine($root, ".{0}.{1}.tmp" -f @([IO.Path]::GetFileName($checkpointFullPath), [Guid]::NewGuid().ToString('N')))
$backupPath = [IO.Path]::Combine($root, ".{0}.{1}.bak" -f @([IO.Path]::GetFileName($checkpointFullPath), [Guid]::NewGuid().ToString('N')))
try {
    $stream = [IO.FileStream]::new($tempPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 81920, [IO.FileOptions]::WriteThrough)
    try {
        $stream.Write($contentBytes, 0, $contentBytes.Length)
        $stream.Flush($true)
    }
    finally { $stream.Dispose() }

    $currentCheckpoint = Get-StrictSnapshot $checkpointFullPath 'Checkpoint'
    if (-not [string]::Equals($currentCheckpoint.Sha256, [string]$checkpointSnapshot.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Checkpoint changed concurrently; refusing to replace it.'
    }
    if ([IO.File]::Exists($backupPath) -or [IO.Directory]::Exists($backupPath)) { throw 'Checkpoint backup path unexpectedly exists.' }
    [IO.File]::Replace($tempPath, $checkpointFullPath, $backupPath, $true)
    $committed = Get-StrictSnapshot $checkpointFullPath 'Committed checkpoint'
    if (-not [string]::Equals($committed.Sha256, $expectedNewHash, [StringComparison]::OrdinalIgnoreCase)) { throw 'Committed checkpoint verification failed.' }
    [IO.File]::Delete($backupPath)
}
finally {
    if ([IO.File]::Exists($tempPath)) { [IO.File]::Delete($tempPath) }
}

Write-Host "Checkpoint $($checkpoint.chunk_id): $($checkpoint.state); escalations: $($escalations.Count); model: $modelSummary"
