# Tasks: stream-ratings-to-letterboxd

## 1. Endpoint research (ordered first; blocks the service surface)

- [ ] 1.1 Probe the official API's film-relationship rating update against a test account (shape, auth, rating scale); record findings in the change dir
- [ ] 1.2 Probe the site's rate action for the scraping path (URL, CSRF, payload, response); record findings
- [ ] 1.3 Decide primary/fallback split from findings (if the official API cannot set ratings, scraping becomes the only implementation and the API client throws NotSupported for the factory to fall through)

## 2. Service surface

- [ ] 2.1 `ILetterboxdService.SetFilmRatingAsync(filmSlug, filmId, rating)`; implement in `LetterboxdApiClient` and `ScrapingLetterboxdService` per 1.x findings
- [ ] 2.2 Unit tests with mocked HTTP for both implementations; integration probe test (Category=Integration) that rates and un-rates a known film

## 3. Rating sync handler

- [ ] 3.1 `RatingSyncHandler` (IHostedService): subscribe `IUserDataManager.UserDataSaved`; filter UpdateUserRating + movie + TMDb id + positive rating; per-account fan-out honoring `SyncRatings`; Debug diagnostic log for every observed rating change
- [ ] 3.2 Trailing-edge debounce (~10s) per (user, item), final value wins
- [ ] 3.3 `RatingSyncSuppression` handshake; wrap `WriteJellyfinRating`'s save in it; handler checks it
- [ ] 3.4 Breaker integration (fifth guarded call site: IsOpen pre-check, RecordFailure/RecordSuccess, one-time notify) + sync-history events with source "rating"
- [ ] 3.5 Handler tests: reason filtering (Import ignored), suppression, debounce-final-value, toggle gating, breaker skip, history record; config round-trip test proving `SyncRatings` defaults true for existing configs

## 4. Config + UI

- [ ] 4.1 `Account.SyncRatings` (default true) + toggle in both dashboard account editors ("Sync ratings to Letterboxd")
- [ ] 4.2 Controller/account-payload tests for the new field

## 5. Verification + release

- [ ] 5.1 Live verification: set a rating via the Jellyfin API on the test server (fires the same event path as any client) and confirm it lands on Letterboxd; then, if available, repeat from Infuse to answer the client-side question
- [ ] 5.2 Version bump (minor) in `Directory.Build.props` + `LetterboxdSync/LetterboxdSync.csproj`; `feat:` PR with `## Release notes` and `site/src/data/release-notes.ts` entry referencing the user report
