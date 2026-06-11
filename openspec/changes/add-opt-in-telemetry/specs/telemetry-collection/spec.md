## ADDED Requirements

### Requirement: Telemetry is opt-in and off by default

Telemetry SHALL be disabled by default. No network request to the telemetry backend may ever occur unless the admin has explicitly enabled `TelemetryEnabled`. Disabling telemetry MUST stop all pings immediately.

#### Scenario: Fresh install never phones home

- **WHEN** the plugin is installed or upgraded and the admin has never touched telemetry settings
- **THEN** no request is made to the telemetry backend, ever, regardless of how long the plugin runs

#### Scenario: Disabling stops pings immediately

- **WHEN** an admin unchecks the telemetry setting and saves
- **THEN** scheduled and error-transition pings stop, and the persisted instance UUID, counters, and error state remain locally but are no longer transmitted

### Requirement: One-time opt-in prompt

A dismissible banner SHALL appear once on the plugin dashboard tab asking the admin to enable telemetry, with honest copy, a link that opens the exact-payload preview, and Enable / No thanks actions. Either action MUST permanently dismiss the banner.

#### Scenario: Banner shows exactly once

- **WHEN** an admin opens the plugin dashboard and has neither enabled telemetry nor dismissed the banner before
- **THEN** the banner renders; after the admin clicks Enable or No thanks, it is never shown again on any subsequent visit

### Requirement: Anonymous minimal payload

The telemetry payload SHALL contain only: schema version, plugin version, Jellyfin version, ping type, the random instance UUID, feature-toggle booleans, bucketed counts, and per-period error-category counts. It MUST NOT contain IP addresses, usernames, film titles, library content, or raw (unbucketed) counts.

#### Scenario: Counts are bucketed

- **WHEN** a payload is built for an instance with 3 linked accounts, a 1,200-film library, and 37 syncs since the last ping
- **THEN** the payload reports accounts "2-4", library "500-2k", syncs-per-week "11-100", and no exact numbers

### Requirement: Regenerable random instance identity

The instance UUID SHALL be generated randomly when telemetry is first enabled, never derived from hardware, network, or Jellyfin identifiers. Settings MUST offer a Regenerate action. Documentation MUST state that regeneration unlinks the identifier going forward but does not erase old rows, and that configuration similarity may still allow correlation at small fleet sizes.

#### Scenario: Regenerate unlinks future pings

- **WHEN** the admin clicks Regenerate
- **THEN** a new random UUID replaces the old one in configuration and all subsequent pings carry only the new UUID

### Requirement: Weekly ping with jitter and week-boundary gating

An `IScheduledTask` SHALL send the weekly ping with a random jitter of ±6 hours. The task MUST NOT send if the server-computed week of the last successful ping has not yet rolled over, preventing two different payloads from targeting the same (instance, week) slot.

#### Scenario: Jitter cannot double-send within one week

- **WHEN** the previous successful weekly ping was 6.5 days ago and the scheduler fires early due to jitter
- **THEN** the task skips sending and retries on its next scheduled run

### Requirement: Persisted counters and error state

Window counters (measured since the last successful weekly ping), the last-successful-ping timestamp, and per-category error-state booleans SHALL persist in PluginConfiguration and flush on each successful ping.

#### Scenario: Restart does not zero the window

- **WHEN** the Jellyfin container restarts mid-week after 12 syncs were counted
- **THEN** the next weekly payload still reflects those 12 syncs in its bucket

### Requirement: Error-transition pings

When any error category transitions clean → failing, a ping of type `error_transition` carrying the full five-category error-state map SHALL fire, rate-limited to one per instance per day. Further transitions during a spent cap window MUST be queued and sent as one consolidated ping when the window reopens — deferred, never silently dropped client-side. Recovery (failing → clean) does not fire a ping; it is visible in the next weekly payload.

#### Scenario: Second category trips during the cap window

- **WHEN** cloudflare_403 transitions at 09:00 (ping sent) and tmdb_lookup transitions at 14:00 the same day
- **THEN** the plugin queues the change and sends one consolidated transition ping carrying both categories' state after the daily window reopens

### Requirement: Payload preview and diagnostic bundle

An admin-authorized endpoint `GET /Telemetry/Preview` SHALL return the exact JSON the next ping would send, rendered in a settings modal. The modal MUST offer copy-as-diagnostic-bundle for bug reports and MUST warn that the bundle contains the instance UUID (pasting it publicly links that identity to the instance's ping history), offering one-click regeneration after filing.

#### Scenario: Preview requires admin

- **WHEN** a non-admin or unauthenticated caller requests `/Telemetry/Preview`
- **THEN** the request is rejected with the same authorization policy as the plugin configuration page

### Requirement: README documents the exact payload

The README SHALL contain a Telemetry section showing the full example payload, the precise anonymity wording (no IPs/usernames/titles in the dataset; platform transport logs are the platform's, retained per its policy), the opt-in default, and the regeneration semantics.

#### Scenario: A skeptical user audits the claim

- **WHEN** a user reads the README Telemetry section and clicks Preview in settings
- **THEN** the documented payload shape and the previewed JSON match field-for-field
