[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64", "AnyCPU")]
    [string]$Platform = "x64",

    [switch]$Clean,

    [string]$MSBuildPath
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root "RaceTrade.sln"
$projectDir = Join-Path $root "RaceTrade"
$assemblyInfo = Join-Path $projectDir "Properties\AssemblyInfo.cs"
$releaseBin = Join-Path $projectDir "bin\$Platform\$Configuration"
$rarExe = "C:\Program Files\WinRAR\Rar.exe"

function Find-MSBuild {
    if ($MSBuildPath) {
        if (-not (Test-Path -LiteralPath $MSBuildPath)) {
            throw "MSBuildPath does not exist: $MSBuildPath"
        }
        return (Resolve-Path -LiteralPath $MSBuildPath).Path
    }

    $knownPaths = @(
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe"
    )

    foreach ($path in $knownPaths) {
        if (Test-Path -LiteralPath $path) {
            return $path
        }
    }

    $cmd = Get-Command "MSBuild.exe" -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    throw "MSBuild.exe was not found. Install Visual Studio Build Tools or pass -MSBuildPath."
}

function Get-AssemblyVersion {
    $text = Get-Content -LiteralPath $assemblyInfo -Raw
    $match = [regex]::Match($text, 'AssemblyVersion\("(?<version>[^"]+)"\)')
    if (-not $match.Success) {
        throw "Could not read AssemblyVersion from $assemblyInfo"
    }
    return $match.Groups["version"].Value
}

if (-not (Test-Path -LiteralPath $solution)) {
    throw "Solution not found: $solution"
}

if ($Configuration -eq "Release" -and -not (Test-Path -LiteralPath $rarExe)) {
    throw "WinRAR Rar.exe not found: $rarExe"
}

$msbuild = Find-MSBuild
$target = if ($Clean) { "Clean;Build" } else { "Build" }
$version = Get-AssemblyVersion

Write-Host "RaceTrade build"
Write-Host "  Version:       $version"
Write-Host "  Configuration: $Configuration"
Write-Host "  Platform:      $Platform"
Write-Host "  MSBuild:       $msbuild"

& $msbuild $solution `
    "/t:$target" `
    "/p:Configuration=$Configuration" `
    "/p:Platform=$Platform" `
    "/verbosity:minimal"

if ($LASTEXITCODE -ne 0) {
    throw "MSBuild failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $releaseBin "RaceTrade.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Build finished, but RaceTrade.exe was not found: $exe"
}

Write-Host "Built EXE: $exe"

if ($Configuration -eq "Release") {
    $rar = Join-Path (Split-Path -Parent $releaseBin) "RaceTrade_$version.rar"
    if (-not (Test-Path -LiteralPath $rar)) {
        throw "Release build finished, but RAR was not found: $rar"
    }

    Write-Host "Built RAR: $rar"
}
