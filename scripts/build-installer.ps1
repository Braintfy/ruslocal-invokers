[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$AppVersion,

    [string]$OutputDirectory = 'work\installer-output',

    [string]$InstallerBaseName,

    [string]$IsccPath,

    [string]$InnoSignToolName,

    [string]$ExpectedSignerThumbprint,

    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'work'))
. (Join-Path $PSScriptRoot 'path-safety.ps1')
. (Join-Path $PSScriptRoot 'windows-payload-policy.ps1')

function Get-AbsolutePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [IO.Path]::GetFullPath(
        $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
}

function Get-RelativePayloadPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootPrefix = (Get-AbsolutePath -Path $Root).TrimEnd('\') + '\'
    $fullPath = Get-AbsolutePath -Path $Path
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Payload entry escaped its root: $fullPath"
    }
    return $fullPath.Substring($rootPrefix.Length).Replace('\', '/')
}

function Assert-NoReparseTree {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Assert-NoReparsePath -Path $Root -Label $Label
    foreach ($item in @(Get-ChildItem -LiteralPath $Root -Force -Recurse)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label contains a reparse point: $($item.FullName)"
        }
    }
}

function Assert-ExactJsonProperties {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string[]]$Properties,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $actual = @($Object.PSObject.Properties.Name)
    $difference = @(Compare-Object -ReferenceObject $Properties -DifferenceObject $actual -CaseSensitive)
    if ($difference.Count -ne 0) {
        throw "$Label has missing or unknown properties."
    }
}

