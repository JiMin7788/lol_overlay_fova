<#
.SYNOPSIS
    Builds the Fova distribution package (light build) and zips it.

.DESCRIPTION
    Publishes the LightMode build - the minimap screen-capture stack is compiled out, so the
    package contains no Windows.Graphics.Capture, no DXGI Desktop Duplication and none of the
    Vortice/SharpGen dependency chain. See README.md for what that does and does not remove.

    Two payload shapes:

      framework-dependent (default)  ~17 MB   needs .NET 8 Desktop Runtime on the target machine
      self-contained (-SelfContained) ~197 MB  runs on a clean Windows, no runtime install

    The framework-dependent build is the default because it is an order of magnitude smaller and
    the runtime is a one-time install most machines already have.

.EXAMPLE
    pwsh packaging/build-release.ps1
    pwsh packaging/build-release.ps1 -SelfContained -Version 1.0.0
#>
[CmdletBinding()]
param(
    # Output directory for the staged package and the .zip.
    [string] $OutDir = "dist",

    # Bundle the .NET runtime. Larger, but runs without installing anything.
    [switch] $SelfContained,

    # Version stamped into the zip name. Any string; not validated against the assembly.
    [string] $Version = "dev",

    # Build the FULL client (screen capture included) instead of the light one.
    # Present so the script can produce a debug/comparison package - not for distribution.
    [switch] $IncludeCapture,

    # Skip the test run. Off by default: shipping an untested build is how a broken
    # package reaches someone else's machine.
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

function Step([string] $msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

# ── sanity ──────────────────────────────────────────────────────────────────────────────────────
Step "Checking the toolchain"
$sdk = (dotnet --version 2>$null)
if (-not $sdk) { throw "dotnet SDK not found on PATH. Install the .NET 8 SDK." }
Write-Host "dotnet SDK $sdk"

if (-not $SkipTests) {
    Step "Running tests"
    # A package is a thing other people run. Build it from a green tree or not at all.
    dotnet test src/Overlay.Core.Tests/Overlay.Core.Tests.csproj -c Debug --nologo
    if ($LASTEXITCODE -ne 0) { throw "tests failed - refusing to package" }
}

# ── publish ─────────────────────────────────────────────────────────────────────────────────────
$light = -not $IncludeCapture
$stage = Join-Path $OutDir ("fova-" + $Version + $(if ($light) { "" } else { "-full" }))

Step "Publishing ($(if ($light) { 'light' } else { 'FULL - capture included' }), $(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }))"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

$args = @(
    'publish', 'src/Overlay.Client/Overlay.Client.csproj',
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', $(if ($SelfContained) { 'true' } else { 'false' }),
    '-p:DebugType=none',          # no .pdb in a shipped package
    '-p:SatelliteResourceLanguages=en',
    '-o', $stage,
    '--nologo'
)
if ($light) { $args += '-p:LightMode=true' }
if ($SelfContained) { $args += '-p:PublishSingleFile=true' }

dotnet @args
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# ── verify the package is what it claims to be ──────────────────────────────────────────────────
Step "Verifying the payload"
$exe = Join-Path $stage 'fova.exe'
if (-not (Test-Path $exe)) { throw "fova.exe missing from $stage" }

# The whole point of the light build: prove the capture stack is absent rather than assume it.
$capture = Get-ChildItem $stage -Recurse -File |
    Where-Object { $_.Name -match '(?i)vortice|sharpgen|Overlay\.Capture' }
if ($light -and $capture) {
    throw ("light package still contains the capture stack: " + ($capture.Name -join ', '))
}
if ($light) { Write-Host "capture stack absent (0 Vortice/SharpGen/Overlay.Capture files)" }

# Data is what the damage numbers are computed from; an empty Data folder builds and then lies.
$dataCount = @(Get-ChildItem (Join-Path $stage 'Data') -Recurse -File -ErrorAction SilentlyContinue).Count
if ($dataCount -lt 300) { throw "Data/ has only $dataCount files - expected 300+. Package is incomplete." }
Write-Host "Data/ has $dataCount files"

# -Include only filters when the PATH ends in a wildcard; without the \* it matches nothing
# and this "cleanup" would be a silent no-op that reads like a safeguard.
$leftovers = @(Get-ChildItem (Join-Path $stage '*') -Recurse -File -Include *.pdb, *.xml.orig, *.user)
if ($leftovers) { $leftovers | Remove-Item -Force; Write-Host "removed $($leftovers.Count) build leftover(s)" }

$size = [math]::Round((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
$files = @(Get-ChildItem $stage -Recurse -File).Count
Write-Host "payload: $files files, $size MB"

# ── runtime note for the framework-dependent package ────────────────────────────────────────────
if (-not $SelfContained) {
    @"
Fova needs the .NET 8 Desktop Runtime (x64).

If the app does not start, install it from:
  https://dotnet.microsoft.com/download/dotnet/8.0/runtime
Pick "Desktop Runtime" - the plain "Runtime" is not enough for a WPF app.

Fova asks for administrator rights on launch. That is required so hotkeys work while the
(elevated) League client has focus - see README.md, which also documents the keyboard hook.
"@ | Set-Content (Join-Path $stage 'RUNTIME-REQUIREMENTS.txt') -Encoding UTF8
}

# ── zip ─────────────────────────────────────────────────────────────────────────────────────────
Step "Zipping"
$zip = "$stage.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$zipMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)

Step "Done"
Write-Host "package : $stage"
Write-Host "zip     : $zip ($zipMb MB)"
Write-Host ""
Write-Host "Installer (optional): packaging/fova.iss - needs Inno Setup 6." -ForegroundColor DarkGray
Write-Host "  iscc packaging/fova.iss /DSourceDir=`"$((Resolve-Path $stage).Path)`" /DAppVersion=$Version" -ForegroundColor DarkGray
