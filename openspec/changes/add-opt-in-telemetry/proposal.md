## Why

The plugin is at v1.15.2 with its roadmap shipped, and the only usage signal is GitHub release download counts — installs, but nothing about which of the 12 per-account settings people enable, which versions actually run, how often syncs fail in the wild, or at what scale. Roadmap decisions are guesses. The goal is a closed loop: anonymous opt-in usage data lands in a queryable store, the maintainer's AI tooling analyses it over SQL, and insights become GitHub issues and PRs. Because every merge auto-ships a release, the same data doubles as a fleet-wide canary that detects regressions (e.g. a Cloudflare 403 spike on the newest releases) and auto-files evidence-backed issues.

The audience is self-hosted Jellyfin users — the most telemetry-hostile population there is — so the privacy posture is the feature: opt-in default-off with a one-time prompt (Home Assistant model), a minimal anonymous payload, and radical transparency (exact payload documented in the README, a "preview exact JSON" button in settings).

## What Changes

- New Supabase backend (free tier): one `pings` table, one `/ingest` edge function (validates shape, caps size, rate-limits, never persists IPs into the dataset), RLS deny-all so the shipped anon key cannot touch the table directly.
- New plugin config: `TelemetryEnabled` (default false), `TelemetryInstanceId` (random UUID generated on enable, user-regenerable), persisted window counters and per-category error state.
- New `TelemetryTask : IScheduledTask`: weekly anonymous ping with ±6 h jitter; payload is plugin/Jellyfin version, feature-toggle booleans, bucketed counts (never raw numbers), and per-period error-category counts.
- Error-transition pings: when any error category goes clean → failing, a ping carrying the full error-state map fires, capped at one per instance per day (further transitions queue and consolidate).
- Settings UI: opt-in checkbox, UUID regenerate button, admin-only "Preview exact JSON" modal that doubles as a copy-diagnostic-bundle for bug reports (with an explicit deanonymization warning).
- One-time dismissible opt-in banner on the plugin dashboard tab, shipped as a separate release after the pipe is proven.
- README "Telemetry" section documenting the exact payload and the precisely-worded anonymity promise.
- Two scheduled GitHub Actions: `telemetry-keepalive.yml` (daily DB touch so the free tier never pauses; self-sustaining against GitHub's 60-day cron auto-disable) and `telemetry-canary.yml` (daily regression check that auto-files gated, deduped GitHub issues).

## Capabilities

### New Capabilities
- `telemetry-collection`: What the plugin collects, when it sends, the opt-in/consent UX, and the transparency surfaces (preview, README).
- `telemetry-backend`: The ingest contract, storage schema, key/RLS model, abuse bounds, and keep-alive.
- `fleet-canary`: The daily regression check — cohort definitions, statistical gates, Sybil resistance, and the auto-filed issue contract.

### Modified Capabilities
<!-- No existing specs in openspec/specs/ to modify. -->

## Impact

- **New plugin code**: `TelemetryTask`, payload builder, error-state tracking wired into existing failure paths, `GET /Telemetry/Preview` admin endpoint, config-page banner/checkbox/modal. Two plugin releases (pipe first, prompt second), both through the normal version-gated pipeline.
- **New infrastructure**: one Supabase project (migration + edge function), maintained near-zero; the maintainer queries it via the existing Supabase MCP connection.
- **New workflows**: `telemetry-keepalive.yml`, `telemetry-canary.yml`; repo secrets gain the Supabase read key. The keep-alive heartbeat commits only to a dedicated non-main branch so the every-merge-ships release pipeline is never triggered by it.
- **README** gains the telemetry section; the privacy wording is load-bearing and reviewed like code.
- **No behaviour change for users who don't opt in**: telemetry is dead code until the checkbox is ticked; the only visible change is the one-time banner.
- **Shipped write key is extractable by design**: worst case is junk rows (data-quality problem bounded by validation, rate limits, and a growth alarm), never a privacy problem.
