param(
    [Parameter(Mandatory = $true)][string]$InputJobs,
    [Parameter(Mandatory = $true)][string]$OutputJobs,
    [ValidateRange(100, 2000)][int]$Total = 500
)

$ErrorActionPreference = 'Stop'
$strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
$inputPath = [System.IO.Path]::GetFullPath($InputJobs)
$outputPath = [System.IO.Path]::GetFullPath($OutputJobs)
if (-not [System.IO.File]::Exists($inputPath)) { throw "Jobs file not found: $inputPath" }
if ([string]::Equals($inputPath, $outputPath, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'InputJobs and OutputJobs must differ.' }

$items = [System.Collections.Generic.List[object]]::new()
foreach ($line in [System.IO.File]::ReadAllLines($inputPath, $strictUtf8)) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }
    $job = $line | ConvertFrom-Json
    $items.Add([pscustomobject]@{ Line = $line; Job = $job })
}
if ($items.Count -lt $Total) { throw "Only $($items.Count) work items are available; requested $Total." }

$ordered = $items | Sort-Object { $_.Job.job_id }
$selected = [System.Collections.Generic.List[object]]::new()
$seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

function Add-Stratum([string]$Risk, [int]$Limit) {
    $count = 0
    foreach ($item in $ordered) {
        if ($count -ge $Limit -or $selected.Count -ge $Total) { break }
        if ($item.Job.risk_flags -contains $Risk -and $seen.Add([string]$item.Job.job_id)) {
            $selected.Add($item)
            $count++
        }
    }
}

$quarter = [Math]::Floor($Total / 5)
Add-Stratum 'protected_tokens' $quarter
Add-Stratum 'numeric' $quarter
Add-Stratum 'context_required' $quarter
Add-Stratum 'long_text' ([Math]::Floor($quarter / 2))

foreach ($item in $ordered) {
    if ($selected.Count -ge $Total) { break }
    if ($seen.Add([string]$item.Job.job_id)) { $selected.Add($item) }
}

$directory = [System.IO.Path]::GetDirectoryName($outputPath)
[System.IO.Directory]::CreateDirectory($directory) | Out-Null
$content = [string]::Join("`n", ($selected | ForEach-Object { $_.Line })) + "`n"
if ([System.IO.File]::Exists($outputPath)) {
    $existing = [System.IO.File]::ReadAllText($outputPath, $strictUtf8)
    if (-not [string]::Equals($existing, $content, [System.StringComparison]::Ordinal)) {
        throw "Existing pilot differs; choose a new OutputJobs path: $outputPath"
    }
}
else {
    [System.IO.File]::WriteAllText($outputPath, $content, $strictUtf8)
}

Write-Host "Created or verified a deterministic stratified pilot with $($selected.Count) work items."
Write-Host "Pilot: $outputPath"
