<#
.SYNOPSIS
    Rebuilds the `main` (distribution) branch from `master`, minus the process material.

.DESCRIPTION
    `main` and `master` have UNRELATED histories on purpose: main started as a README-only commit
    and stays a thin, buildable distribution branch, while master carries the full development
    history. That means they cannot be merged - main is re-stated from master's tree each time.

    This script does that reproducibly:
      1. checks out master's tree onto main
      2. removes everything listed in packaging/main-exclude.txt
      3. verifies the result still builds and still contains the data files
      4. commits (and pushes with -Push)

    It refuses to run on a dirty working tree, because it moves branches around.

.EXAMPLE
    pwsh packaging/sync-main.ps1 -DryRun
    pwsh packaging/sync-main.ps1 -Push -Message "Sync distribution branch"
#>
[CmdletBinding()]
param(
    [string] $From = "master",
    [string] $Branch = "main",
    # Which remote to publish to. GitHub has NO per-branch visibility - a repository is public or
    # private as a whole - so keeping development private while the distribution branch is public
    # means pushing that branch to a SECOND, public repository. -Remote is how you point at it.
    [string] $Remote = "origin",
    [string] $Message = "Sync distribution branch from master",
    # Show what would be removed, change nothing, and leave the branch alone.
    [switch] $DryRun,
    # Push to origin after committing. Off by default - publishing is an explicit act.
    [switch] $Push,
    [switch] $SkipBuild,
    # Package to publish alongside the source, copied to release/ on the branch so the README can
    # link straight at it. GitHub Releases would be the better home, but a committed file is
    # downloadable the moment it is pushed, with no extra step for the owner.
    [string] $ReleaseZip
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

function Step([string] $m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

if (git status --porcelain) {
    throw "working tree is dirty - commit or stash first (this script switches branches)"
}

# ── read the exclusion list ─────────────────────────────────────────────────────────────────────
$listPath = Join-Path $PSScriptRoot 'main-exclude.txt'
if (-not (Test-Path $listPath)) { throw "missing $listPath" }

$remove = @(); $keep = @(); $mainOnly = @()
foreach ($raw in Get-Content $listPath) {
    $line = $raw.Trim()
    if (-not $line -or $line.StartsWith('#')) { continue }
    if ($line.StartsWith('!')) { $keep += $line.Substring(1) }
    elseif ($line.StartsWith('+')) { $mainOnly += $line.Substring(1) }
    else { $remove += $line }
}
Write-Host "$($remove.Count) removal rule(s), $($keep.Count) exception(s), $($mainOnly.Count) main-only path(s)"

# Rule matchers: a trailing / is a directory prefix, anything else an exact path.
function Test-RuleMatch([string[]] $rules, [string] $path) {
    foreach ($rule in $rules) {
        if ($rule.EndsWith('/')) { if ($path.StartsWith($rule)) { return $true } }
        elseif ($path -eq $rule) { return $true }
    }
    return $false
}

$startBranch = (git rev-parse --abbrev-ref HEAD)

if ($DryRun) {
    Step "Dry run - what would be removed from $Branch"
    $tracked = git ls-tree -r --name-only $From
    $doomed = $tracked | Where-Object {
        (Test-RuleMatch $remove $_) -and -not (Test-RuleMatch $keep $_)
    }
    $bytes = 0
    foreach ($f in $doomed) { if (Test-Path $f) { $bytes += (Get-Item $f).Length } }
    $doomed | Group-Object { ($_ -split '/')[0] } |
        Sort-Object Count -Descending |
        ForEach-Object { "{0,6} files  {1}" -f $_.Count, $_.Name }
    Write-Host ("`n{0} files, {1:N2} MB would be removed" -f $doomed.Count, ($bytes / 1MB))
    return
}

# ── restate main from master ────────────────────────────────────────────────────────────────────
Step "Checking out $Branch"
git fetch $Remote $Branch --quiet 2>$null
# NOTE: `if (git show-ref --quiet ...)` would test the command OUTPUT, and --quiet prints none, so
# the branch below is chosen on the EXIT CODE. Getting this wrong once branched main off master
# instead of origin/main and produced an unpushable non-fast-forward.
git show-ref --verify --quiet "refs/remotes/$Remote/$Branch" 2>$null
$hasRemote = ($LASTEXITCODE -eq 0)
if ($hasRemote) {
    git checkout -q -B $Branch "$Remote/$Branch"
    Write-Host "based on $Remote/$Branch ($(git rev-parse --short "$Remote/$Branch"))"
} else {
    git checkout -q --orphan $Branch
    git rm -rq --cached . 2>$null
    Write-Host "$Remote/$Branch does not exist - starting a fresh orphan branch"
}

Step "Taking $From's tree"
git checkout $From -- .
if ($LASTEXITCODE -ne 0) { git checkout -q $startBranch; throw "failed to take $From's tree" }

# ── propagate deletions ─────────────────────────────────────────────────────────────────────────
# `git checkout $From -- .` only ADDS and OVERWRITES; it never deletes. Without this step a file
# deleted or renamed on master persisted on public main forever — compiled into any build made from
# the public tree (SDK-style **/*.cs globs) — and a sensitive file synced before its exclusion rule
# existed could never be removed by the sync at all. Anything main carries that master no longer
# tracks is deleted here, except the declared main-only paths (release/, riot.txt).
Step "Propagating deletions from $From"
$fromSet = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@(git -c core.quotepath=false ls-tree -r --name-only $From),
    [System.StringComparer]::Ordinal)
$stray = @(git -c core.quotepath=false ls-files --cached) | Where-Object {
    -not $fromSet.Contains($_) -and -not (Test-RuleMatch $mainOnly $_)
}
foreach ($f in $stray) {
    git rm -q --cached --ignore-unmatch -- $f | Out-Null
    if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force }
    Write-Host "  deleted $f (no longer on $From)"
}
if (-not $stray) { Write-Host "  nothing to delete" }

