-- Telemetry storage on Cloudflare D1 (SQLite). The database is reachable ONLY
-- through the ingest Worker and scoped API tokens; there is no direct client
-- access path, which is what RLS provided in the abandoned Supabase design.
-- See openspec/changes/add-opt-in-telemetry/specs/telemetry-backend.

CREATE TABLE IF NOT EXISTS pings (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    received_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    -- Computed by the Worker from UTC arrival time (client clocks drift).
    week TEXT NOT NULL,
    instance_id TEXT NOT NULL,
    schema_version INTEGER NOT NULL,
    plugin_version TEXT NOT NULL,
    jellyfin_version TEXT NOT NULL,
    ping_type TEXT NOT NULL CHECK (ping_type IN ('weekly', 'error_transition')),
    features TEXT NOT NULL DEFAULT '{}',
    buckets TEXT NOT NULL DEFAULT '{}',
    errors TEXT NOT NULL DEFAULT '{}'
);

-- One weekly row per instance per week; the Worker merges counters on conflict
-- so a duplicate send can never destroy a window's counts.
CREATE UNIQUE INDEX IF NOT EXISTS pings_weekly_instance_week
    ON pings (instance_id, week) WHERE ping_type = 'weekly';

CREATE INDEX IF NOT EXISTS pings_instance_received ON pings (instance_id, received_at DESC);
CREATE INDEX IF NOT EXISTS pings_type_received ON pings (ping_type, received_at DESC);

-- Active installs by version: latest weekly ping per instance.
CREATE VIEW IF NOT EXISTS latest_per_instance AS
SELECT p.instance_id, p.received_at, p.week, p.plugin_version, p.jellyfin_version,
       p.features, p.buckets, p.errors
FROM pings p
JOIN (
    SELECT instance_id, MAX(received_at) AS max_received
    FROM pings WHERE ping_type = 'weekly' GROUP BY instance_id
) latest ON latest.instance_id = p.instance_id AND latest.max_received = p.received_at
WHERE p.ping_type = 'weekly';
