#Requires -Version 5.1
<#
.SYNOPSIS
    Temporary switch: point the API at an existing local PostgreSQL instead of the bundled portable PostgreSQL.

.DESCRIPTION
    Use this when the app runs from a protected / non-ASCII directory (Desktop, 文档, 下载)
    and portable PostgreSQL cannot initialise there.

    The script:
      1. connects to the local PostgreSQL with psql,
      2. creates the target database when it does not exist and enables pgvector,
      3. stores ConnectionStrings:KnowledgeBase in the API project's user secrets
         (nothing is written into appsettings*.json, so no password is committed).

    Portable PostgreSQL is skipped automatically once the connection string is present
    (PortablePostgres:RespectExternalConnectionString = true).

.PARAMETER ServerHost
    PostgreSQL host. Default 127.0.0.1.

.PARAMETER Port
    PostgreSQL port. Default 5432.

.PARAMETER Database
    Target database, created when missing. Default knowledge_base.

.PARAMETER Username
    Superuser used to create the database. Default postgres.

.PARAMETER Password
    Password of Username. Omit to be prompted (recommended: keeps it out of history).

.PARAMETER PsqlPath
    psql.exe path. Omit to search PATH then the usual install roots.

.PARAMETER ProjectPath
    API project directory. Default ..\MyFirstMafAgent.Api next to this script.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Use-LocalPostgres.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Use-LocalPostgres.ps1 `
        -Database knowledge_base -PsqlPath D:\PgSQL\bin\psql.exe

.EXAMPLE
    # 还原成便携 PostgreSQL
    dotnet user-secrets remove "ConnectionStrings:KnowledgeBase" --project MyFirstMafAgent.Api
#>
[CmdletBinding()]
param(
    [string]$ServerHost = '127.0.0.1',
    [int]$Port = 5432,
    [string]$Database = 'knowledge_base',
    [string]$Username = 'postgres',
    [string]$Password,
    [string]$PsqlPath,
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\MyFirstMafAgent.Api')
)

$ErrorActionPreference = 'Stop'

function Resolve-Psql {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) {
            throw "找不到 psql：$Explicit"
        }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }

    $command = Get-Command psql.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $roots = @(
        'C:\Program Files\PostgreSQL',
        'D:\PgSQL',
        'D:\PostgreSQL',
        'C:\PostgreSQL'
    )
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }

        $direct = Join-Path $root 'bin\psql.exe'
        if (Test-Path -LiteralPath $direct) { return $direct }

        $candidate = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'bin\psql.exe' } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($candidate) { return $candidate }
    }

    throw '找不到 psql.exe。请用 -PsqlPath 指定，例如 -PsqlPath D:\PgSQL\bin\psql.exe'
}

function Invoke-Psql {
    param(
        [string]$Psql,
        [string]$DatabaseName,
        [string]$Sql
    )

    $output = & $Psql -h $ServerHost -p $Port -U $Username -d $DatabaseName -tAc $Sql 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ($output | Out-String).Trim()
    }
    return ($output | Out-String).Trim()
}

# 连接串里的口令含这些字符时需要加双引号，否则会被当成键值分隔符。
function Format-ConnectionString {
    param([string]$RawPassword)

    $value = $RawPassword
    if ($RawPassword -match '[;''"=\s]') {
        $value = '"' + $RawPassword.Replace('"', '""') + '"'
    }

    return "Host=$ServerHost;Port=$Port;Database=$Database;Username=$Username;Password=$value"
}

$psql = Resolve-Psql -Explicit $PsqlPath
Write-Host "psql      : $psql"

if (-not $Password) {
    $secure = Read-Host -Prompt "请输入 $Username@$ServerHost`:$Port 的密码" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $Password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

$env:PGPASSWORD = $Password
$env:PGCLIENTENCODING = 'UTF8'

try {
    Invoke-Psql -Psql $psql -DatabaseName 'postgres' -Sql 'SELECT 1' | Out-Null
    Write-Host "连接成功  : $ServerHost`:$Port (user=$Username)"

    $exists = Invoke-Psql -Psql $psql -DatabaseName 'postgres' `
        -Sql "SELECT 1 FROM pg_database WHERE datname = '$Database'"
    if ($exists -eq '1') {
        Write-Host "数据库    : $Database 已存在"
    }
    else {
        Invoke-Psql -Psql $psql -DatabaseName 'postgres' -Sql "CREATE DATABASE ""$Database""" | Out-Null
        Write-Host "数据库    : 已创建 $Database"
    }

    $vector = Invoke-Psql -Psql $psql -DatabaseName $Database `
        -Sql "SELECT 1 FROM pg_available_extensions WHERE name = 'vector'"
    if ($vector -ne '1') {
        throw "数据库 $Database 所在实例没有安装 pgvector 扩展（缺少 vector 控制文件），请先安装 pgvector。"
    }

    Invoke-Psql -Psql $psql -DatabaseName $Database -Sql 'CREATE EXTENSION IF NOT EXISTS vector' | Out-Null
    $version = Invoke-Psql -Psql $psql -DatabaseName $Database `
        -Sql "SELECT extversion FROM pg_extension WHERE extname = 'vector'"
    Write-Host "pgvector  : 已启用 (v$version)"
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:\PGCLIENTENCODING -ErrorAction SilentlyContinue
}

$connectionString = Format-ConnectionString -RawPassword $Password
$project = Resolve-Path -LiteralPath $ProjectPath
& dotnet user-secrets set 'ConnectionStrings:KnowledgeBase' $connectionString --project $project.Path
if ($LASTEXITCODE -ne 0) {
    throw '写入 user secrets 失败。'
}

Write-Host ''
Write-Host '已写入 user secrets（不会进入仓库）：' -ForegroundColor Green
Write-Host "  ConnectionStrings:KnowledgeBase = Host=$ServerHost;Port=$Port;Database=$Database;Username=$Username;Password=******"
Write-Host ''
Write-Host '现在可以直接 F5 / dotnet run，启动时会跳过便携 PostgreSQL。'
Write-Host '还原成便携 PostgreSQL：'
Write-Host '  dotnet user-secrets remove "ConnectionStrings:KnowledgeBase" --project MyFirstMafAgent.Api'
