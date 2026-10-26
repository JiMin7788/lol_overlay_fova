<#
.SYNOPSIS
    Local end-to-end update test with a separate package id (FovaUpdateTest): does an update that was
    downloaded while Fova sat in the tray apply on the next launch after the process was killed?

.DESCRIPTION
    0. Path guard (every mode): -Root, -UserDataDir and -UpdateSource are refused when they equal, are
       inside or contain %LocalAppData%\Fova, %LocalAppData%\FovaApp or %LocalAppData%\Fova\config
       (plus any -ExtraProtectedDir), both as written and with every junction/symlink resolved.
       -SelfTest checks it with -GuardOnly runs only (nothing is built, installed or deleted).
    1. Guard: FOVA_USER_DATA_DIR / FOVA_UPDATE_SOURCE set and non-empty, no leftover
       %LocalAppData%\FovaUpdateTest. Snapshot the protected state (the user's HKCU Run value 'Fova',
       %LocalAppData%\Fova\config\user_config.json, every file under %LocalAppData%\FovaApp, the file
       listing of %LocalAppData%\Fova\cache).
    2. build-release.ps1 + vpk pack -u FovaUpdateTest for 0.0.1 and 0.0.2 (the second pack writes a delta).
    3. Install 0.0.1 with the test Setup.exe (the copy kept after the 0.0.1 pack); the data dir is
       seeded with general.autoStartWithGame=true.
    4. Tray: wait for 'downloaded 0.0.2 (current 0.0.1); applies on exit', close HOME, still alive at 0.0.1.
    5. Killed in tray: Stop-Process -Force, start current\fova.exe, wait for 'up to date (0.0.2)'.
    6. finally: uninstall, check nothing is left, compare the protected state.
    7. <Root>/RESULT.md.

    -Beta (lolF05) replaces steps 2-5; guard, protected state, seed, kill rule, uninstall and report stay:
    2. build-release.ps1 + vpk pack 0.0.1 on the default channel, then build-release.ps1 -Beta + vpk pack
       --channel beta for 0.0.2-beta.1 and 0.0.2-beta.2. Asserts releases.win.json and releases.beta.json,
       no beta version in releases.win.json and a beta delta.
    a. light: install 0.0.1, wait for 'up to date (0.0.1)', stop it; app.log has no 'beta build:' line.
    b. switch: the 0.0.2-beta.1 Setup --silent over that install; sq.version 0.0.2-beta.1, same install dir,
       exactly one 'Fova Update Test*.lnk' under the Start menu Programs folder; the <channel> is recorded.
    c. beta-update: start it, wait for 'downloaded 0.0.2-beta.2 (current 0.0.2-beta.1); applies on exit' and
       the app.log line 'beta build: enemyJunglerSpotted started, enemyItemAlert started; beta_kill [...]',
       Stop-Process -Force, relaunch, wait for 'up to date (0.0.2-beta.2)'.
    d. settings: the seeded values still hold in <UserDataDir>\config\user_config.json.

    Never publishes or uploads: no -Publish, no vpk upload/download, no gh. Only processes whose path is
    under the test install are ever stopped.

.EXAMPLE
    pwsh -NoProfile -File packaging/test-local-update.ps1
    pwsh -NoProfile -File packaging/test-local-update.ps1 -Beta
    pwsh -NoProfile -File packaging/test-local-update.ps1 -GuardOnly -Beta
    pwsh -NoProfile -File packaging/test-local-update.ps1 -GuardOnly
    pwsh -NoProfile -File packaging/test-local-update.ps1 -SelfTest
    pwsh -NoProfile -File packaging/test-local-update.ps1 -SnapshotTo before.json
