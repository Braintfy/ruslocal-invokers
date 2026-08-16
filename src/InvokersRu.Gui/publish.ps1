[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CliPublishedDirectory,

    [Parameter(Mandatory = $true)]
    [string] $Translations,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [string] $Version = '0.2.0-preview'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $projectRoot 'scripts\path-safety.ps1')

$cliDirectory = (Resolve-Path -LiteralPath $CliPublishedDirectory).Path
$translationsPath = (Resolve-Path -LiteralPath $Translations).Path
$outputPath = Assert-SafeNewOutputPath -Path $OutputDirectory -Label 'GUI publish output'
if (-not [IO.Directory]::Exists($cliDirectory)) { throw "CLI publish directory does not exist: $cliDirectory" }
if (-not [IO.File]::Exists($translationsPath)) { throw "Translation catalog does not exist: $translationsPath" }
Assert-NoReparsePath -Path $cliDirectory -Label 'CLI publish directory'
Assert-NoReparsePath -Path $translationsPath -Label 'Translation catalog'
Assert-OutsideProtectedRuntime -Path $cliDirectory -Label 'CLI publish directory'
Assert-OutsideProtectedRuntime -Path $translationsPath -Label 'Translation catalog'
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]{0,63}$') {
    throw 'Version must be a 1-64 character package token containing only ASCII letters, digits, dots, and hyphens.'
}

$cliPath = Join-Path $cliDirectory 'InvokersRu.Cli.exe'
if (-not [IO.File]::Exists($cliPath)) { throw "Supervised CLI is missing: $cliPath" }
Assert-NoReparsePath -Path $cliPath -Label 'Supervised CLI'
$manifestInfoJson = & $cliPath trusted-manifest-info 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw 'CLI does not expose a trusted embedded manifest.' }
$manifestInfo = $manifestInfoJson | ConvertFrom-Json -Depth 30
if ($manifestInfo.schema -ne 1 -or $manifestInfo.installation_writes_enabled -ne $true -or
    $manifestInfo.embedded_manifest -ne $true -or @($manifestInfo.builds).Count -ne 1 -or
    $manifestInfo.builds[0].certified -ne $true -or $manifestInfo.builds[0].patch_mode -ne 'supervised_preview') {
    throw 'GUI may be packaged only with a certified supervised-preview CLI.'
}

$expectedTranslationHash = [string]$manifestInfo.builds[0].translation_catalog_sha256
$actualTranslationHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $translationsPath).Hash
if ($expectedTranslationHash -ne $actualTranslationHash) {
    throw 'Translation catalog does not match the exact hash embedded in the supervised CLI.'
}

$outputParent = [IO.Path]::GetDirectoryName($outputPath)
if ([string]::IsNullOrWhiteSpace($outputParent)) { throw "GUI publish output has no parent: $outputPath" }
[IO.Directory]::CreateDirectory($outputParent) | Out-Null
Assert-NoReparsePath -Path $outputPath -Label 'GUI publish output'
$temporaryName = ".{0}.{1}.tmp" -f [IO.Path]::GetFileName($outputPath), [Guid]::NewGuid().ToString('N')
$temporaryOutput = Assert-SafeNewOutputPath -Path ([IO.Path]::Combine($outputParent, $temporaryName)) -Label 'Temporary GUI publish output'

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
    & $dotnet publish 'src\InvokersRu.Gui\InvokersRu.Gui.csproj' `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        '-p:PublishSingleFile=true' `
        '-p:PublishTrimmed=false' `
        '-p:DebugType=None' `
        '-p:DebugSymbols=false' `
        "-p:Version=$Version" `
        --output $temporaryOutput
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    $guiPath = Join-Path $temporaryOutput 'InvokersRu.Gui.exe'
    if (-not [IO.File]::Exists($guiPath)) { throw 'Published GUI executable is missing.' }
    [IO.File]::Copy($cliPath, (Join-Path $temporaryOutput 'InvokersRu.Cli.exe'), $false)
    [IO.File]::Copy($translationsPath, (Join-Path $temporaryOutput 'ru_RU.mvp.jsonl'), $false)

    $help = & (Join-Path $temporaryOutput 'InvokersRu.Cli.exe') help 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $help -notmatch 'embedded compatibility manifest') {
        throw 'Packaged CLI failed its read-only help probe.'
    }

    $fakeGameRoot = Join-Path $temporaryOutput 'read-only-unknown-game-root'
    [IO.Directory]::CreateDirectory($fakeGameRoot) | Out-Null
    $probeState = Join-Path $temporaryOutput 'read-only-probe-state.json'
    $plan = & (Join-Path $temporaryOutput 'InvokersRu.Cli.exe') plan --game-root $fakeGameRoot --state $probeState 2>&1 | Out-String
    $planExitCode = $LASTEXITCODE
    if ($planExitCode -ne 5 -or $plan -notmatch 'Status: MissingFiles' -or [IO.File]::Exists($probeState)) {
        throw 'Packaged CLI failed its isolated read-only unknown-version probe.'
    }
    [IO.Directory]::Delete($fakeGameRoot)

    $receipt = [ordered]@{
        schema = 1
        kind = 'invokers-ru-gui-publish'
        version = $Version
        mode = 'diagnostic-preview'
        gui_apply_enabled = $false
        runtime_loader_validated = $false
        gui_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $guiPath).Hash
        cli_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $temporaryOutput 'InvokersRu.Cli.exe')).Hash
        translations_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $temporaryOutput 'ru_RU.mvp.jsonl')).Hash
        trusted_manifest_sha256 = [string]$manifestInfo.manifest_sha256
        build_id = [string]$manifestInfo.builds[0].id
    }
    $receiptPath = Join-Path $temporaryOutput 'GUI-PUBLISH.json'
    $receiptBytes = [Text.UTF8Encoding]::new($false).GetBytes(($receipt | ConvertTo-Json -Depth 10) + "`n")
    $stream = [IO.FileStream]::new($receiptPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $stream.Write($receiptBytes, 0, $receiptBytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }

    Assert-NoReparsePath -Path $temporaryOutput -Label 'Temporary GUI publish output'
    Assert-NoReparsePath -Path $outputPath -Label 'GUI publish output'
    [IO.Directory]::Move($temporaryOutput, $outputPath)
}
finally {
    Pop-Location
}

Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $outputPath 'InvokersRu.Gui.exe')
