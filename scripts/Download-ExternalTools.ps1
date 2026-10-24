<#
.SYNOPSIS
    Download per-platform native tools (pg0 / ffmpeg) into the ExternalTools RID folders.

.DESCRIPTION
    These binaries are large (pg0 16-60 MB, ffmpeg 30-100 MB) and are NOT committed.
    This script fetches them on demand. A failed download never breaks the build:
    the publish script only warns, and the runtime prints exactly which file is missing
    and where it should go.

    Target layout (the API finds these automatically, no renaming needed):
        ExternalTools/pgSQL/<rid>/<upstream file name>
        ExternalTools/ffmpeg/<rid>/ffmpeg[.exe]

    NOTE: keep this file ASCII-only. Windows PowerShell 5.1 misparses UTF-8 sources
    without a BOM.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Download-ExternalTools.ps1 -Target pg0

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Download-ExternalTools.ps1 -Runtime linux-x64 -Target all

.NOTES
    Downloading Linux/macOS binaries from Windows is fine (it is just file copying), but
    the executable bit must be set on the target system. The API adds +x before launching
    when the filesystem allows it.
#>
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string[]]$Runtime = @(),
    [ValidateSet('pg0', 'ffmpeg', 'all')]
    [string]$Target = 'all',
    [string]$Pg0Version = '0.15.2',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $root 'MyFirstMafAgent.Api\ExternalTools'

# Keep in sync with PlatformRuntime.Pg0AssetNames.
$pg0Assets = @{
    'win-x64'     = 'pg0-windows-x86_64.exe'
    'linux-x64'   = 'pg0-linux-x86_64-gnu'
    'linux-arm64' = 'pg0-linux-aarch64-gnu'
    'osx-arm64'   = 'pg0-darwin-aarch64'
    'osx-x64'     = 'pg0-darwin-x86_64'
}

function Get-CurrentRid {
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
    $suffix = if ($architecture -eq 'Arm64') { 'arm64' } else { 'x64' }

    if ($env:OS -eq 'Windows_NT') { return "win-$suffix" }
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) {
        return "osx-$suffix"
    }
    return "linux-$suffix"
}

if ($Runtime.Count -eq 0) { $Runtime = @(Get-CurrentRid) }

$script:failures = @()
$script:skipped = @()

