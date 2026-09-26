using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CSVM.UI.Boards;
using CSVM.UI.Screens;
using Xunit;

namespace CSVM.Tests;

/// <summary>Pins the boot sequence against the block <c>fmv.zrd</c> authors: the eight actions in
/// the reader's own order with the reader's own film names, the card down as the first film starts,
/// the two logos running back to back with no black held between them, the skip set every film
/// takes, and one handoff at the end however the last film stopped.</summary>
[Trait("Tier", "Quick")]
public class BootSequenceTests
{
    /// <summary>The whole block, action for action. ⚠ These strings are the reader's, not a
    /// preference: <c>INTRO</c> is seven actions and <c>CHAP0</c> the eighth
    /// (docs/formats/cinemas.md).</summary>
    [Fact]
    public void TheBlockRunsInTheReadersOwnOrder()
    {
        var stage = new Recorder();
        new BootSequence(stage.Film, stage.Still, stage.Drop).Run(stage.Handoff);

        Assert.Equal(
            new[]
            {
                "still Card 5",
                "drop",
                "film MSopen1.mpg",
                "still Wait 0",
                "still Fade 0",
                "film zipper.mpg",
                "still Wait 0",
                "film Chap0.mpg",
                "handoff",
            },
            stage.Steps);
    }

    /// <summary>The card is seen at the beginning and nowhere else: it goes down once, as the first
    /// film starts, and no later action puts it back. ⚠ This is the original at the controls, not a
    /// reading of the reader, which says nothing about what survives a <c>PLAYAVI</c>.</summary>
    [Fact]
    public void TheFirstFilmTakesTheCardDownForGood()
    {
        var stage = new Recorder();
        new BootSequence(stage.Film, stage.Still, stage.Drop).Run(stage.Handoff);

        Assert.Equal(1, stage.Steps.Count(step => step == "drop"));
        Assert.Equal(1, stage.Steps.Count(step => step == "still Card 5"));
        Assert.True(stage.Steps.IndexOf("drop") < stage.Steps.IndexOf("film MSopen1.mpg"));
    }

    /// <summary><c>FADEOUT</c> keeps its place and its authored second in the block even though the
    /// screen it would ramp is already empty, an authored action not being deleted for being
    /// invisible. Its hold is zero, so the block spends none of that second on it.</summary>
    [Fact]
    public void TheFadeKeepsItsSecondAfterTheCardIsGone()
    {
        var stage = new Recorder();
        new BootSequence(stage.Film, stage.Still, stage.Drop).Run(stage.Handoff);

        Assert.Equal(1.0, BootSequence.FadeSeconds);
        Assert.Equal(0.0, BootSequence.Held(BootHold.Fade, BootSequence.FadeSeconds));
        Assert.True(stage.Steps.IndexOf("still Fade 0") > stage.Steps.IndexOf("drop"));
    }

    /// <summary>The two logos run back to back: every hold after the card is down holds nothing and
    /// takes no time, so the only screen time the block spends on a still is the card's own
    /// <c>WAIT 5.0</c>. ⚠ This is film of the original from launch, not a reading of the reader,
    /// which authors 2.0 s between the logos.</summary>
    [Fact]
    public void NothingIsHeldBetweenTheFilms()
    {
        var stage = new Recorder();
        new BootSequence(stage.Film, stage.Still, stage.Drop).Run(stage.Handoff);

        Assert.Equal(5.0, BootSequence.Held(BootHold.Card, BootSequence.CardSeconds));
        Assert.Equal(0.0, BootSequence.Held(BootHold.Wait, BootSequence.LogoGapSeconds));
        Assert.Equal(
            new[] { "still Card 5" },
            stage.Steps.Where(step => step.StartsWith("still", StringComparison.Ordinal)
                && !step.EndsWith(" 0", StringComparison.Ordinal)));
    }

    /// <summary>Every film takes the boot set, any key or a left click, because a player at the
    /// first screen the game shows has been taught no key at all.</summary>
    [Fact]
    public void EveryFilmTakesTheBootSkipSet()
    {
        var stage = new Recorder();
        new BootSequence(stage.Film, stage.Still, stage.Drop).Run(stage.Handoff);

        Assert.Equal(3, stage.Skips.Count);
        Assert.All(stage.Skips, skip => Assert.Equal(CinemaScreen.BootKeys, skip));
    }

