## ADDED Requirements

### Requirement: Single-table storage with server-computed week

Pings SHALL be stored in one D1 (SQLite) table `pings` (id, received_at, week, instance_id, schema_version, plugin_version, jellyfin_version, ping_type check in ('weekly','error_transition'), features, buckets, errors as JSON text) with a partial unique index on (instance_id, week) for weekly pings. `week` MUST be computed in the ingest Worker from UTC arrival time, never by the client. A `latest_per_instance` view SHALL support active-installs-by-version queries.

#### Scenario: Duplicate weekly ping merges instead of destroying

- **WHEN** a weekly ping arrives for an (instance_id, week) slot that already has a row
- **THEN** the Worker merges error counters into the existing row rather than overwriting, so an earlier window's counts are never silently destroyed

### Requirement: Ingest validation and caps

The Cloudflare Worker SHALL be the sole write path. It MUST require the publishable ingest key header, validate schema_version and payload shape, reject payloads over 2 KB, enforce a per-instance daily cap on error-transition inserts (returning 204 on cap hit), apply a transient in-memory per-IP rate limit plus a global requests-per-minute cap, and never persist IP addresses into the dataset.

#### Scenario: Malformed or oversized payload

- **WHEN** a request arrives with an unknown schema_version, a missing field, a bad key, or a body over 2 KB
- **THEN** the Worker rejects it without writing a row

#### Scenario: Flood of fresh UUIDs

- **WHEN** a client mints new instance UUIDs and posts at high volume
- **THEN** the per-IP and global rate limits bound ingestion volume, and the daily canary's row-growth alarm fails loudly if totals exceed expected bounds

### Requirement: No client-facing database access

The D1 database SHALL be reachable only through the ingest Worker and scoped Cloudflare API tokens (used by the canary workflow and the maintainer's analysis tooling). The plugin SHALL ship only the ingest URL and the publishable write key, whose compromise is bounded to junk rows — never reads, never privacy.

#### Scenario: Extracted key cannot read the dataset

- **WHEN** someone extracts the ingest key from plugin source
- **THEN** they can POST validated, rate-limited payloads and nothing else; no read, list, or delete path exists for them

### Requirement: Always-on backend with loud failure detection

The backend SHALL NOT require keep-alive traffic to stay available (D1 has no idle-pause). The daily canary workflow MUST fail loudly (red run) when the database is unreachable, and MUST keep its own cron schedule alive against GitHub's 60-day auto-disable by committing a heartbeat to a dedicated non-main branch when repo activity is older than 50 days — never to main, where the every-merge-ships pipeline would cut a junk release.

#### Scenario: Quiet repo for two months

- **WHEN** no human commits land for 60+ days
- **THEN** the heartbeat branch commits keep the canary's cron schedule active and ingestion continues uninterrupted

#### Scenario: Backend unreachable

- **WHEN** the canary's first query fails because the database or API is unreachable
- **THEN** the workflow run goes red the same day rather than telemetry dying silently
