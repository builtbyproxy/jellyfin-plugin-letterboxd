# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Jellyfin plugin ("Jellyscribe") that syncs watch history to Letterboxd (film) and Serializd (TV). C#/.NET 9, targets Jellyfin 10.11 (`Jellyfin.Controller`/`Jellyfin.Model` 10.11.0). Letterboxd's official endpoint (`/api/v0/production-log-entries`) is preferred; the plugin falls back to web scraping (cookie login, CSRF tokens, HtmlAgilityPack) when the API path fails. Serializd's API needs no such fallback. The C# namespace, project folder, and solution file all still say `LetterboxdSync` (pre-rebrand name, unchanged this release, see `openspec/changes/rebrand-jellyscribe/`); only the compiled `AssemblyName` and every user-visible surface say Jellyscribe.

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

Deploy a debug build to the local Jellyfin server: `./deploy.sh` (scp's `Jellyscribe.dll` + `HtmlAgilityPack.dll` and restarts the container).

## Architecture

### Service abstraction with fallback

`ILetterboxdService` (`ILetterboxdService.cs`) is the seam every caller uses. Two implementations:

- `LetterboxdApiClient`, preferred, talks to Letterboxd's JSON endpoints.
- `ScrapingLetterboxdService`, fallback, composes `LetterboxdHttpClient` (cookies/CSRF/Cloudflare retry), `LetterboxdAuth` (login + re-auth on 401), `LetterboxdScraper` (HTML parsing, film lookup, diary/watchlist scraping), and `LetterboxdDiary` (diary writes, review posting).

`LetterboxdServiceFactory.CreateAuthenticatedAsync` tries the API first and silently falls back to scraping if auth fails. The factory also exposes an `internal static OverrideForTesting` hook used via `InternalsVisibleTo` from `LetterboxdSync.Tests` to inject mock services, production code never touches it.

### Sync entry points

- `SyncTask`, scheduled, exports recent watches to the Letterboxd diary.
- `WatchlistSyncTask` / `WatchlistSyncRunner`, imports the user's Letterboxd watchlist as a Jellyfin playlist.
- `DiaryImportTask`, marks Jellyfin items as played if present in the Letterboxd diary.
- `PlaybackHandler`, `IHostedService` registered in `ServiceRegistrator`, fires the real-time sync on playback completion.
- `LetterboxdSyncRunner`, shared engine used by `SyncTask` and `PlaybackHandler`; `SyncGate`, `SyncHistory`, `SyncProgress`, and `TmdbCache` coordinate dedupe, progress UI, and TMDb lookups.

### Plugin surface

- `Plugin.cs` + `ServiceRegistrator.cs` register services and config.
- `Api/LetterboxdController.cs` and `Api/SidebarController.cs` expose REST endpoints consumed by the config dashboard. `LetterboxdController` also serves the read-only `ItemRating` endpoint the review modal uses to pre-fill its stars from the caller's stored Jellyfin rating.
- `Web/*.html` and `Web/*.js` are embedded resources (see `LetterboxdSync.csproj`) served as the plugin's config pages.
- `SidebarInjection.cs` registers a transformation with the File Transformation plugin to inject the sidebar link.

## Releasing

**Every merge to `main` that changes what ships, ships a release.** No manual tag pushes, no release-notes files. **We only release when a change affects the user; non-shipping PRs are exempt**: if the whole diff sits in non-shipping paths (any `*.md`, `docs/`, `openspec/`, `site/`, `worker/`, `.github/`, `LetterboxdSync.Tests/`), skip the version bump, the `## Release notes` section, and the `release-notes.ts` entry; the merge then ships no release (release.yml sees the existing tag and stops) and the site still redeploys for `site/**` changes via deploy-docs' push trigger. `manifest.json` is never exempt. The full pipeline is:

1. Open a PR. Unless non-shipping (above), the PR must:
   - Have a **Conventional Commits** title (`feat:`, `fix:`, `chore:`, `docs:`, `ci:`, `refactor:`, `test:`, `perf:`, `build:`, `style:`). Enforced by `pr-title.yml` (this one applies to non-shipping PRs too).
   - **Bump `AssemblyVersion` / `FileVersion`** in both `Directory.Build.props` and `LetterboxdSync/LetterboxdSync.csproj`. Patch bumps (e.g. `1.13.0.0` → `1.13.1.0`) are fine for CI / refactor changes. Enforced by `version-gate.yml`.
   - Fill in the **`## Release notes`** section in the PR body. `release.yml` extracts text between that heading and the next H2 and uses it verbatim as the manifest changelog field and the GitHub Release body. The PR template primes the section so it's the path of least resistance. Past entries on https://jellyscribe.dev/releases set the tone: one paragraph, user-facing prose, no symbol names / internal jargon.
   - Add a structured entry to **`site/src/data/release-notes.ts`** for the new version (headline + summary + categorised highlights). The site renders these on the Releases page above the raw manifest changelog. Same tone as the manifest changelog but split into `new` / `improvements` / `fixes` / `breaking` bullets.
   - **SDK floor policy (issue #63)**: the `Jellyfin.Controller`/`Jellyfin.Model` PackageReference version MUST equal `targetAbi.txt`, Jellyfin assemblies have per-patch AssemblyVersions, so the SDK we compile against is the real minimum Jellyfin a release can load on. Never bump the SDK routinely (Dependabot PRs are a compile signal, not a merge queue); bump it only when we need a newer API, raising `targetAbi.txt` and the minor version in the same PR. CI enforces the SDK==targetAbi match.
   - **Jellyfin 12 cliff (verified 2026-07-06)**: the 12.x SDK packages are net10.0-only, they do NOT restore against this net9.0 project (NU1202). Current net9.0 builds run fine on Jellyfin 12 servers (newer runtime loads older assemblies), but compiling against the 12 SDK forces net10.0, which cannot load on 10.11's .NET 9 host, so adopting the 12 SDK is a one-way release-stream split, never a routine bump. Staged plan: `openspec/changes/add-jellyfin-12-support/`.

2. Merge with **Squash and merge**. The squash subject is the PR title with `(#NN)` appended; the release workflow extracts the PR number from that and fetches the PR body via `gh pr view` (the squash commit body itself is not reliable across merge methods).

3. `release.yml` fires automatically on the push to `main`. It reads `AssemblyVersion` from `Directory.Build.props`, checks no tag for that version exists yet (idempotent), builds + tests, packages, creates the GitHub Release with the PR body's `## Release notes` section, inserts the manifest entry using `targetAbi.txt`, and pushes the auto-commit + tag together.

4. `deploy-docs.yml` fires via `workflow_run` on Release completion, rebuilding jellyscribe.dev with the fresh manifest. (The `GITHUB_TOKEN`-authenticated auto-commit can't fire push-based workflows, hence the explicit `workflow_run` trigger.)

### Breaking changes

The version-bump magnitude is the canonical signal, not a `!` in the PR title. Going `1.x.y` → `2.0.0` means breaking; we do not use `feat!:` / `fix!:`.

### Past incidents this pipeline prevents

- **v1.12.0.0** manifest entry was merged to main with `"checksum": "PLACEHOLDER"` and no tag ever got pushed, leaving the manifest advertising a 404'ing release for every user. `release.yml` is now the *only* writer of `manifest.json`, and `ci.yml`'s manifest validator refuses any PR that touches it with a `PLACEHOLDER` or 404 `sourceUrl`.
- **v1.13.0** was cut manually after the merge, which works but doesn't enforce that *every* merge ships. The version-gate now guarantees a release on every merge.
- **v1.12.0 and v1.13.0 site staleness**: the manifest auto-commit didn't trigger Deploy site (GitHub token limitation). The `workflow_run` trigger now fires Deploy site after every Release.
- **v1.13.0 SDK ABI break**: Jellyfin 10.11.9 removed `IUserManager.Users` (replaced by `GetUsers()`), so v1.13.0 bumped the SDK to 10.11.10 and `targetAbi.txt` to `10.11.9.0`. See `feedback_jellyfin_plugin_abi_break` in the user's memory.
- **v1.13.0 and v1.13.1 changelog drift**: v1.13.0's manifest changelog was written as a multi-paragraph incident report (markdown headings, code backticks, "MissingMethodException", PR refs) instead of the single-paragraph user prose of v1.0–v1.12. v1.13.1's was even worse: just the squash-merge commit subject `ci: enforce version bump on every PR, auto-release on merge, rebuild site on release (#50)`, because the earlier pipeline read `git log -1 --format='%B' HEAD` and `gh pr merge --squash` only puts the PR title there. release.yml now extracts the changelog from a `## Release notes` section in the PR body (via `gh pr view`), with the PR template seeding the section. Backfilled in v1.13.2.

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
