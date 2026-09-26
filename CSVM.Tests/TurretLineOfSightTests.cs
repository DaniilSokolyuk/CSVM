using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// A carried gunner's cached line-of-sight test (docs/formats/turrets.md) now runs through
/// <see cref="TurretController.WorldBlocksLine"/> against whatever <see cref="IWorldQuery"/> it was
/// built with, rather than through a cast back to its host's concrete type. Asserted here with a
/// synthetic <see cref="IWorldQuery"/> and no live node in the process: the blocked and the clear
/// case, and that the mask and exclusion mirror <c>FlightController.WorldBlocksLine</c> exactly
/// (world layer, no exclusion).
/// </summary>
public class TurretLineOfSightTests
{
    [Fact]
    public void AClearPathIsNotBlocked()
    {
        var world = new FakeWorldQuery(hit: false);
        Assert.False(TurretController.WorldBlocksLine(world, Vector3.Zero, Vector3.Forward * 100f));
    }

    [Fact]
    public void AWorldHitBlocksTheLine()
    {
        var world = new FakeWorldQuery(hit: true);
        Assert.True(TurretController.WorldBlocksLine(world, Vector3.Zero, Vector3.Forward * 100f));
    }

    [Fact]
    public void TheMaskIsWorldOnlyWithNoExclusion()
    {
        var world = new FakeWorldQuery(hit: false);
        TurretController.WorldBlocksLine(world, Vector3.Zero, Vector3.Forward * 100f);
        Assert.Equal(CollisionLayers.World, world.LastMask);
        Assert.Null(world.LastExclude);
    }

    // A synthetic IWorldQuery: Ray reports the fixed hit/miss it was built with and records the
    // mask/exclusion it was called with, so a test can pin the call shape with no physics world.
    private sealed class FakeWorldQuery : IWorldQuery
    {
        private readonly bool _hit;

        public FakeWorldQuery(bool hit) => _hit = hit;

        public uint LastMask { get; private set; }

        public Godot.Collections.Array<Rid>? LastExclude { get; private set; }

        public bool Sweep(IReadOnlyList<PlaneCollider.Part> parts, Transform3D baseTransform,
            Vector3 motion, uint mask, Godot.Collections.Array<Rid>? exclude, out SweepReport report)
        {
            report = default;
            return false;
        }

        public bool Ray(Vector3 from, Vector3 to, uint mask, Godot.Collections.Array<Rid>? exclude,
            out RayReport report)
        {
            LastMask = mask;
            LastExclude = exclude;
            report = _hit ? new RayReport(to, Vector3.Up, null) : default;
            return _hit;
        }

        public bool Overlaps(IReadOnlyList<PlaneCollider.Part> parts, Transform3D pose, uint mask,
            Godot.Collections.Array<Rid>? exclude) => false;
    }
}
