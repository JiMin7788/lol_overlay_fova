<#
.SYNOPSIS
    Builds a Fova BETA release as Velopack packages on channel "beta" and, with -Publish, uploads
    it to the public repo's GitHub Releases as a prerelease, where installed beta apps pick it up.

.DESCRIPTION
    1. build-release.ps1 -Version -Beta: test suite, full publish with BETA defined, payload checks
       (dist/fova-<ver>-beta/)
    2. -Publish: vpk download github --pre --channel beta - fetch the previous beta's full package
       as delta base. If there is none (the first beta) the release is full-only.
    3. vpk pack --channel beta: FovaApp-<ver>-*.nupkg, FovaApp-beta-Setup.exe, releases.beta.json
       ... - in dist/velopack-beta/
    4. -Publish: vpk upload github --pre --channel beta - creates prerelease v<ver>.

    The pack identity (packId FovaApp, title, authors, icon) is the same as release.ps1 on
    purpose: installing the beta Setup over a light install switches it, settings kept. Only the
    channel differs, so light installs keep following releases.win.json.

    Without -Publish nothing touches the network. Requires: vpk 1.2.158 (see release.ps1). -Publish
    also requires a token with contents:write on JiMin7788/lol_overlay_fova: $env:GITHUB_TOKEN,
    else the one git stores for github.com.

.EXAMPLE
    pwsh packaging/release-beta.ps1 -Version 0.5.0-beta.1             # local packages only
    pwsh packaging/release-beta.ps1 -Version 0.5.0-beta.1 -Publish    # build, delta, upload prerelease
    pwsh packaging/release-beta.ps1 -SelfTest                         # version check only; nothing built
