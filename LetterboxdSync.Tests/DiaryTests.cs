using System;
using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

public class DiaryTests
{
    [Fact]
    public void ParseDiaryDates_ValidHtml_ParsesDates()
    {
        var html = @"<table>
            <tr>
                <td><a class=""month"">Mar</a></td>
                <td><a class=""daydate"">25</a></td>
                <td><a class=""year"">2026</a></td>
            </tr>
            <tr>
                <td><a class=""month"">Feb</a></td>
                <td><a class=""date"">12</a></td>
                <td><a class=""year"">2026</a></td>
            </tr>
        </table>";

        var dates = Helpers.ParseDiaryDates(html);
        Assert.Equal(2, dates.Count);
        Assert.Contains(dates, d => d.Date == new DateTime(2026, 3, 25));
        Assert.Contains(dates, d => d.Date == new DateTime(2026, 2, 12));
    }

    [Fact]
    public void ParseDiaryDates_EmptyHtml_ReturnsEmpty()
    {
        var dates = Helpers.ParseDiaryDates("<html><body></body></html>");
        Assert.Empty(dates);
    }

    [Fact]
    public void ParseDiaryDates_MismatchedCounts_UsesMinimum()
    {
        // 2 months but only 1 day and 1 year
        var html = @"<div>
            <a class=""month"">Jan</a>
            <a class=""month"">Feb</a>
            <a class=""date"">15</a>
            <a class=""year"">2026</a>
        </div>";

        var dates = Helpers.ParseDiaryDates(html);
        Assert.Single(dates);
    }
}

public class RewatchDetectionTests
{
    [Fact]
    public void IsRewatch_NoPriorEntry_False()
    {
        Assert.False(Helpers.IsRewatch(null, DateTime.Now));
    }

    [Fact]
    public void IsRewatch_SameDay_False()
    {
        var today = DateTime.Today;
        Assert.False(Helpers.IsRewatch(today, today));
    }

    [Fact]
    public void IsRewatch_Yesterday_False()
    {
        var today = DateTime.Today;
        Assert.False(Helpers.IsRewatch(today.AddDays(-1), today));
    }

    [Fact]
    public void IsRewatch_TwoDaysAgo_True()
    {
        var today = DateTime.Today;
        Assert.True(Helpers.IsRewatch(today.AddDays(-2), today));
    }

    [Fact]
    public void IsRewatch_MonthsAgo_True()
    {
        var today = DateTime.Today;
        Assert.True(Helpers.IsRewatch(today.AddMonths(-3), today));
    }
}

public class DuplicateDetectionTests
{
    [Fact]
    public void IsDuplicate_SameDate_True()
    {
        var date = new DateTime(2026, 3, 25);
        Assert.True(Helpers.IsDuplicate(date, date));
    }

    [Fact]
    public void IsDuplicate_DifferentDate_False()
    {
        Assert.False(Helpers.IsDuplicate(new DateTime(2026, 3, 24), new DateTime(2026, 3, 25)));
    }

    [Fact]
    public void IsDuplicate_NoPriorEntry_False()
    {
        Assert.False(Helpers.IsDuplicate(null, DateTime.Now));
    }

    [Fact]
    public void IsDuplicate_SameDateDifferentTime_True()
    {
        var morning = new DateTime(2026, 3, 25, 9, 0, 0);
        var evening = new DateTime(2026, 3, 25, 21, 0, 0);
        Assert.True(Helpers.IsDuplicate(morning, evening));
    }
}
