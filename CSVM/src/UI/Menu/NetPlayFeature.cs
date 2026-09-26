using System;
using System.Collections.Generic;
using System.Threading;
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

/// <summary>One guest a co-op host seated: its peer and its player number. It also says whether
/// the guest is Ready this round and names its airframe from the host's hangar. It carries the
/// guest's fit and player name. <see cref="Left"/> says it walked out of the flight under way.</summary>
public readonly record struct CoopGuest(
    int Peer, int Slot, bool Ready, byte Airframe, CoopFit Fit = default, string Name = "", bool Left = false);

/// <summary>
/// The multiplayer door as a shared feature. It owns the port and the address a board edits, the
/// socket it opens, and the link and port-mapping readouts it shows. The wire a launch carries
/// away comes from here too. Nothing here names a carrier or an engine type: the two factories,
/// the two port-mapping calls and the LAN socket arrive as delegates. The launcher therefore passes the real
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

    /// <summary>How many humans a campaign mission flown together seats, local and remote alike.
    /// A guest past it is told the game is full and hung up on.</summary>
    public const int CoopHumans = 4;

    /// <summary>How long a closing host keeps stepping its socket after the close notices, so
    /// they leave before the socket's close discards what is still queued.</summary>
    public const double LingerSeconds = 0.5;

    /// <summary>How long a refused guest has to hang up on its own after the full notice, before
    /// the host hangs up on it.</summary>
    public const double RefuseGraceSeconds = 1.0;

    /// <summary>What a LAN search asks at unless a suite points it elsewhere.</summary>
    public const string BroadcastAddress = "255.255.255.255";

    /// <summary>The airframe a co-op guest flies when its own pick is not one the host offers: the
    /// campaign's first aeroplane, which every hangar holds.</summary>
    public const byte StarterAirframe = 5;

    private readonly Func<int, int, string, INetTransport> _openHost;
    private readonly Func<string, int, INetTransport> _openJoin;
    private readonly Func<int, UpnpPortMapResult>? _map;
    private readonly Action<int>? _unmap;
    private readonly Func<string, int, ILanSocket>? _lan;
    private readonly List<int> _admitted = new();
    private readonly List<(int Peer, double Waited)> _refused = new();
    private readonly Dictionary<int, CoopFlowMessage> _flowSent = new();

    private NetLobby? _transport;
    private DogfightLobby? _dogfight;
    private NetCoopScreen _coopScreen = NetCoopScreen.Cabin;
    private byte _coopSeq;
    private byte _epoch = 1;
    private byte _progress;
    private ushort _airframes;
    private bool _won;
    private int _objectives;
    private int _cash;
    private CoopPickMessage? _pickSent;
    private byte _pickAirframe = StarterAirframe;
    private bool _pickReady;
    private CoopFit _pickFit;
    private bool _pickLeft;

    // A guest back from a flight while its host still names that flight. Cleared once the host
    // names any other board, so only a flight launched after that can seat it again.
    private bool _flownFlow;

    // A Dogfight guest back from a match: the round it launched under, until the host names a new
    // one. Until then, whatever arrives is the old match's tail and never a launch.
    private byte? _flownEpoch;
    private byte _launchEpoch;
    private NetLobby? _closing;
    private double _lingered;
    private LanResponder? _responder;
    private LanSearch? _search;
    private int _hostPeer = -1;
    private INetLink? _link;
    private NetSessionKind _kind = NetSessionKind.Dogfight;
    private byte _missionSeq = SessionAdvertMessage.NoMission;
    private string _hostName = "";
    private int _localPlayers = 1;
    private Task<UpnpPortMapResult>? _mapping;
    private Task? _lease;
    private CancellationTokenSource? _renewal;
    private bool _released;
    private double _joining;
    private int _mappedPort;

    /// <summary>A door over the carrier <paramref name="openHost"/> and <paramref name="openJoin"/>
    /// build. The first takes a port, a guest count and a bind address, the second an address
    /// and a port. The host's port mapping is <paramref name="map"/> and <paramref name="unmap"/>,
    /// and with no mapper the board shows none. The LAN search's socket is bound by
    /// <paramref name="lan"/> on an address and a port. With none there is no search and an open
    /// door answers none.</summary>
    public NetPlayFeature(
        Func<int, int, string, INetTransport> openHost,
        Func<string, int, INetTransport> openJoin,
        Func<int, UpnpPortMapResult>? map = null,
        Action<int>? unmap = null,
        Func<string, int, ILanSocket>? lan = null)
    {
        _openHost = openHost ?? throw new ArgumentNullException(nameof(openHost));
        _openJoin = openJoin ?? throw new ArgumentNullException(nameof(openJoin));
        _map = map;
        _unmap = unmap;
        _lan = lan;
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

    /// <summary>Where a LAN search sends its query: the broadcast address by default. A suite sets
    /// the loopback, since a broadcast on the loopback proves nothing on Windows.</summary>
    public string SearchAddress { get; set; } = BroadcastAddress;

    /// <summary>How many other peers are on the wire: the guests a host has, or 1 once a guest
    /// has reached its host. A guest a campaign host refused as full is not counted.</summary>
    public int Peers
    {
        get
        {
            if (_transport == null)
            {
                return 0;
            }

            int peers = 0;
            foreach (int peer in _transport.AllPeers)
            {
                peers += Refused(peer) ? 0 : 1;
            }

            return peers;
        }
    }

    /// <summary>Whether this door can search the LAN and answer a search.</summary>
    public bool CanSearch => _lan != null;

    /// <summary>Whether a LAN search is open.</summary>
    public bool Searching => _search != null;

    /// <summary>How many rounds the open search has asked, 0 while none is open.</summary>
    public int SearchRounds => _search?.Rounds ?? 0;

    /// <summary>The open doors the LAN search heard, empty while none is open.</summary>
    public IReadOnlyList<LanGame> Games => _search?.Games ?? (IReadOnlyList<LanGame>)Array.Empty<LanGame>();

    /// <summary>Why the LAN search would not open, or "" when it did.</summary>
    public string SearchFault { get; private set; } = "";

    /// <summary>Whether this host is answering LAN searches.</summary>
    public bool Answering => _responder != null;

    /// <summary>The link as the carrier reports it, or null for a carrier with no word for it.
    /// </summary>
    public EnetLinkState? Link => _link?.LinkState;

    /// <summary>Whether the host has answered this guest already. A guest's own launch waits on
    /// this, rather than timing out against a host that has not flown yet. The answer is held
    /// by the lobby until a session binds it, so the held count is the sign.</summary>
    public bool HostStarted => _transport is { Held: > 0 };

    /// <summary>Whether this end owns the match, meaningful once the door is open.</summary>
    public bool IsHost => Stage == NetDoorStage.Hosting;

    /// <summary>What a host's door holds open: a Dogfight from the Network board, or a campaign
    /// mission from the campaign's own boards.</summary>
    public NetSessionKind HostKind => _kind;

    /// <summary>Whether this door is hosting a campaign mission.</summary>
    public bool IsCoopHost => IsHost && _kind == NetSessionKind.CampaignCoop;

    /// <summary>Whether this door is a guest linked to a host holding a campaign mission open.
    /// </summary>
    public bool IsCoopGuest => Stage == NetDoorStage.Joined && Advert is { Kind: NetSessionKind.CampaignCoop };

    /// <summary>Whether this door is a guest linked to a host holding a Dogfight open.</summary>
    public bool IsDogfightGuest => Stage == NetDoorStage.Joined && Advert is { Kind: NetSessionKind.Dogfight };

    /// <summary>The Multiplayer Lobby this door stands in, or null. A host has one once
    /// <see cref="OpenDogfightHost"/> opened it. A Dogfight guest has one once linked.</summary>
    public DogfightLobby? Dogfight => _dogfight;

    /// <summary>Whether this Dogfight guest's host has launched from its lobby: the options are
    /// heard and the session's opener is waiting here.</summary>
    public bool DogfightLaunchDue =>
        IsDogfightGuest && !_released && _flownEpoch == null && _dogfight is { HasOptions: true } && _transport!.Held > 0;

    /// <summary>The host's word about its session as this guest last heard it, or null while
    /// none has arrived. A join board names the session from this.</summary>
    public SessionAdvertMessage? Advert => _transport?.Advert;

    /// <summary>The word this host hands out, or null while the door is not hosting.</summary>
    public SessionAdvertMessage? Advertising => _transport?.Advertising;

    /// <summary>Whether a launch may leave through this door. A host may fly alone and wait for
    /// nobody; a guest may not fly before its link stands.</summary>
    public bool CanLaunch => Stage == NetDoorStage.Hosting || Stage == NetDoorStage.Joined;

    /// <summary>Whether a launch took this door's wire and has not handed it back yet.</summary>
    public bool Released => _released;

    /// <summary>The board this co-op host last named to its guests.</summary>
    public NetCoopScreen CoopScreen => _coopScreen;

    /// <summary>The round of picks under way on this host. It moves on whenever the host names a
    /// new mission or leaves the briefing and flight check, and a pick counts only under it.
    /// </summary>
    public byte CoopEpoch => _epoch;

    /// <summary>Whether every guest this co-op host seated is Ready under the current round. True
    /// with no guest seated, since a host may fly its campaign alone.</summary>
    public bool CoopAllReady
    {
        get
        {
            foreach (int peer in _admitted)
            {
                if (!ReadyNow(peer))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>The guests this co-op host seated, in player order: each one's player number, its
    /// Ready mark under the current round, and the airframe it flies.</summary>
    public IReadOnlyList<CoopGuest> CoopGuests
    {
        get
        {
            var guests = new List<CoopGuest>(_admitted.Count);
            for (int i = 0; i < _admitted.Count; i++)
            {
                int peer = _admitted[i];
                var pick = _transport!.Picks.TryGetValue(peer, out var sent) ? sent : default;
                guests.Add(new CoopGuest(peer, _localPlayers + i, ReadyNow(peer), AirframeOf(peer),
                    pick.Fit, pick.Name ?? "", pick.Left && pick.Epoch == _epoch));
            }

            return guests;
        }
    }

    /// <summary>The host's latest word about its boards as this co-op guest heard it, or null.
    /// </summary>
    public CoopFlowMessage? CoopFlow => IsCoopGuest ? _transport?.Flow : null;

    /// <summary>How many co-op flows this guest has heard, so a board can tell news from a repeat.
    /// </summary>
    public int CoopFlows => _transport?.Flows ?? 0;

    /// <summary>The airframe this co-op guest picked from its host's hangar.</summary>
    public byte CoopPickAirframe => _pickAirframe;

    /// <summary>Whether this co-op guest means to be Ready, sent or not yet sent. A new round of
    /// picks clears it.</summary>
    public bool CoopPickReady => _pickReady;

    /// <summary>The fit this co-op guest's pick carries to its host.</summary>
    public CoopFit CoopPickFit => _pickFit;

    /// <summary>The name this end's player goes by, sent with a co-op guest's pick so the host's
    /// roster calls the guest by it. Empty when the player has none, and the roster then uses the
    /// player number.</summary>
    public string PlayerName { get; set; } = "";

    /// <summary>Each seat's fit as this co-op guest's host launched it, by seat.</summary>
    public IReadOnlyDictionary<int, CoopFit> CoopSeatFits =>
        _transport?.SeatFits ?? (IReadOnlyDictionary<int, CoopFit>)new Dictionary<int, CoopFit>();

    /// <summary>Whether this co-op guest is Ready under the host's current round.</summary>
    public bool CoopReady => _pickReady && CoopFlow is { } flow && _pickSent is { } sent && sent.Epoch == flow.Epoch;

    /// <summary>Whether this co-op guest's host has launched the mission it is seated for: the host
    /// names the flight and its session's opener is waiting here. A guest that arrived mid-mission
    /// has no opener, since the flight's field was fixed before it came, and waits.</summary>
    public bool CoopLaunchDue =>
        IsCoopGuest && !_released && !_flownFlow && CoopFlow is { Screen: NetCoopScreen.InMission } && _transport!.Held > 0;

    /// <summary>What this co-op host's boards show, named to every guest on the next step. A new
    /// mission starts a new round of picks, as does a move onto a board other than the briefing
    /// and flight check. Every Ready then clears.</summary>
    public void ShowCoop(NetCoopScreen screen, int missionSeq, int progress, ushort airframes)
    {
        byte seq = (byte)Math.Clamp(missionSeq, 0, byte.MaxValue);
        bool picking = screen is NetCoopScreen.Briefing or NetCoopScreen.FlightCheck;
        bool wasPicking = _coopScreen is NetCoopScreen.Briefing or NetCoopScreen.FlightCheck;
        if (seq != _coopSeq || (screen != _coopScreen && !(picking && wasPicking)))
        {
            NextRound();
        }

        _coopScreen = screen;
        _coopSeq = seq;
        _progress = (byte)Math.Clamp(progress, 0, byte.MaxValue);
        _airframes = airframes;
    }

    /// <summary>The shared result a co-op host's debrief shows, named to every guest with the
    /// board. It says whether the mission was won, its objectives met as a mask, and its cash.
    /// </summary>
    public void ShowCoopResult(bool won, int objectives, int cash)
    {
        _won = won;
        _objectives = objectives;
        _cash = cash;
    }

    /// <summary>This co-op guest's pick: <paramref name="airframe"/> from the host's hangar, whether
    /// it is Ready, and the ammunition and ordnance it flies with. Reaches the host on the next
    /// step, under the host's current round.</summary>
    public void PickCoop(int airframe, bool ready, CoopFit fit = default)
    {
        _pickAirframe = (byte)Math.Clamp(airframe, 0, byte.MaxValue);
        _pickReady = ready;
        _pickFit = fit;
    }

    /// <summary>This co-op guest walks out of the flight under way. The host hears it at once and
    /// takes the guest's aeroplane out of its mission, rather than waiting for a link that never
    /// drops. The mark belongs to the flight's round and clears when the host names another.
    /// </summary>
    public void LeaveCoopMission()
    {
        if (!IsCoopGuest || CoopFlow is not { Screen: NetCoopScreen.InMission })
        {
            return;
        }

        // The first follow settles the round, which would otherwise clear a mark set under it.
        FollowHost();
        _pickLeft = true;
        FollowHost();
    }

    /// <summary>Sends every seated guest each seat's fit, <paramref name="bySeat"/> indexed by
    /// seat. A host calls this at its launch, before the session's opener, so a guest has
    /// every fit in hand when it builds the field.</summary>
    public void TellSeatFits(IReadOnlyList<CoopFit> bySeat)
    {
        ArgumentNullException.ThrowIfNull(bySeat);
        if (_transport == null || !IsHost)
        {
            return;
        }

        foreach (int peer in IsCoopHost ? _admitted : _transport.AllPeers)
        {
            for (int seat = 0; seat < bySeat.Count && seat <= byte.MaxValue; seat++)
            {
                _transport.Tell(peer, new CoopSeatFitMessage((byte)seat, bySeat[seat]));
            }
        }
    }

    /// <summary>Takes back the wire a co-op or lobby launch carried away once its flight ends. The
    /// door then holds the link through the debrief or the lobby that follows. False when this door
    /// no longer holds that wire, in which case the caller disposes it.</summary>
    public bool Reclaim()
    {
        if (!_released || _transport == null)
        {
            return false;
        }

        _transport.Unbind();
        _released = false;
        _flowSent.Clear();
        _flownFlow = IsCoopGuest;
        _flownEpoch = IsDogfightGuest ? _launchEpoch : null;
        return true;
    }

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
    /// <remarks>The lobby's host side runs behind it unshown, so an Original guest can pick, chat
    /// and ready up against a Built-in host.</remarks>
    public void OpenHost(int maxGuests)
    {
        OpenHost(maxGuests, NetSessionKind.Dogfight);
        if (_transport != null && Stage == NetDoorStage.Hosting)
        {
            _dogfight = new DogfightLobby(_transport, () => PlayerName);
        }
    }

    /// <summary>Opens a listen server with the Multiplayer Lobby standing on it, the Connection
    /// page's Host. It is <see cref="OpenHost(int)"/> with the lobby shown, whose environment the
    /// advert names. The host's pilot goes by <see cref="PlayerName"/>.</summary>
    public void OpenDogfightHost(int maxGuests)
    {
        OpenHost(maxGuests);
        if (_transport != null && _dogfight != null)
        {
            _hostName = PlayerName;
            _dogfight.Show();
            _transport.Advertise(CurrentAdvert());
        }
    }

    /// <summary>Opens a listen server for a campaign mission flown together, the campaign
    /// boards' own door. It is <see cref="OpenHost(int)"/> with the advert naming the campaign;
    /// <see cref="Offer"/> fills in which mission and whose profile. The socket admits up to
    /// <see cref="CoopHumans"/> guests, so one past the cap can hear why it is refused. The step
    /// refuses every guest past the cap in arrival order.</summary>
    public void OpenCoopHost(int maxGuests) =>
        OpenHost(Math.Min(maxGuests, CoopHumans), NetSessionKind.CampaignCoop);

    /// <summary>What a coop host's advert names: its next mission, its host name, and its local
    /// player count. Guests are counted on top of
    /// <paramref name="localPlayers"/>. Reaches the peers on the next <see cref="Step"/>.</summary>
    public void Offer(int missionSeq, string hostName, int localPlayers)
    {
        _missionSeq = missionSeq is >= 0 and < SessionAdvertMessage.NoMission
            ? (byte)missionSeq
            : SessionAdvertMessage.NoMission;
        _hostName = hostName ?? "";
        _localPlayers = Math.Max(1, localPlayers);
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

        EndLinger();
        try
        {
            _transport = new NetLobby(_openJoin(Address, Port));
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            Fail(e.Message);
            return;
        }

        _link = _transport.Inner as INetLink;
        _hostPeer = -1;
        Fault = "";
        Stage = NetDoorStage.Joining;
        _joining = 0.0;
    }

    /// <summary>Joins a game the LAN search heard, at the address it answered from and the game
    /// port it named. The search stays open; the page that ran it closes it.</summary>
    public void JoinGame(LanGame game)
    {
        if (_transport != null || string.IsNullOrWhiteSpace(game.Address) || game.Port is < 1 or > 65535)
        {
            return;
        }

        Address = game.Address;
        Port = game.Port;
        OpenJoin();
    }

    /// <summary>Asks the LAN for open doors, opening the search on its first call. Each call is a
    /// new round, and a game that answers neither this round nor the last leaves
    /// <see cref="Games"/>. The answers land on later steps.</summary>
    public void Search()
    {
        if (_lan == null)
        {
            return;
        }

        if (_search == null)
        {
            try
            {
                _search = new LanSearch(_lan(BindAddress, 0), SearchAddress, LanDiscovery.Port);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                SearchFault = e.Message;
                return;
            }
        }

        SearchFault = "";
        _search.Ask();
    }

    /// <summary>Closes the LAN search and forgets what it heard.</summary>
    public void StopSearch()
    {
        _search?.Dispose();
        _search = null;
    }

    /// <summary>Drives the socket while the board is up. This is the only place a join lands, and
    /// the only place a guest arrives on a host's board. ⚠ The step is what carries the link, so
    /// a board that stops calling this stops hearing about its own match. It also carries the
    /// LAN search and the answers to one, and the close notices of a host that just closed.
    /// </summary>
    public void Step(double dt)
    {
        StepLinger(dt);
        _search?.Poll();
        if (_transport == null)
        {
            return;
        }

        // A released wire is stepped by the session that carries it. A co-op door still seats,
        // advertises and follows its link here, since it holds the session together in flight.
        bool coop = _kind == NetSessionKind.CampaignCoop || IsCoopGuest;
        if (_released && !coop)
        {
            _responder?.Poll(CurrentAdvert(), Port);
            return;
        }

        if (!_released)
        {
            _transport.Step(dt);
        }

        TakeMapping();
        if (Stage == NetDoorStage.Hosting)
        {
            if (_kind == NetSessionKind.CampaignCoop)
            {
                Admit(dt);
                SendFlows();
            }

            _dogfight?.Step();
            var advert = CurrentAdvert();
            _transport.Advertise(advert);
            _responder?.Poll(advert, Port);
            return;
        }

        // A host that says why it is sending this guest away is believed before its link drops.
        if (_transport.Closed is { } closed)
        {
            Fail(closed.Reason == NetCloseReason.Full ? CoopDoorText.GameFull : CoopDoorText.HostClosed);
            return;
        }

        if (Stage == NetDoorStage.Joined)
        {
            bool hostGone = _hostPeer >= 0 && !Contains(_transport.AllPeers, _hostPeer);
            if (_link?.LinkState == EnetLinkState.Down || hostGone)
            {
                Fail(CoopDoorText.HostLeft);
                return;
            }

            if (IsCoopGuest)
            {
                FollowHost();
            }
            else if (IsDogfightGuest)
            {
                _dogfight ??= new DogfightLobby(_transport, () => PlayerName, _hostPeer);
                if (_flownEpoch is { } flown)
                {
                    // The host names a new round only once its own match is freed, so everything
                    // held up to that word is the old match's.
                    _transport.DropHeld();
                    _flownEpoch = _dogfight.Options.Epoch == flown ? flown : null;
                }

                _dogfight.Step();
            }

            return;
        }

        if (Stage != NetDoorStage.Joining)
        {
            return;
        }

        _joining += dt;
        if (_link?.LinkState == EnetLinkState.Up || (_link == null && _transport.AllPeers.Count > 0))
        {
            Stage = NetDoorStage.Joined;
            _hostPeer = _transport.AllPeers.Count > 0 ? _transport.AllPeers[0] : -1;
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

        // A guest refused as full has no seat in the match the session builds off the roster.
        foreach (var (peer, _) in _refused)
        {
            _transport.Disconnect(peer);
        }

        if (_dogfight is { } lobby)
        {
            // A host's options and list go out ahead of the session's opener, so a guest launches
            // on what the host flies. The list is kept to name the seats on Game Scores.
            if (IsHost)
            {
                lobby.Step();
            }

            lobby.Launched();
            _launchEpoch = lobby.Options.Epoch;
        }

        _released = true;
        if (IsCoopHost)
        {
            // Named before the session's opener is sent, so a guest knows the opener is this
            // flight's and not a stale one.
            ShowCoop(NetCoopScreen.InMission, _coopSeq, _progress, _airframes);
            SendFlows();
        }

        return new MenuNetLaunch(_transport, IsHost);
    }

    /// <summary>Shuts the door. The socket is closed unless a launch took it, the mapping is
    /// taken down, and the port and address are left as they were typed. This is also where a
    /// match that took the transport gives the router's port back. The launcher therefore calls
    /// it at the end of a network flight, as a board does on the way out. A host's guests are
    /// each sent a close notice first, and the socket lingers on later steps so that it leaves.
    /// </summary>
    public void Close()
    {
        _responder?.Dispose();
        _responder = null;
        if (_transport != null && !_released)
        {
            if (Stage == NetDoorStage.Hosting)
            {
                Linger(_transport);
            }
            else
            {
                _transport.Dispose();
            }
        }

        _transport = null;
        _link = null;
        _released = false;
        _admitted.Clear();
        _refused.Clear();
        _dogfight = null;
        _flownEpoch = null;
        ForgetCoop();
        _hostPeer = -1;
        _kind = NetSessionKind.Dogfight;
        Offer(SessionAdvertMessage.NoMission, "", 1);
        Stage = NetDoorStage.Shut;
        PortMap = null;
        UnmapPort();
    }

    /// <summary>Drops everything transient: the socket goes with the presentation that opened it,
    /// since no board is left to show what it is doing. A closing socket and a search go too.
    /// </summary>
    public void Discard()
    {
        Close();
        EndLinger();
        StopSearch();
        Fault = "";
        SearchFault = "";
    }

    private static bool Contains(IReadOnlyList<int> peers, int peer)
    {
        for (int i = 0; i < peers.Count; i++)
        {
            if (peers[i] == peer)
            {
                return true;
            }
        }

        return false;
    }

    // The mapping thread's whole life. It runs on after a launch takes the socket, since a match
    // never steps this door. A lease that lapsed mid-match would shut every guest out.
    private static void HoldLease(Func<int, UpnpPortMapResult> map, int port,
        TaskCompletionSource<UpnpPortMapResult> first, CancellationToken stop)
    {
        UpnpPortMapResult latest;
        try
        {
            latest = map(port);
        }
        catch (Exception e)
        {
            first.SetException(e);
            throw;
        }

        first.SetResult(latest);
        int held = latest.IsMapped ? latest.LeaseSeconds : 0;
        var wait = UpnpLease.NextRenewal(latest, held);
        while (wait != Timeout.InfiniteTimeSpan && !stop.WaitHandle.WaitOne(wait))
        {
            latest = map(port);
            held = latest.IsMapped ? latest.LeaseSeconds : held;
            wait = UpnpLease.NextRenewal(latest, held);
        }
    }

    // Both host doors open the same socket; only the advert's kind tells them apart.
    private void OpenHost(int maxGuests, NetSessionKind kind)
    {
        if (_transport != null)
        {
            return;
        }

        // A reopen on the same port cannot wait for the last close to finish lingering.
        EndLinger();
        try
        {
            _transport = new NetLobby(_openHost(Port, maxGuests, BindAddress));
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            Fail(e.Message);
            return;
        }

        _link = _transport.Inner as INetLink;
        _kind = kind;
        Fault = "";
        Stage = NetDoorStage.Hosting;
        _transport.Advertise(CurrentAdvert());
        OpenResponder();
        MapPort();
    }

    // A second door on this machine finds the discovery port taken. It still hosts; it only
    // goes unanswered on the LAN, and a guest can still type its address.
    private void OpenResponder()
    {
        if (_lan == null)
        {
            return;
        }

        try
        {
            _responder = new LanResponder(_lan(BindAddress, LanDiscovery.Port));
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            _responder = null;
        }
    }

    // The player count is this machine's seats plus every guest on the wire. It moves as guests
    // arrive, and the step re-sends it.
    private SessionAdvertMessage CurrentAdvert()
    {
        int players = Math.Min(_localPlayers + Peers, byte.MaxValue);
        bool coop = _kind == NetSessionKind.CampaignCoop;
        // A Built-in host picks its map after the lobby opened, so its advert names none.
        byte seq = coop ? _missionSeq : _dogfight is { Shown: true } lobby ? lobby.Options.Environment : SessionAdvertMessage.NoMission;
        int cap = coop ? CoopHumans : NetSeats.MaxPlayers;
        var status = players >= cap
            ? NetSessionStatus.Full
            : _released || (coop && _coopScreen == NetCoopScreen.InMission) ? NetSessionStatus.InMission : NetSessionStatus.Waiting;
        return new SessionAdvertMessage(_kind, seq, (byte)players, _hostName, status, (byte)cap);
    }

    // A campaign host seats guests in arrival order up to the cap. One past it is told the game is
    // full, and is hung up on if it has not left by the end of the grace.
    private void Admit(double dt)
    {
        var peers = _transport!.AllPeers;
        _admitted.RemoveAll(peer => !Contains(peers, peer));
        _refused.RemoveAll(refused => !Contains(peers, refused.Peer));
        int seats = Math.Max(0, CoopHumans - _localPlayers);
        for (int i = 0; i < peers.Count; i++)
        {
            int peer = peers[i];
            if (_admitted.Contains(peer) || Refused(peer))
            {
                continue;
            }

            if (_admitted.Count < seats)
            {
                _admitted.Add(peer);
                continue;
            }

            _transport.Farewell(peer, NetCloseReason.Full);
            _refused.Add((peer, 0.0));
        }

        for (int i = 0; i < _refused.Count; i++)
        {
            var (peer, waited) = _refused[i];
            if (double.IsPositiveInfinity(waited))
            {
                continue;
            }

            waited += dt;
            if (waited >= RefuseGraceSeconds)
            {
                // Kept on the list until the carrier reports it gone, so it is not seated meanwhile.
                waited = double.PositiveInfinity;
                _transport.Disconnect(peer);
            }

            _refused[i] = (peer, waited);
        }
    }

    private bool ReadyNow(int peer) =>
        _transport != null && _transport.Picks.TryGetValue(peer, out var pick) && pick.Epoch == _epoch && pick.Ready;

    // A pick of an airframe the host's hangar does not hold, or no pick at all, flies the starter.
    private byte AirframeOf(int peer)
    {
        if (_transport != null && _transport.Picks.TryGetValue(peer, out var pick) && Offers(pick.Airframe))
        {
            return pick.Airframe;
        }

        if (Offers(StarterAirframe) || _airframes == 0)
        {
            return StarterAirframe;
        }

        for (byte a = 0; a < 16; a++)
        {
            if (Offers(a))
            {
                return a;
            }
        }

        return StarterAirframe;
    }

    private bool Offers(int airframe) => airframe is >= 0 and < 16 && (_airframes & (1 << airframe)) != 0;

    // Zero is skipped, so a pick a guest never sent (epoch 0) is never counted as current.
    private void NextRound() => _epoch = (byte)(_epoch == byte.MaxValue ? 1 : _epoch + 1);

    // Each guest's flow differs only in its own player number; one goes out whenever it changed.
    private void SendFlows()
    {
        if (_transport == null)
        {
            return;
        }

        byte mask = 0;
        for (int i = 0; i < _admitted.Count; i++)
        {
            int slot = _localPlayers + i;
            if (slot < 8 && ReadyNow(_admitted[i]))
            {
                mask |= (byte)(1 << slot);
            }
        }

        byte humans = (byte)Math.Min(_localPlayers + _admitted.Count, byte.MaxValue);
        for (int i = 0; i < _admitted.Count; i++)
        {
            int peer = _admitted[i];
            var flow = new CoopFlowMessage(_coopScreen, _coopSeq, _epoch, (byte)(_localPlayers + i), mask, humans,
                _progress, _won, _airframes, _objectives, _cash, (byte)Math.Min(_localPlayers, byte.MaxValue));
            if (_flowSent.TryGetValue(peer, out var sent) && sent == flow)
            {
                continue;
            }

            _transport.Tell(peer, flow);
            _flowSent[peer] = flow;
        }

        foreach (int peer in new List<int>(_flowSent.Keys))
        {
            if (!_admitted.Contains(peer))
            {
                _flowSent.Remove(peer);
            }
        }
    }

    // A guest's Ready belongs to one round: a new round clears it, and the pick goes out again
    // under the new one. What trails in from a flight that ended is never the next one's opener.
    private void FollowHost()
    {
        if (_transport!.Flow is not { } flow)
        {
            return;
        }

        if (flow.Screen != NetCoopScreen.InMission)
        {
            _flownFlow = false;
        }

        // What arrives under the flight this guest already flew is that flight's tail, never an
        // opener for the next one.
        if (flow.Screen != NetCoopScreen.InMission || _flownFlow)
        {
            _transport.DropHeld();
        }

        if (_pickSent is { } last && last.Epoch != flow.Epoch)
        {
            _pickReady = false;
            _pickLeft = false;
        }

        var pick = new CoopPickMessage(flow.Epoch, _pickReady, _pickAirframe, _pickFit, PlayerName, _pickLeft);
        if (_hostPeer >= 0 && _pickSent != pick)
        {
            _transport.Tell(_hostPeer, pick);
            _pickSent = pick;
        }
    }

    private void ForgetCoop()
    {
        _flowSent.Clear();
        _coopScreen = NetCoopScreen.Cabin;
        _coopSeq = 0;
        _progress = 0;
        _airframes = 0;
        _won = false;
        _objectives = 0;
        _cash = 0;
        NextRound();
        _pickSent = null;
        _pickReady = false;
        _pickAirframe = StarterAirframe;
        _pickFit = default;
        _pickLeft = false;
        _flownFlow = false;
    }

    private bool Refused(int peer)
    {
        foreach (var (refused, _) in _refused)
        {
            if (refused == peer)
            {
                return true;
            }
        }

        return false;
    }

    // ⚠ The notices go before the close, and the socket keeps being stepped afterwards. A carrier's
    // close discards what it has queued, so a notice sent and closed on at once never leaves.
    private void Linger(NetLobby lobby)
    {
        EndLinger();
        var peers = new List<int>(lobby.AllPeers);
        foreach (int peer in peers)
        {
            lobby.Farewell(peer, NetCloseReason.Closed);
        }

        lobby.Step(0.0);
        _closing = lobby;
        _lingered = 0.0;
    }

    private void StepLinger(double dt)
    {
        if (_closing == null)
        {
            return;
        }

        _closing.Step(dt);
        _lingered += dt;
        if (_lingered >= LingerSeconds)
        {
            EndLinger();
        }
    }

    private void EndLinger()
    {
        _closing?.Dispose();
        _closing = null;
    }

    // Asked for where hosting opens, and away from the frame. ⚠ The call blocks for the gateway
    // search, so it runs on a dedicated thread, never the pool, which starves. The board shows the
    // first answer on the step that finds it. The same thread renews the lease until Close.
    private void MapPort()
    {
        if (_map is not { } map)
        {
            _mapping = null;
            return;
        }

        int port = Port;
        var first = new TaskCompletionSource<UpnpPortMapResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new CancellationTokenSource();
        _mapping = first.Task;
        _renewal = stop;
        _lease = Task.Factory.StartNew(() => HoldLease(map, port, first, stop.Token), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    // Stopped and waited for before the unmap, so a renewal in flight cannot put the mapping back.
    private void StopRenewal()
    {
        _renewal?.Cancel();
        if (_lease != null)
        {
            Task.WaitAny(_lease);
        }

        _renewal?.Dispose();
        _renewal = null;
        _lease = null;
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
        StopRenewal();
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

    // A wire a launch carried away is the session's to close, so a failure in flight only lets go
    // of it. The launcher disposes it once Reclaim says the door no longer holds it.
    private void Fail(string why)
    {
        if (!_released)
        {
            _transport?.Dispose();
        }

        _responder?.Dispose();
        _responder = null;
        _transport = null;
        _link = null;
        _released = false;
        _admitted.Clear();
        _refused.Clear();
        _dogfight = null;
        _flownEpoch = null;
        ForgetCoop();
        _hostPeer = -1;
        Fault = why;
        Stage = NetDoorStage.Failed;
    }
}
