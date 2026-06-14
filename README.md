# [Jellyfin Letterboxd Sync](https://letterboxdsync.dev/)

[![CI](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/actions/workflows/ci.yml/badge.svg)](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/actions/workflows/ci.yml)
[![Release](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/actions/workflows/release.yml/badge.svg)](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/actions/workflows/release.yml)
[![codecov](https://codecov.io/gh/builtbyproxy/jellyfin-plugin-letterboxd/branch/main/graph/badge.svg)](https://codecov.io/gh/builtbyproxy/jellyfin-plugin-letterboxd)
[![GitHub release](https://img.shields.io/github/v/release/builtbyproxy/jellyfin-plugin-letterboxd)](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Downloads](https://img.shields.io/github/downloads/builtbyproxy/jellyfin-plugin-letterboxd/total)](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/releases)

**Website:** [letterboxdsync.dev](https://letterboxdsync.dev/)
**What's new:** [Release notes for every version](https://letterboxdsync.dev/releases/)

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
- **Rating sync, both ways** — Jellyfin ratings (0-10) mapped to Letterboxd stars (0.5-5.0), and Letterboxd ratings seed your Jellyfin user ratings
- **Favorites** — sync Jellyfin favorites as Letterboxd likes
- **Watchlist sync** — import your Letterboxd watchlist as a Jellyfin playlist
- **Jellyseerr integration** — auto-request watchlist films missing from your library, attributed to the right user; optionally backfill requests for films that arrived outside Jellyseerr, and mirror your Letterboxd watchlist into Jellyseerr
- **Diary import** — mark Jellyfin movies as played if they're in your Letterboxd diary
- **Reviews** — write and post reviews to Letterboxd from the plugin dashboard
- **Dashboard** — sync stats, activity history, and one-click sync from the plugin page
- **Cloudflare resilient** — automatic retry with backoff on rate limits, raw cookie fallback
- **Retry with backoff** — handles transient Letterboxd errors gracefully
- **Date filtering** — limit catch-up syncs to recently watched films

## Install

### Plugin repository (recommended)

1. In Jellyfin, go to **Dashboard > Plugins > Repositories**
2. Add the **File Transformation** repository (required for the sidebar link):
   - **Name:** `File Transformation`
   - **URL:** `https://www.iamparadox.dev/jellyfin/plugins/manifest.json`
3. Add the LetterboxdSync repository:
   - **Name:** `LetterboxdSync`
   - **URL:** `https://raw.githubusercontent.com/builtbyproxy/jellyfin-plugin-letterboxd/main/manifest.json`
4. Go to **Catalog**, install **File Transformation**, then install **LetterboxdSync**
5. Restart Jellyfin
6. Hard-refresh the Jellyfin web UI (Ctrl/Cmd + Shift + R) so the new sidebar link loads

### Manual install

1. Install the **File Transformation** plugin first (see [iamparadox27/Jellyfin.Plugin.FileTransformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/releases)), required for the sidebar link to appear
2. Download the latest LetterboxdSync ZIP from [Releases](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/releases)
3. Extract `LetterboxdSync.dll` and `HtmlAgilityPack.dll` to your Jellyfin plugins directory
4. Restart Jellyfin

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
| **Enabled** | Master switch for this account; nothing syncs while unchecked, saved settings are kept |
| **Favorites as liked** | Marks films as "liked" on Letterboxd if favorited in Jellyfin |
| **Recently played only** | Limits daily catch-up to films played in the last N days |
| **Primary account** | When one Jellyfin user links multiple Letterboxd accounts, the primary wins on rating-import conflicts and is preselected in the review modal |
| **Watchlist to playlist** | Mirrors your Letterboxd watchlist into a Jellyfin playlist daily; each account gets its own playlist (name configurable) |
| **Auto-request via Jellyseerr** | Watchlisted films missing from your library are requested in Jellyseerr, attributed to this user's Jellyseerr account (set the Jellyseerr URL and API key above the account list) |
| **Backfill available requests** | Extends auto-request to films already in the library that have no request record, so films that arrived outside Jellyseerr still show a requester; never triggers re-downloads |
| **Mirror into Jellyseerr watchlist** | Two-way mirror of your Letterboxd watchlist into your Jellyseerr user's own watchlist (movies only) |
| **Import diary as played** | Marks Jellyfin movies as played if they appear in your Letterboxd diary |
| **Skip previously synced** | Uses the plugin's local sync history to skip films already logged without hitting Letterboxd; recommended, especially on large libraries |
| **Stop on failure** | Halts the run at the first failed film to avoid inflaming rate limits; the rest are picked up next run |
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

**Important:** Cloudflare ties `cf_clearance` to the exact User-Agent that solved the challenge. If you copied cookies from Chrome but leave the User-Agent field blank, the plugin sends the default Firefox UA and Cloudflare will reject the cookie. Always paste the User-Agent from the same browser you copied the cookies from. Leave it blank only if you copied cookies from Firefox 134 on Windows.

#### Still 403ing after pasting Raw Cookies and a matching User-Agent

When a correctly-copied cookie still gets blocked, it's usually one of these:

1. **Different IP.** Cloudflare pins `cf_clearance` to the IP address that solved the challenge, not just the User-Agent. If the box running Jellyfin reaches the internet via a different public IP than the browser did (different machine, VPN, mobile tether, a server in a datacenter), Cloudflare sees the token arrive from a new IP and rejects it. Fix: paste fresh cookies from a browser running on the **same network as the Jellyfin server**, and watch out for VPNs or split tunnels.

2. **It expired.** `cf_clearance` from a managed challenge is short-lived, often around 30 minutes. If there's a gap between copying the cookies and the sync actually running, the token can already be dead. Fix: paste fresh cookies and immediately trigger a sync from the plugin dashboard rather than waiting for the scheduled run.

3. **Connection fingerprint.** Cloudflare doesn't only check the cookie and UA, it also fingerprints the TLS handshake and HTTP/2 behaviour of the connection. A plugin's HTTP client doesn't look like a real browser at that layer, so on a site running bot-fight mode the right cookie isn't always enough on its own. There isn't much the plugin can do about this one.

If you've ruled all three out and a single film keeps getting stuck on the TMDb lookup, open an issue. A workaround that skips the Cloudflare-protected lookup for that one film (pointing a TMDb ID directly at a Letterboxd slug) is being considered.

## Telemetry

The plugin can send **anonymous, opt-in** usage telemetry. It is **off by default** — nothing is ever sent unless you enable it (one-time dashboard banner or the Settings checkbox).

When enabled, one small ping is sent per week, plus one extra ping (capped at one per day) when sync errors start occurring so fleet-wide breakage gets caught early. The full payload is exactly this — you can see your own at any time via **Settings → Anonymous Telemetry → Preview exact JSON**:

```json
{
  "schema_version": 1,
  "instance_id": "8a6f4f6e-1f2b-4c43-9a57-2f0e6f3b9d1c",
  "ping_type": "weekly",
  "plugin_version": "1.16.0.0",
  "jellyfin_version": "10.11.11",
  "features": { "watchlist_sync": true, "diary_import": false, "...": "booleans of which settings are enabled" },
  "buckets": { "accounts": "1", "library": "2k-10k", "syncs_per_week": "1-10" },
  "errors": { "cloudflare_403": 0, "auth_failure": 0, "tmdb_lookup": 0, "jellyseerr_error": 0, "other": 0,
              "state": { "cloudflare_403": false, "...": "which error types are currently occurring" } }
}
```

The precise promise, worded carefully:

- **No IPs, usernames, film titles, library content, or exact counts ever enter the dataset.** Counts are reported in buckets only. (Transport logs at the hosting platform retain caller IPs for the platform's own short retention window, like any HTTPS service; they are never stored in the telemetry dataset.)
- The instance ID is **random**, generated when you opt in, never derived from your hardware, network, or Jellyfin install. **Regenerate it any time** in Settings: future pings get a fresh identity. Old rows remain (unlinked going forward); at small fleet sizes configuration similarity could in principle still allow correlation, so the honest claim is "unlinked", not "erased".
- The "Preview exact JSON" modal doubles as a **diagnostic bundle** for bug reports. It contains your instance ID — pasting it into a public issue links that ID to your past pings, which is why the modal offers **Copy + regenerate ID**.
- Disabling telemetry stops all pings immediately.

What it's for: deciding what gets built next based on what people actually use, and an automated canary that compares error rates across releases and files regression issues before bug reports arrive.

### Send logs to the developer

When something goes wrong, the **Logs** tab has a **Send logs to developer** button. It packages the recent Letterboxd Sync log lines shown on that tab (passwords, cookies, and auth tokens are never logged) plus an anonymous telemetry snapshot, uploads them privately, and gives you a short **reference code** (e.g. `LBX-7Q2F9K`) to quote if you open a bug report.

Unlike the anonymous telemetry above, **logs are not anonymous** — they can contain your Letterboxd username or film titles, and the bundle is linked to your telemetry instance ID. So it is strictly opt-in per use: a confirmation step spells this out, lets you add a note describing the problem, and offers a preview of exactly what is sent before anything leaves your server. Works whether or not telemetry is enabled. Uploaded bundles are stored privately and auto-deleted after 90 days.

## Requirements

- Jellyfin 10.11+
- A Letterboxd account
- [File Transformation plugin](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation), required for the Letterboxd link to appear in the Jellyfin sidebar (everything else works without it)
- Optional: a [Jellyseerr](https://github.com/seerr-team/seerr) instance for the auto-request and watchlist-mirror integrations

## Building from source

```bash
git clone https://github.com/builtbyproxy/jellyfin-plugin-letterboxd.git
cd jellyfin-plugin-letterboxd
dotnet build -c Release
```

Output DLLs are in `LetterboxdSync/bin/Release/net9.0/`.

## Contributing

PRs welcome. A few conventions:

- **PR body shape** lives in [`.github/pull_request_template.md`](.github/pull_request_template.md). Symptom first, plain English, six fixed sections (What's broken, Why it happens, What this PR does, How to test, Follow-ups).
- **Non-trivial changes** are planned through [`openspec/`](openspec/) before implementation: proposal, design, specs, then tasks. See [`openspec/changes/`](openspec/changes/) for active proposals and the [`archive/`](openspec/changes/archive/) folder for past ones.

## License

MIT
