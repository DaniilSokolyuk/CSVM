using System;
using System.Collections.Generic;
using CSVM.Session.Campaign;
using CSVM.UI.Screens;
using Xunit;

namespace CSVM.Tests;

/// <summary>Pins the chapter cinema's flow: which film a campaign position plays, that it plays
/// only where a chapter opens, that it plays once, that the passenger cabin opens exactly once
/// however many times the cinema reports stopping, and that the skip set is the chapter's own.
/// </summary>
[Trait("Tier", "Quick")]
public class ChapterCinemaTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(9, 2)]
    [InlineData(10, 3)]
    [InlineData(14, 3)]
    [InlineData(15, 4)]
    [InlineData(19, 4)]
    [InlineData(20, 5)]
    [InlineData(23, 5)]
    [InlineData(-1, 0)]
    [InlineData(24, 0)]
    public void EveryPositionKnowsItsStoryChapter(int seq, int chapter)
    {
        Assert.Equal(chapter, ChapterCinema.ChapterOf(seq));
    }

    [Fact]
    public void OnlyAChaptersFirstMissionOpensOne()
    {
        var opening = new List<int>();
        for (int seq = -1; seq <= 24; seq++)
        {
            if (ChapterCinema.OpensChapter(seq))
            {
                opening.Add(seq);
            }
        }

        Assert.Equal(new[] { 0, 5, 10, 15, 20 }, opening);
    }

    [Fact]
    public void AChapterIsNamedByItsNumber()
    {
        Assert.Equal("chap1", ChapterCinema.NameOf(1));
        Assert.Equal("chap5", ChapterCinema.NameOf(5));
    }

    [Fact]
    public void AChapterOpeningPositionPlaysItsFilmBeforeTheCabin()
    {
        var cinema = new Recorder();
        var flow = new ChapterCinema(cinema.Play);

        Assert.True(flow.OpenCabin(10, cinema.OpenedCabin));

        Assert.Equal("chap3", cinema.Name);
        Assert.Equal(0, cinema.Cabins);
        cinema.Stop();
        Assert.Equal(1, cinema.Cabins);
    }

    [Fact]
    public void EachChapterPlaysItsOwnFilm()
    {
        var played = new List<string>();
        foreach (int seq in new[] { 0, 5, 10, 15, 20 })
        {
            var cinema = new Recorder();
            new ChapterCinema(cinema.Play).OpenCabin(seq, cinema.OpenedCabin);
            played.Add(cinema.Name ?? "none");
        }

        Assert.Equal(new[] { "chap1", "chap2", "chap3", "chap4", "chap5" }, played);
    }

    [Fact]
    public void APositionInsideAChapterGoesStraightToTheCabin()
    {
        var cinema = new Recorder();

        Assert.False(new ChapterCinema(cinema.Play).OpenCabin(3, cinema.OpenedCabin));

        Assert.Null(cinema.Name);
        Assert.Equal(1, cinema.Cabins);
    }

    [Fact]
    public void APositionOutsideTheSequenceStillReachesTheCabin()
    {
        var cinema = new Recorder();

        Assert.False(new ChapterCinema(cinema.Play).OpenCabin(24, cinema.OpenedCabin));

        Assert.Null(cinema.Name);
        Assert.Equal(1, cinema.Cabins);
    }

    // The EC latch the original's own script holds: a skip landing on the frame the cinema plays
    // out ends the film twice, and the cabin must still open once.
    [Fact]
    public void TheCabinOpensOnceWhenASkipLandsOnThePlayoutFrame()
    {
        var cinema = new Recorder();
        var flow = new ChapterCinema(cinema.Play);

        flow.OpenCabin(0, cinema.OpenedCabin);
        cinema.Stop();
        cinema.Stop();

        Assert.Equal(1, cinema.Cabins);
    }

    [Fact]
    public void TheSameChapterDoesNotPlayTwice()
    {
        var cinema = new Recorder();
        var flow = new ChapterCinema(cinema.Play);

        flow.OpenCabin(5, cinema.OpenedCabin);
        cinema.Stop();

        Assert.False(flow.OpenCabin(5, cinema.OpenedCabin));
        Assert.Equal(1, cinema.Plays);
        Assert.Equal(2, cinema.Cabins);
        Assert.Equal(2, flow.ChapterPlayed);
    }

    [Fact]
    public void ANewChapterPlaysAfterTheLastOne()
    {
        var cinema = new Recorder();
        var flow = new ChapterCinema(cinema.Play);

        flow.OpenCabin(5, cinema.OpenedCabin);
        cinema.Stop();
        Assert.True(flow.OpenCabin(10, cinema.OpenedCabin));

        Assert.Equal(2, cinema.Plays);
        Assert.Equal("chap3", cinema.Name);
    }

    // The asymmetry with the closing cinema is authored, not an oversight, so it is pinned from
    // both sides here as well as stated on CinemaSkip itself.
    [Fact]
    public void TheChapterCinemaTakesSpaceAndReturnWhereTheClosingOneDoesNot()
    {
        var cinema = new Recorder();
        new ChapterCinema(cinema.Play).OpenCabin(0, cinema.OpenedCabin);

        Assert.Equal(CinemaScreen.ChapterKeys, cinema.Skip);
        Assert.True(cinema.Skip.HasFlag(CinemaSkip.Space) && cinema.Skip.HasFlag(CinemaSkip.Return));
        Assert.False(CinemaScreen.ClosingKeys.HasFlag(CinemaSkip.Space));
        Assert.False(CinemaScreen.ClosingKeys.HasFlag(CinemaSkip.Return));
    }

    [Fact]
    public void TheChapterComesFromTheProfilesOwnPosition()
    {
        var cinema = new Recorder();
        var profile = CampaignProfileDef.NewProfile("Zachary");

        new ChapterCinema(cinema.Play).OpenCabin(profile, cinema.OpenedCabin);
        Assert.Equal("chap1", cinema.Name);

        var later = new Recorder();
        profile.MissionsCompleted = 5;
        new ChapterCinema(later.Play).OpenCabin(profile, later.OpenedCabin);
        Assert.Equal("chap2", later.Name);
    }

    [Fact]
    public void AProfileInsideAChapterOpensTheCabinWithNoFilm()
    {
        var cinema = new Recorder();
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.MissionsCompleted = 7;

        Assert.False(new ChapterCinema(cinema.Play).OpenCabin(profile, cinema.OpenedCabin));

        Assert.Null(cinema.Name);
        Assert.Equal(1, cinema.Cabins);
    }

    // The stand-in for Launcher.PlayCinema: it records what it was asked for and hands the film's
    // end back to the caller, so a test decides when, and how often, the cinema stops.
    private sealed class Recorder
    {
        private Action? _then;

        public string? Name { get; private set; }

        public CinemaSkip Skip { get; private set; }

        public int Plays { get; private set; }

        public int Cabins { get; private set; }

        public void Play(string name, Action then, CinemaSkip skip)
        {
            Name = name;
            Skip = skip;
            Plays++;
            _then = then;
        }

        public void Stop() => _then?.Invoke();

        public void OpenedCabin() => Cabins++;
    }
}
