# Tasks: prefill-review-rating

## 1. Backend endpoint

- [x] 1.1 Extract the TMDb-ID → library-movie lookup from `WriteJellyfinRating` in `Api/LetterboxdController.cs` into a shared private helper used by both the writeback and the new read path
- [x] 1.2 Add TV resolution helpers: series by TMDb provider ID, and episode by series + season/episode index, mirroring `SerializdSyncRunner`'s sourcing of episode vs show ratings
- [x] 1.3 Add `GET Jellyfin.Plugin.LetterboxdSync/ItemRating` (authenticated, current user via `GetCurrentUserId()`) returning `{ rating, stars }` with `stars = Helpers.MapRating(rating)`; null-body success for missing/non-positive TMDb ID, unresolved items, or no stored rating
- [x] 1.4 Controller tests in `LetterboxdSync.Tests` (style of `RatedFilmsApiTests.cs`) covering: rated movie (7 → 3.5), unrated movie, unknown/missing TMDb ID, episode rating, show-level rating, episode-requested-but-unrated, and unauthenticated rejection

## 2. Half-star widget

- [x] 2.1 Rework the star widget in `Web/configPage.html` to half-star granularity: left/right half click zones per star, CSS-clipped half-fill rendering, label showing e.g. "3.5 / 5"
- [x] 2.2 Apply the identical widget block to `Web/userPage.html` (keep the two copies byte-identical)
- [x] 2.3 Verify posting paths with half values: Letterboxd payload carries 3.5 as-is; Serializd payload doubles to 7 via the existing `Math.round(rating * 2)`

## 3. Modal pre-fill

- [x] 3.1 In `openReview` (both pages): after the synchronous reset, fetch `ItemRating` with `tmdbId` (+ season/episode for the Serializd path), and on a non-null `stars` paint the widget and label; skip the fetch when `tmdbId` is 0/absent
- [x] 3.2 Fail-soft behavior: any fetch error or null response leaves the widget at "No rating" with the modal fully usable; no user-visible error
- [ ] 3.3 Manual verification against a local Jellyfin (deploy via `./deploy.sh`): pre-fill for a rated movie, an unrated movie, a rated episode, and a slug-only history entry; confirm posting the pre-filled value writes through and `WriteJellyfinRating` round-trips

## 4. Release plumbing

- [ ] 4.1 Bump `AssemblyVersion`/`FileVersion` (minor) in `Directory.Build.props` and `LetterboxdSync/LetterboxdSync.csproj`
- [x] 4.2 PR with Conventional Commits title (`feat:`), `## Release notes` paragraph (user-facing prose: review modal now shows your existing Jellyfin rating, and supports half stars), and matching `site/src/data/release-notes.ts` entry
