// M39 telemetry Worker — receives the app's anonymous launch pings and crash reports into D1.
// Contract (client: src/Overlay.Core/Telemetry/TelemetryService.cs, PascalCase JSON):
//   POST /ping  {"InstallId","Version","Os"}
//   POST /crash {"InstallId","Version","Os","Source","Details","At"}
// Everything else: 404/405. No cookies, no IP storage, no auth (the data is worthless to abuse
// and per-key size caps + INSERT OR IGNORE bound what a hostile client could write).

const MAX_BODY_BYTES = 64 * 1024;
const MAX_DETAILS_CHARS = 32 * 1024;

// 32 lowercase hex chars — Guid.ToString("N") from the client.
const INSTALL_ID = /^[0-9a-f]{32}$/;

export default {
  async fetch(request, env) {
    if (request.method !== 'POST') return status(405);

    const path = new URL(request.url).pathname;
    if (path !== '/ping' && path !== '/crash') return status(404);

    const raw = await request.text();
    if (raw.length > MAX_BODY_BYTES) return status(413);

    let body;
    try { body = JSON.parse(raw); } catch { return status(400); }
    if (typeof body !== 'object' || body === null) return status(400);

    const installId = str(body.InstallId, 32).toLowerCase();
    const version = str(body.Version, 64);
    const os = str(body.Os, 64);
    if (!INSTALL_ID.test(installId) || !version || !os) return status(400);

    if (path === '/ping') {
      const day = new Date().toISOString().slice(0, 10); // UTC — server clock, not client
      await env.DB.prepare(
        'INSERT OR IGNORE INTO pings (install_id, day, version, os) VALUES (?1, ?2, ?3, ?4)')
        .bind(installId, day, version, os).run();
      return status(204);
    }

    // /crash
    const source = str(body.Source, 64);
    const at = str(body.At, 40);
    const details = str(body.Details, MAX_DETAILS_CHARS);
    if (!at || Number.isNaN(Date.parse(at))) return status(400);
    await env.DB.prepare(
      'INSERT OR IGNORE INTO crashes (install_id, at, version, os, source, details) VALUES (?1, ?2, ?3, ?4, ?5, ?6)')
      .bind(installId, at, version, os, source, details).run();
    return status(204);
  },
};

function str(value, maxChars) {
  return typeof value === 'string' ? value.slice(0, maxChars) : '';
}

function status(code) {
  return new Response(null, { status: code });
}
