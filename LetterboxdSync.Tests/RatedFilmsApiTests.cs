using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using LetterboxdSync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

public class FilmSummaryHelperTests
{
    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void ExtractTmdbIdFromLinks_ValidTmdbLink_ReturnsId()
    {
        var film = Parse(@"{
            ""links"": [
                { ""type"": ""letterboxd"", ""id"": ""KQMM"" },
                { ""type"": ""imdb"", ""id"": ""tt31193180"" },
                { ""type"": ""tmdb"", ""id"": ""1233413"" }
            ]
        }");

        Assert.Equal(1233413, LetterboxdApiClient.ExtractTmdbIdFromLinks(film));
    }

    [Fact]
    public void ExtractTmdbIdFromLinks_TmdbCaseInsensitive_ReturnsId()
    {
        var film = Parse(@"{ ""links"": [ { ""type"": ""TMDB"", ""id"": ""550"" } ] }");
        Assert.Equal(550, LetterboxdApiClient.ExtractTmdbIdFromLinks(film));
    }

    [Fact]
    public void ExtractTmdbIdFromLinks_NoTmdbLink_ReturnsNull()
    {
        var film = Parse(@"{
            ""links"": [
                { ""type"": ""letterboxd"", ""id"": ""KQMM"" },
                { ""type"": ""imdb"", ""id"": ""tt31193180"" }
            ]
        }");

        Assert.Null(LetterboxdApiClient.ExtractTmdbIdFromLinks(film));
    }

    [Fact]
    public void ExtractTmdbIdFromLinks_MissingLinks_ReturnsNull()
    {
        Assert.Null(LetterboxdApiClient.ExtractTmdbIdFromLinks(Parse(@"{ ""name"": ""Sinners"" }")));
    }

    [Fact]
    public void ExtractTmdbIdFromLinks_NonNumericTmdbId_ReturnsNull()
    {
        var film = Parse(@"{ ""links"": [ { ""type"": ""tmdb"", ""id"": ""junk"" } ] }");
        Assert.Null(LetterboxdApiClient.ExtractTmdbIdFromLinks(film));
    }

    [Fact]
    public void ExtractMemberRating_PresentRating_ReturnsValue()
    {
        var film = Parse(@"{
            ""relationships"": [
                {
                    ""member"": { ""id"": ""614Bn"" },
                    ""relationship"": {
                        ""watched"": true,
                        ""rating"": 2.0,
                        ""liked"": false
                    }
                }
            ]
        }");

        Assert.Equal(2.0, LetterboxdApiClient.ExtractMemberRating(film));
    }

    [Fact]
    public void ExtractMemberRating_HalfStar_ReturnsValue()
    {
        var film = Parse(@"{
            ""relationships"": [
                { ""relationship"": { ""rating"": 4.5 } }
            ]
        }");

        Assert.Equal(4.5, LetterboxdApiClient.ExtractMemberRating(film));
    }

    [Fact]
    public void ExtractMemberRating_NoRatingKey_ReturnsNull()
    {
        var film = Parse(@"{
            ""relationships"": [
                { ""relationship"": { ""watched"": true } }
            ]
        }");

        Assert.Null(LetterboxdApiClient.ExtractMemberRating(film));
    }

    [Fact]
    public void ExtractMemberRating_EmptyRelationships_ReturnsNull()
    {
        Assert.Null(LetterboxdApiClient.ExtractMemberRating(Parse(@"{ ""relationships"": [] }")));
    }

    [Fact]
    public void ExtractMemberRating_MissingRelationshipsKey_ReturnsNull()
    {
        Assert.Null(LetterboxdApiClient.ExtractMemberRating(Parse(@"{ ""name"": ""Sinners"" }")));
    }

    [Fact]
    public void ExtractMemberRating_RelationshipsWrongType_ReturnsNull()
    {
        Assert.Null(LetterboxdApiClient.ExtractMemberRating(Parse(@"{ ""relationships"": ""oops"" }")));
    }

    [Fact]
    public void ExtractMemberRating_RatingNotNumber_ReturnsNull()
    {
        var film = Parse(@"{
            ""relationships"": [
                { ""relationship"": { ""rating"": ""4.5"" } }
            ]
        }");

        Assert.Null(LetterboxdApiClient.ExtractMemberRating(film));
    }
}

public class GetDiaryFilmEntriesIntegrationTests
{
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");

