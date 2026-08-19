<#
.SYNOPSIS
    Snapshots the local runtime localization cache into a compatibility profile for a new game build.

.DESCRIPTION
    Reads the exact dl_en_US.bin / dl_uk_UA.bin / dl_uk_UA.bin.ver tuple the game keeps under
    %USERPROFILE%\AppData\LocalLow\Hit_Zone\Invokers\i18n and writes a schema-1 runtime-cache profile
    with the real hashes, content versions and revisions of that build.

    The generated profile is deliberately readiness=blocked and certified=false. Certification, which is
    what a supervised write build embeds, additionally requires the translation catalog hash and the exact
    built output hash, and stays a separate reviewed step.

    Select Ukrainian in the game at least once before running this: dl_uk_UA.bin is a downloaded runtime
    cache and does not exist until the client has fetched it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputProfile,

    [string] $CacheRoot,

    [string] $Id
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'path-safety.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $repoRoot 'src\InvokersRu.Cli\bin\Release\net10.0\InvokersRu.Cli.dll'
if (-not [IO.File]::Exists($cli)) {
    throw "Build the CLI first: dotnet build src/InvokersRu.Cli/InvokersRu.Cli.csproj -c Release"
}

$outputPath = Assert-SafeNewOutputPath -Path $OutputProfile -Label 'Runtime-cache profile output'

$arguments = @('cache-profile', '--output', $outputPath)
if ($PSBoundParameters.ContainsKey('CacheRoot') -and -not [string]::IsNullOrWhiteSpace($CacheRoot)) {
    $arguments += @('--cache-root', ([IO.Path]::GetFullPath($CacheRoot)))
}
if ($PSBoundParameters.ContainsKey('Id') -and -not [string]::IsNullOrWhiteSpace($Id)) {
    $arguments += @('--id', $Id)
}

& dotnet $cli @arguments
if ($LASTEXITCODE -ne 0) { throw "cache-profile failed with exit code $LASTEXITCODE." }

Write-Host ''
Write-Host "Profile written: $outputPath"
Write-Host 'Next: commit it under config/, then diff the corpus and re-run jobs for changed ids.'
