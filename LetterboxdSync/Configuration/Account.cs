using System.Collections.Generic;

namespace LetterboxdSync.Configuration;

public class Account
{
    public string UserJellyfinId { get; set; } = string.Empty;

    public string LetterboxdUsername { get; set; } = string.Empty;

    public string LetterboxdPassword { get; set; } = string.Empty;

    public string? RawCookies { get; set; }

    public string? UserAgent { get; set; }

    public bool Enabled { get; set; }

    public bool SyncFavorites { get; set; }

    public bool EnableDateFilter { get; set; }

    public int DateFilterDays { get; set; } = 7;

    public bool EnableWatchlistSync { get; set; }

    public bool EnableDiaryImport { get; set; }

    public bool AutoRequestWatchlist { get; set; }

    /// <summary>
    /// When true, <see cref="AutoRequestWatchlist"/> also creates an attributed Jellyseerr
    /// request for watchlisted films that are already in the library / available, as long as
    /// this user has no existing request for them. This backfills a requester trail for films
    /// that entered the library outside Jellyseerr (manual Radarr add, deleted request, etc.)
    /// so "who wanted this?" is answerable. Off by default: it creates request rows for
    /// already-available media (harmless for downloads, Radarr already has the file).
    /// </summary>
    public bool BackfillAvailableRequests { get; set; }

    public bool MirrorJellyseerrWatchlist { get; set; }

    public bool SkipPreviouslySynced { get; set; } = true;

    public bool StopOnFailure { get; set; }

    /// <summary>
    /// When a Jellyfin user has multiple Letterboxd accounts, the primary one is used to:
    /// (1) resolve rating conflicts on diary import (primary's rating wins), and
    /// (2) preselect the default option in manual UI dropdowns (review modal, sync buttons).
    /// Auto-sync paths still fan out to all enabled accounts; this flag does not narrow them.
    /// At most one account per UserJellyfinId should be primary; the loader auto-promotes
    /// the first enabled account if none is marked.
    /// </summary>
    public bool IsPrimary { get; set; }

    /// <summary>
    /// Optional override for the watchlist playlist name. When null, defaults to
    /// "Letterboxd Watchlist ({LetterboxdUsername})" so each account gets its own playlist.
    /// </summary>
    public string? PlaylistName { get; set; }
}
