## ADDED Requirements

### Requirement: Explicit, disclosed consent before sending

The "Send logs to developer" action SHALL require an explicit click and a confirmation step that discloses, before anything is sent: that the bundle is NOT anonymous and may contain a Letterboxd username or film titles, that it is linked to this instance's telemetry id, and that passwords, cookies, and auth tokens are never logged. A preview of what would be sent MUST be available from the confirmation step, and that preview MUST show the COMPLETE bundle — the actual log lines AND the telemetry snapshot — not just the anonymous portion. The preview and the send MUST be assembled by the same code path so the preview cannot diverge from what is uploaded.

#### Scenario: User opens the send dialog

- **WHEN** an admin clicks "Send logs to developer"
- **THEN** a confirmation modal appears stating the logs are not anonymous and offering a preview, and nothing is uploaded until the admin confirms

#### Scenario: Preview shows the real log lines

- **WHEN** the admin clicks "Preview exactly what's sent"
- **THEN** the preview renders the exact bundle including the log lines (not only the anonymous telemetry snapshot), byte-for-byte identical to what the send would upload

#### Scenario: User cancels

- **WHEN** the admin closes or cancels the confirmation modal
- **THEN** no bundle is uploaded

### Requirement: Bundle contents

The bundle SHALL contain only: recent LetterboxdSync-tagged log lines (the same sanitized lines the Logs tab shows), the current telemetry snapshot, the plugin and Jellyfin versions, an instance id, and an optional user-supplied note. It MUST reuse the existing sanitized log reader so no content beyond the Logs-tab lines is included.

#### Scenario: Bundle assembled from the shared log reader

- **WHEN** a send is confirmed
- **THEN** the bundle's log lines are exactly those produced by the shared `ReadRecentLogLines` reader, capped to the recent window, and the telemetry snapshot is embedded as structured JSON

### Requirement: Works regardless of telemetry opt-in

Sending logs SHALL succeed whether or not anonymous telemetry is enabled. If a telemetry instance id exists it is used (so the bundle joins that instance's telemetry); if not, a one-off id is generated for the bundle.

#### Scenario: Telemetry disabled

- **WHEN** an admin with telemetry disabled sends logs
- **THEN** the bundle uploads successfully using a generated instance id and a snapshot reflecting the disabled configuration

### Requirement: Admin-only endpoint

The `POST /Telemetry/SendLogs` endpoint SHALL require elevated (admin) authorization, the same policy as the configuration page that hosts it.

#### Scenario: Non-admin caller

- **WHEN** a non-admin or unauthenticated client calls the endpoint
- **THEN** the request is rejected by the RequiresElevation policy

### Requirement: Reference code returned and quotable

On a successful send the user SHALL receive a short, human-quotable reference code (e.g. `LBX-7Q2F9K`) to optionally quote in a bug report so the maintainer can locate the bundle. Delivery is push: the bundle is stored regardless of whether the user opens a report.

#### Scenario: Successful send

- **WHEN** a bundle uploads successfully
- **THEN** the UI shows the returned reference code and a copy action, and the bundle is retrievable by that code

#### Scenario: Backend unreachable

- **WHEN** the upload fails (no connectivity, backend down)
- **THEN** the UI shows a clear error and no reference code, and the action can be retried

### Requirement: Private storage with bounded retention

Bundles SHALL be stored privately in the `log_bundles` D1 table, reachable only through the ingest Worker and scoped API tokens. A scheduled Worker job MUST delete bundles older than 90 days. The `/logs` route MUST enforce the publishable-key check, a 256 KB size cap, and the per-IP and global rate limits.

#### Scenario: Bundle pruned after retention window

- **WHEN** a bundle is older than 90 days at the daily prune
- **THEN** it is deleted from the table

#### Scenario: Oversized or unauthenticated upload

- **WHEN** a `/logs` request exceeds 256 KB or omits the valid key
- **THEN** the Worker rejects it (413 or 401 respectively) without storing anything
