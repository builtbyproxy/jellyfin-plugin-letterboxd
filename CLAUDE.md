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

**Every merge to `main` ships a release.** No manual tag pushes, no release-notes files. The pipeline is:

1. Open a PR. The PR must:
   - Have a **Conventional Commits** title (`feat:`, `fix:`, `chore:`, `docs:`, `ci:`, `refactor:`, `test:`, `perf:`, `build:`, `style:`). Enforced by `pr-title.yml`.
   - **Bump `AssemblyVersion` / `FileVersion`** in both `Directory.Build.props` and `LetterboxdSync/LetterboxdSync.csproj`. Patch bumps (e.g. `1.13.0.0` → `1.13.1.0`) are fine for docs / CI / refactor changes; every PR ships. Enforced by `version-gate.yml`.
   - If the `Jellyfin.Controller` / `Jellyfin.Model` PackageReference is bumped and the new SDK introduces an ABI break the plugin now depends on, also bump `targetAbi.txt` to the minimum Jellyfin version that has the new ABI. This is the floor Jellyfin's plugin catalog uses to gate the release.

2. Merge with **Squash and merge**. The PR title becomes the squash commit subject, which becomes the GitHub Release body and the `changelog` field in `manifest.json`.

3. `release.yml` fires automatically on the push to `main`. It reads `AssemblyVersion` from `Directory.Build.props`, checks no tag for that version exists yet (idempotent), builds + tests, packages, creates the GitHub Release, inserts the manifest entry using `targetAbi.txt`, and pushes the auto-commit + tag together.

4. `deploy-docs.yml` fires via `workflow_run` on Release completion, rebuilding letterboxdsync.dev with the fresh manifest. (The `GITHUB_TOKEN`-authenticated auto-commit can't fire push-based workflows, hence the explicit `workflow_run` trigger.)

### Breaking changes

The version-bump magnitude is the canonical signal, not a `!` in the PR title. Going `1.x.y` → `2.0.0` means breaking; we do not use `feat!:` / `fix!:`.

### Past incidents this pipeline prevents

- **v1.12.0.0** manifest entry was merged to main with `"checksum": "PLACEHOLDER"` and no tag ever got pushed, leaving the manifest advertising a 404'ing release for every user. `release.yml` is now the *only* writer of `manifest.json`, and `ci.yml`'s manifest validator refuses any PR that touches it with a `PLACEHOLDER` or 404 `sourceUrl`.
- **v1.13.0** was cut manually after the merge, which works but doesn't enforce that *every* merge ships. The version-gate now guarantees a release on every merge.
- **v1.12.0 and v1.13.0 site staleness**: the manifest auto-commit didn't trigger Deploy site (GitHub token limitation). The `workflow_run` trigger now fires Deploy site after every Release.
- **v1.13.0 SDK ABI break**: Jellyfin 10.11.9 removed `IUserManager.Users` (replaced by `GetUsers()`), so v1.13.0 bumped the SDK to 10.11.10 and `targetAbi.txt` to `10.11.9.0`. See `feedback_jellyfin_plugin_abi_break` in the user's memory.

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
