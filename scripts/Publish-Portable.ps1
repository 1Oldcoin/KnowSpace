<#
.SYNOPSIS
    Publish MyFirstMafAgent portable builds (console and Electron sidecar).

.DESCRIPTION
    1. Build the Vue frontend (dist).
    2. dotnet publish the API project for the target RID (self-contained by default, no AOT).
    3. Copy dist into <publish>/wwwroot.
    4. Drop native tools that belong to OTHER platforms, then check the ones this
       platform needs (pg0, embedding model, and optionally ffmpeg).

    Two publish shapes only:
      - console : the API is the application (opens the default browser).
      - desktop : the same API, consumed by the Electron shell in `desktop-electron/`
                  as a sidecar. Not needed for Docker.

    Cross-platform note
    -------------------
    `ExternalTools` keeps one sub-folder per RID (`pgSQL/<rid>/`, `ffmpeg/<rid>/`).
    The API resolves them at runtime from PlatformRuntime.RuntimeIdentifier, so a single
    appsettings.json works on every platform. Missing binaries are WARNINGS, not errors:
    a Linux container usually talks to an external PostgreSQL and installs ffmpeg via apt.

    NOTE: keep this file ASCII-only. Windows PowerShell 5.1 misparses UTF-8 sources
    without a BOM, which silently breaks string literals and warnings.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Publish-Portable.ps1 -Target all

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Publish-Portable.ps1 -Target console -Runtime linux-x64

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Publish-Portable.ps1 -Target console -Runtime osx-arm64 -KeepForeignRids
#>
param(
    [ValidateSet('console', 'desktop', 'all')]
    [string]$Target = 'all',
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Runtime = 'win-x64',
    [bool]$SelfContained = $true,
    [switch]$SkipFrontend,
    # Keep native binaries of other platforms (default: prune them, they are 100-300 MB).
    [switch]$KeepForeignRids,
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $root 'publish' }

$frontend = Join-Path $root 'FrontEnd\myfist-agent'
$dist = Join-Path $frontend 'dist'
$apiProject = Join-Path $root 'MyFirstMafAgent.Api\MyFirstMafAgent.Api.csproj'
$readmeSource = Join-Path $PSScriptRoot 'README-portable.txt'

