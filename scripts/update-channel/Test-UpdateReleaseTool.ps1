[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runner = Join-Path $PSScriptRoot 'Invoke-UpdateReleaseTool.ps1'
& $runner -ToolArguments @('self-test', '--repository-root', $repositoryRoot)
