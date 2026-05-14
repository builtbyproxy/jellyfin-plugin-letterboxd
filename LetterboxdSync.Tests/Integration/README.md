# Integration tests

These talk to a real Letterboxd account over the network and are **skipped by
default**. They exist so a tagged-by-hand `dotnet test` run can verify the
plugin's auth, lookup, and read paths still work end-to-end against
production Letterboxd HTML/API surfaces that the unit suite mocks out.

## Why they're skipped by default

- Network-dependent and slow (Cloudflare warm-up, real HTTP round-trips).
- Require a Letterboxd account credential, which can't live in the public repo.
- Sometimes throttled by Cloudflare; we don't want CI to redden over that.

The unit suite (currently ~470 tests) gives the routine feedback loop; these
are for pre-release sanity checks and reproducing live bugs.

## Setup

1. Create or pick a Letterboxd account dedicated to testing (do not use a real
   personal account — these tests are read-only today, but the credential will
   end up in shell history / env state on whatever machine you run them on).
2. Export the credentials in the shell that will run `dotnet test`:

   ```bash
   export LETTERBOXD_TEST_USERNAME="your-test-account"
   export LETTERBOXD_TEST_PASSWORD="your-test-password"

   # Optional — supply if Cloudflare 403s on raw login
   export LETTERBOXD_TEST_RAW_COOKIES="cf_clearance=...; letterboxd.session=..."
   export LETTERBOXD_TEST_USER_AGENT="Mozilla/5.0 ..."
   ```

3. Run the integration suite:

   ```bash
   dotnet test -c Release --filter Category=Integration
   ```

   Run everything except integration tests:

   ```bash
   dotnet test -c Release --filter "Category!=Integration"
   ```

## Security posture

- Credentials are read from environment variables only. There is **no** code
  path in this repo that loads them from a file checked into git.
- `.env`, `.env.local`, and `*.env.local` are gitignored at the repo root in
  case you want to source a local file (e.g. `set -a && source .env && set +a`).
- Do not paste credentials into commit messages, PR descriptions, issues, or
  any markdown that lands in the repo or its history.

## Write tests

Write coverage uses an internal cleanup helper (`LetterboxdApiClient.
DeleteAllLogEntriesForFilmAsync`, exposed via `InternalsVisibleTo`) that
removes the diary entries the test created via `DELETE /log-entry/{id}`.
Tests are wrapped in `try/finally` so cleanup runs even on assertion failure.
The test account stays predictable across runs.

Note: cleanup is API-only. The scraping fallback path in
`ScrapingLetterboxdService` does not implement delete; write tests will skip
themselves if the API auth fails for the test account.

## CI

A manual-only GitHub Actions workflow lives at
`.github/workflows/integration.yml`. It runs on `workflow_dispatch` (Actions
tab → "Integration tests (live Letterboxd)" → Run workflow), pulling
`LETTERBOXD_TEST_USERNAME`/`PASSWORD` from repository secrets. The default
`ci.yml` workflow filters integration tests out so push/PR runs stay green
without secrets.

To wire up:

1. Repo Settings → Secrets and variables → Actions → New repository secret
2. Add `LETTERBOXD_TEST_USERNAME` and `LETTERBOXD_TEST_PASSWORD` (and
   optionally `LETTERBOXD_TEST_RAW_COOKIES` / `LETTERBOXD_TEST_USER_AGENT`)
3. Trigger via the Actions tab when you want a live run
