using CSVM.Flight.Camera;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The spectator camera's re-lock rule (<see cref="OrbitLock"/>, `BL-428`) off-engine: from no
/// lock it answers the nearest target, from a held one it steps outward, it wraps past the far
/// end so no press is ever a dead one, and equal-range targets still get distinct turns.
/// </summary>
public class OrbitLockTests
{
    // Eye at the origin; A is nearest, C sits between, B is farthest. Deliberately NOT in
    // distance order, so a rule that just returned `current + 1` would fail these.
    private static readonly Vector3[] Targets =
    {
        new(10f, 0f, 0f),   // 0: A, nearest
        new(30f, 0f, 0f),   // 1: B, farthest
        new(20f, 0f, 0f),   // 2: C, between
    };

    [Fact]
    public void NothingToLockAnswersNoTarget()
    {
        Assert.Equal(-1, OrbitLock.Next(System.Array.Empty<Vector3>(), Vector3.Zero, current: -1));
    }

    [Fact]
    public void FromNoLockTakesTheNearest()
    {
        Assert.Equal(0, OrbitLock.Next(Targets, Vector3.Zero, current: -1));
    }

    [Fact]
    public void FromTheNearestStepsOutwardNotAlongTheList()
    {
        // C (index 2) is the next one out, even though B (index 1) is next in the list.
        Assert.Equal(2, OrbitLock.Next(Targets, Vector3.Zero, current: 0));
        Assert.Equal(1, OrbitLock.Next(Targets, Vector3.Zero, current: 2));
    }

    [Fact]
    public void PastTheFarthestWrapsBackToTheNearest()
    {
        Assert.Equal(0, OrbitLock.Next(Targets, Vector3.Zero, current: 1));
    }

    [Fact]
    public void TheEyeDecidesTheOrderNotTheOrigin()
    {
        // Standing beyond B reverses the ranking, so the nearest is now B.
        Assert.Equal(1, OrbitLock.Next(Targets, new Vector3(40f, 0f, 0f), current: -1));
    }

    [Fact]
    public void ATargetNotInTheListIsTreatedAsNoLock()
    {
        // A stale index (the plane it named has since gone) must not strand the key.
        Assert.Equal(0, OrbitLock.Next(Targets, Vector3.Zero, current: 7));
    }

    [Fact]
    public void EqualRangeTargetsEachGetATurn()
    {
        var tied = new[] { new Vector3(5f, 0f, 0f), new Vector3(-5f, 0f, 0f) };

        Assert.Equal(0, OrbitLock.Next(tied, Vector3.Zero, current: -1));
        Assert.Equal(1, OrbitLock.Next(tied, Vector3.Zero, current: 0));
        Assert.Equal(0, OrbitLock.Next(tied, Vector3.Zero, current: 1));
    }
}