Step "Removing process material"
# Delete only what git TRACKS. The first version removed whole directories with
# `Remove-Item -Recurse`, which also deleted .agent-state/state.json - a gitignored local file that
# no checkout could restore, because git does not track it. Untracked and ignored files under a
# removed directory are the user's, not ours.
foreach ($path in $remove) {
    $spec = $path.TrimEnd('/')
    # core.quotepath=false: without it git returns non-ASCII paths WRAPPED IN QUOTES with octal
    # escapes (docs/split_audio holds Korean .mp3 names), Test-Path then misses every one of them,
    # and `git add -A` puts them straight back after the index removal. Cost one bad commit.
    $tracked = @(git -c core.quotepath=false ls-files --cached -- $spec)
    if (-not $tracked) { continue }
    git rm -r -q --cached --ignore-unmatch $spec | Out-Null
    foreach ($f in $tracked) { if (Test-Path $f) { Remove-Item $f -Force } }
    # Drop directories that the removals left empty, deepest first; anything still holding an
    # untracked file survives on purpose.
    if (Test-Path $spec -PathType Container) {
        Get-ChildItem $spec -Recurse -Directory |
            Sort-Object { $_.FullName.Length } -Descending |
            Where-Object { -not (Get-ChildItem $_.FullName -Force) } |
            Remove-Item -Force
        if (-not (Get-ChildItem $spec -Force)) { Remove-Item $spec -Force }
    }
    Write-Host "  removed $path ($($tracked.Count) tracked file(s))"
}
foreach ($path in $keep) {
    git checkout $From -- $path
    git add $path
    Write-Host "  kept    $path (exception)"
}

