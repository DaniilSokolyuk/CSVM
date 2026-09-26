using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Modes;
using CSVM.Session.Roster;
using CSVM.Utils;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The abreast starting grid (<see cref="StartGrid"/>): where each pilot's slot lands, which way it
/// faces, and how far the field is raised to clear the ground. See <c>docs/architecture.md</c>
/// for why lift-by-worst-slot has no visible symptom otherwise. Terrain is a synthetic function
/// of slot position, which is what makes the geometry testable off-engine.
/// </summary>
public class StartGridTests
{
    // The grid's own spacing and clearance, restated so a change to either fails here
    // rather than passing silently. Both are config fallbacks now; these tests run with no
    // config.json, which is the state a scripted run and a fresh checkout are also in.
    private const float Spacing = 60f;
    private const float Clearance = 100f;

    // An anchor high above everything, so the ground never asks for lift.
    private static readonly Vector3 HighAnchor = new(1000f, 5000f, -2000f);

    // ---- The fan -------------------------------------------------------------------------------

    /// <summary>Centred on the anchor and evenly spaced, whatever the count: the offsets are
    /// (i - (n-1)/2) spacings, so an even field straddles the anchor and an odd field puts its
    /// middle plane exactly on it.</summary>
    [Theory]
    [InlineData(1, new[] { 0f })]
    [InlineData(2, new[] { -30f, 30f })]
    [InlineData(3, new[] { -60f, 0f, 60f })]
    [InlineData(4, new[] { -90f, -30f, 30f, 90f })]
    public void SlotsAreCentredOnTheAnchorAndEvenlySpaced(int players, float[] expectedOffsets)
    {
        // Heading 0° faces -Z, so the line across it is +X and the offsets read straight off X.
        var starts = Grid(Flat(0f)).ChooseStarts(
            Spawns(HighAnchor, 0f), "", 0, players);

        Assert.Equal(players, starts.Count);
        for (int i = 0; i < players; i++)
        {
            Assert.Equal(HighAnchor.X + expectedOffsets[i], starts[i].Pos.X, 3);
            Assert.Equal(HighAnchor.Z, starts[i].Pos.Z, 3);
        }
        // Centred: the slots sum to the anchor, and no gap differs from any other.
        Assert.Equal(HighAnchor.X, starts.Average(s => s.Pos.X), 3);
        for (int i = 1; i < players; i++)
        {
            Assert.Equal(Spacing, starts[i].Pos.X - starts[i - 1].Pos.X, 3);
        }
    }

    /// <summary>The line is perpendicular to the anchor's heading, not to a world axis, asserted on
    /// an off-axis heading, where "the slots differ in X" would pass while the fan was wrong.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TheFanLiesAcrossTheAnchorHeading(int players)
    {
        const float Heading = 37f;
        var starts = Grid(Flat(0f)).ChooseStarts(
            Spawns(HighAnchor, Heading), "", 0, players);

        var nose = Forward(Heading);
        foreach (var s in starts)
        {
            var offset = s.Pos - HighAnchor;
            // As a direction, not a length: the anchor is kilometres from the origin, so a metre
            // offset differenced out of those coordinates carries millimetres of float noise.
            Assert.Equal(0f, offset.Normalized().Dot(nose), 3);   // across the heading, never along it
            Assert.Equal(0f, offset.Y, 3);                        // and level: the line is horizontal
        }
        for (int i = 1; i < players; i++)
        {
            Assert.Equal(Spacing, starts[i].Pos.DistanceTo(starts[i - 1].Pos), 3);
        }
    }

    /// <summary>Every pilot faces the way the anchor faces, one heading for the whole field, and
    /// the anchor's own, not a rederived one.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(37f)]
    [InlineData(180f)]
    public void EveryPlaneIsOnTheAnchorHeading(float headingDeg)
    {
        var starts = Grid(Flat(0f)).ChooseStarts(
            Spawns(HighAnchor, headingDeg), "", 0, 4);

        var nose = Forward(headingDeg);
        foreach (var s in starts)
        {
            var actual = (s.LookAt - s.Pos).Normalized();
            Assert.Equal(nose.X, actual.X, 3);
            Assert.Equal(nose.Y, actual.Y, 3);
            Assert.Equal(nose.Z, actual.Z, 3);
        }
    }

