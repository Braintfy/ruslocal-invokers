[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$ToolArguments
)

$ErrorActionPreference = 'Stop'
$scriptDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptDirectory '..\..'))
$project = Join-Path $repositoryRoot 'tools\InvokersRu.UpdateReleaseTool\InvokersRu.UpdateReleaseTool.csproj'
if (-not [IO.File]::Exists($project)) {
    throw "Update release tool project was not found: $project"
}

$repoLocalDotnet = Join-Path $repositoryRoot 'work\dotnet-10\dotnet.exe'
$dotnet = $null
if (-not [string]::IsNullOrWhiteSpace($env:INVOKERSRU_DOTNET) -and [IO.File]::Exists($env:INVOKERSRU_DOTNET)) {
    $dotnet = [IO.Path]::GetFullPath($env:INVOKERSRU_DOTNET)
} elseif ([IO.File]::Exists($repoLocalDotnet)) {
    $dotnet = $repoLocalDotnet
} else {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $command) { $dotnet = $command.Source }
}

if ([string]::IsNullOrWhiteSpace($dotnet)) {
    throw '.NET SDK 10.0.302 or a compatible 10.0.x SDK is required.'
}

& $dotnet 'run' '--project' $project '--configuration' 'Release' '--no-launch-profile' '--' @ToolArguments
if ($LASTEXITCODE -ne 0) {
    throw "Update release tool failed with exit code $LASTEXITCODE."
}