# ── swap in the distribution README ─────────────────────────────────────────────────────────────
# master's README.md is written for someone building the thing; main's audience is someone
# DOWNLOADING it. Without this the sync would silently overwrite the user-facing README with the
# developer one every time, which is exactly the kind of quiet regression a scripted sync invites.
if ($ReleaseZip) {
    if (-not (Test-Path $ReleaseZip)) { git checkout -q $startBranch; throw "no zip at $ReleaseZip" }

    # Existence used to be the ONLY check, so a -full debug package, a zip carrying a populated
    # user_config.json, or the ~197MB self-contained zip (which GitHub's 100MB push limit then
    # wedges the branch on) would all ship verbatim. Inspect before committing.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $za = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $ReleaseZip))
    try { $entries = @($za.Entries | ForEach-Object { $_.FullName }) } finally { $za.Dispose() }
    if ($entries -notcontains 'fova.exe') {
        git checkout -q $startBranch; throw "zip has no fova.exe at its root - not a release payload"
    }
    $bad = $entries | Where-Object { $_ -match '(?i)vortice|sharpgen|Overlay\.Capture|user_config\.json|riot\.key|\.secrets' }
    if ($bad) {
        git checkout -q $startBranch; throw "zip contains disallowed entries: $($bad -join ', ')"
    }
    $zipMb = (Get-Item $ReleaseZip).Length / 1MB
    if ($zipMb -gt 95) {
        git checkout -q $startBranch
        throw ("zip is {0:N0} MB - GitHub rejects pushes over 100 MB; publish it as a Release asset instead" -f $zipMb)
    }

    New-Item -ItemType Directory -Force -Path 'release' | Out-Null
    Copy-Item $ReleaseZip (Join-Path 'release' (Split-Path $ReleaseZip -Leaf)) -Force
    # build-release.ps1 writes <zip>.sha256 beside the zip; ship it so downloads are verifiable.
    if (Test-Path "$ReleaseZip.sha256") {
        Copy-Item "$ReleaseZip.sha256" (Join-Path 'release' (Split-Path "$ReleaseZip.sha256" -Leaf)) -Force
    }
    git add 'release' | Out-Null
    Write-Host ("  release {0} ({1:N1} MB, {2} entries verified)" -f (Split-Path $ReleaseZip -Leaf),
                $zipMb, $entries.Count)
}

$distReadme = 'packaging/README.dist.md'
if (Test-Path $distReadme) {
    Copy-Item $distReadme 'README.md' -Force
    # The source lives on master; shipping it alongside its own copy just confuses.
    git rm -q --cached $distReadme --ignore-unmatch | Out-Null
    Remove-Item $distReadme -Force
    git add 'README.md'
    Write-Host "  README  swapped for the distribution version"
} else {
    Write-Host "  README  no packaging/README.dist.md - keeping $From's developer README" -ForegroundColor Yellow
}

# ── verify before committing, not after ─────────────────────────────────────────────────────────
Step "Verifying"
$dataCount = @(Get-ChildItem 'src/Overlay.Core/Data' -Recurse -File -ErrorAction SilentlyContinue).Count
if ($dataCount -lt 300) {
    git checkout -q $startBranch
    throw "Data/ has only $dataCount files - the strip went too far, aborting before commit"
}
Write-Host "Data/ intact ($dataCount files)"

if ((Test-Path 'README.md') -and -not (Select-String -Path 'README.md' -Pattern '내려받기|다운로드' -Quiet)) {
    git checkout -q $startBranch
    throw "README.md on $Branch is not the distribution one - the swap did not happen"
}

# A download button pointing at a file that is not there is worse than no button: the reader
# concludes the project is broken before running anything.
foreach ($m in Select-String -Path 'README.md' -Pattern '\]\((release/[^)]+)\)' -AllMatches) {
    foreach ($g in $m.Matches) {
        $target = $g.Groups[1].Value
        if (-not (Test-Path $target)) {
            git checkout -q $startBranch
            throw "README links to $target but it is not in the tree - pass -ReleaseZip"
        }
        Write-Host "download link OK -> $target"
    }
}

if (-not $SkipBuild) {
    dotnet build LolOverlay.sln -c Release -p:LightMode=true --nologo 2>&1 | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) {
        git checkout -q $startBranch
        throw "the stripped tree does not build - aborting before commit"
    }
    Write-Host "light build OK"
}

# ── secret scan: the last line of defense before anything is committed publicly ────────────────
# The key and the Riot match data are kept off this branch by master's .gitignore (layer 1) and
# the exclusion list (layer 2). This abort is layer 3: even if both fail, nothing secret-like is
# ever COMMITTED to the public branch.
Step "Secret scan"
git add -A
$secretLike = @(git -c core.quotepath=false ls-files --cached) |
    Where-Object { $_ -match '(?i)^\.secrets/|riot\.key|\.sqlite' }
if ($secretLike) {
    git checkout -q $startBranch
    throw "SECRET-LIKE paths staged for the public branch, refusing to commit: $($secretLike -join ', ')"
}
Write-Host "no secret-like paths staged"

Step "Committing"
if (-not (git diff --cached --name-only)) {
    Write-Host "nothing changed - $Branch is already in sync"
} else {
    git commit -q -m $Message
    Write-Host (git log --oneline -1)
    if ($Push) {
        git push $Remote $Branch
    } else {
        Write-Host "not pushed (pass -Push)" -ForegroundColor Yellow
    }
}

git checkout -q $startBranch
Write-Host "`nback on $startBranch"
