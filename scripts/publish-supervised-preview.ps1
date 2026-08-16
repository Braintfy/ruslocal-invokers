[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $TrustedManifest,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [string] $Version = '0.2.0-supervised-preview'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'path-safety.ps1')
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$projectRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = (Resolve-Path -LiteralPath $TrustedManifest).Path
$outputPath = Assert-SafeNewOutputPath -Path $OutputDirectory -Label 'Publish output'
if (-not [IO.File]::Exists($manifestPath)) { throw "Trusted manifest must be an existing regular file: $manifestPath" }
Assert-NoReparsePath -Path $manifestPath -Label 'Trusted manifest'
Assert-OutsideProtectedRuntime -Path $manifestPath -Label 'Trusted manifest'
if ($manifestPath.Contains(';', [StringComparison]::Ordinal) -or $manifestPath.Contains('%', [StringComparison]::Ordinal)) {
    throw 'Trusted manifest path contains characters reserved by MSBuild property parsing.'
}
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]{0,63}$') {
    throw 'Version must be a 1-64 character package token containing only ASCII letters, digits, dots, and hyphens.'
}

$manifest = [IO.File]::ReadAllText($manifestPath, $strictUtf8) | ConvertFrom-Json -Depth 30
if ($manifest.schema -ne 1 -or @($manifest.builds).Count -ne 1) {
    throw 'A supervised build requires a schema-1 manifest with exactly one build.'
}
$build = $manifest.builds[0]
if ($build.readiness -ne 'ready' -or $build.certified -ne $true -or $build.patch_mode -ne 'supervised_preview') {
    throw 'Trusted manifest must be ready, certified, and use patch_mode=supervised_preview.'
}
foreach ($property in 'translation_catalog_sha256','expected_output_sha256','expected_output_raw_sha256') {
    if ([string]$build.$property -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "Trusted manifest has no valid $property."
    }
}

$outputParent = [IO.Path]::GetDirectoryName($outputPath)
if ([string]::IsNullOrWhiteSpace($outputParent)) { throw "Publish output has no parent directory: $outputPath" }
[IO.Directory]::CreateDirectory($outputParent) | Out-Null
Assert-NoReparsePath -Path $outputPath -Label 'Publish output'
$temporaryOutputName = ".{0}.{1}.tmp" -f [IO.Path]::GetFileName($outputPath), [Guid]::NewGuid().ToString('N')
$temporaryOutput = [IO.Path]::Combine($outputParent, $temporaryOutputName)
$temporaryOutput = Assert-SafeNewOutputPath -Path $temporaryOutput -Label 'Temporary publish output'

$dotnet = Join-Path $projectRoot 'work\dotnet-10\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
$dotnetCliHome = Join-Path $projectRoot 'work\dotnet-home-10'
[IO.Directory]::CreateDirectory($dotnetCliHome) | Out-Null
$env:DOTNET_CLI_HOME = $dotnetCliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Push-Location $projectRoot
try {
    & $dotnet publish 'src\InvokersRu.Cli\InvokersRu.Cli.csproj' `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        '-p:PublishSingleFile=true' `
        '-p:PublishTrimmed=false' `
        '-p:DebugType=None' `
        '-p:DebugSymbols=false' `
        '-p:EnableSupervisedInstallWrites=true' `
        "-p:TrustedCompatibilityPath=$manifestPath" `
        "-p:Version=$Version" `
        --output $temporaryOutput
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    $exe = Join-Path $temporaryOutput 'InvokersRu.Cli.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Published supervised executable is missing.' }
    Assert-NoReparsePath -Path $exe -Label 'Published executable'
    $help = & $exe help 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $help -notmatch 'embedded compatibility manifest') {
        throw 'Published executable did not identify itself as the supervised embedded-manifest build.'
    }

    $manifestHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash
    $manifestInfoJson = & $exe trusted-manifest-info 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'Published executable did not expose trusted manifest metadata.' }
    $manifestInfo = $manifestInfoJson | ConvertFrom-Json -Depth 30
    if ($manifestInfo.schema -ne 1 -or $manifestInfo.installation_writes_enabled -ne $true -or
        $manifestInfo.embedded_manifest -ne $true -or [string]$manifestInfo.manifest_sha256 -ne $manifestHash -or
        @($manifestInfo.builds).Count -ne 1 -or [string]$manifestInfo.builds[0].id -ne [string]$build.id) {
        throw 'Published executable does not embed the exact trusted manifest supplied to this build.'
    }

    $probeState = Join-Path $temporaryOutput 'embedded-manifest-probe.state.json'
    $probe = & $exe plan --game-root $projectRoot --state $probeState 2>&1 | Out-String
    $probeExitCode = $LASTEXITCODE
    if ($probeExitCode -ne 5 -or $probe -notmatch 'Status: MissingFiles') {
        throw 'Published executable did not successfully load and validate its embedded compatibility manifest.'
    }

    $receipt = [ordered]@{
        schema = 1
        kind = 'invokers-ru-supervised-publish'
        version = $Version
        cli_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash
        trusted_manifest_sha256 = $manifestHash
        build_id = [string]$build.id
    }
    $receiptPath = Join-Path $temporaryOutput 'SUPERVISED-PUBLISH.json'
    Assert-NoReparsePath -Path $receiptPath -Label 'Publish receipt'
    $receiptBytes = $strictUtf8.GetBytes(($receipt | ConvertTo-Json -Depth 10) + "`n")
    $receiptStream = [IO.FileStream]::new($receiptPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $receiptStream.Write($receiptBytes, 0, $receiptBytes.Length)
        $receiptStream.Flush($true)
    }
    finally {
        $receiptStream.Dispose()
    }

    Assert-NoReparsePath -Path $temporaryOutput -Label 'Temporary publish output'
    Assert-NoReparsePath -Path $outputPath -Label 'Publish output'
    [IO.Directory]::Move($temporaryOutput, $outputPath)
}
finally {
    Pop-Location
}

Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $outputPath 'InvokersRu.Cli.exe')
