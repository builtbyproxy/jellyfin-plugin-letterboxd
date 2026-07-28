using System.Collections.Generic;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Serializd;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Tests for GET ItemRating: the review modal's pre-fill source. The endpoint
/// must always answer 200 with null fields for anything it can't resolve, so
/// the modal never breaks on the lookup; only a missing user identity is an error.
/// </summary>
[Collection("Plugin")]
public class ItemRatingApiTests : System.IDisposable
{

    public void Dispose()
    {
        // The episode tests override the shared series-TMDb reader; restore the
        // production default so an override can't leak into other test classes.
        SerializdSyncRunner.SeriesTmdbIdReader = SerializdSyncRunner.ReadSeriesTmdbId;
    }

    private static (double? Rating, double? Stars) ReadPayload(ActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var t = ok.Value!.GetType();
        var rating = (double?)t.GetProperty("rating", BindingFlags.Public | BindingFlags.Instance)!.GetValue(ok.Value);
        var stars = (double?)t.GetProperty("stars", BindingFlags.Public | BindingFlags.Instance)!.GetValue(ok.Value);
        return (rating, stars);
    }

    /// <summary>
    /// ControllerTestHarness.SetUsers can't proxy the concrete User class, so we
    /// build a real User (same pattern as LetterboxdControllerTests) and derive
    /// the harness's currentUserId from its generated Id.
    /// </summary>
    private static (ControllerTestHarness Harness, User User) MakeHarness()
    {
        var user = new User("lachlan", "test-provider-id", "test-reset-id");
        var h = new ControllerTestHarness(currentUserId: user.Id.ToString("N"));
        h.UserManager.GetUsers().Returns(new List<User> { user });
        return (h, user);
    }

    [Fact]
    public void RatedMovie_ReturnsRatingAndHalfStars()
    {
        var (h, user) = MakeHarness();
        using var _ = h;
        var movie = MakeMovie(603);
        AddItem(h, movie);
        h.UserDataManager.GetUserData(user, movie).Returns(new UserItemData { Key = "k", Rating = 7 });

        var (rating, stars) = ReadPayload(h.Controller.GetItemRating(tmdbId: 603));

        Assert.Equal(7, rating);
        Assert.Equal(3.5, stars);
    }

    [Fact]
    public void UnratedMovie_ReturnsNulls()
    {
        var (h, user) = MakeHarness();
        using var _ = h;
        var movie = MakeMovie(603);
        AddItem(h, movie);
        h.UserDataManager.GetUserData(user, movie).Returns(new UserItemData { Key = "k" });

        var (rating, stars) = ReadPayload(h.Controller.GetItemRating(tmdbId: 603));

        Assert.Null(rating);
        Assert.Null(stars);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void MissingOrNonPositiveTmdbId_ReturnsNulls(int? tmdbId)
    {
        var (h, _) = MakeHarness();
        using var __ = h;

        var (rating, stars) = ReadPayload(h.Controller.GetItemRating(tmdbId: tmdbId));

        Assert.Null(rating);
        Assert.Null(stars);
    }

    [Fact]
    public void UnknownTmdbId_ReturnsNulls()
    {
        var (h, _) = MakeHarness();
        using var __ = h;
        AddItem(h, MakeMovie(603));

        var (rating, stars) = ReadPayload(h.Controller.GetItemRating(tmdbId: 550));

        Assert.Null(rating);
        Assert.Null(stars);
    }

    [Fact]
    public void EpisodeRating_ResolvedBySeriesTmdbAndNumbers()
    {
        var (h, user) = MakeHarness();
        using var _ = h;
        var episode = new Episode { Name = "Ozymandias", ParentIndexNumber = 5, IndexNumber = 14, Id = System.Guid.NewGuid() };
        AddItem(h, episode);
        SerializdSyncRunner.SeriesTmdbIdReader = _ => 1396;
        h.UserDataManager.GetUserData(user, episode).Returns(new UserItemData { Key = "k", Rating = 8 });

        var (rating, stars) = ReadPayload(
            h.Controller.GetItemRating(tmdbId: 1396, isShow: true, seasonNumber: 5, episodeNumber: 14));

        Assert.Equal(8, rating);
        Assert.Equal(4, stars);
    }

    [Fact]
    public void ShowLevelRating_ResolvedFromSeries()
    {
        var (h, user) = MakeHarness();
        using var _ = h;
        var series = MakeSeries(1396);
        AddItem(h, series);
        h.UserDataManager.GetUserData(user, series).Returns(new UserItemData { Key = "k", Rating = 9 });

        var (rating, stars) = ReadPayload(h.Controller.GetItemRating(tmdbId: 1396, isShow: true));

        Assert.Equal(9, rating);
        Assert.Equal(4.5, stars);
    }

    [Fact]
    public void EpisodeRequested_ButOnlySeriesRated_ReturnsNulls()
    {
        var (h, user) = MakeHarness();
        using var _ = h;
        var series = MakeSeries(1396);
        var episode = new Episode { Name = "Pilot", ParentIndexNumber = 1, IndexNumber = 1, Id = System.Guid.NewGuid() };
        AddItem(h, series);
        AddItem(h, episode);
        SerializdSyncRunner.SeriesTmdbIdReader = _ => 1396;
        h.UserDataManager.GetUserData(user, series).Returns(new UserItemData { Key = "k", Rating = 9 });

        var (rating, stars) = ReadPayload(
            h.Controller.GetItemRating(tmdbId: 1396, isShow: true, seasonNumber: 1, episodeNumber: 1));

        Assert.Null(rating);
        Assert.Null(stars);
    }

    [Fact]
    public void NoUserIdentity_IsRejected()
    {
        using var h = new ControllerTestHarness(currentUserId: null);

        var result = h.Controller.GetItemRating(tmdbId: 603);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    private static Movie MakeMovie(int tmdbId, string name = "Sinners")
    {
        var movie = new Movie { Name = name, Id = System.Guid.NewGuid() };
        movie.SetProviderId(MetadataProvider.Tmdb, tmdbId.ToString());
        return movie;
    }

    private static Series MakeSeries(int tmdbId, string name = "Breaking Bad")
    {
        var series = new Series { Name = name, Id = System.Guid.NewGuid() };
        series.SetProviderId(MetadataProvider.Tmdb, tmdbId.ToString());
        return series;
    }

    /// <summary>
    /// Appends an arbitrary BaseItem to the harness's mocked library list.
    /// (ControllerTestHarness.AddMovie substitutes Movie and stubs GetProviderId,
    /// which NSubstitute can't intercept; real entities avoid that.)
    /// </summary>
    private static void AddItem(ControllerTestHarness h, BaseItem item)
    {
        var existing = System.Linq.Enumerable.ToList(
            System.Linq.Enumerable.Cast<BaseItem>(
                h.LibraryManager.GetItemList(Arg.Any<InternalItemsQuery>())));
        existing.Add(item);
        h.LibraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(existing);
    }
}
