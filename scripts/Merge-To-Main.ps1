<#
.SYNOPSIS
    一键把开发分支合并到稳定分支，并推送到远端 main（默认 dev -> main）。

.DESCRIPTION
    本仓库的本地稳定分支叫 master，远端稳定分支叫 main（master 跟踪 github/main）。
    手敲 `git push github master` 会在远端凭空多出一个 master 分支，所以这套动作
    固定进脚本：

      1. 前置检查：工作区干净、不在 detached HEAD、分支与远端存在
      2. git fetch <remote> --prune
      3. 切到本地稳定分支，快进到远端最新（有本地未推送提交则报错退出）
      4. git merge --no-ff <source>，保留合并提交（便于整体回滚）
      5. 推送 <本地稳定分支>:<远端稳定分支>
      6. 让开发分支也指向合并提交并推送，避免下次合并出现「假落后」
      7. 可选：重打版本标签并强推

    默认在真正动手前要求确认，`-Yes` 可跳过；`-DryRun` 只打印将要执行的命令。

.PARAMETER SourceBranch
    开发分支，默认 dev。

.PARAMETER TargetBranch
    远端稳定分支，默认 main。

.PARAMETER LocalTarget
    本地稳定分支名。留空时自动探测「上游是 <Remote>/<TargetBranch> 的本地分支」，
    探测不到再退回同名本地分支。

.PARAMETER Remote
    远端名，默认 github。

.PARAMETER Message
    合并提交信息。留空时自动生成「merge: 合并 dev 到 main（<最新提交标题>）」。

.PARAMETER TagName
    合并成功后重打并强推的标签，例如 v1.0.2。留空则不动标签。

.PARAMETER Yes
    跳过确认提示。

.PARAMETER DryRun
    只做只读检查并打印将要执行的 git 命令，不改动任何东西。

.PARAMETER NoPush
    完成本地合并不推送（适合先本地验证）。

.PARAMETER AllowNonFastForward
    允许本地稳定分支落后且分叉时继续（默认要求能 --ff-only 到远端）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Merge-To-Main.ps1 -DryRun

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Merge-To-Main.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Merge-To-Main.ps1 -TagName v1.0.2 -Yes
#>
[CmdletBinding()]
param(
    [string]$SourceBranch = 'dev',
    [string]$TargetBranch = 'main',
    [string]$Remote = 'github',
    [string]$LocalTarget = '',
    [string]$Message = '',
    [string]$TagName = '',
    [switch]$Yes,
    [switch]$DryRun,
    [switch]$NoPush,
    [switch]$AllowNonFastForward
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# 优先用当前目录所在的 git 仓库；不在仓库里时退回脚本所在仓库。
function Get-RepoRoot {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git rev-parse --show-toplevel 2>$null
        if ($LASTEXITCODE -eq 0 -and $out) { return ([string]($out | Select-Object -First 1)).Trim() }
    }
    finally { $ErrorActionPreference = $previous }
    return $null
}

$root = Get-RepoRoot
if (-not $root) { $root = Split-Path -Parent $PSScriptRoot }

