using System;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Screens;
using Xunit;

namespace CSVM.Tests;

/// <summary>Pins the closing cinema's flow: that a win on the campaign's last mission plays the film
/// before the scrapbook and every other ending reaches the book with no film, that a replayed win
/// plays it again, that the book opens exactly once however many times the cinema reports stopping,
/// and that the skip set is the closing one's own.
/// </summary>
[Trait("Tier", "Quick")]
public class ClosingCinemaTests
{
    private const int LastSeq = CampaignSequence.MissionCount - 1;

    [Fact]
    public void TheFilmIsTheOneTheFinalCinemaLayoutRowNames()
    {
        Assert.Equal("Final.MPG", ClosingCinema.Name);
    }

    [Fact]
    public void AWinOnTheLastMissionPlaysTheFilmBeforeTheBook()
    {
        var cinema = new Recorder();
        var flow = new ClosingCinema(cinema.Play);

        Assert.True(flow.OpenScrapbook(LastSeq, missionWon: true, cinema.OpenedBook));

        Assert.Equal("Final.MPG", cinema.Name);
        Assert.Equal(0, cinema.Books);
        cinema.Stop();
        Assert.Equal(1, cinema.Books);
    }

    // The reported fault: a mission lost on a profile that has finished the campaign started the
    // film. The result is half the gate, so it opens the book and plays nothing.
    [Fact]
    public void ALossOnTheLastMissionGoesStraightToTheBook()
    {
        var cinema = new Recorder();

        Assert.False(new ClosingCinema(cinema.Play).OpenScrapbook(LastSeq, missionWon: false, cinema.OpenedBook));

        Assert.Null(cinema.Name);
        Assert.Equal(0, cinema.Plays);
        Assert.Equal(1, cinema.Books);
    }

    // FINALCINEMA.SCRIPT's own false branch, and what a player sees 23 times out of 24: the story
    // position is the other half of the gate, so a won mission short of the last one plays nothing.
    [Fact]
    public void AWinOnAnEarlierMissionGoesStraightToTheBook()
    {
        var cinema = new Recorder();

        Assert.False(new ClosingCinema(cinema.Play).OpenScrapbook(LastSeq - 1, missionWon: true, cinema.OpenedBook));

        Assert.Equal(0, cinema.Plays);
        Assert.Equal(1, cinema.Books);
    }

    // A replay of the last mission is a win on the last mission, so it earns the film again. Nothing
    // is latched: the flown result is the whole gate.
    [Fact]
    public void AReplayedWinOnTheLastMissionPlaysTheFilmAgain()
    {
        var cinema = new Recorder();
        var flow = new ClosingCinema(cinema.Play);

        flow.OpenScrapbook(LastSeq, missionWon: true, cinema.OpenedBook);
        cinema.Stop();

        Assert.True(flow.OpenScrapbook(LastSeq, missionWon: true, cinema.OpenedBook));
        cinema.Stop();
        Assert.Equal(2, cinema.Plays);
        Assert.Equal(2, cinema.Books);
    }

    // The EC latch the original's own script holds: a skip landing on the frame the cinema plays
    // out ends the film twice, and the book must still open once.
    [Fact]
    public void TheBookOpensOnceWhenASkipLandsOnThePlayoutFrame()
    {
        var cinema = new Recorder();
        var flow = new ClosingCinema(cinema.Play);

        flow.OpenScrapbook(LastSeq, missionWon: true, cinema.OpenedBook);
        cinema.Stop();
        cinema.Stop();

        Assert.Equal(1, cinema.Books);
    }

    // The asymmetry with the chapter cinema is authored, not an oversight, so it is pinned from
    // both sides here as well as stated on CinemaSkip itself.
    [Fact]
    public void TheClosingCinemaTakesNeitherSpaceNorReturnWhereTheChapterOneDoes()
    {
        var cinema = new Recorder();
        new ClosingCinema(cinema.Play).OpenScrapbook(LastSeq, missionWon: true, cinema.OpenedBook);

        Assert.Equal(CinemaScreen.ClosingKeys, cinema.Skip);
        Assert.True(cinema.Skip.HasFlag(CinemaSkip.Escape) && cinema.Skip.HasFlag(CinemaSkip.LeftMouse));
        Assert.False(cinema.Skip.HasFlag(CinemaSkip.Space) || cinema.Skip.HasFlag(CinemaSkip.Return));
        Assert.True(CinemaScreen.ChapterKeys.HasFlag(CinemaSkip.Space));
        Assert.True(CinemaScreen.ChapterKeys.HasFlag(CinemaSkip.Return));
    }

    // The whole gate as a table: one corner of the four plays the film.
    [Theory]
    [InlineData(CampaignSequence.MissionCount - 1, true, true)]
    [InlineData(CampaignSequence.MissionCount - 1, false, false)]
    [InlineData(0, true, false)]
    [InlineData(0, false, false)]
    public void OnlyAWinOnTheLastMissionEarnsTheFilm(int seq, bool won, bool plays)
    {
        Assert.Equal(plays, ClosingCinema.PlaysAfter(seq, won));
    }

    // The stand-in for Launcher.PlayCinema: it records what it was asked for and hands the film's
    // end back to the caller, so a test decides when, and how often, the cinema stops.
    private sealed class Recorder
    {
        private Action? _then;

        public string? Name { get; private set; }

        public CinemaSkip Skip { get; private set; }

        public int Plays { get; private set; }

        public int Books { get; private set; }

        public void Play(string name, Action then, CinemaSkip skip)
        {
            Name = name;
            Skip = skip;
            Plays++;
            _then = then;
        }

        public void Stop() => _then?.Invoke();

        public void OpenedBook() => Books++;
    }
}
