Set-StrictMode -Version 3.0

function Test-JsonIntegerValue {
    param($Value)

    if ($Value -is [byte] -or $Value -is [sbyte] -or
        $Value -is [int16] -or $Value -is [uint16] -or
        $Value -is [int32] -or $Value -is [uint32] -or
        $Value -is [int64] -or $Value -is [uint64]) {
        return $true
    }
    if ($Value -is [decimal]) {
        return [decimal]::Truncate($Value) -eq $Value
    }
    if ($Value -is [double]) {
        return -not [double]::IsNaN($Value) -and -not [double]::IsInfinity($Value) -and
            [Math]::Truncate($Value) -eq $Value
    }
    if ($Value -is [single]) {
        return -not [single]::IsNaN($Value) -and -not [single]::IsInfinity($Value) -and
            [Math]::Truncate([double]$Value) -eq [double]$Value
    }
    return $false
}

function Get-WindowsPayloadRequiredPaths {
    return @(
        'InvokersRu.Gui.exe',
        'InvokersRu.Cli.exe',
        'InvokersRu.Gui.runtimeconfig.json',
        'InvokersRu.Cli.runtimeconfig.json',
        'coreclr.dll',
        'hostfxr.dll',
        'hostpolicy.dll',
        'translations/ru_RU.jsonl',
        'profiles/runtime-cache-profile.0.60.1247.json',
        'BUILD-RECEIPT.json',
        'LICENSE.txt',
        'README.txt'
    )
}

function Get-WindowsPayloadProjectBinaries {
    return @(
        'InvokersRu.Gui.exe',
        'InvokersRu.Gui.dll',
        'InvokersRu.Cli.exe',
        'InvokersRu.Cli.dll',
        'InvokersRu.Core.dll'
    )
}

function Assert-WindowsPayloadRelativePath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or $RelativePath.Length -gt 240 -or
        $RelativePath.Contains('\') -or $RelativePath.StartsWith('/', [StringComparison]::Ordinal) -or
        $RelativePath.EndsWith('/', [StringComparison]::Ordinal) -or $RelativePath.Contains('//') -or
        $RelativePath.IndexOfAny([IO.Path]::GetInvalidPathChars()) -ge 0) {
        throw "Unsafe payload-relative path: $RelativePath"
    }

    $components = $RelativePath.Split([char]'/')
    foreach ($component in $components) {
        if ($component.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
            $component -eq '.' -or $component -eq '..' -or
            $component.EndsWith(' ', [StringComparison]::Ordinal) -or
            $component.EndsWith('.', [StringComparison]::Ordinal)) {
            throw "Unsafe payload path component in: $RelativePath"
        }
        $deviceStem = $component.Split([char]'.')[0]
        if ($deviceStem -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
            throw "Reserved Windows device name in payload path: $RelativePath"
        }
    }

    $fileName = $components[$components.Length - 1]
    $extension = [IO.Path]::GetExtension($fileName)
    $allowed = switch -CaseSensitive ($extension) {
        '.exe' { @('InvokersRu.Gui.exe', 'InvokersRu.Cli.exe') -ccontains $RelativePath; break }
        '.dll' { $true; break }
        '.jsonl' { $RelativePath -ceq 'translations/ru_RU.jsonl'; break }
        '.txt' { @('LICENSE.txt', 'README.txt') -ccontains $RelativePath; break }
        '.json' {
            $RelativePath -ceq 'BUILD-RECEIPT.json' -or
                $RelativePath -ceq 'profiles/runtime-cache-profile.0.60.1247.json' -or
                ($components.Length -eq 1 -and
                    ($fileName.EndsWith('.deps.json', [StringComparison]::Ordinal) -or
                     $fileName.EndsWith('.runtimeconfig.json', [StringComparison]::Ordinal)))
            break
        }
        default { $false; break }
    }
    if (-not $allowed) {
        throw "Payload path or file type is outside the Windows allowlist: $RelativePath"
    }
}
