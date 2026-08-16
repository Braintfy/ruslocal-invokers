[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [Alias('ChunksManifest', 'ChunksDirectory')]
    [string] $Chunks,

    [Parameter(Mandatory = $true)]
    [string] $SelectionManifest,

    [Parameter(Mandatory = $true)]
    [string] $English,

    [Parameter(Mandatory = $true)]
    [string] $BaseOverlay,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string] $ExpectedBaseOverlaySha256,

    [Parameter(Mandatory = $true)]
    [string] $PromptPath,

    [string] $ReviewPromptPath,

    [Parameter(Mandatory = $true)]
    [string] $GlossaryPath,

    [Parameter(Mandatory = $true)]
    [string] $Output,

    [Parameter(Mandatory = $true)]
    [string] $ReceiptPath,

    [string] $CliPath,

    [string] $DotnetPath,

    [string] $PrivateTempRoot,

    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
. (Join-Path $PSScriptRoot 'path-safety.ps1')

$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$shaPattern = '^[0-9A-Fa-f]{64}$'
$terraIssueCodes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($code in @('ambiguous_context', 'terminology', 'lore', 'ui_length', 'grammar', 'mechanics')) { $null = $terraIssueCodes.Add($code) }
$solIssueCodes = [Collections.Generic.HashSet[string]]::new($terraIssueCodes, [StringComparer]::Ordinal)
$null = $solIssueCodes.Add('source_problem')
$allowedRiskFlags = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($flag in @('legal_or_privacy', 'account_or_payment', 'long_text', 'protected_tokens', 'numeric', 'legacy_control', 'context_required')) { $null = $allowedRiskFlags.Add($flag) }

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string] $Path)

    $stream = [IO.File]::OpenRead([IO.Path]::GetFullPath($Path))
    try {
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') }
        finally { $algorithm.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Test-FixedHash {
    param(
        [AllowNull()][string] $Actual,
        [AllowNull()][string] $Expected
    )

    if ([string]::IsNullOrWhiteSpace($Actual) -or [string]::IsNullOrWhiteSpace($Expected)) { return $false }
    if ($Actual -notmatch $shaPattern -or $Expected -notmatch $shaPattern) { return $false }
    return [string]::Equals($Actual, $Expected, [StringComparison]::OrdinalIgnoreCase)
}

function Resolve-RegularFile {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not [IO.File]::Exists($fullPath)) { throw "$Label must be an existing regular file: $fullPath" }
    Assert-NoReparsePath -Path $fullPath -Label $Label
    return $fullPath
}

function Read-StrictJsonFile {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label
    )

    try { return [IO.File]::ReadAllText($Path, $strictUtf8) | ConvertFrom-Json }
    catch { throw "Invalid $Label JSON: $($_.Exception.Message)" }
}

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string[]] $Expected,
        [Parameter(Mandatory = $true)][string] $Label
    )

    if ($null -eq $Object) { throw "$Label cannot be null." }
    $expectedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $Expected) { $null = $expectedNames.Add($name) }
    $actualNames = @($Object.PSObject.Properties.Name)
    $unknown = @($actualNames | Where-Object { -not $expectedNames.Contains([string]$_) })
    $missing = @($Expected | Where-Object { $actualNames -cnotcontains $_ })
    if ($unknown.Count -gt 0 -or $missing.Count -gt 0 -or $actualNames.Count -ne $Expected.Count) {
        throw "$Label has missing, duplicate, or unsupported properties. Missing=[$([string]::Join(',', $missing))] Unknown=[$([string]::Join(',', $unknown))]"
    }
}

