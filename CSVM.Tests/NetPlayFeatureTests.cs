using System;
using System.Collections.Generic;
using CSVM.Net;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The multiplayer door off-engine: the two fields a board edits, the four operations, the
/// readouts a board draws, and what a launch takes off it. The carrier is a loopback mesh and the
/// router is a stub, which is the whole point of the feature taking both as delegates.
/// </summary>
public class NetPlayFeatureTests
{
    private static readonly LoopbackConditions Clean = new(0.0, 0.0, 0.0);

    [Fact]
    public void ADoorStartsShutOnItsOwnDefaults()
    {
        var door = Door();

        Assert.Equal(NetDoorStage.Shut, door.Stage);
        Assert.Equal(NetPlayFeature.DefaultPort, door.Port);
        Assert.Equal(NetPlayFeature.DefaultAddress, door.Address);
        Assert.Equal("", door.Fault);
        Assert.Null(door.PortMap);
        Assert.Null(door.Link);
        Assert.False(door.CanLaunch);
        Assert.False(door.IsHost);
        Assert.Equal(0, door.Peers);
    }

    [Fact]
    public void ThePortStepsInsideTheUnprivilegedRangeAndWrapsAtBothEnds()
    {
        var door = Door();

        door.StepPort(1);
        Assert.Equal(NetPlayFeature.DefaultPort + 1, door.Port);

        door.StepPort(-1);
        door.StepPort(-(NetPlayFeature.DefaultPort - 1024));
        Assert.Equal(1024, door.Port);

        door.StepPort(-1);
        Assert.Equal(65535, door.Port);

        door.StepPort(1);
        Assert.Equal(1024, door.Port);
    }

    [Fact]
    public void TheAddressTakesOnlyWhatAnAddressIsWrittenWith()
    {
        var door = Door();
        while (door.Address.Length > 0)
        {
            door.EraseAddress();
        }

        door.TypeAddress("10.0.0.7");
        door.TypeAddress("/");
        door.TypeAddress(" ");
        Assert.Equal("10.0.0.7", door.Address);

        door.EraseAddress();
        Assert.Equal("10.0.0.", door.Address);

        door.TypeAddress(new string('9', NetPlayFeature.AddressLimit));
        Assert.Equal(NetPlayFeature.AddressLimit, door.Address.Length);
    }

    [Fact]
    public void HostingOpensTheSocketAndAsksTheRouterForThePortOnce()
    {
        int asked = 0;
        var mapped = new UpnpPortMapResult(UpnpPortMapOutcome.Mapped, 47500, "203.0.113.9", "mapped");
        var door = new NetPlayFeature(
            (_, _, _) => LoopbackTransport.Mesh(1, Clean, new Random(1))[0],
            (_, _) => throw new InvalidOperationException("a host does not join"),
            port =>
            {
                asked++;
                return mapped with { Port = port };
            },
            _ => { });

        door.OpenHost(7);
        Assert.Equal(NetDoorStage.Hosting, door.Stage);
        Assert.True(door.IsHost);
        Assert.True(door.CanLaunch);

        // The mapping is asked for away from the frame, so it lands on a step rather than inside
        // the open. The wait is a wall-clock deadline. The pool thread running the mapping
        // starves under the parallel unit run, where a fixed step count failed.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (door.PortMap == null && DateTime.UtcNow < deadline)
        {
            door.Step(0.016);
            System.Threading.Thread.Sleep(1);
        }

        Assert.Equal(1, asked);
        Assert.True(door.PortMap!.Value.IsMapped);
        Assert.Equal(NetPlayFeature.DefaultPort, door.PortMap!.Value.Port);
        Assert.Equal("203.0.113.9", door.PortMap!.Value.ExternalAddress);
    }

    [Fact]
    public void ASocketThatWillNotOpenLeavesTheDoorShutWithTheReasonOnIt()
    {
        var door = new NetPlayFeature(
            (port, _, _) => throw new InvalidOperationException($"cannot host on {port}"),
            (address, _) => throw new InvalidOperationException($"cannot join {address}"));

        door.OpenHost(7);
        Assert.Equal(NetDoorStage.Failed, door.Stage);
        Assert.Contains("cannot host", door.Fault, StringComparison.Ordinal);
        Assert.False(door.CanLaunch);
        Assert.Null(door.BuildLaunch());

        door.OpenJoin();
        Assert.Equal(NetDoorStage.Failed, door.Stage);
        Assert.Contains("cannot join", door.Fault, StringComparison.Ordinal);
    }

