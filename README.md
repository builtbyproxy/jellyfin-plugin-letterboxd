# Jellyfin Letterboxd Sync

Automatically sync your Jellyfin watch history to your Letterboxd diary. Films are logged in real-time when you finish watching, with a daily scheduled sync as a safety net.

## Features

- **Real-time sync** — films are logged to your diary the moment you finish watching
- **Daily catch-up** — a scheduled task picks up anything missed by the real-time sync
- **Multi-user** — each Jellyfin user can link their own Letterboxd account
- **TMDb matching** — films are matched by TMDb ID, so foreign titles and special characters just work
- **Duplicate detection** — won't log the same film twice on the same day
- **Retry with backoff** — handles transient Letterboxd errors gracefully
- **Favorites** — optionally sync Jellyfin favorites as Letterboxd likes
- **Date filtering** — limit the catch-up sync to recently watched films

Uses Letterboxd's current JSON API (`/api/v0/production-log-entries`).

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
2. Extract `LetterboxdSync.dll` and `HtmlAgilityPack.dll` to:
   ```
   <jellyfin-data>/plugins/LetterboxdSync_1.0.0.0/
   ```
3. Restart Jellyfin

## Setup

1. Go to **Dashboard > Plugins > Letterboxd Sync**
2. Click **Add Account**
3. Select your Jellyfin user, enter your Letterboxd username and password
4. Check **Enabled**
5. Click **Save**

That's it. Watch a movie and check your Letterboxd diary.

### Settings

| Setting | Default | Description |
|---|---|---|
| **Enabled** | Off | Must be checked for this account to sync |
| **Sync favorites as liked** | Off | Marks films as "liked" on Letterboxd if they're favorited in Jellyfin |
| **Only sync recently played** | Off | When enabled, the daily catch-up only looks at films played in the last N days |
| **Days to look back** | 7 | How far back the catch-up sync goes (only applies when date filtering is on) |
| **Raw Cookies** | Empty | For Cloudflare bypass — see below |

### Cloudflare issues

If your Letterboxd login fails with a 403 error, Cloudflare is blocking the request. To fix this:

1. Log into Letterboxd in your browser
2. Open DevTools (F12) > Network tab
3. Reload the page and click any request to `letterboxd.com`
4. Copy the full **Cookie** header value
5. Paste it into the **Raw Cookies** field in the plugin config

The key cookie is `cf_clearance`. It expires periodically, so you may need to refresh it.

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
