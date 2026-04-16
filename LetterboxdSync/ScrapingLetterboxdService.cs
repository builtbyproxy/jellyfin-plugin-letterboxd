using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public class ScrapingLetterboxdService : ILetterboxdService
{
    private readonly LetterboxdHttpClient _http;
    private readonly LetterboxdAuth _auth;
    private readonly LetterboxdScraper _scraper;
    private readonly LetterboxdDiary _diary;

    public ScrapingLetterboxdService(ILogger logger)
    {
        _http = new LetterboxdHttpClient(logger);
        _auth = new LetterboxdAuth(_http, logger);
        _scraper = new LetterboxdScraper(_http, logger);
        _diary = new LetterboxdDiary(_http, _auth, _scraper, logger);
    }

    public async Task AuthenticateAsync(string username, string password, string? rawCookies = null)
    {
        _http.SetRawCookies(rawCookies);
        await _auth.AuthenticateAsync(username, password).ConfigureAwait(false);
    }

    public Task<FilmResult> LookupFilmByTmdbIdAsync(int tmdbId)
        => _scraper.LookupFilmByTmdbIdAsync(tmdbId);

    public Task<DiaryInfo> GetDiaryInfoAsync(string filmIdOrSlug, string username)
        => _scraper.GetDiaryInfoAsync(filmIdOrSlug, username);

    public Task MarkAsWatchedAsync(string filmSlug, string filmId, DateTime? date, bool liked,
        string? productionId = null, bool rewatch = false, double? rating = null)
        => _diary.MarkAsWatchedAsync(filmSlug, filmId, date, liked, productionId, rewatch, rating);

    public Task PostReviewAsync(string filmSlug, string? reviewText, bool containsSpoilers = false,
        bool isRewatch = false, string? date = null, double? rating = null, int? tmdbId = null)
        => _diary.PostReviewAsync(filmSlug, reviewText, containsSpoilers, isRewatch, date, rating);

    public Task<List<int>> GetWatchlistTmdbIdsAsync(string username)
        => _scraper.GetWatchlistTmdbIdsAsync(username);

    public Task<List<int>> GetDiaryTmdbIdsAsync(string username)
        => _scraper.GetDiaryTmdbIdsAsync(username);

    public void Dispose()
    {
        _http.Dispose();
    }
}
