using System;
using System.Threading.Tasks;
using CSVM.Net;

namespace CSVM.UI.Menu;

/// <summary>What the door is doing right now, the one word a board draws its state from.</summary>
public enum NetDoorStage
{
    /// <summary>No socket is open. The port and the address are editable.</summary>
    Shut,

    /// <summary>A listen server is open and admitting guests.</summary>
    Hosting,

    /// <summary>A join is on its way out and has not landed yet.</summary>
    Joining,

    /// <summary>A join landed: this end is linked to a host.</summary>
    Joined,

    /// <summary>The socket would not open, or the join was refused or timed out.</summary>
    Failed,
}

/// <summary>
/// The multiplayer door as a shared feature. It owns the port and the address a board edits, the
/// socket it opens, and the link and port-mapping readouts it shows. The wire a launch carries
/// away comes from here too. Nothing here names a carrier or an engine type: the two factories
/// and the two port-mapping calls arrive as delegates. The launcher therefore passes the real
/// ENet carrier and a suite passes a loopback one. A presentation offers the four operations
/// however it likes; the roster, the seats and the session are the launcher's.
/// </summary>
public sealed class NetPlayFeature : IMenuFeature
{
    /// <summary>The port a host opens on unless the board is stepped off it. Unregistered and
    /// arbitrary: the original carried no port of its own, since DirectPlay chose one.</summary>
    public const int DefaultPort = 47500;

    /// <summary>The address a join opens on. This machine, so a board with nothing typed into it
    /// still names something that can answer.</summary>
    public const string DefaultAddress = "127.0.0.1";

    /// <summary>The longest an address may be. An IPv6 address with a zone is the widest thing a
    /// player can type here.</summary>
    public const int AddressLimit = 48;

    /// <summary>How long a join may stand at <see cref="NetDoorStage.Joining"/> before the door
    /// gives up on it. ENet's own connect attempt gives up first on a routable address; this
    /// bounds the case where nothing answers at all.</summary>
    public const double JoinTimeoutSeconds = 12.0;

    private readonly Func<int, int, string, INetTransport> _openHost;
    private readonly Func<string, int, INetTransport> _openJoin;
    private readonly Func<int, UpnpPortMapResult>? _map;
    private readonly Action<int>? _unmap;

    private INetTransport? _transport;
    private INetLink? _link;
    private Task<UpnpPortMapResult>? _mapping;
    private bool _released;
    private double _joining;
    private int _mappedPort;

    /// <summary>A door over the carrier <paramref name="openHost"/> and <paramref name="openJoin"/>
    /// build. The first takes a port, a guest count and a bind address, the second an address
    /// and a port. The host's port mapping is <paramref name="map"/> and
    /// <paramref name="unmap"/>; with no mapper the board simply shows none.</summary>
    public NetPlayFeature(
        Func<int, int, string, INetTransport> openHost,
        Func<string, int, INetTransport> openJoin,
        Func<int, UpnpPortMapResult>? map = null,
        Action<int>? unmap = null)
    {
        _openHost = openHost ?? throw new ArgumentNullException(nameof(openHost));
        _openJoin = openJoin ?? throw new ArgumentNullException(nameof(openJoin));
        _map = map;
        _unmap = unmap;
    }

    /// <summary>Where the door stands.</summary>
    public NetDoorStage Stage { get; private set; } = NetDoorStage.Shut;

    /// <summary>Which interface a host binds. Every one of them by default, which is what a
    /// player on a network needs. ⚠ A scripted run sets the loopback address instead: a wildcard
    /// bind is what makes Windows put a firewall dialog on somebody's screen.</summary>
    public string BindAddress { get; set; } = "*";

    /// <summary>The port a host opens on, and the port a join is aimed at.</summary>
    public int Port { get; private set; } = DefaultPort;

    /// <summary>The address a join is aimed at, as typed.</summary>
    public string Address { get; private set; } = DefaultAddress;

    /// <summary>Why the last open failed, or "" when none has. Shown on the board rather than
    /// thrown: a taken port and a refused join are both things a player fixes and retries.</summary>
    public string Fault { get; private set; } = "";

    /// <summary>The port mapping this host asked its router for, or null when none was asked for
    /// or the answer has not landed yet.</summary>
    public UpnpPortMapResult? PortMap { get; private set; }

    /// <summary>How many other peers are on the wire: the guests a host has, or 1 once a guest
    /// has reached its host.</summary>
    public int Peers => _transport?.Peers.Count ?? 0;

    /// <summary>The link as the carrier reports it, or null for a carrier with no word for it.
    /// </summary>
    public EnetLinkState? Link => _link?.LinkState;

    /// <summary>Whether the host has answered this guest already. A guest's own launch waits on
    /// this, rather than timing out against a host that has not flown yet.</summary>
    public bool HostStarted => _link is { PendingPayloads: > 0 };

    /// <summary>Whether this end owns the match, meaningful once the door is open.</summary>
    public bool IsHost => Stage == NetDoorStage.Hosting;

    /// <summary>Whether a launch may leave through this door. A host may fly alone and wait for
    /// nobody; a guest may not fly before its link stands.</summary>
    public bool CanLaunch => Stage == NetDoorStage.Hosting || Stage == NetDoorStage.Joined;

    /// <summary>Steps the port by <paramref name="by"/>, wrapping inside the unprivileged range.
    /// Refused while a socket is open, since the open one is the port that matters.</summary>
    public void StepPort(int by)
    {
        if (_transport != null)
        {
            return;
        }

        int port = Port + by;
        Port = port < 1024 ? 65535 : port > 65535 ? 1024 : port;
    }

