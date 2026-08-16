[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $InputJobs,
    [Parameter(Mandatory = $true)][string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
. (Join-Path $PSScriptRoot 'path-safety.ps1')

$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

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

function Write-NewDurableFile {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][byte[]] $Bytes
    )

    $null = Assert-PrivateWorkflowPath -Path $Path -RepoRoot $repoRoot -Label 'Partition staged file'
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $stream.Write($Bytes, 0, $Bytes.Length)
        $stream.Flush($true)
    }
    finally { $stream.Dispose() }
}

function New-PrivateStageDirectory {
    param(
        [Parameter(Mandatory = $true)][string] $Parent,
        [Parameter(Mandatory = $true)][string] $OutputLeaf
    )

    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        $candidate = [IO.Path]::Combine($Parent, ".$OutputLeaf.$([Guid]::NewGuid().ToString('N')).stage")
        $null = Assert-PrivateWorkflowPath -Path $candidate -RepoRoot $repoRoot -Label 'Partition staging directory'
        if ([IO.File]::Exists($candidate) -or [IO.Directory]::Exists($candidate)) { continue }
        $null = [IO.Directory]::CreateDirectory($candidate)
        $null = Assert-PrivateWorkflowPath -Path $candidate -RepoRoot $repoRoot -Label 'Partition staging directory'
        return $candidate
    }
    throw 'Could not reserve a unique partition staging directory.'
}

$inputPath = Assert-PrivateWorkflowPath -Path $InputJobs -RepoRoot $repoRoot -Label 'InputJobs'
if (-not [IO.File]::Exists($inputPath)) { throw "InputJobs must be an existing regular file: $inputPath" }
$outputRoot = Assert-PrivateWorkflowPath -Path $OutputDirectory -RepoRoot $repoRoot -Label 'OutputDirectory'
$outputRoot = Assert-SafeNewOutputPath -Path $outputRoot -Label 'OutputDirectory'

$outputParent = [IO.Path]::GetDirectoryName($outputRoot)
$outputLeaf = [IO.Path]::GetFileName($outputRoot)
if ([string]::IsNullOrWhiteSpace($outputParent) -or [string]::IsNullOrWhiteSpace($outputLeaf)) {
    throw 'OutputDirectory must name a new directory below work/.'
}
$null = Assert-PrivateWorkflowPath -Path $outputParent -RepoRoot $repoRoot -Label 'OutputDirectory parent'

$inputSnapshot = Read-FileSnapshot -Path $inputPath -Label 'InputJobs'
$lanes = [ordered]@{
    'lane-01-short-plain' = [Collections.Generic.List[object]]::new()
    'lane-02-short-structured' = [Collections.Generic.List[object]]::new()
    'lane-03-standard' = [Collections.Generic.List[object]]::new()
    'lane-04-long' = [Collections.Generic.List[object]]::new()
}

$seenJobs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$reader = [IO.StringReader]::new($inputSnapshot.Text)
try {
    $lineNumber = 0
    while ($null -ne ($line = $reader.ReadLine())) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }

        try { $job = $line | ConvertFrom-Json }
        catch { throw "Invalid jobs JSONL at line ${lineNumber}: $($_.Exception.Message)" }

        $recordLabel = "InputJobs line $lineNumber"
        $jobId = (Get-RequiredProperty -Object $job -Name 'job_id' -Label $recordLabel).Value
        $english = (Get-RequiredProperty -Object $job -Name 'english' -Label $recordLabel).Value
        $ids = (Get-RequiredProperty -Object $job -Name 'ids' -Label $recordLabel).Value
        $riskFlags = (Get-RequiredProperty -Object $job -Name 'risk_flags' -Label $recordLabel).Value
        if ($jobId -isnot [string] -or [string]::IsNullOrWhiteSpace($jobId) -or -not $seenJobs.Add($jobId)) {
            throw "Missing, non-string, or duplicate job_id at line $lineNumber."
        }
        if ($english -isnot [string]) { throw "Job $jobId must contain english as a JSON string." }
        if ($ids -isnot [array] -or $ids.Count -eq 0) { throw "Job $jobId must contain a non-empty ids JSON array." }
        foreach ($id in $ids) {
            if ($id -isnot [string] -or [string]::IsNullOrWhiteSpace($id)) {
                throw "Job $jobId ids must contain only non-empty strings."
            }
        }
        if ($riskFlags -isnot [array]) { throw "Job $jobId must contain risk_flags as a JSON array." }
        foreach ($riskFlag in $riskFlags) {
            if ($riskFlag -isnot [string] -or [string]::IsNullOrWhiteSpace($riskFlag)) {
                throw "Job $jobId risk_flags must contain only non-empty strings."
            }
        }

        $length = $english.Length
        $lane = if ($length -le 120 -and $riskFlags.Count -eq 0) {
            'lane-01-short-plain'
        }
        elseif ($length -le 120) {
            'lane-02-short-structured'
        }
        elseif ($length -le 500) {
            'lane-03-standard'
        }
        else {
            'lane-04-long'
        }

        $lanes[$lane].Add([pscustomobject]@{
            Line = $line
            JobId = $jobId
            IdCount = $ids.Count
            Characters = $length
        })
    }
}
finally { $reader.Dispose() }

