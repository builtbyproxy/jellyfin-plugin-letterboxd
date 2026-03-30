# Jellyfin Letterboxd Sync

[![CI](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/actions/workflows/ci.yml/badge.svg)](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/actions/workflows/ci.yml)
[![Release](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/actions/workflows/release.yml/badge.svg)](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/actions/workflows/release.yml)
[![GitHub release](https://img.shields.io/github/v/release/builtbyproxy/jellyfin-plugin-letterboxd)](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Downloads](https://img.shields.io/github/downloads/builtbyproxy/jellyfin-plugin-letterboxd/total)](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/releases)

Automatically sync your Jellyfin watch history to your Letterboxd diary. Films are logged in real-time when you finish watching, with a daily scheduled sync as a safety net.

Uses the authenticated Letterboxd Android REST API (`/api/v0`).

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
- **Dashboard** — sync stats, activity history, one-click manual retry for failed syncs, and manual triggering
- **Automatic APIs Retries** — gracefully handles transient Letterboxd errors and timeouts using cloned requests
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
2. Extract `LetterboxdSync.dll` to your Jellyfin plugins directory
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

| Setting                    | Description                                                                          |
| -------------------------- | ------------------------------------------------------------------------------------ |
| **Enabled**                | Must be checked for this account to sync                                             |
| **Favorites as liked**     | Marks films as "liked" on Letterboxd if favorited in Jellyfin                        |
| **Recently played only**   | Limits daily catch-up to films played in the last N days                             |
| **Watchlist to playlist**  | Creates a "Letterboxd Watchlist" playlist in Jellyfin from your Letterboxd watchlist |
| **Import diary as played** | Marks Jellyfin movies as played if they appear in your Letterboxd diary              |

### Dashboard

The **Dashboard** tab shows:

- Sync statistics (total, synced, rewatches, skipped, failed)
- Recent activity with links to each film on Letterboxd
- **Run Sync Now** button to trigger a sync on demand
- **Review** buttons to write and post reviews directly to Letterboxd

## Requirements

- Jellyfin 10.11+
- A Letterboxd account

## Building from source

```bash
git clone https://github.com/builtbyproxy/jellyfin-plugin-letterboxd.git
cd jellyfin-plugin-letterboxd
```

To build the plugin with working API credentials, you must provide a valid Letterboxd Android OAuth2 Client ID and Secret at build time. There are two ways to do this:

> [!NOTE]
> You can obtain the Client ID and Secret by intercepting the Android app's API requests.

### 1. Using a `local.props` file (Recommended for development)

Create a file named `local.props` inside the `LetterboxdSync/` directory with your credentials:

```xml
<Project>
  <PropertyGroup>
    <LetterboxdClientId>YOUR_CLIENT_ID</LetterboxdClientId>
    <LetterboxdClientSecret>YOUR_CLIENT_SECRET</LetterboxdClientSecret>
  </PropertyGroup>
</Project>
```

Then build normally:

```bash
dotnet build -c Release
```

### 2. Passing MSBuild arguments

You can also pass the properties directly via the command line:

```bash
dotnet build -c Release -p:LetterboxdClientId="YOUR_CLIENT_ID" -p:LetterboxdClientSecret="YOUR_CLIENT_SECRET"
```

Final DLL is in `LetterboxdSync/bin/Release/net9.0/`.

## License

MIT
