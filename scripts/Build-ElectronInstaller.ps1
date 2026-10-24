<#
.SYNOPSIS
    Build the Electron desktop installer (electron-builder + NSIS).

.DESCRIPTION
    1. (optional) publish the .NET sidecar with scripts\Publish-Portable.ps1 -Target desktop
    2. run electron-builder in desktop-electron\
    3. copy the produced artifacts into publish\releases

    Why this wrapper exists:
      The NSIS stage builds and runs a temporary installer stub, and that stub has to
      be executed. On machines where the folder is protected -- anything under
      %USERPROFILE%\Desktop, \Documents and \Downloads when Windows Defender
      Controlled Folder Access is on -- executables started from there are denied,
      and electron-builder dies with an unhelpful "process failed 2" / empty output.
      The same happens for paths that are not pure ASCII. In both cases we build in
      %TEMP%\maf-electron-build and copy the artifacts back into publish\releases
      (a plain file copy, which works everywhere).

    Note: the produced installer carries the same restriction. Run it from a normal
    folder (for example C:\Temp), not from inside this workspace when the workspace
    lives on the Desktop.

.PARAMETER SkipPublish
    Reuse the existing publish\desktop instead of re-running dotnet publish.

.PARAMETER BuildRoot
    Override the electron-builder output directory (default: publish\releases, or
    %TEMP%\maf-electron-build when the repository path cannot host the build).

.PARAMETER ForceTemporaryBuild
    Always build in %TEMP% and copy back, even if publish\releases looks usable.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Build-ElectronInstaller.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Build-ElectronInstaller.ps1 -SkipPublish
#>
param(
    [switch]$SkipPublish,
    [string]$BuildRoot = '',
    [switch]$ForceTemporaryBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$shellDir = Join-Path $root 'desktop-electron'
$releases = Join-Path $root 'publish\releases'
$sidecar = Join-Path $root 'publish\desktop'

function Write-Step([string]$message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Test-AsciiPath([string]$path) {
    return -not ($path -match '[^\x00-\x7F]')
}

function Test-ProtectedPath([string]$path) {
    # Controlled Folder Access blocks unsigned executables started from these folders.
    $userProfile = $env:USERPROFILE
    if ([string]::IsNullOrWhiteSpace($userProfile)) { return $false }
    $full = [System.IO.Path]::GetFullPath($path)
    foreach ($name in @('Desktop', 'Documents', 'Downloads')) {
        $protected = Join-Path $userProfile $name
        if ($full.StartsWith($protected, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

if (-not (Test-Path (Join-Path $shellDir 'node_modules\electron-builder'))) {
    throw "electron-builder is not installed. Run: cd desktop-electron; npm install"
}

if (-not $SkipPublish) {
    Write-Step 'Publish .NET sidecar (publish\desktop)'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Publish-Portable.ps1') -Target desktop -SkipFrontend
    if ($LASTEXITCODE -ne 0) { throw "Publish-Portable.ps1 failed (exit code $LASTEXITCODE)." }
}

if (-not (Test-Path (Join-Path $sidecar 'MyFirstMafAgent.Api.exe'))) {
    throw "Missing $sidecar\MyFirstMafAgent.Api.exe. Run without -SkipPublish."
}

$temporaryBuild = $false
if ([string]::IsNullOrWhiteSpace($BuildRoot)) {
    if (-not $ForceTemporaryBuild -and (Test-AsciiPath $root) -and -not (Test-ProtectedPath $releases)) {
        $BuildRoot = $releases
    }
    else {
        $BuildRoot = Join-Path $env:TEMP 'maf-electron-build'
        $temporaryBuild = $true
        Write-Host "This repository path cannot host the NSIS build (protected folder or non-ASCII path)." -ForegroundColor Yellow
        Write-Host "Building into $BuildRoot and copying the artifacts back." -ForegroundColor Yellow
    }
}

# Mirrors: the default GitHub download endpoints are frequently unreachable here.
# Both variables can be overridden from the environment.
if ([string]::IsNullOrWhiteSpace($env:ELECTRON_MIRROR)) {
    $env:ELECTRON_MIRROR = 'https://npmmirror.com/mirrors/electron/'
}
if ([string]::IsNullOrWhiteSpace($env:ELECTRON_BUILDER_BINARIES_MIRROR)) {
    $env:ELECTRON_BUILDER_BINARIES_MIRROR = 'https://npmmirror.com/mirrors/electron-builder-binaries/'
}

if (Test-Path $BuildRoot) { Remove-Item $BuildRoot -Recurse -Force }
New-Item -ItemType Directory -Force $BuildRoot | Out-Null

Write-Step "Run electron-builder (output: $BuildRoot)"
Push-Location $shellDir
try {
    & npx electron-builder --win --x64 --config.directories.output="$BuildRoot"
    if ($LASTEXITCODE -ne 0) { throw "electron-builder failed (exit code $LASTEXITCODE)." }
}
finally {
    Pop-Location
}

$setup = Get-ChildItem -Path $BuildRoot -Filter '*-setup.exe' -File -ErrorAction SilentlyContinue
if (-not $setup) {
    $found = (Get-ChildItem -Path $BuildRoot -File | Select-Object -ExpandProperty Name) -join ', '
    throw "electron-builder reported success but no *-setup.exe was produced. Output: $found"
}

New-Item -ItemType Directory -Force $releases | Out-Null
if ($temporaryBuild) {
    Write-Step 'Copy artifacts into publish\releases'
    Get-ChildItem -Path $releases -File | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem -Path $BuildRoot -File |
        Where-Object { $_.Extension -in '.exe', '.blockmap', '.yml', '.json' } |
        ForEach-Object {
            Copy-Item $_.FullName (Join-Path $releases $_.Name) -Force
            Write-Host ("  {0} ({1} MB)" -f $_.Name, [int]($_.Length / 1MB))
        }
}
else {
    # Same directory: only drop stale files that electron-builder no longer produces.
    Get-ChildItem -Path $releases -File |
        Where-Object { $_.Extension -notin '.exe', '.blockmap', '.yml', '.json' } |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

Write-Step 'Done'
foreach ($file in Get-ChildItem -Path $releases -Filter '*-setup.exe' -File) {
    Write-Host ("Installer: {0} ({1} MB)" -f $file.FullName, [int]($file.Length / 1MB)) -ForegroundColor Green
}
Write-Host 'Install   (silent, per user): <installer> /S' -ForegroundColor Yellow
Write-Host 'Uninstall (silent):           "%LOCALAPPDATA%\Programs\<ProductName>\Uninstall <ProductName>.exe" /S' -ForegroundColor Yellow
if (Test-ProtectedPath $releases) {
    Write-Host 'Copy the installer to a non-protected folder before running it (e.g. C:\Temp).' -ForegroundColor Yellow
}
