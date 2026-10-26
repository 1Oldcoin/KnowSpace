<#
.SYNOPSIS
    Assemble per-platform native tools (pgsql / ffmpeg) into the ExternalTools RID folders.

.DESCRIPTION
    These trees are large (one portable pgsql tree ~65 MB, ffmpeg 30-100 MB) and are NOT
    committed. This script assembles them on demand. A failure never breaks the build: the
    publish script only warns, and the runtime prints exactly which path is missing and
    where it should go.

    Target layout (the API finds these automatically, no renaming needed):
        ExternalTools/pgSQL/<rid>/bin|lib|share   complete portable PostgreSQL tree
        ExternalTools/ffmpeg/<rid>/ffmpeg[.exe]

    Portable PostgreSQL is no longer a third-party single-file launcher. The API owns the
    database lifecycle and drives initdb / pg_ctl / postgres out of bin/, with pgvector
    shipped in lib/ (vector.dll) and share/extension/ (vector.control). So what gets
    installed is a plain directory tree, copied as-is:

        -PgSource <dir>   a ready tree (bin/ lib/ share/), robocopied verbatim
        -PgZip <zip>      the same tree in a zip, expanded to %TEMP% first

    The zip may carry a single wrapping folder (pgsql/) - it is stripped. When neither
    option yields a tree the script prints how to obtain one and records a skip; it does
    not fail.

    NOTE: keep this file ASCII-only. Windows PowerShell 5.1 misparses UTF-8 sources
    without a BOM.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Download-ExternalTools.ps1 -Target pgsql -PgSource C:\PG\dist\pgsql

.EXAMPLE
    # Show what would be copied, write nothing.
    powershell -ExecutionPolicy Bypass -File scripts\Download-ExternalTools.ps1 -Target pgsql -PgZip D:\PG\pgsql-portable.zip -DryRun

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Download-ExternalTools.ps1 -Runtime linux-x64 -Target ffmpeg

.NOTES
    Downloading Linux/macOS binaries from Windows is fine (it is just file copying), but
    the executable bit must be set on the target system. The API adds +x before launching
    when the filesystem allows it.
#>
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string[]]$Runtime = @(),
    [ValidateSet('pgsql', 'ffmpeg', 'all')]
    [string]$Target = 'all',
    # A ready pgsql tree: the folder that contains bin/ lib/ share/.
    [string]$PgSource = '',
    # A zip of that same tree. Empty by default: pass one explicitly, or use -PgSource.
    # A given-but-missing path is a skip with a hint, never an error.
    [string]$PgZip = '',
    [switch]$Force,
    # Print what would be copied/downloaded and touch nothing outside %TEMP%.
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $root 'MyFirstMafAgent.Api\ExternalTools'

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
$script:zipExtractRoot = $null   # temp folder to delete at the end
$script:zipExtractTree = $null   # tree root inside that temp folder

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

# --- portable PostgreSQL ---------------------------------------------------------------
# The tree is one unit: bin/ (initdb, pg_ctl, postgres), lib/ (pgvector library) and
# share/extension/ (vector.control + the SQL payload) are useless apart. Names differ per
# platform, so derive them from the RID instead of hardcoding Windows ones.
function Get-PostgresRequiredFiles([string]$rid) {
    $exeSuffix = if ($rid -like 'win-*') { '.exe' } else { '' }
    $vectorLibrary = 'vector.so'
    if ($rid -like 'win-*') { $vectorLibrary = 'vector.dll' }
    elseif ($rid -like 'osx-*') { $vectorLibrary = 'vector.dylib' }

    return @(
        "bin\postgres$exeSuffix",
        "lib\$vectorLibrary",
        'share\extension\vector.control')
}

# Returns the list of missing pieces (empty array = complete tree).
function Get-PostgresMissing([string]$treeRoot, [string]$rid) {
    $missing = @()
    foreach ($relative in (Get-PostgresRequiredFiles $rid)) {
        if (-not (Test-Path (Join-Path $treeRoot $relative))) { $missing += $relative }
    }
    return $missing
}