$stageRoot = $null
$published = $false
try {
    $null = [IO.Directory]::CreateDirectory($outputParent)
    $null = Assert-PrivateWorkflowPath -Path $outputParent -RepoRoot $repoRoot -Label 'OutputDirectory parent'
    $null = Assert-PrivateWorkflowPath -Path $outputRoot -RepoRoot $repoRoot -Label 'OutputDirectory'
    if ([IO.File]::Exists($outputRoot) -or [IO.Directory]::Exists($outputRoot)) {
        throw "OutputDirectory already exists: $outputRoot"
    }
    $stageRoot = New-PrivateStageDirectory -Parent $outputParent -OutputLeaf $outputLeaf
    $manifestLanes = [Collections.Generic.List[object]]::new()
    foreach ($pair in $lanes.GetEnumerator()) {
        $ordered = @($pair.Value | Sort-Object `
            @{ Expression = 'IdCount'; Descending = $true }, `
            @{ Expression = 'Characters'; Descending = $false }, `
            @{ Expression = 'JobId'; Descending = $false })
        $fileName = "$($pair.Key).jobs.jsonl"
        $path = [IO.Path]::Combine($stageRoot, $fileName)
        $content = if ($ordered.Count -eq 0) { '' } else { [string]::Join("`n", @($ordered | ForEach-Object { $_.Line })) + "`n" }
        $bytes = $strictUtf8.GetBytes($content)
        Write-NewDurableFile -Path $path -Bytes $bytes

        $manifestLanes.Add([ordered]@{
            lane = $pair.Key
            jobs_file = $fileName
            jobs_sha256 = Get-Sha256HexFromBytes -Bytes $bytes
            job_count = $ordered.Count
            source_id_count = [long](($ordered | Measure-Object -Property IdCount -Sum).Sum)
            source_characters = [long](($ordered | Measure-Object -Property Characters -Sum).Sum)
        })
    }

    $manifest = ([ordered]@{
        schema = 1
        workflow = 'codex-local-full-translation'
        source_jobs_sha256 = $inputSnapshot.Sha256
        total_jobs = $seenJobs.Count
        lanes = @($manifestLanes)
    } | ConvertTo-Json -Depth 5 -Compress) -replace "`r`n", "`n"
    $manifestBytes = $strictUtf8.GetBytes($manifest + "`n")
    Write-NewDurableFile -Path ([IO.Path]::Combine($stageRoot, 'lanes.manifest.json')) -Bytes $manifestBytes

    if ([IO.File]::Exists($outputRoot) -or [IO.Directory]::Exists($outputRoot)) {
        throw "OutputDirectory was created concurrently: $outputRoot"
    }
    $null = Assert-PrivateWorkflowPath -Path $stageRoot -RepoRoot $repoRoot -Label 'Partition staging directory'
    $null = Assert-PrivateWorkflowPath -Path $outputRoot -RepoRoot $repoRoot -Label 'OutputDirectory'
    [IO.Directory]::Move($stageRoot, $outputRoot)
    $published = $true
}
finally {
    if (-not $published -and $null -ne $stageRoot -and [IO.Directory]::Exists($stageRoot)) {
        $null = Assert-PrivateWorkflowPath -Path $stageRoot -RepoRoot $repoRoot -Label 'Partition staging cleanup'
        [IO.Directory]::Delete($stageRoot, $true)
    }
}

Write-Host "Partitioned $($seenJobs.Count) jobs into $($lanes.Count) deterministic lanes."
foreach ($lane in $manifestLanes) {
    Write-Host "$($lane.lane): $($lane.job_count) jobs / $($lane.source_id_count) ids / $($lane.source_characters) source chars"
}
