#Requires -Version 5.1
<#
.SYNOPSIS
    Phase 0 probe: inspect physical vector indexes and the real query plan.

.DESCRIPTION
    Answers the question "is pgvector doing an exact scan?" with evidence:
      - row counts and vector dimensions per table
      - existing indexes on kb_index_vectors / kb_chunks (HNSW/IVFFlat or none)
      - planner settings that affect ANN behaviour
      - EXPLAIN (ANALYZE, BUFFERS) of the same ORDER BY <=> query the app issues

.PARAMETER PsqlPath
    Full path to psql.exe. Defaults to psql on PATH.

.PARAMETER ConnectionString
    Npgsql style connection string. Defaults to $env:MYFIRSTMAFAGENT_DB.

.PARAMETER OutputPath
    Where to tee the probe output. Defaults to ./vector-index-probe.txt

.EXAMPLE
    $env:MYFIRSTMAFAGENT_DB = 'Host=127.0.0.1;Port=5432;Database=knowledge;Username=postgres;Password=postgres'
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Measure-VectorIndex.ps1 -PsqlPath 'D:\PgSQL\bin\psql.exe'
#>
[CmdletBinding()]
param(
    [string]$PsqlPath = '',
    [string]$ConnectionString = $env:MYFIRSTMAFAGENT_DB,
    [string]$OutputPath = (Join-Path (Get-Location) 'vector-index-probe.txt')
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    throw 'Provide -ConnectionString or set MYFIRSTMAFAGENT_DB (Npgsql format: Host=...;Port=...;Database=...;Username=...;Password=...).'
}

if ([string]::IsNullOrWhiteSpace($PsqlPath)) {
    $command = Get-Command psql -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw 'psql.exe not found. Pass -PsqlPath (for example D:\PgSQL\bin\psql.exe).'
    }
    $PsqlPath = $command.Source
}

if (-not (Test-Path -LiteralPath $PsqlPath)) {
    throw "psql not found at $PsqlPath"
}

# --- connection string -> psql arguments ---
$map = @{}
foreach ($part in $ConnectionString.Split(';')) {
    if ([string]::IsNullOrWhiteSpace($part)) { continue }
    $pair = $part.Split('=', 2)
    if ($pair.Count -eq 2) { $map[$pair[0].Trim().ToLowerInvariant()] = $pair[1].Trim() }
}

function Get-Value([hashtable]$source, [string[]]$keys) {
    foreach ($key in $keys) {
        if ($source.ContainsKey($key) -and -not [string]::IsNullOrWhiteSpace($source[$key])) {
            return $source[$key]
        }
    }
    return $null
}

$hostName = Get-Value $map @('host', 'server')
$port = Get-Value $map @('port')
$database = Get-Value $map @('database', 'db')
$username = Get-Value $map @('username', 'user', 'userid', 'user id')
$password = Get-Value $map @('password', 'pwd')

if ($password) { $env:PGPASSWORD = $password }

$sql = @'
\echo '== row counts =='
SELECT 'kb_index_vectors' AS table_name, count(*) AS rows FROM kb_index_vectors
UNION ALL SELECT 'kb_chunks', count(*) FROM kb_chunks
UNION ALL SELECT 'kb_indexes', count(*) FROM kb_indexes;

\echo '== vector dimensions (kb_index_vectors) =='
SELECT vector_dims(embedding) AS dimensions, count(*) AS rows
FROM kb_index_vectors
WHERE embedding IS NOT NULL
GROUP BY 1
ORDER BY 2 DESC;

\echo '== indexes on vector tables =='
SELECT tablename, indexname, indexdef
FROM pg_indexes
WHERE tablename IN ('kb_index_vectors', 'kb_chunks')
ORDER BY tablename, indexname;

\echo '== planner settings =='
SELECT name, setting, unit
FROM pg_settings
WHERE name IN (
    'shared_buffers',
    'work_mem',
    'maintenance_work_mem',
    'effective_cache_size',
    'random_page_cost',
    'max_parallel_workers_per_gather'
)
ORDER BY name;

\echo '== exact vs ANN plan: same ORDER BY as the application query =='
EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT)
WITH sample AS (
    SELECT index_id, embedding
    FROM kb_index_vectors
    WHERE embedding IS NOT NULL
    ORDER BY random()
    LIMIT 1
)
SELECT v.index_id,
       v.chunk_id,
       (1 - (v.embedding <=> sample.embedding))::double precision AS similarity
FROM kb_index_vectors AS v, sample
WHERE v.embedding IS NOT NULL
ORDER BY v.embedding <=> sample.embedding
LIMIT 10;
'@

$tempFile = Join-Path ([System.IO.Path]::GetTempPath()) ('vector-probe-' + [Guid]::NewGuid().ToString('N') + '.sql')
Set-Content -LiteralPath $tempFile -Value $sql -Encoding ASCII

try {
    $arguments = @('-X', '-v', 'ON_ERROR_STOP=1', '-f', $tempFile)
    if ($hostName) { $arguments += @('-h', $hostName) }
    if ($port) { $arguments += @('-p', $port) }
    if ($username) { $arguments += @('-U', $username) }
    if ($database) { $arguments += @('-d', $database) }

    Write-Host ("Running vector index probe with {0}" -f $PsqlPath)
    & $PsqlPath @arguments 2>&1 | Tee-Object -FilePath $OutputPath
    $exitCode = $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host ("Probe output saved to {0}" -f $OutputPath)
Write-Host 'Read the plan:'
Write-Host '  - "Seq Scan on kb_index_vectors" + "Sort" => exact scan, no ANN index.'
Write-Host '  - "Index Scan using <hnsw|ivfflat> index" => ANN index is in use.'
Write-Host 'Record rows, dimensions, plan and buffer numbers in docs/PHASE0-BASELINE.md.'

exit $exitCode
