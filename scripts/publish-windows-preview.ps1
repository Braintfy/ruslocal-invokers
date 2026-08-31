[CmdletBinding()]
param(
    [string]$OutputDirectory = 'work\publish\windows-3.1.4-preview',

    [string]$AppVersion = '3.1.4-preview',

    [string]$TranslationCatalog = 'translations\ru_RU.jsonl',

    [string]$RuntimeCacheProfile = 'config\runtime-cache-profile.0.60.1247.json',

    [string]$SignedUpdateChannelConfig = 'config\signed-update-channel.v1.json',

    [string]$LegacyCompatibilityManifest = 'config\compatibility.v1.json',

    [string]$DotNetPath,

    [string]$SignToolPath,

    [string]$CertificateThumbprint,

    [string]$TimestampUrl,

    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'work'))
. (Join-Path $PSScriptRoot 'path-safety.ps1')
. (Join-Path $PSScriptRoot 'windows-payload-policy.ps1')

function Get-AbsoluteRepositoryPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $candidate = $Path
    if (-not [IO.Path]::IsPathRooted($candidate)) {
        $candidate = Join-Path $script:repoRoot $candidate
    }
    $fullPath = [IO.Path]::GetFullPath($candidate)
    if (-not (Test-CanonicalPathWithin -Path $fullPath -Directory $script:repoRoot)) {
        throw "$Label must stay inside the repository: $fullPath"
    }
    Assert-NoReparsePath -Path $fullPath -Label $Label
    return $fullPath
}

function Get-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $fullPath = Get-AbsoluteRepositoryPath -Path $Path -Label $Label
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "$Label is missing: $fullPath"
    }
    $item = Get-Item -LiteralPath $fullPath -Force
    if ($item.Length -le 0 -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must be a non-empty regular file: $fullPath"
    }
    return $item.FullName
}

function Get-RequiredExternalFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $fullPath = [IO.Path]::GetFullPath(
        $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
    Assert-NoReparsePath -Path $fullPath -Label $Label
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "$Label is missing: $fullPath"
    }
    $item = Get-Item -LiteralPath $fullPath -Force
    if ($item.Length -le 0 -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must be a non-empty regular file: $fullPath"
    }
    return $item.FullName
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

function Copy-SupervisedCliApplication {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    Assert-NoReparseTree -Root $Source -Label 'supervised CLI publish output'
    foreach ($runtimeFile in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Source $runtimeFile) -PathType Leaf)) {
            throw "Supervised CLI publish was not self-contained: $runtimeFile is missing."
        }
    }

    # A WindowsDesktop self-contained publish is a strict runtime superset of
    # the console app's NETCore publish. Keep that one coherent runtime tree and
    # add only the supervised CLI's own application files. The packaged CLI is
    # executed below, so an incompatible shared runtime fails the build.
    $applicationFiles = @(
        'InvokersRu.Cli.exe',
        'InvokersRu.Cli.dll',
        'InvokersRu.Cli.deps.json',
        'InvokersRu.Cli.runtimeconfig.json',
        'InvokersRu.Core.dll'
    )
    foreach ($name in $applicationFiles) {
        $sourcePath = Join-Path $Source $name
        $targetPath = Join-Path $Destination $name
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Supervised CLI application file is missing: $name"
        }
        if (Test-Path -LiteralPath $targetPath) {
            throw "GUI publish unexpectedly already contains the supervised CLI file: $name"
        }
        [IO.File]::Copy($sourcePath, $targetPath, $false)
    }
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

