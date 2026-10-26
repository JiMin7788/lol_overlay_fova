<#
.SYNOPSIS
    Prints the M39 telemetry numbers: installs, daily users, new installs, versions, crashes.

.DESCRIPTION
    Queries the D1 database through the Cloudflare API with the token in the Windows user
    environment variable FOVA_CF_TOKEN (a custom API token with Account - D1 - Read). The token is
    never printed. The account is the first one the token sees; the database id is read from
    wrangler.toml beside this script.

    Release builds lead their stamp with the version ("0.4.1 <hash>+dirty <time>", passed by
    packaging/build-release.ps1); every other stamp is a dev build, so the "release" columns are
    other people's installs. Releases up to 0.4.0 carry only the hash and count as dev.

.EXAMPLE
    pwsh telemetry-worker/stats.ps1
    pwsh telemetry-worker/stats.ps1 -Days 30
#>
param([int]$Days = 14)
$ErrorActionPreference = 'Stop'

$token = [Environment]::GetEnvironmentVariable('FOVA_CF_TOKEN', 'User')
if (-not $token) { throw 'FOVA_CF_TOKEN is not set (Windows user environment variable).' }
$headers = @{ Authorization = "Bearer $token" }
$api = 'https://api.cloudflare.com/client/v4'
$dbId = [regex]::Match((Get-Content (Join-Path $PSScriptRoot 'wrangler.toml') -Raw), 'database_id\s*=\s*"([^"]+)"').Groups[1].Value
$account = (Invoke-RestMethod "$api/accounts" -Headers $headers).result[0].id
$queryUrl = "$api/accounts/$account/d1/database/$dbId/query"

function Query([string]$title, [string]$sql) {
    $body = @{ sql = $sql } | ConvertTo-Json
    $rows = (Invoke-RestMethod $queryUrl -Method Post -Headers $headers -ContentType 'application/json' -Body $body).result[0].results
    "`n== $title =="
    if ($rows) { ($rows | Format-Table -AutoSize | Out-String -Width 200).Trim() } else { '(none)' }
}

$isRelease = "version GLOB '[0-9]*.[0-9]*.[0-9]* [0-9a-z]*'"
$kind = "CASE WHEN $isRelease THEN 'release' ELSE 'dev' END"
$ver = "CASE WHEN $isRelease THEN substr(version, 1, instr(version, ' ') - 1) ELSE 'dev' END"

Query 'Installs, all time' "SELECT $kind AS kind, COUNT(DISTINCT install_id) AS installs, MIN(day) AS first_day, MAX(day) AS last_day FROM pings GROUP BY kind"
Query "Daily users, last $Days days" "SELECT day, SUM($isRelease) AS release, SUM(NOT ($isRelease)) AS dev FROM pings WHERE day >= date('now', '-$Days day') GROUP BY day ORDER BY day DESC"
Query "New installs, last $Days days" "SELECT first_day AS day, SUM(kind = 'release') AS release, SUM(kind = 'dev') AS dev FROM (SELECT install_id, MIN(day) AS first_day, MAX($kind) AS kind FROM pings GROUP BY install_id) WHERE first_day >= date('now', '-$Days day') GROUP BY first_day ORDER BY first_day DESC"
Query "Versions, last $Days days" "SELECT $ver AS version, COUNT(DISTINCT install_id) AS installs, MAX(day) AS last_day FROM pings WHERE day >= date('now', '-$Days day') GROUP BY 1 ORDER BY last_day DESC, installs DESC"
Query "Crashes, last $Days days" "SELECT COUNT(*) AS n, $ver AS version, source, substr(replace(details, char(10), ' '), 1, 110) AS head, MAX(received) AS last FROM crashes WHERE received >= datetime('now', '-$Days day') GROUP BY 2, 3, 4 ORDER BY n DESC LIMIT 20"
