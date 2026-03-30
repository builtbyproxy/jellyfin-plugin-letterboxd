using System;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

public class LetterboxdClientTests
{
    private readonly NullLogger<LetterboxdClient> _logger = new NullLogger<LetterboxdClient>();

    [Fact]
    public void ParseDiaryInfo_WithWatchedAndDiaryEntries_SetsFlagsCorrectly()
    {
        // Arrange
        var json = @"
        {
            ""relationships"": [
                {
                    ""relationship"": {
                        ""watched"": true,
                        ""whenWatched"": ""2026-03-28T20:47:14Z"",
                        ""diaryEntries"": [""dIJNsD""]
                    }
                }
            ]
        }";

        // Act
        var result = LetterboxdClient.ParseDiaryInfo(json, "testFilmId", _logger);

        // Assert
        Assert.True(result.IsWatched);
        Assert.True(result.HasAnyEntry);
        Assert.Equal("dIJNsD", result.LatestEntryId);
        Assert.Equal(DateTime.Parse("2026-03-28T20:47:14Z"), result.LastDate);
    }

    [Fact]
    public void ParseDiaryInfo_WithWatchedButNoDiaryEntries_SetsWatchedOnly()
    {
        // Arrange
        var json = @"
        {
            ""relationships"": [
                {
                    ""relationship"": {
                        ""watched"": true,
                        ""diaryEntries"": []
                    }
                }
            ]
        }";

        // Act
        var result = LetterboxdClient.ParseDiaryInfo(json, "testFilmId", _logger);

        // Assert
        Assert.True(result.IsWatched);
        Assert.False(result.HasAnyEntry);
        Assert.Null(result.LastDate);
        Assert.Null(result.LatestEntryId);
    }

    [Fact]
    public void ParseDiaryInfo_WithWatchedMissingDiaryEntriesArray_HandlesGracefully()
    {
        // Arrange
        var json = @"
        {
            ""relationships"": [
                {
                    ""relationship"": {
                        ""watched"": true
                    }
                }
            ]
        }";

        // Act
        var result = LetterboxdClient.ParseDiaryInfo(json, "testFilmId", _logger);

        // Assert
        Assert.True(result.IsWatched);
        Assert.False(result.HasAnyEntry);
        Assert.Null(result.LastDate);
        Assert.Null(result.LatestEntryId);
    }

    [Fact]
    public void ParseDiaryInfo_WithNotWatched_ReturnsFalseFlags()
    {
        // Arrange
        var json = @"
        {
            ""relationships"": [
                {
                    ""relationship"": {
                        ""watched"": false,
                        ""diaryEntries"": []
                    }
                }
            ]
        }";

        // Act
        var result = LetterboxdClient.ParseDiaryInfo(json, "testFilmId", _logger);

        // Assert
        Assert.False(result.IsWatched);
        Assert.False(result.HasAnyEntry);
        Assert.Null(result.LastDate);
        Assert.Null(result.LatestEntryId);
    }
}
