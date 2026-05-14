export type ReleaseCategory = 'new' | 'improvements' | 'fixes' | 'breaking';

export type ReleaseNotes = {
  version: string;
  headline: string;
  summary?: string;
  highlights: Partial<Record<ReleaseCategory, string[]>>;
  upgradeNotes?: string;
};

export const releaseNotes: ReleaseNotes[] = [
  {
    version: '1.12.0',
    headline: 'Multi-account support: one Jellyfin user, many Letterboxd accounts',
    summary: 'Shared TV logins (e.g. two people on the same family Jellyfin profile) can now each have their own Letterboxd diary, ratings, and watchlist.',
    highlights: {
      new: [
        'A Jellyfin user can link multiple Letterboxd accounts. Auto-sync (real-time and scheduled) fans out across every enabled account; one failing account never blocks the others.',
        'The sidebar "My Letterboxd" page now has feature parity with the admin plugin page for per-account management: add, remove, reorder, test, and configure every account that belongs to your Jellyfin user, without needing admin access. (Admin-only things like the Jellyseerr server URL and editing other users\' accounts stay in the admin page.)',
        "Per-account watchlist playlists. Default name is 'Letterboxd Watchlist ({letterboxdUsername})' so two accounts on one Jellyfin user get two separate playlists, with an optional per-account name override.",
        "Review modal has a 'Post as' account selector when more than one account is enabled, defaulting to posting on all of them.",
        'New IsPrimary flag on each account: used to break rating conflicts on diary import and as the preselected option in the review modal.',
      ],
      improvements: [
        'Manual API endpoints (/Sync, /SyncWatchlist, /Review) accept an optional letterboxdUsername selector. Omit it to fan out across all enabled accounts.',
        'Diary import unions played-state across all linked accounts and merges ratings with the primary account winning conflicts. Existing Jellyfin ratings are still preserved.',
      ],
      breaking: [
        "Single-account users will see a new 'Letterboxd Watchlist (yourusername)' playlist created on the next watchlist sync. The old 'Letterboxd Watchlist' playlist is left untouched so you can delete or migrate at your leisure.",
      ],
    },
  },
  {
    version: '1.11.3',
    headline: 'Plain-English error and docs when Cloudflare 403s with cookies already set',
    highlights: {
      improvements: [
        "The TMDb-lookup 403 exception now names the three real causes (cf_clearance expired, often around 30 minutes; pinned to a different IP than the Jellyfin server; or rejected by Cloudflare's TLS fingerprinting) and points at the README, instead of suggesting raw cookies that the user has already pasted.",
        "README has a new 'Still 403ing after pasting Raw Cookies and a matching User-Agent' subsection under 'Cloudflare issues' with a concrete fix per cause. Addresses the dead-end case reported in #34.",
      ],
    },
  },
  {
    version: '1.11.2',
    headline: 'Stop phantom rewatches on diary-imported films',
    highlights: {
      fixes: [
        "With diary-import enabled, the daily sync was posting phantom rewatch entries to Letterboxd for films you'd only marked played via diary import (never actually watched on Jellyfin). The runner now waits for a real Jellyfin playback before logging the rewatch.",
      ],
      improvements: [
        'Install instructions now call out the File Transformation plugin as a prerequisite for the in-sidebar Letterboxd link.',
      ],
    },
  },
  {
    version: '1.11.1',
    headline: "Don't request the wrong movie when watchlisting a TV show",
    highlights: {
      fixes: [
        'Watchlisting a TV show on Letterboxd no longer auto-requests an unrelated movie in Jellyseerr. TMDb has independent ID namespaces for movies and TV (e.g. tv/198102 = Hijack, movie/198102 = Cutie Honey Flash); the link extractor was treating every tmdb link as a movie ID. Now skips /tv/ links, with regression coverage.',
      ],
    },
  },
  {
    version: '1.11.0',
    headline: 'Bidirectional rating sync and in-dashboard logs',
    highlights: {
      new: [
        'Bidirectional rating sync. Dashboard reviews mirror their star rating into Jellyfin (always overwrites), and the daily diary import seeds Jellyfin ratings from Letterboxd for films that don\'t yet have a Jellyfin rating (anti-clobber).',
        'In-dashboard Logs tab with per-user and free-text filters, copy-to-clipboard, and download-as-.log for support requests.',
      ],
      improvements: [
        'Diary import switched from /log-entries to /films?memberRelationship=Watched so films you rated on Letterboxd without logging a watch now sync too.',
        'Privacy hardening: review text is no longer logged.',
      ],
    },
  },
  {
    version: '1.10.1',
    headline: 'Maintenance: bump GitHub Actions runtime',
    highlights: {
      improvements: [
        'Bumped GitHub Actions versions (checkout v6, setup-dotnet v5, action-gh-release v3) to remain on a supported Node version. No plugin behaviour changes.',
      ],
    },
  },
  {
    version: '1.10.0',
    headline: 'Watchlist mirroring into Jellyseerr',
    highlights: {
      new: [
        "Mirror Letterboxd watchlist into Jellyseerr's user watchlist. Per-account toggle, two-way sync, movies-only so manually-added Jellyseerr TV is safe.",
        "On-demand 'Sync Watchlist Now' button on the plugin dashboard so you don't have to wait for the daily run.",
      ],
      improvements: [
        "Pre-flight check against Jellyseerr's media status eliminates duplicate requests for already-pending/processing/available titles.",
        'Per-user watchlist sync runner extracted with a shared SyncGate so diary and watchlist syncs serialise.',
      ],
    },
  },
  {
    version: '1.9.1',
    headline: 'Local-history backstop against Cloudflare-induced duplicates',
    summary: 'Addresses #21.',
    highlights: {
      fixes: [
        'Refuse to MarkAsWatched when sync history shows a recent successful sync that does not pass the rewatch threshold. Fixes the Cloudflare-failed-validator path that was creating real Letterboxd diary duplicates.',
      ],
      improvements: [
        'Paginated dashboard history table (100 per page) now that the 500-entry cap is gone.',
      ],
    },
  },
  {
    version: '1.9.0',
    headline: 'Skip already-synced films and let non-admins Run Sync Now',
    summary: 'Fixes #20.',
    highlights: {
      new: [
        'Non-admin users can now Run Sync Now (triggers their own account only).',
        'Per-account stop-on-failure toggle to halt the moment Letterboxd anti-flooding triggers.',
      ],
      improvements: [
        "Skip already-synced films using local sync history so we don't burn Cloudflare quota on duplicate checks.",
        'Prioritise previously-failed and skipped films first.',
        'Explicit info-level logging for every skip with a reason.',
      ],
    },
  },
  {
    version: '1.8.0',
    headline: 'Jellyseerr auto-request for unmatched watchlist films',
    highlights: {
      new: [
        'Jellyseerr auto-request for unmatched watchlist films, with per-user attribution via Jellyfin User ID.',
      ],
      fixes: [
        'Dedup fix: the watchlist playlist no longer accumulates duplicate entries on each run.',
      ],
    },
  },
  {
    version: '1.7.1',
    headline: 'Watchlist sync no longer returns 0 films via official API',
    highlights: {
      fixes: [
        'A redundant `member` query param was causing Letterboxd to return empty items. Removed.',
      ],
    },
  },
  {
    version: '1.7.0',
    headline: 'Per-account User-Agent override',
    highlights: {
      new: [
        'Per-account User-Agent override so Cloudflare cookies copied from any browser (Chrome, Safari, etc.) work without UA mismatch.',
      ],
    },
  },
  {
    version: '1.6.1',
    headline: 'Reviews work again via official API',
    summary: 'Fixes #12.',
    highlights: {
      fixes: [
        'Resolve film LID from TMDb ID instead of slug when posting reviews via the official API.',
      ],
    },
  },
  {
    version: '1.6.0',
    headline: 'Official Letterboxd API integration with scraping fallback',
    highlights: {
      new: [
        'Official Letterboxd API is now the primary path, with web scraping as a fallback. Eliminates the Cloudflare 403 errors that were blocking syncs.',
      ],
    },
  },
  {
    version: '1.5.0',
    headline: 'User self-service account setup and standalone user page',
    highlights: {
      new: [
        'User self-service account setup — users link their own Letterboxd account without admin help.',
        'Sidebar link for all users.',
        'Test connection button on the account form.',
        'Standalone user page via File Transformation injection.',
      ],
    },
  },
  {
    version: '1.4.0',
    headline: 'Architecture refactor, TMDb cache, progress dashboard',
    highlights: {
      new: [
        'Progress dashboard showing sync state at a glance.',
        'TMDb cache for repeated lookups.',
      ],
      improvements: [
        'Architecture refactor for clearer separation of HTTP, auth, scraping, and diary writes.',
        'Cloudflare resilience improvements.',
        'Watchlist cleanup pass.',
      ],
      fixes: ['Diary import fix.'],
    },
  },
  {
    version: '1.3.0',
    headline: 'Real-time playback sync',
    highlights: {
      new: [
        'Real-time playback sync via PlaybackHandler — diary entries land within seconds of credits rolling.',
      ],
      improvements: ['Automatic session re-auth on 401.'],
    },
  },
  {
    version: '1.2.1',
    headline: 'Sync history persists across version upgrades',
    highlights: {
      fixes: ['Fix sync history persistence across version upgrades.'],
    },
  },
  {
    version: '1.2.0',
    headline: 'Star ratings in reviews and rewatch date picker',
    highlights: {
      new: [
        'Star ratings in reviews.',
        'Rewatch date picker.',
        'Better error display in the dashboard.',
      ],
      improvements: ['Cloudflare retry on review posting.'],
      fixes: ['Sync history persistence fix.'],
    },
  },
  {
    version: '1.1.0',
    headline: 'Dashboard, watchlist sync, diary import, reviews',
    highlights: {
      new: [
        'Dashboard with sync history.',
        'Watchlist sync.',
        'Diary import.',
        'Reviews from the dashboard.',
        'Rating sync.',
        'Rewatch detection.',
      ],
      improvements: ['Cloudflare backoff.'],
    },
  },
  {
    version: '1.0.0',
    headline: 'Initial release',
    highlights: {
      new: [
        'Real-time sync on playback completion.',
        'Scheduled catch-up sync.',
        'Multi-user support.',
        'Duplicate detection.',
        'Retry with exponential backoff.',
      ],
    },
  },
];

export const notesByVersion: Record<string, ReleaseNotes> = Object.fromEntries(
  releaseNotes.map((n) => [n.version, n]),
);
