Set-StrictMode -Version 3.0

function Test-CanonicalPathWithin {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Directory
    )

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $fullDirectory = [IO.Path]::GetFullPath($Directory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ($fullPath.Equals($fullDirectory, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $fullPath.StartsWith($fullDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparsePath {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($root)) { throw "$Label has no path root: $fullPath" }
    $current = $root
    $remainder = $fullPath.Substring($root.Length)
    $separators = [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    foreach ($component in $remainder.Split($separators, [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = [IO.Path]::Combine($current, $component)
        try {
            $attributes = [IO.File]::GetAttributes($current)
        }
        catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] {
            continue
        }

        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label traverses a reparse point: $current"
        }
    }
}

function Get-ProtectedRuntimeRoots {
    $roots = [Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($env:APPDATA)) {
        $roots.Add((Join-Path $env:APPDATA 'zone.hitzone.invokers.launcher\game'))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $roots.Add((Join-Path $env:LOCALAPPDATA 'Programs\Invokers Titan Legacy'))
        $roots.Add((Join-Path $env:LOCALAPPDATA 'InvokersRussian'))
    }
    return $roots
}

function Assert-OutsideProtectedRuntime {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label
    )

    foreach ($protectedRoot in Get-ProtectedRuntimeRoots) {
        if (Test-CanonicalPathWithin -Path $Path -Directory $protectedRoot) {
            throw "$Label must remain outside the installed game, launcher, and patch state directories: $Path"
        }
    }
}

function Assert-SafeNewOutputPath {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if ([string]::IsNullOrWhiteSpace([IO.Path]::GetFileName($fullPath))) {
        throw "$Label must name a new file or directory below a filesystem root: $fullPath"
    }
    if ([IO.File]::Exists($fullPath) -or [IO.Directory]::Exists($fullPath)) {
        throw "$Label already exists: $fullPath"
    }

    Assert-OutsideProtectedRuntime -Path $fullPath -Label $Label
    Assert-NoReparsePath -Path $fullPath -Label $Label
    return $fullPath
}