    // ---- The lift ------------------------------------------------------------------------------

    /// <summary>
    /// Three slots over three ground heights picked so the two candidate rules disagree: lift by
    /// worst slot puts all three at 550 m; lift per plane would put them at 500 / 550 / 500.
    /// </summary>
    [Fact]
    public void TheFieldIsLiftedByTheWorstSlotNeverPerPlane()
    {
        var anchor = new Vector3(0f, 500f, 0f);
        // Heading 0° ⇒ slots at x = -60, 0, +60. The middle one is the worst: it needs
        // 450 + 100 - 500 = 50 m. The outer two need none (300 and 100 leave them clear already).
        var ground = Heights(new Dictionary<float, float> { [-60f] = 300f, [0f] = 450f, [60f] = 100f });

        var starts = Grid(ground).ChooseStarts(Spawns(anchor, 0f), "", 0, 3);

        Assert.Equal(550f, starts[0].Pos.Y, 3);
        Assert.Equal(550f, starts[1].Pos.Y, 3);
        Assert.Equal(550f, starts[2].Pos.Y, 3);
        // Said again as the property rather than the numbers: one altitude for the whole field.
        Assert.Single(starts.Select(s => MathF.Round(s.Pos.Y, 3)).Distinct());
        // And the look-at rises with it, so the field stays level after the lift.
        foreach (var s in starts)
        {
            Assert.Equal(s.Pos.Y, s.LookAt.Y, 3);
        }
    }

    /// <summary>Ground everyone already clears asks for nothing: the field stays exactly where the
    /// spawn data put it. Without this, a lift that always fired would pass the worst-slot test.</summary>
    [Fact]
    public void GroundWellBelowTheFieldDoesNotMoveIt()
    {
        var anchor = new Vector3(0f, 5000f, 0f);
        var starts = Grid(Heights(new Dictionary<float, float>
        {
            [-90f] = 0f,
            [-30f] = 400f,
            [30f] = 120f,
            [90f] = 90f,
        })).ChooseStarts(Spawns(anchor, 0f), "", 0, 4);

        foreach (var s in starts)
        {
            Assert.Equal(5000f, s.Pos.Y, 3);
        }
    }

    /// <summary>The worst slot decides even when it is an outer one, the lift is a maximum over
    /// every slot, not a probe of the anchor.</summary>
    [Fact]
    public void TheWorstSlotDecidesWhereverItSits()
    {
        var anchor = new Vector3(0f, 500f, 0f);
        // Only the far-left slot is high; the anchor's own column is clear.
        var starts = Grid(Heights(new Dictionary<float, float>
        {
            [-90f] = 900f,
            [-30f] = 0f,
            [30f] = 0f,
            [90f] = 0f,
        })).ChooseStarts(Spawns(anchor, 0f), "", 0, 4);

        foreach (var s in starts)
        {
            Assert.Equal(900f + Clearance, s.Pos.Y, 3);
        }
    }

    // ---- A sample that found nothing -----------------------------------------------------------

    /// <summary>A slot with no ground under it contributes no lift, but is reported. The field is
    /// still raised by whatever the slots that DID answer need, so one hole in the collision world
    /// cannot quietly drop a race into a hillside.</summary>
    [Fact]
    public void AnUnprobedSlotAsksForNoLiftAndSaysSo()
    {
        var anchor = new Vector3(0f, 500f, 0f);
        var lines = new List<string>();
        IReadOnlyList<FlightStart> starts;
        using (Log.PushConsoleSink(lines.Add))
        {
            // x = -90 finds nothing; x = -30 needs 550; the other two are clear.
            starts = Grid(p => MathF.Abs(p.X + 90f) < 1f
                ? (float?)null
                : p.X < -20f ? 450f : 0f).ChooseStarts(Spawns(anchor, 0f), "", 0, 4);
        }

        foreach (var s in starts)
        {
            Assert.Equal(550f, s.Pos.Y, 3);
        }
        Assert.Contains(lines, l => l.Contains("no ground under 1/4 slots", StringComparison.Ordinal));
    }

