using System;
using System.Linq;
using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Targeted tests for Helpers paths missed by the existing rating-focused suite.
/// Covers ExtractSlugFromUrl edge cases (the catch branch when Uri parse throws),
/// ParseDiaryDates (used by the scraper diary path), and IsRewatch/IsDuplicate
/// boundary conditions.
/// </summary>
public class HelpersAdditionalTests
{
    [Fact]
    public void ExtractSlugFromUrl_ValidLetterboxdFilmUrl_ReturnsSlug()
    {
        Assert.Equal("sinners-2025",
            Helpers.ExtractSlugFromUrl("https://letterboxd.com/film/sinners-2025/"));
    }

    [Fact]
    public void ExtractSlugFromUrl_NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(Helpers.ExtractSlugFromUrl(""));
        Assert.Null(Helpers.ExtractSlugFromUrl("   "));
        Assert.Null(Helpers.ExtractSlugFromUrl(null!));
    }

    [Fact]
    public void ExtractSlugFromUrl_NotAFilmUrl_ReturnsNull()
    {
        Assert.Null(Helpers.ExtractSlugFromUrl("https://letterboxd.com/8bitproxy/"));
        Assert.Null(Helpers.ExtractSlugFromUrl("https://letterboxd.com/"));
    }

    [Fact]
    public void ExtractSlugFromUrl_MalformedUri_ReturnsNullViaCatch()
    {
        // Triggers the catch{} branch in ExtractSlugFromUrl: Uri ctor throws on
        // a clearly invalid absolute URL (no scheme + relative path).
        Assert.Null(Helpers.ExtractSlugFromUrl("not a url"));
    }

    [Fact]
    public void ExtractSlugFromUrl_FilmCaseInsensitive_ReturnsSlug()
    {
        Assert.Equal("dune-part-two",
            Helpers.ExtractSlugFromUrl("https://letterboxd.com/FILM/dune-part-two/"));
    }

    [Fact]
    public void IsRewatch_NoPriorEntry_False()
    {
        Assert.False(Helpers.IsRewatch(null, DateTime.Today));
    }

    [Fact]
    public void IsRewatch_PriorEntryWithinDay_False()
    {
        // Same-day or next-day re-watch is treated as "today's watch", not a rewatch.
        var today = DateTime.Today;
        Assert.False(Helpers.IsRewatch(today, today));
        Assert.False(Helpers.IsRewatch(today.AddDays(-1), today));
    }

    [Fact]
    public void IsRewatch_PriorEntryOlderThanOneDay_True()
    {
        var today = DateTime.Today;
        Assert.True(Helpers.IsRewatch(today.AddDays(-2), today));
        Assert.True(Helpers.IsRewatch(today.AddYears(-1), today));
    }

    [Fact]
    public void IsDuplicate_SameDay_True()
    {
        var noon = DateTime.Today.AddHours(12);
        Assert.True(Helpers.IsDuplicate(noon, noon));
        // Same-day at different times of day still counts (we compare .Date).
        Assert.True(Helpers.IsDuplicate(noon.AddHours(-3), noon.AddHours(5)));
    }

    [Fact]
    public void IsDuplicate_DifferentDays_False()
    {
        var today = DateTime.Today;
        Assert.False(Helpers.IsDuplicate(today.AddDays(-1), today));
        Assert.False(Helpers.IsDuplicate(null, today));
    }

    [Fact]
    public void ParseDiaryDates_EmptyHtml_ReturnsEmpty()
    {
        Assert.Empty(Helpers.ParseDiaryDates("<html></html>"));
    }

    [Fact]
    public void ParseDiaryDates_NoDateElements_ReturnsEmpty()
    {
        Assert.Empty(Helpers.ParseDiaryDates(
            "<html><body><div>no dates here</div></body></html>"));
    }

    [Fact]
    public void ParseDiaryDates_OneEntry_ReturnsOneDate()
    {
        // Letterboxd diary HTML has separate links for month/day/year that the
        // helper joins and parses. This minimal shape mirrors the live structure.
        var html = "<html><body>" +
                   "<a class='month'>Mar</a><a class='date'>15</a><a class='year'>2026</a>" +
                   "</body></html>";

        var dates = Helpers.ParseDiaryDates(html).ToList();

        Assert.Single(dates);
        Assert.Equal(new DateTime(2026, 3, 15), dates[0]);
    }

    [Fact]
    public void ParseDiaryDates_MultipleEntries_ReturnsAllInOrder()
    {
        var html = "<html><body>" +
                   "<a class='month'>Mar</a><a class='date'>15</a><a class='year'>2026</a>" +
                   "<a class='month'>Apr</a><a class='date'>1</a><a class='year'>2026</a>" +
                   "</body></html>";

        var dates = Helpers.ParseDiaryDates(html).ToList();

        Assert.Equal(2, dates.Count);
        Assert.Contains(new DateTime(2026, 3, 15), dates);
        Assert.Contains(new DateTime(2026, 4, 1), dates);
    }

    [Fact]
    public void ParseDiaryDates_MismatchedCounts_StopsAtMin()
    {
        // Two months, one date, three years → only one full triple is parseable.
        var html = "<html><body>" +
                   "<a class='month'>Mar</a><a class='month'>Apr</a>" +
                   "<a class='date'>15</a>" +
                   "<a class='year'>2026</a><a class='year'>2025</a><a class='year'>2024</a>" +
                   "</body></html>";

        var dates = Helpers.ParseDiaryDates(html).ToList();

        Assert.Single(dates);
    }
}
