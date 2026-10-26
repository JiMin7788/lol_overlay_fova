# Overlay.Client.UiTests — UI tests that drive a real Fova

An xUnit project that starts a real Fova, operates it with FlaUI (UI Automation, plus real mouse and
keyboard input), inspects the screen and writes a report. Every UI test is skipped unless
`FOVA_UI_TESTS=1`. The pixel-predicate and harness unit tests (`PixelPredicateTests`,
`HarnessTests`) are plain facts and run on every `dotnet test`.

## Run

```
dotnet test src/Overlay.Client.UiTests/Overlay.Client.UiTests.csproj -c Debug --nologo -e FOVA_UI_TESTS=1 [--filter ...]
```

e.g. `--filter "FullyQualifiedName~SmokeTests|FullyQualifiedName~PixelPredicateTests"`. Without the
variable the UI tests report as skipped with
`UI test: set FOVA_UI_TESTS=1 to run (uses the real mouse and keyboard)`.

The project references `Overlay.Core` only, so this command never builds into the Debug bundle
(the Client project's own output folder). It works while your own Fova runs from there.

## Preconditions

- **League of Legends must be closed.** A test fails at once with
  `Close League of Legends before running the UI tests` while a `LeagueClientUx` or
  `League of Legends` process runs.
- **An unlocked, interactive desktop.** A test fails with `The desktop is locked or not interactive`
  when `OpenInputDesktop` fails or there is no foreground window (locked screen, disconnected RDP).
- Windows OCR for Korean (`ko`) must be installed for the screen checks that read text (see OCR).

## What happens on screen

1. On the first UI test, the suite kills leftover Fova processes whose executable is under
   `artifacts/uitest/app` (no other process), then builds its own copy:
   `dotnet build src/Overlay.Client/Overlay.Client.csproj -c Debug -o artifacts/uitest/app --nologo`
   (10-minute timeout; a failed build fails every test with the output tail).
2. Each test starts that copy in test mode: `FOVA_USER_DATA_DIR` is set, in the child process only,
   to a fresh `artifacts/uitest/runs/<runId>/<test>-run<n>/data`. A seed `config/user_config.json`
   holds the schema defaults plus `ads.enabled=false`, `champSelect.enabled=false`,
   `general.language="ko"`, `general.firstRunGuideShown=true`, `telemetry.noticeShown=true` and
   `telemetry.endpoint="http://127.0.0.1:<port>"` (the session's loopback listener), then the
   test's own overrides. Keys outside `ConfigSchema` are refused, because the app would drop them.
3. A Fova HOME window opens (sometimes two, side by side with your own Fova, which is not
   touched). Tests that need it take the **mouse pointer for tens of seconds**: the pointer moves and
   clicks only inside the test Fova's visible windows (`SafeInput` refuses any other point with
   `Refused to move the mouse outside Fova's windows: (x,y)`), and keys are pressed only while a test
   Fova window is in the foreground. Your cursor position is restored at the end of each test.
4. The window closes (UIA WindowPattern.Close, killed after 10 s if it does not exit).

Test mode never writes your `%LocalAppData%\Fova`, the Debug bundle or the HKCU Run key
(`SmokeTests.HkcuRunValueIsUntouched` checks the Run value).

## Retry

A test body runs at most twice, each run with a fresh session and data dir. If you move the mouse
or switch windows during a run, that run fails and the test runs once more. The test passes when
either run passes; both outcomes go into the report.

## Report

`artifacts/uitest/runs/<runId>/REPORT.md` (`runId` = `yyyyMMdd-HHmmss`), rewritten after every test:
a table of test | checklist ids | run1 | run2 | screenshots | notes. Screenshots are PNGs next to it.
On a failure the run also keeps a capture of every test-Fova window, and each session folder
`<test>-run<n>/` keeps `app.log` (copied from `data/logs/app.log`) and the whole data dir.
`artifacts/` is git-ignored.

## Capture

`Screen.Capture` uses `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)` and falls back to a screen copy of
the window rectangle when that fails or returns a blank image. On HOME, **PrintWindow works**
(`SmokeTests.LaunchFindsHomeAndCloseExits` notes the method used; first run: PrintWindow, DPI scale 1).
The layered overlay window may need the screen-copy fallback; the overlay tests record which one they
got. DIP thresholds are multiplied by `Screen.Dpi(hwnd)` (`GetDpiForWindow / 96`, RECURRING_TRAPS 3.3).
The test process makes itself per-monitor DPI aware at load (`AssemblyInfo.cs`), so UIA rectangles, the
cursor and window rectangles are all physical pixels on any display scale. `M45EditorTests` maximizes HOME,
because at 200% the restored window is taller than a 4K screen. The fixture writes its steps with times to
`runs/<runId>/fixture.log`, and the M45 runner writes its steps to `runs/<runId>/m45.log` while it runs.

## OCR

`Screen.Ocr(bitmap, rect, "ko")` uses `Windows.Media.Ocr` on the crop upscaled ×3. Without the Korean
OCR language it fails with `Windows OCR language 'ko' is not installed`; add Korean under Settings →
Time & language → Language & region (with its optical character recognition feature). On the
development PC it reads the HOME button as `오버레이미리보기` (compared with spaces removed, since
Windows OCR may split Hangul into separate words).

## Network: what a session may contact (spec R18)

- **Asserted:** telemetry goes to the session's loopback listener. After launch, `FovaSession` reads
  the session config back and fails the test unless `telemetry.endpoint` is the listener URL,
  `ads.enabled=false` and `champSelect.enabled=false` (skipped only for a key the test overrides).
- **Off by code path:** with `ads.enabled=false` no ad manifest or creative is fetched; with
  `champSelect.enabled=false` no LCU connector is created, so the League client is never contacted.
- **Expected live requests, read-only:** Data Dragon / CommunityDragon downloads (DDragonUpdater,
  AssetPrefetcher, the champion-name summary; the app follows the live patch), the remote-flags GET
  (`RemoteFlags`, a fixed URL) and the Live Client API poll on `127.0.0.1:2999`, which finds no game
  because League is closed. The suite does not monitor these hosts.

## Policy P3/P4

Fova itself never sends or injects input: the app only listens to its own hotkeys and passes every
key on. Only this separate test process sends input, through SendInput (FlaUI), and only into Fova's
own windows (`SafeInput` checks every mouse point against the test Fova's visible top-level windows
and requires a test Fova window in the foreground before keys). It never runs while League runs, so
no key or click can reach the game.

## M45 editor cases

`M45/m45_cases.json` (copied to the output) drives `M45EditorTests.Item(hc: N)`: one theory row per
HUMAN_CHECKLIST.md item, one Fova session per row. Filter one item with
`--filter "FullyQualifiedName~M45EditorTests&DisplayName~hc: 104"`.

- A case: `hc` (the checklist line number), `line` (the start of that line's text, checked by
  `M45CasesTests`), `claims`.
- A claim (one champion / ability / statement of the line): `id`, `champion` (id, typed into
  `ChampionSearchBox`), `championKo` (the tile label clicked), `sequence` (palette slots dragged into the
  sequence, `AA` for the basic attack, at most 5), `states`, `checks`, `engineEvidence`, optional `note`.
- A state: `name`, `knobs` [{`node` (0-based entry), `kind`, `value`}], optional `sequence` that
  replaces the claim's. Knob kinds: `attackCount` (`N타`), `hitDuration` (`Ns`, seconds), `distance`
  (`↔`, percent), `stackCount`, `condition` (`☐`/`☑` badge, `true`), `fx` (the `＋fx` row label, e.g.
  `P · 온히트 · Magic`). Every state is built from an empty sequence ('순서 비우기').
- Checks: `badge` / `noBadge` (`node`, `kind`; `fx` means an enabled `＋fx`), `sliderMax` (`node`,
  `kind`, `max`: the picker Slider's RangeValue Maximum), `paletteHas` (`slot`), `compare` (`a` `op` `b`,
  `op` one of `> < = ratio`, `ratio` with `value`; `a`/`b` are sums of `state.min|max|resolved` terms and
  numbers, `resolved` being the single number or a range's floor; `=` and `ratio` allow 0.5% or 0.5 per
  displayed number), `oracle` (optional `states`, default all), `card` (`label`, `a` `op` `b`, below).
- `card` (lolF02 t6b; `label` is the schema's one extension): `a` and `b` are plain state names with
  sequences of different lengths, `op` is `>` or `<`. The runner saves state `b` on K1 and `a` on K2 (the
  editor's `HotkeyButton`), opens the overlay preview (`PreviewToggle`), presses K2 then K1, and on each card
  (told apart by its chip count) OCRs the text lines between the damage boxes and the chip row
  (`ComboCard.LineBand`). The value is the number before `HP` on the line holding `label`
  (`M45CardLine.Value`, e.g. `≤ 412 HP 확정킬 (멜 P(압도))`). A case with a card check starts its session
  with the OverlayPreviewTests seed (HUD scale 1.5, every preview element but the combo card off).
- `oracle`: each state is saved under the scratch name `m45 <id> <state>`, `combos.saved.<id>` is read
  from the session config (`EngineOracle.FromSavedCombo`), every shown number must be the oracle's
  `ComputePreview` within 0.5, and the scratch combo is deleted (✕ twice). Before the first case the run
  checks Annie [Q, W] against the oracle and fails every case with
  `oracle/editor mismatch on the reference combo` if they differ.
- `engineEvidence`: `null`, `M45 F=100 @40bbddd`, or `Class.Method` of a test under
  `src/Overlay.Core.Tests`, for the parts the editor cannot set (armor/MR, crit, a low-HP target, a mid
  level, AP, items). The oracle runs the app's own engine: it proves the editor's wiring and display,
  not the game numbers (RECURRING_TRAPS 2.11).

## Layout

- `UiFactAttribute.cs` — `UiFact` / `UiTheory` and the `FOVA_UI_TESTS` gate
- `UiTest.cs` — `UiTest.Run` (preconditions, cursor, two runs, report row) and `UiContext`
- `AppCopyFixture.cs` — the build copy and the run id (xUnit collection fixture `Fova UI`)
- `FovaSession.cs`, `ConfigSeed.cs`, `LoopbackListener.cs` — one test-mode Fova, its seed and listener
- `SafeInput.cs`, `NativeMethods.cs` — guarded input and the Win32 calls
- `Screen.cs`, `OverlayPalette.cs` — capture, pixel predicates, OCR, OverlayHost colours
- `EngineOracle.cs` — `ComboRunner.ComputePreview` on the app copy's own data, and the saved-combo reader
- `M45EditorTests.cs`, `M45/M45Cases.cs`, `M45/m45_cases.json` — the M45 editor case runner, its model and cases
- `M45CasesTests.cs` — the case file's coverage, shape, engine evidence and compare arithmetic (plain facts)
- `RunReport.cs` — `REPORT.md`
- `SmokeTests.cs`, `PixelPredicateTests.cs`, `HarnessTests.cs` — the tests
