using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Mech3;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>The briefing's reveal script: the reader that turns a state into opcodes, and the
/// interpreter that runs them against the narration's cue points. Every step here is hand-authored
/// from docs/formats/briefing.md, so the beat order and the marker gating are pinned without an
/// extraction.</summary>
public class BriefingScriptTests
{
    // A hand-authored dialog in the reader's own shape: the states sit at the top level, beside
    // BRIEFINGDIALOG rather than inside it.
    private const string Dialog = """
        [[
          "BRIEFINGDIALOG", ["PRIMITIVES", []],
          "default", ["BACKGROUND_IMAGES", [["loading", 0, 0]]],
          "brief_c61", [
            "IMAGE_PATH", ["..\\data\\c3\\images"],
            "BACKGROUND_IMAGES", [["HA-m1MAP", 0, 0]],
            "SCRIPT", [
              "PlaySound", ["sound", ["briefing_c1m1"], "vol", [1.0], "markers", ["true"], "startmarker", [0]],
              "WaitForMarker", [0],
              "Pict", ["OBJPIN1", "bitmap", ["pin6"], "at", [700, 180], "center", ["true"]],
              "Fade", ["OBJPIN1", "start", [0.0], "end", [1.0], "duration", [0.5]],
              "On", ["OBJPIN1"],
              "Wait", [2.0],
              "Objective", ["ZEPTEXT1", "index", [0]],
              "On", ["ZEPTEXT1"]
            ]
          ]
        ]]
        """;

    [Fact]
    public void TheScriptBlocksOnEachMarkerAndRevealsItsObjectiveBehindIt()
    {
        var reveal = new BriefingReveal(PinThenLine(), new double[] { 5, 10 });

        Assert.Equal("briefing_c1m1", reveal.Narration);
        Assert.Equal(1, reveal.NarrationStarts);
        Assert.Equal(0, reveal.BlockedOnMarker);
        Assert.Empty(reveal.Elements);

        reveal.Advance(5);
        Assert.True(reveal.Element("OBJPIN1")?.Visible);
        Assert.Equal(1, reveal.BlockedOnMarker);
        Assert.Empty(reveal.RevealedObjectives);

        reveal.Advance(5);
        Assert.Equal(new[] { 0 }, reveal.RevealedObjectives);
        Assert.True(reveal.Complete);
    }

    /// <summary>The degraded mode a wav with no cue chunk earns: no marker blocks, so the map
    /// finishes at once and the narration plays over a completed reveal. No timing is invented.</summary>
    [Fact]
    public void WithoutCuePointsEveryMarkerReleasesAtOnce()
    {
        var reveal = new BriefingReveal(PinThenLine(), Array.Empty<double>());

        Assert.True(reveal.Complete);
        Assert.Equal(-1, reveal.BlockedOnMarker);
        Assert.Equal(new[] { 0 }, reveal.RevealedObjectives);
        Assert.Equal("briefing_c1m1", reveal.Narration);
    }

    /// <summary>A marker the wav does not carry is the same case as no cue chunk at all: the beat
    /// releases rather than stalling the reveal forever.</summary>
    [Fact]
    public void AMarkerBeyondTheCueTableDoesNotStallTheReveal()
    {
        var reveal = new BriefingReveal(PinThenLine(), new double[] { 5 });

        reveal.Advance(5);
        Assert.True(reveal.Complete);
        Assert.Equal(new[] { 0 }, reveal.RevealedObjectives);
    }

    [Fact]
    public void AWaitHoldsTheScriptForItsAuthoredSeconds()
    {
        var steps = new List<BriefingStep>
        {
            new() { Op = BriefingOp.On, Id = "A" },
            new() { Op = BriefingOp.Wait, Duration = 2f },
            new() { Op = BriefingOp.On, Id = "B" },
        };
        var reveal = new BriefingReveal(steps, Array.Empty<double>());

        Assert.True(reveal.Element("A")?.Visible);
        Assert.Null(reveal.Element("B"));

        reveal.Advance(1);
        Assert.Null(reveal.Element("B"));

        reveal.Advance(1);
        Assert.True(reveal.Element("B")?.Visible);
    }

    [Fact]
    public void AFadeWalksItsOpacityOverItsDuration()
    {
        var steps = new List<BriefingStep>
        {
            new() { Op = BriefingOp.Pict, Id = "SHOT1", Bitmap = "HA-m1shot2" },
            new() { Op = BriefingOp.Fade, Id = "SHOT1", From = 0f, To = 1f, Duration = 2f },
            new() { Op = BriefingOp.On, Id = "SHOT1" },
        };
        var reveal = new BriefingReveal(steps, Array.Empty<double>());

        Assert.Equal(0f, reveal.Element("SHOT1")!.Opacity, 3);
        reveal.Advance(1);
        Assert.Equal(0.5f, reveal.Element("SHOT1")!.Opacity, 3);
        reveal.Advance(1);
        Assert.Equal(1f, reveal.Element("SHOT1")!.Opacity, 3);
        Assert.True(reveal.Complete);
    }

