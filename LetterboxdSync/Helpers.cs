using System;
using System.Collections.Generic;
using System.Globalization;
using HtmlAgilityPack;

namespace LetterboxdSync;

public static class Helpers
{
    /// <summary>
    /// Map a Jellyfin rating (0-10) to a Letterboxd rating (0.5-5.0 in 0.5 steps).
    /// Returns null if the input is null or out of range.
    /// </summary>
    public static double? MapRating(double? jellyfinRating)
    {
        if (!jellyfinRating.HasValue || jellyfinRating.Value <= 0)
            return null;

        var mapped = Math.Round(jellyfinRating.Value / 2.0 * 2) / 2.0;
        return Math.Clamp(mapped, 0.5, 5.0);
    }

    /// <summary>
    /// Map a Letterboxd rating (0.5-5.0) to a Jellyfin rating (1-10).
    /// Returns null if the input is null or out of range.
    /// </summary>
    public static double? LetterboxdToJellyfinRating(double? letterboxdRating)
    {
        if (!letterboxdRating.HasValue || letterboxdRating.Value <= 0)
            return null;

        return Math.Clamp(letterboxdRating.Value * 2.0, 1.0, 10.0);
    }

    /// <summary>
    /// Extract a film slug from a Letterboxd film URL.
    /// e.g. "https://letterboxd.com/film/gladiator-ii/" -> "gladiator-ii"
    /// </summary>
    public static string? ExtractSlugFromUrl(string filmUrl)
    {
        if (string.IsNullOrWhiteSpace(filmUrl))
            return null;

        try
        {
            var uri = new Uri(filmUrl, UriKind.Absolute);
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length >= 2 && segments[0].Equals("film", StringComparison.OrdinalIgnoreCase))
                return segments[1];
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Parse diary entry dates from a Letterboxd diary page HTML.
    /// Returns a list of dates found.
    /// </summary>
    public static List<DateTime> ParseDiaryDates(string html)
    {
        var dates = new List<DateTime>();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var months = doc.DocumentNode.SelectNodes("//a[contains(@class, 'month')]");
        var days = doc.DocumentNode.SelectNodes("//a[contains(@class, 'date') or contains(@class, 'daydate')]");
        var years = doc.DocumentNode.SelectNodes("//a[contains(@class, 'year')]");

        if (months == null || days == null || years == null)
            return dates;

        var count = Math.Min(Math.Min(months.Count, days.Count), years.Count);
        for (int i = 0; i < count; i++)
        {
            var dateStr = $"{days[i].InnerText?.Trim()} {months[i].InnerText?.Trim()} {years[i].InnerText?.Trim()}";
            if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                dates.Add(parsed);
        }

        return dates;
    }

    /// <summary>
    /// Determine if a viewing should be marked as a rewatch.
    /// Only true if there's a prior diary entry AND it's more than 1 day before the viewing date.
    /// </summary>
    public static bool IsRewatch(DateTime? lastDiaryDate, DateTime viewingDate)
    {
        if (!lastDiaryDate.HasValue)
            return false;

        return (viewingDate.Date - lastDiaryDate.Value.Date).TotalDays > 1;
    }

    /// <summary>
    /// Determine if a viewing is a duplicate (already logged on the same date).
    /// </summary>
    public static bool IsDuplicate(DateTime? lastDiaryDate, DateTime viewingDate)
    {
        if (!lastDiaryDate.HasValue)
            return false;

        return lastDiaryDate.Value.Date == viewingDate.Date;
    }
}
