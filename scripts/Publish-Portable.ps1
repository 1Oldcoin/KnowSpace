<#
.SYNOPSIS
    Publish MyFirstMafAgent portable builds (console and Electron sidecar).

.DESCRIPTION
    1. Build the Vue frontend (dist).
    2. dotnet publish the API project for the target RID (self-contained by default, no AOT).
    3. Copy dist into <publish>/wwwroot.
    4. Drop native tools that belong to OTHER platforms, then check the ones this
       platform needs (portable PostgreSQL, embedding model, and optionally ffmpeg).

    Two publish shapes only:
      - console : the API is the application (opens the default browser).
      - desktop : the same API, consumed by the Electron shell in `desktop-electron/`
                  as a sidecar. Not needed for Docker.

    Cross-platform note
    -------------------
    `ExternalTools` keeps one sub-folder per RID (`pgSQL/<rid>/`, `ffmpeg/<rid>/`).
    `pgSQL/<rid>/` holds a COMPLETE portable PostgreSQL tree: `bin/` (initdb, pg_ctl,
    postgres, psql ...), `lib/` (incl. `vector.dll`, the pgvector extension) and
    `share/` (incl. `extension/vector.control`). The three directories are one unit and
    must be copied whole; the API manages the database lifecycle itself with
    `initdb` / `pg_ctl`, so a lone `bin/` is not enough. A flat `pgSQL/bin/` layout is
    also accepted. Runtime lookup order: `PortablePostgres:BinDirectory` ->
    `ExternalTools/pgSQL/<rid>/bin` -> `ExternalTools/pgSQL/bin`, so a single
    appsettings.json works on every platform. Missing binaries are WARNINGS, not
    errors: a Linux container usually talks to an external PostgreSQL and installs
    ffmpeg via apt.

    NOTE: keep this file ASCII-only. Windows PowerShell 5.1 misparses UTF-8 sources
    without a BOM, which silently breaks string literals and warnings.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Publish-Portable.ps1 -Target all

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Publish-Portable.ps1 -Target console -Runtime linux-x64

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Publish-Portable.ps1 -Target console -Runtime osx-arm64 -KeepForeignRids

.EXAMPLE
    # Republish over an existing folder. App_Data (uploads / plugins / media) is kept
    # unless you explicitly pass -FreshData.
    powershell -ExecutionPolicy Bypass -File scripts\Publish-Portable.ps1 -Target console -SkipFrontend
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
    # By default the existing App_Data (uploads, plugins, media assets) is preserved.
    # Pass this switch to wipe it and start from an empty data folder.
    [switch]$FreshData,
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

