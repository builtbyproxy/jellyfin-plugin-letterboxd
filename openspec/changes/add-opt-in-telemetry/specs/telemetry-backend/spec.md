## ADDED Requirements

### Requirement: Single-table storage with server-computed week

Pings SHALL be stored in one table `pings` (id, received_at, instance_id uuid, schema_version int, plugin_version text, jellyfin_version text, ping_type check in ('weekly','error_transition'), features jsonb, buckets jsonb, errors jsonb, week date NOT NULL) with a unique constraint on (instance_id, week) for weekly pings. `week` MUST be computed in the ingest function from UTC arrival time, not by the client and not as a Postgres generated column (non-immutable over timestamptz). A `latest_per_instance` view SHALL support active-installs-by-version queries.

#### Scenario: Duplicate weekly ping merges instead of destroying

- **WHEN** a weekly ping arrives for an (instance_id, week) slot that already has a row
- **THEN** the ON CONFLICT clause merges counters rather than overwriting, so an earlier window's counts are never silently destroyed

### Requirement: Ingest validation and caps

The `/ingest` edge function SHALL be the sole write path. It MUST validate schema_version and payload shape, reject payloads over 2 KB, enforce a per-instance daily cap on error-transition inserts (returning 204 on cap hit), apply a transient in-memory per-IP rate limit plus a global requests-per-minute cap, and never persist IP addresses into the dataset.

#### Scenario: Malformed or oversized payload

- **WHEN** a request arrives with an unknown schema_version, a missing field, or a body over 2 KB
- **THEN** the function rejects it without writing a row

#### Scenario: Flood of fresh UUIDs

- **WHEN** a client mints new instance UUIDs and posts at high volume
- **THEN** the per-IP and global rate limits bound ingestion volume, and the daily canary's row-growth alarm fails loudly if totals exceed expected bounds

### Requirement: Key and RLS model

The plugin SHALL ship only the anon key. Row Level Security MUST deny all direct access to `pings` for the anon role; the ingest function writes with its internal service role after validation. The repository's documentation SHALL state that the shipped key is publishable by design and its compromise is bounded to junk rows, never reads or privacy.

#### Scenario: Extracted key cannot read the dataset

- **WHEN** someone extracts the anon key from plugin source and queries the `pings` table directly
- **THEN** RLS denies the read and the write; only `/ingest` accepts traffic

### Requirement: Free-tier keep-alive that fails loudly

A daily scheduled workflow SHALL touch the database (one SELECT) so the free-tier project never pauses. The workflow MUST keep itself alive against GitHub's 60-day cron auto-disable by committing a heartbeat timestamp to a dedicated non-main branch when repo activity is older than 50 days — never to main, where the every-merge-ships pipeline would cut a junk release. Any run that cannot reach the database MUST fail the workflow (red + notification), so a paused project is detected within a day.

#### Scenario: Quiet repo for two months

- **WHEN** no human commits land for 60+ days
- **THEN** the heartbeat branch commits keep the cron schedule active, the daily SELECT keeps Supabase unpaused, and ingestion continues uninterrupted

#### Scenario: Project paused anyway

- **WHEN** the keep-alive's SELECT fails because the project is paused or unreachable
- **THEN** the workflow run goes red the same day rather than telemetry dying silently