    /// <summary>Where a state authors both, a move's own first path point is where the element
    /// appears, not the position its <c>Pict</c> named (the PANDORA icon's case).</summary>
    [Fact]
    public void AMoveStartsAtItsPathRatherThanAtThePictPosition()
    {
        var steps = new List<BriefingStep>
        {
            new() { Op = BriefingOp.Pict, Id = "PANDORA", At = new BriefingPoint(300, 500) },
            new()
            {
                Op = BriefingOp.Move,
                Id = "PANDORA",
                Path = new[] { new BriefingPoint(300, 550), new BriefingPoint(300, 450) },
                Duration = 4f,
            },
        };
        var reveal = new BriefingReveal(steps, Array.Empty<double>());

        Assert.Equal(550f, reveal.Element("PANDORA")!.At.Y, 3);
        reveal.Advance(2);
        Assert.Equal(500f, reveal.Element("PANDORA")!.At.Y, 3);
        reveal.Advance(2);
        Assert.Equal(450f, reveal.Element("PANDORA")!.At.Y, 3);
    }

    /// <summary>REPLAY BRIEFING: a blank map and the narration asked for again, which is how a
    /// shell knows to restart playback.</summary>
    [Fact]
    public void RestartClearsTheMapAndAsksForTheNarrationAgain()
    {
        var reveal = new BriefingReveal(PinThenLine(), Array.Empty<double>());
        Assert.Equal(1, reveal.NarrationStarts);

        reveal.Restart();

        Assert.Equal(2, reveal.NarrationStarts);
        Assert.Equal(0d, reveal.Clock);
    }

    [Fact]
    public void TheStateKeyIsTheStorageAddressNotTheStoryPosition()
    {
        Assert.Equal("brief_c61", BriefingDialog.StateKey(6, 1));
        Assert.Equal("brief_c23", BriefingDialog.StateKey(2, 3));
    }

    /// <summary>The reader over a hand-authored dialog: a state's map bitmap, its narration name
    /// and every opcode's arguments, including the ones that carry a bare number.</summary>
    [Fact]
    public void TheReaderTakesTheMapAndTheNarrationFromTheStateItself()
    {
        var dir = TestData.TempDir();
        File.WriteAllText(Path.Combine(dir, "Briefing.json"), Dialog);

        var state = BriefingDialog.Load(dir).Find(6, 1);

        Assert.NotNull(state);
        Assert.Equal("HA-m1MAP", state!.Background);
        Assert.Equal("briefing_c1m1", state.Sound);
        Assert.Equal(0, state.StartMarker);
        Assert.Equal(
            new[]
            {
                BriefingOp.PlaySound, BriefingOp.WaitForMarker, BriefingOp.Pict, BriefingOp.Fade,
                BriefingOp.On, BriefingOp.Wait, BriefingOp.Objective, BriefingOp.On,
            },
            Ops(state.Steps));

        var pict = state.Steps[2];
        Assert.Equal("OBJPIN1", pict.Id);
        Assert.Equal("pin6", pict.Bitmap);
        Assert.Equal(new BriefingPoint(700, 180), pict.At);
        Assert.Equal(0.5f, state.Steps[3].Duration, 3);
        Assert.Equal(2f, state.Steps[5].Duration, 3);
        Assert.Equal(0, state.Steps[6].Index);
    }

    /// <summary>Only states with a script are missions: the <c>default</c> loading state carries
    /// none and must not be offered as one.</summary>
    [Fact]
    public void TheLoadingStateIsNotAMissionState()
    {
        var dir = TestData.TempDir();
        File.WriteAllText(Path.Combine(dir, "Briefing.json"), Dialog);

        Assert.Single(BriefingDialog.Load(dir).States);
    }

    private static BriefingOp[] Ops(IReadOnlyList<BriefingStep> steps)
    {
        var ops = new BriefingOp[steps.Count];
        for (int i = 0; i < steps.Count; i++)
        {
            ops[i] = steps[i].Op;
        }

        return ops;
    }

    // The shape every mission state has: the narration, then a pin and its note line one marker
    // apart.
    private static List<BriefingStep> PinThenLine() => new()
    {
        new() { Op = BriefingOp.PlaySound, Id = "briefing_c1m1" },
        new() { Op = BriefingOp.WaitForMarker, Index = 0 },
        new() { Op = BriefingOp.Pict, Id = "OBJPIN1", Bitmap = "pin6", At = new BriefingPoint(700, 180) },
        new() { Op = BriefingOp.On, Id = "OBJPIN1" },
        new() { Op = BriefingOp.WaitForMarker, Index = 1 },
        new() { Op = BriefingOp.Objective, Id = "ZEPTEXT1", Index = 0 },
        new() { Op = BriefingOp.On, Id = "ZEPTEXT1" },
    };
}

