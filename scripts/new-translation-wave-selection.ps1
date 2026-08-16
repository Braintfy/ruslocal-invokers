[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [Alias('ChunksManifest', 'ChunksDirectory')]
    [string] $Chunks,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]] $CompleteChunkId,

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
    [ValidatePattern('^[A-Za-z0-9._-]{1,100}$')]
    [string] $PromptVersion,

    [ValidatePattern('^[A-Za-z0-9._-]{1,100}$')]
    [string] $ReviewPromptVersion,

    [Parameter(Mandatory = $true)]
    [string] $Output
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
. (Join-Path $PSScriptRoot 'path-safety.ps1')
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

function Get-Sha256Hex([string] $Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function Test-FixedHash([string] $Actual, [string] $Expected) {
    return $Actual -match '^[0-9A-Fa-f]{64}$' -and $Expected -match '^[0-9A-Fa-f]{64}$' -and
        [string]::Equals($Actual, $Expected, [StringComparison]::OrdinalIgnoreCase)
}

function Resolve-RegularFile([string] $Path, [string] $Label) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not [IO.File]::Exists($fullPath)) { throw "$Label must be an existing regular file: $fullPath" }
    Assert-NoReparsePath -Path $fullPath -Label $Label
    Assert-OutsideProtectedRuntime -Path $fullPath -Label $Label
    return $fullPath
}