    [Fact]
    public void AJoinLandsOnAStepAndTheLaunchTakesTheWire()
    {
        var mesh = LoopbackTransport.Mesh(2, Clean, new Random(11));
        var door = new NetPlayFeature(
            (_, _, _) => mesh[0],
            (_, _) => mesh[1]);

        door.OpenJoin();
        Assert.Equal(NetDoorStage.Joining, door.Stage);
        Assert.False(door.CanLaunch);

        door.Step(0.016);
        Assert.Equal(NetDoorStage.Joined, door.Stage);
        Assert.Equal(1, door.Peers);
        Assert.True(door.CanLaunch);

        var launch = door.BuildLaunch();
        Assert.NotNull(launch);
        Assert.Same(mesh[1], launch!.Transport);
        Assert.False(launch.IsHost);
    }

    [Fact]
    public void AJoinNobodyAnswersGivesUpAndSaysSo()
    {
        var mesh = LoopbackTransport.Mesh(1, Clean, new Random(3));
        var door = new NetPlayFeature((_, _, _) => mesh[0], (_, _) => mesh[0]);

        door.OpenJoin();
        for (int i = 0; i < 10; i++)
        {
            door.Step(NetPlayFeature.JoinTimeoutSeconds / 8.0);
        }

        Assert.Equal(NetDoorStage.Failed, door.Stage);
        Assert.Contains("did not answer", door.Fault, StringComparison.Ordinal);
    }

    [Fact]
    public void ClosingGivesTheRoutersPortBackWhetherOrNotALaunchTookTheWire()
    {
        var given = new List<int>();
        var mesh = LoopbackTransport.Mesh(1, Clean, new Random(5));
        var door = new NetPlayFeature(
            (_, _, _) => mesh[0],
            (_, _) => mesh[0],
            port => new UpnpPortMapResult(UpnpPortMapOutcome.Mapped, port, "198.51.100.4", "mapped"),
            given.Add);

        door.StepPort(3);
        door.OpenHost(7);
        Assert.NotNull(door.BuildLaunch());

        door.Close();
        Assert.Equal(NetDoorStage.Shut, door.Stage);
        Assert.Equal(new[] { NetPlayFeature.DefaultPort + 3 }, given);

        // The port and the address are the player's, not the socket's, so they survive the close.
        Assert.Equal(NetPlayFeature.DefaultPort + 3, door.Port);
    }

    [Fact]
    public void AnOpenDoorRefusesToEditTheFieldsUnderIt()
    {
        var mesh = LoopbackTransport.Mesh(1, Clean, new Random(7));
        var door = new NetPlayFeature((_, _, _) => mesh[0], (_, _) => mesh[0]);
        door.OpenHost(7);

        door.StepPort(5);
        door.TypeAddress("9");
        door.EraseAddress();

        Assert.Equal(NetPlayFeature.DefaultPort, door.Port);
        Assert.Equal(NetPlayFeature.DefaultAddress, door.Address);
    }

    [Fact]
    public void DiscardingTakesTheDoorBackToShutWithNoFaultLeftOnIt()
    {
        var door = new NetPlayFeature(
            (_, _, _) => throw new InvalidOperationException("no"),
            (_, _) => throw new InvalidOperationException("no"));
        door.OpenHost(7);
        Assert.Equal(NetDoorStage.Failed, door.Stage);

        door.Discard();
        Assert.Equal(NetDoorStage.Shut, door.Stage);
        Assert.Equal("", door.Fault);
    }

    // A door over a one-peer mesh with no router behind it, which is every case that does not
    // care what the carrier does.
    private static NetPlayFeature Door()
    {
        var mesh = LoopbackTransport.Mesh(1, Clean, new Random(17));
        return new NetPlayFeature((_, _, _) => mesh[0], (_, _) => mesh[0]);
    }
}