#>
[CmdletBinding()]
param(
    [string] $Root = 'artifacts/update-test',
    [AllowEmptyString()] [string] $UserDataDir,
    [AllowEmptyString()] [string] $UpdateSource,
    [AllowEmptyString()] [string] $InstallDir,
    [switch] $GuardOnly,
    [switch] $SelfTest,
    [string] $SnapshotTo,
    [string[]] $ExtraProtectedDir,
    [ValidateSet('runvalue', 'userconfig', 'fovaapp')] [string] $InjectFailure,
    [switch] $Beta
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

function Resolve-Dir([string] $p) {
    if ([string]::IsNullOrWhiteSpace($p)) { return '' }
    if (-not [IO.Path]::IsPathRooted($p)) { $p = Join-Path $repo $p }
    [IO.Path]::GetFullPath($p).TrimEnd('\')
}

# A full path with every existing component that is a junction or symlink replaced by its target
# (targets resolved the same way); the components after the first missing one are kept as written.
function Resolve-Links([string] $path, [int] $depth = 0) {
    if (-not $path) { return '' }
    # AI-NOTE: a link that leads back to itself would recurse forever; no real chain is 32 links long.
    if ($depth -gt 32) { throw "Resolve-Links: more than 32 links resolving $path" }
    $full = [IO.Path]::GetFullPath($path)
    $root = [IO.Path]::GetPathRoot($full)
    $parts = $full.Substring($root.Length).Split([char[]]'\/', [StringSplitOptions]::RemoveEmptyEntries)
    $cur = $root
    for ($i = 0; $i -lt $parts.Count; $i++) {
        $next = Join-Path $cur $parts[$i]
        $item = Get-Item -LiteralPath $next -Force -ErrorAction SilentlyContinue
        if (-not $item) { return (Join-Path $cur ($parts[$i..($parts.Count - 1)] -join '\')).TrimEnd('\') }
        if ($item.LinkType -in 'Junction', 'SymbolicLink') {
            $target = @($item.Target)[0]
            if (-not [IO.Path]::IsPathRooted($target)) { $target = Join-Path $cur $target }
            $next = Resolve-Links $target ($depth + 1)
        }
        $cur = $next
    }
    # AI-NOTE: not Resolve-Dir — GetFullPath('C:') is the current dir on C:, not the drive root.
    $cur.TrimEnd('\')
}

$Root = Resolve-Dir $Root
if (-not $PSBoundParameters.ContainsKey('UserDataDir')) { $UserDataDir = Join-Path $Root 'data' }
if (-not $PSBoundParameters.ContainsKey('UpdateSource')) { $UpdateSource = Join-Path $Root 'releases' }

# AI-NOTE: set before anything else so every child (Setup, Update.exe, the app, the uninstall hook)
# inherits them: test mode keeps the app's state under the data dir and makes Autostart a no-op,
# and the update source replaces GitHub. An empty value removes the variable, which Test-Guards refuses.
$env:FOVA_USER_DATA_DIR = Resolve-Dir $UserDataDir
$env:FOVA_UPDATE_SOURCE = Resolve-Dir $UpdateSource

$defaultInstallDir = Join-Path $env:LOCALAPPDATA 'FovaUpdateTest'
if (-not $PSBoundParameters.ContainsKey('InstallDir')) { $InstallDir = $defaultInstallDir }
$InstallDir = Resolve-Dir $InstallDir

# ── path guard: the user's installed Fova ───────────────────────────────────────────────────────

# AI-NOTE: -ExtraProtectedDir only adds to the list, so it can never weaken the guard; -SelfTest uses
# it to point a junction at a temp stand-in instead of the real %LocalAppData%\Fova.
$protectedDirs = @(@('Fova', 'FovaApp', 'Fova\config') | ForEach-Object { Resolve-Dir (Join-Path $env:LOCALAPPDATA $_) }) +
    @($ExtraProtectedDir | Where-Object { $_ } | ForEach-Object { Resolve-Dir $_ })

# How a resolved path overlaps a protected dir ('equals …', 'is inside …', 'contains …'), or $null.
function Get-ProtectedOverlap([string] $path) {
    if (-not $path) { return $null }
    foreach ($p in $protectedDirs) {
        if ([string]::Equals($path, $p, [StringComparison]::OrdinalIgnoreCase)) { return "equals $p" }
        if ($path.StartsWith($p + '\', [StringComparison]::OrdinalIgnoreCase)) { return "is inside $p" }
        # AI-NOTE: Resolve-Dir trims 'C:\' to 'C:', so a drive root is caught here too.
        if ($p.StartsWith($path + '\', [StringComparison]::OrdinalIgnoreCase)) { return "contains $p" }
    }
    return $null
}

function Reset-OwnDir([string] $dir) {
    # AI-NOTE: second line of defence behind the path guard below: never reset the user's Fova folders.
    $overlap = Get-ProtectedOverlap (Resolve-Links $dir)
    if ($overlap) { throw "Reset-OwnDir refuses $dir — it $overlap (the user's installed Fova)" }
    # AI-NOTE: a stale update.log or package from an earlier run would satisfy the waits below with old
    # lines. Only dirs under -Root are this script's own output; any other dir must already be empty.
    if (Test-Path -LiteralPath $dir) {
        if ($dir.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $dir -Recurse -Force
        } elseif (Get-ChildItem -LiteralPath $dir -Force | Select-Object -First 1) {
            throw "$dir is not under -Root and not empty"
        }
    }
    New-Item -ItemType Directory -Force $dir | Out-Null
}

# AI-NOTE: checked before every mode (-SelfTest, -SnapshotTo, -GuardOnly, the run), so nothing below
# can create, reset or delete a dir that equals, is inside or contains the user's Fova folders.
$pathParams = [ordered]@{ '-Root' = $Root; '-UserDataDir' = $env:FOVA_USER_DATA_DIR; '-UpdateSource' = $env:FOVA_UPDATE_SOURCE }
foreach ($name in $pathParams.Keys) {
    $overlap = Get-ProtectedOverlap $pathParams[$name]
    if ($overlap) {
        Write-Output "REFUSED — $name $($pathParams[$name]) $overlap (the user's installed Fova)"
        exit 1
    }
}
# AI-NOTE: the strings above can be harmless while a junction or symlink in them leads into a protected
# folder (lolF03 QA: a %TEMP% junction to Fova\config passed -GuardOnly). A target that equals or
# contains a protected folder is refused with the same line.
foreach ($name in $pathParams.Keys) {
    $path = $pathParams[$name]
    $target = Resolve-Links $path
    if (Get-ProtectedOverlap $target) {
        Write-Output "GUARD: REFUSED — $name '$path' resolves to '$target' inside a protected folder"
        exit 1
    }
}

$runKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\FovaUpdateTest'
$userConfig = Join-Path $env:LOCALAPPDATA 'Fova\config\user_config.json'
$fovaAppDir = Join-Path $env:LOCALAPPDATA 'FovaApp'
$cacheDir = Join-Path $env:LOCALAPPDATA 'Fova\cache'

# ── guards and protected state ──────────────────────────────────────────────────────────────────

function Test-Guards {
    if ([string]::IsNullOrWhiteSpace($env:FOVA_USER_DATA_DIR)) { return 'FOVA_USER_DATA_DIR empty' }
    if ([string]::IsNullOrWhiteSpace($env:FOVA_UPDATE_SOURCE)) { return 'FOVA_UPDATE_SOURCE empty' }
    if ([string]::IsNullOrWhiteSpace($InstallDir)) { return 'InstallDir empty' }
    if (Test-Path -LiteralPath $InstallDir) { return "leftover install at $InstallDir" }
    return $null
}

function Get-FileState([string] $path) {
    $f = Get-Item -LiteralPath $path -Force
    try { $hash = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash }
    catch { $hash = "unreadable: $($_.Exception.Message)" }
    [ordered]@{ path = $f.FullName; sha256 = $hash; mtime = $f.LastWriteTimeUtc.ToString('o') }
}

function Get-ProtectedState {
    $run = $null
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKeyPath)
    if ($key) {
        try {
            $value = $key.GetValue('Fova', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if ($null -ne $value) {
                $kind = $key.GetValueKind('Fova')
                $bytes = if ($value -is [byte[]]) { $value } else { [Text.Encoding]::Unicode.GetBytes(($value -join "`0")) }
                $run = [ordered]@{ kind = "$kind"; bytes = [Convert]::ToBase64String($bytes) }
            }
        } finally { $key.Dispose() }
    }
    $config = if (Test-Path -LiteralPath $userConfig) { Get-FileState $userConfig } else { $null }
    $app = @()
    if (Test-Path -LiteralPath $fovaAppDir) {
        $app = @(Get-ChildItem -LiteralPath $fovaAppDir -Recurse -File -Force | Sort-Object FullName |
            ForEach-Object { Get-FileState $_.FullName })
    }
    # AI-NOTE: names, sizes and mtimes only, no hashes: the cache holds thousands of icons.
    $cache = [ordered]@{ exists = $false; files = @() }
    if (Test-Path -LiteralPath $cacheDir -PathType Container) {
        $cache.exists = $true
        $cache.files = @(Get-ChildItem -LiteralPath $cacheDir -Recurse -File -Force | Sort-Object FullName |
            ForEach-Object { [ordered]@{ path = $_.FullName.Substring($cacheDir.Length + 1); size = $_.Length; mtime = $_.LastWriteTimeUtc.ToString('o') } })
    }
    [ordered]@{ runvalue = $run; userconfig = $config; fovaapp = $app; cache = $cache }
}

function ConvertTo-StateJson($state) { ConvertTo-Json -InputObject $state -Depth 6 }

# 'different — <what>' for two cache listings (added / removed / changed files by relative path), or $null.
function Compare-CacheListing($before, $after) {
    $old = @{}; foreach ($f in $before.files) { $old[$f.path] = "$($f.size)|$($f.mtime)" }
    $new = @{}; foreach ($f in $after.files) { $new[$f.path] = "$($f.size)|$($f.mtime)" }
    $parts = [Collections.Generic.List[string]]::new()
    if ($before.exists -ne $after.exists) { $parts.Add($(if ($after.exists) { 'dir created' } else { 'dir removed' })) }
    $kinds = [ordered]@{
        added   = @($new.Keys | Where-Object { -not $old.ContainsKey($_) })
        removed = @($old.Keys | Where-Object { -not $new.ContainsKey($_) })
        changed = @($new.Keys | Where-Object { $old.ContainsKey($_) -and $old[$_] -cne $new[$_] })
    }
    foreach ($k in $kinds.Keys) {
        $list = @($kinds[$k] | Sort-Object)
        if (-not $list.Count) { continue }
        # AI-NOTE: a Data Dragon patch move touches thousands of files; the first 10 paths say what
        # happened, the full listings are in protected_before.json / protected_after.json.
        $more = if ($list.Count -gt 10) { ", … +$($list.Count - 10) more" } else { '' }
        $parts.Add("$k $($list.Count): $(($list | Select-Object -First 10) -join ', ')$more")
    }
    if ($parts.Count) { 'different — ' + ($parts -join '; ') } else { $null }
}

function Compare-ProtectedState($before, $after) {
    $result = [ordered]@{}
    foreach ($check in 'runvalue', 'userconfig', 'fovaapp') {
        $a = ConvertTo-Json -InputObject $before[$check] -Depth 6 -Compress
        $b = ConvertTo-Json -InputObject $after[$check] -Depth 6 -Compress
        $result[$check] = if ($a -ceq $b) { 'same' } else { 'different' }
    }
    $cacheDiff = Compare-CacheListing $before['cache'] $after['cache']
    $result['cache'] = if ($cacheDiff) { $cacheDiff } else { 'same' }
    # AI-NOTE: test only — flipped AFTER the real comparison, so the abort path below runs for real.
    if ($InjectFailure) { $result[$InjectFailure] = 'different' }
    $result
}

# An app.log line without the 'yyyy-MM-dd HH:mm:ss.fff ' stamp AppComposition.Log puts in front of it.
function Get-LogMessage([string] $line) { $line -replace '^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} ', '' }

# The versions a Velopack releases.<channel>.json lists.
function Get-FeedVersions([string] $json) {
    $feed = ConvertFrom-Json -InputObject $json
    if ($null -eq $feed.Assets) { throw "no Assets in the feed" }
    @($feed.Assets | ForEach-Object { $_.Version })
}

if ($SelfTest) {
    # AI-NOTE: every child runs with -GuardOnly, which never creates, deletes or starts anything, so a
    # broken path guard shows up as a failed case (exit 0), never as a real run against the user's Fova.
    function Get-ProtectedListing {
        @($protectedDirs | Where-Object { Test-Path -LiteralPath $_ } |
            ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -File -Force -ErrorAction SilentlyContinue } |
            Sort-Object FullName -Unique |
            ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.ToString('o'))" })
    }
    $la = $env:LOCALAPPDATA
    $fova = Join-Path $la 'Fova'
    # AI-NOTE: the junction cases link to an empty stand-in under %TEMP% that the children get through
    # -ExtraProtectedDir; the self-test never creates a link to the real %LocalAppData%\Fova.
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "fova-guard-selftest-$PID"
    if (Get-ProtectedOverlap (Resolve-Links $tmp)) { throw "self-test temp dir $tmp overlaps a protected folder" }
    $standIn = Join-Path $tmp 'protected'
    $link = Join-Path $tmp 'link'
    New-Item -ItemType Directory -Force $standIn | Out-Null
    New-Item -ItemType Junction -Path $link -Target $standIn | Out-Null
    $cases = @(
        @{ args = @('-Root', $fova); ok = $false; text = "REFUSED — -Root $fova equals" }
        @{ args = @('-Root', $la); ok = $false; text = "REFUSED — -Root $la contains" }
        @{ args = @('-Root', "$la\FOVA\"); ok = $false; text = "REFUSED — -Root $la\FOVA equals" }
        @{ args = @('-UserDataDir', "$fova\config"); ok = $false; text = "REFUSED — -UserDataDir $fova\config is inside" }
        @{ args = @('-UserDataDir', "$la\FovaApp\x"); ok = $false; text = "REFUSED — -UserDataDir $la\FovaApp\x is inside" }
        @{ args = @('-UpdateSource', "$fova\..\Fova"); ok = $false; text = "REFUSED — -UpdateSource $fova equals" }
        @{ args = @(); ok = $true; text = 'GUARD: OK' }
        @{ args = @('-Beta'); ok = $true; text = 'GUARD: OK' }
        @{ args = @('-Beta', '-Root', $fova); ok = $false; text = "REFUSED — -Root $fova equals" }
        @{ args = @('-UserDataDir', ''); ok = $false; text = 'GUARD: REFUSED — FOVA_USER_DATA_DIR empty' }
        @{ args = @('-UpdateSource', ''); ok = $false; text = 'GUARD: REFUSED — FOVA_UPDATE_SOURCE empty' }
        @{ args = @('-InjectFailure', 'runvalue'); ok = $true; text = 'GUARD: OK' }
        @{ args = @('-InjectFailure', 'userconfig'); ok = $true; text = 'GUARD: OK' }
        @{ args = @('-InjectFailure', 'fovaapp'); ok = $true; text = 'GUARD: OK' }
        @{ args = @('-InjectFailure', 'bogus'); ok = $false; text = 'InjectFailure' }
        @{ args = @('-UserDataDir', "$link\data", '-ExtraProtectedDir', $standIn); ok = $false
           text = "GUARD: REFUSED — -UserDataDir '$link\data' resolves to '$standIn\data' inside a protected folder" }
        @{ args = @('-Root', $link, '-ExtraProtectedDir', $standIn); ok = $false
           text = "GUARD: REFUSED — -Root '$link' resolves to '$standIn' inside a protected folder" }
        # Control: the same junction is fine when its target is not protected.
        @{ args = @('-UserDataDir', "$link\data"); ok = $true; text = 'GUARD: OK' }
    )
    $pwsh = (Get-Process -Id $PID).Path
    $before = Get-ProtectedListing
    $failed = [Collections.Generic.List[string]]::new()
    try {
        foreach ($c in $cases) {
            $a = $c.args
            $out = (& $pwsh -NoProfile -File $PSCommandPath -GuardOnly @a 2>&1 | Out-String).Trim()
            $code = $LASTEXITCODE
            $pass = (($code -eq 0) -eq $c.ok) -and $out.Contains($c.text)
            if (-not $pass) { $failed.Add("-GuardOnly $($a -join ' ')") }
            Write-Output "$(if ($pass) { 'ok  ' } else { 'FAIL' }) -GuardOnly $($a -join ' ') -> exit $code; $out"
        }
        # AI-NOTE: $link exists, is not under -Root and its target is empty, so a Reset-OwnDir that missed
        # the junction would only New-Item -Force an existing dir; it can never delete anything.
        $script:protectedDirs += Resolve-Dir $standIn
        try { Reset-OwnDir $link; $msg = 'no exception' } catch { $msg = $_.Exception.Message }
        $script:protectedDirs = @($protectedDirs | Select-Object -SkipLast 1)
        $pass = $msg.StartsWith("Reset-OwnDir refuses $link — it equals $standIn")
        if (-not $pass) { $failed.Add('Reset-OwnDir junction') }
        Write-Output "$(if ($pass) { 'ok  ' } else { 'FAIL' }) Reset-OwnDir $link (junction to a protected stand-in) -> $msg"
    } finally {
        # AI-NOTE: Directory.Delete without recursion removes only the junction, never its target's files.
        [IO.Directory]::Delete($link)
        Remove-Item -LiteralPath $tmp -Recurse -Force
    }
    # Cache comparison on made-up listings: nothing on disk is read.
    $file = { param($p, $s) [ordered]@{ path = $p; size = $s; mtime = '2026-01-01T00:00:00.0000000Z' } }
    $absent = [ordered]@{ exists = $false; files = @() }
    $cacheCases = @(
        @{ name = 'cache unchanged'; b = $absent; a = $absent; text = 'same' }
        @{ name = 'cache added/removed/changed'
           b = [ordered]@{ exists = $true; files = @((& $file 'a.png' 1), (& $file 'ddragon\b.png' 2)) }
           a = [ordered]@{ exists = $true; files = @((& $file 'ddragon\b.png' 3), (& $file 'c.png' 1)) }
           text = 'different — added 1: c.png; removed 1: a.png; changed 1: ddragon\b.png' }
        @{ name = 'cache dir created'; b = $absent; a = [ordered]@{ exists = $true; files = @((& $file 'x\y.png' 5)) }
           text = 'different — dir created; added 1: x\y.png' }
    )
    foreach ($c in $cacheCases) {
        $got = (Compare-ProtectedState ([ordered]@{ cache = $c.b }) ([ordered]@{ cache = $c.a }))['cache']
        $pass = $got -ceq $c.text
        if (-not $pass) { $failed.Add($c.name) }
        Write-Output "$(if ($pass) { 'ok  ' } else { 'FAIL' }) $($c.name) -> $got"
    }
    # -Beta log and feed parsing on made-up lines: nothing on disk is read.
    $parseCases = @(
        @{ name = 'app.log stamp stripped'; got = Get-LogMessage '2026-10-26 12:00:01.234 beta build: x'; text = 'beta build: x' }
        @{ name = 'app.log header kept'; got = Get-LogMessage '=== BUILD 0.0.1 | started 2026-10-26 12:00:00 ==='
           text = '=== BUILD 0.0.1 | started 2026-10-26 12:00:00 ===' }
        @{ name = 'feed versions'; got = (Get-FeedVersions '{"Assets":[{"Version":"0.0.1"},{"Version":"0.0.2-beta.1"}]}') -join ','
           text = '0.0.1,0.0.2-beta.1' }
    )
    foreach ($c in $parseCases) {
        $pass = $c.got -ceq $c.text
        if (-not $pass) { $failed.Add($c.name) }
        Write-Output "$(if ($pass) { 'ok  ' } else { 'FAIL' }) $($c.name) -> $($c.got)"
    }
    # AI-NOTE: $la exists and is not under -Root, so even a Reset-OwnDir without its own check would only
    # throw 'not under -Root and not empty' here; it can never delete or create anything.
    try { Reset-OwnDir $la; $msg = 'no exception' } catch { $msg = $_.Exception.Message }
    $pass = $msg.StartsWith("Reset-OwnDir refuses $la — it contains")
    if (-not $pass) { $failed.Add('Reset-OwnDir') }
    Write-Output "$(if ($pass) { 'ok  ' } else { 'FAIL' }) Reset-OwnDir $la -> $msg"
    $after = Get-ProtectedListing
    $same = ($before -join "`n") -ceq ($after -join "`n")
    if (-not $same) { $failed.Add('protected listing') }
    Write-Output "$(if ($same) { 'ok  ' } else { 'FAIL' }) protected listing (names, sizes, mtimes): $($before.Count) files before, $($after.Count) after, $(if ($same) { 'identical' } else { 'different' })"
    if ($failed.Count) { Write-Output "SELFTEST: FAIL — $($failed -join ', ')"; exit 1 }
    Write-Output 'SELFTEST: PASS'
    exit 0
}

if ($SnapshotTo) {
    ConvertTo-StateJson (Get-ProtectedState) | Set-Content -LiteralPath $SnapshotTo -Encoding utf8
    exit 0
}

if ($GuardOnly) {
    $reason = Test-Guards
    if ($reason) { Write-Output "GUARD: REFUSED — $reason"; exit 1 }
    Write-Output 'GUARD: OK'
    exit 0
}

if ($InstallDir -ne $defaultInstallDir) {
    # The real Setup always installs to %LocalAppData%\FovaUpdateTest; another dir would test nothing.
    Write-Output "REFUSED — a non-default -InstallDir is only allowed with -GuardOnly"
    exit 1
}

$reason = Test-Guards
if ($reason) {
    Write-Output "REFUSED — $reason"
    if ($reason -like 'leftover install*') {
        # AI-NOTE: the env must be set for the uninstall too, or the app's uninstall hook
        # (Autostart.Remove) runs outside test mode and deletes the user's Run value 'Fova'.
        Write-Output "Remove it first (same shell):"
        Write-Output "  `$env:FOVA_USER_DATA_DIR = '$env:FOVA_USER_DATA_DIR'; & '$InstallDir\Update.exe' --uninstall"
    }
    exit 1
}

# AI-NOTE: an installed Fova runs LegacyInstall.Migrate on every start, and that silently runs the old
# Inno Setup uninstaller, which deletes %LocalAppData%\Fova. The test app must never do that.
$legacyKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7F3A1C64-5B2E-4E7B-9D14-2C6A8F0B51D3}_is1')
if ($legacyKey) {
    $legacyKey.Dispose()
    Write-Output "REFUSED — an old Inno Setup Fova install is registered; the test app would uninstall it"
    exit 1
}

# ── run ─────────────────────────────────────────────────────────────────────────────────────────

$data = $env:FOVA_USER_DATA_DIR
$releases = $env:FOVA_UPDATE_SOURCE
$build = Join-Path $Root 'build'
$setupDir = Join-Path $Root 'setup-0.0.1'
$betaSetupDir = Join-Path $Root 'setup-0.0.2-beta.1'
$updateLog = Join-Path $data 'logs\update.log'
$appLog = Join-Path $data 'logs\app.log'
$sqVersion = Join-Path $InstallDir 'current\sq.version'
$times = [ordered]@{}
$observed = [Collections.Generic.List[string]]::new()
$safety = [ordered]@{}
$failure = $null
$step = 'guard'

function Mark([string] $name) { $script:step = $name; $script:times[$name] = (Get-Date).ToString('HH:mm:ss') }

function Test-UnderInstall([string] $path) {
    $path -and $path.StartsWith($InstallDir + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Get-TestProcesses([string] $name) {
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        (Test-UnderInstall $_.Path) -and (-not $name -or $_.ProcessName -eq $name) })
}

# The kill rule: nothing outside the test install is ever stopped.
function Stop-TestProcess($p) {
    if (-not (Test-UnderInstall $p.Path)) { throw "refusing to stop $($p.Id) at $($p.Path)" }
    Stop-Process -Id $p.Id -Force
    $p.WaitForExit(10000) | Out-Null
}

function Get-InstalledVersion {
    if (-not (Test-Path -LiteralPath $sqVersion)) { return $null }
    if ((Get-Content -LiteralPath $sqVersion -Raw) -match '<version>\s*([^<\s]+)\s*</version>') { return $Matches[1] }
    return '?'
}

function Get-LogLines([string] $path = $updateLog) {
    if (Test-Path -LiteralPath $path) { @(Get-Content -LiteralPath $path) } else { @() }
}

# Waits for a NEW update.log line (index >= $from) containing $text.
function Wait-LogLine([string] $text, [int] $from, [int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        # AI-NOTE: @() — a one-line log comes back as a bare string, and $lines[$i] would be a char.
        $lines = @(Get-LogLines)
        for ($i = $from; $i -lt $lines.Count; $i++) { if ($lines[$i].Contains($text)) { return $lines[$i] } }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $null
}

# Waits for a NEW app.log line (index >= $from) whose message (stamp removed) starts with $prefix.
function Wait-AppLogLine([string] $prefix, [int] $from, [int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        $lines = @(Get-LogLines $appLog)
        for ($i = $from; $i -lt $lines.Count; $i++) { if ((Get-LogMessage $lines[$i]).StartsWith($prefix)) { return $lines[$i] } }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Wait-AppProcess([int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        $p = Get-TestProcesses 'fova' | Select-Object -First 1
        if ($p) { return $p }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Write-SeedConfig {
    # Seeded before the first start: closing HOME then hides to the tray (HomeWindow Closing).
    # AI-NOTE: telemetry and ads off so the test install never reports to the real telemetry Worker;
    # the first-run guide and telemetry notice are marked shown so HOME opens plain.
    $seed = [ordered]@{
        general   = [ordered]@{ autoStartWithGame = $true; firstRunGuideShown = $true }
        telemetry = [ordered]@{ enabled = $false; noticeShown = $true }
        ads       = [ordered]@{ enabled = $false }
    }
    New-Item -ItemType Directory -Force (Join-Path $data 'config') | Out-Null
    ConvertTo-Json -InputObject $seed -Depth 4 | Set-Content -LiteralPath (Join-Path $data 'config\user_config.json') -Encoding utf8
}

# Runs a test Setup with --silent and records its exit code.
function Invoke-TestSetup([string] $path) {
    # AI-NOTE: not Start-Process -Wait — it waits for the whole process tree, and Setup starts the app.
    $sp = Start-Process -FilePath $path -ArgumentList '--silent' -PassThru
    if (-not $sp.WaitForExit(300000)) { throw "$(Split-Path -Leaf $path) did not exit within 300 s" }
    $observed.Add("$(Split-Path -Leaf $path) --silent exit code: $($sp.ExitCode)")
}

# The app Setup started (waited for up to $seconds), else one started from current\fova.exe.
function Get-OrStartApp([int] $seconds) {
    $app = Wait-AppProcess $seconds
    if ($app) { $observed.Add("Setup started the app (pid $($app.Id)); reused"); return $app }
    Start-Process -FilePath (Join-Path $InstallDir 'current\fova.exe') | Out-Null
    $app = Wait-AppProcess 30
    if (-not $app) { throw "the app did not start" }
    $observed.Add("app started by the script (pid $($app.Id))")
    $app
}

function Invoke-TestPack([string] $v, [string] $stage, [string[]] $extra = @()) {
    vpk pack -u FovaUpdateTest -v $v -p $stage -e fova.exe -r win-x64 `
        --packTitle "Fova Update Test" --packAuthors Fova `
        --framework net8.0-x64-desktop `
        -o $releases @extra
    if ($LASTEXITCODE -ne 0) { throw "vpk pack $v failed (exit $LASTEXITCODE)" }
}

function Get-StartMenuShortcuts {
    @(Get-ChildItem -LiteralPath ([Environment]::GetFolderPath('Programs')) -Recurse -Filter 'Fova Update Test*.lnk' -ErrorAction SilentlyContinue)
}

# lolF05: light 0.0.1 install -> the 0.0.2-beta.1 Setup over it -> beta-channel update to 0.0.2-beta.2.
function Invoke-BetaRun {
    Mark 'build'
    & (Join-Path $PSScriptRoot 'build-release.ps1') -Version 0.0.1 -SkipTests -OutDir $build
    Set-Location $repo
    Invoke-TestPack '0.0.1' (Join-Path $build 'fova-0.0.1')
    New-Item -ItemType Directory -Force $setupDir | Out-Null
    Copy-Item -LiteralPath (Join-Path $releases 'FovaUpdateTest-win-Setup.exe') -Destination $setupDir -Force
    $betaSetup = $null
    foreach ($v in '0.0.2-beta.1', '0.0.2-beta.2') {
        & (Join-Path $PSScriptRoot 'build-release.ps1') -Version $v -Beta -SkipTests -OutDir $build
        Set-Location $repo
        Invoke-TestPack $v (Join-Path $build "fova-$v-beta") '--channel', 'beta'
        if ($v -eq '0.0.2-beta.1') {
            # AI-NOTE: as with 0.0.1, the beta.2 pack overwrites the beta Setup, so beta.1's is kept aside.
            # Found by pattern rather than assumed, so a different vpk name shows up in the report.
            $betaSetup = Get-ChildItem -LiteralPath $releases -Filter '*beta*Setup.exe' | Select-Object -First 1
            if (-not $betaSetup) { throw "no beta Setup in $releases ($((Get-ChildItem -LiteralPath $releases).Name -join ', '))" }
            $observed.Add("beta Setup name: $($betaSetup.Name)")
            New-Item -ItemType Directory -Force $betaSetupDir | Out-Null
            Copy-Item -LiteralPath $betaSetup.FullName -Destination $betaSetupDir -Force
        }
    }
    $observed.Add("release files: $((Get-ChildItem -LiteralPath $releases -File | Sort-Object Name).Name -join ', ')")
    foreach ($f in 'releases.win.json', 'releases.beta.json') {
        if (-not (Test-Path -LiteralPath (Join-Path $releases $f))) { throw "no $f in $releases" }
    }
    $winVersions = Get-FeedVersions (Get-Content -LiteralPath (Join-Path $releases 'releases.win.json') -Raw)
    $betaVersions = Get-FeedVersions (Get-Content -LiteralPath (Join-Path $releases 'releases.beta.json') -Raw)
    $observed.Add("releases.win.json versions: $($winVersions -join ', '); releases.beta.json versions: $($betaVersions -join ', ')")
    if (@($winVersions | Where-Object { $_ -match 'beta' }).Count) { throw "releases.win.json lists a beta version: $($winVersions -join ', ')" }
    $delta = Get-ChildItem -LiteralPath $releases -Filter '*beta*-delta.nupkg' | Select-Object -First 1
    if (-not $delta) { throw "no beta *-delta.nupkg in $releases" }
    $observed.Add("beta delta package: $($delta.Name) ($([math]::Round($delta.Length / 1MB, 2)) MB)")

    Mark 'light'
    Write-SeedConfig
    Invoke-TestSetup (Join-Path $setupDir 'FovaUpdateTest-win-Setup.exe')
    $installed = Get-InstalledVersion
    $observed.Add("installed sq.version: $installed")
    if ($installed -ne '0.0.1') { throw "sq.version after the light install is '$installed', expected 0.0.1" }
    $app = Get-OrStartApp 30
    $line = Wait-LogLine 'up to date (0.0.1)' 0 120
    if (-not $line) { throw "no 'up to date (0.0.1)' within 120 s; update.log: $((Get-LogLines) -join ' | ')" }
    $observed.Add("update.log (light): $line")
    foreach ($p in Get-TestProcesses) { Stop-TestProcess $p }
    $observed.Add("light app stopped (pid $($app.Id))")
    if (-not (Test-Path -LiteralPath $appLog)) { throw "no app.log at $appLog after the light run" }
    $lightLog = @(Get-LogLines $appLog)
    $lightBeta = @($lightLog | Where-Object { (Get-LogMessage $_).StartsWith('beta build:') })
    $observed.Add("app.log after the light run: $($lightLog.Count) lines, $($lightBeta.Count) starting 'beta build:'")
    if ($lightBeta.Count) { throw "the light app.log has a beta line: $($lightBeta[0])" }
    $observed.Add("Start menu shortcuts after the light install: $((Get-StartMenuShortcuts).Count)")

    Mark 'switch'
    $logMark = @(Get-LogLines).Count
    $appMark = $lightLog.Count
    Invoke-TestSetup (Join-Path $betaSetupDir $betaSetup.Name)
    $installed = Get-InstalledVersion
    $observed.Add("sq.version after the beta Setup: $installed")
    if ($installed -ne '0.0.2-beta.1') { throw "sq.version after the beta Setup is '$installed', expected 0.0.2-beta.1" }
    if (-not (Test-Path -LiteralPath (Join-Path $InstallDir 'current\fova.exe'))) { throw "no current\fova.exe under $InstallDir after the beta Setup" }
    $ukey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallKeyPath)
    $location = if ($ukey) { try { $ukey.GetValue('InstallLocation') } finally { $ukey.Dispose() } } else { $null }
    $observed.Add("install dir: $InstallDir (uninstall key InstallLocation: $(if ($location) { $location } else { '(none)' }))")
    if ($location -and (Resolve-Dir $location) -ne $InstallDir) { throw "the beta install is registered at $location, not $InstallDir" }
    $channel = if ((Get-Content -LiteralPath $sqVersion -Raw) -match '<channel>\s*([^<\s]+)\s*</channel>') { $Matches[1] } else { '(none)' }
    $observed.Add("sq.version <channel>: $channel")
    $links = Get-StartMenuShortcuts
    $observed.Add("Start menu shortcuts after the beta Setup: $($links.Count) ($($links.FullName -join ', '))")
    if ($links.Count -ne 1) { throw "$($links.Count) 'Fova Update Test*.lnk' under the Start menu Programs folder, expected 1" }

    Mark 'beta-update'
    $app = Get-OrStartApp 10
    $line = Wait-LogLine 'downloaded 0.0.2-beta.2 (current 0.0.2-beta.1); applies on exit' $logMark 120
    if (-not $line) { throw "no 'downloaded 0.0.2-beta.2 (current 0.0.2-beta.1); applies on exit' within 120 s; update.log: $((Get-LogLines) -join ' | ')" }
    $observed.Add("update.log (beta): $line")
    $line = Wait-AppLogLine 'beta build: enemyJunglerSpotted started, enemyItemAlert started; beta_kill [' $appMark 120
    if (-not $line) {
        $seen = @(Get-LogLines $appLog | Select-Object -Skip $appMark | Where-Object { (Get-LogMessage $_).StartsWith('beta build:') })
        throw "no 'beta build: enemyJunglerSpotted started, enemyItemAlert started; beta_kill [' app.log line within 120 s; beta lines: $(if ($seen) { $seen -join ' | ' } else { '(none)' })"
    }
    $observed.Add("app.log (beta): $line")
    $logMark = @(Get-LogLines).Count
    Stop-TestProcess $app
    $observed.Add("killed pid $($app.Id) (Stop-Process -Force); sq.version $(Get-InstalledVersion)")
    Start-Process -FilePath (Join-Path $InstallDir 'current\fova.exe') | Out-Null
    $line = Wait-LogLine 'up to date (0.0.2-beta.2)' $logMark 120
    $nextVersion = Get-InstalledVersion
    foreach ($l in @(@(Get-LogLines) | Select-Object -Skip $logMark)) { $observed.Add("update.log after relaunch: $l") }
    $observed.Add("sq.version after relaunch: $nextVersion")
    if (-not $line) { throw "no 'up to date (0.0.2-beta.2)' within 120 s after the relaunch (sq.version $nextVersion)" }
    if ($nextVersion -ne '0.0.2-beta.2') { throw "sq.version after relaunch is '$nextVersion', expected 0.0.2-beta.2" }

    Mark 'settings'
    $cfg = Get-Content -LiteralPath (Join-Path $data 'config\user_config.json') -Raw | ConvertFrom-Json
    $values = [ordered]@{
        'general.autoStartWithGame' = $cfg.general.autoStartWithGame
        'telemetry.enabled'         = $cfg.telemetry.enabled
        'ads.enabled'               = $cfg.ads.enabled
    }
    $text = ($values.Keys | ForEach-Object { "$_=$($values[$_])" }) -join ', '
    $observed.Add("user_config.json at the end: $text")
    if ($values['general.autoStartWithGame'] -ne $true -or $values['telemetry.enabled'] -ne $false -or $values['ads.enabled'] -ne $false) {
        throw "seeded values changed: $text"
    }
}

New-Item -ItemType Directory -Force $Root | Out-Null
Mark 'guard'
$before = Get-ProtectedState
ConvertTo-StateJson $before | Set-Content -LiteralPath (Join-Path $Root 'protected_before.json') -Encoding utf8
Reset-OwnDir $data
Reset-OwnDir $releases
Reset-OwnDir $build
Reset-OwnDir $setupDir
if ($Beta) { Reset-OwnDir $betaSetupDir }

try {
    if ($Beta) {
        Invoke-BetaRun
    } else {
        Mark 'build'
        foreach ($v in '0.0.1', '0.0.2') {
            & (Join-Path $PSScriptRoot 'build-release.ps1') -Version $v -SkipTests -OutDir $build
            Set-Location $repo
            $stage = Join-Path $build "fova-$v"
            vpk pack -u FovaUpdateTest -v $v -p $stage -e fova.exe -r win-x64 `
                --packTitle "Fova Update Test" --packAuthors Fova `
                --framework net8.0-x64-desktop `
                -o $releases
            if ($LASTEXITCODE -ne 0) { throw "vpk pack $v failed (exit $LASTEXITCODE)" }
            # AI-NOTE: every pack overwrites releases\FovaUpdateTest-win-Setup.exe with the newest
            # version (the first run installed 0.0.2 from it), so the 0.0.1 Setup is kept aside here.
            if ($v -eq '0.0.1') {
                New-Item -ItemType Directory -Force $setupDir | Out-Null
                Copy-Item -LiteralPath (Join-Path $releases 'FovaUpdateTest-win-Setup.exe') -Destination $setupDir -Force
            }
        }
        $delta = Get-ChildItem -LiteralPath $releases -Filter '*-delta.nupkg' | Select-Object -First 1
        if (-not $delta) { throw "no *-delta.nupkg in $releases" }
        $observed.Add("delta package: $($delta.Name) ($([math]::Round($delta.Length / 1MB, 2)) MB)")

        Mark 'install'
        Write-SeedConfig
        $setup = Join-Path $setupDir 'FovaUpdateTest-win-Setup.exe'
        # AI-NOTE: not Start-Process -Wait — it waits for the whole process tree, and Setup starts the app.
        $sp = Start-Process -FilePath $setup -ArgumentList '--silent' -PassThru
        if (-not $sp.WaitForExit(300000)) { throw "Setup did not exit within 300 s" }
        $observed.Add("Setup exit code: $($sp.ExitCode)")
        $installed = Get-InstalledVersion
        $observed.Add("installed sq.version: $installed")
        if ($installed -ne '0.0.1') { throw "sq.version after install is '$installed', expected 0.0.1" }
        $app = Wait-AppProcess 30
        if ($app) { $observed.Add("Setup started the app (pid $($app.Id)); reused") }
        else {
            Start-Process -FilePath (Join-Path $InstallDir 'current\fova.exe') | Out-Null
            $app = Wait-AppProcess 30
            if (-not $app) { throw "the app did not start" }
            $observed.Add("app started by the script (pid $($app.Id))")
        }

        Mark 'tray'
        $line = Wait-LogLine 'downloaded 0.0.2 (current 0.0.1); applies on exit' 0 120
        if (-not $line) { throw "no 'downloaded 0.0.2 (current 0.0.1); applies on exit' within 120 s; update.log: $((Get-LogLines) -join ' | ')" }
        $observed.Add("update.log: $line")
        $app.Refresh()
        $observed.Add("main window: '$($app.MainWindowTitle)' (handle $($app.MainWindowHandle))")
        if (-not $app.CloseMainWindow()) { throw "CloseMainWindow found no main window" }
        Start-Sleep -Seconds 3
        $app.Refresh()
        if ($app.HasExited) { throw "the process exited after the window was closed (exit $($app.ExitCode)); no tray" }
        $trayVersion = Get-InstalledVersion
        $observed.Add("after close: pid $($app.Id) alive, main window handle $($app.MainWindowHandle), sq.version $trayVersion")
        if ($trayVersion -ne '0.0.1') { throw "sq.version in the tray is '$trayVersion', expected 0.0.1" }

        Mark 'killed'
        $logMark = @(Get-LogLines).Count
        Stop-TestProcess $app
        $observed.Add("killed pid $($app.Id) (Stop-Process -Force); sq.version $(Get-InstalledVersion)")
        Start-Process -FilePath (Join-Path $InstallDir 'current\fova.exe') | Out-Null
        $line = Wait-LogLine 'up to date (0.0.2)' $logMark 120
        $nextVersion = Get-InstalledVersion
        $newLines = @(@(Get-LogLines) | Select-Object -Skip $logMark)
        foreach ($l in $newLines) { $observed.Add("update.log after relaunch: $l") }
        $observed.Add("sq.version after relaunch: $nextVersion")
        if (-not $line) { throw "no 'up to date (0.0.2)' within 120 s after the relaunch (sq.version $nextVersion)" }
        if ($nextVersion -ne '0.0.2') { throw "sq.version after relaunch is '$nextVersion', expected 0.0.2" }

        Mark 'close'
        foreach ($p in Get-TestProcesses 'fova') {
            $p.CloseMainWindow() | Out-Null
            if (-not $p.WaitForExit(10000)) { Stop-TestProcess $p; $observed.Add("pid $($p.Id) still alive 10 s after close (tray); killed") }
        }
    }
}
catch {
    $failure = "${step}: $($_.Exception.Message)"
}
finally {
    $times['uninstall'] = (Get-Date).ToString('HH:mm:ss')
    foreach ($p in Get-TestProcesses) { Stop-TestProcess $p }
    $ukey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallKeyPath)
    $uninstallString = $null
    if ($ukey) { $uninstallString = $ukey.GetValue('UninstallString'); $ukey.Dispose() }
    if (-not $uninstallString -and (Test-Path -LiteralPath (Join-Path $InstallDir 'Update.exe'))) {
        $uninstallString = "`"$(Join-Path $InstallDir 'Update.exe')`" --uninstall"
    }
    if ($uninstallString -and $uninstallString -match '^"([^"]+)"\s*(.*)$') {
        $observed.Add("uninstall: $uninstallString --silent")
        # AI-NOTE: --silent so no completion dialog waits for a click; the env above is inherited.
        $up = Start-Process -FilePath $Matches[1] -ArgumentList "$($Matches[2]) --silent" -PassThru
        if (-not $up.WaitForExit(120000)) { $observed.Add('uninstaller still running after 120 s') }
        else { $observed.Add("uninstaller exit code: $($up.ExitCode)") }
    }
    elseif ($uninstallString) { $observed.Add("uninstall: cannot parse UninstallString '$uninstallString'") }
    # Update.exe cannot delete itself; the folder goes a moment after it exits.
    for ($i = 0; $i -lt 60 -and (Test-Path -LiteralPath $InstallDir); $i++) { Start-Sleep -Milliseconds 500 }

    $shortcutDirs = @([Environment]::GetFolderPath('DesktopDirectory'),
        (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'))
    $shortcuts = @($shortcutDirs | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object {
        Get-ChildItem -LiteralPath $_ -Recurse -Filter 'Fova Update Test*.lnk' -ErrorAction SilentlyContinue })
    $safety['install folder removed'] = if (Test-Path -LiteralPath $InstallDir) { 'FAIL' } else { 'ok' }
    $ukey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallKeyPath)
    $safety['uninstall key removed'] = if ($ukey) { $ukey.Dispose(); 'FAIL' } else { 'ok' }
    $safety['shortcuts removed'] = if ($shortcuts) { 'FAIL: ' + ($shortcuts.FullName -join ', ') } else { 'ok' }

    $after = Get-ProtectedState
    ConvertTo-StateJson $after | Set-Content -LiteralPath (Join-Path $Root 'protected_after.json') -Encoding utf8
    $cmp = Compare-ProtectedState $before $after
    foreach ($check in $cmp.Keys) { $safety[$check] = if ($cmp[$check] -eq 'same') { 'ok' } else { "FAIL: $($cmp[$check])" } }
}

# ── report ──────────────────────────────────────────────────────────────────────────────────────

$aborted = @($safety.Keys | Where-Object { $safety[$_] -ne 'ok' })
$commit = (git -C $repo rev-parse --short HEAD 2>$null)
$md = [Collections.Generic.List[string]]::new()
$md.Add($(if ($Beta) { "# Local beta update test (FovaUpdateTest)" } else { "# Local update test (FovaUpdateTest)" }))
$md.Add("")
$md.Add("Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm'), built from commit $commit.")
$md.Add("Data dir: $data; update source: $releases; install: $InstallDir.")
$md.Add("")
$md.Add("## Step times")
foreach ($k in $times.Keys) { $md.Add("- ${k}: $($times[$k])") }
$md.Add("")
$md.Add("## Observed")
foreach ($o in $observed) { $md.Add("- $o") }
$md.Add("")
$md.Add("## update.log (full)")
$md.Add('```')
foreach ($l in Get-LogLines) { $md.Add($l) }
$md.Add('```')
$md.Add("")
if ($Beta) {
    $md.Add("## app.log (full)")
    $md.Add('```')
    foreach ($l in Get-LogLines $appLog) { $md.Add($l) }
    $md.Add('```')
    $md.Add("")
}
$md.Add("## Safety checks")
foreach ($k in $safety.Keys) { $md.Add("- ${k}: $($safety[$k])") }
$md.Add("- kill rule: Stop-Process only for processes under $InstallDir (Stop-TestProcess)")
if ($InjectFailure) { $md.Add("- injected failure: $InjectFailure") }
$md.Add("")
if ($aborted) {
    $md.Add("RESULT: FAIL — safety: ABORTED — $($aborted -join ', ')")
} elseif ($failure) {
    $md.Add("RESULT: FAIL — $failure")
} elseif ($Beta) {
    $md.Add("RESULT: PASS — light 0.0.1 did not see the beta; the beta Setup switched the install to beta (0.0.2-beta.1, settings kept, one shortcut, held-back features started); beta update applied after a kill (0.0.2-beta.1 -> 0.0.2-beta.2)")
} else {
    $md.Add("RESULT: PASS — update applied on the next launch after a kill in the tray (0.0.1 -> 0.0.2)")
}
$md | Set-Content -LiteralPath (Join-Path $Root 'RESULT.md') -Encoding utf8
$md | ForEach-Object { Write-Output $_ }

if ($aborted) {
    foreach ($a in $aborted) { Write-Output "ABORTED — $a" }
    exit 1
}
if ($failure) { exit 1 }
exit 0
