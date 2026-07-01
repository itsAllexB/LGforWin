# Builds LGforWin with Visual Studio's MSBuild.
#
# WinUI 3 PRI/MSIX generation needs the AppxPackage MSBuild task
# (Microsoft.Build.Packaging.Pri.Tasks.dll) which ships with Visual Studio, not the
# standalone .NET SDK — so `dotnet build` fails with MSB4062. We therefore build with
# VS's MSBuild, which resolves that task from the VS install.
#
# Usage:  pwsh -File build.ps1 [-Configuration Debug|Release] [-Run]

param(
    [string]$Configuration = "Debug",
    [switch]$Run
)

$ErrorActionPreference = "Stop"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -requires Microsoft.Component.MSBuild -property installationPath
if (-not $vs) { throw "Visual Studio with MSBuild not found." }

$msbuild = Join-Path $vs "MSBuild\Current\Bin\amd64\MSBuild.exe"
if (-not (Test-Path $msbuild)) { $msbuild = Join-Path $vs "MSBuild\Current\Bin\MSBuild.exe" }

$proj = Join-Path $PSScriptRoot "LGforWin.csproj"
Write-Host "Building $Configuration with $msbuild" -ForegroundColor Cyan
& $msbuild $proj -restore -p:Configuration=$Configuration -p:Platform=x64 -m

if ($Run) {
    $exe = Join-Path $PSScriptRoot "bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64\LGforWin.exe"
    if (Test-Path $exe) { Write-Host "Launching $exe" -ForegroundColor Green; & $exe }
    else { Write-Warning "Executable not found at $exe" }
}
