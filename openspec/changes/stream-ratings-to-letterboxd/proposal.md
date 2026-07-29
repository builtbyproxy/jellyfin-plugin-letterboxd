# Proposal: stream-ratings-to-letterboxd

## Why

A user (Jellyfin + Infuse + Jellyscribe) rates films in Infuse *after* watching them and expects those ratings to reach Letterboxd. Today they never do: the real-time sync fires at playback completion, before the rating exists, and dedupe correctly stops any later re-send, so a rating added after the credits has no path to Letterboxd. v2.2.0's review-modal pre-fill only helps users who post reviews through Jellyscribe; this user reasonably points out that if they have to open a UI to rate, they'd just use Letterboxd directly. This was the explicitly deferred non-goal of the `prefill-review-rating` change (user report thread, follow-up to v2.2.0).

## What Changes

- **Rating change streaming**: the plugin subscribes to Jellyfin's user-data-saved event and, when a user's rating on a movie changes (from any client: Infuse, the Jellyfin web UI, the API), pushes the mapped half-star rating to Letterboxd as the member's film rating, for every enabled account of that Jellyfin user.
- **New service surface**: `ILetterboxdService` gains a set-film-rating operation, implemented in both the official-API client and the scraping fallback.
- **Echo/loop prevention**: rating writes that the plugin itself performs (diary import, review-modal writeback) are recognized and never re-streamed; rapid successive changes (a user adjusting stars in a client) are debounced into one push.
- **Breaker + toggle**: streaming respects the account-level auth circuit breaker (issue #103) and gets a per-account "sync ratings to Letterboxd" toggle, default on.
- **Diagnostics**: rating-change events are logged (source save-reason, item, mapped value) so a "my client's ratings don't sync" report can be answered from logs, including the open question of whether Infuse writes ratings to Jellyfin at all.

## Capabilities

### New Capabilities

- `rating-streaming`: Detecting Jellyfin rating changes, mapping and pushing them to Letterboxd as film ratings, echo prevention, debounce, breaker/toggle gating, and diagnostic logging.

### Modified Capabilities

<!-- none: diary sync, diary import, watchlist, review posting keep their requirements; this adds a new independent flow -->

## Impact

- New `RatingSyncHandler` (IHostedService) subscribing to `IUserDataManager.UserDataSaved`.
- `ILetterboxdService`, `LetterboxdApiClient`, `ScrapingLetterboxdService` (+ its composed parts): set-film-rating operation. Endpoint shapes need live verification (research task; same probing approach as the Serializd work).
- `Configuration/Account.cs` + both dashboard account editors: `SyncRatings` toggle.
- `DiaryImportTask` already saves with the Import reason (ignored by the handler); the review-modal writeback saves with UpdateUserRating and needs an explicit suppression handshake.
- Tests: handler unit tests (event filtering, echo prevention, debounce, breaker), service-layer tests, integration probe.
- Ships a release: version bump, `## Release notes`, `release-notes.ts` entry.

## Non-goals

- Updating the rating stored on an already-posted **diary entry**. Letterboxd's member film rating is what the film page and profile show; retro-editing diary entries needs entry-id bookkeeping we don't have and can create duplicate-entry risk. The film rating satisfies the reported use case.
- TV ratings to Serializd (different service; follow-up if requested).
- Streaming likes/favorites changes (favorites already flow with diary sync).
- Rating removal propagation: clearing a rating in Jellyfin does not unrate on Letterboxd in this change (logged, not pushed), to keep the failure surface small; revisit if users ask.
- Making Infuse write ratings to Jellyfin: if the client never persists the rating server-side, nothing in this plugin can see it. The diagnostics exist to prove which side is failing.
