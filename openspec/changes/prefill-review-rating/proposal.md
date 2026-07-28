# Proposal: prefill-review-rating

## Why

A user report (Kostadamus, Jellyfin + Infuse setup) surfaced that ratings already stored in Jellyfin never appear in the review modal: the star widget always opens at "No rating", so users re-enter ratings the server already knows, or post reviews without them. Rating flow is currently one-way (modal → Jellyfin via the writeback in `LetterboxdController`); the modal never reads back. The widget is also whole-stars only, which cannot represent Jellyfin's 1–10 scale (7/10 = 3.5 stars) even though Letterboxd itself supports half stars. No GitHub issue exists yet for this report.

## What Changes

- New authenticated GET endpoint on the plugin API that returns the **current Jellyfin user's** stored rating (`UserItemData.Rating`, 1–10) and favorite flag for an item, resolved by TMDb ID for movies and by TMDb ID + season/episode for the Serializd (TV) path.
- The review modal in both `Web/configPage.html` and `Web/userPage.html` fetches that endpoint on open and pre-fills the star widget and label when a rating exists; the widget still resets to "No rating" when none exists or the lookup fails.
- The star widget gains **half-star** granularity (0.5–5.0) so Jellyfin ratings map losslessly for display and Letterboxd posts can carry half stars, matching what Letterboxd accepts.
- The TMDb-to-library-item lookup currently embedded in `WriteJellyfinRating` is extracted into a shared helper so the read and write paths use the same resolution logic.

## Capabilities

### New Capabilities

- `review-rating-prefill`: Reading a Jellyfin user's stored rating for an item through the plugin API and presenting it as the starting state of the review modal, including half-star display and the movie/TV resolution rules.

### Modified Capabilities

<!-- none: sync behavior (letterboxd sync, serializd-sync) is unchanged; this only touches the review-modal surface and adds a read-only endpoint -->

## Impact

- `LetterboxdSync/Api/LetterboxdController.cs`: new GET endpoint; extract item-resolution helper out of `WriteJellyfinRating`.
- `LetterboxdSync/Helpers.cs`: reuse `MapRating` (1–10 → half stars); no mapping changes expected.
- `LetterboxdSync/Web/configPage.html` and `LetterboxdSync/Web/userPage.html`: `openReview` pre-fill fetch; star widget half-star rendering and click handling (code is duplicated across both embedded pages and must be changed in both).
- `LetterboxdSync.Tests`: new controller tests (style of `RatedFilmsApiTests.cs`) and widget-mapping cases.
- Review POST payloads may now contain half values (e.g. `3.5`); Letterboxd already accepts these, and the Serializd path already multiplies by 2 into its 1–10 scale.
- Ships a release: version bump in `Directory.Build.props` + `LetterboxdSync/LetterboxdSync.csproj`, `## Release notes` PR section, `site/src/data/release-notes.ts` entry.

## Non-goals

- **Late-arriving ratings**: updating an already-synced Letterboxd diary entry when the user rates the film afterwards (e.g. in Infuse post-credits). Needs an `IUserDataManager.UserDataSaved` subscription and new `ILetterboxdService` surface in both API and scraping implementations; explicitly deferred to its own change.
- No changes to the diary sync, playback handler, or import paths; they already read/write ratings.
- No verification or workaround for whether Infuse itself writes numeric ratings to Jellyfin; that is a client-side question outside the plugin.
