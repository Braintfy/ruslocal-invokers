[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $FixtureDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Get-Command dotnet -ErrorAction Stop
$resolvedFixtures = (Resolve-Path -LiteralPath $FixtureDirectory).Path

Push-Location $projectRoot
try {
    & $dotnet.Source run --project 'src\InvokersRu.SmokeTests\InvokersRu.SmokeTests.csproj' --configuration Release --no-build -- $resolvedFixtures
    if ($LASTEXITCODE -ne 0) { throw "smoke tests failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}
