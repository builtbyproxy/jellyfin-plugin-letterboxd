# auth-circuit-breaker

## ADDED Requirements

### Requirement: Breaker opens after three consecutive login failures
The plugin SHALL count consecutive Letterboxd authentication failures per account (keyed by Jellyfin user id + Letterboxd username) across all sync entry points, and SHALL open the account's breaker when the count reaches 3. Only failures of the authentication step itself count; errors occurring after a successful login MUST NOT affect the breaker. A successful login SHALL reset the count to zero.

#### Scenario: Third consecutive failure opens the breaker
- **WHEN** an account's login fails for the third consecutive time, regardless of which entry point attempted it
- **THEN** the breaker for that account is open, and the opened-at timestamp records the first failure's date

#### Scenario: Success resets the count
- **WHEN** an account has 2 consecutive failures and the next login succeeds
- **THEN** the failure count is 0 and the breaker remains closed

#### Scenario: Post-auth errors are not counted
- **WHEN** login succeeds but the subsequent diary fetch fails
- **THEN** the failure count is unchanged

### Requirement: Open breaker short-circuits every entry point without network traffic
While an account's breaker is open, scheduled sync, watchlist sync, diary import, and real-time playback sync SHALL skip that account without attempting authentication or any Letterboxd request. Other accounts MUST be unaffected.

#### Scenario: Scheduled sync skips a paused account
- **WHEN** the scheduled sync runs for a user whose account breaker is open
- **THEN** no login attempt is made for that account, a skip is logged, and any other enabled accounts sync normally

#### Scenario: Playback completion with an open breaker
- **WHEN** a user with a breaker-open account finishes watching a film
- **THEN** the real-time sync short-circuits without a login attempt, and the watch is picked up by the scheduled task after the breaker closes (existing dedupe/catch-up behavior)

### Requirement: Opening the breaker notifies the admin once
The transition from closed to open SHALL write one Jellyfin activity-log entry naming the Letterboxd account and stating that syncing is paused until credentials are updated. Skipped runs while the breaker is open MUST NOT create additional activity-log entries.

#### Scenario: Activity entry on open
- **WHEN** the breaker opens for account "kostadamus"
- **THEN** exactly one activity-log entry is created, its text names the account and the failing-since date

#### Scenario: No repeat notifications
- **WHEN** ten further scheduled runs skip the paused account
- **THEN** no additional activity-log entries are created

### Requirement: Re-saving credentials closes the breaker
Persisting credentials for an account through either account endpoint SHALL reset that account's breaker (count zero, closed), so the next run attempts login normally. Breaker state SHALL survive plugin and server restarts until reset.

#### Scenario: Credential re-save resumes syncing
- **WHEN** the user saves the account with a new password while the breaker is open
- **THEN** the breaker is closed and the next scheduled run attempts authentication

#### Scenario: State survives restart
- **WHEN** the breaker is open and Jellyfin restarts
- **THEN** the breaker is still open afterwards

### Requirement: Dashboard shows paused state
The accounts listing returned by the plugin API SHALL include whether each account's breaker is open and since when, and both dashboard pages SHALL render a paused indicator for breaker-open accounts telling the user to re-save credentials.

#### Scenario: Paused badge
- **WHEN** the accounts page loads while an account's breaker is open
- **THEN** that account row shows a paused indicator with the failing-since date
