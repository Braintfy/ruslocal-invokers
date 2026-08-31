[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Installer,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$ReleaseTag,
    [Parameter(Mandatory=$true)][long]$Sequence,
    [Parameter(Mandatory=$true)][string]$PrivateKey,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$NotesFile
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
& (Join-Path $PSScriptRoot 'Invoke-UpdateReleaseTool.ps1') -ToolArguments @(
    'build-patcher-update', '--repository-root', $repository,
    '--installer', [IO.Path]::GetFullPath($Installer), '--version', $Version,
    '--release-tag', $ReleaseTag, '--sequence', $Sequence.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--private-key', [IO.Path]::GetFullPath($PrivateKey), '--output-directory', [IO.Path]::GetFullPath($OutputDirectory),
    '--notes-file', [IO.Path]::GetFullPath($NotesFile)
)
