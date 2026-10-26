<#
.SYNOPSIS
    Scratch-worktree test of sync-main.ps1 (lolT05). Never pushes, never moves `main`, never switches
    branches in this checkout.

.DESCRIPTION
    Setup: a git worktree at -Root on a scratch start branch (= -From plus THIS checkout's
    sync-main.ps1), a scratch branch that does not build (= -From plus a .cs that does not compile),
    and a scratch remote-tracking ref copied from -BaseRef so sync-main takes the same "based on
    <remote>/<branch>" path the real run takes. sync-main runs with -Remote/-Branch set to those
    scratch names, so the git fetch fails harmlessly and every commit lands on a scratch branch.
    1. dry run: prints exactly one 'would drop from LolOverlay.sln:' line, for MinimapReplay.Tests.
    2. strip + build (no -Push): succeeds, drops exactly that entry, the light Release build reports
       '0 Error(s)', and the committed stripped .sln lists only projects present in the stripped tree.
    3. build failure (-From the broken branch): aborts at the build and leaves the worktree on the
       start branch with a clean tree.
    Also checks that refs/heads/main and -From's LolOverlay.sln are unchanged.
    finally: dotnet build-server shutdown, remove the worktree, the scratch branches and the ref.

.EXAMPLE
    pwsh -NoProfile -File packaging/test-sync-main.ps1
