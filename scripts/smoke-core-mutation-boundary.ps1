[CmdletBinding()]
param(
    [string] $DotNetPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
    $DotNetPath = Join-Path $projectRoot 'work\dotnet-10\dotnet.exe'
}
$dotnet = (Resolve-Path -LiteralPath $DotNetPath).Path
$coreProject = Join-Path $projectRoot 'src\InvokersRu.Core\InvokersRu.Core.csproj'
$nugetConfig = Join-Path $projectRoot 'NuGet.Config'
$trustedCompatibility = Join-Path $projectRoot 'config\compatibility.v1.json'
$trustedRuntimeProfile = Join-Path $projectRoot 'src\InvokersRu.SmokeTests\Fixtures\runtime-cache-profile.observed.v1.json'
$offlinePackages = Join-Path $projectRoot 'work\dotnet-home-10\.nuget\packages'
$auditRoot = Join-Path $projectRoot 'work\audit-negative\core-mutation-boundary'
$productionBin = Join-Path $projectRoot 'work\supervised-write-bin\core'
$productionObj = Join-Path $projectRoot 'work\supervised-write-obj\core'
$testBin = Join-Path $projectRoot 'work\test-write-bin\core'
$testObj = Join-Path $projectRoot 'work\test-write-obj\core'

foreach ($requiredPath in @($coreProject, $nugetConfig, $trustedCompatibility, $trustedRuntimeProfile)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required boundary-smoke input is missing: $requiredPath"
    }
}
if (-not (Test-Path -LiteralPath $offlinePackages -PathType Container)) {
    throw "Offline package cache is missing: $offlinePackages"
}

