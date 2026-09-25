using Tuxflix.Core.Plex;
using Xunit;

namespace Tuxflix.Tests;

/// <summary>The skip button and the next episode's card, from a file's markers.</summary>
public sealed class MarkerTimelineTests
{
    // As the server sent them for a real episode: the intro and the credits share one id.
    private static readonly Marker Intro = new() { Id = 42389, Type = "intro", StartTimeOffset = 90_869, EndTimeOffset = 124_137 };
    private static readonly Marker Credits = new() { Id = 42389, Type = "credits", StartTimeOffset = 1_240_809, EndTimeOffset = 1_286_809 };
    private static readonly Marker FinalCredits = new() { Id = 7, Type = "credits", StartTimeOffset = 1_324_074, EndTimeOffset = 1_348_972, Final = true };

    [Fact]
    public void SkippingTheIntroLeavesCreditsThatShareItsIdSkippable()
    {
        var timeline = new MarkerTimeline([Credits, Intro]);

        Assert.Same(Intro, timeline.SkippableAt(91_869, hasNext: true));
        timeline.Skipped(Intro);

        Assert.Null(timeline.SkippableAt(100_000, hasNext: true));
        Assert.Same(Credits, timeline.SkippableAt(1_241_809, hasNext: true));
    }

    [Fact]
    public void TheSkipGoesAwayInAMarkersLastMoments()
    {
        var timeline = new MarkerTimeline([Intro]);

        Assert.Same(Intro, timeline.SkippableAt(Intro.EndTimeOffset - MarkerTimeline.SkipCutoffMs - 1, hasNext: false));
        Assert.Null(timeline.SkippableAt(Intro.EndTimeOffset - MarkerTimeline.SkipCutoffMs, hasNext: false));
        Assert.Null(timeline.SkippableAt(Intro.StartTimeOffset - 1, hasNext: false));
    }

    [Fact]
    public void TheFinalCreditsAreTheNextEpisodesUnlessNothingFollows()
    {
        var timeline = new MarkerTimeline([Intro, FinalCredits]);
        var inCredits = FinalCredits.StartTimeOffset + 1000;

        Assert.Null(timeline.SkippableAt(inCredits, hasNext: true));
        Assert.Same(FinalCredits, timeline.SkippableAt(inCredits, hasNext: false));
    }

    [Fact]
    public void TheNextEpisodeIsOfferedFromTheFinalCredits()
    {
        var timeline = new MarkerTimeline([Intro, Credits, FinalCredits]);

        Assert.False(timeline.OffersNext(FinalCredits.StartTimeOffset - 1, 1_348_972));
        Assert.True(timeline.OffersNext(FinalCredits.StartTimeOffset, 1_348_972));
        Assert.False(timeline.OffersNext(Credits.StartTimeOffset + 1000, 1_348_972));
    }

    [Fact]
    public void WithoutFinalCreditsTheNextEpisodeIsOfferedInTheLastHalfMinute()
    {
        var timeline = new MarkerTimeline([Intro, Credits]);
        const long duration = 1_325_344;

        Assert.False(timeline.OffersNext(duration - MarkerTimeline.NextLeadMs - 1, duration));
        Assert.True(timeline.OffersNext(duration - MarkerTimeline.NextLeadMs, duration));
        Assert.False(timeline.OffersNext(0, 0));
    }
}
