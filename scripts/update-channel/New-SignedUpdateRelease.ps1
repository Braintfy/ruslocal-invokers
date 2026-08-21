[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Catalog,
    [Parameter(Mandatory = $true)] [string]$Compatibility,
    [Parameter(Mandatory = $true)] [string]$PrivateKey,
    [Parameter(Mandatory = $true)] [string]$SigningState,
    [Parameter(Mandatory = $true)] [string]$OutputDirectory,
    [Parameter(Mandatory = $true)] [string]$ReleaseId,
    [Parameter(Mandatory = $true)] [string]$ArtifactId,
    [Parameter(Mandatory = $true)] [UInt64]$Sequence,
    [Parameter(Mandatory = $true)] [UInt64]$ExpectedPreviousSequence,
    [Parameter(Mandatory = $true)] [string]$IssuedUtc,
    [Parameter(Mandatory = $true)] [string]$ExpiresUtc,
    [Parameter(Mandatory = $true)] [string]$MinimumPatcherVersion,
    [Parameter(Mandatory = $true)] [string]$LatestPatcherVersion,
    [ValidateSet('release-approved-v1', 'validated-preview-v1')]
    [string]$TranslationPolicy = 'release-approved-v1',
    [string]$Notes = '',
    [string]$NotesFile,
    [string]$RevokedReleaseIds
)

$ErrorActionPreference = 'Stop'
if (-not [string]::IsNullOrEmpty($Notes) -and -not [string]::IsNullOrWhiteSpace($NotesFile)) {
    throw 'Specify only Notes or NotesFile.'
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runner = Join-Path $PSScriptRoot 'Invoke-UpdateReleaseTool.ps1'
$invariant = [Globalization.CultureInfo]::InvariantCulture
$arguments = @(
    'build-release',
    '--repository-root', $repositoryRoot,
    '--catalog', [IO.Path]::GetFullPath($Catalog),
    '--compatibility', [IO.Path]::GetFullPath($Compatibility),
    '--private-key', [IO.Path]::GetFullPath($PrivateKey),
    '--signing-state', [IO.Path]::GetFullPath($SigningState),
    '--output-directory', [IO.Path]::GetFullPath($OutputDirectory),
    '--release-id', $ReleaseId,
    '--artifact-id', $ArtifactId,
    '--sequence', $Sequence.ToString($invariant),
    '--expected-previous-sequence', $ExpectedPreviousSequence.ToString($invariant),
    '--issued-utc', $IssuedUtc,
    '--expires-utc', $ExpiresUtc,
    '--minimum-patcher-version', $MinimumPatcherVersion,
    '--latest-patcher-version', $LatestPatcherVersion,
    '--translation-policy', $TranslationPolicy
)

if (-not [string]::IsNullOrWhiteSpace($NotesFile)) {
    $arguments += @('--notes-file', [IO.Path]::GetFullPath($NotesFile))
} else {
    $arguments += @('--notes', $Notes)
}

if (-not [string]::IsNullOrWhiteSpace($RevokedReleaseIds)) {
    $arguments += @('--revoked-release-ids', [IO.Path]::GetFullPath($RevokedReleaseIds))
}

& $runner -ToolArguments $arguments