function Read-AndVerifyPayload {
    param([Parameter(Mandatory = $true)][string]$Directory)

    Assert-NoReparseTree -Root $Directory -Label 'Installer payload'
    $manifestPath = Join-Path $Directory 'PAYLOAD-SHA256.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "PAYLOAD-SHA256.json is missing: $manifestPath"
    }
    $manifestItem = Get-Item -LiteralPath $manifestPath -Force
    if ($manifestItem.Length -le 0 -or $manifestItem.Length -gt 4194304) {
        throw 'PAYLOAD-SHA256.json must be a non-empty file no larger than 4 MiB.'
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-ExactJsonProperties -Object $manifest -Properties @(
        'schema_version', 'kind', 'app_version', 'runtime_identifier',
        'self_contained', 'publish_single_file', 'file_count', 'total_bytes', 'files'
    ) -Label 'Payload manifest'
    if (-not (Test-JsonIntegerValue $manifest.schema_version) -or [int64]$manifest.schema_version -ne 2 -or
        -not ($manifest.kind -is [string]) -or $manifest.kind -cne 'invokers-ru-windows-player-payload' -or
        -not ($manifest.app_version -is [string]) -or
        -not ($manifest.runtime_identifier -is [string]) -or $manifest.runtime_identifier -cne 'win-x64' -or
        -not ($manifest.self_contained -is [bool]) -or $manifest.self_contained -ne $true -or
        -not ($manifest.publish_single_file -is [bool]) -or $manifest.publish_single_file -ne $false -or
        -not (Test-JsonIntegerValue $manifest.file_count) -or
        -not (Test-JsonIntegerValue $manifest.total_bytes) -or
        -not ($manifest.files -is [Array])) {
        throw 'Payload manifest does not describe a supported win-x64 self-contained multi-file build.'
    }
    if (-not [string]::Equals($manifest.app_version, $script:AppVersion, [StringComparison]::Ordinal)) {
        throw "Payload app_version '$($manifest.app_version)' does not match '$script:AppVersion'."
    }

    $records = @($manifest.files)
    if ($records.Count -lt 12 -or $records.Count -gt 512 -or [int]$manifest.file_count -ne $records.Count) {
        throw 'Payload manifest has an invalid or inconsistent file count.'
    }
    $recordsByPath = New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    $expectedTotal = [long]0
    foreach ($record in $records) {
        Assert-ExactJsonProperties -Object $record -Properties @('path', 'length', 'sha256') -Label 'Payload file record'
        if (-not ($record.path -is [string]) -or -not (Test-JsonIntegerValue $record.length) -or
            -not ($record.sha256 -is [string])) {
            throw 'Payload file record types do not match schema 2.'
        }
        $relative = $record.path
        Assert-WindowsPayloadRelativePath -RelativePath $relative
        if ($recordsByPath.ContainsKey($relative)) {
            throw "Payload manifest contains a case-insensitive duplicate path: $relative"
        }
        $length = [long]$record.length
        $hash = $record.sha256
        if ($length -le 0 -or $length -gt 268435456 -or $hash -cnotmatch '^[A-F0-9]{64}$') {
            throw "Payload manifest contains invalid metadata for '$relative'."
        }
        $expectedTotal += $length
        if ($expectedTotal -gt 1073741824) { throw 'Payload exceeds the 1 GiB build limit.' }
        $recordsByPath.Add($relative, $record)
    }
    if ([long]$manifest.total_bytes -ne $expectedTotal) {
        throw 'Payload manifest total_bytes does not match its file records.'
    }

    foreach ($required in @(Get-WindowsPayloadRequiredPaths)) {
        if (-not $recordsByPath.ContainsKey($required)) {
            throw "Payload manifest is missing required file: $required"
        }
    }

    $actualFiles = @(Get-ChildItem -LiteralPath $Directory -Force -File -Recurse | Where-Object {
        -not [string]::Equals($_.FullName, $manifestPath, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($actualFiles.Count -ne $records.Count) {
        throw "Payload contains $($actualFiles.Count) files but its manifest lists $($records.Count)."
    }
    foreach ($file in $actualFiles) {
        $relative = Get-RelativePayloadPath -Root $Directory -Path $file.FullName
        if (-not $recordsByPath.ContainsKey($relative)) {
            throw "Payload contains an unlisted file: $relative"
        }
        $record = $recordsByPath[$relative]
        if ([long]$file.Length -ne [long]$record.length) {
            throw "Length mismatch for '$relative'."
        }
        $actualHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, [string]$record.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "SHA-256 mismatch for '$relative'."
        }
    }
    return $manifest
}

function Assert-ExpectedAuthenticodeSigner {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedThumbprint,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label is missing: $Path"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ([string]$signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
        throw "$Label does not have a valid Authenticode signature: $($signature.Status)"
    }
    $actualThumbprint = [string]$signature.SignerCertificate.Thumbprint
    if (-not [string]::Equals($actualThumbprint, $ExpectedThumbprint, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label signer thumbprint '$actualThumbprint' does not match the expected release signer."
    }
    return [ordered]@{
        path = $Path
        status = [string]$signature.Status
        signer_subject = [string]$signature.SignerCertificate.Subject
        signer_thumbprint = $actualThumbprint.ToUpperInvariant()
    }
}

function Assert-SignedPayloadReceipt {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$ExpectedThumbprint
    )

    $receiptPath = Join-Path $Directory 'BUILD-RECEIPT.json'
    $receiptItem = Get-Item -LiteralPath $receiptPath -Force
    if ($receiptItem.Length -le 0 -or $receiptItem.Length -gt 1048576) {
        throw 'BUILD-RECEIPT.json must be a non-empty file no larger than 1 MiB.'
    }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $receiptProperties = @($receipt.PSObject.Properties.Name)
    if ($receiptProperties -cnotcontains 'authenticode_requested' -or
        $receiptProperties -cnotcontains 'expected_signer_thumbprint') {
        throw 'BUILD-RECEIPT.json has no signed-payload signer binding.'
    }
    if (-not ($receipt.authenticode_requested -is [bool]) -or $receipt.authenticode_requested -ne $true -or
        -not ($receipt.expected_signer_thumbprint -is [string]) -or
        -not [string]::Equals($receipt.expected_signer_thumbprint, $ExpectedThumbprint, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'BUILD-RECEIPT.json does not bind this payload to the expected release signer.'
    }

    $report = New-Object Collections.Generic.List[object]
    foreach ($relative in @(Get-WindowsPayloadProjectBinaries)) {
        $report.Add((Assert-ExpectedAuthenticodeSigner -Path (Join-Path $Directory $relative) `
            -ExpectedThumbprint $ExpectedThumbprint -Label "Payload binary '$relative'"))
    }
    return $report.ToArray()
}

function Find-Iscc {
    param([string]$ExplicitPath)

    $candidates = New-Object Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $candidates.Add((Get-AbsolutePath -Path $ExplicitPath))
    }
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) { $candidates.Add($command.Source) }
    foreach ($root in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, $env:LOCALAPPDATA)) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $relative = if ([string]::Equals($root, $env:LOCALAPPDATA, [StringComparison]::OrdinalIgnoreCase)) {
            'Programs\Inno Setup 6\ISCC.exe'
        }
        else {
            'Inno Setup 6\ISCC.exe'
        }
        $candidates.Add((Join-Path $root $relative))
    }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            Assert-NoReparsePath -Path $candidate -Label 'Inno Setup compiler'
            return (Get-Item -LiteralPath $candidate -Force).FullName
        }
    }
    throw 'Inno Setup 6 compiler (ISCC.exe) was not found. Install it from the official source; this build script performs no downloads.'
}

function Write-NewUtf8Text {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Text
    )

    $encoding = New-Object Text.UTF8Encoding($false)
    $stream = New-Object IO.FileStream($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = $encoding.GetBytes($Text)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

if ($AppVersion -notmatch '^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}(-[A-Za-z0-9][A-Za-z0-9.-]{0,31})?$') {
    throw "AppVersion is not an accepted semantic version: $AppVersion"
}
if (-not [string]::IsNullOrWhiteSpace($InnoSignToolName) -and $InnoSignToolName -notmatch '^[A-Za-z0-9_.-]{1,32}$') {
    throw 'InnoSignToolName must be a 1-32 character Inno Setup signing-tool name.'
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint) -and
    $ExpectedSignerThumbprint -notmatch '^[A-Fa-f0-9]{40}$') {
    throw 'ExpectedSignerThumbprint must be a 40-character certificate thumbprint.'
}
if ($VerifyOnly -and -not [string]::IsNullOrWhiteSpace($InnoSignToolName)) {
    throw 'VerifyOnly cannot validate an Inno signing-tool configuration or claim a signed installer. Remove InnoSignToolName; optionally use ExpectedSignerThumbprint to verify the signed payload only.'
}
if (-not [string]::IsNullOrWhiteSpace($InnoSignToolName) -and
    [string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint)) {
    throw 'A signed installer build requires ExpectedSignerThumbprint and an already signed payload.'
}
$normalizedExpectedSigner = if ([string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint)) {
    $null
}
else {
    $ExpectedSignerThumbprint.ToUpperInvariant()
}

$inputFull = Get-AbsolutePath -Path $InputDirectory
if (-not (Test-CanonicalPathWithin -Path $inputFull -Directory $workRoot) -or
    -not (Test-Path -LiteralPath $inputFull -PathType Container)) {
    throw "InputDirectory must be an existing directory below the repository work directory: $inputFull"
}
Assert-OutsideProtectedRuntime -Path $inputFull -Label 'Installer payload'
$manifest = Read-AndVerifyPayload -Directory $inputFull
$payloadSignatureReport = @()
$payloadSignatureVerified = $false
if ($null -ne $normalizedExpectedSigner) {
    $payloadSignatureReport = @(Assert-SignedPayloadReceipt -Directory $inputFull `
        -ExpectedThumbprint $normalizedExpectedSigner)
    $payloadSignatureVerified = $true
}

if ($VerifyOnly) {
    [ordered]@{
        status = 'verified'
        app_version = $AppVersion
        input_directory = $inputFull
        file_count = [int]$manifest.file_count
        total_bytes = [long]$manifest.total_bytes
        payload_signature_verified = $payloadSignatureVerified
        expected_signer_thumbprint = $normalizedExpectedSigner
        installer_built = $false
        installer_signature_verified = $false
        inno_sign_tool_configuration_checked = $false
    } | ConvertTo-Json -Depth 4
    return
}

$outputCandidate = $OutputDirectory
if (-not [IO.Path]::IsPathRooted($outputCandidate)) { $outputCandidate = Join-Path $repoRoot $outputCandidate }
$outputFull = [IO.Path]::GetFullPath($outputCandidate)
if (-not (Test-CanonicalPathWithin -Path $outputFull -Directory $workRoot)) {
    throw "OutputDirectory must stay below the repository work directory: $outputFull"
}
if ((Test-CanonicalPathWithin -Path $outputFull -Directory $inputFull) -or
    (Test-CanonicalPathWithin -Path $inputFull -Directory $outputFull)) {
    throw 'Installer input and output directories must not overlap.'
}
Assert-NoReparsePath -Path $outputFull -Label 'Installer output directory'
[IO.Directory]::CreateDirectory($outputFull) | Out-Null
Assert-NoReparsePath -Path $outputFull -Label 'Installer output directory'

if ([string]::IsNullOrWhiteSpace($InstallerBaseName)) {
    $InstallerBaseName = "InvokersRu-3.0-Preview-$AppVersion-win-x64"
}
if ($InstallerBaseName -notmatch '^InvokersRu-[A-Za-z0-9._-]{1,96}$') {
    throw "InstallerBaseName is unsafe: $InstallerBaseName"
}
$installerPath = Join-Path $outputFull ($InstallerBaseName + '.exe')
$hashSidecarPath = $installerPath + '.sha256'
if ((Test-Path -LiteralPath $installerPath) -or (Test-Path -LiteralPath $hashSidecarPath)) {
    throw "Installer output already exists; refusing to overwrite it: $installerPath"
}

$compiler = Find-Iscc -ExplicitPath $IsccPath
$stageRoot = Join-Path $workRoot 'installer-stage'
[IO.Directory]::CreateDirectory($stageRoot) | Out-Null
Assert-NoReparsePath -Path $stageRoot -Label 'Installer stage root'
$stage = Join-Path $stageRoot ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null

foreach ($record in @($manifest.files)) {
    $relative = ([string]$record.path).Replace('/', '\')
    $source = Join-Path $inputFull $relative
    $destination = Join-Path $stage $relative
    [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
    [IO.File]::Copy($source, $destination, $false)
}
[IO.File]::Copy((Join-Path $inputFull 'PAYLOAD-SHA256.json'), (Join-Path $stage 'PAYLOAD-SHA256.json'), $false)
$stageManifest = Read-AndVerifyPayload -Directory $stage

$issPath = Join-Path $repoRoot 'installer\InvokersRu.iss'
$compilerArguments = @(
    "/DSourceDir=$stage",
    "/DOutputDir=$outputFull",
    "/DAppVersion=$AppVersion",
    "/DInstallerBaseName=$InstallerBaseName"
)
if (-not [string]::IsNullOrWhiteSpace($InnoSignToolName)) {
    $compilerArguments += "/DInnoSignTool=$InnoSignToolName"
}
else {
    Write-Warning 'UNSIGNED LOCAL INSTALLER: Windows may show an Unknown publisher/SmartScreen warning. Do not distribute it as an official release.'
}
$compilerArguments += $issPath

& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE." }

$stageManifest = Read-AndVerifyPayload -Directory $stage
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "ISCC.exe reported success but the installer is missing: $installerPath"
}
$installerItem = Get-Item -LiteralPath $installerPath -Force
if ($installerItem.Length -le 0) { throw "Compiled installer is empty: $installerPath" }

$signature = Get-AuthenticodeSignature -LiteralPath $installerPath
$installerSignatureVerified = $false
$installerSignerThumbprint = $null
if (-not [string]::IsNullOrWhiteSpace($InnoSignToolName)) {
    $installerSignatureReport = Assert-ExpectedAuthenticodeSigner -Path $installerPath `
        -ExpectedThumbprint $normalizedExpectedSigner -Label 'Compiled installer'
    $installerSignatureVerified = $true
    $installerSignerThumbprint = [string]$installerSignatureReport.signer_thumbprint
}
elseif ([string]$signature.Status -ne 'Valid') {
    Write-Warning "Installer Authenticode status is '$($signature.Status)'. This is acceptable only for local testing."
}

$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToUpperInvariant()
Write-NewUtf8Text -Path $hashSidecarPath -Text "$installerHash *$($installerItem.Name)`r`n"

[ordered]@{
    status = 'built'
    app_version = $AppVersion
    installer = $installerPath
    sha256 = $installerHash
    sha256_file = $hashSidecarPath
    authenticode_status = [string]$signature.Status
    installer_signature_verified = $installerSignatureVerified
    installer_signer_thumbprint = $installerSignerThumbprint
    payload_signature_verified = $payloadSignatureVerified
    expected_signer_thumbprint = $normalizedExpectedSigner
    inno_sign_tool_configured = -not [string]::IsNullOrWhiteSpace($InnoSignToolName)
    staged_payload = $stage
    file_count = [int]$stageManifest.file_count
    compiler = $compiler
} | ConvertTo-Json -Depth 5
