param(
    [Parameter(Mandatory = $true)][string]$CheckpointPath,
    [Parameter(Mandatory = $true)][string]$PromptPath,
    [Parameter(Mandatory = $true)][string]$GlossaryPath,
    [Parameter(Mandatory = $true)][ValidateRange(0, 1000000)][int]$ValidationErrors
)

$ErrorActionPreference = 'Stop'
$strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)

function Get-Sha256Hex([string]$Path) {
    $stream = [System.IO.File]::OpenRead([System.IO.Path]::GetFullPath($Path))
    try {
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
        try { return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') }
        finally { $algorithm.Dispose() }
    }
    finally { $stream.Dispose() }
}

$checkpointFullPath = [System.IO.Path]::GetFullPath($CheckpointPath)
$root = [System.IO.Path]::GetDirectoryName($checkpointFullPath)
$checkpoint = [System.IO.File]::ReadAllText($checkpointFullPath, $strictUtf8) | ConvertFrom-Json
$jobsPath = [System.IO.Path]::Combine($root, [string]$checkpoint.jobs_file)
$resultPath = [System.IO.Path]::Combine($root, [string]$checkpoint.result_file)

if ((Get-Sha256Hex $jobsPath) -ne $checkpoint.jobs_sha256) { throw 'Checkpoint jobs hash mismatch.' }
if ((Get-Sha256Hex $PromptPath) -ne $checkpoint.prompt_sha256) { throw 'Checkpoint prompt hash mismatch.' }
if ((Get-Sha256Hex $GlossaryPath) -ne $checkpoint.glossary_sha256) { throw 'Checkpoint glossary hash mismatch.' }
if (-not [System.IO.File]::Exists($resultPath)) { throw "Result file is missing: $resultPath" }

$jobIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($line in [System.IO.File]::ReadAllLines($jobsPath, $strictUtf8)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $job = $line | ConvertFrom-Json
    if (-not $jobIds.Add([string]$job.job_id)) { throw "Duplicate job_id: $($job.job_id)" }
}

$resultIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$escalations = [System.Collections.Generic.List[string]]::new()
$models = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($line in [System.IO.File]::ReadAllLines($resultPath, $strictUtf8)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $result = $line | ConvertFrom-Json
    $id = [string]$result.job_id
    if (-not $resultIds.Add($id) -or -not $jobIds.Contains($id)) { throw "Unknown or duplicate result job_id: $id" }
    $models.Add([string]$result.model) | Out-Null
    if ([bool]$result.needs_review -or [string]$result.confidence -ne 'high') { $escalations.Add($id) }
}
if ($jobIds.Count -ne $resultIds.Count) { throw "Incomplete result: $($resultIds.Count) of $($jobIds.Count) jobs." }
if ($ValidationErrors -ne 0) { throw "Validator reported $ValidationErrors error(s); checkpoint state was not advanced." }

$checkpoint.result_sha256 = Get-Sha256Hex $resultPath
$checkpoint.model = [string]::Join(',', ($models | Sort-Object))
$checkpoint.validation_errors = 0
$checkpoint.escalation_ids = @($escalations)
$checkpoint.state = if ($escalations.Count -gt 0) { 'needs_sol' } else { 'validated' }

$tempPath = "$checkpointFullPath.$([Guid]::NewGuid().ToString('N')).tmp"
$backupPath = "$checkpointFullPath.prev"
try {
    [System.IO.File]::WriteAllText($tempPath, (($checkpoint | ConvertTo-Json -Depth 8) + "`n"), $strictUtf8)
    [System.IO.File]::Replace($tempPath, $checkpointFullPath, $backupPath, $true)
    if ([System.IO.File]::Exists($backupPath)) { [System.IO.File]::Delete($backupPath) }
}
finally {
    if ([System.IO.File]::Exists($tempPath)) { [System.IO.File]::Delete($tempPath) }
}

Write-Host "Checkpoint $($checkpoint.chunk_id): $($checkpoint.state); escalations: $($escalations.Count); model: $($checkpoint.model)"
