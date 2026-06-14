## Why

Anonymous telemetry (see [[add-opt-in-telemetry]]) tells the maintainer *that* something is wrong across the fleet, but it deliberately carries no detail: bucketed counts and error categories, never log text. When a specific user hits a problem (the first real external user hit a first-run `auth_failure` within seconds of installing), there's no way to see *why*. The plugin already writes sanitized logs locally and shows them on a Logs tab, but getting them to the developer means a manual copy-paste into a bug report, which most users won't do.

A one-click "Send logs to developer" closes the loop: telemetry flags the pattern cheaply and anonymously; an affected user opts in to handing over the detail needed to diagnose it. The send is an explicit, disclosed click, so it does not compromise the anonymous-telemetry privacy posture.

## What Changes

- New admin endpoint `POST /Telemetry/SendLogs`: builds a diagnostic bundle (recent sanitized log lines + the current telemetry snapshot + plugin/Jellyfin versions + an optional user note) and uploads it to the telemetry backend, returning a short reference code.
- New "Send logs to developer" button on the Logs tab, with a consent modal that discloses the bundle is **not anonymous** (may contain a Letterboxd username or film titles) and is linked to this instance's telemetry id, a preview link, an optional "what went wrong?" note, and a result showing the reference code to quote in a bug report.
- New `POST /logs` route on the existing Cloudflare Worker: validates the publishable key, caps the bundle at 256 KB, generates a quotable ref code (e.g. `LBX-7Q2F9K`), and stores the bundle in a new `log_bundles` D1 table. A Worker `scheduled()` cron prunes bundles after 90 days so user logs are never hoarded.
- Bundle delivery is **push** (every bundle lands so it can be triaged) **plus** the returned ref code (so a user can optionally tie their bundle to a bug report they open).

### New Capabilities
- `log-bundles`: the user-initiated diagnostic bundle — what it contains, the consent/disclosure UX, the upload contract, storage, retention, and the ref-code linkage.

### Modified Capabilities
- `telemetry-backend` (from [[add-opt-in-telemetry]]): the Worker gains the `/logs` route, the `log_bundles` table, and a scheduled prune. The anonymous-pings contract is unchanged.

## Impact

- **Plugin**: new controller endpoint + `SendLogsRequest`; a shared `ReadRecentLogLines` helper (refactored out of `GetLogs`); `TelemetryService.SendLogBundleAsync`; Logs-tab button + consent/result modal. One plugin release (1.17.0.0).
- **Backend**: `/logs` route, `log_bundles` D1 table, daily prune cron on the existing Worker. No new Cloudflare service (D1 was chosen over R2 to avoid R2's account-activation requirement).
- **Privacy**: this is the one place identifying user data is collected, and it is gated behind an explicit click with full disclosure, a preview, 90-day retention, and the same publishable-key/size-cap/rate-limit guards as ingest. Works whether or not anonymous telemetry is enabled.
- **Triage** (deferred to a follow-up): an AI routine that pulls new bundles and analyses them against telemetry. Not in this change; capture first.
