<#
.SYNOPSIS
    Publish the built desktop installer to GitHub Releases (the auto-update feed).

.DESCRIPTION
    electron-updater reads the newest non-prerelease release and its `latest.yml`,
    then downloads the installer asset named inside it. This script only needs the
    REST API, so no `gh` CLI and no `electron-builder --publish` are required.

    Token resolution order:
      1. $env:GH_TOKEN / $env:GITHUB_TOKEN
      2. the credential stored by git-credential-manager for https://github.com
         (`git credential fill`) -- this is what the interactive push already used,
         so it always works on a machine that can `git push` to the repo.

    Assets uploaded: latest.yml plus every *.exe / *.blockmap in the release folder.
    Re-running for an existing tag appends/replaces the same-named assets, so the
    script is safe to re-run after a failed upload.

.PARAMETER Version
    Release version, e.g. 1.0.1. Becomes tag v1.0.1 and release name.

.PARAMETER ReleasesDir
    Folder holding the electron-builder output. Default: <repo>\publish\releases.

.PARAMETER Notes
    Markdown body for the release. Default: a one-line auto message.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Publish-Release.ps1 -Version 1.0.1
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ReleasesDir = '',
    [string]$Owner = '1Oldcoin',
    [string]$Repo = 'KnowSpace',
    [string]$Notes = '',
    [switch]$Draft
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReleasesDir)) { $ReleasesDir = Join-Path $root 'publish\releases' }

function Get-GitHubToken {
    if ($env:GH_TOKEN) { return $env:GH_TOKEN.Trim() }
    if ($env:GITHUB_TOKEN) { return $env:GITHUB_TOKEN.Trim() }

    $previous = $env:GIT_TERMINAL_PROMPT
    $env:GIT_TERMINAL_PROMPT = '0'
    try {
        $input = "protocol=https`nhost=github.com`n`n"
        $lines = ($input | git credential fill 2>$null)
    }
    finally {
        $env:GIT_TERMINAL_PROMPT = $previous
    }

    $password = ($lines | Where-Object { $_ -match '^password=' })
    if (-not $password) { throw 'No GitHub token: set GH_TOKEN or sign in with git-credential-manager.' }
    return ($password -replace '^password=', '').Trim()
}

$token = Get-GitHubToken
$headers = @{
    Authorization          = "token $token"
    'User-Agent'           = 'maf-release-script'
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
}

if (-not (Test-Path $ReleasesDir)) { throw "Missing release folder: $ReleasesDir" }

$installer = Get-ChildItem $ReleasesDir -Filter '*.exe' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$latest = Get-ChildItem $ReleasesDir -Filter 'latest.yml' -File | Select-Object -First 1
if (-not $installer) { throw "No installer (*.exe) in $ReleasesDir. Build it first (scripts\Build-ElectronInstaller.ps1)." }
if (-not $latest) { throw "No latest.yml in $ReleasesDir. It must come from the same electron-builder run." }

# latest.yml must describe the installer we actually ship, otherwise the client
# downloads a file whose hash does not match and every update fails at install time.
$ymlText = Get-Content $latest.FullName -Raw
if ($ymlText -notmatch [regex]::Escape($installer.Name)) {
    throw "latest.yml does not reference $($installer.Name). Re-run electron-builder."
}
if ($ymlText -notmatch [regex]::Escape("version: $Version")) {
    throw "latest.yml version does not match -Version $Version. Bump package.json and rebuild."
}

# Get-ChildItem -Include only matches when the path itself has a wildcard, so the
# assets are collected explicitly: latest.yml + the installer + its blockmap.
$assets = @($latest.FullName, $installer.FullName)
$assets += @(Get-ChildItem -Path $ReleasesDir -Filter '*.blockmap' -File | ForEach-Object { $_.FullName })
$assets = $assets | Select-Object -Unique

$tag = "v$Version"
$body = if ([string]::IsNullOrWhiteSpace($Notes)) { "MyFirstMafAgent $Version" } else { $Notes }

Write-Host "==> Release $tag ($Owner/$Repo)" -ForegroundColor Cyan
foreach ($asset in $assets) { Write-Host ("    asset: {0} ({1:N1} MB)" -f (Split-Path $asset -Leaf), ((Get-Item $asset).Length / 1MB)) }

$release = $null
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases/tags/$tag" -Headers $headers -Method Get
    Write-Host "    release already exists (id $($release.id)), reusing."
}
catch {
    $payload = @{
        tag_name         = $tag
        name           = $tag
        body           = $body
        draft          = [bool]$Draft
        prerelease     = $false
        target_commitish = 'main'
    } | ConvertTo-Json
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases" -Headers $headers -Method Post -Body $payload -ContentType 'application/json'
    Write-Host "    release created (id $($release.id))."
}

foreach ($asset in $assets) {
    $name = Split-Path $asset -Leaf
    $existing = $release.assets | Where-Object { $_.name -eq $name }
    if ($existing) {
        Write-Host "    deleting existing asset $name" -ForegroundColor DarkGray
        Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases/assets/$($existing.id)" -Headers $headers -Method Delete
    }

    $uploadUrl = "https://uploads.github.com/repos/$Owner/$Repo/releases/$($release.id)/assets?name=$([uri]::EscapeDataString($name))"
    Write-Host "    uploading $name ..." -ForegroundColor Yellow
    $bytes = [System.IO.File]::ReadAllBytes($asset)
    Invoke-RestMethod -Uri $uploadUrl -Headers $headers -Method Post -Body $bytes -ContentType 'application/octet-stream' -TimeoutSec 3600 | Out-Null
    Write-Host "    uploaded $name" -ForegroundColor Green
}

$final = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases/tags/$tag" -Headers $headers -Method Get
Write-Host ''
Write-Host "==> $tag now has:" -ForegroundColor Cyan
$final.assets | ForEach-Object { Write-Host ("    {0}  {1:N1} MB" -f $_.name, ($_.size / 1MB)) }
Write-Host "    url: $($final.html_url)"
