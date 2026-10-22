<#
.SYNOPSIS
    Publish MyFirstMafAgent portable builds (console and Photino desktop).

.DESCRIPTION
    1. Build the Vue frontend (dist).
    2. dotnet publish the API or Desktop project (self-contained win-x64 by default, no AOT).
    3. Copy dist into <publish>/wwwroot.
    4. Validate pg0, the embedding model, appsettings.json and other runtime files.
    5. Optionally run Velopack to build an installer/update package.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Publish-Portable.ps1 -Target all

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Publish-Portable.ps1 -Target desktop -Velopack -Version 1.0.1
#>
param(
    [ValidateSet('console', 'desktop', 'all')]
    [string]$Target = 'all',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [bool]$SelfContained = $true,
    [switch]$SkipFrontend,
    [switch]$Velopack,
    [string]$Version = '1.0.0',
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $root 'publish' }

$frontend = Join-Path $root 'FrontEnd\myfist-agent'
$dist = Join-Path $frontend 'dist'
$apiProject = Join-Path $root 'MyFirstMafAgent.Api\MyFirstMafAgent.Api.csproj'
$desktopProject = Join-Path $root 'MyFirstMafAgent.Desktop\MyFirstMafAgent.Desktop.csproj'
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

function Assert-RuntimeFiles([string]$output, [string]$mainExe) {
    $required = @(
        $mainExe,
        'appsettings.json',
        'wwwroot\index.html',
        'ExternalTools\pgSQL\pg0-windows-x86_64.exe',
        'ExternalTools\Embedding\bge-small-zh-v1.5\model.onnx',
        'ExternalTools\Embedding\bge-small-zh-v1.5\tokenizer.json'
    )
    foreach ($relative in $required) {
        $path = Join-Path $output $relative
        if (-not (Test-Path $path)) {
            Write-Warning "Missing required file: $relative"
        }
    }
}

function Write-Readme([string]$output) {
    if (Test-Path $readmeSource) {
        Copy-Item $readmeSource (Join-Path $output 'README.txt') -Force
    }
}

Write-Step 'Check dotnet'
dotnet --version | Out-Null
Assert-ExitCode 'dotnet'

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
    Write-Step 'Publish console version'
    $output = Join-Path $OutputRoot 'console'
    Publish-Project $apiProject $output
    Copy-Frontend $output
    Assert-RuntimeFiles $output 'MyFirstMafAgent.Api.exe'
    Write-Readme $output
    Write-Host "Console output: $output" -ForegroundColor Green
}

if ($Target -eq 'desktop' -or $Target -eq 'all') {
    Write-Step 'Publish Photino desktop version'
    $output = Join-Path $OutputRoot 'desktop'
    Publish-Project $desktopProject $output
    Copy-Frontend $output
    Assert-RuntimeFiles $output 'MyFirstMafAgent.Desktop.exe'
    Write-Readme $output
    Write-Host "Desktop output: $output" -ForegroundColor Green
}

if ($Velopack) {
    if ($Target -eq 'console') {
        Write-Warning 'Velopack only applies to the desktop build; ignored for -Target=console.'
    }
    else {
        Write-Step 'Velopack packaging'
        $vpk = Get-Command vpk -ErrorAction SilentlyContinue
        if (-not $vpk) {
            $toolPath = Join-Path $env:USERPROFILE '.dotnet\tools'
            $vpkExe = Join-Path $toolPath 'vpk.exe'
            if (Test-Path $vpkExe) {
                $env:PATH = "$toolPath;$env:PATH"
                $vpk = Get-Command vpk -ErrorAction SilentlyContinue
            }
        }
        if (-not $vpk) {
            throw 'vpk was not found. Install it with: dotnet tool install -g vpk'
        }

        $desktopOutput = Join-Path $OutputRoot 'desktop'
        # 运行期数据目录不能进安装包。
        Remove-Directory (Join-Path $desktopOutput 'App_Data')
        $releaseOutput = Join-Path $OutputRoot 'releases'
        Remove-Directory $releaseOutput
        New-Item -ItemType Directory -Force $releaseOutput | Out-Null

        # Installer / shortcut icon. The desktop exe icon comes from ApplicationIcon in the csproj.
        $iconPath = Join-Path $root 'assets\app.ico'
        $iconArgs = @()
        if (Test-Path $iconPath) {
            $iconArgs = @('--icon', $iconPath)
        }
        else {
            Write-Warning "Icon not found, using default: $iconPath"
        }

        vpk pack `
            --packId MyFirstMafAgent `
            --packVersion $Version `
            --packTitle 'MyFirstMafAgent' `
            --packAuthors 'MyFirstMafAgent' `
            --packDir $desktopOutput `
            --mainExe MyFirstMafAgent.Desktop.exe `
            --runtime $Runtime `
            --outputDir $releaseOutput `
            @iconArgs
        Assert-ExitCode 'vpk pack'
        Write-Host "Velopack output: $releaseOutput" -ForegroundColor Green
        Get-ChildItem $releaseOutput -Filter '*.exe' | ForEach-Object {
            Write-Host ("Installer: {0} ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) -ForegroundColor Green
        }
    }
}

Write-Step 'Done'
Write-Host "Publish directory: $OutputRoot" -ForegroundColor Green