#>
[CmdletBinding(DefaultParameterSetName = 'Release')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Release')] [string] $Version,
    [Parameter(ParameterSetName = 'Release')] [switch] $Publish,
    # Where the beta Velopack packages go. Kept apart from dist/velopack: a beta full package
    # there would become the light release's delta base and its feed.
    [Parameter(ParameterSetName = 'Release')] [string] $ReleaseDir = "dist/velopack-beta",
    # Check the -Version refusal, in-process and through a child run of this script; nothing is built.
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')] [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$repoUrl = "https://github.com/JiMin7788/lol_overlay_fova"

function Step([string] $m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

# AI-NOTE: \z, not $ - in .NET regex $ also matches before a final newline, so '0.5.0-beta.1`n' passed
# and the build ran into MSB3191 on 'dist\fova-0.5.0-beta.1`n-beta' (lolF05 QA S21). [0-9], not \d,
# which takes any Unicode digit.
function Assert-BetaVersion([string] $v) {
    if ($v -notmatch '^[0-9]+\.[0-9]+\.[0-9]+-beta\.[0-9]+\z') {
        throw "-Version must look like 0.5.0-beta.1 (got '$v')"
    }
}

if ($SelfTest) {
    $failed = [Collections.Generic.List[string]]::new()
    $cases = 0
    function Check([string] $name, [bool] $pass, [string] $out) {
        $script:cases++
        if (-not $pass) { $failed.Add($name) }
        Write-Output "$(if ($pass) { 'ok  ' } else { 'FAIL' }) $name -> $($out -replace '\r?\n', '\n')"
    }
    foreach ($v in '0.5.0-beta.1', '10.20.30-beta.12') {
        try { Assert-BetaVersion $v; Check "accepts '$v'" $true 'accepted' }
        catch { Check "accepts '$v'" $false $_.Exception.Message }
    }
    $bad = @('0.5.0', '0.5.0-beta', '0.5.0-rc.1', '0.5.0-beta.1; calc', "0.5.0-beta.1`n", "0.5.0-beta.1`r`n",
             ' 0.5.0-beta.1', "0.5.0-beta.$([char]0x0661)", '')
    foreach ($v in $bad) {
        $name = "refuses '$($v -replace '\r', '\r' -replace '\n', '\n')'"
        try { Assert-BetaVersion $v; Check $name $false 'accepted' }
        catch { Check $name ($_.Exception.Message -eq "-Version must look like 0.5.0-beta.1 (got '$v')") $_.Exception.Message }
    }

    # The S21 command lines, through -File as QA ran them. AI-NOTE: -ReleaseDir dist/velopack makes the next
    # check refuse too, so a version that slips through still builds nothing - it shows up as that message.
    $snap = { if (Test-Path dist) { (Get-ChildItem dist -Recurse -Force | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }) -join "`n" } else { '(none)' } }
    $before = & $snap
    foreach ($v in '0.5.0', '0.5.0-beta', '0.5.0-rc.1', '0.5.0-beta.1; calc', "0.5.0-beta.1`n", '') {
        $out = (& pwsh -NoProfile -File $PSCommandPath -Version $v -ReleaseDir dist/velopack 2>&1 | Out-String) -replace '\x1b\[[0-9;]*m', ''
        $code = $LASTEXITCODE
        # '' never reaches the script: Mandatory refuses it at binding, in the console's language.
        $refused = if ($v) { $out.Contains("-Version must look like 0.5.0-beta.1 (got '") }
                   else { $out.Contains("'Version'") -and -not $out.Contains('-Version must look like') }
        $name = "child -Version '$($v -replace '\n', '\n')'"
        $pass = $code -ne 0 -and $refused -and -not $out.Contains('-ReleaseDir must not be')
        Check $name $pass "exit $code$(if (-not $pass) { ' ' + $out.Trim() })"
    }
    Check 'dist unchanged' ((& $snap) -eq $before) ''

    if ($failed.Count) { Write-Output "SELFTEST: FAIL — $($failed -join ', ')"; exit 1 }
    Write-Output "SELFTEST: PASS ($cases cases)"
    exit 0
}

Assert-BetaVersion $Version
# AI-NOTE: GetFullPath with an explicit base - the one-argument form resolves against the process
# directory, which Set-Location does not change.
$full = { param($p) [IO.Path]::GetFullPath($p, $repo).TrimEnd('\', '/') }
if ((& $full $ReleaseDir) -eq (& $full 'dist/velopack')) {
    throw "-ReleaseDir must not be the light release folder dist/velopack"
}
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "vpk not found - dotnet tool install -g vpk --version 1.2.158"
}
if ($Publish -and -not $env:GITHUB_TOKEN) {
    # Fall back to the token git already uses for github.com.
    $cred = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
    $env:GITHUB_TOKEN = ($cred | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', ''
}
if ($Publish -and -not $env:GITHUB_TOKEN) { throw "-Publish needs `$env:GITHUB_TOKEN" }

& (Join-Path $PSScriptRoot 'build-release.ps1') -Version $Version -Beta
$stage = "dist/fova-$Version-beta"
New-Item -ItemType Directory -Force $ReleaseDir | Out-Null

if ($Publish) {
    Step "Fetching the previous beta (delta base)"
    vpk download github --repoUrl $repoUrl --token $env:GITHUB_TOKEN --pre --channel beta -o $ReleaseDir
    # AI-NOTE: a warning, not a throw - the first beta has no previous prerelease to download.
    if ($LASTEXITCODE -ne 0) { Write-Warning "no previous beta release downloaded (exit $LASTEXITCODE) - packing without a delta" }
}

Step "Packing $Version (channel beta)"
vpk pack -u FovaApp -v $Version -p $stage -e fova.exe -r win-x64 --channel beta `
    --packTitle Fova --packAuthors Fova `
    -i src/Overlay.Client/fova.ico `
    --framework net8.0-x64-desktop `
    -o $ReleaseDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }
foreach ($name in 'releases.beta.json', 'FovaApp-beta-Setup.exe') {
    if (-not (Test-Path (Join-Path $ReleaseDir $name))) { throw "vpk produced no $name in $ReleaseDir" }
}
Get-ChildItem $ReleaseDir -Filter "*$Version*" | ForEach-Object { "{0,-40} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
$setup = Get-Item (Join-Path $ReleaseDir 'FovaApp-beta-Setup.exe')
Write-Host ("Setup  : {0} ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB))

if ($Publish) {
    Step "Uploading to GitHub Releases (prerelease)"
    vpk upload github --repoUrl $repoUrl --token $env:GITHUB_TOKEN -o $ReleaseDir --channel beta --pre `
        --publish --releaseName "Fova $Version" --tag "v$Version"
    if ($LASTEXITCODE -ne 0) { throw "vpk upload failed" }
    Write-Host "published (prerelease): $repoUrl/releases/tag/v$Version"
} else {
    Write-Host "not published: run again with -Publish to upload v$Version as a GitHub prerelease"
}
