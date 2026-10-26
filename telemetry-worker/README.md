# fova telemetry worker (M39)

Self-hosted endpoint for the app's anonymous usage pings + crash reports (Cloudflare Workers +
D1, free tier is far beyond what this needs). The client half is
`src/Overlay.Core/Telemetry/TelemetryService.cs`; the module doc is `docs/modules/M39_TELEMETRY.md`.

**Collected fields, total:** anonymous install GUID, app version, OS version, and — for crashes —
the crash source + scrubbed exception text. No IP is stored, no account/game data exists in the
payloads at all.

## Deploy (one-time, ~10 minutes, human)

Requires a free Cloudflare account and Node.js.

```powershell
cd telemetry-worker
npx wrangler login                       # opens the browser once
npx wrangler d1 create fova-telemetry    # prints a database_id
# → paste that id into wrangler.toml (database_id = "...")
npx wrangler d1 execute fova-telemetry --remote --file=schema.sql
npx wrangler deploy                      # prints the URL, e.g. https://fova-telemetry.<acct>.workers.dev
```

Then wire the URL into the app:

1. `src/Overlay.Client/AppComposition.cs` → set `DefaultTelemetryEndpoint` to the printed URL
   (no trailing slash). This is what makes shipped builds send anything at all.
2. Rebuild/redeploy. Local test without a rebuild: set `"telemetry": { "endpoint": "https://..." }`
   in `config/user_config.json`.

## Reading the numbers

`pwsh telemetry-worker/stats.ps1 [-Days 30]` prints installs, daily users, new installs, versions
and crashes, with release installs and dev builds in separate columns. It reads the token from the
Windows user environment variable `FOVA_CF_TOKEN` (custom API token, Account - D1 - Read) and never
prints it. Release builds stamp their version first (`build-release.ps1` passes it); releases up to
0.4.0 carry only the git hash and count as dev.

With wrangler instead:

```powershell
# DAU, last 14 days
npx wrangler d1 execute fova-telemetry --remote --command "SELECT day, COUNT(*) AS dau FROM pings GROUP BY day ORDER BY day DESC LIMIT 14"

# Total distinct installs
npx wrangler d1 execute fova-telemetry --remote --command "SELECT COUNT(DISTINCT install_id) FROM pings"

# Version mix, last 7 days
npx wrangler d1 execute fova-telemetry --remote --command "SELECT version, COUNT(DISTINCT install_id) AS installs FROM pings WHERE day >= date('now', '-7 day') GROUP BY version ORDER BY installs DESC"

# Recent crashes (grouped by first line of the exception)
npx wrangler d1 execute fova-telemetry --remote --command "SELECT COUNT(*) AS n, version, substr(details, 1, 120) AS head FROM crashes WHERE received >= datetime('now', '-7 day') GROUP BY version, head ORDER BY n DESC LIMIT 20"
```

## Contract

| Route | Body (JSON, PascalCase) | Behavior |
|---|---|---|
| `POST /ping` | `InstallId` (32 hex), `Version`, `Os` | `INSERT OR IGNORE` on `(install_id, UTC day)` → the per-day dedupe IS the DAU count. 204. |
| `POST /crash` | + `Source`, `Details` (≤32k), `At` (ISO) | `INSERT OR IGNORE` on `(install_id, at)` — a client-side resend after a failed delete is absorbed. 204. |

Malformed input → 400, oversized body → 413, other paths/methods → 404/405. All responses are
bodyless; the client treats any non-2xx as a silent failure (max 2 per session, M29 posture).