    /// <summary>Every sample coming back empty is the shape a physics space that has not ticked
    /// produces. The grid leaves the field on the authored spawn altitude rather than inventing a
    /// correction, and says so loudly enough that the absence is not mistaken for flat ground at
    /// sea level.</summary>
    [Fact]
    public void AFieldWithNoGroundAnywhereIsLeftWhereTheSpawnPutItAndReported()
    {
        var anchor = new Vector3(0f, 500f, 0f);
        var lines = new List<string>();
        IReadOnlyList<FlightStart> starts;
        using (Log.PushConsoleSink(lines.Add))
        {
            starts = Grid(_ => null).ChooseStarts(Spawns(anchor, 0f), "", 0, 4);
        }

        foreach (var s in starts)
        {
            Assert.Equal(500f, s.Pos.Y, 3);
        }
        Assert.Contains(lines, l => l.Contains("no ground under 4/4 slots", StringComparison.Ordinal));
    }

    /// <summary>A silent zero is exactly what the null case must not be: ground found AT sea level
    /// and ground not found at all give different answers, so the log line is not the only
    /// difference.</summary>
    [Fact]
    public void FoundSeaLevelGroundIsNotTheSameAsFindingNone()
    {
        var anchor = new Vector3(0f, 50f, 0f);
        var found = Grid(Flat(0f)).ChooseStarts(Spawns(anchor, 0f), "", 0, 2);
        var lines = new List<string>();
        IReadOnlyList<FlightStart> none;
        using (Log.PushConsoleSink(lines.Add))
        {
            none = Grid(_ => null).ChooseStarts(Spawns(anchor, 0f), "", 0, 2);
        }

        Assert.Equal(Clearance, found[0].Pos.Y, 3);   // ground found at 0 ⇒ raised to the clearance
        Assert.Equal(50f, none[0].Pos.Y, 3);     // left alone
    }

    // ---- The anchor is still the spawn picker's answer ------------------------------------------

    /// <summary>`--pos` still beats the spawn list through the grid: the override wins inside
    /// <c>ChooseSpawn</c>, and the grid centres on whatever came back, so the named point is the
    /// middle of the starting line and `--direction` is the field's heading.</summary>
    [Fact]
    public void PosOverrideReachesThroughTheGridAndCentresTheField()
    {
        var spec = SessionSpec.Parse(new[] { "--stunt", "--pos=100,700,-250", "--direction=1,0,0" });
        var grid = new StartGrid(new SpawnPicker(spec), Flat(0f));
        // A spawn list that would otherwise be used, to prove the override beats it rather than
        // there being nothing to beat.
        var spawns = new[] { new SpawnPoint(new Vector3(-9999f, 300f, 9999f), 123f) };

        var starts = grid.ChooseStarts(spawns, "", 0, 4);

        // Facing +X ⇒ the line runs along Z; the field's centre is the --pos point exactly.
        Assert.Equal(100f, starts.Average(s => s.Pos.X), 3);
        Assert.Equal(700f, starts.Average(s => s.Pos.Y), 3);
        Assert.Equal(-250f, starts.Average(s => s.Pos.Z), 3);
        foreach (var s in starts)
        {
            Assert.Equal(100f, s.Pos.X, 3);
            var nose = (s.LookAt - s.Pos).Normalized();
            Assert.Equal(1f, nose.X, 3);
            Assert.Equal(0f, nose.Z, 3);
        }
        Assert.Equal(Spacing, MathF.Abs(starts[1].Pos.Z - starts[0].Pos.Z), 3);
    }

    /// <summary>The anchor is player 0's spawn, so `--spawn=N` picks which list entry the whole
    /// field lines up on, and no other entry is consulted.</summary>
    [Fact]
    public void TheAnchorIsTheSpawnBaseEntryAndNoOther()
    {
        var spawns = new[]
        {
            new SpawnPoint(new Vector3(0f, 500f, 0f), 0f),
            new SpawnPoint(new Vector3(4000f, 800f, 1000f), 0f),
        };
        var starts = new StartGrid(new SpawnPicker(SessionSpec.Parse(new[] { "--stunt" })), Flat(0f))
            .ChooseStarts(spawns, "", 1, 4);

        Assert.Equal(4000f, starts.Average(s => s.Pos.X), 3);
        foreach (var s in starts)
        {
            Assert.Equal(800f, s.Pos.Y, 3);
            Assert.Equal(1000f, s.Pos.Z, 3);
        }
    }