function Resolve-Leaf([string] $Root, [string] $Name, [string] $Label) {
    if ([string]::IsNullOrWhiteSpace($Name) -or
        $Name.IndexOfAny([char[]]@('/', '\', ':')) -ge 0 -or
        -not [string]::Equals([IO.Path]::GetFileName($Name), $Name, [StringComparison]::Ordinal)) {
        throw "$Label must be a plain file name."
    }
    $path = [IO.Path]::GetFullPath([IO.Path]::Combine($Root, $Name))
    if (-not (Test-CanonicalPathWithin -Path $path -Directory $Root)) { throw "$Label leaves the chunks directory." }
    return Resolve-RegularFile -Path $path -Label $Label
}

$chunksInput = [IO.Path]::GetFullPath($Chunks)
if ([IO.Directory]::Exists($chunksInput)) {
    $chunksRoot = $chunksInput
    $manifestPath = [IO.Path]::Combine($chunksRoot, 'chunks.manifest.json')
}
elseif ([IO.File]::Exists($chunksInput)) {
    $manifestPath = $chunksInput
    $chunksRoot = [IO.Path]::GetDirectoryName($manifestPath)
}
else { throw "Chunks must name an existing directory or manifest: $chunksInput" }

$manifestPath = Resolve-RegularFile $manifestPath 'Chunks manifest'
$chunksRoot = Assert-PrivateWorkflowPath -Path $chunksRoot -RepoRoot $repoRoot -Label 'Private chunks directory'
$manifestPath = Assert-PrivateWorkflowPath -Path $manifestPath -RepoRoot $repoRoot -Label 'Private chunks manifest'
$basePath = Resolve-RegularFile $BaseOverlay 'Base overlay'
$promptFullPath = Resolve-RegularFile $PromptPath 'Translation prompt'
$glossaryFullPath = Resolve-RegularFile $GlossaryPath 'Translation glossary'
$hasReviewPrompt = -not [string]::IsNullOrWhiteSpace($ReviewPromptPath)
$hasReviewVersion = -not [string]::IsNullOrWhiteSpace($ReviewPromptVersion)
if ($hasReviewPrompt -ne $hasReviewVersion) { throw 'ReviewPromptPath and ReviewPromptVersion must be supplied together.' }
if (-not [string]::Equals($PromptVersion, 'ru-v2', [StringComparison]::Ordinal)) {
    throw 'PromptVersion must be exactly ru-v2.'
}
if ($hasReviewVersion -and -not [string]::Equals($ReviewPromptVersion, 'ru-review-v1', [StringComparison]::Ordinal)) {
    throw 'ReviewPromptVersion must be exactly ru-review-v1.'
}
$reviewPromptFullPath = $null
if ($hasReviewPrompt) { $reviewPromptFullPath = Resolve-RegularFile $ReviewPromptPath 'Translation review prompt' }
$outputPath = Assert-SafeNewOutputPath -Path $Output -Label 'Wave selection manifest'

try { $manifest = [IO.File]::ReadAllText($manifestPath, $strictUtf8) | ConvertFrom-Json }
catch { throw "Invalid chunks manifest JSON: $($_.Exception.Message)" }
if ([int]$manifest.schema -ne 1 -or [string]$manifest.workflow -ne 'codex-local-translation') {
    throw 'Chunks manifest must use schema 1 and workflow codex-local-translation.'
}

$baseSha = Get-Sha256Hex $basePath
$promptSha = Get-Sha256Hex $promptFullPath
$reviewPromptSha = if ($hasReviewPrompt) { Get-Sha256Hex $reviewPromptFullPath } else { $null }
$glossarySha = Get-Sha256Hex $glossaryFullPath
if (-not (Test-FixedHash $baseSha $ExpectedBaseOverlaySha256) -or
    -not (Test-FixedHash $promptSha ([string]$manifest.prompt_sha256)) -or
    -not (Test-FixedHash $glossarySha ([string]$manifest.glossary_sha256))) {
    throw 'Base overlay, prompt, or glossary does not match its expected SHA-256 pin.'
}

$chunksById = @{}
foreach ($chunk in @($manifest.chunks)) {
    $chunkId = [string]$chunk.chunk_id
    if ([string]::IsNullOrWhiteSpace($chunkId) -or $chunksById.ContainsKey($chunkId)) {
        throw "Chunks manifest has a missing or duplicate chunk_id: $chunkId"
    }
    $chunksById[$chunkId] = $chunk
}

$selectedIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$selectionRecords = [Collections.Generic.List[object]]::new()
foreach ($chunkId in $CompleteChunkId) {
    if ($chunkId -notmatch '^chunk-[0-9]{4,8}$' -or -not $selectedIds.Add($chunkId)) {
        throw "CompleteChunkId contains an invalid or duplicate id: $chunkId"
    }
    if (-not $chunksById.ContainsKey($chunkId)) { throw "CompleteChunkId is absent from the chunks manifest: $chunkId" }

    $chunk = $chunksById[$chunkId]
    $jobsPath = Resolve-Leaf $chunksRoot ([string]$chunk.jobs_file) "$chunkId jobs"
    $checkpointPath = Resolve-Leaf $chunksRoot ([string]$chunk.checkpoint_file) "$chunkId checkpoint"
    if (-not (Test-FixedHash (Get-Sha256Hex $jobsPath) ([string]$chunk.jobs_sha256))) {
        throw "$chunkId jobs hash does not match the chunks manifest."
    }

    try { $checkpoint = [IO.File]::ReadAllText($checkpointPath, $strictUtf8) | ConvertFrom-Json }
    catch { throw "Invalid $chunkId checkpoint JSON: $($_.Exception.Message)" }
    $state = [string]$checkpoint.state
    if ($state -notin @('terra_done', 'validated', 'needs_sol') -or
        [string]$checkpoint.chunk_id -ne $chunkId -or
        [string]$checkpoint.jobs_file -ne [string]$chunk.jobs_file -or
        -not (Test-FixedHash ([string]$checkpoint.jobs_sha256) ([string]$chunk.jobs_sha256)) -or
        -not (Test-FixedHash ([string]$checkpoint.prompt_sha256) $promptSha) -or
        -not (Test-FixedHash ([string]$checkpoint.glossary_sha256) $glossarySha)) {
        throw "$chunkId checkpoint is not a complete result bound to the selected workflow inputs."
    }
    $checkpointModels = @([string]$checkpoint.model -split ',')
    if ($checkpointModels -contains 'gpt-5.6-sol') {
        $reviewHashProperty = $checkpoint.PSObject.Properties['review_prompt_sha256']
        if (-not $hasReviewPrompt -or $null -eq $reviewHashProperty -or
            -not (Test-FixedHash ([string]$reviewHashProperty.Value) $reviewPromptSha)) {
            throw "$chunkId Sol checkpoint is not chained to the selected review prompt."
        }
    }

    $resultPath = Resolve-Leaf $chunksRoot ([string]$checkpoint.result_file) "$chunkId result"
    $resultSha = Get-Sha256Hex $resultPath
    if ($state -ne 'terra_done' -and -not (Test-FixedHash $resultSha ([string]$checkpoint.result_sha256))) {
        throw "$chunkId result hash does not match its completed checkpoint."
    }
    if ($state -eq 'terra_done' -and
        -not [string]::IsNullOrWhiteSpace([string]$checkpoint.result_sha256) -and
        -not (Test-FixedHash $resultSha ([string]$checkpoint.result_sha256))) {
        throw "$chunkId optional terra_done result hash does not match its checkpoint."
    }

    $selectionRecords.Add([ordered]@{
        chunk_id = $chunkId
        checkpoint_sha256 = Get-Sha256Hex $checkpointPath
        result_sha256 = $resultSha
    })
}

$selection = [ordered]@{
    schema = 1
    workflow = 'codex-local-import-wave'
    chunks_manifest_sha256 = Get-Sha256Hex $manifestPath
    base_overlay_sha256 = $baseSha
    prompt_sha256 = $promptSha
    glossary_sha256 = $glossarySha
    prompt_version = $PromptVersion
    review_prompt_sha256 = $reviewPromptSha
    review_prompt_version = if ($hasReviewPrompt) { $ReviewPromptVersion } else { $null }
    complete_chunks = $selectionRecords
}

$parent = [IO.Path]::GetDirectoryName($outputPath)
if ($parent) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
Assert-NoReparsePath -Path $outputPath -Label 'Wave selection manifest'
$temporary = [IO.Path]::Combine($parent, ".{0}.{1}.tmp" -f @([IO.Path]::GetFileName($outputPath), [Guid]::NewGuid().ToString('N')))
try {
    $bytes = $strictUtf8.GetBytes(($selection | ConvertTo-Json -Depth 8) + "`n")
    $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 81920, [IO.FileOptions]::WriteThrough)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally { $stream.Dispose() }
    if ([IO.File]::Exists($outputPath) -or [IO.Directory]::Exists($outputPath)) {
        throw "Wave selection manifest appeared before commit: $outputPath"
    }
    [IO.File]::Move($temporary, $outputPath)
}
finally {
    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}

Write-Host "Created source-free wave selection for $($selectionRecords.Count) explicitly named complete chunks."
Write-Host "Selection: $outputPath"