    /// <summary>Appends one typed character to the address. Refused while a socket is open, and
    /// for anything outside the characters an address is written with.</summary>
    public void TypeAddress(string typed)
    {
        if (_transport != null || string.IsNullOrEmpty(typed))
        {
            return;
        }

        foreach (char c in typed)
        {
            bool allowed = char.IsAsciiLetterOrDigit(c) || c is '.' or ':' or '%' or '-';
            if (allowed && Address.Length < AddressLimit)
            {
                Address += c;
            }
        }
    }

    /// <summary>Takes the last character off the address.</summary>
    public void EraseAddress()
    {
        if (_transport == null && Address.Length > 0)
        {
            Address = Address[..^1];
        }
    }

    /// <summary>Opens a listen server for <paramref name="maxGuests"/> guests on
    /// <see cref="BindAddress"/> and asks the router for the port. A socket that will not open
    /// leaves the door shut with the reason on <see cref="Fault"/>.</summary>
    public void OpenHost(int maxGuests)
    {
        if (_transport != null)
        {
            return;
        }

        try
        {
            _transport = _openHost(Port, maxGuests, BindAddress);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            Fail(e.Message);
            return;
        }

        _link = _transport as INetLink;
        Fault = "";
        Stage = NetDoorStage.Hosting;
        MapPort();
    }

    /// <summary>Starts a join to the typed address. The join lands on a later
    /// <see cref="Step"/>; until then the door stands at <see cref="NetDoorStage.Joining"/>.
    /// </summary>
    public void OpenJoin()
    {
        if (_transport != null)
        {
            return;
        }

        try
        {
            _transport = _openJoin(Address, Port);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            Fail(e.Message);
            return;
        }

        _link = _transport as INetLink;
        Fault = "";
        Stage = NetDoorStage.Joining;
        _joining = 0.0;
    }

    /// <summary>Drives the socket while the board is up. This is the only place a join lands, and
    /// the only place a guest arrives on a host's board. ⚠ The step is what carries the link, so
    /// a board that stops calling this stops hearing about its own match.</summary>
    public void Step(double dt)
    {
        if (_transport == null || _released)
        {
            return;
        }

        _transport.Step(dt);
        TakeMapping();
        if (Stage != NetDoorStage.Joining)
        {
            return;
        }

        _joining += dt;
        if (_link?.LinkState == EnetLinkState.Up || (_link == null && _transport.Peers.Count > 0))
        {
            Stage = NetDoorStage.Joined;
        }
        else if (_link?.LinkState == EnetLinkState.Down)
        {
            Fail($"{Address}:{Port} refused the join");
        }
        else if (_joining >= JoinTimeoutSeconds)
        {
            Fail($"{Address}:{Port} did not answer in {JoinTimeoutSeconds:0} seconds");
        }
    }

    /// <summary>The wire a launch carries, or null when this door is shut or still joining. The
    /// transport goes with it: the door neither steps nor closes it afterwards, because the
    /// session does both. The port mapping stays up until <see cref="Close"/>.</summary>
    public MenuNetLaunch? BuildLaunch()
    {
        if (_transport == null || !CanLaunch)
        {
            return null;
        }

        _released = true;
        return new MenuNetLaunch(_transport, IsHost);
    }

    /// <summary>Shuts the door. The socket is closed unless a launch took it, the mapping is
    /// taken down, and the port and address are left as they were typed. This is also where a
    /// match that took the transport gives the router's port back. The launcher therefore calls
    /// it at the end of a network flight, as a board does on the way out.</summary>
    public void Close()
    {
        if (_transport is IDisposable open && !_released)
        {
            open.Dispose();
        }

        _transport = null;
        _link = null;
        _released = false;
        Stage = NetDoorStage.Shut;
        PortMap = null;
        UnmapPort();
    }

    /// <summary>Drops everything transient: the socket goes with the presentation that opened it,
    /// since no board is left to show what it is doing.</summary>
    public void Discard()
    {
        Close();
        Fault = "";
    }

    // Asked for once, where hosting opens, and away from the frame. ⚠ The call blocks for the
    // gateway search, so it runs on its own thread. The board shows the answer on the step that
    // finds it, rather than holding the menu still for two seconds.
    private void MapPort()
    {
        int port = Port;
        _mapping = _map == null ? null : Task.Run(() => _map(port));
    }

    private void TakeMapping()
    {
        if (_mapping is not { IsCompleted: true })
        {
            return;
        }

        var result = _mapping.Result;
        _mapping = null;
        PortMap = result;
        _mappedPort = result.IsMapped ? result.Port : 0;
    }

    // The way back out, where the board or the launcher gives the port back. This one is not put
    // on a thread: a mapping left behind is a door standing open in the player's own router. The
    // wait falls on the way out of hosting, not in front of a player waiting to fly.
    private void UnmapPort()
    {
        TakeMapping();
        if (_mapping != null)
        {
            var landed = _mapping.Result;
            _mapping = null;
            _mappedPort = landed.IsMapped ? landed.Port : 0;
        }

        if (_mappedPort != 0 && _unmap != null)
        {
            _unmap(_mappedPort);
        }

        _mappedPort = 0;
    }

    private void Fail(string why)
    {
        if (_transport is IDisposable open)
        {
            open.Dispose();
        }

        _transport = null;
        _link = null;
        Fault = why;
        Stage = NetDoorStage.Failed;
    }
}
