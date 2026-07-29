# rating-streaming

## ADDED Requirements

### Requirement: Rating changes stream to Letterboxd automatically
When a Jellyfin user's rating on a movie changes with save reason UpdateUserRating, the plugin SHALL push the mapped half-star value to Letterboxd as the member's film rating for every enabled account of that user whose rating-sync toggle is on, without any user interaction. The film SHALL be resolved by TMDb id; items without one are skipped with a log line.

#### Scenario: Rate after watching in a client app
- **WHEN** a user finishes a film (already synced to the diary) and later rates it 7/10 in a Jellyfin client
- **THEN** the user's Letterboxd film rating becomes 3.5 stars, and a sync-history entry with source "rating" records it

#### Scenario: Rating without a watch
- **WHEN** a user rates a library film they never played
- **THEN** the rating is still pushed (film rating only; no diary entry is created)

#### Scenario: Toggle off
- **WHEN** an account's rating-sync toggle is off and the user rates a film
- **THEN** nothing is pushed for that account, while other enabled accounts of the same user still receive the rating

### Requirement: The plugin's own rating writes never echo back out
Rating saves originating from the plugin itself SHALL NOT be streamed. All plugin-originated rating writes (diary import and the review-modal writeback) SHALL save with the Import reason, and the handler SHALL stream only UpdateUserRating saves.

#### Scenario: Diary import does not bounce
- **WHEN** diary import writes a Letterboxd rating into Jellyfin
- **THEN** no push back to Letterboxd occurs

#### Scenario: Review-modal writeback does not double-push
- **WHEN** a user posts a review with stars in Jellyscribe (which already carries the rating to Letterboxd and mirrors it into Jellyfin)
- **THEN** the mirror write does not trigger a second Letterboxd push

### Requirement: Rapid changes debounce to one push
Successive rating changes for the same (user, item) within the debounce window SHALL result in exactly one push carrying the final value.

#### Scenario: Tapping through star values
- **WHEN** a user changes a film's rating three times within a few seconds, ending on 9/10
- **THEN** exactly one Letterboxd push occurs, with 4.5 stars

### Requirement: Streaming respects account health and reports outcomes
Rating pushes SHALL skip breaker-open accounts without a login attempt, SHALL record auth failures through the breaker exactly like other entry points, and SHALL record successes and failures in sync history with source "rating".

#### Scenario: Breaker open
- **WHEN** an account's auth breaker is open and the user rates a film
- **THEN** no login attempt is made and the skip is logged

#### Scenario: Auth failure counts toward the breaker
- **WHEN** a rating push fails at authentication for the third consecutive time across entry points
- **THEN** the breaker opens and the admin is notified once, per the auth-circuit-breaker capability

### Requirement: Rating events are diagnosable from logs
Every movie rating change observed by the plugin SHALL be logged with item, user, and value, so support can distinguish "the client never wrote the rating to Jellyfin" from "the push to Letterboxd failed" using the existing log viewer.

#### Scenario: Client that does not persist ratings
- **WHEN** a user rates films in a client that never writes ratings to the Jellyfin server
- **THEN** the plugin logs contain no rating-change events for those actions, proving the gap is client-side