function Resolve-ManifestLeaf {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $invalidName = [string]::IsNullOrWhiteSpace($Name) -or
        $Name -eq '.' -or
        $Name -eq '..' -or
        $Name.IndexOfAny([char[]]@('/', '\', ':')) -ge 0 -or
        -not [string]::Equals([IO.Path]::GetFileName($Name), $Name, [StringComparison]::Ordinal)
    if ($invalidName) {
        throw "$Label must be a plain file name without path components."
    }

    $candidate = [IO.Path]::GetFullPath([IO.Path]::Combine($Root, $Name))
    if (-not (Test-CanonicalPathWithin -Path $candidate -Directory $Root)) {
        throw "$Label resolves outside the chunks directory."
    }
    return Resolve-RegularFile -Path $candidate -Label $Label
}

function Read-JsonLinesWithJobIds {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label,
        [AllowNull()][AllowEmptyCollection()][string[]] $AllowedPromptVersions
    )

    $records = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadAllLines($Path, $strictUtf8)) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }
        try { $record = $line | ConvertFrom-Json }
        catch { throw "Invalid $Label JSONL at line ${lineNumber}: $($_.Exception.Message)" }

        if (-not ($record.job_id -is [string])) {
            throw "$Label contains a non-string job_id at line $lineNumber."
        }
        $jobId = [string]$record.job_id
        if ([string]::IsNullOrWhiteSpace($jobId) -or -not $seen.Add($jobId)) {
            throw "$Label contains a missing or duplicate job_id at line $lineNumber."
        }
        if ($null -ne $AllowedPromptVersions -and $AllowedPromptVersions.Count -gt 0) {
            if ([string]::IsNullOrWhiteSpace([string]$record.prompt_version) -or
                $AllowedPromptVersions -cnotcontains [string]$record.prompt_version) {
                throw "$Label result $jobId does not use an allowed pinned prompt_version."
            }
        }
        $records.Add([pscustomobject]@{ Raw = $line; JobId = $jobId; Value = $record })
    }
    return $records
}

function Assert-StringArray {
    param(
        [AllowNull()] $Value,
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][Collections.Generic.HashSet[string]] $Allowed
    )

    if ($null -eq $Value -or -not ($Value -is [Array])) { throw "$Label must be an exact JSON array." }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $Value) {
        if (-not ($item -is [string]) -or [string]::IsNullOrWhiteSpace([string]$item) -or -not $Allowed.Contains([string]$item)) {
            throw "$Label contains a non-string, empty, or unsupported value."
        }
        if (-not $seen.Add([string]$item)) { throw "$Label contains a duplicate value: $item" }
    }
}

function Assert-ResultMetadata {
    param(
        [Parameter(Mandatory = $true)] $Result,
        [Parameter(Mandatory = $true)][string] $Label
    )

    if (-not ($Result.model -is [string]) -or -not ($Result.prompt_version -is [string]) -or
        -not ($Result.translation -is [string]) -or -not ($Result.confidence -is [string]) -or
        -not ($Result.needs_review -is [bool])) {
        throw "$Label has invalid model/prompt/translation/confidence/needs_review JSON types."
    }
    $model = [string]$Result.model
    $promptVersion = [string]$Result.prompt_version
    if (($model -eq 'gpt-5.6-terra' -and $promptVersion -ne 'ru-v2') -or
        ($model -eq 'gpt-5.6-sol' -and $promptVersion -ne 'ru-review-v1') -or
        $model -notin @('gpt-5.6-terra', 'gpt-5.6-sol')) {
        throw "$Label violates the exact model-to-prompt mapping."
    }
    if ([string]$Result.confidence -notin @('high', 'medium', 'low')) { throw "$Label has unsupported confidence." }
    $allowedIssues = if ($model -eq 'gpt-5.6-sol') { $solIssueCodes } else { $terraIssueCodes }
    Assert-StringArray -Value $Result.issue_codes -Label "$Label issue_codes" -Allowed $allowedIssues
    return $model
}

function Write-JsonLinesAggregate {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][Collections.Generic.List[string]] $Lines
    )

    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $writer = [IO.StreamWriter]::new($stream, $strictUtf8, 1024, $true)
        try {
            foreach ($line in $Lines) { $writer.WriteLine($line) }
            $writer.Flush()
            $stream.Flush($true)
        }
        finally { $writer.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Write-NewJsonFile {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $Value
    )

    $parent = [IO.Path]::GetDirectoryName($Path)
    if (-not [string]::IsNullOrWhiteSpace($parent)) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
    Assert-NoReparsePath -Path $Path -Label 'Wave import receipt'
    $temporaryName = ".{0}.{1}.tmp" -f @([IO.Path]::GetFileName($Path), [Guid]::NewGuid().ToString('N'))
    $temporary = [IO.Path]::Combine($parent, $temporaryName)
    try {
        $bytes = $strictUtf8.GetBytes(($Value | ConvertTo-Json -Depth 12) + "`n")
        $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 81920, [IO.FileOptions]::WriteThrough)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally { $stream.Dispose() }
        if ([IO.File]::Exists($Path) -or [IO.Directory]::Exists($Path)) { throw "Wave import receipt appeared before commit: $Path" }
        [IO.File]::Move($temporary, $Path)
    }
    finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Get-FileIdentity {
    param([Parameter(Mandatory = $true)][string] $Path)

    $item = [IO.FileInfo]::new([IO.Path]::GetFullPath($Path))
    $item.Refresh()
    if (-not $item.Exists) { throw "Expected created file is missing: $Path" }
    return [pscustomobject]@{
        FullName = $item.FullName
        Length = $item.Length
        CreationTicks = $item.CreationTimeUtc.Ticks
        LastWriteTicks = $item.LastWriteTimeUtc.Ticks
        Sha256 = Get-Sha256Hex $item.FullName
    }
}

