using System.Collections.Generic;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>A Danger Zone as the targeting path sees it: an objective-flagged entry on the pilot's
/// Enemy cycle, labelled off the same <c>targets.zrd</c> triple every other objective site is, and
/// held by the zone OBJECT so two pilots racing the same course never share a pick.</summary>
public class StuntTargetTests
{
    private static readonly Vector3 Ahead = new(0f, 0f, -400f);

    /// <summary>The original's own stunt marker text, assembled out of the three display keys:
    /// "Danger Zone [Fly Through] -" over "Train Tunnel Mid".</summary>
    [Fact]
    public void AZoneCarriesTheOriginalsMarkerTextAsAnObjective()
    {
        var pool = new TargetPool();
        pool.Rebuild(new AimCandidateSet(), null, AimAssist.PlayerTeam, null,
            Offer(Zone("dz3", "Train Tunnel Mid")));

        var target = Assert.Single(pool.Enemy);
        Assert.True(target.Objective);
        Assert.Equal(TargetClass.Enemy, target.Class);
        Assert.Equal("Danger Zone [Fly Through] -", target.CategoryLine);
        Assert.Equal("Train Tunnel Mid", target.DisplayName);
        Assert.Equal("dz3", target.Name);
    }

    /// <summary>A zone with no <c>description</c> falls back to its own node name rather than
    /// printing nothing, the way an unnamed site does.</summary>
    [Fact]
    public void AZoneWithNoDescriptionPrintsItsNodeName()
    {
        var pool = new TargetPool();
        pool.Rebuild(new AimCandidateSet(), null, AimAssist.PlayerTeam, null,
            Offer(Zone("dz7", "")));

        Assert.Equal("dz7", Assert.Single(pool.Enemy).DisplayName);
    }

    /// <summary>A zone and a hostile share the one cycle, and only the zone carries the sort key
    /// that puts an objective ahead of every sector. That is what makes the Enemy/Objective step
    /// reach the zones first in a run that also has aircraft in it.</summary>
    [Fact]
    public void AZoneAndAnEnemyShareTheCycleAndOnlyTheZoneSortsFirst()
    {
        var pool = new TargetPool();
        var scan = new AimCandidateSet();
        scan.AddVehicle(new Vector3(0f, 0f, -100f), Vector3.Zero, 2, true, new object());
        pool.Rebuild(scan, null, AimAssist.PlayerTeam, null, Offer(Zone("dz1", "Passenger Hangar")));

        Assert.Equal(2, pool.Enemy.Count);
        foreach (var target in pool.Enemy)
        {
            Assert.Equal(target.Objective, target.SortsFirst);
        }
    }

    /// <summary>⚠ The per-pane rule this fold must not break: two pilots racing the same course hold
    /// their own <see cref="StuntZone"/> objects, so the selection (held by source identity) cannot
    /// be moved from one pane by a press in the other.</summary>
    [Fact]
    public void TwoPilotsZonesAreDistinctSourcesEvenWithTheSameName()
    {
        var mine = Zone("dz3", "Train Tunnel Mid");
        var theirs = Zone("dz3", "Train Tunnel Mid");
        var onePool = new TargetPool();
        var otherPool = new TargetPool();
        onePool.Rebuild(new AimCandidateSet(), null, AimAssist.PlayerTeam, null, Offer(mine));
        otherPool.Rebuild(new AimCandidateSet(), null, AimAssist.PlayerTeam, null, Offer(theirs));

        var one = Assert.Single(onePool.Enemy);
        var other = Assert.Single(otherPool.Enemy);
        Assert.Equal(one.Name, other.Name);
        Assert.False(one.IsSameTarget(other));
        Assert.True(one.IsSameTarget(one));
    }

    private static StuntZone Zone(string dzName, string description) => new()
    {
        DzName = dzName,
        Position = Ahead,
        Description = description,
        Category = "Danger Zone",
        Help = "Fly Through",
    };

    private static List<AimCandidate> Offer(StuntZone zone) => new()
    {
        new AimCandidate
        {
            Position = zone.Position,
            Team = AimAssist.NeutralTeam,
            Live = true,
            ConeOverride = AimAssist.NoConeOverride,
            Source = zone,
        },
    };
}