    /// <summary>Nothing runs the handoff before the opening cinema, which is what a caller relies
    /// on when it puts the launchscreen behind it.</summary>
    [Fact]
    public void TheHandoffWaitsForTheLastFilm()
    {
        var stage = new Recorder(runContinuations: false);
        new BootSequence(stage.Film, stage.Still, stage.Drop).Run(stage.Handoff);

        Assert.Equal(new[] { "still Card 5" }, stage.Steps);
        Assert.False(stage.HandedOff);
    }

    /// <summary>A film this install does not carry runs its continuation at once, which is the
    /// shape <c>Launcher.PlayCinema</c> takes for an unreadable file, so the block still reaches
    /// its end and the card is still put up first.</summary>
    [Fact]
    public void AnInstallMissingEveryFilmStillReachesTheHandoff()
    {
        var stage = new Recorder();
        new BootSequence((_, then, _) => then(), stage.Still, stage.Drop).Run(stage.Handoff);

        Assert.Equal(
            new[]
            {
                "still Card 5", "drop", "still Wait 0", "still Fade 0", "still Wait 0", "handoff",
            },
            stage.Steps);
    }

    /// <summary>The card's shape, which holds with no install present: the splash art under two
    /// lines centred across the whole authored width, each drawn twice for its shadow.</summary>
    [Fact]
    public void TheCardIsTheSplashArtUnderTwoCentredLines()
    {
        var card = BootSequence.Card(TestData.TempDir());

        var art = Assert.Single(card.Backdrop);
        Assert.Equal(BoardArtLibrary.Ui, art.Art.Library);
        Assert.Equal("MM_SPLASHBACKGROUND.JPG", art.Art.Name);
        Assert.Equal(0f, art.X);
        Assert.Equal(0f, art.Y);

        Assert.Equal(4, card.Lines.Count);
        Assert.Equal(new[] { 551f, 550f, 566f, 565f }, card.Lines.Select(line => line.Y));
        Assert.Equal(new[] { 1f, 0f, 1f, 0f }, card.Lines.Select(line => line.X));
        Assert.All(card.Lines, line => Assert.Equal(BoardFit.AuthoredWidth, line.Width));
        Assert.All(card.Lines, line => Assert.Equal(BoardJustify.Center, line.Justify));
        Assert.All(card.Lines, line => Assert.Equal(12f, line.Size));
    }

    /// <summary>The three things the card needs are in the extraction rather than in this project:
    /// the art file, and the two <c>MSG_COPYRIGHT</c> keys the message table resolves.</summary>
    [ExtractedDataFact]
    public void TheCardsArtAndStringsComeOutOfTheExtraction()
    {
        string root = TestData.DataRoot!;
        Assert.True(File.Exists(Path.Combine(
            root, "extracted", "rof", "ASSETS", "GRAPHICS", BootSequence.CardArt)));

        var lines = BootSequence.Card(root);
        string first = lines.Lines[1].Text;
        string second = lines.Lines[3].Text;
        Assert.DoesNotContain("MSG_COPYRIGHT", first, StringComparison.Ordinal);
        Assert.DoesNotContain("MSG_COPYRIGHT", second, StringComparison.Ordinal);
        Assert.Contains("Microsoft Corporation", first, StringComparison.Ordinal);
        Assert.Contains("copyright laws", second, StringComparison.Ordinal);
        Assert.Equal(first, lines.Lines[0].Text);
        Assert.Equal(second, lines.Lines[2].Text);
    }

    private sealed class Recorder
    {
        private readonly bool _run;

        public Recorder(bool runContinuations = true) => _run = runContinuations;

        public List<string> Steps { get; } = new();

        public List<CinemaSkip> Skips { get; } = new();

        public bool HandedOff { get; private set; }

        public void Film(string name, Action then, CinemaSkip skip)
        {
            Steps.Add($"film {name}");
            Skips.Add(skip);
            if (_run)
            {
                then();
            }
        }

        public void Drop() => Steps.Add("drop");

        public void Still(BootHold hold, double seconds, Action then)
        {
            Steps.Add($"still {hold} {seconds.ToString("0.###", CultureInfo.InvariantCulture)}");
            if (_run)
            {
                then();
            }
        }

        public void Handoff()
        {
            Steps.Add("handoff");
            HandedOff = true;
        }
    }
}
