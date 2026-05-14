using System.Collections.Generic;

namespace LetterboxdSync.Api;

public class AccountUpdateRequest
{
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

    public bool MirrorJellyseerrWatchlist { get; set; }

    public bool SkipPreviouslySynced { get; set; } = true;

    public bool StopOnFailure { get; set; }

    public bool IsPrimary { get; set; }

    public string? PlaylistName { get; set; }
}

/// <summary>
/// Bulk-replace payload for the per-user multi-account endpoint. The caller submits
/// the full set of accounts they want for themselves; the server ignores any
/// UserJellyfinId in the request and stamps every entry with the calling user.
/// </summary>
public class AccountsUpdateRequest
{
    public List<AccountUpdateRequest> Accounts { get; set; } = new List<AccountUpdateRequest>();
}

public class TestConnectionRequest
{
    public string LetterboxdUsername { get; set; } = string.Empty;

    public string LetterboxdPassword { get; set; } = string.Empty;

    public string? RawCookies { get; set; }

    public string? UserAgent { get; set; }
}

public class JellyseerrTestRequest
{
    public string? Url { get; set; }
    public string? ApiKey { get; set; }
}