#>
[CmdletBinding()]
param(
    [string] $From = 'master',
    [string] $Root = 'C:/dev/_wt/sync-main-test',
    [string] $BaseRef = 'refs/remotes/fova/main'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$start = 'sync-main-test-start'; $broken = 'sync-main-test-broken'; $out = 'sync-main-test-main'
$remote = 'sync-main-test-remote'
$remoteRef = "refs/remotes/$remote/$out"
$expected = 'tools\MinimapReplay.Tests\MinimapReplay.Tests.csproj'
# AI-NOTE: the '0 Error(s)' check reads MSBuild's summary, which is localized on a Korean Windows.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$script:fails = 0
function Check([bool] $ok, [string] $what) {
    if ($ok) { Write-Host "PASS $what" -ForegroundColor Green }
    else { Write-Host "FAIL $what" -ForegroundColor Red; $script:fails++ }
}
function Wt { git -C $Root @args; if ($LASTEXITCODE -ne 0) { throw "git $args failed in $Root" } }
function Get-SlnProjects([string[]] $sln) {
    foreach ($l in $sln) { if ($l -match '^Project\(.*\) = ".*", "(.*\.csproj)"') { $Matches[1] } }
}
function Invoke-Sync([string[]] $a) {
    $ErrorActionPreference = 'Continue'   # sync-main's own errors arrive on stderr; judge by exit code
    Write-Host "`n>>> sync-main.ps1 $($a -join ' ')"
    $o = @(pwsh -NoProfile -File (Join-Path $Root 'packaging/sync-main.ps1') @a 2>&1 | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
    $o | ForEach-Object { Write-Host "    | $_" }
    [pscustomobject]@{ Out = $o; Code = $code }
}
function Test-OnCleanStart([string] $what) {
    Check ((git -C $Root rev-parse --abbrev-ref HEAD) -eq $start) "$what - back on $start"
    $dirty = @(git -C $Root status --porcelain)
    Check ($dirty.Count -eq 0) "$what - clean tree ($($dirty.Count) change(s))"
}

if (Test-Path $Root) { throw "$Root exists - remove it first (a previous run did not clean up)" }
foreach ($b in $start, $broken, $out) {
    git -C $repo show-ref --verify --quiet "refs/heads/$b"
    if ($LASTEXITCODE -eq 0) { throw "branch $b already exists - delete it first" }
}
git -C $repo show-ref --verify --quiet $BaseRef
if ($LASTEXITCODE -ne 0) { throw "$BaseRef not found - fetch it or pass -BaseRef" }

$mainBefore = git -C $repo rev-parse --verify -q refs/heads/main
$slnBefore = git -C $repo rev-parse "${From}:LolOverlay.sln"

try {
    # ── setup ──────────────────────────────────────────────────────────────────────────────────
    git -C $repo worktree add -q -b $start $Root $From
    if ($LASTEXITCODE -ne 0) { throw "git worktree add failed" }
    Copy-Item (Join-Path $PSScriptRoot 'sync-main.ps1') (Join-Path $Root 'packaging/sync-main.ps1') -Force
    Wt add packaging/sync-main.ps1
    Wt commit -q --allow-empty -m "test: sync-main.ps1 under test"

    Wt checkout -q -b $broken $From
    Set-Content (Join-Path $Root 'src/Overlay.Core/SyncMainTestBroken.cs') 'this does not compile'
    Wt add src/Overlay.Core/SyncMainTestBroken.cs
    Wt commit -q -m "test: a tree that does not build"
    Wt checkout -q $start

    Wt update-ref $remoteRef $BaseRef

    # ── 1. dry run ─────────────────────────────────────────────────────────────────────────────
    $r = Invoke-Sync @('-DryRun', '-From', $From)
    Check ($r.Code -eq 0) "dry run exits 0"
    $would = @($r.Out | Where-Object { $_ -like 'would drop from LolOverlay.sln: *' })
    Check ($would.Count -eq 1 -and $would[0] -eq "would drop from LolOverlay.sln: $expected") `
        "dry run lists exactly: would drop from LolOverlay.sln: $expected"

    # ── 2. strip + build ───────────────────────────────────────────────────────────────────────
    $r = Invoke-Sync @('-From', $From, '-Remote', $remote, '-Branch', $out, '-Message', 'test: stripped tree')
    Check ($r.Code -eq 0) "sync-main succeeds on the stripped tree of $From"
    $dropped = @($r.Out | ForEach-Object { if ($_ -match '^\s*dropped (.+) from LolOverlay\.sln') { $Matches[1] } })
    Check ($dropped.Count -eq 1 -and $dropped[0] -eq $expected) "dropped exactly $expected"
    Check (@($r.Out | Where-Object { $_ -match '^\s*0 Error\(s\)' }).Count -eq 1) "light Release build reports 0 Error(s)"
    Check ($r.Out -contains 'light build OK') "light build OK"
    Test-OnCleanStart "after success"

    $stripped = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]@(git -C $repo ls-tree -r --name-only $out), [System.StringComparer]::Ordinal)
    $outProjects = @(Get-SlnProjects @(git -C $repo show "${out}:LolOverlay.sln"))
    $missing = @($outProjects | Where-Object { -not $stripped.Contains($_.Replace('\', '/')) })
    Check ($outProjects.Count -gt 0 -and $missing.Count -eq 0) "stripped .sln lists only projects in the stripped tree ($($outProjects.Count))"
    $gone = @(Get-SlnProjects @(git -C $repo show "${From}:LolOverlay.sln") | Where-Object { $outProjects -notcontains $_ })
    Check ($gone.Count -eq 1 -and $gone[0] -eq $expected) "stripped .sln lost exactly $expected"

    # ── 3. a failure after the branch switch ───────────────────────────────────────────────────
    $r = Invoke-Sync @('-From', $broken, '-Remote', $remote, '-Branch', $out)
    Check ($r.Code -ne 0 -and ($r.Out -match 'the stripped tree does not build')) "build failure aborts the run"
    Test-OnCleanStart "after the build failure"

    # ── untouched ──────────────────────────────────────────────────────────────────────────────
    Check ((git -C $repo rev-parse --verify -q refs/heads/main) -eq $mainBefore) "refs/heads/main unchanged"
    Check ((git -C $repo rev-parse "${From}:LolOverlay.sln") -eq $slnBefore) "$From's LolOverlay.sln unchanged"
}
finally {
    Set-Location $repo
    $ErrorActionPreference = 'Continue'
    dotnet build-server shutdown | Out-Null
    if (Test-Path $Root) { git -C $repo worktree remove --force $Root }
    foreach ($b in $start, $broken, $out) { git -C $repo branch -q -D $b 2>$null }
    git -C $repo update-ref -d $remoteRef 2>$null
    Check (-not (Test-Path $Root)) "scratch worktree removed"
}

if ($script:fails) { Write-Host "`nRESULT: FAIL ($script:fails)" -ForegroundColor Red; exit 1 }
Write-Host "`nRESULT: PASS" -ForegroundColor Green
