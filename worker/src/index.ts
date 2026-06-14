// Telemetry ingest: the SOLE write path into the D1 pings table.
// Contract (openspec/changes/add-opt-in-telemetry/specs/telemetry-backend):
//  - validate write key + schema_version + shape, reject > 2 KB
//  - never persist IPs into the dataset (transient in-memory rate limiting only)
//  - weekly: upsert on (instance_id, week) MERGING counters, never overwriting
//  - error_transition: per-instance daily cap, 204 on cap hit (plugin never retries)
//  - global + per-IP requests-per-minute caps bound abuse from minted UUIDs

export interface Env {
  DB: D1Database;
  INGEST_KEY: string;
}

const CATEGORIES = ["cloudflare_403", "auth_failure", "tmdb_lookup", "jellyseerr_error", "other"];
const MAX_BODY_BYTES = 2048;
const MAX_LOG_BYTES = 262144; // 256 KB cap on a diagnostic bundle
// Crockford base32 minus ambiguous chars, for human-quotable ref codes.
const CODE_ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
const PER_IP_PER_MINUTE = 30;
const GLOBAL_PER_MINUTE = 600;
const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

// Transient, per-isolate, in-memory. IPs never touch storage; isolates recycle
// often, so this is best-effort flood damping, not bookkeeping.
const ipHits = new Map<string, { count: number; windowStart: number }>();
let globalHits = { count: 0, windowStart: 0 };

function rateLimited(ip: string): boolean {
  const now = Date.now();
  if (now - globalHits.windowStart > 60_000) globalHits = { count: 0, windowStart: now };
  if (++globalHits.count > GLOBAL_PER_MINUTE) return true;

  const entry = ipHits.get(ip);
  if (!entry || now - entry.windowStart > 60_000) {
    ipHits.set(ip, { count: 1, windowStart: now });
    if (ipHits.size > 10_000) ipHits.clear();
    return false;
  }
  return ++entry.count > PER_IP_PER_MINUTE;
}

function weekOfUtc(d: Date): string {
  // Monday-based UTC week start, matching TelemetryService.WeekStartUtc.
  const day = (d.getUTCDay() + 6) % 7;
  const monday = new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate() - day));
  return monday.toISOString().slice(0, 10);
}