function Test-FileIdentityUnchanged {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $Identity
    )

    if (-not [IO.File]::Exists($Path)) { return $false }
    $current = Get-FileIdentity -Path $Path
    return [string]::Equals($current.FullName, [string]$Identity.FullName, [StringComparison]::OrdinalIgnoreCase) -and
        $current.Length -eq [long]$Identity.Length -and
        $current.CreationTicks -eq [long]$Identity.CreationTicks -and
        $current.LastWriteTicks -eq [long]$Identity.LastWriteTicks -and
        (Test-FixedHash $current.Sha256 ([string]$Identity.Sha256))
}

$chunksInput = [IO.Path]::GetFullPath($Chunks)
if ([IO.Directory]::Exists($chunksInput)) {
    $chunksRoot = $chunksInput
    $chunksManifestPath = [IO.Path]::Combine($chunksRoot, 'chunks.manifest.json')
}
elseif ([IO.File]::Exists($chunksInput)) {
    $chunksManifestPath = $chunksInput
    $chunksRoot = [IO.Path]::GetDirectoryName($chunksManifestPath)
}
else {
    throw "Chunks must name an existing directory or chunks.manifest.json: $chunksInput"
}

$chunksManifestPath = Resolve-RegularFile -Path $chunksManifestPath -Label 'Chunks manifest'
$chunksRoot = Assert-PrivateWorkflowPath -Path $chunksRoot -RepoRoot $repoRoot -Label 'Private chunks directory'
$chunksManifestPath = Assert-PrivateWorkflowPath -Path $chunksManifestPath -RepoRoot $repoRoot -Label 'Private chunks manifest'
$selectionPath = Resolve-RegularFile -Path $SelectionManifest -Label 'Wave selection manifest'
$englishPath = Resolve-RegularFile -Path $English -Label 'Private English LOC1'
$englishPath = Assert-PrivateWorkflowPath -Path $englishPath -RepoRoot $repoRoot -Label 'Private English LOC1'
$baseOverlayPath = Resolve-RegularFile -Path $BaseOverlay -Label 'Base overlay'
$promptFullPath = Resolve-RegularFile -Path $PromptPath -Label 'Translation prompt'
$glossaryFullPath = Resolve-RegularFile -Path $GlossaryPath -Label 'Translation glossary'
foreach ($safeRead in @($chunksManifestPath, $selectionPath, $englishPath, $baseOverlayPath, $promptFullPath, $glossaryFullPath)) {
    Assert-OutsideProtectedRuntime -Path $safeRead -Label 'Wave import input'
}

