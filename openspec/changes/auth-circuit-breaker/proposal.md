# Proposal: auth-circuit-breaker

## Why

Issue #103: when a Letterboxd account's credentials go stale, every scheduled sync retries the login forever. The failure is only visible inside the plugin dashboard, so on a set-and-forget server an account can sit broken for weeks, and the repeated login attempts risk tripping Letterboxd rate limiting that then hurts accounts which still work. Per-film failure caps and Cloudflare backoff exist, but there is no account-level breaker for authentication, the failure mode most likely to be permanent until a human acts.

## What Changes

- **Account-level auth circuit breaker**: after 3 consecutive login failures for a Letterboxd account (counted across distinct attempts, any entry point), the plugin stops attempting to authenticate that account. While open, scheduled sync, watchlist sync, diary import, and real-time playback sync all short-circuit for that account as cheap no-ops with no network traffic.
- **Admin notification**: the transition to open (not every skipped run) writes a Jellyfin activity-log entry naming the account and start date, so it surfaces in the admin dashboard's activity feed where operators actually look.
- **Reset paths**: re-saving the account's credentials through either account endpoint closes the breaker immediately; a successful login also resets the failure count. The dashboard accounts list shows a paused badge for a breaker-open account.
- Breaker state persists across restarts in the plugin's configurations directory (same pattern as sync history), never in `PluginConfiguration`.

## Capabilities

### New Capabilities

- `auth-circuit-breaker`: Counting consecutive Letterboxd login failures per account, opening/closing the breaker, short-circuiting all sync entry points while open, the admin activity-log notification, and the reset semantics.

### Modified Capabilities

<!-- none: existing sync capabilities keep their behavior when auth works; the breaker only inserts a guard in the failure path -->

## Impact

- `LetterboxdSync/AuthBreaker.cs` (new): static state store, SyncHistory-style JSONL/JSON persistence with `DataPathOverride` test seam.
- `LetterboxdSync/LetterboxdSyncRunner.cs`, `WatchlistSyncRunner.cs`, `DiaryImportTask.cs`, `PlaybackHandler.cs`: pre-check + failure/success recording around `LetterboxdServiceFactory.CreateAuthenticatedAsync`; `IActivityManager` added to constructors for the open-transition notification.
- `LetterboxdSync/Api/LetterboxdController.cs`: breaker reset on `PUT Account` / `PUT Accounts` credential saves; breaker state exposed on `GET Accounts` for the paused badge.
- `LetterboxdSync/Web/configPage.html` + `userPage.html`: paused badge on breaker-open accounts.
- Tests: new breaker unit tests plus auth-failure-path tests for the runners.
- Ships a release: version bump, `## Release notes`, `release-notes.ts` entry.

## Non-goals

- Serializd accounts: their bearer-token auth has different failure semantics; a Serializd breaker is a follow-up if the problem shows up there.
- Configurable threshold: fixed at 3 consecutive failures; a config knob can come later if anyone asks.
- Email/webhook notification channels: the Jellyfin activity log is the notification surface.
- Distinguishing "wrong password" from transient Letterboxd outages beyond the consecutive-failure count itself.
