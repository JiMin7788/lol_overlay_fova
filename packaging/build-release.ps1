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
    pwsh packaging/build-release.ps1 -Beta -Version 0.5.0-beta.1
    pwsh packaging/build-release.ps1 -SelfTest
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

    # Build the BETA client: the full build (capture included, implies -IncludeCapture) compiled
    # with -p:FovaBeta=true, staged as fova-<Version>-beta. The payload check then requires the
    # capture stack instead of forbidding it (lolF05).
    [switch] $Beta,

    # Skip the test run. Off by default: shipping an untested build is how a broken
    # package reaches someone else's machine.
    [switch] $SkipTests,

    # Check the payload checks on made-up stages under %TEMP%; nothing is built.
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

function Step([string] $msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

# The relative paths of the files embedded in a .NET single-file bundle; empty for a plain apphost.
# AI-NOTE: -SelfContained publishes with PublishSingleFile, which embeds every managed DLL - Overlay.Capture
# and the Vortice/SharpGen chain included - in fova.exe, so the stage has no loose copy of them. The payload
# checks read the bundle manifest too; a loose-file check alone always failed the beta (lolF05 QA S17) and
# passed the light one without looking. Layout (Microsoft.NET.HostModel): the host holds a 32-byte signature
# preceded by the int64 offset of the bundle header (0 when nothing is bundled); the header lists the files.
function Get-BundledFiles([string] $exePath) {
    $signature = [byte[]](0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
                          0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae)
    # Latin-1 maps each byte to one char, so an ordinal string search is a byte search.
    $latin1 = [Text.Encoding]::GetEncoding(28591)
    $needle = $latin1.GetString($signature)
    $fs = [IO.File]::OpenRead($exePath)
    try {
        $chunk = [byte[]]::new(4MB)
        $base = 0L
        $at = -1L
        while ($at -lt 0) {
            $fs.Position = $base
            $n = $fs.Read($chunk, 0, $chunk.Length)
            if ($n -lt $signature.Length) { break }
            $i = $latin1.GetString($chunk, 0, $n).IndexOf($needle, [StringComparison]::Ordinal)
            if ($i -ge 0) { $at = $base + $i } else { $base += $n - $signature.Length + 1 }
        }
        if ($at -lt 8) { return @() }
        $r = [IO.BinaryReader]::new($fs)
        $fs.Position = $at - 8
        $headerOffset = $r.ReadInt64()
        if ($headerOffset -eq 0) { return @() }
        $fs.Position = $headerOffset
        $major = $r.ReadUInt32()
        [void] $r.ReadUInt32()                 # minor version
        $count = $r.ReadInt32()
        [void] $r.ReadString()                 # bundle id
        if ($major -ge 2) { $fs.Position += 5 * 8 }   # deps.json and runtimeconfig.json offset+size, flags
        @(for ($k = 0; $k -lt $count; $k++) {
            $fs.Position += $(if ($major -ge 6) { 3 * 8 } else { 2 * 8 })   # offset, size[, compressed size]
            [void] $r.ReadByte()               # file type
            $r.ReadString()
        })
    } finally { $fs.Dispose() }
}

# Throws unless the capture stack is absent from a light stage and present in a beta stage, looking at the
# loose files and at what a single-file fova.exe bundles.
function Test-CapturePayload([string] $stage, [bool] $light, [bool] $beta) {
    $bundled = @(Get-BundledFiles (Join-Path $stage 'fova.exe'))
    # The whole point of the light build: prove the capture stack is absent rather than assume it.
    $capture = @(Get-ChildItem $stage -Recurse -File | ForEach-Object Name) + $bundled |
        Where-Object { $_ -match '(?i)vortice|sharpgen|Overlay\.Capture' }
    if ($light -and $capture) {
        throw ("light package still contains the capture stack: " + ($capture -join ', '))
    }
    if ($light) { Write-Host "capture stack absent (0 Vortice/SharpGen/Overlay.Capture files)" }

    # The beta exists to test the capture stack: prove it shipped rather than assume it.
    if ($beta) {
        if (-not (Test-Path (Join-Path $stage 'Overlay.Capture.dll')) -and $bundled -notcontains 'Overlay.Capture.dll') {
            throw "beta package has no capture stack (Overlay.Capture.dll missing)"
        }
        Write-Host "capture stack present (Overlay.Capture.dll)"
    }
}

if ($SelfTest) {
    # Writes a made-up fova.exe: filler, the host's offset+signature, filler, then a bundle header (major
    # $major) listing $names - the same shape the SDK appends to a single-file publish.
    function New-FakeExe([string] $path, [string[]] $names, [int] $major = 6, [switch] $Unbundled) {
        $ms = [IO.MemoryStream]::new()
        $w = [IO.BinaryWriter]::new($ms)
        $w.Write([byte[]]::new(1000))
        $offsetAt = $ms.Position
        $w.Write([long] 0)
        $w.Write([byte[]](0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
                          0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae))
        $w.Write([byte[]]::new(500))
        $header = $ms.Position
        $w.Write([uint32] $major); $w.Write([uint32] 0); $w.Write([int] $names.Count); $w.Write('bundle-id')
        if ($major -ge 2) { foreach ($x in 1..5) { $w.Write([long] $x) } }
        foreach ($n in $names) {
            $w.Write([long] 1); $w.Write([long] 2)
            if ($major -ge 6) { $w.Write([long] 3) }
            $w.Write([byte] 1); $w.Write($n)
        }
        if (-not $Unbundled) { $ms.Position = $offsetAt; $w.Write([long] $header) }
        [IO.File]::WriteAllBytes($path, $ms.ToArray())
    }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "fova-build-release-selftest-$PID"
    $sc = @('fova.dll', 'Overlay.Core.dll', 'Overlay.Capture.dll', 'Vortice.Direct3D11.dll', 'SharpGen.Runtime.dll')
    $cases = @(
        # The S17 stage: a self-contained beta is one fova.exe with the capture stack inside it.
        @{ name = 'beta single-file, capture bundled'; light = $false; beta = $true; exe = @{ names = $sc }; ok = $true
           text = 'capture stack present (Overlay.Capture.dll)' }
        @{ name = 'beta single-file, capture not bundled'; light = $false; beta = $true; exe = @{ names = @('fova.dll') }
           ok = $false; text = 'beta package has no capture stack (Overlay.Capture.dll missing)' }
        @{ name = 'beta framework-dependent, loose dll'; light = $false; beta = $true; exe = @{ names = @(); Unbundled = $true }
           loose = @('Overlay.Capture.dll'); ok = $true; text = 'capture stack present (Overlay.Capture.dll)' }
        @{ name = 'beta framework-dependent, no dll'; light = $false; beta = $true; exe = @{ names = @(); Unbundled = $true }
           ok = $false; text = 'beta package has no capture stack (Overlay.Capture.dll missing)' }
        @{ name = 'light single-file, capture bundled'; light = $true; beta = $false; exe = @{ names = $sc }; ok = $false
           text = 'light package still contains the capture stack: Overlay.Capture.dll, Vortice.Direct3D11.dll, SharpGen.Runtime.dll' }
        @{ name = 'light single-file, clean'; light = $true; beta = $false; exe = @{ names = @('fova.dll', 'Overlay.Core.dll') }
           ok = $true; text = 'capture stack absent' }
        @{ name = 'light framework-dependent, loose Vortice'; light = $true; beta = $false; exe = @{ names = @(); Unbundled = $true }
           loose = @('Vortice.DXGI.dll'); ok = $false; text = 'light package still contains the capture stack: Vortice.DXGI.dll' }
        @{ name = 'beta single-file, bundle header v1'; light = $false; beta = $true; exe = @{ names = $sc; major = 1 }; ok = $true
           text = 'capture stack present (Overlay.Capture.dll)' }
    )
    $failed = [Collections.Generic.List[string]]::new()
    try {
        foreach ($c in $cases) {
            $stage = Join-Path $tmp ($cases.IndexOf($c))
            New-Item -ItemType Directory -Force $stage | Out-Null
            $e = $c.exe
            New-FakeExe (Join-Path $stage 'fova.exe') @e
            foreach ($l in @($c.loose | Where-Object { $_ })) { Set-Content (Join-Path $stage $l) 'x' }
            try {
                $out = (Test-CapturePayload $stage $c.light $c.beta 6>&1 | Out-String).Trim()
                $threw = $false
            } catch { $out = $_.Exception.Message; $threw = $true }
            $pass = ($threw -ne $c.ok) -and $out.Contains($c.text)
            if (-not $pass) { $failed.Add($c.name) }
            Write-Output "$(if ($pass) { 'ok  ' } else { 'FAIL' }) $($c.name) -> $out"
        }
    } finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
    if ($failed.Count) { Write-Output "SELFTEST: FAIL — $($failed -join ', ')"; exit 1 }
    Write-Output "SELFTEST: PASS ($($cases.Count) cases)"
    exit 0
}

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
if ($Beta) {
    # The beta is always the full build; the csproj refuses FovaBeta together with LightMode.
    $light = $false
    $stage = Join-Path $OutDir ("fova-" + $Version + "-beta")
}

if ($Beta) {
    Step "Publishing (BETA - full build with capture, $(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }))"
} else {
    Step "Publishing ($(if ($light) { 'light' } else { 'FULL - capture included' }), $(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }))"
}
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
if ($Beta) { $args += '-p:FovaBeta=true' }   # defines BETA -> BuildFlavor.IsBeta
if ($SelfContained) { $args += '-p:PublishSingleFile=true' }
if ($Version -ne 'dev') { $args += "-p:FovaReleaseVersion=$Version" } # leads the build stamp (telemetry, bug reports)

dotnet @args
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# ── verify the package is what it claims to be ──────────────────────────────────────────────────
Step "Verifying the payload"
$exe = Join-Path $stage 'fova.exe'
if (-not (Test-Path $exe)) { throw "fova.exe missing from $stage" }

# Data is what the damage numbers are computed from; an empty Data folder builds and then lies.
$dataCount = @(Get-ChildItem (Join-Path $stage 'Data') -Recurse -File -ErrorAction SilentlyContinue).Count
if ($dataCount -lt 300) { throw "Data/ has only $dataCount files - expected 300+. Package is incomplete." }
Write-Host "Data/ has $dataCount files"

Test-CapturePayload $stage $light ([bool] $Beta)

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

Fova does not require administrator rights - it runs as a normal user process. README.md
documents the keyboard hook it installs for in-game hotkeys.
"@ | Set-Content (Join-Path $stage 'RUNTIME-REQUIREMENTS.txt') -Encoding UTF8
}

# ── zip ─────────────────────────────────────────────────────────────────────────────────────────
Step "Zipping"
$zip = "$stage.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$zipMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)

# A download users are told to elevate/trust with no integrity story is a supply-chain hole:
# publish the SHA-256 beside the zip so a substituted payload is at least detectable.
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash *$(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding ascii

Step "Done"
Write-Host "package : $stage"
Write-Host "zip     : $zip ($zipMb MB)"
Write-Host "sha256  : $hash (written to $zip.sha256)"
Write-Host ""
Write-Host "Installer (optional): packaging/fova.iss - needs Inno Setup 6." -ForegroundColor DarkGray
Write-Host "  iscc packaging/fova.iss /DSourceDir=`"$((Resolve-Path $stage).Path)`" /DAppVersion=$Version" -ForegroundColor DarkGray
