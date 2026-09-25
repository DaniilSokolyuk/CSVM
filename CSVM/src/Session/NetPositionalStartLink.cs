using System;
using System.Collections.Generic;
using CSVM.Flight;
using CSVM.Net;
using CSVM.Utils;

namespace CSVM.Session;

/// <summary>
/// The positional starts over the wire. The host's landing trigger and ladder switch read every
/// seat, its own panes and each guest's copy. Each row start and holder change goes out as a
/// reliable <see cref="PositionalStartMessage"/>. A guest's trigger and switch are replicated:
/// they start a row for the seat the host named and step the ladder on the host's holder. A guest
/// still offers the auto-land prompt to its own humans and tells the host when their button is
/// held. A guest derives what the row then plays, the airframe swap included, from its own
/// playback (<c>docs/org/multiplayer-messages.md</c>).
/// </summary>
internal sealed class NetPositionalStartLink
{
    private readonly NetSession _net;
    private readonly Func<IReadOnlyList<PlayerRig>> _seats;
    private readonly LandingApproachRuntime? _landings;
    // A guest's own seats whose held button the host was last told about.
    private readonly HashSet<int> _told = new();

    private NetPositionalStartLink(NetSession net, Func<IReadOnlyList<PlayerRig>> seats,
        LandingApproachRuntime? landings)
    {
        _net = net;
        _seats = seats;
        _landings = landings;
    }

    /// <summary>Row starts a guest replayed and a host's link sent, for a suite and the log.</summary>
    internal int RowStarts { get; private set; }

    /// <summary>Row starts a guest could not replay because its world never bound the row.</summary>
    internal int RowsRefused { get; private set; }

    /// <summary>Wires both runtimes to <paramref name="net"/>. <paramref name="seats"/> is the
    /// whole field in seat order, read live because a swap rebuilds a seat's controller.</summary>
    internal static NetPositionalStartLink Open(NetSession net, Func<IReadOnlyList<PlayerRig>> seats,
        LandingApproachRuntime? landings, LadderSwitchRuntime? ladder)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(seats);
        var link = new NetPositionalStartLink(net, seats, landings);
        if (net.IsHost)
        {
            link.Publish(ladder);
        }
        else
        {
            link.Follow(ladder);
        }

        return link;
    }

    /// <summary>A guest's half of the auto-land: each own seat's held button, sent when it changes.
    /// Run after the trigger's tick. Nothing on the host.</summary>
    internal void Step()
    {
        if (_net.IsHost || _landings is not { Replicated: true } landings)
        {
            return;
        }

        foreach (var rig in _seats())
        {
            if (rig.Controller is not { RemoteOwned: false })
            {
                continue;
            }

            bool held = landings.Pressing(rig.Index);
            if (held != _told.Contains(rig.Index))
            {
                _ = held ? _told.Add(rig.Index) : _told.Remove(rig.Index);
                _net.Send(_net.HostPeer, new PositionalStartMessage(
                    NetPositionalStart.AutoLandHeld, (byte)rig.Index, 0, held));
            }
        }
    }

    private void Publish(LadderSwitchRuntime? ladder)
    {
        if (_landings != null)
        {
            _landings.Started += (row, by) =>
            {
                RowStarts++;
                _net.Broadcast(new PositionalStartMessage(
                    NetPositionalStart.LandingRow, (byte)by.Index, row, false));
            };
        }

        if (ladder != null)
        {
            ladder.HolderChanged += holder => _net.Broadcast(new PositionalStartMessage(
                NetPositionalStart.LadderHolder, holder != null ? (byte)holder.Index : NetMessage.NoSeat, 0, false));
        }

        // ⚠ Only the seat's own machine speaks for its button. A relayed or forged seat would
        // otherwise start a row for a human who never pressed.
        _net.On<PositionalStartMessage>((peer, message) =>
        {
            if (message.Kind == NetPositionalStart.AutoLandHeld && _net.PeerOfSeat(message.Seat) == peer
                && SeatRig(message.Seat)?.Controller is { RemoteOwned: true } copy)
            {
                copy.RemoteAutoLand = message.Held;
            }
        });
    }

    private void Follow(LadderSwitchRuntime? ladder)
    {
        _landings?.Replicate();
        ladder?.Replicate();
        _net.On<PositionalStartMessage>((_, message) =>
        {
            switch (message.Kind)
            {
                case NetPositionalStart.LandingRow when _landings != null && SeatRig(message.Seat) is { } by:
                    if (_landings.StartRow(message.Row, by))
                    {
                        RowStarts++;
                    }
                    else
                    {
                        RowsRefused++;
                        Log.Warn("world", $"net landings: the host started row {message.Row} for seat {message.Seat}, which this world never bound");
                    }

                    break;
                case NetPositionalStart.LadderHolder when ladder != null:
                    ladder.TakeHolder(message.Seat == NetMessage.NoSeat ? null : SeatRig(message.Seat));
                    break;
            }
        });
    }

    private PlayerRig? SeatRig(int seat)
    {
        foreach (var rig in _seats())
        {
            if (rig.Index == seat)
            {
                return rig;
            }
        }

        return null;
    }
}
