[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PayloadDirectory,

    [string]$AppVersion = '3.1.4-preview'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'path-safety.ps1')
. (Join-Path $PSScriptRoot 'windows-payload-policy.ps1')

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) { throw $Message }
}

function Test-SchemaAllowsPath {
    param(
        [Parameter(Mandatory = $true)]$PathSchema,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ($Path.Length -lt [int]$PathSchema.minLength -or $Path.Length -gt [int]$PathSchema.maxLength) {
        return $false
    }
    if (-not [regex]::IsMatch($Path, [string]$PathSchema.allOf[0].pattern) -or
        [regex]::IsMatch($Path, [string]$PathSchema.allOf[1].not.pattern)) {
        return $false
    }
    $choiceMatchCount = 0
    foreach ($choice in @($PathSchema.oneOf)) {
        $enumProperty = $choice.PSObject.Properties['enum']
        $patternProperty = $choice.PSObject.Properties['pattern']
        if ($null -ne $enumProperty -and @($enumProperty.Value) -ccontains $Path) { $choiceMatchCount++ }
        elseif ($null -ne $patternProperty -and
            [regex]::IsMatch($Path, [string]$patternProperty.Value)) { $choiceMatchCount++ }
    }
    return $choiceMatchCount -eq 1
}

function Assert-ManifestMutationRejected {
    param(
        [Parameter(Mandatory = $true)][string]$SourceManifest,
        [Parameter(Mandatory = $true)][string]$BuildScript,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][scriptblock]$Mutation,
        [Parameter(Mandatory = $true)][string]$ExpectedError,
        [Parameter(Mandatory = $true)][string]$CaseName
    )

    $workRoot = [IO.Path]::GetFullPath((Join-Path $script:repoRoot 'work'))
    $fixtureName = '.packaging-manifest-test-' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $fixtureRoot = Join-Path $workRoot $fixtureName
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    try {
        $fixtureManifest = Join-Path $fixtureRoot 'PAYLOAD-SHA256.json'
        $manifestObject = Get-Content -LiteralPath $SourceManifest -Raw -Encoding UTF8 | ConvertFrom-Json
        & $Mutation $manifestObject
        $encoding = New-Object Text.UTF8Encoding($false)
        [IO.File]::WriteAllText($fixtureManifest,
            (($manifestObject | ConvertTo-Json -Depth 8) + "`r`n"), $encoding)

        $rejected = $false
        try {
            & $BuildScript -InputDirectory $fixtureRoot -AppVersion $Version -VerifyOnly | Out-Null
        }
        catch {
            if ($_.Exception.Message -notmatch $ExpectedError) {
                throw "Manifest case '$CaseName' failed for an unexpected reason: $($_.Exception.Message)"
            }
            $rejected = $true
        }
        Assert-True -Condition $rejected -Message "Runtime validator accepted manifest case: $CaseName"
    }
    finally {
        $verifiedRoot = [IO.Path]::GetFullPath($fixtureRoot)
        if (-not (Test-CanonicalPathWithin -Path $verifiedRoot -Directory $workRoot) -or
            [IO.Path]::GetFileName($verifiedRoot) -notmatch '^\.packaging-manifest-test-[a-f0-9]{32}\.tmp$') {
            throw "Test cleanup target failed its path check: $verifiedRoot"
        }
        Assert-NoReparsePath -Path $verifiedRoot -Label 'Packaging manifest test cleanup root'
        if (Test-Path -LiteralPath $verifiedRoot -PathType Container) {
            [IO.Directory]::Delete($verifiedRoot, $true)
        }
    }
}

$schemaPath = Join-Path $repoRoot 'installer\payload-manifest.schema.json'
$schema = Get-Content -LiteralPath $schemaPath -Raw -Encoding UTF8 | ConvertFrom-Json
$pathSchema = $schema.properties.files.items.properties.path
$recordSchema = $schema.properties.files.items.properties

Assert-True -Condition ([int64]$schema.properties.schema_version.const -eq 2) `
    -Message 'Schema does not require manifest schema_version 2.'
Assert-True -Condition ([string]$schema.properties.app_version.type -ceq 'string') `
    -Message 'Schema app_version type differs from the runtime validator.'
Assert-True -Condition ([string]$schema.properties.file_count.type -ceq 'integer') `
    -Message 'Schema file_count type differs from the runtime validator.'
Assert-True -Condition ([string]$schema.properties.total_bytes.type -ceq 'integer') `
    -Message 'Schema total_bytes type differs from the runtime validator.'
