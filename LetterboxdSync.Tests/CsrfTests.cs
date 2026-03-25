using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

public class CsrfTests
{
    [Fact]
    public void ExtractHiddenInput_StandardForm_ExtractsCsrf()
    {
        var html = @"<html><body>
            <form>
                <input type=""hidden"" name=""__csrf"" value=""abc123def456"" />
                <input type=""text"" name=""username"" />
            </form>
        </body></html>";

        var result = LetterboxdClient.ExtractHiddenInput(html, "__csrf");
        Assert.Equal("abc123def456", result);
    }

    [Fact]
    public void ExtractHiddenInput_NotFound_ReturnsNull()
    {
        var html = @"<html><body><p>No form here</p></body></html>";

        var result = LetterboxdClient.ExtractHiddenInput(html, "__csrf");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractHiddenInput_TypicalLetterboxdPage_ExtractsCsrf()
    {
        // Realistic Letterboxd sign-in page snippet
        var html = @"<form method=""post"" action=""/user/login.do"">
            <input type=""hidden"" name=""__csrf"" value=""a1b2c3d4e5f6"" />
            <input type=""text"" name=""username"" />
            <input type=""password"" name=""password"" />
        </form>";

        var result = LetterboxdClient.ExtractHiddenInput(html, "__csrf");
        Assert.Equal("a1b2c3d4e5f6", result);
    }

    [Fact]
    public void ExtractHiddenInput_HtmlEncodedValue_Decodes()
    {
        var html = @"<input type=""hidden"" name=""__csrf"" value=""a&amp;b&lt;c"" />";

        var result = LetterboxdClient.ExtractHiddenInput(html, "__csrf");
        Assert.Equal("a&b<c", result);
    }
}