function Write-PostgresInstructions([string]$rid) {
    Write-Host ''
    Write-Host "No portable PostgreSQL tree supplied, skipping pgsql for $rid." -ForegroundColor Yellow
    Write-Host '  Needs the WHOLE tree (bin/ + lib/ + share/); bin/ on its own cannot run initdb/pg_ctl.' -ForegroundColor Gray
    Write-Host '  Pick one and re-run with -Target pgsql:' -ForegroundColor Gray
    Write-Host '    1. Reuse an assembled tree (easiest):' -ForegroundColor Gray
    Write-Host '         -PgSource <folder containing bin\, lib\, share\>' -ForegroundColor Gray
    Write-Host '    2. Reuse a prepared zip (with or without one wrapping pgsql\ folder):' -ForegroundColor Gray
    Write-Host '         -PgZip <zip path>' -ForegroundColor Gray
    Write-Host '    3. Build it once from scratch:' -ForegroundColor Gray
    Write-Host '         a. EnterpriseDB: postgresql-17.9-1-windows-x64-binaries.zip -> extract pgsql\ as the source' -ForegroundColor Gray
    Write-Host '         b. Build pgvector 0.8.7 with MSVC against those headers (nmake /A /F Makefile.win),' -ForegroundColor Gray
    Write-Host '            then copy vector.dll -> <source>\lib\;' -ForegroundColor Gray
    Write-Host '            vector.control + vector--0.8.7.sql -> <source>\share\extension\' -ForegroundColor Gray
    Write-Host "    4. Or drop the portable database for ${rid}: set ConnectionStrings__KnowledgeBase to an external PostgreSQL." -ForegroundColor Gray
}