function Invoke-RejectedCoreBuild {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string[]] $Properties,
        [Parameter(Mandatory = $true)][string] $ExpectedDiagnostic
    )

    $arguments = @(
        'build', $coreProject,
        '--configuration', 'Release',
        '--no-restore',
        '--nologo',
        '--verbosity', 'minimal',
        '-t:Rebuild'
    ) + $Properties
    $output = @(& $dotnet @arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $rendered = $output -join [Environment]::NewLine
    if ($exitCode -eq 0) {
        throw "Boundary case '$Name' unexpectedly built a mutation-capable Core."
    }
    if ($rendered.IndexOf($ExpectedDiagnostic, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Boundary case '$Name' failed for the wrong reason. Expected diagnostic '$ExpectedDiagnostic'.`n$rendered"
    }
    Write-Host "PASS (rejected): $Name"
}

function Invoke-SuccessfulDotNet {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string[]] $Arguments
    )

    $output = @(& $dotnet @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Boundary case '$Name' unexpectedly failed.`n$($output -join [Environment]::NewLine)"
    }
    Write-Host "PASS (accepted): $Name"
}

function Test-IsolationMode {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string[]] $ModeProperties,
        [Parameter(Mandatory = $true)][string] $ExpectedBaseOutput,
        [Parameter(Mandatory = $true)][string] $ExpectedBaseIntermediate,
        [Parameter(Mandatory = $true)][string] $ExpectedBaseOutputProperty,
        [Parameter(Mandatory = $true)][string] $ExpectedBaseIntermediateProperty
    )

    $badBaseOutput = Join-Path $ExpectedBaseOutput '..\escape'
    $badBaseIntermediate = Join-Path $ExpectedBaseIntermediate '..\escape'
    $redirectRoot = Join-Path $auditRoot $Name
    $assemblyTraversal = '..\..\..\..\src\InvokersRu.Core\bin\Release\net10.0\InvokersRu.Core'

    Invoke-RejectedCoreBuild "$Name BaseOutputPath traversal" `
        ($ModeProperties + "-p:BaseOutputPath=$badBaseOutput") 'BaseOutputPath'
    Invoke-RejectedCoreBuild "$Name BaseOutputPath traversal plus normalized spoof" `
        ($ModeProperties + "-p:BaseOutputPath=$badBaseOutput" + "-p:_NormalizedBaseOutputPath=$ExpectedBaseOutput") 'BaseOutputPath'
    Invoke-RejectedCoreBuild "$Name BaseOutputPath traversal plus expected-path spoof" `
        ($ModeProperties + "-p:BaseOutputPath=$badBaseOutput" + "-p:$ExpectedBaseOutputProperty=$badBaseOutput") 'BaseOutputPath'

    Invoke-RejectedCoreBuild "$Name BaseIntermediateOutputPath traversal" `
        ($ModeProperties + "-p:BaseIntermediateOutputPath=$badBaseIntermediate") 'BaseIntermediateOutputPath'
    Invoke-RejectedCoreBuild "$Name BaseIntermediateOutputPath traversal plus normalized spoof" `
        ($ModeProperties + "-p:BaseIntermediateOutputPath=$badBaseIntermediate" + "-p:_NormalizedBaseIntermediateOutputPath=$ExpectedBaseIntermediate") 'BaseIntermediateOutputPath'
    Invoke-RejectedCoreBuild "$Name BaseIntermediateOutputPath traversal plus expected-path spoof" `
        ($ModeProperties + "-p:BaseIntermediateOutputPath=$badBaseIntermediate" + "-p:$ExpectedBaseIntermediateProperty=$badBaseIntermediate") 'BaseIntermediateOutputPath'

    Invoke-RejectedCoreBuild "$Name OutputPath redirect" `
        ($ModeProperties + "-p:OutputPath=$(Join-Path $redirectRoot 'output')") 'OutputPath'
    Invoke-RejectedCoreBuild "$Name OutDir redirect" `
        ($ModeProperties + "-p:OutDir=$(Join-Path $redirectRoot 'outdir')") 'OutDir'
    Invoke-RejectedCoreBuild "$Name IntermediateOutputPath redirect" `
        ($ModeProperties + "-p:IntermediateOutputPath=$(Join-Path $redirectRoot 'intermediate')") 'IntermediateOutputPath'
    Invoke-RejectedCoreBuild "$Name MSBuildProjectExtensionsPath redirect" `
        ($ModeProperties + "-p:MSBuildProjectExtensionsPath=$(Join-Path $redirectRoot 'extensions')") 'MSBuildProjectExtensionsPath'
    Invoke-RejectedCoreBuild "$Name AssemblyName traversal to ordinary output" `
        ($ModeProperties + "-p:AssemblyName=$assemblyTraversal") 'fixed assembly and target name'
    Invoke-RejectedCoreBuild "$Name TargetName traversal to ordinary output" `
        ($ModeProperties + "-p:TargetName=$assemblyTraversal") 'fixed assembly and target name'
    Invoke-RejectedCoreBuild "$Name TargetFileName traversal" `
        ($ModeProperties + '-p:TargetFileName=..\..\..\escaped-core.dll') 'fixed target extension and file name'
    Invoke-RejectedCoreBuild "$Name TargetDir redirect" `
        ($ModeProperties + "-p:TargetDir=$(Join-Path $redirectRoot 'target-dir')") 'TargetDir'
    Invoke-RejectedCoreBuild "$Name TargetPath redirect" `
        ($ModeProperties + "-p:TargetPath=$(Join-Path $redirectRoot 'target-path\InvokersRu.Core.dll')") 'TargetPath'
    Invoke-RejectedCoreBuild "$Name TargetRefPath redirect" `
        ($ModeProperties + "-p:TargetRefPath=$(Join-Path $redirectRoot 'target-ref\InvokersRu.Core.dll')") 'target reference path'
    Invoke-RejectedCoreBuild "$Name RuntimeIdentifier traversal" `
        ($ModeProperties + '-p:RuntimeIdentifier=..\ordinary') 'empty RuntimeIdentifier or exact win-x64'
}

$productionMode = @(
    '-p:EnableCoreMutations=true',
    "-p:TrustedCompatibilityPath=$trustedCompatibility",
    "-p:TrustedRuntimeCacheCompatibilityPath=$trustedRuntimeProfile"
)
$testMode = @('-p:EnableCoreTestMutations=true')
$symbolOutput = Join-Path $auditRoot 'symbol-bin'

Push-Location $projectRoot
try {
    Invoke-RejectedCoreBuild 'raw production reserved symbol' `
        @('-p:DefineConstants=INVOKERSRU_CORE_MUTATIONS', "-p:BaseOutputPath=$symbolOutput") 'Reserved Core mutation symbols'
    Invoke-RejectedCoreBuild 'raw test reserved symbol' `
        @('-p:DefineConstants=INVOKERSRU_CORE_TEST_MUTATIONS', "-p:BaseOutputPath=$symbolOutput") 'Reserved Core mutation symbols'
    Invoke-RejectedCoreBuild 'raw production symbol with cached-property spoofs' `
        @('-p:DefineConstants=INVOKERSRU_CORE_MUTATIONS', '-p:_IncomingDefineConstants=SAFE', '-p:_InjectedCoreMutationSymbol=False', "-p:BaseOutputPath=$symbolOutput") 'Reserved Core mutation symbols'
    Invoke-RejectedCoreBuild 'raw test symbol with cached-property spoofs' `
        @('-p:DefineConstants=INVOKERSRU_CORE_TEST_MUTATIONS', '-p:_IncomingDefineConstants=SAFE', '-p:_InjectedCoreMutationSymbol=False', "-p:BaseOutputPath=$symbolOutput") 'Reserved Core mutation symbols'

    Test-IsolationMode 'production' $productionMode $productionBin $productionObj `
        '_ExpectedSupervisedBaseOutputPath' '_ExpectedSupervisedBaseIntermediateOutputPath'
    Test-IsolationMode 'test-write' $testMode $testBin $testObj `
        '_ExpectedTestBaseOutputPath' '_ExpectedTestBaseIntermediateOutputPath'

    Invoke-SuccessfulDotNet 'production isolated restore' `
        (@('restore', $coreProject, '--configfile', $nugetConfig, '--nologo', '--verbosity', 'minimal') + $productionMode)
    Invoke-SuccessfulDotNet 'production exact isolated build' `
        (@('build', $coreProject, '--configuration', 'Release', '--no-restore', '--nologo', '--verbosity', 'minimal', '-t:Rebuild') + $productionMode)
    Invoke-SuccessfulDotNet 'production win-x64 isolated restore' `
        (@('restore', $coreProject, '--runtime', 'win-x64', '--packages', $offlinePackages, '--configfile', $nugetConfig, '--nologo', '--verbosity', 'minimal') + $productionMode)
    Invoke-SuccessfulDotNet 'production win-x64 exact isolated build' `
        (@('build', $coreProject, '--configuration', 'Release', '--runtime', 'win-x64', '--no-restore', '--nologo', '--verbosity', 'minimal', '-t:Rebuild') + $productionMode)
    Invoke-SuccessfulDotNet 'test-write isolated restore' `
        (@('restore', $coreProject, '--configfile', $nugetConfig, '--nologo', '--verbosity', 'minimal') + $testMode)
    Invoke-SuccessfulDotNet 'test-write exact isolated build' `
        (@('build', $coreProject, '--configuration', 'Release', '--no-restore', '--nologo', '--verbosity', 'minimal', '-t:Rebuild') + $testMode)
    Invoke-SuccessfulDotNet 'ordinary disabled-Core restore' `
        @('restore', $coreProject, '--configfile', $nugetConfig, '--nologo', '--verbosity', 'minimal')
    Invoke-SuccessfulDotNet 'ordinary disabled-Core rebuild after privileged variants' `
        @('build', $coreProject, '--configuration', 'Release', '--no-restore', '--nologo', '--verbosity', 'minimal', '-t:Rebuild')
}
finally {
    Pop-Location
}

Write-Host 'PASS: Core mutation capability and output-isolation adversarial matrix'
