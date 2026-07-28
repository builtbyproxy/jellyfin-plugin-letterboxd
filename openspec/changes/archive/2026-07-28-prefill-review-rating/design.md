# Design: prefill-review-rating

## Context

The review modal (dashboard `configPage.html` and self-service `userPage.html`, duplicated embedded resources) opens via `openReview(svc, tmdbId, title, slug, season, episode)` and hard-resets the star widget to "No rating". Jellyfin already stores per-user ratings in `UserItemData.Rating` (1–10 scale), which the plugin both reads (diary sync, Serializd sync) and writes (`WriteJellyfinRating` after a modal post, `DiaryImportTask` on import). The modal is the only rating surface that ignores it. `Helpers.MapRating` already converts 1–10 to Letterboxd half-stars (0.5–5.0) with tests. Review posts run as the current Jellyfin user (`GetCurrentUserId()`), so there is exactly one user whose rating is relevant.

## Goals / Non-Goals

**Goals:**
- Modal opens showing the current user's existing Jellyfin rating when one exists.
- Star widget supports half-star selection and display (0.5–5.0), removing the whole-star limitation for both pre-fill display and manual entry.
- Read path and write path resolve library items with the same logic.

**Non-Goals:**
- Updating already-posted Letterboxd diary entries when a rating arrives later (own change; needs `UserDataSaved` subscription and new `ILetterboxdService` surface).
- Pre-filling review text, rewatch state, or favorite/like.
- Any change to sync/import behavior.

## Decisions

**1. One read-only endpoint on `LetterboxdController`, serving both movie and TV paths.**
`GET Jellyfin.Plugin.LetterboxdSync/ItemRating?tmdbId=&seasonNumber=&episodeNumber=` → `{ "rating": 1–10|null, "stars": 0.5–5.0|null }`. `stars` is `Helpers.MapRating(rating)`. The controller already has `_userDataManager`, `_libraryManager`, and the current-user resolution used by the review POST; adding a sibling endpoint keeps auth and user semantics identical. *Alternative considered*: embedding the rating in the activity-list payload the modal is opened from — rejected because activity entries are rendered long before the modal opens (stale on rate-in-between) and slug-only Letterboxd entries have no rating source anyway.

**2. Resolution rules mirror the existing writers.**
Movies: same TMDb provider-ID query as `WriteJellyfinRating`, extracted into a shared private helper so read and write cannot drift. TV: series located by TMDb provider ID (as `SerializdSyncRunner` does); with `seasonNumber`+`episodeNumber` present, the episode's own `UserData.Rating` is returned, otherwise the series-level rating — matching how Serializd sync sources episode vs show ratings today. `tmdbId <= 0` returns `rating: null` without touching the library (slug-only history entries).

**3. Half-star widget via left/right click zones on each star, rendered with a clipped overlay.**
Clicking the left half of star *n* selects *n − 0.5*, the right half selects *n*; rendering uses a CSS-clipped filled-star overlay for the half state (no images, no external assets — pages are self-contained embedded resources). *Alternative considered*: doubling to ten thin stars — rejected as visually noisy and a bigger diff to two duplicated pages. Pre-filled values land on half-star boundaries by construction (`MapRating` rounds to nearest 0.5).

**4. Pre-fill is async and fail-soft.**
`openReview` still resets synchronously and opens the modal immediately; the fetch then paints stars only on a successful response with a non-null `stars`. Any error (endpoint missing, item not in library, no rating) leaves "No rating" — identical to today's behavior, so the modal never blocks or breaks on the lookup.

**5. Serializd payload keeps its existing `rating * 2` conversion.**
With half stars, Serializd reviews can now express the full 1–10 range (3.5 stars → 7). No scale changes on either service payload; Letterboxd already accepts half values.

## Risks / Trade-offs

- [Duplicated JS in `configPage.html` and `userPage.html` can drift] → Keep the widget + pre-fill block byte-identical between the two pages; task list calls out both files explicitly, as every prior modal change has.
- [Half-star click zones depend on pointer `offsetX`; coarse on touch] → Zones are half a star wide (~12px+), same precision Letterboxd's own mobile widget requires; worst case a tap selects the adjacent half step, still correctable by tapping again.
- [Endpoint returns data for the current user only; admin dashboard shows *other* users' activity] → Accepted: pre-fill reflects the person posting the review, which is also whose rating the writeback would overwrite — consistent semantics with the existing POST.
- [Series-level rating shown when reviewing a show could surprise users who only rated episodes] → Matches Serializd sync semantics already shipped; documented in spec scenarios.

## Migration Plan

Purely additive endpoint + frontend change; no config or data migration. Rollback is a normal release revert. Old clients (cached pages) simply never call the new endpoint.

## Open Questions

None blocking. (Whether Infuse writes numeric ratings into Jellyfin is a client-side question tracked with the user report; it does not change this design, which works for any client that populates `UserItemData.Rating`.)
