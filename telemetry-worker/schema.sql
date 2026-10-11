-- M39 telemetry D1 schema. Apply with:
--   npx wrangler d1 execute fova-telemetry --remote --file=schema.sql

-- One row per install per UTC day (the PRIMARY KEY is the DAU dedupe: the client pings every
-- launch, INSERT OR IGNORE keeps the first per day). No IP, no account, no game data — ever.
CREATE TABLE IF NOT EXISTS pings (
  install_id TEXT NOT NULL,
  day        TEXT NOT NULL,            -- UTC YYYY-MM-DD, stamped server-side
  version    TEXT NOT NULL,
  os         TEXT NOT NULL,
  first_seen TEXT NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (install_id, day)
);

-- One row per crash. (install_id, at) is the dedupe key: the client deletes a queued report only
-- after a 2xx, so a delete that failed re-sends the same report — INSERT OR IGNORE absorbs it.
CREATE TABLE IF NOT EXISTS crashes (
  install_id TEXT NOT NULL,
  at         TEXT NOT NULL,            -- client-side crash time (ISO 8601, UTC)
  version    TEXT NOT NULL,
  os         TEXT NOT NULL,
  source     TEXT NOT NULL,            -- e.g. "UI-thread (Dispatcher)", "AppDomain"
  details    TEXT NOT NULL,            -- scrubbed exception text (client caps at 16k chars)
  received   TEXT NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (install_id, at)
);

CREATE INDEX IF NOT EXISTS idx_pings_day      ON pings (day);
CREATE INDEX IF NOT EXISTS idx_crashes_received ON crashes (received);
