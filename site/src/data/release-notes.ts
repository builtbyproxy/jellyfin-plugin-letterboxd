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
    version: '2.3.1',
    headline: 'Manually marking something watched no longer logs it to 1970',
    summary:
      'Films and episodes that Jellyfin marks watched through real playback have always synced with the correct date. But checking a title watched by hand, using the checkmark rather than actually playing it, could leave Jellyfin\'s own watched-date field set to January 1st 1970 instead of a real date, and the plugin trusted that value as-is. The fix treats an epoch-adjacent watched date the same way it already treats a missing one: there\'s no real watch date to log yet, so the sync waits rather than posting a bogus entry to Letterboxd or Serializd. Once Jellyfin records an actual playback date for the title, it syncs normally.',
    highlights: {
      fixes: [
        'Manually marking a film or episode watched no longer risks logging it to Letterboxd or Serializd with a "seen on 1970-01-01" date.',
        'Titles affected by this are simply skipped (not synced with a fabricated date) until Jellyfin records a real watch date, matching how the plugin already handles a missing watched date.',
      ],
    },
  },
  {
    version: '2.3.0',
    headline: 'Broken Letterboxd logins now pause themselves and tell you about it',
    summary:
      'When a Letterboxd password went stale, the plugin used to retry the login on every scheduled run, forever, and the only place you could find out was the plugin\'s own dashboard. On a set-and-forget server that meant weeks of silent failures, and the constant retries could even trip Letterboxd\'s rate limiting for accounts that still worked. Now, after three consecutive failed logins, syncing for that account pauses itself: no more login attempts, a warning lands in Jellyfin\'s activity log where admins actually look, and the account card in both dashboards shows a "login failing" badge. Re-save the account\'s credentials and syncing resumes on the next run, and nothing is lost in the meantime, the scheduled catch-up picks up any watches from while it was paused.',
    highlights: {
      new: [
        'After three consecutive failed Letterboxd logins, syncing for that account pauses automatically instead of retrying forever, protecting you from rate limiting.',
        'The pause is announced in Jellyfin\'s activity log, so admins see it without opening the plugin dashboard.',
        'Account cards on both the admin and user pages show a "Login failing · sync paused" badge with the date it started.',
        'Re-saving the account\'s credentials (or any successful login) resumes syncing; watches from the paused period are picked up by the next catch-up run.',
      ],
    },
  },
  {
    version: '2.2.0',
    headline: 'The review modal now shows the rating you already gave, and stars come in halves',
    summary:
      'Opening the review modal used to always start at "No rating", even when Jellyfin already knew exactly what you\'d rated the film or episode, whether you rated it in the Jellyfin web UI, a client app like Infuse, or through an earlier Jellyscribe review. Now the modal looks up your stored rating and pre-fills the stars, so you\'re editing your rating rather than re-entering it. The star widget also finally does half stars: click the left half of a star for a .5 rating, matching what Letterboxd itself supports. Ratings on Jellyfin\'s 10-point scale (like a 7/10) now display faithfully as 3.5 stars instead of not showing at all.',
    highlights: {
      new: [
        'The review modal pre-fills its stars from the rating already stored in Jellyfin, for films, shows, and individual episodes, whichever app you rated them in.',
        'Half-star ratings: click the left half of a star for .5 increments, on both the admin dashboard and the user page. Letterboxd reviews carry the half star as-is; Serializd reviews map it onto the 10-point scale.',
      ],
    },
  },
  {
    version: '2.1.2',
    headline: 'Sidebar link now reliably survives a Jellyfin restart',
    summary:
      'The sidebar link this plugin adds to Jellyfin\'s nav is injected by registering with the File Transformation plugin once, at Jellyfin startup. That registration was racing File Transformation\'s own startup init, so on some restarts (including scheduled ones) it silently failed and the link just never appeared, with nothing in the logs beyond a generic, undiagnosable error. Registration now retries a few times before giving up, and a real failure logs the actual cause instead of a useless wrapper message.',
    highlights: {
      fixes: [
        'Sidebar link registration now retries (with a short delay) instead of giving up after a single attempt, so it no longer depends on winning a startup race against the File Transformation plugin on every Jellyfin restart.',
        'A registration failure now logs the real underlying exception instead of reflection\'s generic "Exception has been thrown by the target of an invocation" message.',
      ],
    },
  },
  {
    version: '2.1.1',
    headline: 'Watchlist auto-requests now show up in your sync history',
    summary:
      'If your watchlist syncs with "auto-request via Jellyseerr" turned on, a successful request used to only ever show up in the server log, there was no way to see it from the dashboard itself. Now every title your watchlist successfully requests (film or TV) gets its own entry in your sync history, with the real title, right alongside your regular Letterboxd and Serializd activity. A request that fails shows up too, so if Jellyseerr rejects something you can see exactly what and when instead of wondering why your watchlist "isn\'t working."',
    highlights: {
      new: [
        'Sync history gets a new "Requested" entry for every title your watchlist successfully auto-requests via Jellyseerr, film or TV, with its real title.',
        'Failed auto-requests show up too, so a Jellyseerr rejection is visible on the dashboard instead of only in the server log.',
        'The Stats page counts these separately from your regular sync activity.',
      ],
    },
  },
  {
    version: '2.1.0',
    headline: 'Telemetry now covers TV syncs, the dashboard credit is back, and Logs actually refresh',
    summary:
      'A few follow-ups to the 2.0.0 rebrand. The admin and user dashboard headers lost their "by Lachlan Young and AI" credit line during the redesign, it\'s back. The opt-in anonymous telemetry, which only ever counted Letterboxd film syncs, now counts Serializd TV syncs and errors too, since that pipeline was never touched when TV support was built. And the Logs tab\'s Refresh button, which had been dumping a raw JSON blob into the panel instead of readable log lines, now shows the actual logs.',
    highlights: {
      fixes: [
        'Restored the "by Lachlan Young and AI" credit line in both the admin and user dashboard headers.',
        'Opt-in anonymous telemetry now tracks Serializd (TV) syncs and errors independently from the Letterboxd (film) side, instead of only ever reporting film activity.',
        'Fixed the Logs tab\'s Refresh button showing the raw JSON response instead of readable log lines.',
      ],
    },
  },
  {
    version: '2.0.0',
    headline: 'Renamed to Jellyscribe, and TV shows now sync to Serializd',
    summary:
      'Two things land together in this release: the plugin is renamed from "Letterboxd Sync" to Jellyscribe, and it now syncs TV shows to Serializd alongside films to Letterboxd, as a fully independent second diary.\n\nNothing about the rename requires any action, every linked account, sync history entry, and setting carries over untouched. The two diaries never affect each other, so a hiccup on one never touches the other. Details below.',
    highlights: {
      new: [
        'Renamed to Jellyscribe: new display name, new bookmark-ribbon mark, new site palette. Auto-updates in place, no reinstall or reconfiguration, same plugin GUID and repository URL as always.',
        'New TV / Serializd tab: link a Serializd account per Jellyfin user (email or username) with a Verify login button that confirms your credentials before saving.',
        'Real-time episode sync: finishing a TV episode logs it to your Serializd watched list automatically, matched by TMDb id so it works regardless of naming, dated to the actual watch time.',
        'Daily catch-up scheduled task ("Sync watched TV to Serializd") logs anything the real-time path missed, plus a Sync TV Now button to run it immediately.',
        'Series ratings and favorites sync to Serializd as a show rating and like; write a full review (with an optional rating) straight from the dashboard, at the show or a specific episode.',
        'Serializd watchlist sync: watchlisted shows mirror into a Jellyfin collection (browse) and the watchlisted seasons\' episodes into a playlist (play queue), with an opt-in Jellyseerr auto-request for anything missing.',
        'Reverse diary import: mark Jellyfin episodes played when they already show up in your Serializd diary.',
        'Serializd runs fully isolated from Letterboxd: films keep syncing to Letterboxd, and a failure on one service never blocks the other.',
      ],
    },
  },
  {
    version: '1.19.6',
    headline: 'Reverse sync now explains itself when it has nothing to do',
    summary:
      'If the "Import Letterboxd diary as Jellyfin watched" task ran and did nothing, it used to finish in a second and leave no trace in the log, so a switched-off toggle looked identical to a broken feature. The task now says why it skipped each user (for example, an account exists but diary import is not turned on, with a pointer to the setting), reports when your Letterboxd returned no films, and always ends with a one-line summary of what it checked. A stuck progress banner on the dashboard for empty diaries is also fixed. If reverse sync has not been working for you, update, run the task once, and the log will now tell you exactly why.',
    highlights: {
      fixes: [
        'The diary import task logs the reason whenever it skips a user, reports empty results, and always writes a completion summary, so a run that does nothing is explainable from the log alone.',
        'Fixed the dashboard progress banner staying active forever when a diary import found no films to import.',
      ],
    },
  },
  {
    version: '1.19.5',
    headline: 'Send logs to developer now tells you when there is nothing to send',
    summary:
      'A user sent a diagnostic bundle that arrived completely empty: the plugin had not logged anything recently, so there was nothing to collect, but the dialog still reported plain success and neither side could tell why the bundle was blank. Now the send dialog warns you when no plugin log lines were found (with a hint to reproduce the problem first and try again), and every bundle carries a small status line saying which log files were read and how many lines matched, so an empty bundle explains itself.',
    highlights: {
      fixes: [
        'The "Send logs to developer" dialog now shows a clear warning when the bundle contains no log lines, instead of reporting bare success on an empty send.',
        'Diagnostic bundles now include a collector status line (files read, lines matched, any read error), so support can tell an idle plugin from a broken log reader.',
      ],
    },
  },
  {
    version: '1.19.4',
    headline: 'Housekeeping: a warning-clean test build',
    summary:
      'No functional changes. The plugin\u2019s test suite compiled with seven compiler and analyzer warnings (a couple of async test methods that never awaited anything, one fire-and-forget mock setup, and a few assertions written the long way around). All seven are fixed, so the build is warning-clean again and future warnings stand out instead of drowning in noise. Nothing about syncing, accounts, or the dashboard changes in this release.',
    highlights: {
      fixes: [
        'Cleaned up all seven compiler and analyzer warnings in the test project; every touched test was verified to still fail when its assertion is broken, so no coverage was lost.',
      ],
    },
  },
  {
    version: '1.19.3',
    headline: 'Telemetry can now tell a quiet week from a plugin that never worked',
    summary:
      'A small improvement to the opt-in anonymous telemetry: alongside the existing per-week sync bucket, the weekly ping now includes a lifetime sync bucket. Until now, an instance reporting zero syncs in a week was indistinguishable from one where syncing never worked at all; the lifetime bucket separates the two, so setup problems can be spotted and fixed. Same privacy rules as everything else: a coarse bucket only (0, 1-10, 11-100, 100+), never exact numbers, and nothing is sent unless you opted in.',
    highlights: {
      improvements: [
        'Weekly telemetry pings gain a syncs_ever bucket: the lifetime count of successful syncs, reported in the same coarse buckets as all other counts. This distinguishes a healthy-but-idle instance from an install where syncing has never succeeded.',
      ],
    },
  },
  {
    version: '1.19.2',
    headline: 'Stored credentials are now encrypted at rest',
    summary:
      'Your Letterboxd password, session cookies, and Seerr API key are now encrypted before they are written to disk, instead of sitting in the plugin config file as plaintext. Nothing changes about how you use the plugin: existing saved credentials are picked up and upgraded automatically the next time they are saved, no re-entry needed.',
    highlights: {
      improvements: [
        'Letterboxd password, raw cookies, and Seerr API key are encrypted at rest using ASP.NET Core Data Protection, with the key kept alongside the plugin’s other data files.',
        'Existing plaintext values from earlier versions are read normally and silently upgraded to encrypted on the next save.',
      ],
    },
  },
  {
    version: '1.19.1',
    headline: 'AI transparency page and a code of conduct that fits',
    summary:
      'No functional changes to syncing. The project now documents openly how it is built: a new AI.md page explains that most of the plugin is AI-written under human direction and review, what tooling is used, and the checks every change passes before it ships. The Code of Conduct is rewritten to something honest for a one-person project, and the README links to all of it.',
    highlights: {
      improvements: [
        'New AI transparency page (AI.md in the repository) covering the AI tooling, the human review and CI gates, and the concrete scale of what has been built.',
        'Code of Conduct rewritten for a solo-maintained project, with reports directed to GitHub issues.',
        'Documentation cleanup: consistent punctuation across the README, website release notes, and code comments.',
      ],
    },
  },
  {
    version: '1.19.0',
    headline: 'A faster mirror joins your plugin catalog',
    summary:
      'A second repository entry for this plugin, an edge-cached mirror of the exact same manifest, is added to your Jellyfin catalog next to the existing GitHub one, a single time. Your GitHub entry is never touched, so updates keep working even if the mirror is unreachable, and if you would rather not have the mirror you can simply delete it: the plugin will not add it back. The mirror counts an anonymous, weekly-rotating hash per request so we can see roughly how many servers run the plugin; no IP addresses or personal data are ever stored, and this is entirely separate from the opt-in telemetry.',
    highlights: {
      improvements: [
        'The edge-cached manifest mirror is added to your plugin catalog alongside the GitHub entry (named after your existing entry with a "(mirror)" suffix, matching its enabled state). It serves the identical manifest and redirects downloads to the official GitHub releases. This runs once: deleting the mirror entry is respected and it is never re-added.',
        'The mirror no longer caches upstream GitHub errors at the edge, so a brief GitHub hiccup cannot pin a failed manifest response for other users.',
      ],
    },
  },
  {
    version: '1.18.5',
    headline: 'Anonymous install counter',
    summary:
      'No functional changes to syncing. This release adds an anonymous, privacy-preserving install counter so the developer can see roughly how many Jellyfin servers run the plugin. No personal data is collected: your IP address is never stored, and the anonymous weekly fingerprint used to avoid double-counting cannot be traced back to you or linked across weeks.',
    highlights: {
      new: [
        'Active installs are now counted from ordinary catalog update checks and release downloads served through an edge mirror of the official manifest. GitHub download totals were useless for this (re-downloads and auto-updates inflate them), and the opt-in telemetry only sees servers that enabled it.',
        'Uniqueness is approximated with a weekly-rotating anonymous fingerprint: your IP address is used transiently at the edge to compute it and is never stored, and counts cannot be linked across weeks. Unlike the opt-in telemetry, this counting is always on; it carries no other information about you, your server, or your library.',
      ],
      improvements: [
        'Release downloads in the plugin catalog now redirect through the mirror to the identical GitHub release file. Checksums are unchanged, so Jellyfin\'s integrity check on install and update passes exactly as before.',
      ],
    },
  },
  {
    version: '1.18.4',
    headline: 'Sync works again after a Letterboxd page change',
    summary:
      'Fixes a sync failure introduced when Letterboxd changed their website. Films stopped logging to your diary because the plugin could no longer read each film\'s identifier from its Letterboxd page. The plugin now reads the new page format, so watched films sync again.',
    highlights: {
      fixes: [
        'Letterboxd removed the film-page attributes the plugin\'s web-scraping fallback used to identify films, moving the identifiers into a new format on the poster element. The plugin now reads the new format (and still understands the old one). Users on the official API path were unaffected; anyone on the scraping fallback had every diary sync fail until this release.',
        'A live test now exercises the scraping path against the real Letterboxd site, so the next markup change of this kind is caught by CI instead of by user bug reports.',
      ],
    },
  },
  {
    version: '1.18.3',
    headline: 'More precise error reporting in telemetry',
    summary:
      'If you have opted in to the anonymous telemetry, plugin errors are now sorted into more specific categories instead of a single generic "other" bucket. Rate-limiting, Letterboxd server outages, failed writes back to Letterboxd, and page-parsing problems are each labelled distinctly. This is purely a labelling improvement: no behaviour changes, and nothing new about you or your library is collected.',
    highlights: {
      improvements: [
        'Anonymous telemetry now distinguishes four previously-uncategorised error types: rate-limiting (being throttled by Letterboxd), server errors (Letterboxd returning a 5xx), write failures (a watch or review that could not be logged after retries), and parsing errors (a Letterboxd page that no longer matches what the plugin expects). Previously these all counted as "other".',
      ],
      fixes: [
        'Fixed the error-state recovery so that, after a successful sync, every error category is cleared. One category was previously skipped, which could leave a stale "still failing" flag set.',
      ],
    },
  },
  {
    version: '1.18.2',
    headline: 'Jellyseerr is now called Seerr',
    summary:
      'Jellyseerr and Overseerr have merged into a single project called Seerr. The plugin now uses the new name everywhere you see it, in the settings page, your personal account page, and the documentation, so the labels match what you see in the app you are connecting to. Nothing about the integration changes: your existing Seerr URL, API key, and per-account settings are kept, and there is nothing to reconfigure.',
    highlights: {
      improvements: [
        'The "Jellyseerr integration" settings, the auto-request and watchlist-mirror options, and the connection-test messages now all read "Seerr" to match the renamed project. Your saved URL, API key, and account settings carry over untouched.',
      ],
    },
  },
  {
    version: '1.18.1',
    headline: 'Internal: test analytics in CI',
    summary:
      'No functional changes. CI now uploads JUnit test results to Codecov Test Analytics so flaky tests and failures are tracked across releases. Nothing in the plugin itself changes.',
    highlights: {
      improvements: [
        'CI emits a JUnit test report and uploads it to Codecov Test Analytics for flaky-test detection and failure reporting. Developer-facing only; no change to plugin behaviour.',
      ],
    },
  },
  {
    version: '1.18.0',
    headline: 'Clearer guidance when Letterboxd login fails',
    summary:
      'Letterboxd often blocks plain username/password login with a Cloudflare or reCAPTCHA challenge, and accounts with two-factor authentication can never log in that way at all. Telemetry showed this is the single most common reason a sync fails on a fresh install. This release makes raw cookies the clearly recommended way to authenticate, warns you when you save an account without them, and turns the cryptic two-factor login failure into a message that tells you exactly what to do.',
    highlights: {
      improvements: [
        'Raw Cookies are now presented as the recommended way to authenticate, on both the admin settings page and your personal account page, with a short explanation of why password-only login is unreliable (Cloudflare/reCAPTCHA challenges, and two-factor accounts that cannot use it at all).',
        'Saving an enabled account that has a password but no raw cookies now shows a non-blocking warning, so you find out before the first sync fails rather than after. The save still goes through.',
      ],
      fixes: [
        'Accounts with two-factor authentication enabled now fail login with a clear, actionable message ("this account has two-factor authentication enabled, add raw cookies including cf_clearance, or disable 2FA") instead of a vague generic login error. Password login genuinely cannot complete a 2FA challenge, so the plugin now says so and points you at the fix.',
      ],
    },
    upgradeNotes:
      'If your syncs have been failing with auth errors, open your Letterboxd Sync account settings and paste Raw Cookies (including cf_clearance) from a browser already signed in to Letterboxd. This is now the recommended setup, especially for accounts with two-factor authentication.',
  },
  {
    version: '1.17.0',
    headline: 'Send your logs to the developer in one click',
    summary:
      'Reporting a problem used to mean digging through Jellyfin log files and pasting the right lines into a GitHub issue. Now there is a "Send logs to developer" button on the Logs tab: it packages your recent Letterboxd Sync activity, uploads it privately, and hands you a short reference code to quote in your bug report. It is opt-in every single time you use it, shows you exactly what will be sent before anything leaves your server, and is upfront that logs, unlike the anonymous telemetry, can identify you. This release also tightens who on your server is allowed to read those logs.',
    highlights: {
      new: [
        'Send logs to developer: open Dashboard, then Letterboxd Sync, then the Logs tab, and click "Send logs to developer". The plugin bundles the recent Letterboxd Sync log lines (the same ones already shown on that tab) and uploads them, then gives you a short reference code like LBX-7Q2F9K. Quote that code when you open a GitHub issue and the developer can pull your exact logs to diagnose the problem, no more copy-pasting walls of text.',
        'See exactly what is sent, before it is sent. The confirmation step has a "Preview exactly what is sent" view that renders the real log lines and the telemetry snapshot, byte for byte what gets uploaded. You can also add a short note describing what went wrong, which is often the single most useful thing for diagnosis.',
        'Honest about privacy. Unlike the anonymous weekly telemetry, logs are NOT anonymous: they can contain a Letterboxd username or film titles, and the bundle is linked to the telemetry ID for that install. The dialog says this plainly, nothing is sent until you confirm, passwords and cookies and auth tokens are never logged, and it works whether or not anonymous telemetry is turned on.',
        'Your logs are not kept forever. Uploaded bundles are stored privately and automatically deleted after 90 days.',
      ],
      improvements: [
        'Stronger log access control. The plugin log endpoint now requires administrator access. Previously any signed-in Jellyfin user on the server could read the Letterboxd Sync logs, which name every linked Letterboxd account and watched film. On shared or family servers this is now admin-only.',
        'Better diagnostics when you do send logs: full multi-line error traces are now captured intact (they were previously cut into fragments), log text is cleaned of stray terminal control codes, and the bundle always records your Jellyfin version.',
      ],
    },
    upgradeNotes:
      'Nothing changes unless you choose to use the new button. To send logs, go to Dashboard, then Letterboxd Sync, then the Logs tab, and click "Send logs to developer" (admin only). Anonymous telemetry, if you enabled it in 1.16.0, is unaffected.',
  },
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
  },
  {
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
        'User self-service account setup, users link their own Letterboxd account without admin help.',
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
        'Real-time playback sync via PlaybackHandler, diary entries land within seconds of credits rolling.',
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
