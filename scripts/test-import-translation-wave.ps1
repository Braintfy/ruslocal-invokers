[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$importScript = Join-Path $PSScriptRoot 'import-translation-wave.ps1'
$markScript = Join-Path $PSScriptRoot 'mark-checkpoint.ps1'
$selectionScript = Join-Path $PSScriptRoot 'new-translation-wave-selection.ps1'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$workRoot = [IO.Path]::Combine($repoRoot, 'work')

function Write-Utf8 {
    param([string] $Path, [string] $Content)
    [IO.File]::WriteAllText($Path, $Content, $strictUtf8)
}

function Get-Sha256Hex {
    param([string] $Path)
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function New-EmptyJsonArray {
    return ,([Array]::CreateInstance([object], 0))
}

function New-TestFixture {
    param(
        [string] $Parent,
        [string] $Name,
        [ValidateRange(1, 3)][int] $JobCount = 1,
        [ValidateRange(1, 3)][int] $ResultCount = 1,
        [switch] $DuplicateJobs,
        [string] $ResultPromptVersion,
        [string] $ResultModel = 'gpt-5.6-terra',
        [string] $CheckpointModel,
        [ValidateSet('terra_done', 'validated', 'needs_sol')][string] $CheckpointState = 'validated',
        [switch] $Escalated,
        [switch] $ScalarIssueCodes,
        [switch] $DuplicateIssueCodes,
        [switch] $TerraSourceProblem,
        [switch] $ScalarRiskFlags,
        [switch] $StringNeedsReview,
        [string] $Confidence = 'high',
        [switch] $WrongReviewCheckpointHash
    )

    if ([string]::IsNullOrWhiteSpace($ResultPromptVersion)) {
        $ResultPromptVersion = if ($ResultModel -eq 'gpt-5.6-sol') { 'ru-review-v1' } else { 'ru-v2' }
    }
    if ([string]::IsNullOrWhiteSpace($CheckpointModel)) { $CheckpointModel = $ResultModel }

    $root = [IO.Path]::Combine($Parent, $Name)
    $chunks = [IO.Path]::Combine($root, 'chunks')
    $privateTemp = [IO.Path]::Combine($root, 'private-temp')
    [IO.Directory]::CreateDirectory($chunks) | Out-Null
    [IO.Directory]::CreateDirectory($privateTemp) | Out-Null

    $prompt = [IO.Path]::Combine($root, 'prompt.md')
    $reviewPrompt = [IO.Path]::Combine($root, 'review-prompt.md')
    $glossary = [IO.Path]::Combine($root, 'glossary.json')
    $base = [IO.Path]::Combine($root, 'base-overlay.jsonl')
    $english = [IO.Path]::Combine($root, 'english.private.loc1')
    Write-Utf8 $prompt "test prompt ru-v2`n"
    Write-Utf8 $reviewPrompt "test review prompt ru-review-v1`n"
    Write-Utf8 $glossary "{`"schema`":1}`n"
    Write-Utf8 $base "{`"id`":`"0000000000000001`",`"translation`":`"База`"}`n"
    Write-Utf8 $english "PRIVATE ENGLISH FIXTURE - NEVER A TRACKED OUTPUT`n"

    $jobLines = [Collections.Generic.List[string]]::new()
    for ($index = 1; $index -le $JobCount; $index++) {
        $jobId = if ($DuplicateJobs) { 'job-1' } else { "job-$index" }
        [object] $riskFlags = New-EmptyJsonArray
        if ($Escalated) { $riskFlags = [object[]]@('context_required') }
        if ($ScalarRiskFlags) { $riskFlags = 'context_required' }
        $jobValue = [ordered]@{ job_id = $jobId; english = "PRIVATE SOURCE $index"; risk_flags = $riskFlags }
        $jobLines.Add(($jobValue | ConvertTo-Json -Compress))
    }

    [object] $issueCodes = New-EmptyJsonArray
    if ($ScalarIssueCodes) { $issueCodes = 'ambiguous_context' }
    elseif ($DuplicateIssueCodes) { $issueCodes = [object[]]@('lore', 'lore') }
    elseif ($TerraSourceProblem) { $issueCodes = [object[]]@('source_problem') }

    $resultLines = [Collections.Generic.List[string]]::new()
    for ($index = 1; $index -le $ResultCount; $index++) {
        [object] $needsReview = $false
        if ($StringNeedsReview) { $needsReview = 'false' }
        $resultValue = [ordered]@{
            job_id = "job-$index"
            translation = "Перевод $index"
            model = $ResultModel
            prompt_version = $ResultPromptVersion
            confidence = $Confidence
            needs_review = $needsReview
            issue_codes = $issueCodes
        }
        $resultLines.Add(($resultValue | ConvertTo-Json -Compress))
    }

    $jobs = [IO.Path]::Combine($chunks, 'chunk-0001.jobs.jsonl')
    $result = [IO.Path]::Combine($chunks, 'chunk-0001.result.jsonl')
    $checkpoint = [IO.Path]::Combine($chunks, 'chunk-0001.checkpoint.json')
    Write-Utf8 $jobs ([string]::Join("`n", $jobLines) + "`n")
    Write-Utf8 $result ([string]::Join("`n", $resultLines) + "`n")

    $promptSha = Get-Sha256Hex $prompt
    $reviewPromptSha = Get-Sha256Hex $reviewPrompt
    $glossarySha = Get-Sha256Hex $glossary
    $useReviewPrompt = $ResultModel -eq 'gpt-5.6-sol' -or @($CheckpointModel -split ',') -contains 'gpt-5.6-sol'
    [object] $checkpointReviewSha = if ($useReviewPrompt) { $reviewPromptSha } else { $null }
    if ($WrongReviewCheckpointHash) { $checkpointReviewSha = '0' * 64 }
    [object] $checkpointEscalations = New-EmptyJsonArray
    if ($Escalated) { $checkpointEscalations = [object[]]@(1..$ResultCount | ForEach-Object { "job-$_" }) }
    $checkpointValue = [ordered]@{
        schema = 1
        chunk_id = 'chunk-0001'
        state = $CheckpointState
        jobs_file = 'chunk-0001.jobs.jsonl'
        jobs_sha256 = Get-Sha256Hex $jobs
        result_file = 'chunk-0001.result.jsonl'
        result_sha256 = if ($CheckpointState -eq 'terra_done') { $null } else { Get-Sha256Hex $result }
        prompt_sha256 = $promptSha
        review_prompt_sha256 = $checkpointReviewSha
        glossary_sha256 = $glossarySha
        model = $CheckpointModel
        validation_errors = if ($CheckpointState -eq 'terra_done') { $null } else { 0 }
        escalation_ids = $checkpointEscalations
    }
    Write-Utf8 $checkpoint (($checkpointValue | ConvertTo-Json -Depth 5) + "`n")

    $chunksManifest = [IO.Path]::Combine($chunks, 'chunks.manifest.json')
    $chunksManifestValue = [ordered]@{
        schema = 1
        workflow = 'codex-local-translation'
        source_jobs_sha256 = ('A' * 64)
        prompt_sha256 = $promptSha
        glossary_sha256 = $glossarySha
        chunk_size_limit = 100
        chunk_character_limit = 30000
        total_items = $JobCount
        chunks = @([ordered]@{
            chunk_id = 'chunk-0001'
            jobs_file = 'chunk-0001.jobs.jsonl'
            jobs_sha256 = Get-Sha256Hex $jobs
            item_count = $JobCount
            checkpoint_file = 'chunk-0001.checkpoint.json'
        })
    }
    Write-Utf8 $chunksManifest (($chunksManifestValue | ConvertTo-Json -Depth 8) + "`n")

    $selection = [IO.Path]::Combine($root, 'wave.selection.json')
    $selectionValue = [ordered]@{
        schema = 1
        workflow = 'codex-local-import-wave'
        chunks_manifest_sha256 = Get-Sha256Hex $chunksManifest
        base_overlay_sha256 = Get-Sha256Hex $base
        prompt_sha256 = $promptSha
        glossary_sha256 = $glossarySha
        prompt_version = 'ru-v2'
        review_prompt_sha256 = if ($useReviewPrompt) { $reviewPromptSha } else { $null }
        review_prompt_version = if ($useReviewPrompt) { 'ru-review-v1' } else { $null }
        complete_chunks = @([ordered]@{
            chunk_id = 'chunk-0001'
            checkpoint_sha256 = Get-Sha256Hex $checkpoint
            result_sha256 = Get-Sha256Hex $result
        })
    }
    Write-Utf8 $selection (($selectionValue | ConvertTo-Json -Depth 8) + "`n")

    return [pscustomobject]@{
        Root = $root
        Chunks = $chunks
        Manifest = $chunksManifest
        Selection = $selection
        English = $english
        Base = $base
        BaseSha = Get-Sha256Hex $base
        Prompt = $prompt
        ReviewPrompt = $reviewPrompt
        UseReviewPrompt = $useReviewPrompt
        Glossary = $glossary
        Jobs = $jobs
        Result = $result
        Checkpoint = $checkpoint
        PrivateTemp = $privateTemp
        Output = [IO.Path]::Combine($root, 'next-overlay.jsonl')
        Receipt = [IO.Path]::Combine($root, 'wave.receipt.json')
    }
}

function Invoke-DryRun {
    param(
        $Fixture,
        [string] $ExpectedBaseSha = $Fixture.BaseSha,
        [string] $PrivateTempRoot = $Fixture.PrivateTemp
    )

    $arguments = @{
        Chunks = $Fixture.Chunks
        SelectionManifest = $Fixture.Selection
        English = $Fixture.English
        BaseOverlay = $Fixture.Base
        ExpectedBaseOverlaySha256 = $ExpectedBaseSha
        PromptPath = $Fixture.Prompt
        GlossaryPath = $Fixture.Glossary
        Output = $Fixture.Output
        ReceiptPath = $Fixture.Receipt
        PrivateTempRoot = $PrivateTempRoot
        DryRun = $true
    }
    if ($Fixture.UseReviewPrompt) { $arguments.ReviewPromptPath = $Fixture.ReviewPrompt }
    & $importScript @arguments
}

function Assert-Throws {
    param([scriptblock] $Action, [string] $MessagePattern, [string] $Label)

    try { & $Action; throw "Expected rejection did not occur: $Label" }
    catch {
        if ($_.Exception.Message -like 'Expected rejection did not occur:*') { throw }
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw "$Label rejected for an unexpected reason: $($_.Exception.Message)"
        }
    }
}

[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$testParent = [IO.Path]::Combine($workRoot, "wave-import-selftest-$([Guid]::NewGuid().ToString('N'))")
[IO.Directory]::CreateDirectory($testParent) | Out-Null
$passed = 0
try {
    $valid = New-TestFixture -Parent $testParent -Name 'valid'
    Invoke-DryRun $valid
    $receipt = [IO.File]::ReadAllText($valid.Receipt, $strictUtf8)
    $receiptValue = $receipt | ConvertFrom-Json
    if ([string]$receiptValue.status -ne 'validated-dry-run' -or [int]$receiptValue.selected_job_count -ne 1) {
        throw 'Positive dry-run receipt has incorrect summary metadata.'
    }
    if ($receipt.IndexOf('PRIVATE SOURCE', [StringComparison]::Ordinal) -ge 0 -or
        $receipt.IndexOf('Перевод 1', [StringComparison]::Ordinal) -ge 0 -or
        $receipt.IndexOf($valid.Root, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw 'Source-free receipt leaked source/result text or an absolute fixture path.'
    }
    if (@([IO.Directory]::EnumerateFileSystemEntries($valid.PrivateTemp)).Count -ne 0) {
        throw 'Private aggregate directory was not cleaned after dry-run.'
    }
    $passed++

    $sol = New-TestFixture -Parent $testParent -Name 'sol' -ResultModel 'gpt-5.6-sol'
    Invoke-DryRun $sol
    $solReceipt = [IO.File]::ReadAllText($sol.Receipt, $strictUtf8) | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$solReceipt.review_prompt_sha256)) { throw 'Sol receipt did not retain its review prompt pin.' }
    $passed++

    $generatedTerraSelection = [IO.Path]::Combine($valid.Root, 'generated.selection.json')
    & $selectionScript -Chunks $valid.Chunks -CompleteChunkId 'chunk-0001' `
        -BaseOverlay $valid.Base -ExpectedBaseOverlaySha256 $valid.BaseSha `
        -PromptPath $valid.Prompt -GlossaryPath $valid.Glossary -PromptVersion 'ru-v2' `
        -Output $generatedTerraSelection
    $generatedTerra = [IO.File]::ReadAllText($generatedTerraSelection, $strictUtf8) | ConvertFrom-Json
    if ([string]$generatedTerra.complete_chunks[0].checkpoint_sha256 -ne (Get-Sha256Hex $valid.Checkpoint) -or
        -not [string]::IsNullOrWhiteSpace([string]$generatedTerra.review_prompt_sha256)) {
        throw 'Generated Terra selection has incorrect checkpoint/review prompt pins.'
    }
    $passed++

    $generatedSolSelection = [IO.Path]::Combine($sol.Root, 'generated.selection.json')
    & $selectionScript -Chunks $sol.Chunks -CompleteChunkId 'chunk-0001' `
        -BaseOverlay $sol.Base -ExpectedBaseOverlaySha256 $sol.BaseSha `
        -PromptPath $sol.Prompt -ReviewPromptPath $sol.ReviewPrompt -GlossaryPath $sol.Glossary `
        -PromptVersion 'ru-v2' -ReviewPromptVersion 'ru-review-v1' -Output $generatedSolSelection
    $generatedSol = [IO.File]::ReadAllText($generatedSolSelection, $strictUtf8) | ConvertFrom-Json
    if (-not [string]::Equals([string]$generatedSol.review_prompt_sha256, (Get-Sha256Hex $sol.ReviewPrompt), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Generated Sol selection did not retain its review prompt pin.'
    }
    $passed++

    Assert-Throws {
        & $selectionScript -Chunks $valid.Chunks -CompleteChunkId 'chunk-0001' `
            -BaseOverlay $valid.Base -ExpectedBaseOverlaySha256 $valid.BaseSha `
            -PromptPath $valid.Prompt -GlossaryPath $valid.Glossary -PromptVersion 'ru-v2' `
            -Output $generatedTerraSelection
    } 'already exists' 'selection no-overwrite'
    $passed++

    $needsSol = New-TestFixture -Parent $testParent -Name 'needs-sol' -CheckpointState 'needs_sol' -Escalated
    Invoke-DryRun $needsSol
    $needsSolReceipt = [IO.File]::ReadAllText($needsSol.Receipt, $strictUtf8) | ConvertFrom-Json
    if ([string]$needsSolReceipt.chunks[0].checkpoint_state -ne 'needs_sol' -or
        [int]$needsSolReceipt.chunks[0].escalation_count -ne 1) {
        throw 'needs_sol working-draft chunk was not represented correctly in the receipt.'
    }
    $passed++

    $terraDone = New-TestFixture -Parent $testParent -Name 'terra-done' -CheckpointState 'terra_done'
    Invoke-DryRun $terraDone
    $terraReceipt = [IO.File]::ReadAllText($terraDone.Receipt, $strictUtf8) | ConvertFrom-Json
    if ([string]$terraReceipt.chunks[0].checkpoint_state -ne 'terra_done') {
        throw 'Pinned terra_done chunk was not accepted as a working draft wave.'
    }
    $passed++

    $badBase = New-TestFixture -Parent $testParent -Name 'bad-base'
    Assert-Throws { Invoke-DryRun $badBase ('0' * 64) } 'SHA-256 pin' 'base overlay pin'
    $passed++

    $badResultHash = New-TestFixture -Parent $testParent -Name 'bad-result-hash'
    [IO.File]::AppendAllText($badResultHash.Result, " `n", $strictUtf8)
    Assert-Throws { Invoke-DryRun $badResultHash } 'result SHA-256' 'result hash pin'
    $passed++

    $badCheckpointHash = New-TestFixture -Parent $testParent -Name 'bad-checkpoint-hash'
    [IO.File]::AppendAllText($badCheckpointHash.Checkpoint, " `n", $strictUtf8)
    Assert-Throws { Invoke-DryRun $badCheckpointHash } 'checkpoint SHA-256' 'checkpoint hash pin'
    $passed++

    $incomplete = New-TestFixture -Parent $testParent -Name 'incomplete' -JobCount 2 -ResultCount 1
    Assert-Throws { Invoke-DryRun $incomplete } 'coverage' 'exact result coverage'
    $passed++

    $duplicates = New-TestFixture -Parent $testParent -Name 'duplicates' -JobCount 2 -ResultCount 1 -DuplicateJobs
    Assert-Throws { Invoke-DryRun $duplicates } 'duplicate job_id' 'unique job ids'
    $passed++

    $badModelPrompt = New-TestFixture -Parent $testParent -Name 'bad-model-prompt' -ResultModel 'gpt-5.6-sol' -ResultPromptVersion 'ru-v2'
    Assert-Throws { Invoke-DryRun $badModelPrompt } 'model-to-prompt mapping' 'model prompt mapping'
    $passed++

    $badCheckpointModel = New-TestFixture -Parent $testParent -Name 'bad-checkpoint-model' -CheckpointModel 'gpt-5.6-sol'
    Assert-Throws { Invoke-DryRun $badCheckpointModel } 'actual sorted model set' 'checkpoint model set'
    $passed++

    $scalarIssues = New-TestFixture -Parent $testParent -Name 'scalar-issues' -ScalarIssueCodes
    Assert-Throws { Invoke-DryRun $scalarIssues } 'exact JSON array' 'scalar issue_codes'
    $passed++

    $duplicateIssues = New-TestFixture -Parent $testParent -Name 'duplicate-issues' -DuplicateIssueCodes
    Assert-Throws { Invoke-DryRun $duplicateIssues } 'duplicate value' 'duplicate issue_codes'
    $passed++

    $terraSourceProblem = New-TestFixture -Parent $testParent -Name 'terra-source-problem' -TerraSourceProblem
    Assert-Throws { Invoke-DryRun $terraSourceProblem } 'unsupported value' 'Terra source_problem allowlist'
    $passed++

    $scalarRisk = New-TestFixture -Parent $testParent -Name 'scalar-risk' -ScalarRiskFlags
    Assert-Throws { Invoke-DryRun $scalarRisk } 'exact JSON array' 'scalar risk_flags'
    $passed++

    $stringNeedsReview = New-TestFixture -Parent $testParent -Name 'string-needs-review' -StringNeedsReview
    Assert-Throws { Invoke-DryRun $stringNeedsReview } 'invalid model/prompt/translation/confidence/needs_review JSON types' 'needs_review type'
    $passed++

    $badConfidence = New-TestFixture -Parent $testParent -Name 'bad-confidence' -Confidence 'certain'
    Assert-Throws { Invoke-DryRun $badConfidence } 'unsupported confidence' 'confidence allowlist'
    $passed++

    $wrongReviewPin = New-TestFixture -Parent $testParent -Name 'wrong-review-checkpoint-pin' -ResultModel 'gpt-5.6-sol' -WrongReviewCheckpointHash
    Assert-Throws { Invoke-DryRun $wrongReviewPin } 'Sol results are not chained' 'review prompt checkpoint pin'
    $passed++

    $changedPrompt = New-TestFixture -Parent $testParent -Name 'changed-prompt'
    [IO.File]::AppendAllText($changedPrompt.Prompt, "changed`n", $strictUtf8)
    Assert-Throws { Invoke-DryRun $changedPrompt } 'SHA-256 pin' 'prompt content pin'
    $passed++

    $changedReviewPrompt = New-TestFixture -Parent $testParent -Name 'changed-review-prompt' -ResultModel 'gpt-5.6-sol'
    [IO.File]::AppendAllText($changedReviewPrompt.ReviewPrompt, "changed`n", $strictUtf8)
    Assert-Throws { Invoke-DryRun $changedReviewPrompt } 'review prompt content/version' 'review prompt content pin'
    $passed++

    $badEscalationState = New-TestFixture -Parent $testParent -Name 'bad-escalation-state' -CheckpointState 'needs_sol'
    Assert-Throws { Invoke-DryRun $badEscalationState } 'state does not match' 'derived escalation state'
    $passed++

    $existingOutput = New-TestFixture -Parent $testParent -Name 'existing-output'
    Write-Utf8 $existingOutput.Output "do not overwrite`n"
    Assert-Throws { Invoke-DryRun $existingOutput } 'already exists' 'no-overwrite output'
    if ([IO.File]::ReadAllText($existingOutput.Output, $strictUtf8) -ne "do not overwrite`n") {
        throw 'Existing output was modified by a rejected import.'
    }
    $passed++

    $existingReceipt = New-TestFixture -Parent $testParent -Name 'existing-receipt'
    Write-Utf8 $existingReceipt.Receipt "do not overwrite`n"
    Assert-Throws { Invoke-DryRun $existingReceipt } 'already exists' 'no-overwrite receipt'
    if ([IO.File]::ReadAllText($existingReceipt.Receipt, $strictUtf8) -ne "do not overwrite`n") {
        throw 'Existing receipt was modified by a rejected import.'
    }
    $passed++

    $publicTemp = New-TestFixture -Parent $testParent -Name 'public-temp'
    Assert-Throws { Invoke-DryRun -Fixture $publicTemp -PrivateTempRoot $repoRoot } 'cannot use a public repository path' 'public private-temp root'
    $passed++

    $markTerra = New-TestFixture -Parent $testParent -Name 'mark-terra'
    & $markScript -CheckpointPath $markTerra.Checkpoint -PromptPath $markTerra.Prompt `
        -GlossaryPath $markTerra.Glossary -ValidationErrors 0
    $markedTerra = [IO.File]::ReadAllText($markTerra.Checkpoint, $strictUtf8) | ConvertFrom-Json
    if ([string]$markedTerra.model -ne 'gpt-5.6-terra' -or
        -not [string]::IsNullOrWhiteSpace([string]$markedTerra.review_prompt_sha256)) {
        throw 'Terra checkpoint commit did not retain exact model/review prompt metadata.'
    }
    $passed++

    $markSol = New-TestFixture -Parent $testParent -Name 'mark-sol' -ResultModel 'gpt-5.6-sol'
    & $markScript -CheckpointPath $markSol.Checkpoint -PromptPath $markSol.Prompt `
        -ReviewPromptPath $markSol.ReviewPrompt -GlossaryPath $markSol.Glossary -ValidationErrors 0
    $markedSol = [IO.File]::ReadAllText($markSol.Checkpoint, $strictUtf8) | ConvertFrom-Json
    if ([string]$markedSol.model -ne 'gpt-5.6-sol' -or
        -not [string]::Equals([string]$markedSol.review_prompt_sha256, (Get-Sha256Hex $markSol.ReviewPrompt), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Sol checkpoint commit did not retain exact model/review prompt metadata.'
    }
    $passed++
}
finally {
    $resolvedWorkRoot = [IO.Path]::GetFullPath($workRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $resolvedTestParent = [IO.Path]::GetFullPath($testParent)
    if (-not $resolvedTestParent.StartsWith($resolvedWorkRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolvedTestParent).StartsWith('wave-import-selftest-', [StringComparison]::Ordinal)) {
        throw "Refusing to clean unexpected self-test directory: $resolvedTestParent"
    }
    if ([IO.Directory]::Exists($resolvedTestParent)) { [IO.Directory]::Delete($resolvedTestParent, $true) }
}

Write-Host "Translation wave import self-tests: $passed/29 passed."
