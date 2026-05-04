# [Jellyfin Letterboxd Sync](https://lachlanyoung.dev/jellyfin-plugin-letterboxd/)

[![CI](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/actions/workflows/ci.yml/badge.svg)](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/actions/workflows/ci.yml)
[![Release](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/actions/workflows/release.yml/badge.svg)](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/actions/workflows/release.yml)
[![codecov](https://codecov.io/gh/builtbyproxy/jellyfin-plugin-letterboxd/branch/main/graph/badge.svg)](https://codecov.io/gh/builtbyproxy/jellyfin-plugin-letterboxd)
[![GitHub release](https://img.shields.io/github/v/release/builtbyproxy/jellyfin-plugin-letterboxd)](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Downloads](https://img.shields.io/github/downloads/builtbyproxy/jellyfin-plugin-letterboxd/total)](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/releases)

**Website:** [lachlanyoung.dev/jellyfin-plugin-letterboxd](https://lachlanyoung.dev/jellyfin-plugin-letterboxd/)

Automatically sync your Jellyfin watch history to your Letterboxd diary. Films are logged in real-time when you finish watching, with a daily scheduled sync as a safety net.

Uses Letterboxd's current JSON API (`/api/v0/production-log-entries`).

<img width="2430" height="1432" alt="CleanShot 2026-03-25 at 23 34 29@2x" src="https://github.com/user-attachments/assets/19f74448-93b9-4ad2-b02b-bfb0c35d0706" />

## Features

- **Real-time sync** — films logged to your diary the moment you finish watching
- **Daily catch-up** — scheduled task picks up anything missed
- **Multi-user** — each Jellyfin user can link their own Letterboxd account
- **TMDb matching** — films matched by TMDb ID, so foreign titles and special characters work
- **Duplicate detection** — won't log the same film twice on the same day
- **Rewatch detection** — real-time playback automatically marks rewatches
- **Rating sync** — Jellyfin ratings (0-10) mapped to Letterboxd stars (0.5-5.0)
- **Favorites** — sync Jellyfin favorites as Letterboxd likes
- **Watchlist sync** — import your Letterboxd watchlist as a Jellyfin playlist
- **Diary import** — mark Jellyfin movies as played if they're in your Letterboxd diary
- **Reviews** — write and post reviews to Letterboxd from the plugin dashboard
- **Dashboard** — sync stats, activity history, and one-click sync from the plugin page
- **Cloudflare resilient** — automatic retry with backoff on rate limits, raw cookie fallback
- **Retry with backoff** — handles transient Letterboxd errors gracefully
- **Date filtering** — limit catch-up syncs to recently watched films

## Install

### Plugin repository (recommended)

1. In Jellyfin, go to **Dashboard > Plugins > Repositories**
2. Add a new repository:
   - **Name:** `LetterboxdSync`
   - **URL:** `https://raw.githubusercontent.com/builtbyproxy/jellyfin-plugin-letterboxd/main/manifest.json`
3. Go to **Catalog**, find **LetterboxdSync**, and click **Install**
4. Restart Jellyfin

### Manual install

1. Download the latest ZIP from [Releases](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/releases)
2. Extract `LetterboxdSync.dll` and `HtmlAgilityPack.dll` to your Jellyfin plugins directory
3. Restart Jellyfin

## Setup

1. Go to **Dashboard > Plugins > Letterboxd Sync**
2. Switch to the **Settings** tab
3. Click **+ Add Account**
4. Select your Jellyfin user, enter your Letterboxd username and password
5. Check **Enabled**
6. Click **Save**

That's it. Watch a movie and check your Letterboxd diary.

### Settings per account

| Setting | Description |
|---|---|
| **Enabled** | Must be checked for this account to sync |
| **Favorites as liked** | Marks films as "liked" on Letterboxd if favorited in Jellyfin |
| **Recently played only** | Limits daily catch-up to films played in the last N days |
| **Watchlist to playlist** | Creates a "Letterboxd Watchlist" playlist in Jellyfin from your Letterboxd watchlist |
| **Import diary as played** | Marks Jellyfin movies as played if they appear in your Letterboxd diary |
| **Raw Cookies** | For Cloudflare bypass — see below |

### Dashboard

The **Dashboard** tab shows:
- Sync statistics (total, synced, rewatches, skipped, failed)
- Recent activity with links to each film on Letterboxd
- **Run Sync Now** button to trigger a sync on demand
- **Review** buttons to write and post reviews directly to Letterboxd

### Cloudflare issues

If login fails with a 403 error:

1. Log into Letterboxd in your browser
2. Open DevTools (F12) > Network tab
3. Reload and click any request to `letterboxd.com`
4. Copy the **Cookie** header value (everything after `Cookie: `, not the label itself)
5. Paste it into the **Raw Cookies** field
6. Copy the **User-Agent** request header value from the same request and paste it into the **User-Agent** field

The `cf_clearance` cookie expires periodically and may need refreshing.

**Important:** Cloudflare ties `cf_clearance` to the exact User-Agent that solved the challenge. If you copied cookies from Chrome but leave the User-Agent field blank, the plugin sends the default Firefox UA and Cloudflare will reject the cookie. Always paste the User-Agent from the same browser you copied the cookies from. Leave it blank only if you copied cookies from Firefox 134 on Windows.

## Requirements

- Jellyfin 10.11+
- A Letterboxd account

## Building from source

```bash
git clone https://github.com/builtbyproxy/jellyfin-plugin-letterboxd.git
cd jellyfin-plugin-letterboxd
dotnet build -c Release
```

Output DLLs are in `LetterboxdSync/bin/Release/net9.0/`.

## License

MIT
