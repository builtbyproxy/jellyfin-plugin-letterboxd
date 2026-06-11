## ADDED Requirements

### Requirement: Daily deterministic regression check

A scheduled workflow SHALL run daily (plus manual dispatch) executing deterministic SQL checks — no AI in the loop — comparing error-transition rates between a foreground cohort and a baseline cohort. Daily cadence is required: the check exists to beat human bug reports to fleet-wide breakage.

#### Scenario: Canary run on a healthy fleet

- **WHEN** the daily run finds no cohort pair exceeding the gates
- **THEN** the workflow succeeds silently — no issue, no noise

### Requirement: Cohort definitions resilient to every-merge releases

Because every merge ships a release, single versions never accumulate useful cohorts. The foreground SHALL be all versions released in the trailing 14 days, pooled; the baseline SHALL be all older versions, pooled, excluding any version referenced by a still-open canary issue. An instance's cohort SHALL be assigned from its most recent ping of ANY type (transition pings carry plugin_version and move an instance immediately). "Active" = weekly ping in the trailing 14 days; the transition window is the trailing 7 days; a transition is attributed to the plugin_version stamped on the transition ping itself. Canary rates MUST be computed from transition pings only.

#### Scenario: Break-on-upgrade is attributed to the new version

- **WHEN** an instance upgrades on Monday and its syncs start failing the same day, before its next weekly ping
- **THEN** its error-transition ping (stamped with the new version) moves it into the foreground cohort immediately and the failure counts against the new release, not the baseline

#### Scenario: Unfixed regression cannot poison the baseline

- **WHEN** a version flagged by a still-open canary issue ages past 14 days
- **THEN** it is excluded from the baseline pool until the issue closes, so the live regression neither masks itself nor raises the bar for detecting the next one

### Requirement: Statistical and Sybil gates

No issue SHALL be filed unless both cohorts contain n ≥ 10 instances and the foreground rate is ≥ 3x the baseline rate (thresholds tunable in the workflow file). Only instances with at least 2 weeks of weekly-ping history SHALL count toward either cohort, so freshly-minted UUIDs cannot trip the canary.

#### Scenario: Attacker mints instances to fake a regression

- **WHEN** fresh UUIDs send weekly pings claiming the newest version followed by error-transition pings
- **THEN** none of them count toward any cohort until they have 2 weeks of history, and the canary stays silent

#### Scenario: Below the gate means silence

- **WHEN** the foreground cohort has 7 active instances
- **THEN** no issue is filed regardless of the rate multiple — silence below the gate is correct behaviour, not failure

### Requirement: Auto-filed issue contract

When the gates trip, the workflow SHALL file a templated GitHub issue containing the error category, the per-version evidence table (versions, n, rates), the comparison window, and the exact query used. Issues MUST be deduplicated on (suspect version from the evidence table, error_category) so a sliding foreground window cannot refile the same regression daily.

#### Scenario: Regression persists across days

- **WHEN** the same suspect version and error category exceed the gates on consecutive daily runs while the first issue is still open
- **THEN** no duplicate issue is filed
