# Design: auth-circuit-breaker

## Context

All four Letterboxd entry points (scheduled `SyncTask` via `LetterboxdSyncRunner`, `WatchlistSyncRunner`, `DiaryImportTask`, real-time `PlaybackHandler`) authenticate per run through `LetterboxdServiceFactory.CreateAuthenticatedAsync`, which tries the JSON API and falls back to scraping; any throw from it is an authentication failure (transport-level errors inside a sync surface later, after auth succeeded). There is no cross-run memory of auth failures. Per-film failure caps (`MaxConsecutiveSyncFailures`) and Cloudflare backoff already exist and are untouched. `SyncHistory` establishes the house pattern for plugin state: a static class persisting to the plugin configurations directory with an internal `DataPathOverride` test seam.

## Goals / Non-Goals

**Goals:**
- No login attempts for an account after 3 consecutive failures, across every entry point, until reset.
- One admin-visible Jellyfin activity-log entry per open transition.
- Reset on credential re-save; automatic reset on any successful login.
- Breaker state survives restarts; skipped runs cost nothing on the network.

**Non-Goals:**
- Serializd accounts, configurable thresholds, notification channels beyond the activity log (see proposal).

## Decisions

**1. `AuthBreaker` static class with JSON persistence, keyed by (Jellyfin user id, Letterboxd username).**
Follows `SyncHistory` exactly: static API, lock-guarded load/save, file in the plugin configurations directory (`letterboxd-auth-breaker.json`), `DataPathOverride` + `ResetForTesting` seams. State per account: consecutive failure count, opened-at timestamp (null when closed), last failure message. Not stored in `PluginConfiguration` because the config JSON round-trips through both dashboards on save, which would let a stale page clobber breaker state, and config XML is the wrong place for operational counters.

**2. Callers guard and record; the factory stays dumb.**
`CreateAuthenticatedAsync` is a static factory with a test override; teaching it about accounts and activity logs would couple every consumer to breaker semantics. Instead each of the four call sites: (a) checks `AuthBreaker.IsOpen(userId, lbUsername)` before authenticating and skips the account (logged at Information, recorded in sync history as a skip with a "sync paused, login failing since <date>" reason where a history record already exists for that path); (b) on catch around authentication calls `AuthBreaker.RecordFailure(...)`; (c) after successful authentication calls `AuthBreaker.RecordSuccess(...)`. *Alternative considered*: wrapping the factory — rejected because the factory has no Jellyfin-user identity and its test override would then bypass the breaker in every existing test.

**3. Failure classification: only throws from `CreateAuthenticatedAsync` count.**
The factory throwing means both API and scraping auth failed. Errors after a service is returned (diary scrape 403s, timeouts) never touch the breaker, so a Cloudflare hiccup mid-sync cannot open it. This is deliberately coarse: three consecutive full auth failures across runs is strong evidence of stale credentials, per the issue.

**4. Notification via `IActivityManager` at the open transition only.**
`RecordFailure` returns whether this call opened the breaker; the caller then writes one `ActivityLog` entry ("Jellyscribe: Letterboxd login for <username> has been failing since <date>; syncing is paused until credentials are updated", Warning severity) through an `IActivityManager` injected into the four callers' constructors. Skipped runs while open log locally but never notify. *Alternative considered*: a static activity sink wired at startup — rejected as hidden global coupling when constructor DI is already how these classes get Jellyfin services.

**5. Reset semantics.**
`PUT Account` and `PUT Accounts` call `AuthBreaker.Reset(userId, lbUsername)` whenever they persist a password or cookie value for that account (the self-service page always sends credentials on save; the admin page's bulk PUT includes them when changed). Reset zeroes the count and clears opened-at, so the next scheduled run attempts login normally. A successful login via `RecordSuccess` does the same, covering server-side recoveries (e.g. Letterboxd outage) without operator action. No separate "retry now" button: re-saving the account is the documented retry path and the dashboard badge says so.

**6. Dashboard surfacing: badge from `GET Accounts`.**
`GET Accounts` (already consumed by both pages) gains `authPaused` and `authPausedSince` fields per account; both pages render a "paused, login failing" badge next to a breaker-open account. No new endpoint.

## Risks / Trade-offs

- [Threshold 3 opens on a sustained Letterboxd outage] → `RecordSuccess` auto-closes on the next successful login after the outage; the activity entry is then stale but truthful about what happened. Accepted per issue ("reduce auth-driven rate limiting" is the priority).
- [Static state + static seams accumulate (same debt class as `SeriesTmdbIdReader`)] → Accepted knowingly: consistency with `SyncHistory` beats introducing a second state-management idiom in this codebase; the maintainability backlog already tracks DI-ification of static seams.
- [Two accounts with the same Letterboxd username under different Jellyfin users are tracked independently] → Correct behavior: credentials are stored per account entry.
- [PlaybackHandler short-circuit means a watch during breaker-open is not synced later] → Existing catch-up behavior already covers this: the scheduled task picks up unsynced watches once the breaker closes, per sync history dedupe.

## Migration Plan

Purely additive. First run creates an empty state file; absence of the file means all breakers closed. Rollback: delete the plugin's newer DLL; the state file is ignored by older versions.

## Open Questions

None.
