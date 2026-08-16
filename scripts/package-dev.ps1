[CmdletBinding()]
param(
    [string] $Version = '0.1.2-dev'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$stageRoot = Join-Path $projectRoot "work\package\InvokersRu-MVP-$Version"
$releaseStage = Join-Path $stageRoot 'release'
$sourceStage = Join-Path $stageRoot 'source'
$releaseZip = Join-Path $projectRoot "outputs\InvokersRu-MVP-$Version-win-x64.zip"
$sourceZip = Join-Path $projectRoot "outputs\InvokersRu-source-$Version.zip"
$publishedExe = Join-Path $projectRoot "work\publish\$Version-win-x64\InvokersRu.Cli.exe"

if (Test-Path -LiteralPath $stageRoot) { throw "Staging path already exists: $stageRoot" }
if (Test-Path -LiteralPath $releaseZip) { throw "Output already exists: $releaseZip" }
if (Test-Path -LiteralPath $sourceZip) { throw "Output already exists: $sourceZip" }
if (-not (Test-Path -LiteralPath $publishedExe)) { throw "Publish the win-x64 executable first: $publishedExe" }

function Copy-RelativeFile {
    param(
        [Parameter(Mandatory = $true)][string] $RelativePath,
        [Parameter(Mandatory = $true)][string] $DestinationRoot
    )

    $source = Join-Path $projectRoot $RelativePath
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing package source: $RelativePath" }
    $destination = Join-Path $DestinationRoot $RelativePath
    $parent = Split-Path -Parent $destination
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    Copy-Item -LiteralPath $source -Destination $destination
}

New-Item -ItemType Directory -Force -Path $releaseStage, $sourceStage | Out-Null
Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $releaseStage 'InvokersRu.Cli.exe')

$releaseFiles = @(
    'README.md',
    'CHANGELOG.md',
    'LICENSE',
    'config\compatibility.v1.json',
    'config\codex-translation-policy.v1.json',
    'translations\ru_RU.jsonl',
    'localization\glossary.ru.json',
    'docs\architecture.md',
    'docs\translation-workflow.md',
    'prompts\translation-system.ru-v1.md',
    'prompts\translation-review.ru-v1.md'
)
foreach ($relative in $releaseFiles) { Copy-RelativeFile -RelativePath $relative -DestinationRoot $releaseStage }

$checksums = Get-ChildItem -LiteralPath $releaseStage -Recurse -File |
    Sort-Object FullName |
    ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($releaseStage, $_.FullName).Replace('\', '/')
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
        "$hash  $relative"
    }
[IO.File]::WriteAllLines((Join-Path $releaseStage 'SHA256SUMS.txt'), [string[]]$checksums, [Text.UTF8Encoding]::new($false))

$topLevelFiles = @(
    '.gitignore',
    'CHANGELOG.md',
    'Directory.Build.props',
    'InvokersRu.sln',
    'LICENSE',
    'NuGet.Config',
    'README.md'
)
foreach ($relative in $topLevelFiles) { Copy-RelativeFile -RelativePath $relative -DestinationRoot $sourceStage }

$sourceDirectories = @('config', 'docs', 'localization', 'prompts', 'scripts', 'translations', 'src')
foreach ($directory in $sourceDirectories) {
    $absolute = Join-Path $projectRoot $directory
    Get-ChildItem -LiteralPath $absolute -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($projectRoot, $_.FullName)
            Copy-RelativeFile -RelativePath $relative -DestinationRoot $sourceStage
        }
}

Compress-Archive -Path (Join-Path $releaseStage '*') -DestinationPath $releaseZip -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $sourceStage '*') -DestinationPath $sourceZip -CompressionLevel Optimal

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($archivePath in @($releaseZip, $sourceZip)) {
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $forbidden = $archive.Entries | Where-Object {
            $_.FullName -match '(^|/)(work|bin|obj)/' -or
            $_.FullName -match '(en_US|uk_UA)\.bin(\.br)?$' -or
            $_.FullName -match 'manifest\.dat$' -or
            $_.FullName -match 'private\.jsonl$'
        }
        if ($forbidden) {
            throw "Forbidden private/build content entered $archivePath`: $($forbidden.FullName -join ', ')"
        }
    }
    finally {
        $archive.Dispose()
    }
}

Get-FileHash -Algorithm SHA256 -LiteralPath $releaseZip, $sourceZip | Format-Table -AutoSize