    // ---- The values in force, and the report of them --------------------------------------------

    /// <summary>The two dialable values fall back to exactly the numbers the rest of this file
    /// asserts, so an absent config.json places a field identically to the consts that preceded it,
    /// and reading the key is what these tests are measuring, not a stale copy of the default.</summary>
    [Fact]
    public void TheConfigFallbacksAreTheGeometryTheseTestsAssert()
    {
        Assert.Equal(Spacing, StartGrid.SlotSpacingDefault, 3);
        Assert.Equal(Clearance, StartGrid.GroundClearanceDefault, 3);
        Assert.Equal(Spacing, Config.GetFloat("startGrid.slotSpacing", StartGrid.SlotSpacingDefault), 3);
        Assert.Equal(Clearance, Config.GetFloat("startGrid.groundClearance", StartGrid.GroundClearanceDefault), 3);
    }

    /// <summary>Every slot reports itself. This is the only instrument a hand-flown race has for the
    /// fan, the panes are chase-cam only, so a neighbour a spacing away is out of frame and no
    /// screenshot can show whether the field is abreast, level or evenly spaced. The line therefore
    /// has to carry the slot, the point, the field's lift and the spacing in force.</summary>
    [Fact]
    public void EverySlotIsReportedWithItsIndexPositionSpacingAndTheFieldsLift()
    {
        var anchor = new Vector3(0f, 500f, 0f);
        var lines = new List<string>();
        using (Log.PushConsoleSink(lines.Add))
        {
            // Flat ground at 450 ⇒ the whole field lifts 50 m, to y = 550.
            Grid(Flat(450f)).ChooseStarts(Spawns(anchor, 0f), "", 0, 4);
        }

        for (int i = 0; i < 4; i++)
        {
            float x = (i - 1.5f) * Spacing;
            string expected = $"spawn [P{i + 1} grid slot {i + 1} of 4] pos=({x:0},550,0) " +
                $"heading=0° spacing=60m lift=50m";
            Assert.Contains(lines, l => l.Contains(expected, StringComparison.Ordinal));
        }
    }

    /// <summary>The reported heading is the field's real one, asserted off-axis where a hardcoded
    /// zero would still read as plausible.</summary>
    [Fact]
    public void TheReportedHeadingIsTheAnchorHeading()
    {
        var lines = new List<string>();
        using (Log.PushConsoleSink(lines.Add))
        {
            Grid(Flat(0f)).ChooseStarts(Spawns(HighAnchor, 37f), "", 0, 2);
        }

        // The anchor's own line carries the heading too, so the count is qualified to slot lines.
        Assert.Equal(2, lines.Count(l => l.Contains("grid slot", StringComparison.Ordinal)
            && l.Contains("heading=37°", StringComparison.Ordinal)));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    // A grid over a picker with no `--pos` override, so the anchor comes from the
    // spawn list handed to `ChooseStarts`.
    private static StartGrid Grid(Func<Vector3, float?> ground) =>
        new(new SpawnPicker(SessionSpec.Parse(new[] { "--stunt" })), ground);

    private static IReadOnlyList<SpawnPoint> Spawns(Vector3 pos, float headingDeg) =>
        new[] { new SpawnPoint(pos, headingDeg) };

    // Ground at one height everywhere.
    private static Func<Vector3, float?> Flat(float y) => _ => y;

    // A synthetic heightfield keyed on the slot's X offset from the origin, the axis the
    // fan runs along at heading 0°. An unlisted column is at sea level.
    private static Func<Vector3, float?> Heights(Dictionary<float, float> byX) => p =>
    {
        foreach (var (x, h) in byX)
        {
            if (MathF.Abs(p.X - x) < 1f)
            {
                return h;
            }
        }
        return 0f;
    };

    // The nose direction a spawn heading means, built the way `SpawnPicker` builds
    // it, so a change to that convention fails these tests rather than sliding past them.
    private static Vector3 Forward(float headingDeg) =>
        (new Basis(Vector3.Up, Mathf.DegToRad(headingDeg)) * Vector3.Forward).Normalized();
}
