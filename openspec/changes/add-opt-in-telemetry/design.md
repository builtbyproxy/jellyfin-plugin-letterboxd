# Design

Source: office-hours design doc `~/.gstack/projects/builtbyproxy-jellyfin-plugin-letterboxd/lachlan-main-design-20260611-144403.md` (3 rounds of adversarial review, 35 findings fixed). This file records the decisions; the spec deltas record the requirements.

## Decision 1: Cloudflare Workers + D1 (REVISED 2026-06-11; originally Supabase)

The load-bearing requirement is that the maintainer's AI tooling can query the data over SQL so analysis ends in issues/PRs without manual export. Plausible's aggregate event model cannot do per-instance longitudinal queries and its Stats API is not SQL.

Originally chosen: Supabase free tier (existing MCP connection). Abandoned during provisioning: the free tier's 2-active-project limit was full, and upgrading to Pro priced out at ~US$45/month all-in (Pro base + per-project compute for the existing projects) — absurd for a few thousand rows. Cloudflare Workers + D1 replaces it: one free platform hosts both the ingest logic and the database, the free tier (100k requests/day, 5 GB) is orders of magnitude beyond this workload, D1 never pauses on idle (deleting the DB keep-alive machinery from the design entirely), and querying happens via wrangler/the D1 HTTP API with a scoped token.

## Decision 2: Opt-in, default off, one-time prompt

Audience is self-hosted Jellyfin users; opt-out telemetry is a reputation-ending move in that community (Audacity 2021). A one-time honest banner on the plugin dashboard converts 5-15% vs ~1% for a buried checkbox, and n decides whether any of this is useful. The banner ships as its own release AFTER the pipe is proven end-to-end, so the ask is never broken at first impression.

## Decision 3: Payload is bucketed, never raw

Counts are reported in buckets (accounts 1 / 2-4 / 5+; library <500 / 500-2k / 2k-10k / 10k+; syncs-per-week 0 / 1-10 / 11-100 / 100+) so no instance is fingerprintable by an exact number. Feature flags are booleans of existing config toggles. No IPs in the dataset, no usernames, no film titles, no library content. The README states precisely that Supabase platform request logs retain caller IPs for the platform's own retention window — the promise is "no IPs in the dataset", not a claim we can't keep.

## Decision 4: Identity is a regenerable random UUID

Generated when telemetry is enabled, never derived from hardware or Jellyfin identifiers. Regeneration unlinks the identifier going forward; old rows remain, and at small fleet sizes configuration similarity may still allow correlation — documented honestly rather than over-promised.

## Decision 5: Weekly ping + error-transition ping (revised premise)

Weekly-only pings cannot detect fleet-wide breakage (Letterboxd endpoint changes are step functions) faster than GitHub issues. So: weekly ping for adoption data, plus a ping when any error category transitions clean → failing, rate-limited to one per instance per day. The transition ping carries the full five-category error-state map; if a further category trips while the cap is spent, the plugin queues and sends one consolidated ping when the window reopens — deferred, never lost. Canary rates are computed from transition pings only; weekly error counters serve ad-hoc analysis.

## Decision 6: Counters and error state persist in PluginConfiguration

Self-hosters restart containers constantly. Window counters (measured since the last successful weekly ping), the last-successful-ping timestamp, and the per-category error-state booleans all persist in PluginConfiguration and flush on successful ping. In-memory-only state would systematically undercount the exact fleet being measured.

## Decision 7: Week handling and idempotency

`week` is computed in the edge function from UTC arrival time (a Postgres generated column over `timestamptz` is non-immutable and fails). Weekly pings upsert on `(instance_id, week)`; the client gates sending until the server week has rolled over since its persisted last-ping timestamp, so ±6 h jitter can never produce two different payloads in the same week slot; `ON CONFLICT` merges counters as a backstop.

## Decision 8: Key and abuse model (REVISED for D1)

The plugin ships a publishable static ingest key checked by the Worker (stops drive-by scanner POSTs). The D1 database has no client-facing access path at all — only the Worker and scoped API tokens can touch it, which is what RLS provided in the Supabase design. The key is extractable by design — junk rows are the worst case, bounded by shape validation, a 2 KB payload cap, per-instance caps, a transient (never persisted) per-IP rate limit plus a global requests-per-minute cap, and a row-growth alarm in the daily canary query.

## Decision 9: Canary statistics under an every-merge release cadence

Single versions never accumulate useful cohorts when every merge ships. So: foreground = all versions released in the trailing 14 days, pooled; baseline = everything older, pooled; per-version detail appears only in the issue's evidence table. Cohort assignment uses the most recent ping of any type (transition pings carry `plugin_version` and move an instance immediately — otherwise break-on-upgrade inflates the baseline and hides itself). "Active" = weekly ping in the trailing 14 days; transition window = trailing 7 days. Sybil gate: only instances with ≥2 weeks of weekly-ping history count. Issue gate: both cohorts n ≥ 10 AND rate multiple ≥ 3x; silent below the gate. Dedup on (suspect version, error_category); versions referenced by an open canary issue are excluded from the baseline so an unfixed regression can't mask itself.

## Decision 10: Deterministic canary, AI analysis ad hoc

The canary workflow is plain SQL + thresholds — no AI in the loop, so its issues are reproducible and arguable. Deep analysis (feature adoption, scale, roadmap questions) happens in normal AI sessions over the Supabase MCP, and findings become issues/PRs through human review.

## Decision 11: No DB keep-alive needed (REVISED for D1)

D1 never pauses on idle, so the dedicated keep-alive workflow was deleted. The two parts of it that still mattered moved into the canary workflow: the GitHub 60-day cron auto-disable heartbeat (commits to the non-main `telemetry-heartbeat` branch when repo activity is older than 50 days) and the loud-failure contract (any canary run that cannot reach the database fails red).

## Deferred

- Public aggregate stats page (Home Assistant style) — revisit after 90 days of data.
- User-initiated server-side erasure — out of scope; README documents that regeneration unlinks but does not erase.
- AI-driven canary analysis — deterministic checks first.
