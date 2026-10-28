# 校验 README.md 与 docs/*.md 中的相对链接是否存在。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Check-DocLinks.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $files = @('README.md', 'ARCHITECTURE.md', 'PROJECT_DOCUMENTATION.md')
    $files += Get-ChildItem docs -Filter *.md | ForEach-Object { "docs/$($_.Name)" }
    $files += 'desktop-electron/README.md'

    $broken = [System.Collections.Generic.List[string]]::new()
    $checked = 0

    foreach ($file in $files) {
        if (-not (Test-Path $file)) { continue }
        $dir = Split-Path $file -Parent
        if ([string]::IsNullOrEmpty($dir)) { $dir = '.' }
        $text = [IO.File]::ReadAllText((Resolve-Path $file))

        # Inline code and fenced blocks contain examples such as [2024](url); strip them
        # so the checker only looks at real markdown links.
        $text = [regex]::Replace($text, '(?s)```.*?```', '')
        $text = [regex]::Replace($text, '`[^`\r\n]*`', '')

        foreach ($match in [regex]::Matches($text, '\[[^\]]*\]\(([^)]+)\)')) {
            $link = $match.Groups[1].Value.Trim()

            # 去掉标题锚点与尖括号
            $link = ($link -split '#')[0].Trim('<', '>', ' ')
            if ([string]::IsNullOrWhiteSpace($link)) { continue }
            if ($link -match '^(https?|mailto|file):') { continue }

            $checked++
            $target = if ($link.StartsWith('/')) { $link.TrimStart('/') } else { Join-Path $dir $link }
            if (-not (Test-Path -LiteralPath $target)) {
                $broken.Add("$file  ->  $link")
            }
        }
    }

    Write-Host "检查 $checked 个相对链接，覆盖 $($files.Count) 个文档"
    if ($broken.Count -eq 0) {
        Write-Host '全部有效' -ForegroundColor Green
        exit 0
    }

    Write-Host "失效 $($broken.Count) 个：" -ForegroundColor Yellow
    $broken | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" }
    exit 1
}
finally {
    Pop-Location
}
