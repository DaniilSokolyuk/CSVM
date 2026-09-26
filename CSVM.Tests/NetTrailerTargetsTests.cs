using System;
using CSVM.Mech3;
using CSVM.Session.Objectives;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The session half: turning a net's trailer NAME into a live position supplier.
/// Engine-free, the world-node arm needs a scene tree and is exercised by the
/// <c>ai-net-trailer</c> engine suite; what is checked here is which nets get a supplier at all,
/// which is the decision that decides whether a net rides or stays put.
/// </summary>
public class NetTrailerTargetsTests
{
    [Fact]
    public void OnlyAnAnchoredNamedTrailerGetsASupplier()
    {
        var targets = new NetTrailerTargets(() => new Vector3(100f, 0f, 200f), null);
        Assert.NotNull(targets.For(Net(new AiNetTrailer(4, "player"))));
        Assert.Null(targets.For(Net(null)));                            // 133 + 8 shipped nets
        Assert.Null(targets.For(Net(new AiNetTrailer(-1, "piratezep")))); // 4: named, unattached
        Assert.Null(targets.For(Net(new AiNetTrailer(3, null))));         // 1: C2 net 33, [3]
    }

    [Fact]
    public void PlayerResolvesToTheRigAndTheOffsetFollowsIt()
    {
        var player = new Vector3(3000f, 900f, -1500f);
        var targets = new NetTrailerTargets(() => player, null);
        var net = Net(new AiNetTrailer(4, "PLAYER"));   // the join key is case-insensitive
        Assert.NotNull(targets.For(net));
        // Anchor node 4 is at (500,500): offset = target − anchor in XZ, and zero in Y.
        Assert.Equal(new Vector3(2500f, 0f, -2000f), targets.OffsetOf(net));
        player = new Vector3(500f, 0f, 500f);
        Assert.Equal(Vector3.Zero, targets.OffsetOf(net));
    }

    [Fact]
    public void NoPlayerRigAndNoWorldLookupLeaveTheNetWhereItWasAuthored()
    {
        // A --fly session with neither: the engine's own unresolved-target branch.
        var targets = new NetTrailerTargets(null, null);
        Assert.Null(targets.For(Net(new AiNetTrailer(4, "player"))));
        Assert.Null(targets.For(Net(new AiNetTrailer(4, "piratezep"))));
        Assert.Equal(Vector3.Zero, targets.OffsetOf(Net(new AiNetTrailer(4, "player"))));
    }

    private static AiNet Net(AiNetTrailer? trailer) => new()
    {
        Id = 10,
        Name = "TestAnchored",
        Nodes = new[]
        {
            Node(0f, 0f), Node(1000f, 0f), Node(1000f, 1000f), Node(0f, 1000f), Node(500f, 500f),
        },
        Edges = new[] { (0, 1), (1, 2), (2, 3), (0, 3) },
        Trailer = trailer,
    };

    private static AiNetNode Node(float x, float z) =>
        new(new Vector3(x, 400f, z), Array.Empty<float>());
}
