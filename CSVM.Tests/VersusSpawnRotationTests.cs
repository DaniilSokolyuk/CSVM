using System;
using System.Collections.Generic;
using CSVM.Flight.Modes;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Dogfight respawn chooser (<see cref="VersusSpawnRotation"/>), off-engine: the opening
/// ledger, the point a downed seat may not come back to, the spacing rule against living seats,
/// the pull away from the field and from the killer in particular, and the replay a seeded
/// <see cref="Random"/> gives a pinned run. Like <see cref="VersusMatch"/> it is a plain class
/// reaching no <c>GD.*</c>; <see cref="Vector3"/> is a managed struct and needs no engine.
/// </summary>
public class VersusSpawnRotationTests
{
    // Eight points a kilometre apart on one line, the shape C1's own dogfight_ace list has (eight
    // scattered entries): far enough apart that "which one is roomiest" is unambiguous.
    private static readonly IReadOnlyList<SpawnPoint> Line = BuildLine(8, 1000f);

    [Fact]
    public void EverySeatOpensOnTheListEntryTheSpawnPickerWouldHaveGivenIt()
    {
        var rotation = Rotation(spawnBase: 6, seatCount: 4);

        Assert.Equal(6, rotation.IndexOf(0));
        Assert.Equal(7, rotation.IndexOf(1));
        Assert.Equal(0, rotation.IndexOf(2));
        Assert.Equal(1, rotation.IndexOf(3));
        Assert.Equal(8, rotation.PointCount);
        Assert.Equal(4, rotation.SeatCount);
    }

    [Fact]
    public void ADownedSeatNeverComesBackToThePointItDiedAt()
    {
        var rotation = Rotation(spawnBase: 0, seatCount: 2);
        var field = new Vector3?[] { null, Line[1].Position };

        for (int again = 0; again < 40; again++)
        {
            int wasAt = rotation.IndexOf(0);
            rotation.Choose(0, field, killer: 1);
            Assert.NotEqual(wasAt, rotation.IndexOf(0));
        }
    }

    [Fact]
    public void TheSeatNeverTakesAPointALivingSeatHolds()
    {
        var rotation = Rotation(spawnBase: 0, seatCount: 3);
        // Seats 1 and 2 opened on entries 1 and 2 and are both still flying, so the rotation owes
        // seat 0 a point that is neither of those, nor the entry 0 it was downed at.
        var field = new Vector3?[] { null, Line[1].Position, Line[2].Position };

        for (int again = 0; again < 40; again++)
        {
            rotation.Choose(0, field, killer: 1);
            Assert.NotEqual(1, rotation.IndexOf(0));
            Assert.NotEqual(2, rotation.IndexOf(0));
        }
    }

    [Fact]
    public void ThePickIsFarFromTheFieldAndFurtherStillFromTheKiller()
    {
        var rotation = Rotation(spawnBase: 0, seatCount: 2);
        // The killer sits over entry 1, near the low end of the line, so the roomy candidates are
        // all at the far end of it: entry 7 is 6 km off while entries 0, 2 and 3 are 2 km or less.
        var killerAt = Line[1].Position;
        var field = new Vector3?[] { null, killerAt };

        for (int again = 0; again < 40; again++)
        {
            rotation.Choose(0, field, killer: 1);
            float away = Line[rotation.IndexOf(0)].Position.DistanceTo(killerAt);
            Assert.True(away >= 3000f,
                $"a respawn {away:0} m from the killer was taken (entry {rotation.IndexOf(0)})");
        }
    }

    [Fact]
    public void TheKillerOutweighsAnEquallyDistantBystander()
    {
        // Two opponents 1.5 km either side of the downed seat's own point, the killer low and a
        // bystander high. Unweighted, the low end of the line (entry 0) reads as roomy as the high
        // end and joins the draw; weighted, the killer's 1.5 km counts as 750 m and rules it out.
        var rotation = Rotation(spawnBase: 3, seatCount: 3);
        var field = new Vector3?[] { null, new Vector3(1500f, 500f, 0f), new Vector3(4500f, 500f, 0f) };

        for (int again = 0; again < 40; again++)
        {
            rotation.Choose(0, field, killer: 1);
            Assert.True(rotation.IndexOf(0) >= 6,
                $"the pick fell on the killer's side of the line: entry {rotation.IndexOf(0)}");
        }
    }

    [Fact]
    public void ASeededRunReplaysExactly()
    {
        var field = new Vector3?[] { null, Line[1].Position, Line[2].Position };
        var first = new List<int>();
        var second = new List<int>();

        foreach (var take in new[] { first, second })
        {
            var rotation = Rotation(spawnBase: 0, seatCount: 3);
            for (int again = 0; again < 25; again++)
            {
                rotation.Choose(0, field, killer: 1);
                take.Add(rotation.IndexOf(0));
            }
        }

        Assert.Equal(first, second);
        // A rotation that always answered the same entry would satisfy the replay above without
        // rotating at all, so the run must also visit more than one point.
        Assert.True(new HashSet<int>(first).Count > 1, "the rotation never moved off one entry");
    }

    [Fact]
    public void ARestartOpensOnTheOpeningSpawnsAgain()
    {
        var rotation = Rotation(spawnBase: 0, seatCount: 2);
        var field = new Vector3?[] { null, Line[1].Position };
        rotation.Choose(0, field, killer: 1);
        Assert.NotEqual(0, rotation.IndexOf(0));

        rotation.Restart();
        Assert.Equal(0, rotation.IndexOf(0));
        Assert.Equal(1, rotation.IndexOf(1));

        // The first respawn of the fresh round is the opening point, and the one after it rotates.
        Assert.Equal(Line[0].Position, rotation.Choose(0, field, killer: 1).Position);
        rotation.Choose(0, field, killer: 1);
        Assert.NotEqual(0, rotation.IndexOf(0));
    }

    [Fact]
    public void AnEmptySkyStillRotatesAndAnEmptyListBuildsNothing()
    {
        var rotation = Rotation(spawnBase: 0, seatCount: 1);
        var field = new Vector3?[] { null };
        var visited = new HashSet<int>();
        for (int again = 0; again < 25; again++)
        {
            rotation.Choose(0, field, killer: null);
            visited.Add(rotation.IndexOf(0));
        }

        Assert.True(visited.Count > 1, "with nobody to be far from the draw is the whole rotation");
        Assert.Null(VersusSpawnRotation.For(null, 0, 2, new Random(1)));
        Assert.Null(VersusSpawnRotation.For(Array.Empty<SpawnPoint>(), 0, 2, new Random(1)));
    }

    [Fact]
    public void HeadingBecomesTheNoseAxisAPlacementAimsAlong()
    {
        var due = new SpawnPoint(Vector3.Zero, 0f).Forward;
        var quarter = new SpawnPoint(Vector3.Zero, 90f).Forward;

        // Heading 0 is the model's own nose axis, -Z; +90 degrees yaws about up onto -X.
        Assert.True(due.IsEqualApprox(Vector3.Forward));
        Assert.True(quarter.IsEqualApprox(Vector3.Left));
    }

    private static VersusSpawnRotation Rotation(int spawnBase, int seatCount) =>
        VersusSpawnRotation.For(Line, spawnBase, seatCount, new Random(20260913))!;

    private static IReadOnlyList<SpawnPoint> BuildLine(int count, float spacing)
    {
        var points = new SpawnPoint[count];
        for (int i = 0; i < count; i++)
            points[i] = new SpawnPoint(new Vector3(i * spacing, 500f, 0f), i * 45f);
        return points;
    }
}
