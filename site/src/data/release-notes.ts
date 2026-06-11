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
    version: '1.16.0',
    headline: 'Anonymous opt-in telemetry',
    summary:
      'Entirely opt-in and off by default. If you enable it, one small anonymous ping a week tells the project which features are actually used, so roadmap decisions stop being guesses. Nothing else in the plugin changes.',
    highlights: {
      new: [
        'Anonymous usage telemetry, opt-in via a one-time dashboard banner or the Settings checkbox. The payload is minimal and bucketed: version numbers, which features are enabled, and rough size buckets. Never film titles, usernames, IPs, or exact numbers. A "Preview exact JSON" button in Settings shows precisely what would be sent, and the full payload is documented in the README.',
        'Identified only by a random instance ID generated when you opt in, with a one-click Regenerate button that unlinks future pings. The preview doubles as a diagnostic bundle for bug reports, with an honest warning about what pasting it publicly reveals.',
        'An extra anonymous ping fires when sync errors start occurring (capped at one per day), powering an automated canary that compares error rates across releases and catches fleet-wide breakage, like a Letterboxd endpoint change, before bug reports arrive.',
      ],
    },
    version: '1.15.4',
    headline: 'Required update for Jellyfin 10.11.9 and 10.11.10 servers',
    summary:
      'Fixes releases v1.14.1 through v1.15.3 failing to load on Jellyfin 10.11.9/10.11.10 (the plugin was compiled against a newer Jellyfin SDK than those servers ship). If your plugin recently showed as disabled or "malfunctioned", update to this version. No feature changes.',
    highlights: {
      fixes: [
        'Jellyfin assemblies carry full per-patch versions, so a plugin compiled against the 10.11.11 SDK silently fails to load on 10.11.10 and older, while the catalog still offered those releases to 10.11.9+ servers. This release is compiled against the 10.11.9 SDK, matching the advertised minimum for the first time, and the catalog metadata for the affected versions has been corrected.',
        'New build policy, enforced by CI: the plugin is always compiled against the oldest supported Jellyfin SDK, and the minimum only rises deliberately when a newer Jellyfin API is genuinely needed, never via a routine dependency update.',
      ],
    },
    upgradeNotes:
      'If the plugin is currently disabled on your server: update to v1.15.4 in the catalog (or reinstall), then restart Jellyfin.',
  },
  {
    version: '1.15.3',
    headline: 'Maintenance: telemetry design spec',
    summary: 'No plugin behaviour changes.',
    highlights: {
      improvements: [
        'Adds the OpenSpec change proposal for upcoming anonymous, opt-in usage telemetry: minimal bucketed payload, default off with a one-time prompt, full payload preview in settings, and a fleet canary that auto-detects regressions. Spec only; no telemetry code ships in this release.',
      ],
    },
  },
  {
    version: '1.15.2',
    headline: 'Maintenance: deterministic test teardown for manual-sync endpoints',
    summary: 'No plugin behaviour changes.',
    highlights: {
      improvements: [
        'Fixes an intermittent CI failure: the manual sync endpoints return 202 and run in the background, and a background sync from one test could still hold the shared sync gate when the next test ran, turning an expected 400 into a 409. The test harness now waits for the spawned sync to finish before the next test starts.',
      ],
    },
  },
  {
    version: '1.15.1',
    headline: 'Maintenance: letterboxdsync.dev release notes backfill',
    summary: 'No plugin behaviour changes.',
    highlights: {
      improvements: [
        'The Releases page on letterboxdsync.dev now has structured highlights for v1.13.4 through v1.15.0, which had shipped without entries.',
      ],
    },
  },
  {
    version: '1.15.0',
    headline: 'Jellyseerr request backfill for already-available watchlist films',
    summary:
      'New opt-in per-account setting "Also backfill requests for already-available watchlist films" (off by default; requires Auto-request). Default behaviour is unchanged for everyone who leaves the box off.',
    highlights: {
      new: [
        'Watchlist auto-request already creates attributed Jellyseerr requests for films missing from the library, but a film that is on a watchlist and entered the library through another path (manual Radarr add, an import list, or a deleted request) ended up Available with no request record, untraceable. With backfill on, the whole watchlist is considered and available-but-unrequested titles still get an attributed request, so "who requested this?" is always answerable.',
        'Per-user dedup: a backfill request is skipped only when the title is blocklisted or this user already has a request for it. Verified against Jellyseerr 3.2.0 that a backfill request on available media succeeds without triggering a re-download.',
      ],
    },
  },
  {
    version: '1.14.1',
    headline: 'Maintenance: Jellyfin SDK 10.11.11',
    summary: 'No plugin behaviour changes.',
    highlights: {
      improvements: [
        'Jellyfin.Controller and Jellyfin.Model updated from 10.11.10 to 10.11.11 (a one-change upstream patch release; no ABI impact, targetAbi unchanged). First update delivered by the Dependabot watch introduced in v1.13.3.',
      ],
    },
  },
  {
    version: '1.14.0',
    headline: 'Stops phantom daily rewatches and endless retries of permanently-failing films',
    summary:
      'Fixes two scheduled-sync dedup bugs reported by a plugin user, one of which re-logged the same film to Letterboxd roughly every other day.',
    highlights: {
      fixes: [
        'Films marked played on Jellyfin with no last-played date (marked watched manually, or before Jellyfin tracked dates) defaulted their viewing date to "today", which drifted every run and slipped past all duplicate checks, posting a phantom rewatch to the Letterboxd diary on a rolling basis. Scheduled sync now skips these films entirely until a real play date exists.',
        'A film whose sync always fails (for example one Letterboxd cannot match) was re-queued at the head of the queue on every run, forever. Sync now abandons a film after 3 consecutive failures and a successful sync resets the counter.',
      ],
    },
  },
  {
    version: '1.13.4',
    headline: 'Maintenance: test coverage expansion',
    summary: 'No plugin behaviour changes.',
    highlights: {
      improvements: [
        'Test suite expanded to 89.8% line / 81.4% branch coverage, adding coverage for sync-gate contention, named-account targeting, skip-previously-synced filtering, stop-on-failure, and the local-history duplicate backstop.',
      ],
    },
  },
  {
    version: '1.13.3',
    headline: 'Maintenance: Dependabot watches the Jellyfin SDK, version-gate links targetAbi to minor bumps',
    summary:
      'Two preventative measures aimed at the next ABI surprise. No plugin behaviour changes.',
    highlights: {
      improvements: [
        "Dependabot now opens a PR the day Jellyfin ships a new Jellyfin.Controller / Jellyfin.Model patch, so the next 10.11.x ABI break is caught by CI on our timeline rather than via a user report (incident: v1.13.0 only existed because 10.11.9's silent IUserManager.Users removal surfaced as a bigrichwood bug report).",
        'version-gate now refuses a patch-only bump when targetAbi.txt changes in the same PR. Moving the Jellyfin compatibility floor stops a cohort of users from being offered the next plugin update; that warrants at least a minor version bump so the change is visible in the release notes.',
      ],
    },
  },
  {
    version: '1.13.2',
    headline: 'Release notes pipeline: prose-style changelogs back, sourced from the PR body',
    summary:
      "Restores the v1.0–v1.12 manifest-changelog tone after v1.13.0 and v1.13.1 drifted into incident-report prose and a conventional-commit subject line respectively. Backfills both on letterboxdsync.dev's Releases page.",
    highlights: {
      improvements: [
        "release.yml now fetches the merged PR's body via the GitHub API and extracts text between a '## Release notes' header and the next H2 as the manifest changelog. Falls back to the PR title only if the section is missing.",
        "New .github/pull_request_template.md primes every PR with the section so it's the path of least resistance.",
        'Backfills the manifest changelog for v1.13.0 and v1.13.1 to match the single-paragraph user-facing prose of v1.0 through v1.12.',
        'Backfills letterboxdsync.dev with structured Release notes entries for v1.13.0 and v1.13.1 (previously missing).',
      ],
    },
  },
  {
    version: '1.13.1',
    headline: 'Maintenance: stronger release pipeline',
    summary: 'No plugin behaviour changes.',
    highlights: {
      improvements: [
        'Every PR now bumps the version (gated by a required CI check) and auto-ships on merge to main, replacing the manual `git tag` step.',
        'PR titles must follow Conventional Commits (`feat:`, `fix:`, `chore:`, `docs:`, `ci:`, `refactor:`, `test:`, `perf:`, `build:`, `style:`). Enforced by CI on every PR.',
        'letterboxdsync.dev rebuilds on every release via a `workflow_run` trigger, so the Releases page is never stale (the manifest auto-commit otherwise cannot fire push-based workflows).',
      ],
    },
  },
  {
    version: '1.13.0',
    headline: 'Required update for Jellyfin 10.11.9 and newer',
    summary: 'Fixes #46. Restores sync on the new SDK ABI; no other plugin behaviour changes.',
    highlights: {
      fixes: [
        'Jellyfin 10.11.9 removed the `Users` property from `IUserManager` and replaced it with a `GetUsers()` method. The plugin was compiled against the 10.11.0 SDK, so on Jellyfin 10.11.9 and 10.11.10 every read of the user list threw `MissingMethodException`: the dashboard Stats and History endpoints returned 500, and both scheduled sync tasks failed immediately. Recompiled against 10.11.10 and rewrote all seven `_userManager.Users` call sites to use `GetUsers()`.',
      ],
      breaking: [
        '`targetAbi` is now `10.11.9.0`. The Jellyfin plugin catalog will only offer this release to servers running 10.11.9 or newer; Jellyfin 10.11.0 through 10.11.8 servers stay on v1.12.0 (which still works for them).',
      ],
    },
    upgradeNotes:
      'If your Jellyfin server is on 10.11.0 through 10.11.8 the catalog will not offer v1.13.0 by design; upgrade Jellyfin to 10.11.9 or newer to receive this and future plugin updates.',
  },
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