# Expands -PgZip once (cached), stripping a single wrapping folder such as pgsql/.
function Get-PostgresZipTree {
    if ($script:zipExtractTree) { return $script:zipExtractTree }

    $extract = Join-Path $env:TEMP ('pgsql-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $extract | Out-Null
    $script:zipExtractRoot = $extract

    Expand-Archive -Path $PgZip -DestinationPath $extract -Force
    $entries = @(Get-ChildItem $extract -Force)
    $tree = $extract
    if ($entries.Count -eq 1 -and $entries[0].PSIsContainer) { $tree = $entries[0].FullName }

    $script:zipExtractTree = $tree
    Write-Host "   expanded archive to $tree" -ForegroundColor DarkGray
    return $tree
}

function Install-Postgres([string]$rid) {
    $tree = $null

    if (-not [string]::IsNullOrWhiteSpace($PgSource)) {
        if (-not (Test-Path $PgSource)) {
            $script:failures += "pgsql / $rid (-PgSource not found: $PgSource)"
            Write-PostgresInstructions $rid
            return
        }
        $tree = (Resolve-Path $PgSource).Path

        # Tolerate pointing one level too high (dist\ instead of dist\pgsql).
        if (-not (Test-Path (Join-Path $tree 'bin'))) {
            $children = @(Get-ChildItem $tree -Directory | Where-Object { $_.Name -ne 'bin' })
            $nested = $children | Where-Object { Test-Path (Join-Path $_.FullName 'bin') } | Select-Object -First 1
            if ($nested) {
                Write-Host "   $tree has no bin\; using nested tree $($nested.FullName)" -ForegroundColor DarkGray
                $tree = $nested.FullName
            }
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($PgZip)) {
        if (-not (Test-Path $PgZip)) {
            Write-Warning "   -PgZip not found: $PgZip"
            $script:skipped += "pgsql / $rid (no -PgSource, and -PgZip does not exist)"
            Write-PostgresInstructions $rid
            return
        }
        try {
            $tree = Get-PostgresZipTree
        }
        catch {
            $script:failures += "pgsql / $rid (could not expand $PgZip : $($_.Exception.Message))"
            return
        }
    }
    else {
        $script:skipped += "pgsql / $rid (no -PgSource / -PgZip given)"
        Write-PostgresInstructions $rid
        return
    }

    # A tree built for another OS dropped into this RID folder is the wrong source. Catch it
    # before the missing-files check, otherwise the message is a misleading "missing
    # bin/postgres" when in fact every file is there - just for the wrong platform.
    $hasWindowsServer = Test-Path (Join-Path $tree 'bin\postgres.exe')
    $hasUnixServer = Test-Path (Join-Path $tree 'bin\postgres')
    if (($rid -notlike 'win-*' -and $hasWindowsServer) -or
        ($rid -like 'win-*' -and $hasUnixServer -and -not $hasWindowsServer)) {
        $otherOs = if ($hasWindowsServer) { 'Windows' } else { 'unix' }
        $script:failures += "pgsql / $rid (source tree $tree is a $otherOs build; $rid needs a $rid build)"
        Write-Warning "   source tree looks like a $otherOs build, but the target RID is $rid."
        Write-Warning "   Assemble a matching $rid tree (same bin/lib/share layout) and pass that as -PgSource."
        return
    }

    $missing = @(Get-PostgresMissing $tree $rid)
    if ($missing.Count -gt 0) {
        $script:failures += "pgsql / $rid (source tree $tree is incomplete: missing $($missing -join ', '))"
        Write-Warning "   incomplete source tree: $tree is missing $($missing -join ', ')"
        return
    }

    $destination = Join-Path $toolsRoot "pgSQL\$rid"

    if ($DryRun) {
        $files = @(Get-ChildItem $tree -Recurse -File)
        $sizeMb = [math]::Round((($files | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
        Write-Host "   [DryRun] robocopy `"$tree`" `"$destination`" /E /MT:8" -ForegroundColor Yellow
        Write-Host "   [DryRun] would copy $($files.Count) file(s), $sizeMb MB - nothing written." -ForegroundColor Yellow
        Write-Host "   [DryRun] source tree is complete ($((Get-PostgresRequiredFiles $rid) -join ', '))." -ForegroundColor Yellow
        return
    }

    Write-Step "pgsql $rid -> ExternalTools/pgSQL/$rid"
    New-Item -ItemType Directory -Force $destination | Out-Null

    # robocopy exit codes 0-7 are success (bit flags), >= 8 is a real failure.
    & robocopy $tree $destination /E /MT:8 /NFL /NDL /NJH /NJS /NP | Out-Null
    $code = $LASTEXITCODE
    if ($code -ge 8) {
        $script:failures += "pgsql / $rid (robocopy exit code $code - close any running API/postgres first)"
        return
    }

    $missing = @(Get-PostgresMissing $destination $rid)
    if ($missing.Count -gt 0) {
        $script:failures += "pgsql / $rid (copied to $destination but missing $($missing -join ', '))"
        return
    }

    Write-Host "   assembled: $destination" -ForegroundColor Green
}

# --- ffmpeg ----------------------------------------------------------------------------
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
if ($DryRun) { Write-Host 'Dry run: nothing will be copied or downloaded.' -ForegroundColor Yellow }

try {
    foreach ($rid in $Runtime) {
        if ($Target -in @('pgsql', 'all')) { Install-Postgres $rid }
        if ($Target -in @('ffmpeg', 'all')) {
            if ($DryRun) {
                Write-Host "   [DryRun] would install ffmpeg for $rid (no download performed)." -ForegroundColor Yellow
            }
            else {
                Install-Ffmpeg $rid
            }
        }
    }
}
finally {
    if ($script:zipExtractRoot -and (Test-Path $script:zipExtractRoot)) {
        Remove-Item $script:zipExtractRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
if ($script:skipped.Count -gt 0) {
    Write-Host 'Skipped:' -ForegroundColor Yellow
    $script:skipped | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
}

if ($script:failures.Count -gt 0) {
    Write-Warning ('Not assembled/downloaded (fix and re-run, or copy the tree in by hand): ' + ($script:failures -join ', '))
    Write-Warning 'pgsql: pass -PgSource <dist\pgsql> (folder with bin/ lib/ share/) or -PgZip <zip>; see the instructions above.'
    exit 1
}

Write-Host 'Done.' -ForegroundColor Green
