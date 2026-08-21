[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$RuntimeProfile,
    [Parameter(Mandatory = $true)] [string]$EnglishLoc1,
    [Parameter(Mandatory = $true)] [string]$BaseLoc1,
    [Parameter(Mandatory = $true)] [string]$Stamp,
    [Parameter(Mandatory = $true)] [string]$Catalog,
    [Parameter(Mandatory = $true)] [string]$BuiltLoc1,
    [Parameter(Mandatory = $true)] [string]$BuildReport,
    [Parameter(Mandatory = $true)] [string]$Output
)

$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'Invoke-UpdateReleaseTool.ps1'
$arguments = @(
    'build-compatibility',
    '--runtime-profile', [IO.Path]::GetFullPath($RuntimeProfile),
    '--english-loc1', [IO.Path]::GetFullPath($EnglishLoc1),
    '--base-loc1', [IO.Path]::GetFullPath($BaseLoc1),
    '--stamp', [IO.Path]::GetFullPath($Stamp),
    '--catalog', [IO.Path]::GetFullPath($Catalog),
    '--built-loc1', [IO.Path]::GetFullPath($BuiltLoc1),
    '--build-report', [IO.Path]::GetFullPath($BuildReport),
    '--output', [IO.Path]::GetFullPath($Output)
)

& $runner -ToolArguments $arguments