Assert-True -Condition ([string]$schema.properties.files.type -ceq 'array') `
    -Message 'Schema files type differs from the runtime validator.'
Assert-True -Condition ([string]$recordSchema.path.type -ceq 'string' -and
    [string]$recordSchema.length.type -ceq 'integer' -and
    [string]$recordSchema.sha256.type -ceq 'string') `
    -Message 'Schema record types differ from the runtime validator.'

$accepted = @(
    'InvokersRu.Gui.exe',
    'InvokersRu.Cli.exe',
    'ru/System.Private.CoreLib.resources.dll',
    'InvokersRu.Gui.deps.json',
    'InvokersRu.Gui.runtimeconfig.json',
    'translations/ru_RU.jsonl',
    'profiles/runtime-cache-profile.0.60.1247.json',
    'BUILD-RECEIPT.json',
    'LICENSE.txt',
    'README.txt'
)
$rejected = @(
    '../evil.dll',
    'dir\evil.dll',
    '/evil.dll',
    'dir//evil.dll',
    'dir/./evil.dll',
    'dir/../evil.dll',
    'CON.dll',
    'dir/aux.payload.dll',
    'dir/trailing./evil.dll',
    'dir/trailing /evil.dll',
    'evil.exe',
    'dir/evil.deps.json',
    'evil.json',
    'evil.ps1',
    'evil.DLL',
    'evil:bad.dll'
)

foreach ($path in $accepted) {
    Assert-WindowsPayloadRelativePath -RelativePath $path
    Assert-True -Condition (Test-SchemaAllowsPath -PathSchema $pathSchema -Path $path) `
        -Message "Schema rejected a policy-approved path: $path"
}
foreach ($path in $rejected) {
    $policyRejected = $false
    try { Assert-WindowsPayloadRelativePath -RelativePath $path }
    catch { $policyRejected = $true }
    Assert-True -Condition $policyRejected -Message "Policy accepted a forbidden path: $path"
    Assert-True -Condition (-not (Test-SchemaAllowsPath -PathSchema $pathSchema -Path $path)) `
        -Message "Schema accepted a policy-forbidden path: $path"
}

foreach ($validInteger in @([int32]2, [int64]150750104, [double]2.0, [decimal]2.0, [single]2.0)) {
    Assert-True -Condition (Test-JsonIntegerValue $validInteger) `
        -Message 'Integer type helper rejected a mathematical JSON integer.'
}
foreach ($jsonText in @('{"value":2.0}', '{"value":1e0}')) {
    $parsedValue = (ConvertFrom-Json -InputObject $jsonText).value
    Assert-True -Condition (Test-JsonIntegerValue $parsedValue) `
        -Message "Integer type helper rejected schema-valid JSON: $jsonText"
}
foreach ($invalidInteger in @('2', [double]2.5, [decimal]2.5, $true, $null,
    [double]::NaN, [double]::PositiveInfinity)) {
    Assert-True -Condition (-not (Test-JsonIntegerValue $invalidInteger)) `
        -Message 'Integer type helper accepted a non-integral or non-finite value.'
}

$payloadFull = [IO.Path]::GetFullPath(
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PayloadDirectory))
$payloadManifestPath = Join-Path $payloadFull 'PAYLOAD-SHA256.json'
$payloadManifest = Get-Content -LiteralPath $payloadManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($record in @($payloadManifest.files)) {
    Assert-True -Condition (Test-SchemaAllowsPath -PathSchema $pathSchema -Path ([string]$record.path)) `
        -Message "Schema rejected an actual runtime-approved payload path: $($record.path)"
}

$publishScriptText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'publish-windows-preview.ps1') `
    -Raw -Encoding UTF8
$createParentPosition = $publishScriptText.IndexOf('[IO.Directory]::CreateDirectory($outputParent)',
    [StringComparison]::Ordinal)
$commitPosition = $publishScriptText.IndexOf('[IO.Directory]::Move($payloadRoot, $outputPath)',
    [StringComparison]::Ordinal)
Assert-True -Condition ($createParentPosition -ge 0 -and $commitPosition -gt $createParentPosition) `
    -Message 'Publish does not create the final output parent before its atomic Directory.Move.'