    /// <summary>
    /// Real shape of the /films?memberRelationship=Watched response we captured
    /// during the live API probe — three rated films, one unrated, one duplicate
    /// TMDb id (defensive: API has been seen to return dupes after edits).
    /// </summary>
    private const string SampleResponse = @"{
        ""next"": null,
        ""items"": [
            {
                ""type"": ""FilmSummary"",
                ""id"": ""KQMM"",
                ""name"": ""Sinners"",
                ""links"": [
                    { ""type"": ""letterboxd"", ""id"": ""KQMM"" },
                    { ""type"": ""tmdb"", ""id"": ""1233413"" }
                ],
                ""relationships"": [
                    { ""relationship"": { ""watched"": true, ""rating"": 2.0 } }
                ]
            },
            {
                ""type"": ""FilmSummary"",
                ""id"": ""2a9q"",
                ""name"": ""Iron Man"",
                ""links"": [
                    { ""type"": ""tmdb"", ""id"": ""1726"" }
                ],
                ""relationships"": [
                    { ""relationship"": { ""watched"": true, ""rating"": 4.5 } }
                ]
            },
            {
                ""type"": ""FilmSummary"",
                ""id"": ""abcd"",
                ""name"": ""Some Watched But Unrated Film"",
                ""links"": [
                    { ""type"": ""tmdb"", ""id"": ""99999"" }
                ],
                ""relationships"": [
                    { ""relationship"": { ""watched"": true } }
                ]
            },
            {
                ""type"": ""FilmSummary"",
                ""id"": ""KQMM"",
                ""name"": ""Sinners (duplicate row)"",
                ""links"": [
                    { ""type"": ""tmdb"", ""id"": ""1233413"" }
                ],
                ""relationships"": [
                    { ""relationship"": { ""watched"": true, ""rating"": 5.0 } }
                ]
            }
        ]
    }";

    [Fact]
    public async Task GetDiaryFilmEntriesAsync_ParsesFilmsAndRatings()
    {
        string? capturedQuery = null;

        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/films") == true &&
                request.Method == HttpMethod.Get)
            {
                capturedQuery = request.RequestUri.Query;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SampleResponse)
                };
            }
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var entries = await client.GetDiaryFilmEntriesAsync("user");

        // The /films endpoint must be called with the Watched relationship and
        // MemberRelationship include — that's what carries the per-member rating.
        Assert.NotNull(capturedQuery);
        Assert.Contains("memberRelationship=Watched", capturedQuery);
        Assert.Contains("include=MemberRelationship", capturedQuery);

        // Three unique TMDb IDs (Sinners deduped), parsed in input order.
        Assert.Equal(3, entries.Count);
        Assert.Equal(new[] { 1233413, 1726, 99999 }, entries.Select(e => e.TmdbId).ToArray());

        // Sinners kept its first rating (2.0), not the duplicate row's 5.0.
        var sinners = entries.First(e => e.TmdbId == 1233413);
        Assert.Equal(2.0, sinners.Rating);

        var ironMan = entries.First(e => e.TmdbId == 1726);
        Assert.Equal(4.5, ironMan.Rating);

        var unrated = entries.First(e => e.TmdbId == 99999);
        Assert.Null(unrated.Rating);
    }

    [Fact]
    public async Task GetDiaryFilmEntriesAsync_FollowsPagination()
    {
        var pageRequests = new List<string>();

        var page1 = @"{
            ""next"": ""start=2"",
            ""items"": [
                { ""type"": ""FilmSummary"", ""id"": ""A"", ""links"": [ { ""type"": ""tmdb"", ""id"": ""1"" } ],
                  ""relationships"": [ { ""relationship"": { ""rating"": 5.0 } } ] }
            ]
        }";
        var page2 = @"{
            ""items"": [
                { ""type"": ""FilmSummary"", ""id"": ""B"", ""links"": [ { ""type"": ""tmdb"", ""id"": ""2"" } ],
                  ""relationships"": [ { ""relationship"": { ""rating"": 3.5 } } ] }
            ]
        }";

        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/films") == true &&
                request.Method == HttpMethod.Get)
            {
                var q = request.RequestUri.Query;
                pageRequests.Add(q);
                var body = q.Contains("start=") ? page2 : page1;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body)
                };
            }
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var entries = await client.GetDiaryFilmEntriesAsync("user");

        Assert.Equal(2, pageRequests.Count);
        Assert.DoesNotContain("start=", pageRequests[0]);
        Assert.Contains("start=", pageRequests[1]);

        Assert.Equal(2, entries.Count);
        Assert.Equal(1, entries[0].TmdbId);
        Assert.Equal(5.0, entries[0].Rating);
        Assert.Equal(2, entries[1].TmdbId);
        Assert.Equal(3.5, entries[1].Rating);
    }

    [Fact]
    public async Task GetDiaryFilmEntriesAsync_EmptyResponse_ReturnsEmpty()
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/films") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{ ""items"": [] }")
                };
            }
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var entries = await client.GetDiaryFilmEntriesAsync("user");

        Assert.Empty(entries);
    }

    [Fact]
    public async Task GetDiaryTmdbIdsAsync_ReturnsJustTmdbIds()
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/films") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SampleResponse)
                };
            }
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var ids = await client.GetDiaryTmdbIdsAsync("user");

        // Same dedup behaviour as the entries call, just stripped to ids.
        Assert.Equal(new[] { 1233413, 1726, 99999 }, ids.ToArray());
    }
}
