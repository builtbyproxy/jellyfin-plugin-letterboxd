# CLAUDE.md

## Project Overview

Jellyfin plugin that syncs watch history to Letterboxd. C#/.NET 9, targets Jellyfin 10.11. No official Letterboxd API exists, so this plugin authenticates via web scraping (cookie-based login, CSRF tokens, HTML parsing with HtmlAgilityPack).

## Build & Test

```bash
dotnet build -c Release
dotnet test -c Release --verbosity normal
```

## Architecture

Four core classes handle Letterboxd interaction:
- `LetterboxdHttpClient` — shared HTTP client, cookie/CSRF management, Cloudflare retry
- `LetterboxdAuth` — login, session management, re-auth on 401
- `LetterboxdScraper` — HTML parsing, film lookup, diary/watchlist scraping
- `LetterboxdDiary` — diary writes, review posting

Three scheduled tasks: `SyncTask` (export watches), `WatchlistSyncTask` (import watchlist to playlist), `DiaryImportTask` (import diary as played).

Real-time sync via `PlaybackHandler` (IHostedService, fires on playback completion).

## Release Process

Tag with `git tag v1.x.0 && git push --tags`. GitHub Actions builds, tests, packages ZIP, creates release, and auto-updates manifest.json checksum. Use PLACEHOLDER for checksum in manifest before tagging.

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
