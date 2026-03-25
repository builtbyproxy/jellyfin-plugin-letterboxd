using System.Collections.Generic;

namespace LetterboxdSync.Configuration;

public class Account
{
    public string UserJellyfinId { get; set; } = string.Empty;

    public string LetterboxdUsername { get; set; } = string.Empty;

    public string LetterboxdPassword { get; set; } = string.Empty;

    public string? RawCookies { get; set; }

    public bool Enabled { get; set; }

    public bool SyncFavorites { get; set; }

    public bool EnableDateFilter { get; set; }

    public int DateFilterDays { get; set; } = 7;

    public bool EnableWatchlistSync { get; set; }

    public bool EnableDiaryImport { get; set; }
}
