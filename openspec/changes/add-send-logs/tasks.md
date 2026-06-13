## 1. Backend (Cloudflare Worker + D1)

- [x] 1.1 Add `log_bundles` table to `worker/schema.sql` (ref_code PK, received_at, instance_id, versions, telemetry, note, log_lines) + indexes; apply to remote D1
- [x] 1.2 Add `POST /logs` route to the Worker: key check, 256 KB cap, rate limits, Web-Crypto ref-code with collision check, insert into `log_bundles`
- [x] 1.3 Add `scheduled()` handler pruning bundles older than 90 days; add daily cron to `wrangler.jsonc`; deploy
- [x] 1.4 curl-test: valid bundle returns ref code + row in D1; bad key 401; missing log_lines 400; cleanup test row

## 2. Plugin

- [x] 2.1 Refactor `GetLogs` log-reading into shared `ReadRecentLogLines`
- [x] 2.2 `TelemetryService.SendLogBundleAsync`: assemble bundle JSON (instance id, versions, telemetry snapshot, note, lines), POST to `/logs`, return ref code; test seam
- [x] 2.3 Admin `POST /Telemetry/SendLogs` endpoint + `SendLogsRequest` DTO; generates a one-off instance id when telemetry is off
- [x] 2.4 Logs-tab "Send logs to developer" button + consent/result modal (disclosure, optional note, preview link, ref-code display + copy)
- [x] 2.5 Tests: bundle shape, telemetry-disabled path, failure path, endpoint requires elevation

## 3. Ship

- [x] 3.1 OpenSpec change `add-send-logs`
- [x] 3.2 Bump to 1.17.0.0; release-notes.ts entry
- [x] 3.3 Build, sideload to the maintainer's server, click send, verify bundle lands in D1 with a ref code
- [x] 3.4 Open PR; remove the disabled public-repo canary file in the same release (housekeeping)

## 4. Deferred (follow-up change)

- [ ] 4.1 AI triage routine: pull new bundles, analyse against telemetry, post private triage notes
- [ ] 4.2 Bundle retrieval CLI/helper for the maintainer
