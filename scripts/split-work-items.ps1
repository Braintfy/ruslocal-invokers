param(
    [Parameter(Mandatory = $true)][string]$InputJobs,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$PromptPath,
    [Parameter(Mandatory = $true)][string]$GlossaryPath,
    [ValidateRange(25, 250)][int]$ChunkSize = 150,
    [ValidateRange(10000, 500000)][int]$MaxCharacters = 60000
)

$ErrorActionPreference = 'Stop'
$strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)

function Get-Sha256Hex([string]$Path) {
    $stream = [System.IO.File]::OpenRead([System.IO.Path]::GetFullPath($Path))
    try {
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '')
        }
        finally {
            $algorithm.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Write-VerifiedNewFile([string]$Path, [string]$Content) {
    $bytes = $strictUtf8.GetBytes($Content)
    if ([System.IO.File]::Exists($Path)) {
        $existing = [System.IO.File]::ReadAllBytes($Path)
        if (-not [System.Linq.Enumerable]::SequenceEqual[byte]($existing, $bytes)) {
            throw "Existing checkpoint or manifest differs: $Path"
        }
        return
    }

    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

$inputPath = [System.IO.Path]::GetFullPath($InputJobs)
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$promptFullPath = [System.IO.Path]::GetFullPath($PromptPath)
$glossaryFullPath = [System.IO.Path]::GetFullPath($GlossaryPath)

if (-not [System.IO.File]::Exists($inputPath)) { throw "Jobs file not found: $inputPath" }
if (-not [System.IO.File]::Exists($promptFullPath)) { throw "Prompt file not found: $promptFullPath" }
if (-not [System.IO.File]::Exists($glossaryFullPath)) { throw "Glossary file not found: $glossaryFullPath" }
if ($outputRoot.StartsWith($inputPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory cannot be inside the input file path.'
}

[System.IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$lines = [System.IO.File]::ReadAllLines($inputPath, $strictUtf8) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and -not $_.TrimStart().StartsWith('#') }
if ($lines.Count -eq 0) { throw 'The jobs file contains no work items.' }

$chunks = [System.Collections.Generic.List[object]]::new()
$current = [System.Collections.Generic.List[string]]::new()
$currentCharacters = 0
foreach ($line in $lines) {
    $null = $line | ConvertFrom-Json
    if ($current.Count -gt 0 -and ($current.Count -ge $ChunkSize -or $currentCharacters + $line.Length -gt $MaxCharacters)) {
        $chunks.Add($current.ToArray())
        $current = [System.Collections.Generic.List[string]]::new()
        $currentCharacters = 0
    }
    $current.Add($line)
    $currentCharacters += $line.Length
}
if ($current.Count -gt 0) { $chunks.Add($current.ToArray()) }

$inputSha = Get-Sha256Hex $inputPath
$promptSha = Get-Sha256Hex $promptFullPath
$glossarySha = Get-Sha256Hex $glossaryFullPath
$manifestChunks = [System.Collections.Generic.List[object]]::new()

for ($index = 0; $index -lt $chunks.Count; $index++) {
    $chunkId = 'chunk-{0:D4}' -f ($index + 1)
    $jobsName = "$chunkId.jobs.jsonl"
    $resultName = "$chunkId.result.jsonl"
    $checkpointName = "$chunkId.checkpoint.json"
    $jobsPath = [System.IO.Path]::Combine($outputRoot, $jobsName)
    $chunkContent = [string]::Join("`n", $chunks[$index]) + "`n"
    Write-VerifiedNewFile $jobsPath $chunkContent
    $chunkSha = Get-Sha256Hex $jobsPath

    $checkpoint = [ordered]@{
        schema = 1
        chunk_id = $chunkId
        state = 'pending'
        jobs_file = $jobsName
        jobs_sha256 = $chunkSha
        result_file = $resultName
        result_sha256 = $null
        prompt_sha256 = $promptSha
        review_prompt_sha256 = $null
        glossary_sha256 = $glossarySha
        model = $null
        validation_errors = $null
        escalation_ids = @()
    } | ConvertTo-Json -Depth 5
    Write-VerifiedNewFile ([System.IO.Path]::Combine($outputRoot, $checkpointName)) ($checkpoint + "`n")

    $manifestChunks.Add([ordered]@{
        chunk_id = $chunkId
        jobs_file = $jobsName
        jobs_sha256 = $chunkSha
        item_count = $chunks[$index].Count
        checkpoint_file = $checkpointName
    })
}

$manifest = [ordered]@{
    schema = 1
    workflow = 'codex-local-translation'
    source_jobs_sha256 = $inputSha
    prompt_sha256 = $promptSha
    glossary_sha256 = $glossarySha
    chunk_size_limit = $ChunkSize
    chunk_character_limit = $MaxCharacters
    total_items = $lines.Count
    chunks = $manifestChunks
} | ConvertTo-Json -Depth 6
Write-VerifiedNewFile ([System.IO.Path]::Combine($outputRoot, 'chunks.manifest.json')) ($manifest + "`n")

Write-Host "Created or verified $($chunks.Count) deterministic Codex chunks for $($lines.Count) work items."
Write-Host "Manifest: $([System.IO.Path]::Combine($outputRoot, 'chunks.manifest.json'))"
