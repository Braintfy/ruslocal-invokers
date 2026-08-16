[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Jobs,
    [Parameter(Mandatory = $true)][string] $Checkpoint,
    [Parameter(Mandatory = $true)][string] $TerraResults,
    [Parameter(Mandatory = $true)][string] $SolResults,
    [Parameter(Mandatory = $true)][string] $Output
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
. (Join-Path $PSScriptRoot 'path-safety.ps1')

$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$shaPattern = '^[0-9A-Fa-f]{64}$'
$terraIssueCodes = @('ambiguous_context', 'terminology', 'lore', 'ui_length', 'grammar', 'mechanics')
$solIssueCodes = @('ambiguous_context', 'terminology', 'lore', 'ui_length', 'grammar', 'mechanics', 'source_problem')

function Get-Sha256HexFromBytes {
    param([Parameter(Mandatory = $true)][byte[]] $Bytes)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function Convert-StrictUtf8BytesToText {
    param(
        [Parameter(Mandatory = $true)][byte[]] $Bytes,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $offset = if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) { 3 } else { 0 }
    try { return $strictUtf8.GetString($Bytes, $offset, $Bytes.Length - $offset) }
    catch { throw "$Label is not strict UTF-8: $($_.Exception.Message)" }
}

function Resolve-PrivateRegularFile {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $fullPath = Assert-PrivateWorkflowPath -Path $Path -RepoRoot $repoRoot -Label $Label
    if (-not [IO.File]::Exists($fullPath)) {
        throw "$Label must be an existing regular file: $fullPath"
    }
    return $fullPath
}

function Read-FileSnapshot {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Length -gt [int]::MaxValue) { throw "$Label is too large to snapshot safely." }
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -le 0) { throw "$Label changed or ended while it was being snapshotted." }
            $offset += $read
        }
    }
    finally { $stream.Dispose() }

    return [pscustomobject]@{
        Bytes = $bytes
        Text = Convert-StrictUtf8BytesToText -Bytes $bytes -Label $Label
        Sha256 = Get-Sha256HexFromBytes -Bytes $bytes
    }
}

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Label
    )

    if ($null -eq $Object -or $Object -isnot [pscustomobject]) { throw "$Label must be a JSON object." }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { throw "$Label is missing required property '$Name'." }
    return $property
}

function Convert-StrictJsonSnapshot {
    param(
        [Parameter(Mandatory = $true)] $Snapshot,
        [Parameter(Mandatory = $true)][string] $Label
    )

    try { $value = $Snapshot.Text | ConvertFrom-Json }
    catch { throw "Invalid $Label JSON: $($_.Exception.Message)" }
    if ($null -eq $value -or $value -isnot [pscustomobject]) { throw "$Label must contain one JSON object." }
    return $value
}

function Read-JobLines {
    param(
        [Parameter(Mandatory = $true)] $Snapshot,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $records = [Collections.Generic.List[object]]::new()
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $reader = [IO.StringReader]::new($Snapshot.Text)
    try {
        $lineNumber = 0
        while ($null -ne ($line = $reader.ReadLine())) {
            $lineNumber++
            if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }
            try { $value = $line | ConvertFrom-Json }
            catch { throw "Invalid $Label JSONL at line ${lineNumber}: $($_.Exception.Message)" }

            $idValue = (Get-RequiredProperty -Object $value -Name 'job_id' -Label "$Label line $lineNumber").Value
            if ($idValue -isnot [string] -or [string]::IsNullOrWhiteSpace($idValue) -or -not $ids.Add($idValue)) {
                throw "Missing, non-string, or duplicate $Label job_id at line $lineNumber."
            }
            $records.Add([pscustomobject]@{ Id = $idValue; Value = $value })
        }
    }
    finally { $reader.Dispose() }
    return [pscustomobject]@{ Records = $records.ToArray() }
}