$issText = Get-Content -LiteralPath (Join-Path $repoRoot 'installer\InvokersRu.iss') -Raw -Encoding UTF8
$minimumWindowsBuild = '10.0.14393'
$releaseVersion = '3.1.4-preview'
Assert-True -Condition ([regex]::Matches($issText,
    '(?m)^MinVersion=10\.0\.14393\r?$').Count -eq 1) `
    -Message "Installer must require exactly x64 Windows 10 build $minimumWindowsBuild or newer."
Assert-True -Condition ($issText.IndexOf('MinVersion=10.0.17763', [StringComparison]::Ordinal) -lt 0) `
    -Message 'Installer still blocks Windows 10 builds older than 1809.'
Assert-True -Condition ([regex]::Matches($issText,
    '(?m)^ArchitecturesAllowed=x64compatible\r?$').Count -eq 1) `
    -Message 'Installer architecture allowlist is not exactly x64compatible.'
Assert-True -Condition ([regex]::Matches($issText,
    '(?m)^ArchitecturesInstallIn64BitMode=x64compatible\r?$').Count -eq 1) `
    -Message 'Installer does not install the x64 payload in 64-bit mode.'
foreach ($versionedInstallerSetting in @(
    'AppVerName=InvokersRu {#AppVersion}',
    'UninstallDisplayName=InvokersRu {#AppVersion}',
    'VersionInfoDescription=InvokersRu {#AppVersion} installer',
    'VersionInfoProductName=InvokersRu {#AppVersion}'
)) {
    Assert-True -Condition ($issText.IndexOf($versionedInstallerSetting, [StringComparison]::Ordinal) -ge 0) `
        -Message "Installer does not derive version metadata from AppVersion: $versionedInstallerSetting"
}

$guiProjectText = Get-Content -LiteralPath (Join-Path $repoRoot 'src\InvokersRu.Gui\InvokersRu.Gui.csproj') `
    -Raw -Encoding UTF8
$cliProjectText = Get-Content -LiteralPath (Join-Path $repoRoot 'src\InvokersRu.Cli\InvokersRu.Cli.csproj') `
    -Raw -Encoding UTF8
$publishScriptText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'publish-windows-preview.ps1') `
    -Raw -Encoding UTF8
Assert-True -Condition ($guiProjectText.IndexOf(
    '<TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>', [StringComparison]::Ordinal) -ge 0) `
    -Message 'GUI does not use the oldest Windows API target pack shipped by this .NET 10 SDK.'
Assert-True -Condition ($guiProjectText.IndexOf(
    '<SupportedOSPlatformVersion>10.0.14393.0</SupportedOSPlatformVersion>', [StringComparison]::Ordinal) -ge 0) `
    -Message 'GUI supported-platform metadata does not declare Windows 10 build 14393.'
foreach ($projectVersionText in @($guiProjectText, $cliProjectText)) {
    Assert-True -Condition ($projectVersionText.IndexOf(
        "<Version>$releaseVersion</Version>", [StringComparison]::Ordinal) -ge 0) `
        -Message "A Windows entry point does not default to version $releaseVersion."
}
Assert-True -Condition ($publishScriptText.IndexOf(
    "[string]`$OutputDirectory = 'work\publish\windows-$releaseVersion'", [StringComparison]::Ordinal) -ge 0) `
    -Message "Windows publish output does not default to version $releaseVersion."
Assert-True -Condition ($publishScriptText.IndexOf(
    "[string]`$AppVersion = '$releaseVersion'", [StringComparison]::Ordinal) -ge 0) `
    -Message "Windows publish AppVersion does not default to $releaseVersion."
foreach ($requiredInstallerSetting in @(
    'DefaultDirName={localappdata}\Programs\InvokersRu',
    'DisableDirPage=yes',
    'UsePreviousAppDir=no',
    'PrivilegesRequired=lowest',
    'RequestedDirectory := CanonicalDirectory(WizardDirValue);',
    'FixedDirectory := CanonicalDirectory(ExpandConstant(''{localappdata}\Programs\InvokersRu''));',
    'PathTraversesReparsePoint(RequestedDirectory)',
    'FindFirst(SearchPath, FindRec)',
    'FILE_ATTRIBUTE_REPARSE_POINT'
)) {
    Assert-True -Condition ($issText.IndexOf($requiredInstallerSetting, [StringComparison]::Ordinal) -ge 0) `
        -Message "Installer fixed-directory guard is missing: $requiredInstallerSetting"
}
Assert-True -Condition ($issText.IndexOf('ignoreversion', [StringComparison]::OrdinalIgnoreCase) -lt 0) `
    -Message 'Installer still uses ignoreversion.'
Assert-True -Condition ($issText.IndexOf('replacesameversion', [StringComparison]::OrdinalIgnoreCase) -ge 0) `
    -Message 'Installer may preserve stale same-version binaries while replacing unversioned data.'
Assert-True -Condition ($issText.IndexOf("`n[Run]", [StringComparison]::OrdinalIgnoreCase) -lt 0) `
    -Message 'Installer unexpectedly contains a Run section.'

$buildScript = Join-Path $PSScriptRoot 'build-installer.ps1'
Assert-ManifestMutationRejected -SourceManifest $payloadManifestPath -BuildScript $buildScript `
    -Version $AppVersion -CaseName 'schema_version string' `
    -ExpectedError 'supported win-x64 self-contained multi-file build' `
    -Mutation { param($manifest) $manifest.schema_version = '2' }
Assert-ManifestMutationRejected -SourceManifest $payloadManifestPath -BuildScript $buildScript `
    -Version $AppVersion -CaseName 'uppercase-only SHA-256' `
    -ExpectedError 'invalid metadata' `
    -Mutation { param($manifest) $manifest.files[0].sha256 = ([string]$manifest.files[0].sha256).ToLowerInvariant() }
Assert-ManifestMutationRejected -SourceManifest $payloadManifestPath -BuildScript $buildScript `
    -Version $AppVersion -CaseName 'path outside shared allowlist' `
    -ExpectedError 'outside the Windows allowlist' `
    -Mutation { param($manifest) $manifest.files[0].path = 'evil.DLL' }

$verifyOutput = @(& $buildScript -InputDirectory $payloadFull -AppVersion $AppVersion -VerifyOnly)
$verify = ($verifyOutput -join [Environment]::NewLine) | ConvertFrom-Json
Assert-True -Condition ([string]$verify.status -eq 'verified') -Message 'VerifyOnly did not verify the payload.'
Assert-True -Condition ($verify.installer_built -eq $false) -Message 'VerifyOnly claimed that an installer was built.'
Assert-True -Condition ($verify.installer_signature_verified -eq $false) -Message 'VerifyOnly claimed a verified installer signature.'
Assert-True -Condition ($verify.inno_sign_tool_configuration_checked -eq $false) -Message 'VerifyOnly claimed to inspect Inno signing configuration.'

$fakeToolRejected = $false
try {
    & $buildScript -InputDirectory $payloadFull -AppVersion $AppVersion -VerifyOnly `
        -InnoSignToolName 'FakeConfiguredTool' | Out-Null
}
catch {
    $fakeToolRejected = $_.Exception.Message -match 'VerifyOnly cannot validate'
}
Assert-True -Condition $fakeToolRejected -Message 'VerifyOnly accepted a fake configured Inno Sign Tool.'

$unsignedSignerRejected = $false
try {
    & $buildScript -InputDirectory $payloadFull -AppVersion $AppVersion -VerifyOnly `
        -ExpectedSignerThumbprint '0123456789ABCDEF0123456789ABCDEF01234567' | Out-Null
}
catch {
    $unsignedSignerRejected = $_.Exception.Message -match 'BUILD-RECEIPT|Authenti|signature'
}
Assert-True -Condition $unsignedSignerRejected -Message 'VerifyOnly accepted an unsigned payload for an expected signer.'

[ordered]@{
    status = 'passed'
    accepted_path_cases = $accepted.Count
    rejected_path_cases = $rejected.Count
    payload_file_count = [int]$verify.file_count
    actual_payload_paths_schema_checked = @($payloadManifest.files).Count
    publish_parent_before_commit = $true
    minimum_windows_build = $minimumWindowsBuild
    windows_architecture = 'x64compatible'
    source_release_version = $releaseVersion
    gui_windows_target = 'net10.0-windows10.0.17763.0'
    fixed_installer_directory_guard = $true
    malformed_manifest_cases_rejected = 3
    json_schema_integer_semantics_checked = $true
    installer_reparse_guard = $true
    fake_sign_tool_rejected = $fakeToolRejected
    unsigned_expected_signer_rejected = $unsignedSignerRejected
} | ConvertTo-Json -Depth 4
