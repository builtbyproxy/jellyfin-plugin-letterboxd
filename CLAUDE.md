# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Jellyfin plugin that syncs watch history to Letterboxd. C#/.NET 9, targets Jellyfin 10.11 (`Jellyfin.Controller`/`Jellyfin.Model` 10.11.0). Letterboxd's official endpoint (`/api/v0/production-log-entries`) is preferred; the plugin falls back to web scraping (cookie login, CSRF tokens, HtmlAgilityPack) when the API path fails.

The sidebar link in the Jellyfin web UI depends on the third-party **File Transformation** plugin; the rest of the plugin works without it.

## Build & Test

```bash
dotnet build -c Release
dotnet test  -c Release --verbosity normal
```

Run a single test class or method (xUnit, `dotnet test` filter syntax):

```bash
dotnet test --filter "FullyQualifiedName~ScraperTests"
dotnet test --filter "FullyQualifiedName~ScraperTests.LookupBySlug_Returns_Result"
```

CI also collects coverage via `--collect:"XPlat Code Coverage"` into `TestResults/`; Codecov consumes the Cobertura XML.

Deploy a debug build to the local Jellyfin server: `./deploy.sh` (scp's `LetterboxdSync.dll` + `HtmlAgilityPack.dll` and restarts the container).

## Architecture

### Service abstraction with fallback

`ILetterboxdService` (`ILetterboxdService.cs`) is the seam every caller uses. Two implementations:

- `LetterboxdApiClient` — preferred, talks to Letterboxd's JSON endpoints.
- `ScrapingLetterboxdService` — fallback, composes `LetterboxdHttpClient` (cookies/CSRF/Cloudflare retry), `LetterboxdAuth` (login + re-auth on 401), `LetterboxdScraper` (HTML parsing, film lookup, diary/watchlist scraping), and `LetterboxdDiary` (diary writes, review posting).

`LetterboxdServiceFactory.CreateAuthenticatedAsync` tries the API first and silently falls back to scraping if auth fails. The factory also exposes an `internal static OverrideForTesting` hook used via `InternalsVisibleTo` from `LetterboxdSync.Tests` to inject mock services — production code never touches it.

### Sync entry points

- `SyncTask` — scheduled, exports recent watches to the Letterboxd diary.
- `WatchlistSyncTask` / `WatchlistSyncRunner` — imports the user's Letterboxd watchlist as a Jellyfin playlist.
- `DiaryImportTask` — marks Jellyfin items as played if present in the Letterboxd diary.
- `PlaybackHandler` — `IHostedService` registered in `ServiceRegistrator`, fires the real-time sync on playback completion.
- `LetterboxdSyncRunner` — shared engine used by `SyncTask` and `PlaybackHandler`; `SyncGate`, `SyncHistory`, `SyncProgress`, and `TmdbCache` coordinate dedupe, progress UI, and TMDb lookups.

### Plugin surface

- `Plugin.cs` + `ServiceRegistrator.cs` register services and config.
- `Api/LetterboxdController.cs` and `Api/SidebarController.cs` expose REST endpoints consumed by the config dashboard.
- `Web/*.html` and `Web/*.js` are embedded resources (see `LetterboxdSync.csproj`) served as the plugin's config pages.
- `SidebarInjection.cs` registers a transformation with the File Transformation plugin to inject the sidebar link.

## Releasing

`manifest.json` is **CI-managed**. `release.yml` is the only writer. Do not pre-add `PLACEHOLDER` entries by hand; `ci.yml` will refuse the PR. Past incident: a v1.12.0.0 manifest entry was merged to main without the tag ever being pushed, leaving the manifest advertising a release whose ZIP did not exist (404 on install for every user). The workflow now inserts the manifest entry only after the build succeeds, so that state is unreachable.

Steps:

1. Bump versions on `main`:
   - `Directory.Build.props`, set `<Version>` / `<AssemblyVersion>` / `<FileVersion>`
   - `LetterboxdSync/LetterboxdSync.csproj`, set `<AssemblyVersion>` / `<FileVersion>`
2. Write the user-facing changelog into `release-notes.md` (or pass `-m` inline). The release workflow uses this verbatim for both the GitHub release body and the `changelog` field in `manifest.json`.
3. Create an **annotated** tag and push it:

   ```bash
   git tag -a vX.Y.Z -F release-notes.md
   git push origin vX.Y.Z
   ```

   Lightweight tags (no annotation) are rejected by the workflow.

`release.yml` then verifies the tag matches the project's `AssemblyVersion`, builds + tests, packages the ZIP, creates the GitHub release, and inserts a new entry into `manifest.json` on `main` with the real md5 checksum, the release `sourceUrl`, and the tag annotation as the changelog. Re-running the workflow on the same tag is idempotent (updates the entry in place).

## OpenSpec

Spec-driven workflow lives under `openspec/` (`changes/`, `specs/`, `config.yaml`). Use the `/opsx:propose`, `/opsx:apply`, `/opsx:archive`, `/opsx:explore` skills for non-trivial changes when the user requests them.

## Skill routing

When the user's request matches an available skill, ALWAYS invoke it using the Skill
tool as your FIRST action. Do NOT answer directly, do NOT use other tools first.
The skill has specialized workflows that produce better results than ad-hoc answers.

Key routing rules:
- Product ideas, "is this worth building", brainstorming → invoke office-hours
- Bugs, errors, "why is this broken", 500 errors → invoke investigate
- Ship, deploy, push, create PR → invoke ship
- QA, test the site, find bugs → invoke qa
- Code review, check my diff → invoke review
- Update docs after shipping → invoke document-release
- Weekly retro → invoke retro
- Design system, brand → invoke design-consultation
- Visual audit, design polish → invoke design-review
- Architecture review → invoke plan-eng-review
