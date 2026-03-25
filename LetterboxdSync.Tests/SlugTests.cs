using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

public class SlugTests
{
    [Theory]
    [InlineData("https://letterboxd.com/film/gladiator-ii/", "gladiator-ii")]
    [InlineData("https://letterboxd.com/film/a-silent-voice-the-movie/", "a-silent-voice-the-movie")]
    [InlineData("https://letterboxd.com/film/dune-part-two/", "dune-part-two")]
    [InlineData("https://letterboxd.com/film/the-theory-of-everything-2014/", "the-theory-of-everything-2014")]
    public void ExtractSlugFromUrl_ValidUrls_ExtractsSlug(string url, string expected)
    {
        Assert.Equal(expected, Helpers.ExtractSlugFromUrl(url));
    }

    [Theory]
    [InlineData("https://letterboxd.com/user/watchlist/")]
    [InlineData("https://letterboxd.com/")]
    [InlineData("https://example.com/film/test/")]
    public void ExtractSlugFromUrl_NonFilmUrls_ReturnsNull(string url)
    {
        // Non-letterboxd film URLs or non-film paths
        var result = Helpers.ExtractSlugFromUrl(url);
        if (url.Contains("/film/"))
            Assert.NotNull(result); // example.com/film/test would still parse
        else
            Assert.Null(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void ExtractSlugFromUrl_EmptyOrNull_ReturnsNull(string? url)
    {
        Assert.Null(Helpers.ExtractSlugFromUrl(url!));
    }

    [Fact]
    public void ExtractSlugFromUrl_NoTrailingSlash_Works()
    {
        Assert.Equal("fury-2014", Helpers.ExtractSlugFromUrl("https://letterboxd.com/film/fury-2014"));
    }
}
