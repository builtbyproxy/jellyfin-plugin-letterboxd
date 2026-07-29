# Design: stream-ratings-to-letterboxd

## Context

Ratings flow into Jellyfin's `UserItemData.Rating` (1–10) from any client via the user-data API. The plugin already reads that field in four places and writes it in two (`DiaryImportTask` with `UserDataSaveReason.Import`, the review-modal writeback with `UserDataSaveReason.UpdateUserRating`). The 10.11 SDK exposes `IUserDataManager.UserDataSaved` (verified present, with `UserDataSaveEventArgs` carrying user, item, save reason) — the same eventing pattern `PlaybackHandler` uses for `ISessionManager.PlaybackStopped`. `Helpers.MapRating` maps 1–10 to Letterboxd half-stars. The auth circuit breaker (PR #105) gates all authentication. On Letterboxd, a member's **film rating** is a film-relationship property independent of diary entries; diary entries snapshot a rating at logging time.

## Goals / Non-Goals

**Goals:**
- A rating set in any Jellyfin client after (or before, or without) a watch reaches Letterboxd within seconds, without user interaction.
- The plugin's own rating writes never echo back out; a stars-fiddling user produces one push, not five.
- Failures are visible (sync history + logs) and respect the auth breaker.

**Non-Goals:**
- Diary-entry retro-editing, Serializd, rating removal, favorites (see proposal).

## Decisions

**1. A dedicated `RatingSyncHandler` hosted service, not an extension of `PlaybackHandler`.**
Subscribes to `IUserDataManager.UserDataSaved`; filters to `SaveReason == UpdateUserRating`, movie items with a TMDb id, a non-null positive rating, and users with at least one enabled account whose `SyncRatings` toggle is on. `PlaybackHandler` stays a playback-event handler; mixing two unrelated event sources in one class buys nothing. Registered in `ServiceRegistrator` beside it.

**2. Push the member film rating, not a diary edit.**
New `ILetterboxdService.SetFilmRatingAsync(filmSlug, filmId, rating)`:
- `LetterboxdApiClient`: the official API models this as a film-relationship update (rating on the member↔film relationship). Exact endpoint shape is a **research task** with a live probe before implementation, same as the Serializd endpoint work.
- `ScrapingLetterboxdService`: the site's own rate widget posts a CSRF-protected rate action; probe and mirror it in `LetterboxdDiary`/`LetterboxdScraper` composition.
Both implementations must be verified against a real account before the handler lands (the factory's silent fallback means either path can serve any user).

**3. Echo prevention: one rule — only `UpdateUserRating` saves stream.**
Both plugin-originated rating writes save with `UserDataSaveReason.Import`:
- `DiaryImportTask` already does.
- The review-modal writeback (`WriteJellyfinRating`) currently saves with `UpdateUserRating`; this change switches it to `Import`, which is semantically accurate (in both cases the value originates from the Letterboxd side — the review post already carried it there). One pattern for every current and future plugin-originated rating write; a suppression handshake was considered and rejected as a second mechanism for the same concern.
- No cross-system loop is possible: every Letterboxd→Jellyfin write path now uses `Import`, and the handler streams only `UpdateUserRating`. Regression guard: a test pins the writeback's save reason so a future edit can't silently reopen the loop.

**4. Debounce: trailing-edge, per (user, item), 10 seconds exactly.**
Clients like Infuse let users tap through star values; each tap fires a save. The handler keeps an in-memory `ConcurrentDictionary<(Guid,Guid), PendingPush>`; each event overwrites the pending value and restarts that key's `System.Threading.Timer` at 10s; the timer callback pushes the value present at fire time and removes the key. Only the final value is ever sent. No persistence: a server restart mid-window loses at most one rating tap, and the next change re-triggers. Same in-memory-static trade-off as `SyncGate`, accepted knowingly. 10s balances Letterboxd request economy against perceived staleness; it is a named constant, not config.

**5. Gating order: toggle → breaker → auth.**
Skip silently when `SyncRatings` is off. Skip with an Information log when the breaker is open (no history spam per rating tap). On auth failure, `AuthBreaker.RecordFailure` + notify exactly as the other four entry points do — this becomes the fifth guarded call site, same pattern. On success, record a `SyncEvent` (`Source = "rating"`) so the dashboard activity shows "Rated <film> 3.5 stars on Letterboxd".

**6. Diagnostics answer the Infuse question.**
At Debug level, log every `UserDataSaved` with `UpdateUserRating` on a movie (client-agnostic: item, user, value). If a user reports "Infuse ratings don't sync" and their logs show no such events while rating in Infuse, the client isn't writing to Jellyfin — the plugin can then say so definitively. The in-dashboard log viewer already surfaces plugin-tagged lines.

## Risks / Trade-offs

- [Infuse may not write ratings to Jellyfin at all] → Out of plugin control; diagnostics (decision 6) make it provable either way, and the feature still works for the web UI/API/other clients. Verify during live testing by setting a rating via the Jellyfin API (fires the same event) before involving Infuse.
- [Unknown official-API endpoint shape for rating] → Research task ordered first; if the official API can't set ratings, the scraping path becomes primary for this operation (factory consumers won't notice — the interface hides which one ran).
- [Per-tap logins under scraping could look bot-like] → Debounce collapses taps; the breaker caps sustained failures; ratings are rare events compared to sync runs.
- [Event volume: UserDataSaved fires for playback progress constantly] → First filter is the save reason enum comparison, so the hot path is one branch; no I/O unless it's a real rating change.
- [Multi-account fan-out doubles pushes] → Same fan-out semantics as review posting (every enabled account gets the rating); per-account toggle allows opting a secondary account out.

## Migration Plan

Additive. New `SyncRatings` account field defaults to true (XML deserialization of old configs leaves it true via default initializer — verify with a config round-trip test). Rollback: previous DLL ignores the field.

## Open Questions

- Official API rating endpoint shape. HARD GATE: task group 1 must conclude before the service surface is written; if the official API cannot set a member film rating, the API client's implementation throws NotSupported and the factory's scraping fallback carries the feature (documented in the release notes as requiring a working scraping login).

## Alternatives considered

- **Poll-based rating sync inside SyncTask** (plan-review suggestion): scan rated items each scheduled run and push un-pushed ratings. Rejected: latency becomes the sync interval (hours) for a feature whose whole point is "rate on the couch, see it on Letterboxd", it needs new pushed-rating bookkeeping to know what is already on Letterboxd, and the event path's complexity collapsed once echo prevention unified on save reasons. Revisit as a catch-up complement if event delivery proves unreliable.
