## MODIFIED Requirements

### Requirement: Single-table storage with server-computed week

(Extended) The Worker SHALL also expose a `/logs` route that accepts user-initiated diagnostic bundles and stores them in a separate `log_bundles` table (ref_code primary key, received_at, instance_id, plugin_version, jellyfin_version, telemetry JSON, note, log_lines JSON). This is distinct from the anonymous `pings` table and follows a different retention rule (90-day prune). The anonymous-pings storage and contract are unchanged.

#### Scenario: Log bundle stored separately from pings

- **WHEN** a valid bundle is POSTed to `/logs`
- **THEN** it is inserted into `log_bundles` with a generated ref code and never touches the `pings` table

### Requirement: Always-on backend with loud failure detection

(Extended) The Worker SHALL run a daily `scheduled()` job that deletes `log_bundles` rows older than 90 days, so user-identifying bundles are never retained indefinitely. The anonymous `pings` are not subject to this prune.

#### Scenario: Scheduled prune runs

- **WHEN** the daily scheduled job executes
- **THEN** bundles older than 90 days are removed and anonymous pings are left intact
