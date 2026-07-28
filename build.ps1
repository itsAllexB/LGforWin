# Builds LGforWin with Visual Studio's MSBuild.
#
# WinUI 3 PRI/MSIX generation needs the AppxPackage MSBuild task
# (Microsoft.Build.Packaging.Pri.Tasks.dll) which ships with Visual Studio, not the
# standalone .NET SDK — so `dotnet build` fails with MSB4062. We therefore build with
# VS's MSBuild, which resolves that task from the VS install.
#
# Usage:  pwsh -File build.ps1 [-Configuration Debug|Release] [-Run] [-Package]
#
# -Package additionally produces, in dist\:
#   LGforWin-<version>-win-x64.zip   portable build
#   LGforWin-<version>-setup.exe     Inno Setup installer (needs Inno Setup 6)

param(
    [string]$Configuration = "Debug",
    [switch]$Run,
    [switch]$Package
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

$outDir = Join-Path $PSScriptRoot "bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64"
$exe = Join-Path $outDir "LGforWin.exe"

if ($Package) {
    if (-not (Test-Path $exe)) { throw "Build output not found at $exe" }

    # Single source of truth for the version: whatever the built exe reports.
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion -replace '\+.*$', ''
    $dist = Join-Path $PSScriptRoot "dist"
    New-Item -ItemType Directory -Force $dist | Out-Null

    $zip = Join-Path $dist "LGforWin-$version-win-x64.zip"
    Write-Host "Packing $zip" -ForegroundColor Cyan
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zip -CompressionLevel Optimal

    # winget installs Inno Setup per-user or per-machine depending on the host, so check both.
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    if (-not $iscc) {
        Write-Warning "Inno Setup 6 not found - skipping setup.exe. Install with: winget install JRSoftware.InnoSetup"
    } else {
        Write-Host "Building installer with $iscc" -ForegroundColor Cyan
        & $iscc "/DAppVersion=$version" `
                "/DSourceDir=bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64" `
                (Join-Path $PSScriptRoot "installer.iss")
        if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }
    }

    Get-ChildItem $dist | ForEach-Object {
        "{0,-42} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)
    }
}

if ($Run) {
    if (Test-Path $exe) { Write-Host "Launching $exe" -ForegroundColor Green; & $exe }
    else { Write-Warning "Executable not found at $exe" }
}
