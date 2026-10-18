<#
.SYNOPSIS
    Builds a Fova release as Velopack packages (full + delta) and, with -Publish, uploads it to
    the public repo's GitHub Releases, where installed apps pick it up automatically.

.DESCRIPTION
    1. build-release.ps1 -Version: test suite, light publish, payload checks (dist/fova-<ver>/)
    2. -Publish: vpk download github - fetch the previous release's full package, which is what
       vpk needs to compute the delta. Without it the release is full-only (still updates, just
       a bigger download).
    3. vpk pack: FovaApp-<ver>-full.nupkg, FovaApp-<ver>-delta.nupkg, FovaApp-win-Setup.exe,
       FovaApp-win-Portable.zip, releases.win.json - in dist/velopack/
    4. -Publish: vpk upload github - creates release v<ver> and attaches those files.

    The package id is FovaApp, so the install lives in %LocalAppData%\FovaApp. It must NOT be
    "Fova": Velopack deletes its whole install root on uninstall, and %LocalAppData%\Fova holds
    user settings (AppPaths) and the ad/flag caches.

    Requires: vpk (dotnet tool install -g vpk --version 1.2.158, same as the Velopack package in
    Overlay.Client.csproj). -Publish also requires a token with contents:write on
    JiMin7788/lol_overlay_fova: $env:GITHUB_TOKEN, else the one git stores for github.com.

.EXAMPLE
    pwsh packaging/release.ps1 -Version 0.3.4                 # local packages only
    pwsh packaging/release.ps1 -Version 0.3.4 -Publish        # build, delta, upload
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [switch] $Publish,
    # Where the Velopack packages go. Reused across releases on purpose: a previous full package
    # left here is also used for the delta.
    [string] $ReleaseDir = "dist/velopack"
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$repoUrl = "https://github.com/JiMin7788/lol_overlay_fova"

function Step([string] $m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "vpk not found - dotnet tool install -g vpk --version 1.2.158"
}
if ($Publish -and -not $env:GITHUB_TOKEN) {
    # Fall back to the token git already uses for github.com.
    $cred = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
    $env:GITHUB_TOKEN = ($cred | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', ''
}
if ($Publish -and -not $env:GITHUB_TOKEN) { throw "-Publish needs `$env:GITHUB_TOKEN" }

& (Join-Path $PSScriptRoot 'build-release.ps1') -Version $Version
$stage = "dist/fova-$Version"
New-Item -ItemType Directory -Force $ReleaseDir | Out-Null

if ($Publish) {
    Step "Fetching the previous release (delta base)"
    vpk download github --repoUrl $repoUrl --token $env:GITHUB_TOKEN -o $ReleaseDir
    if ($LASTEXITCODE -ne 0) { throw "vpk download failed" }
}

Step "Packing $Version"
vpk pack -u FovaApp -v $Version -p $stage -e fova.exe -r win-x64 `
    --packTitle Fova --packAuthors Fova `
    -i src/Overlay.Client/fova.ico `
    --framework net8.0-x64-desktop `
    -o $ReleaseDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }
Get-ChildItem $ReleaseDir -Filter "*$Version*" | ForEach-Object { "{0,-40} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }

if ($Publish) {
    Step "Uploading to GitHub Releases"
    vpk upload github --repoUrl $repoUrl --token $env:GITHUB_TOKEN -o $ReleaseDir `
        --publish --releaseName "Fova $Version" --tag "v$Version"
    if ($LASTEXITCODE -ne 0) { throw "vpk upload failed" }
    Write-Host "published: $repoUrl/releases/tag/v$Version"
}
