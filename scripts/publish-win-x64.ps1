[CmdletBinding()]
param(
    [string] $OutputDirectory = 'work\publish\win-x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Get-Command dotnet -ErrorAction Stop

Push-Location $projectRoot
try {
    & $dotnet.Source restore 'src\InvokersRu.Cli\InvokersRu.Cli.csproj' --runtime 'win-x64' --configfile 'NuGet.Config' --source 'https://api.nuget.org/v3/index.json'
    if ($LASTEXITCODE -ne 0) { throw "runtime-pack restore failed with exit code $LASTEXITCODE" }
    & $dotnet.Source publish 'src\InvokersRu.Cli\InvokersRu.Cli.csproj' --configuration Release --runtime 'win-x64' --self-contained true --no-restore '-p:PublishSingleFile=true' '-p:PublishTrimmed=false' '-p:DebugType=None' '-p:DebugSymbols=false' --output $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}
