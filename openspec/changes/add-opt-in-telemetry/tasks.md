## 1. Backend (no plugin release)

- [ ] 1.1 Create the Supabase project (or reuse the existing org) and apply the `pings` migration: columns per spec, unique (instance_id, week) where ping_type='weekly', `latest_per_instance` view, RLS deny-all for anon
- [ ] 1.2 Implement the `/ingest` edge function: schema_version + shape validation, 2 KB cap, server-computed UTC `week`, weekly upsert with counter-merging ON CONFLICT, error-transition insert with per-instance daily cap (204 on hit), transient per-IP + global rate limits, service-role write, IP never persisted
- [ ] 1.3 curl-test the full matrix: valid weekly, duplicate same-week (verify merge), valid transition, capped transition (verify 204), oversized payload, malformed payload, direct table access with anon key (verify RLS denial)
- [ ] 1.4 Add `.github/workflows/telemetry-keepalive.yml`: daily SELECT, loud failure on unreachable DB, heartbeat commit to `telemetry-heartbeat` branch when repo activity > 50 days old
- [ ] 1.5 Add the Supabase read key to repo secrets; verify a Supabase MCP query returns the curl-test rows

## 2. Plugin release 1 — the pipe (feat: anonymous opt-in telemetry)

- [ ] 2.1 Add `TelemetryEnabled` (default false), `TelemetryInstanceId`, persisted window counters, last-successful-ping timestamp, and per-category error-state booleans to PluginConfiguration
- [ ] 2.2 Implement the payload builder: versions, feature-toggle booleans, bucketed counts (accounts 1/2-4/5+, library <500/500-2k/2k-10k/10k+, syncs-per-week 0/1-10/11-100/100+), per-period error-category counts
- [ ] 2.3 Implement `TelemetryTask : IScheduledTask`: weekly with ±6 h jitter, week-rollover send gate, counter flush on success
- [ ] 2.4 Wire error-category detection (cloudflare_403, auth_failure, tmdb_lookup, jellyseerr_error, other) into the existing failure paths; rising-edge transition pings carrying the full state map; client-side queue-and-consolidate when the daily cap is spent
- [ ] 2.5 Add admin-authorized `GET /Telemetry/Preview` returning the exact next payload
- [ ] 2.6 Settings UI: opt-in checkbox, UUID Regenerate button, Preview modal with copy-diagnostic-bundle + deanonymization warning + post-filing regenerate shortcut
- [ ] 2.7 README "Telemetry" section: example payload, precise anonymity wording (dataset vs platform logs), opt-in default, regeneration unlinks-not-erases
- [ ] 2.8 Tests: payload bucketing, week-gate, transition state machine (rising edge, cap, consolidation, restart persistence), preview auth; verify a real ping from the maintainer's server lands and is queryable via MCP
- [ ] 2.9 Version bump + release notes per the release process

## 3. Plugin release 2 — the prompt (feat: telemetry opt-in banner)

- [ ] 3.1 Draft the banner copy (3-4 sentences) and get reactions from real users (GitHub Discussions or the r/jellyfin thread) before shipping
- [ ] 3.2 One-time dismissible banner on the dashboard tab: Enable / No thanks, "see exactly what's sent" link opening the preview modal, dismissed-forever state in configuration
- [ ] 3.3 Tests: banner shows once, both actions dismiss permanently, Enable generates UUID and schedules the task
- [ ] 3.4 Version bump + release notes

## 4. Canary (no plugin release)

- [ ] 4.1 Add `.github/workflows/telemetry-canary.yml`: daily + manual dispatch, SQL checks implementing the cohort definitions (14-day foreground pool, baseline exclusions, any-ping cohort assignment, transition-ping rates), Sybil gate (≥2 weeks history), issue gates (n ≥ 10 both cohorts, ≥3x multiple)
- [ ] 4.2 Issue template with error category, per-version evidence table, window, and the exact query; dedup on (suspect version, error_category) against open issues
- [ ] 4.3 Row-growth alarm in the same daily query (total rows + new instances outside expected bounds → red run)
- [ ] 4.4 Dry-run against synthetic data: healthy fleet (silent), real regression (files once, not daily), minted-UUID flood (silent)

## 5. Wrap-up

- [ ] 5.1 Update wiki/site docs if the plugin's public feature description changes
- [ ] 5.2 After 90 days: review n, decide whether the deferred public stats page earns its keep, and record the first data-driven roadmap decision in an issue citing telemetry numbers
