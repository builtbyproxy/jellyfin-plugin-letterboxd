using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

public class RatingTests
{
    [Theory]
    [InlineData(10.0, 5.0)]
    [InlineData(9.0, 4.5)]
    [InlineData(8.0, 4.0)]
    [InlineData(7.0, 3.5)]
    [InlineData(6.0, 3.0)]
    [InlineData(5.0, 2.5)]
    [InlineData(4.0, 2.0)]
    [InlineData(3.0, 1.5)]
    [InlineData(2.0, 1.0)]
    [InlineData(1.0, 0.5)]
    public void MapRating_WholeNumbers_MapsCorrectly(double input, double expected)
    {
        Assert.Equal(expected, Helpers.MapRating(input));
    }

    [Theory]
    [InlineData(8.5, 4.0)]
    [InlineData(7.3, 3.5)]
    [InlineData(6.8, 3.5)]
    [InlineData(5.5, 3.0)]
    [InlineData(3.7, 2.0)]
    public void MapRating_FractionalValues_RoundsToNearestHalf(double input, double expected)
    {
        Assert.Equal(expected, Helpers.MapRating(input));
    }

    [Fact]
    public void MapRating_Null_ReturnsNull()
    {
        Assert.Null(Helpers.MapRating(null));
    }

    [Fact]
    public void MapRating_Zero_ReturnsNull()
    {
        Assert.Null(Helpers.MapRating(0.0));
    }

    [Fact]
    public void MapRating_Negative_ReturnsNull()
    {
        Assert.Null(Helpers.MapRating(-1.0));
    }

    [Fact]
    public void MapRating_AboveMax_ClampedTo5()
    {
        Assert.Equal(5.0, Helpers.MapRating(12.0));
    }

    [Fact]
    public void MapRating_VerySmall_ClampedToHalf()
    {
        Assert.Equal(0.5, Helpers.MapRating(0.1));
    }
}
