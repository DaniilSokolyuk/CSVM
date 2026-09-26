using System;
using System.Collections.Generic;
using System.Globalization;
using CSVM.Net;

namespace CSVM.UI.Menu;

/// <summary>The three mission types the lobby's Type box lists, in the string table's order.
/// Only the Deathmatch is flown; it is the Dogfight.</summary>
public enum DogfightMissionType : byte
{
    /// <summary>Capture the Flag, drawn greyed.</summary>
    CaptureTheFlag = 0,

    /// <summary>Deathmatch, the Dogfight every lobby flies.</summary>
    Deathmatch = 1,

    /// <summary>Zeppelin vs Zeppelin, drawn greyed.</summary>
    ZeppelinVsZeppelin = 2,
}

/// <summary>One line of the lobby's chat panel: who typed it and what.</summary>
public readonly record struct DogfightChatLine(string Name, string Text);

/// <summary>One pilot's line on the Game Scores page after a match: its name and its final points,
/// kills and deaths.</summary>
public readonly record struct DogfightScore(string Name, int Points, int Kills, int Deaths);

/// <summary>
/// The Multiplayer Lobby of a Dogfight, on both of its ends, engine-free. The host owns the Mission
/// Options and the player list, and each guest reads them off the wire. Every pilot picks a stock
/// airframe and a fit and marks itself Ready, and the host launches once every pilot is Ready. A
/// guest's pick travels as a <see cref="CoopPickMessage"/> under the round the host's options name,
/// so an option change clears every Ready. A match flown from here lands back on it with its scores.
/// </summary>
public sealed class DogfightLobby
{
    /// <summary>How many environments the Environment box lists.</summary>
    public const int EnvironmentCount = 7;

    /// <summary>The Time box's value when a lobby opens, the original's own default.</summary>
    public const int DefaultTimeMinutes = 10;

    /// <summary>The Score box's value when a lobby opens, the original's own default.</summary>
    public const int DefaultScore = 40;

    /// <summary>The Lives box's value once Limited Lives is ticked. The one invented number in the
    /// lives rule: the original leaves the box empty (docs/org/multiplayer-scoring.md).</summary>
    public const int DefaultLives = 3;

    /// <summary>The largest time the two-character wide box takes, in minutes.</summary>
    public const int MaxTimeMinutes = 99;

    /// <summary>The largest score the Score box takes.</summary>
    public const int MaxScore = 999;

    /// <summary>The largest life count the two-character Lives box takes.</summary>
    public const int MaxLives = 99;

    /// <summary>How many chat lines the panel keeps; older lines scroll away.</summary>
    public const int ChatDepth = 64;

    /// <summary>The airframe a pilot flies until it picks one: the Devastator, the original's first
    /// row.</summary>
    public const byte DefaultAirframe = NetPlayFeature.StarterAirframe;

    /// <summary>How many stock airframes the Select Plane list offers.</summary>
    public const int AirframeCount = 11;

    /// <summary>Why a Built-in host's launch waits: a lobby guest is not Ready yet.</summary>
    public const string GuestsNotReady = "waiting for every lobby pilot to be Ready";

    /// <summary>Why a Built-in host's launch waits: a lobby guest flies only the seven environments.
    /// </summary>
    public const string MapUnlisted = "a lobby pilot flies only the seven lobby environments";

    // The Environment box's words in the string table's order, and the chapter each is flown on.
    private static readonly string[] EnvironmentNames =
    {
        "Above the Clouds", "Hawai'ian Islands", "Hollywood Studio", "Manhattan",
        "NW Boeing Field", "NW Lighthouse", "Sky Haven (Rockies)",
    };

    private static readonly string[] EnvironmentChapters = { "C2B", "C3", "C2", "C5", "C1", "C1B", "C4" };

    private readonly NetLobby _wire;
    private readonly Func<string> _name;
    private readonly int _hostPeer;
    private readonly List<DogfightChatLine> _chat = new();
    private readonly Dictionary<int, DogfightOptionsMessage> _optionsSent = new();
    private readonly Dictionary<int, DogfightRosterMessage> _rosterSent = new();
    private DogfightOptionsMessage _options = new(
        1, 0, (byte)DogfightMissionType.Deathmatch, DogfightVictory.Time, DefaultTimeMinutes, DefaultScore,
        false, DefaultLives, true);