function Read-ResultLines {
    param(
        [Parameter(Mandatory = $true)] $Snapshot,
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][string] $ExpectedModel,
        [Parameter(Mandatory = $true)][string] $ExpectedPromptVersion,
        [Parameter(Mandatory = $true)][string[]] $AllowedIssueCodes
    )

    $records = [Collections.Generic.List[object]]::new()
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $allowedIssues = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($allowedIssue in $AllowedIssueCodes) { $null = $allowedIssues.Add($allowedIssue) }
    $reader = [IO.StringReader]::new($Snapshot.Text)
    try {
        $lineNumber = 0
        while ($null -ne ($line = $reader.ReadLine())) {
            $lineNumber++
            if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }
            try { $value = $line | ConvertFrom-Json }
            catch { throw "Invalid $Label JSONL at line ${lineNumber}: $($_.Exception.Message)" }

            $recordLabel = "$Label line $lineNumber"
            $id = (Get-RequiredProperty -Object $value -Name 'job_id' -Label $recordLabel).Value
            if ($id -isnot [string] -or [string]::IsNullOrWhiteSpace($id) -or -not $ids.Add($id)) {
                throw "Missing, non-string, or duplicate $Label job_id at line $lineNumber."
            }

            $translation = (Get-RequiredProperty -Object $value -Name 'translation' -Label $recordLabel).Value
            $model = (Get-RequiredProperty -Object $value -Name 'model' -Label $recordLabel).Value
            $promptVersion = (Get-RequiredProperty -Object $value -Name 'prompt_version' -Label $recordLabel).Value
            $confidence = (Get-RequiredProperty -Object $value -Name 'confidence' -Label $recordLabel).Value
            $needsReview = (Get-RequiredProperty -Object $value -Name 'needs_review' -Label $recordLabel).Value
            $issueCodes = (Get-RequiredProperty -Object $value -Name 'issue_codes' -Label $recordLabel).Value

            if ($translation -isnot [string] -or [string]::IsNullOrWhiteSpace($translation)) {
                throw "$Label $id must contain a non-empty string translation."
            }
            if ($model -isnot [string] -or $promptVersion -isnot [string] -or
                -not [string]::Equals($model, $ExpectedModel, [StringComparison]::Ordinal) -or
                -not [string]::Equals($promptVersion, $ExpectedPromptVersion, [StringComparison]::Ordinal)) {
                throw "$Label $id must use the pinned model/prompt pair $ExpectedModel / $ExpectedPromptVersion."
            }
            if ($confidence -isnot [string] -or @('high', 'medium', 'low') -cnotcontains $confidence) {
                throw "$Label $id has an invalid confidence value."
            }
            if ($needsReview -isnot [bool]) { throw "$Label $id must contain needs_review as a JSON boolean." }
            if ($issueCodes -isnot [array]) { throw "$Label $id must contain issue_codes as a JSON array." }

            $seenIssues = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($issueCode in $issueCodes) {
                if ($issueCode -isnot [string] -or -not $allowedIssues.Contains($issueCode)) {
                    throw "$Label $id contains an invalid issue_codes entry."
                }
                if (-not $seenIssues.Add($issueCode)) { throw "$Label $id contains a duplicate issue_codes entry: $issueCode" }
            }
            $records.Add([pscustomobject]@{ Id = $id; Value = $value; Raw = $line })
        }
    }
    finally { $reader.Dispose() }
    return [pscustomobject]@{ Records = $records.ToArray() }
}

function Write-AtomicNewFile {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][byte[]] $Bytes
    )

    $parent = [IO.Path]::GetDirectoryName($Path)
    if ([string]::IsNullOrWhiteSpace($parent)) { throw 'Output must have a parent directory.' }
    $null = Assert-PrivateWorkflowPath -Path $parent -RepoRoot $repoRoot -Label 'Output parent'
    $null = [IO.Directory]::CreateDirectory($parent)
    $null = Assert-PrivateWorkflowPath -Path $Path -RepoRoot $repoRoot -Label 'Output'
    if ([IO.File]::Exists($Path) -or [IO.Directory]::Exists($Path)) { throw "Output already exists: $Path" }

    $stagePath = [IO.Path]::Combine($parent, ".$([IO.Path]::GetFileName($Path)).$([Guid]::NewGuid().ToString('N')).tmp")
    $null = Assert-PrivateWorkflowPath -Path $stagePath -RepoRoot $repoRoot -Label 'Output staging file'
    try {
        $stream = [IO.FileStream]::new($stagePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $stream.Write($Bytes, 0, $Bytes.Length)
            $stream.Flush($true)
        }
        finally { $stream.Dispose() }
        [IO.File]::Move($stagePath, $Path)
    }
    finally {
        if ([IO.File]::Exists($stagePath)) { [IO.File]::Delete($stagePath) }
    }
}

$jobsPath = Resolve-PrivateRegularFile -Path $Jobs -Label 'Jobs'
$checkpointPath = Resolve-PrivateRegularFile -Path $Checkpoint -Label 'Checkpoint'
$terraPath = Resolve-PrivateRegularFile -Path $TerraResults -Label 'Terra results'
$solPath = Resolve-PrivateRegularFile -Path $SolResults -Label 'Sol results'
$outputPath = Assert-PrivateWorkflowPath -Path $Output -RepoRoot $repoRoot -Label 'Output'
$outputPath = Assert-SafeNewOutputPath -Path $outputPath -Label 'Output'

$inputPaths = @($jobsPath, $checkpointPath, $terraPath, $solPath)
if ($inputPaths -contains $outputPath) { throw 'Output must differ from every input.' }

$jobsSnapshot = Read-FileSnapshot -Path $jobsPath -Label 'Jobs'
$checkpointSnapshot = Read-FileSnapshot -Path $checkpointPath -Label 'Checkpoint'
$terraSnapshot = Read-FileSnapshot -Path $terraPath -Label 'Terra results'
$solSnapshot = Read-FileSnapshot -Path $solPath -Label 'Sol results'
$checkpointValue = Convert-StrictJsonSnapshot -Snapshot $checkpointSnapshot -Label 'checkpoint'