function bad(status: number, msg: string): Response {
  return new Response(JSON.stringify({ error: msg }), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

// Generate a human-quotable ref code like "LBX-7Q2F9K". Uses Web Crypto (available
// in the Workers runtime), not Math.random.
function genRefCode(): string {
  const bytes = new Uint8Array(6);
  crypto.getRandomValues(bytes);
  let s = "";
  for (const b of bytes) s += CODE_ALPHABET[b % CODE_ALPHABET.length];
  return `LBX-${s}`;
}

// POST /logs — accept a user-initiated diagnostic bundle (sanitized log lines +
// telemetry snapshot), store it privately in the log_bundles D1 table keyed by a
// ref code, return the code. Unlike telemetry, a bundle is NOT anonymous (it may
// carry the user's Letterboxd username or film titles) — but it only arrives on
// an explicit, disclosed user click. The scheduled() handler prunes bundles after
// 90 days.
async function handleLogs(req: Request, env: Env): Promise<Response> {
  const raw = await req.text();
  // Measure actual UTF-8 bytes, not UTF-16 code units, so multibyte content
  // (accented / CJK film titles) is capped accurately.
  if (new TextEncoder().encode(raw).length > MAX_LOG_BYTES) return bad(413, "bundle too large");

  let p: Record<string, unknown>;
  try {
    p = JSON.parse(raw);
  } catch {
    return bad(400, "invalid JSON");
  }
  if (typeof p.instance_id !== "string" || !UUID_RE.test(p.instance_id)) return bad(400, "invalid instance_id");
  if (!Array.isArray(p.log_lines)) return bad(400, "missing log_lines");

  // Find a free ref code (collision is astronomically unlikely; check anyway).
  // Re-check after each regenerate so we never fall through to a colliding INSERT.
  let code = "";
  let allocated = false;
  for (let i = 0; i < 6; i++) {
    code = genRefCode();
    const hit = await env.DB.prepare("SELECT 1 FROM log_bundles WHERE ref_code = ?1").bind(code).first();
    if (!hit) { allocated = true; break; }
  }
  if (!allocated) return bad(503, "could not allocate ref code, retry");

  const logLines = JSON.stringify((p.log_lines as unknown[]).slice(0, 5000).map((l) => String(l).slice(0, 2000)));
  const telemetry = p.telemetry != null ? JSON.stringify(p.telemetry) : null;
  const note = typeof p.note === "string" ? p.note.slice(0, 2000) : null;

  await env.DB.prepare(
    `INSERT INTO log_bundles (ref_code, received_at, instance_id, plugin_version, jellyfin_version, telemetry, note, log_lines)
     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)`,
  ).bind(
    code,
    new Date().toISOString().slice(0, 19) + "Z",
    p.instance_id,
    typeof p.plugin_version === "string" ? p.plugin_version : "unknown",
    typeof p.jellyfin_version === "string" ? p.jellyfin_version : "unknown",
    telemetry,
    note,
    logLines,
  ).run();

  return new Response(JSON.stringify({ ref_code: code }), {
    status: 201,
    headers: { "Content-Type": "application/json" },
  });
}

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    if (req.method !== "POST") return bad(405, "POST only");

    // Publishable write key: stops drive-by scanner POSTs, nothing more. Its
    // extraction from plugin source is expected; junk rows are the worst case.
    if (req.headers.get("x-lbsync-key") !== env.INGEST_KEY) return bad(401, "bad key");

    const ip = req.headers.get("cf-connecting-ip") ?? "unknown";
    if (rateLimited(ip)) return bad(429, "rate limited");

    // Diagnostic-bundle upload is a separate path with its own (larger) size cap.
    if (new URL(req.url).pathname === "/logs") return handleLogs(req, env);

    const raw = await req.text();
    if (raw.length > MAX_BODY_BYTES) return bad(413, "payload too large");

    let p: Record<string, unknown>;
    try {
      p = JSON.parse(raw);
    } catch {
      return bad(400, "invalid JSON");
    }

    if (p.schema_version !== 1) return bad(400, "unknown schema_version");
    if (typeof p.instance_id !== "string" || !UUID_RE.test(p.instance_id)) return bad(400, "invalid instance_id");
    if (p.ping_type !== "weekly" && p.ping_type !== "error_transition") return bad(400, "invalid ping_type");
    if (typeof p.plugin_version !== "string" || p.plugin_version.length > 32) return bad(400, "invalid plugin_version");
    if (typeof p.jellyfin_version !== "string" || p.jellyfin_version.length > 32) return bad(400, "invalid jellyfin_version");
    if (typeof p.features !== "object" || p.features === null) return bad(400, "invalid features");
    if (typeof p.buckets !== "object" || p.buckets === null) return bad(400, "invalid buckets");
    if (typeof p.errors !== "object" || p.errors === null) return bad(400, "invalid errors");

    const features = Object.fromEntries(
      Object.entries(p.features as Record<string, unknown>)
        .filter(([, v]) => typeof v === "boolean").slice(0, 24),
    );
    const buckets = Object.fromEntries(
      Object.entries(p.buckets as Record<string, unknown>)
        .filter(([, v]) => typeof v === "string" && (v as string).length <= 16).slice(0, 8),
    );
    const errsIn = p.errors as Record<string, unknown>;
    const errors: Record<string, unknown> = {};
    for (const c of CATEGORIES) {
      errors[c] = typeof errsIn[c] === "number" ? Math.min(Math.max(errsIn[c] as number, 0), 1_000_000) : 0;
    }
    const stateIn = (errsIn.state ?? {}) as Record<string, unknown>;
    errors.state = Object.fromEntries(CATEGORIES.map((c) => [c, stateIn[c] === true]));

    const now = new Date();
    const receivedAt = now.toISOString().slice(0, 19) + "Z";
    const week = weekOfUtc(now);
    const instanceId = (p.instance_id as string).toLowerCase();

    if (p.ping_type === "error_transition") {
      // Server-side daily cap: 204 and drop on hit; the plugin queues and
      // consolidates client-side, so nothing is lost.
      const capRow = await env.DB.prepare(
        "SELECT COUNT(*) AS n FROM pings WHERE instance_id = ?1 AND ping_type = 'error_transition' AND received_at > ?2",
      ).bind(instanceId, new Date(now.getTime() - 86_400_000).toISOString().slice(0, 19) + "Z").first<{ n: number }>();
      if ((capRow?.n ?? 0) >= 1) return new Response(null, { status: 204 });

      await env.DB.prepare(
        `INSERT INTO pings (received_at, week, instance_id, schema_version, plugin_version, jellyfin_version, ping_type, features, buckets, errors)
         VALUES (?1, ?2, ?3, 1, ?4, ?5, 'error_transition', ?6, ?7, ?8)`,
      ).bind(receivedAt, week, instanceId, p.plugin_version, p.jellyfin_version,
        JSON.stringify(features), JSON.stringify(buckets), JSON.stringify(errors)).run();
      return new Response(null, { status: 201 });
    }

    // Weekly: merge counters on conflict rather than overwriting, so a duplicate
    // same-week send can never destroy an earlier window's counts.
    const existing = await env.DB.prepare(
      "SELECT id, errors FROM pings WHERE instance_id = ?1 AND week = ?2 AND ping_type = 'weekly'",
    ).bind(instanceId, week).first<{ id: number; errors: string }>();

    if (existing) {
      let prev: Record<string, unknown> = {};
      try { prev = JSON.parse(existing.errors); } catch { /* treat as empty */ }
      const merged: Record<string, unknown> = { ...errors };
      for (const c of CATEGORIES) {
        merged[c] = ((typeof prev[c] === "number" ? prev[c] as number : 0)) + (merged[c] as number);
      }
      await env.DB.prepare(
        `UPDATE pings SET received_at = ?1, plugin_version = ?2, jellyfin_version = ?3,
                          features = ?4, buckets = ?5, errors = ?6 WHERE id = ?7`,
      ).bind(receivedAt, p.plugin_version, p.jellyfin_version,
        JSON.stringify(features), JSON.stringify(buckets), JSON.stringify(merged), existing.id).run();
      return new Response(null, { status: 200 });
    }

    await env.DB.prepare(
      `INSERT INTO pings (received_at, week, instance_id, schema_version, plugin_version, jellyfin_version, ping_type, features, buckets, errors)
       VALUES (?1, ?2, ?3, 1, ?4, ?5, 'weekly', ?6, ?7, ?8)`,
    ).bind(receivedAt, week, instanceId, p.plugin_version, p.jellyfin_version,
      JSON.stringify(features), JSON.stringify(buckets), JSON.stringify(errors)).run();
    return new Response(null, { status: 201 });
  },

  // Daily prune: diagnostic bundles auto-delete after 90 days so user logs are
  // never hoarded. (Telemetry pings are anonymous and kept; bundles are not.)
  async scheduled(_event: ScheduledController, env: Env): Promise<void> {
    // received_at is stored as "YYYY-MM-DDTHH:MM:SSZ"; compare against the same format
    // (datetime() renders "YYYY-MM-DD HH:MM:SS" and would mis-sort at the T/space char).
    await env.DB.prepare(
      "DELETE FROM log_bundles WHERE received_at < strftime('%Y-%m-%dT%H:%M:%SZ','now','-90 days')",
    ).run();
  },
};