    private byte _airframe = DefaultAirframe;
    private CoopFit _fit;
    private bool _ready;
    private byte _readyEpoch;
    private CoopPickMessage? _pickSent;
    private string[] _launchNames = Array.Empty<string>();
    private DogfightScore[] _scores = Array.Empty<DogfightScore>();

    /// <summary>A host's lobby over <paramref name="wire"/>, its pilot named by
    /// <paramref name="name"/>.</summary>
    public DogfightLobby(NetLobby wire, Func<string> name)
        : this(wire, name, -1)
    {
    }

    /// <summary>A guest's lobby over <paramref name="wire"/>, following the host at
    /// <paramref name="hostPeer"/>, its pilot named by <paramref name="name"/>.</summary>
    public DogfightLobby(NetLobby wire, Func<string> name, int hostPeer)
    {
        _wire = wire ?? throw new ArgumentNullException(nameof(wire));
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _hostPeer = hostPeer;
    }

    /// <summary>Whether this end owns the options and launches the match.</summary>
    public bool IsHost => _hostPeer < 0;

    /// <summary>Whether a guest has heard the host's options yet. Always true on the host.</summary>
    public bool HasOptions => IsHost || _wire.DogfightOptions.HasValue;

    /// <summary>The Mission Options as this end stands on them: its own on the host, the host's word
    /// on a guest. A guest that has heard nothing reads the opening defaults.</summary>
    public DogfightOptionsMessage Options => IsHost ? _options : _wire.DogfightOptions ?? _options with { Epoch = 0 };

    /// <summary>The stock airframe this pilot picked, an index into the eleven.</summary>
    public byte Airframe => _airframe;

    /// <summary>The ammunition and ordnance this pilot picked, stock when all zero.</summary>
    public CoopFit Fit => _fit;

    /// <summary>Whether this pilot marked itself Ready under the current round.</summary>
    public bool Ready => _ready && _readyEpoch == Options.Epoch;

    /// <summary>The chat panel's lines, oldest first.</summary>
    public IReadOnlyList<DogfightChatLine> Chat => _chat;

    /// <summary>The player list, the host first: each pilot's name, airframe and Ready mark.</summary>
    public IReadOnlyList<DogfightLobbySeat> Players => IsHost ? HostRows() : _wire.DogfightRoster?.Rows ?? GuestAlone();

    /// <summary>This pilot's own row in <see cref="Players"/>.</summary>
    public int You => IsHost ? 0 : _wire.DogfightRoster?.You ?? 0;

