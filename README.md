# Jellyfin Letterboxd Sync Plugin

A Jellyfin plugin that automatically syncs your watch history to your Letterboxd diary. Films are logged in real-time when you finish watching, with a daily scheduled task as a safety net.

## Features

- **Real-time sync** — logs films to your Letterboxd diary the moment you finish watching
- **Daily catch-up sync** — scheduled task picks up anything the real-time handler missed
- **Multi-user support** — map multiple Jellyfin users to their own Letterboxd accounts
- **TMDb-based matching** — looks up films by TMDb ID, not title slugs, so foreign titles and special characters work correctly
- **Duplicate detection** — checks your Letterboxd diary before logging to avoid duplicate entries
- **Retry with backoff** — retries up to 3 times with exponential backoff on transient errors (expired CSRF tokens, rate limiting)
- **Favorites sync** — optionally marks films as "liked" on Letterboxd based on your Jellyfin favorites
- **Date filtering** — optionally limit the catch-up sync to only recently played films
- **Cloudflare bypass** — supports raw cookie injection when Cloudflare blocks automated login

## Requirements

- Jellyfin 10.11+
- A Letterboxd account

## Installation

### From release

1. Download the latest release ZIP from the [releases page](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/releases)
2. Extract `LetterboxdSync.dll` and `HtmlAgilityPack.dll` into your Jellyfin plugins directory:
   ```
   <jellyfin-data>/plugins/LetterboxdSync_1.0.0.0/
   ```
3. Restart Jellyfin

### Build from source

```bash
git clone https://github.com/builtbyproxy/jellyfin-plugin-letterboxd.git
cd jellyfin-plugin-letterboxd
dotnet build -c Release
```

The built DLLs will be in `LetterboxdSync/bin/Release/net9.0/`. Copy `LetterboxdSync.dll` and `HtmlAgilityPack.dll` to your plugins directory.

## Configuration

After installation, go to **Dashboard > Plugins > Letterboxd Sync** in Jellyfin.

For each user you want to sync:

1. Click **Add Account**
2. Select the Jellyfin user from the dropdown
3. Enter their Letterboxd username and password
4. Enable the account
5. Click **Save**

### Optional settings per account

| Setting | Description |
|---|---|
| **Sync favorites as liked** | When enabled, films marked as favorites in Jellyfin will be marked as "liked" on Letterboxd |
| **Only sync recently played** | Limits the daily catch-up sync to films played within the last N days (prevents a full library resync) |
| **Raw Cookies** | Paste your browser's cookie header here if Letterboxd login returns 403 due to Cloudflare protection |

### Cloudflare troubleshooting

If login fails with a 403 error, Cloudflare is blocking the automated request. To work around this:

1. Log into Letterboxd in your browser
2. Open DevTools (F12) > Network tab
3. Reload the page and click any request to `letterboxd.com`
4. Copy the full `Cookie` header value from the request headers
5. Paste it into the **Raw Cookies** field in the plugin config

The `cf_clearance` cookie in particular is what bypasses the Cloudflare challenge. These cookies expire, so you may need to refresh them periodically.

## How it works

### Real-time sync

The plugin listens for Jellyfin's `PlaybackStopped` event. When a movie is played to completion:

1. Checks if the user has a configured Letterboxd account
2. Looks up the film on Letterboxd via its TMDb ID (`letterboxd.com/tmdb/{id}`)
3. Checks the user's diary to avoid logging duplicates for the same date
4. Submits a diary entry via Letterboxd's web interface

### Scheduled sync

A daily task iterates all configured users and their played movies, syncing any that haven't been logged yet. This catches anything missed by the real-time handler (e.g., if Jellyfin was restarted mid-playback, or if Letterboxd was temporarily unreachable).

The scheduled task runs once per day by default. You can trigger it manually from **Dashboard > Scheduled Tasks > Sync watched movies to Letterboxd**.

## License

MIT
