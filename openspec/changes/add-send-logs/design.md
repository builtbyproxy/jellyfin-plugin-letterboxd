# Design

## Decision 1: Push + ref-code hybrid

The user chose push (bundle lands automatically so the pile can be AI-triaged) but also wanted a ref code for users who go on to open a bug report. So the endpoint always stores the bundle AND returns a code. No required support conversation, but the code makes one possible. Best of both: triageable pile + linkable bundles.

## Decision 2: D1, not R2

R2 (object storage) is the textbook home for log blobs, but enabling R2 requires a Cloudflare dashboard activation (and typically a card on file) even on the free tier. Bundles are capped at 256 KB and user-initiated (rare), which fits comfortably in a D1 table, and D1 is already enabled and bound to the Worker. So bundles live in a `log_bundles` D1 table. Retention is a Worker `scheduled()` cron that deletes rows older than 90 days, rather than an R2 lifecycle rule.

## Decision 3: Consent is the feature

This is the only place the system collects identifying data, so the gate is explicit and loud: a confirm modal states the bundle is not anonymous (may contain a Letterboxd username or film titles), that it links to this instance's telemetry id, and that passwords/cookies/tokens are never logged. A preview reuses the telemetry-preview modal (the snapshot) plus the already-visible Logs tab (the lines). Nothing sends without the click.

## Decision 4: Reuse the existing sanitized log reader

`GetLogs` already reads only LetterboxdSync-tagged lines from the two newest Jellyfin log files and relies on the plugin never logging secrets. That logic is refactored into `ReadRecentLogLines` and shared, so the bundle carries exactly what the Logs tab shows, nothing more.

## Decision 5: Works without telemetry opt-in

A user needing help should not have to enable anonymous telemetry first. If a telemetry instance id exists, the bundle uses it (so logs join to that instance's telemetry history); if not, a one-off random id is generated for the bundle. Either way the bundle is self-contained (it embeds the current telemetry snapshot).

## Decision 6: Same backend guards as ingest

The `/logs` route reuses the publishable key check, the per-IP and global rate limits, and adds a 256 KB body cap. The key's compromise is bounded to junk bundles, same risk profile as junk pings. Server-computed ref codes use Web Crypto, with a collision check against the table.

## Deferred

- **AI triage routine** — pull new bundles, analyse each against its instance's telemetry, post a private triage note. Build after capture is proven and bundles exist.
- **Bundle retrieval tooling** — for now the maintainer reads bundles via a D1 query by ref code; a nicer fetch CLI can follow.