Push-Location $root
try {
    function Show-Section([string]$Text) { Write-Host ''; Write-Host $Text -ForegroundColor White }
    function Show-Ok([string]$Text) { Write-Host ('  [ok] ' + $Text) -ForegroundColor Green }
    function Show-Note([string]$Text) { Write-Host ('  [--] ' + $Text) -ForegroundColor Yellow }

    # 统一封装 git 调用。PS 5.1 下原生命令写 stderr + `2>&1` 会被 ErrorActionPreference=Stop
    # 当作终止错误，所以调用期间临时改成 Continue，只认退出码。
    function Invoke-Git {
        param(
            [Parameter(Mandatory = $true)][string[]]$GitArgs,
            [switch]$AllowFailure
        )
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $raw = & git @GitArgs 2>&1
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previous
        }

        $text = (($raw | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.Exception.Message } else { [string]$_ }
        }) -join "`n").Trim()

        if ($exitCode -ne 0 -and -not $AllowFailure) {
            throw ("git {0} 失败（退出码 {1}）`n{2}" -f ($GitArgs -join ' '), $exitCode, $text)
        }
        return [pscustomobject]@{ Output = $text; ExitCode = $exitCode }
    }

    function Invoke-Step {
        param([string]$Description, [string[]]$GitArgs)
        Write-Host ('  -> ' + $Description) -ForegroundColor Cyan
        Write-Host ('     git ' + ($GitArgs -join ' ')) -ForegroundColor DarkGray
        if ($DryRun) { return [pscustomobject]@{ Output = ''; ExitCode = 0 } }
        return Invoke-Git -GitArgs $GitArgs
    }

    function Test-GitRef([string]$Ref) {
        $result = Invoke-Git -GitArgs @('rev-parse', '--verify', '--quiet', "$Ref^{commit}") -AllowFailure
        return ($result.ExitCode -eq 0)
    }

    # ------------------------------------------------------------------ 1/7
    Show-Section '1/7 前置检查'

    $inside = Invoke-Git -GitArgs @('rev-parse', '--is-inside-work-tree') -AllowFailure
    if ($inside.ExitCode -ne 0 -or $inside.Output -ne 'true') { throw "当前目录不是 git 仓库：$root" }

    $original = (Invoke-Git -GitArgs @('rev-parse', '--abbrev-ref', 'HEAD')).Output
    if ($original -eq 'HEAD') { throw '当前处于 detached HEAD 状态，请先 checkout 一个分支再运行。' }

    $statusLines = @((Invoke-Git -GitArgs @('status', '--porcelain')).Output -split "`n" | Where-Object { $_ })
    $dirty = @($statusLines | Where-Object { -not $_.StartsWith('??') })
    if ($dirty.Count -gt 0) {
        throw ("工作区有未提交的改动，先提交或 stash 再运行：`n" + ($dirty -join "`n"))
    }
    $untracked = @($statusLines | Where-Object { $_.StartsWith('??') })
    if ($untracked.Count -gt 0) { Show-Note ("有 {0} 个未跟踪文件，不会进入合并" -f $untracked.Count) }

    $remotes = @((Invoke-Git -GitArgs @('remote')).Output -split "`n" | Where-Object { $_ })
    if ($remotes -notcontains $Remote) {
        throw ("远端 '$Remote' 不存在。现有远端：{0}" -f ($remotes -join ', '))
    }

    if (Test-GitRef "refs/heads/$SourceBranch") {
        $sourceRef = $SourceBranch
        $sourceIsLocal = $true
    }
    elseif (Test-GitRef "refs/remotes/$Remote/$SourceBranch") {
        $sourceRef = "$Remote/$SourceBranch"
        $sourceIsLocal = $false
        Show-Note "本地没有 $SourceBranch，改用 $sourceRef"
    }
    else {
        throw "分支 '$SourceBranch' 在本地和远端 $Remote 上都不存在。"
    }

    if ([string]::IsNullOrWhiteSpace($LocalTarget)) {
        if (Test-GitRef "refs/heads/$TargetBranch") {
            $LocalTarget = $TargetBranch
        }
        else {
            $pairs = @((Invoke-Git -GitArgs @(
                        'for-each-ref', '--format=%(refname:short)|%(upstream:short)', 'refs/heads'
                    )).Output -split "`n" | Where-Object { $_ })
            foreach ($pair in $pairs) {
                $parts = $pair -split '\|', 2
                if ($parts.Count -eq 2 -and $parts[1].Trim() -eq "$Remote/$TargetBranch") {
                    $LocalTarget = $parts[0].Trim()
                    break
                }
            }
            if (-not $LocalTarget) {
                throw ("找不到跟踪 $Remote/$TargetBranch 的本地分支。用 -LocalTarget 指定，或先执行：" +
                    "git checkout -b $TargetBranch --track $Remote/$TargetBranch")
            }
        }
    }
    if (-not (Test-GitRef "refs/heads/$LocalTarget")) { throw "本地分支 '$LocalTarget' 不存在。" }

    $current = (Invoke-Git -GitArgs @('rev-parse', '--short', 'HEAD')).Output
    Show-Ok "仓库 $root（当前 $original @ $current）"
    Show-Ok "合并方向 $sourceRef  ->  $Remote/$TargetBranch（本地用 $LocalTarget）"

    # ------------------------------------------------------------------ 2/7
    Show-Section '2/7 同步远端'
    Invoke-Step "拉取 $Remote（--prune）" @('fetch', $Remote, '--prune') | Out-Null

    $remoteTarget = "$Remote/$TargetBranch"
    if (-not $DryRun -and -not (Test-GitRef "refs/remotes/$remoteTarget")) {
        throw "远端分支 $remoteTarget 不存在，检查 -TargetBranch / -Remote。"
    }

    # ------------------------------------------------------------------ 3/7
    Show-Section '3/7 让本地稳定分支跟上远端'
    if ($original -ne $LocalTarget) {
        Invoke-Step "切到本地稳定分支 $LocalTarget" @('checkout', $LocalTarget) | Out-Null
    }

    # 下面这些都是只读查询（基于 ref，与工作区无关），-DryRun 下也照常算；
    # 真正的写操作都走 Invoke-Step，-DryRun 时只打印不执行。
    $canFastForward = (Invoke-Git -GitArgs @('merge-base', '--is-ancestor', $LocalTarget, $remoteTarget) -AllowFailure).ExitCode -eq 0
    if ($canFastForward) {
        $behind = (Invoke-Git -GitArgs @('rev-list', '--count', "$LocalTarget..$remoteTarget")).Output
        if ($behind -ne '0') {
            Invoke-Step "本地 $LocalTarget 落后 $behind 个提交，快进到 $remoteTarget" @('merge', '--ff-only', $remoteTarget) | Out-Null
        }
        else {
            Show-Ok "本地 $LocalTarget 与 $remoteTarget 一致"
        }
    }
    else {
        $ahead = (Invoke-Git -GitArgs @('rev-list', '--count', "$remoteTarget..$LocalTarget")).Output
        if ($AllowNonFastForward) {
            Show-Note "本地 $LocalTarget 有 $ahead 个未推送提交，按 -AllowNonFastForward 继续"
        }
        else {
            throw ("本地 $LocalTarget 有 {0} 个提交没推送到 $remoteTarget，直接合并会把它们一起推上去。" +
                "`n先确认这些提交应该保留（git push {1} {2}:{3}），或丢弃（git reset --hard {1}/{3}），" +
                "再加 -AllowNonFastForward 重跑。" -f $ahead, $Remote, $LocalTarget, $TargetBranch)
        }
    }

    # ------------------------------------------------------------------ 4/7
    Show-Section '4/7 合并开发分支'

    $pending = [int](Invoke-Git -GitArgs @('rev-list', '--count', "$remoteTarget..$sourceRef")).Output
    if ($pending -eq 0) {
        Show-Ok "$sourceRef 没有新提交，$TargetBranch 已是最新，无事可做。"
        if ($original -ne $LocalTarget) { Show-Note "如需切回：git checkout $original" }
        return
    }

    Write-Host ''
    Write-Host ("  待合并的 {0} 个提交（最近 10 条）：" -f $pending) -ForegroundColor Gray
    $log = (Invoke-Git -GitArgs @('log', '--oneline', '-10', "$remoteTarget..$sourceRef")).Output
    $log -split "`n" | Where-Object { $_ } | ForEach-Object { Write-Host ('    ' + $_) -ForegroundColor DarkGray }
    if ($pending -gt 10) { Write-Host ("    ... 另有 {0} 条" -f ($pending - 10)) -ForegroundColor DarkGray }

    if ([string]::IsNullOrWhiteSpace($Message)) {
        $subject = (Invoke-Git -GitArgs @('log', '-1', '--format=%s', $sourceRef)).Output
        $Message = "merge: 合并 $SourceBranch 到 $TargetBranch（$subject）"
    }

    if (-not $Yes -and -not $DryRun) {
        Write-Host ''
        Write-Host ("即将把 {0} 合并进 {1} 并推送到 {2}/{1}。" -f $sourceRef, $TargetBranch, $Remote) -ForegroundColor Yellow
        $answer = Read-Host '继续？[y/N]'
        if ($answer -notmatch '^(y|yes|是)$') { Write-Host '已取消，未做任何改动。' -ForegroundColor Yellow; return }
    }

    try {
        Invoke-Step ("合并 $sourceRef（--no-ff）") @('merge', '--no-ff', $sourceRef, '-m', $Message) | Out-Null
    }
    catch {
        throw ("合并失败，可能有冲突。解决后手动 ``git commit``，或执行 ``git merge --abort`` 回退。`n" + $_.Exception.Message)
    }

    # ------------------------------------------------------------------ 5/7
    Show-Section '5/7 推送到远端稳定分支'
    if ($NoPush) {
        Show-Note '按 -NoPush 跳过推送，合并只留在本地'
    }
    else {
        Invoke-Step "推送 $LocalTarget -> $Remote/$TargetBranch" @('push', $Remote, "${LocalTarget}:${TargetBranch}") | Out-Null
        if (-not $DryRun) { Show-Ok "已推送 $remoteTarget" }
    }

    # ------------------------------------------------------------------ 6/7
    Show-Section '6/7 让开发分支跟上合并提交'
    if (-not $sourceIsLocal) {
        Show-Note "本地没有 $SourceBranch，跳过本地同步"
    }
    elseif ($DryRun -or (Invoke-Git -GitArgs @('merge-base', '--is-ancestor', $sourceRef, 'HEAD') -AllowFailure).ExitCode -eq 0) {
        if ($original -ne $SourceBranch) {
            Invoke-Step "本地 $SourceBranch 指向合并提交" @('branch', '-f', $SourceBranch, 'HEAD') | Out-Null
        }
        if ($NoPush) {
            Show-Note '按 -NoPush 未推送开发分支'
        }
        else {
            Invoke-Step "推送 $SourceBranch（保持与 $TargetBranch 齐平）" @('push', $Remote, "HEAD:refs/heads/$SourceBranch") | Out-Null
            if (-not $DryRun) { Show-Ok "已同步 $Remote/$SourceBranch" }
        }
    }
    else {
        Show-Note "$sourceRef 不是合并提交的祖先，跳过 $SourceBranch 同步"
    }

    # ------------------------------------------------------------------ 7/7
    Show-Section '7/7 版本标签'
    if ([string]::IsNullOrWhiteSpace($TagName)) {
        Show-Note '未指定 -TagName，标签保持不变'
    }
    else {
        Invoke-Step "重打标签 $TagName -> HEAD" @('tag', '-f', $TagName, 'HEAD') | Out-Null
        if ($NoPush) {
            Show-Note '按 -NoPush 未推送标签'
        }
        else {
            Invoke-Step "强推标签 $TagName" @('push', '-f', $Remote, "refs/tags/$TagName") | Out-Null
        }
    }

    # ------------------------------------------------------------------ 汇总
    Show-Section '完成'
    if ($DryRun) {
        Show-Note '这是 -DryRun，以上命令都未真正执行'
    }
    else {
        $head = (Invoke-Git -GitArgs @('log', '-1', '--format=%h %s', 'HEAD')).Output
        Show-Ok "本地 $LocalTarget  @ $head"
        if (-not $NoPush) {
            $remoteHeads = (Invoke-Git -GitArgs @('ls-remote', '--heads', $Remote) -AllowFailure).Output
            foreach ($line in ($remoteHeads -split "`n")) {
                if ($line -match "refs/heads/($([regex]::Escape($TargetBranch))|$([regex]::Escape($SourceBranch)))$") {
                    Write-Host ('  ' + ($line -replace "`t", '  ')) -ForegroundColor Green
                }
            }
        }
        if ($original -ne $LocalTarget) { Show-Note "当前在 $LocalTarget，如需切回：git checkout $original" }
    }
}
finally {
    Pop-Location
}
