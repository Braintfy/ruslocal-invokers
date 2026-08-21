[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runner = Join-Path $PSScriptRoot 'Invoke-UpdateReleaseTool.ps1'
$arguments = @(
    'keygen',
    '--repository-root', $repositoryRoot,
    '--output-directory', [IO.Path]::GetFullPath($OutputDirectory)
)

& $runner -ToolArguments $arguments