$checkpointState = (Get-RequiredProperty -Object $checkpointValue -Name 'state' -Label 'Checkpoint').Value
if ($checkpointState -isnot [string] -or -not [string]::Equals($checkpointState, 'needs_sol', [StringComparison]::Ordinal)) {
    throw "Checkpoint must be needs_sol, got: $checkpointState"
}
$checkpointJobsHash = (Get-RequiredProperty -Object $checkpointValue -Name 'jobs_sha256' -Label 'Checkpoint').Value
$checkpointResultHash = (Get-RequiredProperty -Object $checkpointValue -Name 'result_sha256' -Label 'Checkpoint').Value
if ($checkpointJobsHash -isnot [string] -or $checkpointJobsHash -notmatch $shaPattern -or
    -not [string]::Equals($jobsSnapshot.Sha256, $checkpointJobsHash, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Jobs SHA-256 does not match checkpoint.'
}
if ($checkpointResultHash -isnot [string] -or $checkpointResultHash -notmatch $shaPattern -or
    -not [string]::Equals($terraSnapshot.Sha256, $checkpointResultHash, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Terra result SHA-256 does not match checkpoint.'
}

$jobRecords = @((Read-JobLines -Snapshot $jobsSnapshot -Label 'Jobs').Records)
$invalidJobRecord = @($jobRecords | Where-Object { $null -eq $_.PSObject.Properties['Id'] } | Select-Object -First 1)
if ($invalidJobRecord.Count -gt 0) {
    throw "Internal jobs parser returned an invalid record of type $($invalidJobRecord[0].GetType().FullName)."
}
$jobIds = @($jobRecords | ForEach-Object { $_.Id })
$terra = @((Read-ResultLines -Snapshot $terraSnapshot -Label 'Terra result' `
    -ExpectedModel 'gpt-5.6-terra' -ExpectedPromptVersion 'ru-v2' -AllowedIssueCodes $terraIssueCodes).Records)
$sol = @((Read-ResultLines -Snapshot $solSnapshot -Label 'Sol review' `
    -ExpectedModel 'gpt-5.6-sol' -ExpectedPromptVersion 'ru-review-v1' -AllowedIssueCodes $solIssueCodes).Records)

$terraById = @{}
foreach ($record in $terra) { $terraById[$record.Id] = $record }
if ($terra.Count -ne $jobIds.Count -or $terraById.Count -ne $jobIds.Count) {
    throw "Terra coverage is $($terra.Count); jobs require $($jobIds.Count)."
}
foreach ($id in $jobIds) {
    if (-not $terraById.ContainsKey($id)) { throw "Terra result is missing job_id: $id" }
}

$escalationValue = (Get-RequiredProperty -Object $checkpointValue -Name 'escalation_ids' -Label 'Checkpoint').Value
if ($escalationValue -isnot [array]) { throw 'Checkpoint escalation_ids must be a JSON array.' }
$expectedEscalations = [Collections.Generic.List[string]]::new()
$expectedEscalationSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($escalationId in $escalationValue) {
    if ($escalationId -isnot [string] -or [string]::IsNullOrWhiteSpace($escalationId) -or
        -not $expectedEscalationSet.Add($escalationId)) {
        throw 'Checkpoint escalation_ids must contain unique, non-empty strings.'
    }
    $expectedEscalations.Add($escalationId)
}
if ($expectedEscalations.Count -ne $sol.Count) {
    throw "Sol coverage is $($sol.Count); checkpoint requires $($expectedEscalations.Count)."
}
for ($index = 0; $index -lt $expectedEscalations.Count; $index++) {
    if (-not [string]::Equals($expectedEscalations[$index], $sol[$index].Id, [StringComparison]::Ordinal)) {
        throw "Sol order/set differs from checkpoint at index $index."
    }
}

$solById = @{}
foreach ($record in $sol) {
    if (-not $terraById.ContainsKey($record.Id)) { throw "Sol review contains unknown job_id: $($record.Id)" }
    $solById[$record.Id] = $record
}

$mergedLines = [Collections.Generic.List[string]]::new()
foreach ($id in $jobIds) {
    if ($solById.ContainsKey($id)) { $mergedLines.Add([string]$solById[$id].Raw) }
    else { $mergedLines.Add([string]$terraById[$id].Raw) }
}

$content = [string]::Join("`n", $mergedLines) + "`n"
$outputBytes = $strictUtf8.GetBytes($content)
$outputSha256 = Get-Sha256HexFromBytes -Bytes $outputBytes
Write-AtomicNewFile -Path $outputPath -Bytes $outputBytes

Write-Host "Merged $($sol.Count) Sol reviews into $($terra.Count) complete results."
Write-Host "Output SHA-256: $outputSha256"