/// <summary>The objectives note: which <c>objectives.zrd</c> blocks become lines, in what order,
/// and what an unresolved message key shows.</summary>
public class BriefingObjectivesTests
{
    /// <summary>Priority is the order, not file order: the shipped strings carry their own
    /// numbering and only priority reproduces it.</summary>
    [Fact]
    public void TheNoteIsOrderedByPriorityAndSkipsTheKeylessBlocks()
    {
        var lines = BriefingObjectives.Load(Reader(), new Messages());

        Assert.Equal(new[] { "MSG_A", "MSG_B" }, Keys(lines));
        Assert.Equal(new[] { 1, 2 }, Priorities(lines));
    }

    /// <summary>⚠ A line's number is its <c>OBJECTIVEn</c> block, never its priority: the two are
    /// inverted here, and a screen that asks the objectives runtime by priority marks the wrong
    /// row.</summary>
    [Fact]
    public void EachLineCarriesTheNumberOfTheBlockItWasReadFrom()
    {
        var lines = BriefingObjectives.Load(Reader(), new Messages());

        Assert.Equal(new[] { 2, 1 }, Numbers(lines));
        Assert.Equal(new[] { 1, 2 }, Priorities(lines));
    }

    /// <summary>Without a message table the screen shows the raw key rather than crashing or
    /// going blank.</summary>
    [Fact]
    public void AnUnresolvedKeyFallsBackToTheKeyItself()
    {
        var lines = BriefingObjectives.Load(Reader(), new Messages());

        Assert.Equal("MSG_A", lines[0].Text);
    }

    /// <summary>⚠ One block may author more than one <c>IDENTITY</c> (C4/M05's OBJECTIVE23 does),
    /// and both are note lines; keeping one per block loses that mission's third.</summary>
    [Fact]
    public void BothIdentitiesOfADoublyIdentifiedBlockBecomeLines()
    {
        var reader = new List<object?>
        {
            new List<object?>
            {
                "OBJECTIVE23", new List<object?>
                {
                    "IDENTITY", new List<object?> { "PRIMARY", 3f, "MSG_C" },
                    "BEGIN_DORMANT", new List<object?> { -1f },
                    "IDENTITY", new List<object?> { "PRIMARY", 1f, "MSG_A" },
                },
            },
        };

        var lines = BriefingObjectives.Load(reader, new Messages());

        Assert.Equal(new[] { "MSG_A", "MSG_C" }, Keys(lines));
        Assert.Equal(new[] { 23, 23 }, Numbers(lines));
    }

    /// <summary>A bare flag between two blocks shifts the reader's pairs, so a walk that steps by
    /// two silently reads the wrong halves after it.</summary>
    [Fact]
    public void ABareFlagBetweenBlocksDoesNotShiftTheRest()
    {
        var reader = new List<object?>
        {
            new List<object?>
            {
                "MISSION_TIMER",
                "OBJECTIVE1", new List<object?>
                {
                    "IDENTITY", new List<object?> { "PRIMARY", 1f, "MSG_A" },
                },
            },
        };

        Assert.Equal(new[] { "MSG_A" }, Keys(BriefingObjectives.Load(reader, new Messages())));
    }

    [Fact]
    public void AReaderWithNoObjectivesIsAnEmptyNote()
    {
        Assert.Empty(BriefingObjectives.Load(new List<object?>(), new Messages()));
    }

    // Two keyed objectives out of priority order plus a keyless one, which is the shape the
    // shipped missions have.
    private static List<object?> Reader() => new()
    {
        new List<object?>
        {
            "OBJECTIVE0", new List<object?> { "IDENTITY", new List<object?> { "SECONDARY", 11f } },
            "OBJECTIVE1", new List<object?> { "IDENTITY", new List<object?> { "PRIMARY", 2f, "MSG_B" } },
            "OBJECTIVE2", new List<object?> { "IDENTITY", new List<object?> { "PRIMARY", 1f, "MSG_A" } },
        },
    };

    private static string[] Keys(IReadOnlyList<BriefingObjective> lines)
    {
        var keys = new string[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            keys[i] = lines[i].Key;
        }

        return keys;
    }

    private static int[] Numbers(IReadOnlyList<BriefingObjective> lines)
    {
        var numbers = new int[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            numbers[i] = lines[i].Number;
        }

        return numbers;
    }

    private static int[] Priorities(IReadOnlyList<BriefingObjective> lines)
    {
        var priorities = new int[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            priorities[i] = lines[i].Priority;
        }

        return priorities;
    }
}