function Write-Step([string]$message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Assert-ExitCode([string]$what) {
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

function Remove-Directory([string]$path) {
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

function Publish-Project([string]$project, [string]$output) {
    Remove-Directory $output
    New-Item -ItemType Directory -Force $output | Out-Null

    $arguments = @(
        'publish', $project,
        '-c', $Configuration,
        '-r', $Runtime,
        '--self-contained', $SelfContained.ToString().ToLowerInvariant(),
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        '--nologo',
        '-o', $output
    )
    dotnet @arguments
    Assert-ExitCode "dotnet publish $project"
}

function Copy-Frontend([string]$output) {
    if (-not (Test-Path (Join-Path $dist 'index.html'))) {
        throw "Missing $dist\index.html. Build the frontend first or omit -SkipFrontend."
    }
    $wwwroot = Join-Path $output 'wwwroot'
    Remove-Directory $wwwroot
    New-Item -ItemType Directory -Force $wwwroot | Out-Null
    Copy-Item (Join-Path $dist '*') $wwwroot -Recurse -Force
}

function Write-Readme([string]$output) {
    if (Test-Path $readmeSource) {
        Copy-Item $readmeSource (Join-Path $output 'README.txt') -Force
    }
}

# win-x64 keeps the historical folder names (`console` / `desktop`) because the Electron
# packaging scripts depend on them; other RIDs get a suffix so several platforms can be
# published on the same machine without overwriting each other.
function Get-PublishFolder([string]$shape) {
    if ($Runtime -eq 'win-x64') { return $shape }
    return "$shape-$Runtime"
}

function Get-Pg0Candidates {
    switch ($Runtime) {
        'win-x64' {
            return @(
                'ExternalTools\pgSQL\win-x64\pg0-windows-x86_64.exe',
                'ExternalTools\pgSQL\win-x64\pg0.exe',
                'ExternalTools\pgSQL\pg0-windows-x86_64.exe',
                'ExternalTools\pgSQL\pg0-win-x64.exe')
        }
        'linux-x64' {
            return @(
                'ExternalTools\pgSQL\linux-x64\pg0-linux-x86_64-gnu',
                'ExternalTools\pgSQL\linux-x64\pg0-linux-x86_64-musl',
                'ExternalTools\pgSQL\linux-x64\pg0',
                'ExternalTools\pgSQL\pg0-linux-x86_64-gnu',
                'ExternalTools\pgSQL\pg0-linux-x64')
        }
        'linux-arm64' {
            return @(
                'ExternalTools\pgSQL\linux-arm64\pg0-linux-aarch64-gnu',
                'ExternalTools\pgSQL\linux-arm64\pg0-linux-aarch64-musl',
                'ExternalTools\pgSQL\linux-arm64\pg0',
                'ExternalTools\pgSQL\pg0-linux-aarch64-gnu',
                'ExternalTools\pgSQL\pg0-linux-arm64')
        }
        'osx-arm64' {
            return @(
                'ExternalTools\pgSQL\osx-arm64\pg0-darwin-aarch64',
                'ExternalTools\pgSQL\osx-arm64\pg0',
                'ExternalTools\pgSQL\pg0-darwin-aarch64',
                'ExternalTools\pgSQL\pg0-osx-arm64')
        }
        'osx-x64' {
            return @(
                'ExternalTools\pgSQL\osx-x64\pg0-darwin-x86_64',
                'ExternalTools\pgSQL\osx-x64\pg0',
                'ExternalTools\pgSQL\pg0-darwin-x86_64',
                'ExternalTools\pgSQL\pg0-osx-x64')
        }
        default { return @() }
    }
}

function Assert-RuntimeFiles([string]$output) {
    $exeSuffix = if ($Runtime -like 'win-*') { '.exe' } else { '' }
    $mainExe = "MyFirstMafAgent.Api$exeSuffix"

    foreach ($relative in @($mainExe, 'appsettings.json', 'wwwroot\index.html',
            'ExternalTools\Embedding\bge-small-zh-v1.5\model.onnx',
            'ExternalTools\Embedding\bge-small-zh-v1.5\tokenizer.json')) {
        if (-not (Test-Path (Join-Path $output $relative))) {
            Write-Warning "Missing required file: $relative"
        }
    }

    $pg0 = (Get-Pg0Candidates) | Where-Object { Test-Path (Join-Path $output $_) } | Select-Object -First 1
    if ($pg0) {
        Write-Host "  pg0: $pg0" -ForegroundColor DarkGray
    }
    else {
        Write-Warning "No portable PostgreSQL (pg0) binary for $Runtime."
        Write-Warning "  Runtime will report which candidate paths it tried."
        Write-Warning "  Fetch it: powershell -ExecutionPolicy Bypass -File scripts\Download-ExternalTools.ps1 -Runtime $Runtime -Target pg0"
        Write-Warning "  Or point ConnectionStrings__KnowledgeBase at an external PostgreSQL (recommended in Docker)."
    }

    $ffmpegCandidates = if ($Runtime -like 'win-*') {
        @("ExternalTools\ffmpeg\$Runtime\ffmpeg.exe", 'ExternalTools\ffmpeg\ffmpeg.exe')
    }
    else {
        @("ExternalTools\ffmpeg\$Runtime\ffmpeg", 'ExternalTools\ffmpeg\ffmpeg')
    }

    $ffmpeg = $ffmpegCandidates | Where-Object { Test-Path (Join-Path $output $_) } | Select-Object -First 1
    if ($ffmpeg) {
        Write-Host "  ffmpeg: $ffmpeg" -ForegroundColor DarkGray
    }
    else {
        Write-Warning "No ffmpeg for $Runtime (only affects video input; PATH ffmpeg is used as fallback)."
    }
}

# Remove native binaries that belong to other platforms: pg0 is 16-60 MB, each ffmpeg 100 MB.
# Rules:
#   - delete RID sub-folders other than $Runtime;
#   - non-Windows target: delete root-level *.exe (legacy flat Windows layout);
#   - Windows target: delete root-level extension-less binaries.
function Remove-ForeignRids([string]$output) {
    if ($KeepForeignRids) {
        Write-Host '  -KeepForeignRids: keeping other platforms' -ForegroundColor Yellow
        return
    }

    $removed = 0
    foreach ($tool in @('pgSQL', 'ffmpeg')) {
        $toolRoot = Join-Path $output "ExternalTools\$tool"
        if (-not (Test-Path $toolRoot)) { continue }

        foreach ($directory in Get-ChildItem $toolRoot -Directory) {
            if ($directory.Name -eq $Runtime) { continue }
            if ($directory.Name -notmatch '^(win|linux|osx)-') { continue }
            Remove-Item $directory.FullName -Recurse -Force
            $removed++
        }

        foreach ($file in Get-ChildItem $toolRoot -File) {
            $isWindowsBinary = $file.Extension -eq '.exe'
            $isUnixBinary = [string]::IsNullOrEmpty($file.Extension) -and $file.Name -notlike '*.md'
            if ($Runtime -notlike 'win-*' -and $isWindowsBinary) {
                Remove-Item $file.FullName -Force
                $removed++
            }
            elseif ($Runtime -like 'win-*' -and $isUnixBinary) {
                Remove-Item $file.FullName -Force
                $removed++
            }
        }
    }

    Write-Host "  pruned $removed other-platform file(s)/folder(s)" -ForegroundColor DarkGray
}

Write-Step 'Check dotnet'
dotnet --version | Out-Null
Assert-ExitCode 'dotnet'

Write-Host "Target RID: $Runtime (self-contained: $SelfContained)" -ForegroundColor Cyan

if (-not $SkipFrontend) {
    Write-Step 'Build frontend (Vue 3 + Vite)'
    Push-Location $frontend
    try {
        if (-not (Test-Path 'node_modules')) {
            npm install
            Assert-ExitCode 'npm install'
        }
        npm run build
        Assert-ExitCode 'npm run build'
    }
    finally {
        Pop-Location
    }
}
else {
    Write-Step 'Skip frontend build (reuse existing dist)'
}

if (-not (Test-Path (Join-Path $dist 'index.html'))) {
    throw "Frontend build output is missing: $dist\index.html"
}

if ($Target -eq 'console' -or $Target -eq 'all') {
    Write-Step "Publish console version ($Runtime)"
    $output = Join-Path $OutputRoot (Get-PublishFolder 'console')
    Publish-Project $apiProject $output
    Copy-Frontend $output
    Remove-ForeignRids $output
    Assert-RuntimeFiles $output
    Write-Readme $output
    Write-Host "Console output: $output" -ForegroundColor Green
}

if ($Target -eq 'desktop' -or $Target -eq 'all') {
    # The Electron main process spawns this as a sidecar (see desktop-electron/main.js:
    # process.resourcesPath/api and the publish/desktop fallback), so what we publish is
    # the API itself, not a .NET desktop shell.
    Write-Step "Publish Electron sidecar version ($Runtime)"
    $output = Join-Path $OutputRoot (Get-PublishFolder 'desktop')
    Publish-Project $apiProject $output
    Copy-Frontend $output
    Remove-ForeignRids $output
    Assert-RuntimeFiles $output
    Write-Readme $output
    Write-Host "Sidecar output: $output" -ForegroundColor Green
    Write-Host 'Next: cd desktop-electron; npm run dist   (electron-builder -> publish\releases)' -ForegroundColor Yellow
    Write-Host '      full chain: cd desktop-electron; npm run dist:full' -ForegroundColor Yellow
}

Write-Step 'Done'
Write-Host "Publish directory: $OutputRoot" -ForegroundColor Green
