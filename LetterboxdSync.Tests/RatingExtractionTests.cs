using HtmlAgilityPack;
using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

public class RatingExtractionTests
{
    private static HtmlNode Container(string innerHtml)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml($"<li class=\"poster-container\">{innerHtml}</li>");
        return doc.DocumentNode.SelectSingleNode("//li");
    }

    [Theory]
    [InlineData(1, 0.5)]
    [InlineData(2, 1.0)]
    [InlineData(5, 2.5)]
    [InlineData(7, 3.5)]
    [InlineData(10, 5.0)]
    public void ExtractRating_RatedClass_ReturnsHalfStars(int classNum, double expected)
    {
        var node = Container(
            $"<div data-film-slug=\"foo\"></div>" +
            $"<p class=\"poster-viewingdata\"><span class=\"rating rated-{classNum}\">★</span></p>");

        Assert.Equal(expected, LetterboxdScraper.ExtractRatingFromContainer(node));
    }

    [Fact]
    public void ExtractRating_NoRatingSpan_ReturnsNull()
    {
        var node = Container("<div data-film-slug=\"foo\"></div>");
        Assert.Null(LetterboxdScraper.ExtractRatingFromContainer(node));
    }

    [Fact]
    public void ExtractRating_OutOfRangeClass_ReturnsNull()
    {
        var node = Container(
            "<div data-film-slug=\"foo\"></div>" +
            "<span class=\"rating rated-99\">junk</span>");

        Assert.Null(LetterboxdScraper.ExtractRatingFromContainer(node));
    }

    [Fact]
    public void ExtractRating_RatedZero_ReturnsNull()
    {
        var node = Container(
            "<div data-film-slug=\"foo\"></div>" +
            "<span class=\"rating rated-0\">unrated</span>");

        Assert.Null(LetterboxdScraper.ExtractRatingFromContainer(node));
    }

    [Fact]
    public void ExtractRating_NonNumericRatedSuffix_ReturnsNull()
    {
        var node = Container(
            "<div data-film-slug=\"foo\"></div>" +
            "<span class=\"rated-foo\">??</span>");

        Assert.Null(LetterboxdScraper.ExtractRatingFromContainer(node));
    }
}