function Write-Step([string]$message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Get-RemoteFile([string]$url, [string]$destination) {
    $directory = Split-Path -Parent $destination
    New-Item -ItemType Directory -Force $directory | Out-Null
    if ((Test-Path $destination) -and -not $Force) {
        Write-Host "   already present, skipped: $destination" -ForegroundColor DarkGray
        return $true
    }

    $temp = "$destination.part"
    try {
        & curl.exe -fL --retry 3 --retry-delay 2 --connect-timeout 20 -o $temp $url
        if ($LASTEXITCODE -ne 0) { throw "curl exit code $LASTEXITCODE" }

        Move-Item $temp $destination -Force
        $size = [math]::Round((Get-Item $destination).Length / 1MB, 1)
        Write-Host "   downloaded ($size MB): $destination" -ForegroundColor Green
        return $true
    }
    catch {
        if (Test-Path $temp) { Remove-Item $temp -Force -ErrorAction SilentlyContinue }
        Write-Warning "   download failed: $url`n   $($_.Exception.Message)"
        return $false
    }
}

function Install-Pg0([string]$rid) {
    if (-not $pg0Assets.ContainsKey($rid)) {
        $script:skipped += "pg0 / $rid (upstream publishes no binary for this RID)"
        return
    }

    $assetName = $pg0Assets[$rid]
    $destination = Join-Path $toolsRoot "pgSQL\$rid\$assetName"
    $url = "https://github.com/vectorize-io/pg0/releases/download/v$Pg0Version/$assetName"

    Write-Step "pg0 $rid -> ExternalTools/pgSQL/$rid/$assetName"
    if (-not (Get-RemoteFile $url $destination)) {
        $script:failures += "pg0 / $rid"
    }
}

function Install-Ffmpeg([string]$rid) {
    if ($rid -eq 'win-x64') {
        # Windows keeps its ffmpeg.exe one level up (legacy layout); nothing to download.
        $legacy = Join-Path $toolsRoot 'ffmpeg\ffmpeg.exe'
        if (Test-Path $legacy) {
            Write-Host '   Windows already uses ExternalTools/ffmpeg/ffmpeg.exe, skipped.' -ForegroundColor DarkGray
        }
        else {
            $script:skipped += 'ffmpeg / win-x64 (place ffmpeg.exe in ExternalTools/ffmpeg manually)'
        }
        return
    }

    if ($rid -eq 'linux-arm64' -or $rid -eq 'win-arm64') {
        $script:skipped += "ffmpeg / $rid (no common static build; use a package manager)"
        return
    }

    if ($rid -eq 'linux-x64') {
        Write-Step "ffmpeg $rid -> ExternalTools/ffmpeg/$rid/ffmpeg"
        $archive = Join-Path $env:TEMP 'ffmpeg-release-amd64-static.tar.xz'
        $url = 'https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz'
        if (-not (Get-RemoteFile $url $archive)) {
            $script:failures += "ffmpeg / $rid"
            return
        }

        $extract = Join-Path $env:TEMP ('ffmpeg-static-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $extract | Out-Null
        & tar.exe -xf $archive -C $extract
        if ($LASTEXITCODE -ne 0) {
            $script:failures += "ffmpeg / $rid (extract failed)"
            return
        }

        $binary = Get-ChildItem $extract -Recurse -Filter 'ffmpeg' | Select-Object -First 1
        $probe = Get-ChildItem $extract -Recurse -Filter 'ffprobe' | Select-Object -First 1
        $targetDirectory = Join-Path $toolsRoot "ffmpeg\$rid"
        New-Item -ItemType Directory -Force $targetDirectory | Out-Null
        if ($binary) { Copy-Item $binary.FullName (Join-Path $targetDirectory 'ffmpeg') -Force }
        if ($probe) { Copy-Item $probe.FullName (Join-Path $targetDirectory 'ffprobe') -Force }
        Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "   extracted to $targetDirectory" -ForegroundColor Green
        return
    }

    # macOS: evermeet ships a single-binary zip.
    Write-Step "ffmpeg $rid -> ExternalTools/ffmpeg/$rid/ffmpeg"
    $archive = Join-Path $env:TEMP "ffmpeg-$rid.zip"
    if (-not (Get-RemoteFile 'https://evermeet.cx/ffmpeg/getrelease/ffmpeg/zip' $archive)) {
        $script:failures += "ffmpeg / $rid"
        return
    }

    $targetDirectory = Join-Path $toolsRoot "ffmpeg\$rid"
    New-Item -ItemType Directory -Force $targetDirectory | Out-Null
    Expand-Archive -Path $archive -DestinationPath $targetDirectory -Force
    Write-Host "   extracted to $targetDirectory" -ForegroundColor Green
}

Write-Host "Target RIDs: $($Runtime -join ', ')" -ForegroundColor Cyan

foreach ($rid in $Runtime) {
    if ($Target -in @('pg0', 'all')) { Install-Pg0 $rid }
    if ($Target -in @('ffmpeg', 'all')) { Install-Ffmpeg $rid }
}

Write-Host ''
if ($script:skipped.Count -gt 0) {
    Write-Host 'Skipped:' -ForegroundColor Yellow
    $script:skipped | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
}

if ($script:failures.Count -gt 0) {
    Write-Warning ('Downloads failed (retry later, or drop the files in manually): ' + ($script:failures -join ', '))
    Write-Warning 'pg0: https://github.com/vectorize-io/pg0/releases'
    exit 1
}

Write-Host 'Done.' -ForegroundColor Green