$outputPath = Assert-SafeNewOutputPath -Path $Output -Label 'Wave import output'
$receiptFullPath = Assert-SafeNewOutputPath -Path $ReceiptPath -Label 'Wave import receipt'
if ([string]::Equals($outputPath, $receiptFullPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Wave import output and receipt must be different files.'
}

$chunksManifest = Read-StrictJsonFile -Path $chunksManifestPath -Label 'chunks manifest'
$selection = Read-StrictJsonFile -Path $selectionPath -Label 'wave selection manifest'
Assert-ExactProperties -Object $chunksManifest -Expected @(
    'schema', 'workflow', 'source_jobs_sha256', 'prompt_sha256', 'glossary_sha256',
    'chunk_size_limit', 'chunk_character_limit', 'total_items', 'chunks'
) -Label 'Chunks manifest'
Assert-ExactProperties -Object $selection -Expected @(
    'schema', 'workflow', 'chunks_manifest_sha256', 'base_overlay_sha256', 'prompt_sha256',
    'glossary_sha256', 'prompt_version', 'review_prompt_sha256', 'review_prompt_version', 'complete_chunks'
) -Label 'Wave selection manifest'

if ([int]$chunksManifest.schema -ne 1 -or [string]$chunksManifest.workflow -ne 'codex-local-translation') {
    throw 'Chunks manifest must use schema 1 and workflow codex-local-translation.'
}
if ([string]$chunksManifest.source_jobs_sha256 -notmatch $shaPattern -or
    [string]$chunksManifest.prompt_sha256 -notmatch $shaPattern -or
    [string]$chunksManifest.glossary_sha256 -notmatch $shaPattern -or
    [int]$chunksManifest.total_items -le 0) {
    throw 'Chunks manifest has invalid corpus/hash metadata.'
}
if ([int]$selection.schema -ne 1 -or [string]$selection.workflow -ne 'codex-local-import-wave') {
    throw 'Wave selection manifest must use schema 1 and workflow codex-local-import-wave.'
}
if (-not [string]::Equals([string]$selection.prompt_version, 'ru-v2', [StringComparison]::Ordinal)) {
    throw 'Wave selection manifest must pin translation prompt_version ru-v2.'
}

$chunksManifestSha = Get-Sha256Hex $chunksManifestPath
$selectionSha = Get-Sha256Hex $selectionPath
$baseOverlaySha = Get-Sha256Hex $baseOverlayPath
$promptSha = Get-Sha256Hex $promptFullPath
$glossarySha = Get-Sha256Hex $glossaryFullPath
$englishSha = Get-Sha256Hex $englishPath
$hasReviewHash = -not [string]::IsNullOrWhiteSpace([string]$selection.review_prompt_sha256)
$hasReviewVersion = -not [string]::IsNullOrWhiteSpace([string]$selection.review_prompt_version)
if ($hasReviewHash -ne $hasReviewVersion) { throw 'Wave selection must supply review prompt hash/version together.' }
if ($hasReviewVersion -and -not [string]::Equals([string]$selection.review_prompt_version, 'ru-review-v1', [StringComparison]::Ordinal)) {
    throw 'Wave selection review prompt_version must be ru-review-v1.'
}
$reviewPromptSha = $null
if ($hasReviewHash) {
    if ([string]::IsNullOrWhiteSpace($ReviewPromptPath)) { throw 'ReviewPromptPath is required by the wave selection.' }
    $reviewPromptFullPath = Resolve-RegularFile -Path $ReviewPromptPath -Label 'Translation review prompt'
    Assert-OutsideProtectedRuntime -Path $reviewPromptFullPath -Label 'Wave import input'
    $reviewPromptSha = Get-Sha256Hex $reviewPromptFullPath
    if (-not (Test-FixedHash $reviewPromptSha ([string]$selection.review_prompt_sha256)) -or
        [string]::Equals([string]$selection.review_prompt_version, [string]$selection.prompt_version, [StringComparison]::Ordinal)) {
        throw 'Review prompt content/version does not match its independent selection pin.'
    }
}
elseif (-not [string]::IsNullOrWhiteSpace($ReviewPromptPath)) {
    throw 'ReviewPromptPath was supplied, but the wave selection does not pin a review prompt.'
}
$allowedPromptVersions = @([string]$selection.prompt_version)
if ($hasReviewVersion) { $allowedPromptVersions += [string]$selection.review_prompt_version }

if (-not (Test-FixedHash $chunksManifestSha ([string]$selection.chunks_manifest_sha256)) -or
    -not (Test-FixedHash $baseOverlaySha ([string]$selection.base_overlay_sha256)) -or
    -not (Test-FixedHash $baseOverlaySha $ExpectedBaseOverlaySha256) -or
    -not (Test-FixedHash $promptSha ([string]$chunksManifest.prompt_sha256)) -or
    -not (Test-FixedHash $promptSha ([string]$selection.prompt_sha256)) -or
    -not (Test-FixedHash $glossarySha ([string]$chunksManifest.glossary_sha256)) -or
    -not (Test-FixedHash $glossarySha ([string]$selection.glossary_sha256))) {
    throw 'Chunks manifest, selection manifest, base overlay, prompt, or glossary SHA-256 pin does not match.'
}

$manifestChunks = @($chunksManifest.chunks)
$manifestById = @{}
$manifestFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$manifestItemTotal = [long]0
foreach ($chunk in $manifestChunks) {
    Assert-ExactProperties -Object $chunk -Expected @(
        'chunk_id', 'jobs_file', 'jobs_sha256', 'item_count', 'checkpoint_file'
    ) -Label 'Chunks manifest record'
    $chunkId = [string]$chunk.chunk_id
    if ($chunkId -notmatch '^chunk-[0-9]{4,8}$' -or $manifestById.ContainsKey($chunkId)) {
        throw "Chunks manifest contains an invalid or duplicate chunk_id: $chunkId"
    }
    if ([int]$chunk.item_count -le 0 -or [string]$chunk.jobs_sha256 -notmatch $shaPattern) {
        throw "Chunks manifest record $chunkId has an invalid count or jobs hash."
    }
    foreach ($manifestFileName in @([string]$chunk.jobs_file, [string]$chunk.checkpoint_file)) {
        if ([string]::IsNullOrWhiteSpace($manifestFileName) -or
            $manifestFileName.IndexOfAny([char[]]@('/', '\', ':')) -ge 0 -or
            -not $manifestFiles.Add($manifestFileName)) {
            throw "Chunks manifest record $chunkId contains an unsafe or reused file name."
        }
    }
    $manifestItemTotal += [int]$chunk.item_count
    $manifestById[$chunkId] = $chunk
}
if ($manifestChunks.Count -eq 0) { throw 'Chunks manifest contains no chunks.' }
if ($manifestItemTotal -ne [long]$chunksManifest.total_items) {
    throw "Chunks manifest total_items is $($chunksManifest.total_items), but chunk records sum to $manifestItemTotal."
}

$selectedRecords = @($selection.complete_chunks)
if ($selectedRecords.Count -eq 0) { throw 'Wave selection manifest explicitly selects no complete chunks.' }
$selectedIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$allJobIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$allResultIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$aggregateJobLines = [Collections.Generic.List[string]]::new()
$aggregateResultLines = [Collections.Generic.List[string]]::new()
$receiptChunks = [Collections.Generic.List[object]]::new()

foreach ($selected in $selectedRecords) {
    Assert-ExactProperties -Object $selected -Expected @('chunk_id', 'checkpoint_sha256', 'result_sha256') -Label 'Wave selection record'
    $chunkId = [string]$selected.chunk_id
    if (-not $selectedIds.Add($chunkId)) { throw "Wave selection contains duplicate chunk_id: $chunkId" }
    if (-not $manifestById.ContainsKey($chunkId)) { throw "Wave selection contains unknown chunk_id: $chunkId" }
    if ([string]$selected.checkpoint_sha256 -notmatch $shaPattern -or [string]$selected.result_sha256 -notmatch $shaPattern) {
        throw "Wave selection record $chunkId has an invalid checkpoint or result SHA-256."
    }

    $chunk = $manifestById[$chunkId]
    $jobsPath = Resolve-ManifestLeaf -Root $chunksRoot -Name ([string]$chunk.jobs_file) -Label "$chunkId jobs"
    $checkpointPath = Resolve-ManifestLeaf -Root $chunksRoot -Name ([string]$chunk.checkpoint_file) -Label "$chunkId checkpoint"
    $jobsSha = Get-Sha256Hex $jobsPath
    $checkpointSha = Get-Sha256Hex $checkpointPath
    if (-not (Test-FixedHash $jobsSha ([string]$chunk.jobs_sha256)) -or
        -not (Test-FixedHash $checkpointSha ([string]$selected.checkpoint_sha256))) {
        throw "$chunkId jobs or checkpoint SHA-256 does not match its signed selection chain."
    }

    $checkpoint = Read-StrictJsonFile -Path $checkpointPath -Label "$chunkId checkpoint"
    Assert-ExactProperties -Object $checkpoint -Expected @(
        'schema', 'chunk_id', 'state', 'jobs_file', 'jobs_sha256', 'result_file', 'result_sha256',
        'prompt_sha256', 'review_prompt_sha256', 'glossary_sha256', 'model', 'validation_errors', 'escalation_ids'
    ) -Label "$chunkId checkpoint"
    $checkpointState = [string]$checkpoint.state
    if ($checkpointState -notin @('terra_done', 'validated', 'needs_sol') -or
        [int]$checkpoint.schema -ne 1 -or
        [string]$checkpoint.chunk_id -ne $chunkId -or
        $null -eq $checkpoint.escalation_ids -or
        [string]$checkpoint.jobs_file -ne [string]$chunk.jobs_file -or
        -not (Test-FixedHash ([string]$checkpoint.jobs_sha256) $jobsSha) -or
        -not (Test-FixedHash ([string]$checkpoint.prompt_sha256) $promptSha) -or
        -not (Test-FixedHash ([string]$checkpoint.glossary_sha256) $glossarySha)) {
        throw "$chunkId checkpoint is not a complete translation state bound to the selected jobs/prompt/glossary."
    }
    if ([string]::IsNullOrWhiteSpace([string]$checkpoint.model)) {
        throw "$chunkId checkpoint must pin its exact sorted model set."
    }
    if ($checkpointState -ne 'terra_done' -and [int]$checkpoint.validation_errors -ne 0) {
        throw "$chunkId checkpoint state $checkpointState requires zero validation errors and model provenance."
    }

    $resultPath = Resolve-ManifestLeaf -Root $chunksRoot -Name ([string]$checkpoint.result_file) -Label "$chunkId result"
    $resultSha = Get-Sha256Hex $resultPath
    $checkpointResultHashMatches = Test-FixedHash $resultSha ([string]$checkpoint.result_sha256)
    if (($checkpointState -ne 'terra_done' -and -not $checkpointResultHashMatches) -or
        ($checkpointState -eq 'terra_done' -and -not [string]::IsNullOrWhiteSpace([string]$checkpoint.result_sha256) -and -not $checkpointResultHashMatches) -or
        -not (Test-FixedHash $resultSha ([string]$selected.result_sha256))) {
        throw "$chunkId result SHA-256 does not match its checkpoint and wave selection pins."
    }

    $jobs = @(Read-JsonLinesWithJobIds -Path $jobsPath -Label "$chunkId jobs" -AllowedPromptVersions @())
    $results = @(Read-JsonLinesWithJobIds -Path $resultPath -Label "$chunkId results" -AllowedPromptVersions $allowedPromptVersions)
    if ($jobs.Count -ne [int]$chunk.item_count) {
        throw "$chunkId jobs coverage is $($jobs.Count); manifest requires $($chunk.item_count)."
    }

    $chunkJobIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($job in $jobs) {
        if (-not $chunkJobIds.Add([string]$job.JobId) -or -not $allJobIds.Add([string]$job.JobId)) {
            throw "Duplicate job_id across selected chunks: $($job.JobId)"
        }
        $aggregateJobLines.Add([string]$job.Raw)
    }

    $chunkResultIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($result in $results) {
        if (-not $chunkResultIds.Add([string]$result.JobId) -or -not $allResultIds.Add([string]$result.JobId)) {
            throw "Duplicate result job_id across selected chunks: $($result.JobId)"
        }
        $aggregateResultLines.Add([string]$result.Raw)
    }
    if ($chunkJobIds.Count -ne $chunkResultIds.Count) {
        throw "$chunkId result coverage is $($chunkResultIds.Count); expected exactly $($chunkJobIds.Count)."
    }
    foreach ($jobId in $chunkJobIds) {
        if (-not $chunkResultIds.Contains($jobId)) { throw "$chunkId result is missing job_id: $jobId" }
    }
    foreach ($jobId in $chunkResultIds) {
        if (-not $chunkJobIds.Contains($jobId)) { throw "$chunkId result contains unknown job_id: $jobId" }
    }

    $jobById = @{}
    foreach ($job in $jobs) {
        if ($null -eq $job.Value) { throw "$chunkId job $($job.JobId) is null." }
        Assert-StringArray -Value $job.Value.risk_flags -Label "$chunkId job $($job.JobId) risk_flags" -Allowed $allowedRiskFlags
        $jobById[[string]$job.JobId] = $job.Value
    }
    $derivedEscalations = [Collections.Generic.List[string]]::new()
    $chunkModels = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($result in $results) {
        $model = Assert-ResultMetadata -Result $result.Value -Label "$chunkId result $($result.JobId)"
        $null = $chunkModels.Add($model)
        $jobValue = $jobById[[string]$result.JobId]
        $riskFlags = @($jobValue.risk_flags)
        if ([bool]$result.Value.needs_review -or
            [string]$result.Value.confidence -ne 'high' -or
            $riskFlags -contains 'context_required' -or
            $riskFlags -contains 'long_text') {
            $derivedEscalations.Add([string]$result.JobId)
        }
    }
    [string[]]$sortedModels = @($chunkModels)
    [Array]::Sort($sortedModels, [StringComparer]::Ordinal)
    $actualModelSet = [string]::Join(',', $sortedModels)
    if (-not [string]::Equals($actualModelSet, [string]$checkpoint.model, [StringComparison]::Ordinal)) {
        throw "$chunkId actual sorted model set '$actualModelSet' does not match checkpoint.model '$($checkpoint.model)'."
    }
    if ($chunkModels.Contains('gpt-5.6-sol')) {
        if (-not $hasReviewHash -or -not (Test-FixedHash ([string]$checkpoint.review_prompt_sha256) $reviewPromptSha)) {
            throw "$chunkId Sol results are not chained to the pinned review prompt SHA-256."
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace([string]$checkpoint.review_prompt_sha256)) {
        throw "$chunkId Terra-only checkpoint unexpectedly pins a review prompt."
    }

    if ($checkpointState -ne 'terra_done') {
        $checkpointEscalations = @($checkpoint.escalation_ids | ForEach-Object { [string]$_ })
        if ($checkpointEscalations.Count -ne $derivedEscalations.Count) {
            throw "$chunkId checkpoint escalation coverage $($checkpointEscalations.Count) does not match derived $($derivedEscalations.Count)."
        }
        for ($escalationIndex = 0; $escalationIndex -lt $derivedEscalations.Count; $escalationIndex++) {
            if (-not [string]::Equals($checkpointEscalations[$escalationIndex], $derivedEscalations[$escalationIndex], [StringComparison]::Ordinal)) {
                throw "$chunkId checkpoint escalation order/id mismatch at index $escalationIndex."
            }
        }
        if (($checkpointState -eq 'validated' -and $derivedEscalations.Count -ne 0) -or
            ($checkpointState -eq 'needs_sol' -and $derivedEscalations.Count -eq 0)) {
            throw "$chunkId checkpoint state does not match the derived escalation set."
        }
    }

    if (-not (Test-FixedHash (Get-Sha256Hex $jobsPath) $jobsSha) -or
        -not (Test-FixedHash (Get-Sha256Hex $resultPath) $resultSha) -or
        -not (Test-FixedHash (Get-Sha256Hex $checkpointPath) $checkpointSha)) {
        throw "$chunkId jobs/result/checkpoint changed while the wave was being validated."
    }

    $receiptChunks.Add([ordered]@{
        chunk_id = $chunkId
        checkpoint_state = $checkpointState
        jobs_sha256 = $jobsSha
        checkpoint_sha256 = $checkpointSha
        result_sha256 = $resultSha
        job_count = $jobs.Count
        escalation_count = $derivedEscalations.Count
    })
}

if ($allJobIds.Count -ne $allResultIds.Count) {
    throw 'Selected wave does not have exact global jobs/results coverage.'
}
if (-not (Test-FixedHash (Get-Sha256Hex $chunksManifestPath) $chunksManifestSha) -or
    -not (Test-FixedHash (Get-Sha256Hex $selectionPath) $selectionSha)) {
    throw 'Chunks or wave selection manifest changed while the wave was being validated.'
}

$tempRoot = if ([string]::IsNullOrWhiteSpace($PrivateTempRoot)) { [IO.Path]::GetTempPath() } else { [IO.Path]::GetFullPath($PrivateTempRoot) }
$tempRoot = Assert-PrivateTempRoot -Path $tempRoot -RepoRoot $repoRoot -Label 'Private aggregation root'
$privateDirectory = [IO.Path]::Combine($tempRoot, "invokers-ru-wave-$([Guid]::NewGuid().ToString('N'))")
if ([IO.File]::Exists($privateDirectory) -or [IO.Directory]::Exists($privateDirectory)) { throw 'Private aggregation directory unexpectedly already exists.' }
[IO.Directory]::CreateDirectory($privateDirectory) | Out-Null

$aggregateJobsPath = [IO.Path]::Combine($privateDirectory, 'selected.jobs.private.jsonl')
$aggregateResultsPath = [IO.Path]::Combine($privateDirectory, 'selected.results.private.jsonl')
$baseSnapshotPath = [IO.Path]::Combine($privateDirectory, 'base-overlay.snapshot.jsonl')
$englishSnapshotPath = [IO.Path]::Combine($privateDirectory, 'english.snapshot.loc1')
$outputSha = $null
$outputIdentity = $null
$outputCreatedByThisRun = $false
try {
    Write-JsonLinesAggregate -Path $aggregateJobsPath -Lines $aggregateJobLines
    Write-JsonLinesAggregate -Path $aggregateResultsPath -Lines $aggregateResultLines
    [IO.File]::Copy($baseOverlayPath, $baseSnapshotPath, $false)
    [IO.File]::Copy($englishPath, $englishSnapshotPath, $false)

    if (-not (Test-FixedHash (Get-Sha256Hex $baseSnapshotPath) $ExpectedBaseOverlaySha256) -or
        -not (Test-FixedHash (Get-Sha256Hex $englishSnapshotPath) $englishSha)) {
        throw 'Private base/English snapshot changed during preparation.'
    }

    $aggregateJobsSha = Get-Sha256Hex $aggregateJobsPath
    $aggregateResultsSha = Get-Sha256Hex $aggregateResultsPath
    if (-not $DryRun) {
        if ([IO.File]::Exists($outputPath) -or [IO.Directory]::Exists($outputPath)) {
            throw "Wave import output appeared before CLI commit: $outputPath"
        }
        if ([string]::IsNullOrWhiteSpace($CliPath)) { throw 'CliPath is required unless -DryRun is used.' }
        $cliFullPath = Resolve-RegularFile -Path $CliPath -Label 'InvokersRu CLI'
        Assert-OutsideProtectedRuntime -Path $cliFullPath -Label 'InvokersRu CLI'
        $extension = [IO.Path]::GetExtension($cliFullPath)
        $arguments = @(
            'import-results', '--english', $englishSnapshotPath, '--jobs', $aggregateJobsPath,
            '--results', $aggregateResultsPath, '--translations', $baseSnapshotPath, '--output', $outputPath
        )

        if ([string]::Equals($extension, '.dll', [StringComparison]::OrdinalIgnoreCase)) {
            $dotnetExecutable = if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
                (Get-Command dotnet -ErrorAction Stop).Source
            }
            else {
                Resolve-RegularFile -Path $DotnetPath -Label '.NET host'
            }
            if (-not [string]::Equals([IO.Path]::GetExtension($dotnetExecutable), '.exe', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'DotnetPath must name dotnet.exe.'
            }
            $cliOutput = @(& $dotnetExecutable $cliFullPath @arguments 2>&1)
        }
        elseif ([string]::Equals($extension, '.exe', [StringComparison]::OrdinalIgnoreCase)) {
            $cliOutput = @(& $cliFullPath @arguments 2>&1)
        }
        else {
            throw 'CliPath must name an InvokersRu .exe or .dll.'
        }
        $cliExitCode = $LASTEXITCODE
        if ($cliExitCode -ne 0) {
            throw "InvokersRu CLI import-results failed with exit code ${cliExitCode}:`n$($cliOutput -join [Environment]::NewLine)"
        }
        if (-not [IO.File]::Exists($outputPath)) { throw 'CLI reported success but did not create the requested output.' }
        $outputIdentity = Get-FileIdentity -Path $outputPath
        $outputSha = [string]$outputIdentity.Sha256
        $outputCreatedByThisRun = $true
        foreach ($line in $cliOutput) { Write-Host $line }
    }

    $receipt = [ordered]@{
        schema = 1
        kind = 'invokers-ru-translation-wave-import'
        status = if ($DryRun) { 'validated-dry-run' } else { 'imported' }
        generated_at = [DateTimeOffset]::UtcNow.ToString('O')
        chunks_manifest_sha256 = $chunksManifestSha
        selection_manifest_sha256 = $selectionSha
        base_overlay_sha256 = $baseOverlaySha
        english_container_sha256 = $englishSha
        prompt_sha256 = $promptSha
        review_prompt_sha256 = $reviewPromptSha
        glossary_sha256 = $glossarySha
        prompt_versions = $allowedPromptVersions
        aggregate_jobs_sha256 = $aggregateJobsSha
        aggregate_results_sha256 = $aggregateResultsSha
        selected_chunk_count = $selectedRecords.Count
        selected_job_count = $allJobIds.Count
        chunks = $receiptChunks
        output_file = [IO.Path]::GetFileName($outputPath)
        output_sha256 = $outputSha
    }
    try {
        Write-NewJsonFile -Path $receiptFullPath -Value $receipt
    }
    catch {
        $receiptCommitError = $_
        if ($outputCreatedByThisRun) {
            if (Test-FileIdentityUnchanged -Path $outputPath -Identity $outputIdentity) {
                [IO.File]::Delete($outputPath)
                if ([IO.File]::Exists($outputPath)) { throw 'Receipt commit failed and exact created output rollback did not complete.' }
                $outputCreatedByThisRun = $false
            }
            else {
                throw [InvalidOperationException]::new('Receipt commit failed after output creation, but output identity changed; refusing to delete it.', $receiptCommitError.Exception)
            }
        }
        throw
    }
}
finally {
    foreach ($privateFile in @($aggregateJobsPath, $aggregateResultsPath, $baseSnapshotPath, $englishSnapshotPath)) {
        if ([IO.File]::Exists($privateFile)) { [IO.File]::Delete($privateFile) }
    }
    if ([IO.Directory]::Exists($privateDirectory)) { [IO.Directory]::Delete($privateDirectory, $false) }
}

Write-Host "Translation wave $($receipt.status): $($allJobIds.Count) jobs from $($selectedRecords.Count) explicitly selected chunks."
Write-Host "Source-free receipt: $receiptFullPath"
if (-not $DryRun) { Write-Host "New overlay: $outputPath ($outputSha)" }