    /// <summary>Whether every pilot in the lobby is Ready under the current round, the host included.
    /// A guest reads the host's list for it.</summary>
    public bool AllReady
    {
        get
        {
            foreach (var player in Players)
            {
                if (!player.Ready)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Whether LAUNCH! goes: on the host, once every pilot is Ready.</summary>
    public bool CanLaunch => IsHost && AllReady;

    /// <summary>Whether a lobby screen stands on this lobby. A Built-in Network board never shows
    /// one, so its guest sends no pick and its flight ends the way it always has.</summary>
    public bool Shown { get; private set; }

    /// <summary>The last match's lines for the Game Scores page, best first, or empty before any
    /// match has been flown from this lobby.</summary>
    public IReadOnlyList<DogfightScore> Scores => _scores;

    /// <summary>The players named at the last launch, in seat order.</summary>
    public IReadOnlyList<string> LaunchNames => _launchNames;

    /// <summary>The environment's words as the Environment box and the games list write them, or ""
    /// outside the seven.</summary>
    public static string EnvironmentName(int environment) =>
        environment is >= 0 and < EnvironmentCount ? EnvironmentNames[environment] : "";

    /// <summary>The chapter an environment is flown on.</summary>
    public static string ChapterOf(int environment) => EnvironmentChapters[Math.Clamp(environment, 0, EnvironmentCount - 1)];

    /// <summary>The environment flown on <paramref name="chapter"/>, or -1 for a chapter the
    /// Environment box does not list.</summary>
    public static int EnvironmentOf(string chapter) => Array.IndexOf(EnvironmentChapters, chapter);

    /// <summary>The Game Scores lines for a match's standings, best first. Each seat is named from
    /// <paramref name="names"/>, the list as it stood at the launch, or by its player tag past it.
    /// </summary>
    public static DogfightScore[] ScoresOf(IEnumerable<CSVM.Flight.Modes.VersusStanding> standings, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(names);
        var lines = new List<(int Rank, DogfightScore Line)>();
        foreach (var standing in standings)
        {
            int seat = standing.PlayerIndex;
            string name = seat >= 0 && seat < names.Count ? names[seat] : "P" + (seat + 1).ToString(CultureInfo.InvariantCulture);
            lines.Add((standing.Rank, new DogfightScore(name, standing.Score, standing.Kills, standing.Deaths)));
        }

        lines.Sort((a, b) => a.Rank.CompareTo(b.Rank));
        return lines.ConvertAll(line => line.Line).ToArray();
    }

    /// <summary>Whether the Type box offers a mission type as live. Capture the Flag and Zeppelin vs
    /// Zeppelin are drawn greyed.</summary>
    public static bool Flies(DogfightMissionType type) => type == DogfightMissionType.Deathmatch;

    /// <summary>The match rules a launch carries: the victory condition as a kill target or a match
    /// clock, the other limit off, and the lives rule.</summary>
    public static VersusRules RulesOf(DogfightOptionsMessage options) => new(
        options.Victory == DogfightVictory.Score ? options.Score : 0,
        options.Victory == DogfightVictory.Time ? options.TimeMinutes : 0,
        options.LimitedLives ? ClampLives(options.Lives) : 0,
        options.AutoRespawn);

    /// <summary>A life count held to 1..99. The original clamps nothing, so its empty box launches
    /// on 0 lives and a pilot on 0 counts as alive after its first death.</summary>
    public static int ClampLives(int lives) => Math.Clamp(lives, 1, MaxLives);

    /// <summary>Picks the environment. Refused on a guest and outside the seven.</summary>
    public bool SetEnvironment(int environment) =>
        environment is >= 0 and < EnvironmentCount && Change(_options with { Environment = (byte)environment });

    /// <summary>Picks the mission type. Refused on a guest and for a greyed type.</summary>
    public bool SetMissionType(DogfightMissionType type) => Flies(type) && Change(_options with { MissionType = (byte)type });

    /// <summary>Picks how the match is won. Refused on a guest.</summary>
    public bool SetVictory(DogfightVictory victory) =>
        victory is DogfightVictory.Time or DogfightVictory.Score && Change(_options with { Victory = victory });

    /// <summary>Sets the Time box, in minutes. Refused on a guest and outside 1 to 99.</summary>
    public bool SetTimeMinutes(int minutes) =>
        minutes is >= 1 and <= MaxTimeMinutes && Change(_options with { TimeMinutes = (byte)minutes });

    /// <summary>Sets the Score box. Refused on a guest and outside 1 to 999.</summary>
    public bool SetScore(int score) => score is >= 1 and <= MaxScore && Change(_options with { Score = (ushort)score });

    /// <summary>Checks or clears Limited Lives. Refused on a guest.</summary>
    public bool SetLimitedLives(bool limited) => Change(_options with { LimitedLives = limited });

    /// <summary>Sets the Lives box, clamped to 1..99. Refused on a guest and while Limited Lives is
    /// clear, which is when the box is greyed.</summary>
    public bool SetLives(int lives) =>
        _options.LimitedLives && Change(_options with { Lives = (byte)ClampLives(lives) });

    /// <summary>Checks or clears Auto Respawn. Refused on a guest.</summary>
    public bool SetAutoRespawn(bool auto) => Change(_options with { AutoRespawn = auto });

    /// <summary>Allow Custom Planes, drawn greyed: guests fly stock planes, so nothing sets it.</summary>
    public bool SetAllowCustomPlanes(bool allow) => false;

    /// <summary>Outlaw Components, drawn greyed with Allow Custom Planes.</summary>
    public bool SetOutlawComponents(bool outlaw) => false;

    /// <summary>Picks this pilot's airframe and fit, live whether or not it is Ready. A changed pick
    /// clears this pilot's own Ready. Refused outside the eleven stock airframes.</summary>
    public bool Pick(int airframe, CoopFit fit)
    {
        if (airframe is < 0 or >= AirframeCount)
        {
            return false;
        }

        // ⚠ Readiness is consent to the plane as it stood, so the host must not launch a changed one.
        if (airframe != _airframe || fit != _fit)
        {
            _ready = false;
        }

        _airframe = (byte)airframe;
        _fit = fit;
        return true;
    }

    /// <summary>Marks this pilot Ready or not under the current round. A guest's mark reaches the host
    /// on the next step.</summary>
    public void SetReady(bool ready)
    {
        _ready = ready;
        _readyEpoch = Options.Epoch;
    }

    /// <summary>A lobby screen now stands on this lobby, so a guest's pick and name go to the host.
    /// </summary>
    public void Show() => Shown = true;

    /// <summary>A Built-in host's launch against the guests that sent a pick, which only a lobby
    /// screen sends. It waits for their Ready and writes the map and rules into the options. Null
    /// when the launch may go, otherwise why not.</summary>
    public string? CheckBuiltInLaunch(string chapter, VersusRules rules)
    {
        bool picked = false;
        foreach (int peer in _wire.AllPeers)
        {
            if (!_wire.Picks.TryGetValue(peer, out var pick))
            {
                continue;
            }

            picked = true;
            if (!pick.Ready || pick.Epoch != _options.Epoch)
            {
                return GuestsNotReady;
            }
        }

        return AdoptLaunch(chapter, rules) || !picked ? null : MapUnlisted;
    }

    /// <summary>A Built-in host's launch written into the options, so an Original guest flies the same
    /// map and rules. False on a guest and for a chapter the Environment box does not list.</summary>
    public bool AdoptLaunch(string chapter, VersusRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        int environment = EnvironmentOf(chapter);
        if (!IsHost || environment < 0)
        {
            return false;
        }

        var adopted = _options with
        {
            Environment = (byte)environment,
            Victory = rules.KillTarget > 0 ? DogfightVictory.Score : DogfightVictory.Time,
            TimeMinutes = (byte)Math.Clamp(rules.TimeLimitMinutes > 0 ? rules.TimeLimitMinutes : _options.TimeMinutes, 1, MaxTimeMinutes),
            Score = (ushort)Math.Clamp(rules.KillTarget > 0 ? rules.KillTarget : _options.Score, 1, MaxScore),
            LimitedLives = rules.Lives > 0,
            Lives = (byte)ClampLives(rules.Lives > 0 ? rules.Lives : _options.Lives),
            AutoRespawn = rules.AutoRespawn,
        };

        // Written without a new round: the guests' Ready marks are what let this launch go.
        _options = adopted with { Epoch = _options.Epoch };
        return true;
    }

    /// <summary>The launch leaves: the player list as it stands is kept, so the match's seats can be
    /// named on the Game Scores page afterwards.</summary>
    public void Launched()
    {
        var players = Players;
        _launchNames = new string[players.Count];
        for (int i = 0; i < players.Count; i++)
        {
            _launchNames[i] = players[i].Name;
        }
    }

    /// <summary>Back from a match: its scores stand on the Game Scores page and this pilot's Ready
    /// clears. The host opens a new round, which clears every guest's mark too.</summary>
    public void Land(IReadOnlyList<DogfightScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        _scores = new List<DogfightScore>(scores).ToArray();
        _ready = false;
        if (IsHost)
        {
            _options = _options with { Epoch = NextEpoch() };
        }
    }

    /// <summary>Sends one chat line under this pilot's name and shows it here at once. Refused when
    /// empty or longer than the edit box holds.</summary>
    public bool Say(string text)
    {
        string line = (text ?? "").Trim();
        if (line.Length == 0 || line.Length > LobbyChatMessage.MaxChars)
        {
            return false;
        }

        var message = new LobbyChatMessage(OwnName(), line);
        Add(message);
        if (IsHost)
        {
            foreach (int peer in _wire.AllPeers)
            {
                _wire.Tell(peer, message);
            }
        }
        else if (_hostPeer >= 0)
        {
            _wire.Tell(_hostPeer, message);
        }

        return true;
    }

    /// <summary>One menu frame, after the socket was stepped. The host relays each guest's chat to the
    /// others and sends each guest its options and list when they change. A guest takes the host's
    /// chat and sends its own pick.</summary>
    public void Step()
    {
        foreach (var (from, line) in _wire.TakeChat())
        {
            Add(line);
            if (!IsHost)
            {
                continue;
            }

            foreach (int peer in _wire.AllPeers)
            {
                if (peer != from)
                {
                    _wire.Tell(peer, line);
                }
            }
        }

        if (IsHost)
        {
            SendToGuests();
        }
        else
        {
            SendPick();
        }
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

    // Every host change is a new round: the Ready marks under the old options clear.
    private bool Change(DogfightOptionsMessage changed)
    {
        if (!IsHost)
        {
            return false;
        }

        if (changed == _options)
        {
            return true;
        }

        _options = changed with { Epoch = NextEpoch() };
        _ready = false;
        return true;
    }

    // Round 0 is a guest's "heard nothing", so the count wraps past it.
    private byte NextEpoch() => (byte)(_options.Epoch == byte.MaxValue ? 1 : _options.Epoch + 1);

    private string OwnName()
    {
        string name = _name() ?? "";
        return name.Length > 0 ? name : IsHost ? "Host" : "Pilot";
    }

    private void Add(LobbyChatMessage line)
    {
        if (_chat.Count >= ChatDepth)
        {
            _chat.RemoveAt(0);
        }

        _chat.Add(new DogfightChatLine(line.Name, line.Text));
    }

    private List<DogfightLobbySeat> HostRows()
    {
        var rows = new List<DogfightLobbySeat> { new(OwnName(), _airframe, Ready, true) };
        var peers = _wire.AllPeers;
        for (int i = 0; i < peers.Count; i++)
        {
            bool picked = _wire.Picks.TryGetValue(peers[i], out var pick);
            string name = picked && pick.Name is { Length: > 0 } named
                ? named
                : "Pilot " + (rows.Count + 1).ToString(CultureInfo.InvariantCulture);
            byte airframe = picked && pick.Airframe < AirframeCount ? pick.Airframe : DefaultAirframe;
            rows.Add(new DogfightLobbySeat(name, airframe, picked && pick.Ready && pick.Epoch == _options.Epoch, false));
        }

        return rows;
    }

    // A guest that has not heard the host's list yet shows itself alone.
    private DogfightLobbySeat[] GuestAlone() => new[] { new DogfightLobbySeat(OwnName(), _airframe, Ready, false) };

    private void SendToGuests()
    {
        var peers = _wire.AllPeers;
        var rows = HostRows();
        for (int i = 0; i < peers.Count; i++)
        {
            int peer = peers[i];
            if (!_optionsSent.TryGetValue(peer, out var sent) || sent != _options)
            {
                _wire.Tell(peer, _options);
                _optionsSent[peer] = _options;
            }

            var roster = new DogfightRosterMessage(_options.Epoch, (byte)(i + 1), rows);
            if (!_rosterSent.TryGetValue(peer, out var listed) || listed != roster)
            {
                _wire.Tell(peer, roster);
                _rosterSent[peer] = roster;
            }
        }

        foreach (int gone in new List<int>(_optionsSent.Keys))
        {
            if (!Contains(peers, gone))
            {
                _optionsSent.Remove(gone);
                _rosterSent.Remove(gone);
            }
        }
    }

    // A Ready mark stands under the round it was given in, so a new round from the host clears it.
    // The pick goes out again under the new round.
    private void SendPick()
    {
        if (_hostPeer < 0 || !Shown || _wire.DogfightOptions is not { } options)
        {
            return;
        }

        var pick = new CoopPickMessage(options.Epoch, Ready, _airframe, _fit, OwnName());
        if (_pickSent != pick)
        {
            _wire.Tell(_hostPeer, pick);
            _pickSent = pick;
        }
    }
}