function Invoke-CodeSigning {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Files,
        [Parameter(Mandatory = $true)][string]$Thumbprint,
        [Parameter(Mandatory = $true)][string]$Rfc3161Url
    )

    foreach ($file in $Files) {
        & $Executable sign /sha1 $Thumbprint /fd SHA256 /tr $Rfc3161Url /td SHA256 $file
        if ($LASTEXITCODE -ne 0) {
            throw "signtool.exe failed for '$file' with exit code $LASTEXITCODE."
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        if ([string]$signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
            -not [string]::Equals([string]$signature.SignerCertificate.Thumbprint, $Thumbprint, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Authenticode verification failed for '$file': $($signature.StatusMessage)"
        }
    }
}

if ($AppVersion -notmatch '^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}(-[A-Za-z0-9][A-Za-z0-9.-]{0,31})?$') {
    throw "AppVersion is not an accepted semantic version: $AppVersion"
}

$catalogPath = Get-RequiredFile -Path $TranslationCatalog -Label 'Translation catalog'
$profilePath = Get-RequiredFile -Path $RuntimeCacheProfile -Label 'Runtime-cache compatibility profile'
$channelConfigPath = Get-RequiredFile -Path $SignedUpdateChannelConfig -Label 'Signed-update channel config'
$legacyManifestPath = Get-RequiredFile -Path $LegacyCompatibilityManifest -Label 'Legacy compatibility manifest'
$licensePath = Get-RequiredFile -Path 'LICENSE' -Label 'Project license'
$readmePath = Get-RequiredFile -Path 'installer\PREVIEW-README.txt' -Label 'Installed preview README'

foreach ($msbuildPath in @($profilePath, $channelConfigPath, $legacyManifestPath)) {
    if ($msbuildPath.IndexOfAny([char[]]@(';', '%')) -ge 0) {
        throw "MSBuild input path contains a reserved character: $msbuildPath"
    }
}

$profile = Get-Content -LiteralPath $profilePath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([int]$profile.schema -ne 1 -or [string]$profile.game_version -ne '0.60.1247' -or
    [string]$profile.readiness -ne 'ready' -or $profile.certified -ne $true) {
    throw 'Windows Preview 3.1 may be built only with the ready, certified 0.60.1247 runtime-cache profile.'
}
$catalogHash = (Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash.ToUpperInvariant()
if (-not [string]::Equals($catalogHash, [string]$profile.translation_catalog_sha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Translation catalog SHA-256 does not match the certified profile. Expected $($profile.translation_catalog_sha256), got $catalogHash."
}
$channelConfig = Get-Content -LiteralPath $channelConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([int]$channelConfig.schema -ne 1 -or
    [string]$channelConfig.kind -cne 'invokers-ru-update-channel' -or
    [string]$channelConfig.envelope_url -cne 'https://github.com/Braintfy/ruslocal-invokers/releases/download/invokersru-update-channel-v1/update-envelope.v1.json' -or
    [string]::IsNullOrWhiteSpace([string]$channelConfig.key_id) -or
    [string]::IsNullOrWhiteSpace([string]$channelConfig.public_key_spki_base64)) {
    throw 'Signed-update channel config does not describe the fixed public GitHub channel and key.'
}
try {
    $channelPublicKey = [Convert]::FromBase64String([string]$channelConfig.public_key_spki_base64)
}
catch {
    throw 'Signed-update channel public key is not valid Base64.'
}
if ($channelPublicKey.Length -le 0 -or $channelPublicKey.Length -gt 1024) {
    throw 'Signed-update channel public key has an invalid byte count.'
}
$channelConfigHash = (Get-FileHash -LiteralPath $channelConfigPath -Algorithm SHA256).Hash.ToUpperInvariant()
$channelPublicKeyHash = ([BitConverter]::ToString(
    [Security.Cryptography.SHA256]::Create().ComputeHash($channelPublicKey))).Replace('-', '')

$outputCandidate = $OutputDirectory
if (-not [IO.Path]::IsPathRooted($outputCandidate)) {
    $outputCandidate = Join-Path $repoRoot $outputCandidate
}
$outputPath = Assert-SafeNewOutputPath -Path $outputCandidate -Label 'Windows publish output'
if (-not (Test-CanonicalPathWithin -Path $outputPath -Directory $workRoot)) {
    throw "Windows publish output must stay below the repository work directory: $outputPath"
}
$outputParent = [IO.Path]::GetDirectoryName($outputPath)
if ([string]::IsNullOrWhiteSpace($outputParent) -or
    -not (Test-CanonicalPathWithin -Path $outputParent -Directory $workRoot)) {
    throw "Windows publish output parent must stay below the repository work directory: $outputParent"
}
Assert-NoReparsePath -Path $outputParent -Label 'Windows publish output parent'
[IO.Directory]::CreateDirectory($outputParent) | Out-Null
Assert-NoReparsePath -Path $outputParent -Label 'Windows publish output parent'

$signingValues = @($SignToolPath, $CertificateThumbprint, $TimestampUrl)
$specifiedSigningValues = @($signingValues | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$signingEnabled = $specifiedSigningValues.Count -ne 0
if ($signingEnabled -and $specifiedSigningValues.Count -ne 3) {
    throw 'SignToolPath, CertificateThumbprint, and TimestampUrl must be supplied together.'
}
$resolvedSignTool = $null
if ($signingEnabled) {
    if ($CertificateThumbprint -notmatch '^[A-Fa-f0-9]{40}$') {
        throw 'CertificateThumbprint must be a 40-character SHA-1 certificate thumbprint.'
    }
    $timestampUri = $null
    if (-not [Uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
        @('http', 'https') -notcontains $timestampUri.Scheme -or -not [string]::IsNullOrWhiteSpace($timestampUri.UserInfo)) {
        throw 'TimestampUrl must be an absolute HTTP(S) RFC 3161 endpoint without embedded credentials.'
    }
    $resolvedSignTool = Get-RequiredExternalFile -Path $SignToolPath -Label 'signtool.exe'
    if (-not [string]::Equals([IO.Path]::GetFileName($resolvedSignTool), 'signtool.exe', [StringComparison]::OrdinalIgnoreCase)) {
        throw "SignToolPath must point to signtool.exe: $resolvedSignTool"
    }
}
else {
    Write-Warning 'UNSIGNED LOCAL BUILD: Windows may show an Unknown publisher/SmartScreen warning. Do not distribute this build as an official release.'
}

$dotnetCandidate = Join-Path $repoRoot 'work\dotnet-10\dotnet.exe'
if (-not [string]::IsNullOrWhiteSpace($DotNetPath)) {
    $dotnet = Get-RequiredExternalFile -Path $DotNetPath -Label '.NET 10 SDK host'
}
elseif (Test-Path -LiteralPath $dotnetCandidate -PathType Leaf) {
    $dotnet = (Get-Item -LiteralPath $dotnetCandidate -Force).FullName
}
else {
    $dotnetCommand = Get-Command dotnet -ErrorAction Stop
    $dotnet = $dotnetCommand.Source
}
Assert-NoReparsePath -Path $dotnet -Label '.NET SDK host'
$detectedSdkVersion = (& $dotnet --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $detectedSdkVersion -notmatch '^(1[0-9]|[2-9][0-9])\.') {
    throw "A .NET 10 or newer SDK is required. Detected '$detectedSdkVersion' at '$dotnet'. Pass -DotNetPath with the full path to a .NET 10 dotnet.exe."
}

$temporaryRoot = Join-Path $workRoot ('.windows-publish-' + [Guid]::NewGuid().ToString('N') + '.tmp')
$temporaryRoot = Assert-SafeNewOutputPath -Path $temporaryRoot -Label 'Temporary Windows publish root'
$payloadRoot = Join-Path $temporaryRoot 'payload'
$cliRoot = Join-Path $temporaryRoot 'cli'
[IO.Directory]::CreateDirectory($payloadRoot) | Out-Null
[IO.Directory]::CreateDirectory($cliRoot) | Out-Null

$dotnetCliHome = Join-Path $workRoot 'dotnet-home-windows-preview'
[IO.Directory]::CreateDirectory($dotnetCliHome) | Out-Null
$env:DOTNET_CLI_HOME = $dotnetCliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Push-Location $repoRoot
try {
    if (-not $NoRestore) {
        & $dotnet restore 'src\InvokersRu.Gui\InvokersRu.Gui.csproj' `
            --runtime win-x64 --configfile NuGet.Config `
            --source https://api.nuget.org/v3/index.json
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet restore failed for the GUI with exit code $LASTEXITCODE."
        }

        & $dotnet restore 'src\InvokersRu.Cli\InvokersRu.Cli.csproj' `
            --runtime win-x64 --configfile NuGet.Config `
            --source https://api.nuget.org/v3/index.json `
            '-p:EnableSupervisedInstallWrites=true' `
            "-p:TrustedCompatibilityPath=$legacyManifestPath" `
            "-p:TrustedRuntimeCacheCompatibilityPath=$profilePath" `
            "-p:SignedUpdateChannelConfigPath=$channelConfigPath"
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet restore failed for the supervised CLI with exit code $LASTEXITCODE."
        }
    }

    $commonPublishProperties = @(
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        '-p:PublishReadyToRun=false',
        '-p:EnableCompressionInSingleFile=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:SatelliteResourceLanguages=ru',
        "-p:Version=$AppVersion"
    )

    & $dotnet publish 'src\InvokersRu.Gui\InvokersRu.Gui.csproj' `
        --configuration Release --runtime win-x64 --self-contained true --no-restore `
        @commonPublishProperties --output $payloadRoot
    if ($LASTEXITCODE -ne 0) { throw "GUI publish failed with exit code $LASTEXITCODE." }

    $cliProperties = @($commonPublishProperties) + @(
        '-p:EnableSupervisedInstallWrites=true',
        "-p:TrustedCompatibilityPath=$legacyManifestPath",
        "-p:TrustedRuntimeCacheCompatibilityPath=$profilePath",
        "-p:SignedUpdateChannelConfigPath=$channelConfigPath"
    )
    & $dotnet publish 'src\InvokersRu.Cli\InvokersRu.Cli.csproj' `
        --configuration Release --runtime win-x64 --self-contained true --no-restore `
        @cliProperties --output $cliRoot
    if ($LASTEXITCODE -ne 0) { throw "Supervised CLI publish failed with exit code $LASTEXITCODE." }

    Copy-SupervisedCliApplication -Source $cliRoot -Destination $payloadRoot

    # createdump.exe is an optional runtime crash-dump helper, not required to
    # run either application. Omit the third executable from the distributed
    # player so its executable surface stays limited to the two reviewed apps.
    $createDumpPath = Join-Path $payloadRoot 'createdump.exe'
    if (Test-Path -LiteralPath $createDumpPath -PathType Leaf) {
        Assert-NoReparsePath -Path $createDumpPath -Label 'Optional .NET createdump helper'
        [IO.File]::Delete($createDumpPath)
    }

    $translationsDirectory = Join-Path $payloadRoot 'translations'
    $profilesDirectory = Join-Path $payloadRoot 'profiles'
    [IO.Directory]::CreateDirectory($translationsDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($profilesDirectory) | Out-Null
    [IO.File]::Copy($catalogPath, (Join-Path $translationsDirectory 'ru_RU.jsonl'), $false)
    [IO.File]::Copy($profilePath, (Join-Path $profilesDirectory 'runtime-cache-profile.0.60.1247.json'), $false)
    [IO.File]::Copy($licensePath, (Join-Path $payloadRoot 'LICENSE.txt'), $false)
    [IO.File]::Copy($readmePath, (Join-Path $payloadRoot 'README.txt'), $false)

    foreach ($required in @(
        'InvokersRu.Gui.exe',
        'InvokersRu.Cli.exe',
        'InvokersRu.Gui.runtimeconfig.json',
        'InvokersRu.Cli.runtimeconfig.json',
        'coreclr.dll',
        'hostfxr.dll',
        'hostpolicy.dll',
        'translations\ru_RU.jsonl',
        'profiles\runtime-cache-profile.0.60.1247.json'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $payloadRoot $required) -PathType Leaf)) {
            throw "Self-contained multi-file payload is missing '$required'."
        }
    }

    $payloadExecutables = @(Get-ChildItem -LiteralPath $payloadRoot -File -Recurse -Filter '*.exe')
    $unexpectedExecutables = @($payloadExecutables | Where-Object {
        @('InvokersRu.Gui.exe', 'InvokersRu.Cli.exe') -cnotcontains $_.Name
    })
    if ($unexpectedExecutables.Count -ne 0 -or $payloadExecutables.Count -ne 2) {
        throw "Published payload must contain exactly the GUI and supervised CLI executables. Found: $($payloadExecutables.Name -join ', ')"
    }

    if (@(Get-ChildItem -LiteralPath $payloadRoot -File -Recurse | Where-Object {
        $_.Extension -in @('.ps1', '.psm1', '.cmd', '.bat', '.vbs', '.vbe', '.js', '.jse', '.wsf', '.hta')
    }).Count -ne 0) {
        throw 'Published player payload contains a forbidden script file.'
    }

    $cliPath = Join-Path $payloadRoot 'InvokersRu.Cli.exe'
    $profileInfoText = & $cliPath trusted-runtime-cache-info 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw 'Supervised CLI did not expose its embedded runtime-cache profile.'
    }
    $profileInfo = $profileInfoText | ConvertFrom-Json
    $expectedProfileHash = (Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash
    if ($profileInfo.installation_writes_enabled -ne $true -or
        $profileInfo.embedded_runtime_cache_profile -ne $true -or
        -not [string]::Equals([string]$profileInfo.profile_sha256, $expectedProfileHash, [StringComparison]::OrdinalIgnoreCase) -or
        [string]$profileInfo.profile.game_version -ne '0.60.1247' -or
        [string]$profileInfo.profile.translation_catalog_sha256 -ne $catalogHash) {
        throw 'Supervised CLI does not embed the exact 0.60.1247 runtime-cache profile and catalog pin.'
    }

    $updateStatusText = & $cliPath update-status --json 2>&1 | Out-String
    $updateStatusExit = $LASTEXITCODE
    if ($updateStatusExit -notin @(0, 5)) {
        throw "Supervised CLI signed-update introspection failed with exit code $updateStatusExit."
    }
    $updateStatus = $updateStatusText | ConvertFrom-Json
    if ($updateStatus.configured -ne $true -or $null -eq $updateStatus.channel -or
        [string]$updateStatus.channel.envelope_url -cne [string]$channelConfig.envelope_url -or
        [string]$updateStatus.channel.key_id -cne [string]$channelConfig.key_id -or
        -not [string]::Equals([string]$updateStatus.channel.public_key_spki_sha256,
            $channelPublicKeyHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Supervised CLI does not embed the exact signed-update channel URL and public key.'
    }

    $signTargets = foreach ($relative in @(Get-WindowsPayloadProjectBinaries)) {
        $target = Join-Path $payloadRoot $relative
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
            throw "Published payload is missing the required project binary '$relative'."
        }
        $target
    }
    if ($signingEnabled) {
        Invoke-CodeSigning -Executable $resolvedSignTool -Files $signTargets `
            -Thumbprint $CertificateThumbprint.ToUpperInvariant() -Rfc3161Url $TimestampUrl
    }

    $authenticode = foreach ($target in $signTargets) {
        $signature = Get-AuthenticodeSignature -LiteralPath $target
        [ordered]@{
            path = [IO.Path]::GetFileName($target)
            status = [string]$signature.Status
            signer_subject = if ($null -eq $signature.SignerCertificate) { $null } else { [string]$signature.SignerCertificate.Subject }
            signer_thumbprint = if ($null -eq $signature.SignerCertificate) { $null } else { [string]$signature.SignerCertificate.Thumbprint }
        }
    }

    $sdkVersion = $detectedSdkVersion
    $receipt = [ordered]@{
        schema = 2
        kind = 'invokers-ru-windows-preview-publish'
        app_version = $AppVersion
        game_version = [string]$profile.game_version
        runtime_identifier = 'win-x64'
        self_contained = $true
        publish_single_file = $false
        publish_trimmed = $false
        publish_ready_to_run = $false
        embedded_supervised_profile = $true
        translation_catalog_sha256 = $catalogHash
        runtime_cache_profile_sha256 = $expectedProfileHash.ToUpperInvariant()
        signed_update_channel_config_sha256 = $channelConfigHash
        signed_update_envelope_url = [string]$channelConfig.envelope_url
        signed_update_key_id = [string]$channelConfig.key_id
        signed_update_public_key_spki_sha256 = $channelPublicKeyHash
        gui_sha256 = (Get-FileHash -LiteralPath (Join-Path $payloadRoot 'InvokersRu.Gui.exe') -Algorithm SHA256).Hash.ToUpperInvariant()
        cli_sha256 = (Get-FileHash -LiteralPath $cliPath -Algorithm SHA256).Hash.ToUpperInvariant()
        dotnet_sdk = $sdkVersion
        authenticode_requested = $signingEnabled
        expected_signer_thumbprint = if ($signingEnabled) { $CertificateThumbprint.ToUpperInvariant() } else { $null }
        authenticode = @($authenticode)
    }
    Write-NewUtf8Text -Path (Join-Path $payloadRoot 'BUILD-RECEIPT.json') `
        -Text (($receipt | ConvertTo-Json -Depth 8) + "`r`n")

    $manifestScript = Join-Path $PSScriptRoot 'new-installer-input-manifest.ps1'
    $manifestOutput = @(& $manifestScript -InputDirectory $payloadRoot `
        -ManifestPath (Join-Path $payloadRoot 'PAYLOAD-SHA256.json') -AppVersion $AppVersion)
    $manifestResult = ($manifestOutput -join [Environment]::NewLine) | ConvertFrom-Json
    if ([string]$manifestResult.status -ne 'created' -or [int]$manifestResult.file_count -lt 12) {
        throw 'Payload manifest generator returned an invalid receipt.'
    }

    Assert-NoReparseTree -Root $payloadRoot -Label 'Completed Windows player payload'
    Assert-NoReparsePath -Path $outputParent -Label 'Windows publish output parent before commit'
    if ([IO.File]::Exists($outputPath) -or [IO.Directory]::Exists($outputPath)) {
        throw "Windows publish output appeared before final commit: $outputPath"
    }
    [IO.Directory]::Move($payloadRoot, $outputPath)
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        try {
            $verifiedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
            $temporaryName = [IO.Path]::GetFileName($verifiedTemporaryRoot)
            if (-not (Test-CanonicalPathWithin -Path $verifiedTemporaryRoot -Directory $workRoot) -or
                $temporaryName -notmatch '^\.windows-publish-[a-f0-9]{32}\.tmp$') {
                throw "Temporary cleanup target failed its path check: $verifiedTemporaryRoot"
            }
            Assert-NoReparsePath -Path $verifiedTemporaryRoot -Label 'Temporary Windows publish cleanup root'
            [IO.Directory]::Delete($verifiedTemporaryRoot, $true)
        }
        catch {
            Write-Warning "Temporary publish cleanup was skipped: $($_.Exception.Message)"
        }
    }
}

$result = [ordered]@{
    status = 'published'
    app_version = $AppVersion
    game_version = '0.60.1247'
    output_directory = $outputPath
    signed = $signingEnabled
    payload_manifest = (Join-Path $outputPath 'PAYLOAD-SHA256.json')
    installer_command = ".\scripts\build-installer.ps1 -InputDirectory `"$outputPath`" -AppVersion $AppVersion"
}
$result | ConvertTo-Json -Depth 4