# Republishing into an existing folder used to wipe App_Data, which is where uploads,
# installed plugins and media assets live. The database rows survive (they live in
# %LOCALAPPDATA%) so the result was silent data loss: documents listed but unopenable.
# We therefore move App_Data aside before publishing and merge it back afterwards.
function Publish-Project([string]$project, [string]$output) {
    $dataBackup = $null
    $dataPath = Join-Path $output 'App_Data'
    if (-not $FreshData -and (Test-Path $dataPath)) {
        $dataBackup = "$output.App_Data.bak"
        Remove-Directory $dataBackup
        Move-Item $dataPath $dataBackup -Force
        Write-Host "  preserving existing App_Data ($dataBackup)" -ForegroundColor DarkGray
    }

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

    if ($dataBackup -and (Test-Path $dataBackup)) {
        # Merge: the freshly published App_Data only carries placeholder folders,
        # so moving the backup on top of it restores every user file.
        if (Test-Path $dataPath) { Remove-Directory $dataPath }
        Move-Item $dataBackup $dataPath -Force
        Write-Host "  restored App_Data" -ForegroundColor DarkGray
    }
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

# Portable PostgreSQL is no longer a single third-party launcher binary: it is a full
# pgsql directory tree (bin + lib + share) that the API drives with initdb / pg_ctl.
# So what we probe for is a bin DIRECTORY, not a file name. Paths are relative to the
# publish output root. Same order as the runtime resolution (PortablePostgres:BinDirectory
# first, then the RID folder, then the flat layout).
function Get-PostgresBinCandidates {
    return @(
        "ExternalTools\pgSQL\$Runtime\bin",
        'ExternalTools\pgSQL\bin')
}

function Assert-RuntimeFiles([string]$output) {
    $exeSuffix = if ($Runtime -like 'win-*') { '.exe' } else { '' }
    $mainExe = "Zhijian.Api$exeSuffix"

    foreach ($relative in @($mainExe, 'appsettings.json', 'wwwroot\index.html',
            'ExternalTools\Embedding\bge-small-zh-v1.5\model.onnx',
            'ExternalTools\Embedding\bge-small-zh-v1.5\tokenizer.json')) {
        if (-not (Test-Path (Join-Path $output $relative))) {
            Write-Warning "Missing required file: $relative"
        }
    }

    # Portable PostgreSQL: a whole pgsql tree, so we look for a real server binary inside
    # the bin directory. `postgres` is the one file that proves bin/ is actually populated
    # (an empty placeholder bin/ counts as missing).
    $postgresBinary = "postgres$exeSuffix"
    $binCandidates = Get-PostgresBinCandidates
    $binRelative = $binCandidates |
        Where-Object { Test-Path (Join-Path (Join-Path $output $_) $postgresBinary) } |
        Select-Object -First 1

    if ($binRelative) {
        Write-Host "  postgres: $binRelative" -ForegroundColor DarkGray

        # pgvector is what retrieval depends on; bin/ alone still gives you a database
        # that cannot do vector search, so say it loudly instead of failing later at
        # "CREATE EXTENSION vector". Library name differs per platform.
        $vectorLibrary = 'vector.so'
        if ($Runtime -like 'win-*') { $vectorLibrary = 'vector.dll' }
        elseif ($Runtime -like 'osx-*') { $vectorLibrary = 'vector.dylib' }

        $pgRoot = Split-Path (Join-Path $output $binRelative) -Parent
        $missingPgvector = @()
        if (-not (Test-Path (Join-Path $pgRoot "lib\$vectorLibrary"))) { $missingPgvector += "lib\$vectorLibrary" }
        if (-not (Test-Path (Join-Path $pgRoot 'share\extension\vector.control'))) {
            $missingPgvector += 'share\extension\vector.control'
        }
        if ($missingPgvector.Count -gt 0) {
            Write-Warning "pgvector missing in $binRelative tree ($($missingPgvector -join ', '))."
            Write-Warning "  Vector/hybrid retrieval will fail at CREATE EXTENSION vector."
            Write-Warning "  Re-assemble the tree: scripts\Download-ExternalTools.ps1 -Target pgsql -PgSource <dist\pgsql>"
        }
    }
    else {
        Write-Warning "No portable PostgreSQL server tree for $Runtime (no $postgresBinary under: $($binCandidates -join ', '))."
        Write-Warning "  Needs the whole tree ExternalTools\pgSQL\$Runtime\ (bin + lib + share); bin/ alone is not enough."
        Write-Warning "  Provide it: powershell -ExecutionPolicy Bypass -File scripts\Download-ExternalTools.ps1 -Target pgsql -PgSource <ready dist\pgsql>"
        Write-Warning "              (or -PgZip <pgsql-portable zip>)"
        if ($Runtime -notlike 'win-*') {
            # Linux/macOS: the recommended path is the OS package, not a bundled tree.
            Write-Warning "  Linux/macOS: use the OS package instead of a bundled tree (this project does not ship one)."
            Write-Warning "    Debian/Ubuntu: apt-get install -y postgresql-17 postgresql-17-pgvector"
            Write-Warning "    Then set PortablePostgres__BinDirectory=/usr/lib/postgresql/17/bin"
            Write-Warning "    deploy/linux/ has a ready systemd unit and install.sh."
        }
        Write-Warning "  Or skip the portable database entirely: set ConnectionStrings__KnowledgeBase to an external PostgreSQL (recommended in Docker)."
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

# A root-level leftover only gets pruned if it really is a platform binary. The old rule
# was "root-level file without an extension", which also matched README, LICENSE and any
# other extension-less file someone dropped there. The file header decides instead: PE
# (MZ), ELF, or Mach-O magic. Everything else is left alone.
function Test-IsNativeBinary([System.IO.FileInfo]$file) {
    if ($file.Extension -eq '.exe') { return $true }
    if (-not ([string]::IsNullOrEmpty($file.Extension))) { return $false }
    if ($file.Length -lt 4) { return $false }

    $header = New-Object byte[] 4
    $stream = [System.IO.File]::OpenRead($file.FullName)
    try {
        $read = $stream.Read($header, 0, 4)
    }
    finally {
        $stream.Dispose()
    }
    if ($read -lt 4) { return $false }

    $isPe = $header[0] -eq 0x4D -and $header[1] -eq 0x5A
    $isElf = $header[0] -eq 0x7F -and $header[1] -eq 0x45 -and $header[2] -eq 0x4C -and $header[3] -eq 0x46
    $isMachO = ($header[0] -eq 0xFE -and $header[1] -eq 0xED -and $header[2] -eq 0xFA -and ($header[3] -eq 0xCE -or $header[3] -eq 0xCF)) -or
               ($header[0] -eq 0xCE -and $header[1] -eq 0xFA -and $header[2] -eq 0xED -and $header[3] -eq 0xFE) -or
               ($header[0] -eq 0xCF -and $header[1] -eq 0xFA -and $header[2] -eq 0xED -and $header[3] -eq 0xFE) -or
               ($header[0] -eq 0xCA -and $header[1] -eq 0xFE -and $header[2] -eq 0xBA -and $header[3] -eq 0xBE) -or
               ($header[0] -eq 0xBE -and $header[1] -eq 0xBA -and $header[2] -eq 0xFE -and $header[3] -eq 0xCA)

    return ($isPe -or $isElf -or $isMachO)
}

# Remove native binaries that belong to other platforms: one pgsql tree is ~65 MB, each
# ffmpeg ~100 MB.
# Rules:
#   - delete RID sub-folders other than $Runtime (their VERSION.txt / notices describe that
#     platform, so they go with it - they are not package-root files);
#   - non-Windows target: delete root-level *.exe (legacy flat Windows layout);
#   - Windows target: delete root-level extension-less binaries (ELF / Mach-O).
# Root-level docs (README.md, VERSION.txt, THIRD-PARTY-NOTICES.txt, ...) and everything
# INSIDE the kept <rid> folder are never touched: the file pass is not recursive and only
# considers real binaries.
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
            # Not a binary (README.md, VERSION.txt, notices, ...): never delete.
            if (-not (Test-IsNativeBinary $file)) { continue }

            $isWindowsBinary = $file.Extension -eq '.exe'
            if ($Runtime -notlike 'win-*' -and $isWindowsBinary) {
                Remove-Item $file.FullName -Force
                $removed++
            }
            elseif ($Runtime -like 'win-*' -and -not $isWindowsBinary) {
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
