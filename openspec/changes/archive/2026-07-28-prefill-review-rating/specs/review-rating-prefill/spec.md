# review-rating-prefill

## ADDED Requirements

### Requirement: Item rating endpoint returns the current user's stored rating
The plugin API SHALL expose an authenticated GET endpoint that returns the calling Jellyfin user's stored rating for an item, as both the raw Jellyfin value (`rating`, 1–10) and the Letterboxd half-star mapping (`stars`, 0.5–5.0 via the existing rating mapping). The endpoint SHALL resolve the rating for the authenticated user only, never for another user.

#### Scenario: Movie with a stored rating
- **WHEN** the endpoint is called with the TMDb ID of a movie in the library for which the current user has a Jellyfin rating of 7
- **THEN** the response contains `rating: 7` and `stars: 3.5`

#### Scenario: Movie without a stored rating
- **WHEN** the endpoint is called with the TMDb ID of a library movie the current user has never rated
- **THEN** the response contains `rating: null` and `stars: null`

#### Scenario: Item not in the library or no TMDb ID supplied
- **WHEN** the endpoint is called with a TMDb ID that matches no library item, or with a missing/non-positive TMDb ID
- **THEN** the response contains `rating: null` and `stars: null` and does not error

#### Scenario: Unauthenticated request
- **WHEN** the endpoint is called without a valid Jellyfin session
- **THEN** the request is rejected with an authentication error and no rating data is returned

### Requirement: TV resolution distinguishes episode and show ratings
For TV items the endpoint SHALL locate the series by TMDb provider ID. When season and episode numbers are supplied it SHALL return the rating stored on that episode; when they are absent it SHALL return the series-level rating. This mirrors how Serializd sync sources episode versus show ratings.

#### Scenario: Episode rating requested
- **WHEN** the endpoint is called with a series TMDb ID plus season and episode numbers, and the current user rated that episode 8
- **THEN** the response contains `rating: 8` and `stars: 4`

#### Scenario: Show-level rating requested
- **WHEN** the endpoint is called with only a series TMDb ID and the current user rated the series 9
- **THEN** the response contains `rating: 9` and `stars: 4.5`

#### Scenario: Episode requested but only the series is rated
- **WHEN** the endpoint is called with season and episode numbers for an episode the user has not rated, even though the series has a rating
- **THEN** the response contains `rating: null` and `stars: null`

### Requirement: Review modal pre-fills from the stored rating
When the review modal opens, both the dashboard page and the user page SHALL fetch the item rating endpoint and, on a successful response with a non-null `stars` value, display that value in the star widget and rating label as the starting state. The user MUST be able to change or clear the pre-filled value before posting.

#### Scenario: Modal opens for a rated film
- **WHEN** a user opens the review modal for a film they rated 7/10 in Jellyfin
- **THEN** the star widget shows 3.5 filled stars and the label reads "3.5 / 5" before any user input

#### Scenario: Modal opens for an unrated film
- **WHEN** a user opens the review modal for a film with no stored rating
- **THEN** the widget shows "No rating", identical to current behavior

#### Scenario: Rating lookup fails
- **WHEN** the rating fetch errors or times out after the modal has opened
- **THEN** the modal remains fully usable with the widget in the "No rating" state, and no error blocks posting

#### Scenario: Pre-filled rating is posted unchanged
- **WHEN** the user posts a review without touching the pre-filled stars
- **THEN** the posted review carries the pre-filled rating value

### Requirement: Star widget supports half-star selection
The review modal star widget SHALL support values in 0.5 increments from 0.5 to 5.0, both when displaying a pre-filled rating and when the user selects a rating manually. Selecting the left half of a star SHALL choose that star's value minus 0.5; the right half SHALL choose the whole value. The widget SHALL be identical in both embedded pages.

#### Scenario: Half-star manual selection
- **WHEN** the user clicks the left half of the fourth star
- **THEN** the widget shows 3.5 stars and the label reads "3.5 / 5"

#### Scenario: Half-star value sent to Letterboxd
- **WHEN** the user posts a Letterboxd review with 3.5 stars
- **THEN** the review payload carries the rating 3.5

#### Scenario: Half-star value sent to Serializd
- **WHEN** the user posts a Serializd review with 3.5 stars
- **THEN** the review payload carries the rating 7 on Serializd's 1–10 scale
