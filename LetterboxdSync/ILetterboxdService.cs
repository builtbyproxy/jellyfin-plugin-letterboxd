using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LetterboxdSync;

public interface ILetterboxdService : IDisposable
{
    Task AuthenticateAsync(string username, string password, string? rawCookies = null);

    Task<FilmResult> LookupFilmByTmdbIdAsync(int tmdbId);

    Task<DiaryInfo> GetDiaryInfoAsync(string filmIdOrSlug, string username);

    Task MarkAsWatchedAsync(string filmSlug, string filmId, DateTime? date, bool liked,
        string? productionId = null, bool rewatch = false, double? rating = null);

    Task PostReviewAsync(string filmSlug, string? reviewText, bool containsSpoilers = false,
        bool isRewatch = false, string? date = null, double? rating = null);

    Task<List<int>> GetWatchlistTmdbIdsAsync(string username);

    Task<List<int>> GetDiaryTmdbIdsAsync(string username);
}
