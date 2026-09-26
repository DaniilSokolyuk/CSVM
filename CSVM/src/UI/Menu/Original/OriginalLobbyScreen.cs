using System;
using System.Collections.Generic;
using System.Globalization;
using CSVM.Flight;
using CSVM.Mech3;
using CSVM.Net;
using CSVM.Session;

namespace CSVM.UI.Menu.Original;

/// <summary>The Multiplayer Lobby's four tabs, in the order the tab strip draws them.</summary>
public enum LobbyTab
{
    /// <summary>Mission Options: environment, type, victory, teams, lives and planes.</summary>
    Mission,

    /// <summary>Select Plane: the stock airframe this pilot flies.</summary>
    Plane,

    /// <summary>Select Ammo: the gun ammunition and the rocket on each wing cell.</summary>
    Ammo,

    /// <summary>Game Scores, greyed until a match has been flown.</summary>
    Scores,
}

/// <summary>
/// The original's Multiplayer Lobby over <see cref="DogfightLobby"/>, drawn from the lobby scripts'
/// own placements (<c>docs/org/menu-inventory.md</c>). The player list, Ready, chat and Leave Game
/// stand on every tab. Only a Dogfight flies, so Capture the Flag, the zeppelin mode, the teams,
/// Allow Custom Planes, Outlaw Components and Boot draw greyed. The host's options lock while the host
/// is Ready. Every pilot's own picks stay live, and changing one clears that pilot's Ready.
/// </summary>
public sealed class OriginalLobbyScreen : IOriginalScreenModule
{
    /// <summary>The Mission Options tab.</summary>
    public const string MissionTabKey = "MPL_TAB_MISSION";

    /// <summary>The Select Plane tab.</summary>
    public const string PlaneTabKey = "MPL_TAB_PLANE";

    /// <summary>The Select Ammo tab.</summary>
    public const string AmmoTabKey = "MPL_TAB_AMMO";

    /// <summary>The Game Scores tab, greyed until a match has been flown from this lobby.</summary>
    public const string ScoresTabKey = "MPL_TAB_SCORES";

    /// <summary>The Mission Environment box.</summary>
    public const string EnvironmentKey = "MPL_D_ENVIRONMENT";

    /// <summary>The Mission Type box.</summary>
    public const string TypeKey = "MPL_D_TYPE";

    /// <summary>LAUNCH!, live on the host once every pilot is Ready.</summary>
    public const string LaunchKey = "MPL_B_LAUNCH";

    /// <summary>The Time victory radio.</summary>
    public const string TimeRadioKey = "MPL_R_TIME";

    /// <summary>The Score victory radio.</summary>
    public const string ScoreRadioKey = "MPL_R_SCORE";

    /// <summary>The Time box, in minutes.</summary>
    public const string TimeKey = "MPL_E_TIME";

    /// <summary>The Score box.</summary>
    public const string ScoreKey = "MPL_E_SCORE";

    /// <summary>Restrict Number of Teams, greyed.</summary>
    public const string TeamsKey = "MPL_C_TEAMS";

    /// <summary>The Limited Lives checkbox.</summary>
    public const string LimitedLivesKey = "MPL_C_LIVES";

    /// <summary>The Lives box.</summary>
    public const string LivesKey = "MPL_E_LIVES";

    /// <summary>The Auto Respawn checkbox.</summary>
    public const string AutoRespawnKey = "MPL_C_RESPAWN";

    /// <summary>Allow Custom Planes, greyed.</summary>
    public const string CustomPlanesKey = "MPL_C_CUSTOM";

    /// <summary>Outlaw Components, greyed.</summary>
    public const string OutlawKey = "MPL_C_OUTLAW";

    /// <summary>The outlaw list's Select..., greyed.</summary>
    public const string SelectKey = "MPL_B_SELECT";

    /// <summary>The Default Planes sub-tab.</summary>
    public const string DefaultPlanesKey = "MPL_TAB_DEFAULT";

    /// <summary>The Custom Planes sub-tab, greyed.</summary>
    public const string CustomTabKey = "MPL_TAB_CUSTOM";

    /// <summary>The Select Plane box.</summary>
    public const string PlaneKey = "MPL_D_PLANE";

    /// <summary>The Guns sub-tab.</summary>
    public const string GunsTabKey = "MPL_TAB_GUNS";

    /// <summary>The Rockets sub-tab.</summary>
    public const string RocketsTabKey = "MPL_TAB_ROCKETS";

    /// <summary>Boot, greyed.</summary>
    public const string BootKey = "MPL_B_BOOT";

    /// <summary>Create Team, greyed.</summary>
    public const string TeamKey = "MPL_B_TEAM";

    /// <summary>The large Ready checkbox.</summary>
    public const string ReadyKey = "MPL_C_READY";

    /// <summary>The chat edit box.</summary>
    public const string ChatKey = "MPL_E_CHAT";

    /// <summary>Send, which sends the chat box's line.</summary>
    public const string SendKey = "MPL_B_SEND";

    /// <summary>Leave Game, back to the Connection page.</summary>
    public const string LeaveKey = "MPL_B_LEAVE";

    /// <summary>How many rows the player list shows.</summary>
    public const int VisiblePlayers = 11;

    private const string GunKeyPrefix = "MPL_D_GUN_";
    private const string CellKeyPrefix = "MPL_D_CELL_";

    private const string Background = "MP_LOBBY_BACKGROUND.JPG";
    private const string SmallArt = "MP_B_SMALL.PNG";
    private const string MediumArt = "MP_B_MEDIUM.PNG";
    private const string LargeArt = "MP_B_LARGE.PNG";
    private const string ReadyArt = "MP_B_CHECKBOXLARGE.PNG";
    private const string RadioArt = "MP_B_RADIO8STATESSM.PNG";
    private const string CheckArt = "MP_B_CHECKBOX8STATES.PNG";
    private const string MarkArt = "MP_B_CHECKBOX.PNG";
    private const string ArrowArt = "MP_B_LISTBOXARROW.PNG";
    private const string TabLargeArt = "MP_LOBBY_TABLARGE.PNG";
    private const string TabSmallArt = "MP_LOBBY_TABSMALL.PNG";
    private const string IconArt = "MP_PLANEICONSTOPFRONT.PNG";

    // The tab page's corner and the two list geometries every tab shares.
    private const float PageX = 314f;
    private const float PageY = 26f;
    private const float ListX = 34f;
    private const float ListY = 83f;
    private const float ListPitch = 20f;
    private const float NameWidth = 230f;
    private const float ChatX = 34f;
    private const float ChatY = 373f;
    private const float ChatWidth = 735f;
    private const float ChatHeight = 165f;
    private const float ChatNameColumn = 100f;
    private const float TextFallback = 12f;
    private const float DisabledArrow = 0.45f;
    private const int IconFrames = 11;

    private static readonly string[] TabKeys = { MissionTabKey, PlaneTabKey, AmmoTabKey, ScoresTabKey };
    private static readonly string[] TabPages =
        { "MP_LOBBY_MISSION.PNG", "MP_LOBBY_PLANE.PNG", "MP_LOBBY_AMMO.PNG", "MP_LOBBY_STATSCREEN.PNG" };

    private static readonly float[] TabX = { 324f, 442f, 548f, 656f };
    private static readonly float[] TabWidth = { 110f, 100f, 100f, 100f };
    private static readonly int[] TabIds = { 10094, 10114, 10119, 10507 };
    private static readonly string[] TabNames = { "Mission Options", "Select Plane", "Select Ammo", "Game Scores" };
    private static readonly string[] ShortNames =
    {
        "Hoplite", "Hellhound", "Balmoral", "Bloodhawk", "Brigand", "Devastator", "Firebrand", "Fury",
        "Kestrel", "Peacemaker", "Warhawk",
    };

    private static readonly string[] RatingLabels = { "TOP SPEED:", "ARMOR:", "AGILITY:", "OFFENSE:" };
    private static readonly string[] RatingWords = { "Poor", "Fair", "Average", "Good", "Excellent" };
    private static readonly int[] Calibres = { 70, 60, 50, 40, 30 };
    private static readonly string[] TypeNames = { "Capture the Flag", "Deathmatch", "Zeppelin vs Zeppelin" };
    private static readonly int[] TypeDescriptions = { 10124, 10123, 10125 };
    private static readonly int[] ScoreHeaderIds = { 10542, 10543, 10544, 10545, 10546 };
    private static readonly string[] ScoreHeaders = { "Team Name", "Points", "Kills", "Deaths", "Hits %" };
    private static readonly (float X, float Y)[] ScoreHeaderAt = { (21f, 43f), (179f, 44f), (242f, 44f), (303f, 44f), (362f, 44f) };

    // The scripts' colours. They are the plaque labels in four states, the tab ink, the picked
    // sub-tab's red, the Ready? label, the LAUNCH! blink and the own name.
    private static readonly BoardTint LabelDisabled = new(142, 142, 142);
    private static readonly BoardTint LabelNormal = new(226, 224, 206);
    private static readonly BoardTint LabelRollover = new(198, 188, 140);
    private static readonly BoardTint LabelPressed = new(255, 204, 102);
    private static readonly BoardTint TabDisabled = new(128, 128, 128);
    private static readonly BoardTint Black = new(0, 0, 0);
    private static readonly BoardTint Picked = new(142, 0, 0);
    private static readonly BoardTint ReadyLabel = new(255, 255, 170);
    private static readonly BoardTint LaunchBlink = new(255, 50, 25);
    private static readonly BoardTint OwnName = new(255, 0, 0);

    private readonly Func<NetPlayFeature?> _net;
    private readonly IOriginalScreenHost _host;
    private readonly string? _dataRoot;
    private readonly Func<StockLoadouts?> _stock;
    private readonly Func<IReadOnlyList<int>> _pads;
    private readonly Func<string?> _pilotName;
    private UiStrings? _strings;
    private string? _open;
    private int _listTop;
    private string _chat = string.Empty;
    private string? _typing;
    private string _draft = string.Empty;
    private double _clock;
    private int _blink;
    private int _heard;

    /// <summary>A lobby module over the door <paramref name="net"/> answers. Its words come from the
    /// string table under <paramref name="dataRoot"/>, its fits from <paramref name="stock"/>. <paramref name="pads"/> names seat 0's flight devices and
    /// <paramref name="pilotName"/> the pilot's name, null for none.</summary>
    public OriginalLobbyScreen(
        Func<NetPlayFeature?> net, IOriginalScreenHost host, string? dataRoot, Func<StockLoadouts?>? stock,
        Func<IReadOnlyList<int>>? pads = null, Func<string?>? pilotName = null)
    {
        _net = net ?? throw new ArgumentNullException(nameof(net));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _dataRoot = dataRoot;
        _stock = stock ?? (() => null);
        _pads = pads ?? (() => Array.Empty<int>());
        _pilotName = pilotName ?? (() => null);
    }

    /// <summary>The tab showing.</summary>
    public LobbyTab Tab { get; private set; } = LobbyTab.Mission;

    /// <summary>Whether the Select Ammo tab shows its Rockets page rather than its Guns page.</summary>
    public bool Rockets { get; private set; }

    /// <summary>The dropdown whose list stands open, or null.</summary>
    public string? OpenDropdown => _open;

    /// <summary>The chat box's line as typed so far.</summary>
    public string ChatDraft => _chat;

    /// <summary>Whether seat 0's typed characters feed one of the lobby's boxes. That holds while
    /// the lobby shows with a box focused and nothing over it.</summary>
    internal bool CapturingText =>
        _host.Screen == OriginalScreen.Lobby && !_host.DialogOpen && _open == null && IsBox(_host.FocusedKey);

    private UiStrings Strings => _strings ??= (_dataRoot is { } root ? UiStrings.TryLoad(root) : null) ?? UiStrings.Empty;

    private DogfightLobby? Lobby => _net()?.Dogfight;

    /// <summary>A gun box's key by its zero-based slot.</summary>
    public static string GunKey(int slot) => GunKeyPrefix + slot.ToString(CultureInfo.InvariantCulture);

    /// <summary>A rocket box's key by its zero-based wing cell.</summary>
    public static string CellKey(int cell) => CellKeyPrefix + cell.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether a screen is this module's.</summary>
    public bool Owns(OriginalScreen screen) => screen == OriginalScreen.Lobby;

    /// <summary>The Connection page's Host and the games list's Create Game: a listen server with
    /// the lobby standing on it, under the pilot's own name. A socket that will not open says why
    /// over the page it was asked from.</summary>
    public void OpenHost()
    {
        if (_net() is not { } net)
        {
            return;
        }

        net.StopSearch();
        if (net.Stage is NetDoorStage.Failed)
        {
            net.Close();
        }

        net.PlayerName = _pilotName() ?? string.Empty;
        net.OpenDogfightHost(NetSeats.MaxPlayers - 1);
        if (net.Dogfight == null)
        {
            _host.RaiseDialog(net.Fault.Length > 0 ? net.Fault : "The game could not be hosted.", DialogIcon.Warning,
                new OriginalDialogAnswer(OriginalShell.DialogOkKey, CampaignBoards.DialogCenterKey, Word(100, "OK"), null));
            return;
        }

        Enter();
    }

    /// <summary>A joined Dogfight guest's way in, once its host's advert names a Dogfight.</summary>
    public void OpenGuest()
    {
        if (_net() is { } net)
        {
            net.PlayerName = _pilotName() ?? string.Empty;
            net.Dogfight?.Show();
        }

        Enter();
    }

    /// <summary>Back from a match onto its lobby's Game Scores page, every Ready cleared. False when
    /// the door no longer holds a lobby, and the caller then shows the Connection page.</summary>
    public bool Land(IReadOnlyList<DogfightScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        if (_net() is not { CanLaunch: true } || Lobby is not { Shown: true } lobby)
        {
            return false;
        }

        lobby.Land(scores);
        Enter();
        Tab = LobbyTab.Scores;
        return true;
    }

    /// <summary>Shows a tab directly, the screenshot aids' door. It opens Game Scores too, which
    /// the pointer cannot.</summary>
    public void ShowTab(LobbyTab tab, bool rockets = false)
    {
        _open = null;
        Tab = tab;
        Rockets = rockets;
    }

    /// <summary>The showing tab's rows, or the open list's items alone while one stands open.</summary>
    public void BuildRows(List<OriginalRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (OpenList() is { } drop)
        {
            _listTop = OriginalDropLists.Top(drop, _listTop, _host.FocusedRow);
            OriginalDropLists.AddRows(drop, _listTop, rows, StripSize);
            return;
        }

        Widgets(rows);
    }

    /// <summary>No lobby list scrolls: every dropdown shows all its items.</summary>
    public void Lists(List<OriginalList> lists)
    {
    }

    /// <summary>A sideways step on a live dropdown picks its next allowed value.</summary>
    public bool StepSideways(IReadOnlyList<OriginalRow> rows, int focus, int direction)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (_open != null || focus < 0 || focus >= rows.Count || direction == 0)
        {
            return false;
        }

        var row = rows[focus];
        if (row.Kind != OriginalRowKind.Dropdown || !row.Enabled || DropdownFor(row.Key) is not { } list || list.Items.Count == 0)
        {
            return false;
        }

        int step = Math.Sign(direction);
        int at = list.Current;
        for (int i = 0; i < list.Items.Count; i++)
        {
            at = ((at + step) % list.Items.Count + list.Items.Count) % list.Items.Count;
            if (list.Allowed(at))
            {
                list.Select(at);
                return true;
            }
        }

        return true;
    }

    /// <summary>Closes the open list and puts the focus back on its box.</summary>
    public bool CloseDropdown()
    {
        if (_open is not { } key)
        {
            return false;
        }

        _open = null;
        _host.FocusKey(key);
        return true;
    }

    /// <summary>The lobby's answer to an activated row.</summary>
    public MenuExit? Activate(OriginalRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _typing = null;
        if (row.Key.IndexOf(':', StringComparison.Ordinal) is var colon and > 0)
        {
            PickFromList(row.Key[..colon], row.Key[(colon + 1)..]);
            return null;
        }

        var lobby = Lobby;
        switch (row.Key)
        {
            case MissionTabKey:
            case PlaneTabKey:
            case AmmoTabKey:
            case ScoresTabKey:
                Tab = (LobbyTab)Array.IndexOf(TabKeys, row.Key);
                return null;
            case GunsTabKey:
                Rockets = false;
                return null;
            case RocketsTabKey:
                Rockets = true;
                return null;
            case TimeRadioKey:
                lobby?.SetVictory(DogfightVictory.Time);
                return null;
            case ScoreRadioKey:
                lobby?.SetVictory(DogfightVictory.Score);
                return null;
            case LimitedLivesKey:
                lobby?.SetLimitedLives(!lobby.Options.LimitedLives);
                return null;
            case AutoRespawnKey:
                lobby?.SetAutoRespawn(!lobby.Options.AutoRespawn);
                return null;
            case ReadyKey:
                lobby?.SetReady(!lobby.Ready);
                return null;
            case ChatKey:
            case SendKey:
                if (lobby != null && lobby.Say(_chat))
                {
                    _chat = string.Empty;
                }

                return null;
            case LaunchKey:
                return _net() is { } net && lobby is { CanLaunch: true } ? Exit(net, lobby) : null;
            case LeaveKey:
                Leave();
                return null;
        }

        if (row.Kind == OriginalRowKind.Dropdown && DropdownFor(row.Key) is { } list)
        {
            _open = row.Key;
            _listTop = 0;
            _host.FocusedRow = Math.Max(0, list.Current);
        }

        return null;
    }

    /// <summary>Back closes an open list, and otherwise leaves the game as Leave Game does.</summary>
    public bool Back()
    {
        if (!CloseDropdown())
        {
            Leave();
        }

        return true;
    }

    /// <summary>The lobby as drawn: the background, the tab page, the players, the chat and the
    /// plaques. An open list stands over all of it.</summary>
    public void Compose(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(layers);

        // The widgets are rebuilt closed, so the page under an open list is drawn as it stands.
        var widgets = new List<OriginalRow>();
        Widgets(widgets);
        string focused = _open ?? (focus >= 0 && focus < rows.Count && !_host.DialogOpen ? rows[focus].Key : string.Empty);
        int pressedAt = _host.DialogOpen || _open != null ? -1 : _host.PressedRow;
        string pressed = pressedAt >= 0 && pressedAt < rows.Count ? rows[pressedAt].Key : string.Empty;

        layers.Backdrop.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, Background), 0f, 0f));
        // The page is backdrop so the boxes' fills land over it and under their words.
        layers.Backdrop.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, TabPages[(int)Tab]), PageX, PageY));
        ComposePlayers(layers);
        ComposeChat(layers);
        foreach (var row in widgets)
        {
            ComposeWidget(row, row.Key == focused, row.Key == pressed, layers);
        }

        ComposePage(layers);
        if (_open != null)
        {
            ComposeOpenList(rows, focus, layers);
        }

        if (_host.SeatPanel(false) is { } strip)
        {
            layers.Overlays.Add(strip);
        }
    }

    /// <summary>One menu frame's upkeep while the lobby shows, after the door was stepped. It moves
    /// the LAUNCH! blink, and it notices a changed list, option or chat line. Returns whether the
    /// picture changed.</summary>
    public bool Tick(double dt)
    {
        if (_host.Screen != OriginalScreen.Lobby)
        {
            return false;
        }

        if (_typing != null && _host.FocusedKey != _typing)
        {
            _typing = null;
        }

        _clock += dt;
        int blink = (int)(_clock * 4.0) % 2;
        bool changed = blink != _blink && Lobby is { CanLaunch: true } && Tab == LobbyTab.Mission;
        _blink = blink;
        int heard = Hash(Lobby);
        changed |= heard != _heard;
        _heard = heard;
        return changed;
    }

    /// <summary>A Dogfight guest's launch once its host has launched, taken after the door was
    /// stepped, or null.</summary>
    public MenuExit? GuestLaunch() =>
        _net() is { DogfightLaunchDue: true } net && net.Dogfight is { } lobby ? Exit(net, lobby) : null;

    /// <summary>Typed characters and Backspace into the focused box. The chat box takes printable
    /// characters up to its width. The number boxes take digits, and a value inside the box's range
    /// is set as it is typed.</summary>
    internal bool TypeText(MenuCommands commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (!CapturingText || Lobby is not { } lobby || (commands.Typed.Length == 0 && !commands.Erase))
        {
            return false;
        }

        string key = _host.FocusedKey;
        if (key == ChatKey)
        {
            string before = _chat;
            foreach (char c in commands.Typed)
            {
                if (c is >= ' ' and < (char)127 && _chat.Length < LobbyChatMessage.MaxChars)
                {
                    _chat += c;
                }
            }

            if (commands.Erase && _chat.Length > 0)
            {
                _chat = _chat[..^1];
            }

            return _chat != before;
        }

        string draft = _typing == key ? _draft : BoxValue(key, lobby);
        foreach (char c in commands.Typed)
        {
            if (char.IsAsciiDigit(c) && draft.Length < BoxWidth(key))
            {
                draft += c;
            }
        }

        if (commands.Erase && draft.Length > 0)
        {
            draft = draft[..^1];
        }

        _typing = key;
        _draft = draft;
        if (int.TryParse(draft, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        {
            _ = key switch
            {
                TimeKey => lobby.SetTimeMinutes(value),
                ScoreKey => lobby.SetScore(value),
                _ => lobby.SetLives(value),
            };
        }

        return true;
    }

    private static bool IsBox(string key) => key is ChatKey or TimeKey or ScoreKey or LivesKey;

    private static int BoxWidth(string key) => key == ScoreKey ? 3 : 2;

    private static string BoxValue(string key, DogfightLobby lobby) => key switch
    {
        TimeKey => lobby.Options.TimeMinutes.ToString(CultureInfo.InvariantCulture),
        ScoreKey => lobby.Options.Score.ToString(CultureInfo.InvariantCulture),
        _ => lobby.Options.Lives.ToString(CultureInfo.InvariantCulture),
    };

    private static int Hash(DogfightLobby? lobby)
    {
        if (lobby == null)
        {
            return 0;
        }

        var hash = default(HashCode);
        hash.Add(lobby.Options);
        hash.Add(lobby.HasOptions);
        hash.Add(lobby.Ready);
        hash.Add(lobby.You);
        hash.Add(lobby.Chat.Count);
        hash.Add(lobby.Scores.Count);
        foreach (var player in lobby.Players)
        {
            hash.Add(player);
        }

        return hash.ToHashCode() | 1;
    }

    private static BoardTint LabelTint(bool enabled, bool focused, bool pressed) =>
        !enabled ? LabelDisabled : pressed ? LabelPressed : focused ? LabelRollover : LabelNormal;

    private static int StateFrame(OriginalRow row, bool focused, bool pressed) =>
        !row.Enabled ? 0 : pressed ? 3 : focused ? 2 : 1;

    private static int Index(string key, string prefix) =>
        int.TryParse(key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int i) ? i : -1;

    private static GunSpec? GunFor(LoadoutDef? def, int slot)
    {
        if (def == null)
        {
            return null;
        }

        foreach (var gun in def.Guns)
        {
            if (gun.Slot == slot && !gun.Turret)
            {
                return gun;
            }
        }

        return null;
    }

    private static CoopFit Refit(CoopFit fit, int slot, int ammo, int cell, int ordnance)
    {
        var ammoPicks = new int[CoopFit.GunSlots];
        for (int i = 0; i < ammoPicks.Length; i++)
        {
            ammoPicks[i] = i == slot ? ammo : fit.AmmoAt(i);
        }

        var cells = new int[CoopFit.Cells];
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i] = i == cell ? ordnance : fit.OrdnanceAt(i);
        }

        return CoopFit.Of(ammoPicks, cells);
    }

    private static string Item(DropdownList list) =>
        list.Current >= 0 && list.Current < list.Items.Count ? list.Items[list.Current] : string.Empty;

    private void Enter()
    {
        _open = null;
        _typing = null;
        _chat = string.Empty;
        Tab = LobbyTab.Mission;
        Rockets = false;
        _host.CloseDialog();
        _host.Open(OriginalScreen.Lobby);
    }

    private void Leave()
    {
        _open = null;
        _typing = null;
        _chat = string.Empty;
        _net()?.Close();
        _host.CloseDialog();
        _host.Open(OriginalScreen.Connection);
    }

    // Seat 0 on the airframe and fit this pilot picked, on the chapter and the rules the host's
    // options name. The wire rides out with it.
    private LaunchExit Exit(NetPlayFeature net, DogfightLobby lobby)
    {
        var options = lobby.Options;
        var seat = new MenuSeatChoice(
            PlanePickerRoster.AirframeNode(lobby.Airframe), _pads(), CampaignLoadout.For(lobby.Fit, _stock()));
        return new LaunchExit(
            DogfightLobby.ChapterOf(options.Environment), new[] { seat }, MenuMode.Versus,
            Match: DogfightLobby.RulesOf(options), Net: net.BuildLaunch());
    }

    private string Word(int id, string fallback)
    {
        string text = Strings.Text(id, fallback).Trim().TrimStart(']');
        return text.Length > 0 ? text : fallback;
    }

    // A string's face in regular weight, as the Connection page draws the multiplayer faces.
    private LanguiFace? Regular(int id) => LanguiFace.Parse(Strings.Face(id)) is { } face ? face with { Bold = false } : null;

    private BoardLine Line(
        int id, string fallback, float x, float y, float width, BoardTint colour, BoardJustify justify = BoardJustify.Left,
        string? text = null, int faceId = 0)
    {
        var face = Regular(faceId != 0 ? faceId : id);
        return new BoardLine(
            text ?? Word(id, fallback), x, y, width, face?.Pixels ?? TextFallback, BoardInk.Row, -1,
            Justify: justify, Face: face, Colour: colour);
    }

    private (float Width, float Height) StripSize(BoardArt? art, float fallbackWidth, float fallbackHeight) =>
        OriginalWidgets.StripSize(art, _host.Measure, fallbackWidth, fallbackHeight);

    private OriginalRow Strip(
        string key, string art, float x, float y, bool enabled, int column, float fallbackWidth, float fallbackHeight, int frames = 4)
    {
        var strip = new BoardArt(BoardArtLibrary.Ui, art, frames);
        var size = StripSize(strip, fallbackWidth, fallbackHeight);
        return new OriginalRow(key, string.Empty, OriginalRowKind.Button, x, y, size.Width, size.Height, enabled, column, strip);
    }

    private OriginalRow Check(string key, string art, float x, float y, float hitWidth, float size, bool enabled) =>
        new(key, string.Empty, OriginalRowKind.Radio, x, y, hitWidth, size, enabled, 1, new BoardArt(BoardArtLibrary.Ui, art, 8));

    private OriginalRow Drop(string key, string label, float x, float y, float width, float height, bool enabled) =>
        new(key, label, OriginalRowKind.Dropdown, x, y, width, height, enabled, 1, null);

    private OriginalRow Box(string key, string label, float x, float y, float width, float height, bool enabled, int column = 1) =>
        new(key, label, OriginalRowKind.TextField, x, y, width, height, enabled, column, null);

    // Every widget of the showing tab and its frame, in focus order. The tabs and the page come
    // first, then the player list's plaques, the chat line and Leave Game.
    private void Widgets(List<OriginalRow> rows)
    {
        var lobby = Lobby;
        for (int i = 0; i < TabKeys.Length; i++)
        {
            rows.Add(new OriginalRow(TabKeys[i], string.Empty, OriginalRowKind.TextButton, TabX[i], 24f, TabWidth[i], 25f,
                lobby != null && (i != (int)LobbyTab.Scores || lobby.Scores.Count > 0), 1, null));
        }

        if (lobby != null)
        {
            switch (Tab)
            {
                case LobbyTab.Mission:
                    MissionRows(lobby, rows);
                    break;
                case LobbyTab.Plane:
                    PlaneRows(lobby, rows);
                    break;
                case LobbyTab.Ammo:
                    AmmoRows(lobby, rows);
                    break;
            }
        }

        rows.Add(Strip(BootKey, SmallArt, 19f, 325f, false, 0, 74f, 37f));
        rows.Add(Strip(TeamKey, LargeArt, 105f, 325f, false, 0, 131f, 37f));
        rows.Add(new OriginalRow(ReadyKey, string.Empty, OriginalRowKind.Radio, 250f, 325f, 58f, 37f,
            lobby is { HasOptions: true }, 0, new BoardArt(BoardArtLibrary.Ui, ReadyArt, 8)));
        rows.Add(Box(ChatKey, _chat, 88f, 552f, 481f, 18f, lobby != null, 0));
        rows.Add(Strip(SendKey, SmallArt, 577f, 548f, lobby != null, 1, 74f, 37f));
        rows.Add(Strip(LeaveKey, LargeArt, 655f, 548f, true, 1, 131f, 37f));
    }

    private void MissionRows(DogfightLobby lobby, List<OriginalRow> rows)
    {
        var options = lobby.Options;
        bool live = lobby.IsHost && !lobby.Ready;
        rows.Add(Drop(EnvironmentKey, EnvironmentWord(options.Environment), PageX + 25f, PageY + 63f, 175f, 22f, live));
        rows.Add(Drop(TypeKey, TypeWord(options.MissionType), PageX + 25f, PageY + 108f, 175f, 22f, live));
        rows.Add(Strip(LaunchKey, LargeArt, PageX + 47f, PageY + 284f, lobby.CanLaunch, 1, 131f, 37f));
        rows.Add(Check(TimeRadioKey, RadioArt, PageX + 241f, PageY + 70f, 120f, 12f, live));
        rows.Add(Box(TimeKey, BoxText(TimeKey, lobby), PageX + 370f, PageY + 68f, 73f, 18f, live && options.Victory == DogfightVictory.Time));
        rows.Add(Check(ScoreRadioKey, RadioArt, PageX + 241f, PageY + 92f, 120f, 12f, live));
        rows.Add(Box(ScoreKey, BoxText(ScoreKey, lobby), PageX + 370f, PageY + 92f, 73f, 18f, live && options.Victory == DogfightVictory.Score));
        rows.Add(Check(TeamsKey, CheckArt, PageX + 241f, PageY + 133f, 180f, 11f, false));
        rows.Add(Check(LimitedLivesKey, CheckArt, PageX + 241f, PageY + 192f, 110f, 11f, live));
        rows.Add(Box(LivesKey, BoxText(LivesKey, lobby), PageX + 360f, PageY + 193f, 25f, 22f, live && options.LimitedLives));
        rows.Add(Check(AutoRespawnKey, CheckArt, PageX + 241f, PageY + 207f, 110f, 11f, live));
        rows.Add(Check(CustomPlanesKey, CheckArt, PageX + 241f, PageY + 247f, 150f, 11f, false));
        rows.Add(Check(OutlawKey, CheckArt, PageX + 265f, PageY + 262f, 150f, 11f, false));
        rows.Add(Strip(SelectKey, MediumArt, PageX + 295f, PageY + 277f, false, 1, 96f, 37f));
    }

    private void PlaneRows(DogfightLobby lobby, List<OriginalRow> rows)
    {
        rows.Add(Strip(DefaultPlanesKey, TabLargeArt, PageX + 194f, PageY + 44f, true, 1, 119f, 23f));
        rows.Add(Strip(CustomTabKey, TabLargeArt, PageX + 316f, PageY + 44f, false, 1, 119f, 23f));
        rows.Add(Drop(PlaneKey, PlaneWord(lobby.Airframe), PageX + 11f, PageY + 80f, 195f, 36f, true));
    }

    private void AmmoRows(DogfightLobby lobby, List<OriginalRow> rows)
    {
        rows.Add(Strip(GunsTabKey, TabSmallArt, PageX + 287f, PageY + 46f, true, 1, 69f, 23f));
        rows.Add(Strip(RocketsTabKey, TabSmallArt, PageX + 363f, PageY + 46f, true, 1, 69f, 23f));
        var def = StockDef(lobby.Airframe);
        if (!Rockets)
        {
            for (int slot = 0; slot < CoopFit.GunSlots; slot++)
            {
                bool gun = GunFor(def, slot + 1) != null;
                string label = gun && DropdownFor(GunKey(slot)) is { } list ? Item(list) : Word(10144, "<none>");
                rows.Add(Drop(GunKey(slot), label, PageX + 11f, PageY + 100f + (50f * slot), 195f, 22f, gun));
            }

            return;
        }

        for (int cell = 0; cell < CoopFit.Cells; cell++)
        {
            bool hung = Loadout.PylonForCell(cell, def?.Hardpoints) != 0;
            string label = hung && DropdownFor(CellKey(cell)) is { } list ? Item(list) : Word(10144, "<none>");
            rows.Add(Drop(CellKey(cell), label, PageX + 25f, PageY + 80f + (30f * cell), 195f, 22f, hung));
        }
    }

    private string BoxText(string key, DogfightLobby lobby) => _typing == key ? _draft : BoxValue(key, lobby);

    private string EnvironmentWord(int environment) => Word(10558 + environment, DogfightLobby.EnvironmentName(environment));

    private string TypeWord(int type) =>
        type is >= 0 and < 3 ? Word(10555 + type, TypeNames[type]) : string.Empty;

    private string PlaneWord(int airframe) =>
        Word(10565, "Stock") + " " + ShortNames[Math.Clamp(airframe, 0, ShortNames.Length - 1)];

    private LoadoutDef? StockDef(int airframe) => _stock()?.ForModel(PlanePickerRoster.AirframeNode(airframe));

    // The list behind a dropdown: its words, the value it stands on, which values can be picked and
    // how a pick is written. Null for a key that is not a live dropdown.
    private DropdownList? DropdownFor(string key)
    {
        if (Lobby is not { } lobby)
        {
            return null;
        }

        switch (key)
        {
            case EnvironmentKey:
                {
                    var items = new string[DogfightLobby.EnvironmentCount];
                    for (int i = 0; i < items.Length; i++)
                    {
                        items[i] = EnvironmentWord(i);
                    }

                    return new DropdownList(items, lobby.Options.Environment, _ => true, i => lobby.SetEnvironment(i));
                }

            case TypeKey:
                return new DropdownList(new[] { TypeWord(0), TypeWord(1), TypeWord(2) }, lobby.Options.MissionType,
                    i => DogfightLobby.Flies((DogfightMissionType)i), i => lobby.SetMissionType((DogfightMissionType)i));
            case PlaneKey:
                {
                    var items = new string[DogfightLobby.AirframeCount];
                    for (int i = 0; i < items.Length; i++)
                    {
                        items[i] = PlaneWord(i);
                    }

                    return new DropdownList(items, lobby.Airframe, _ => true, i =>
                    {
                        if (i != lobby.Airframe)
                        {
                            lobby.Pick(i, default);
                        }
                    });
                }
        }

        var def = StockDef(lobby.Airframe);
        if (key.StartsWith(GunKeyPrefix, StringComparison.Ordinal) && GunFor(def, Index(key, GunKeyPrefix) + 1) is { } gun)
        {
            int slot = Index(key, GunKeyPrefix);
            var options = _stock()?.Options.GunAmmo;
            var items = new string[CampaignLoadout.AmmoNames.Length];
            for (int i = 0; i < items.Length; i++)
            {
                items[i] = options != null && i < options.Count ? options[i].Label : CampaignLoadout.AmmoNames[i];
            }

            int stored = lobby.Fit.AmmoAt(slot);
            int current = stored is >= 0 and < 4 ? stored : Math.Max(0, Array.IndexOf(CampaignLoadout.AmmoNames, gun.Ammo));
            return new DropdownList(items, current, _ => true, i => lobby.Pick(lobby.Airframe, Refit(lobby.Fit, slot, i, -1, 0)));
        }

        if (key.StartsWith(CellKeyPrefix, StringComparison.Ordinal) && _stock()?.Options.PylonOrdnance is { Count: > 0 } table)
        {
            int cell = Index(key, CellKeyPrefix);
            int pylon = Loadout.PylonForCell(cell, def?.Hardpoints);
            if (pylon == 0)
            {
                return null;
            }

            var items = new string[table.Count];
            int current = -1;
            int entry = Array.IndexOf(Loadout.PylonFillOrder, pylon);
            string? stock = def?.Hardpoints is { } hp && entry >= 0 && entry < hp.Stock.Length ? hp.Stock[entry] : null;
            for (int i = 0; i < items.Length; i++)
            {
                items[i] = table[i].Label;
                if (table[i].Id == stock)
                {
                    current = i;
                }
            }

            int stored = lobby.Fit.OrdnanceAt(cell);
            current = stored is > 0 && stored <= table.Count ? stored - 1 : current;
            return new DropdownList(items, current, _ => true, i => lobby.Pick(lobby.Airframe, Refit(lobby.Fit, -1, 0, cell, i + 1)));
        }

        return null;
    }

    // The open dropdown's list hung under its box. The Select Plane box is taller than its items,
    // so its list starts under the box at the items' own height.
    private OpenDropList? OpenList()
    {
        if (_open is not { } key || DropdownFor(key) is not { } list)
        {
            return null;
        }

        var closed = new List<OriginalRow>();
        Widgets(closed);
        var box = closed.Find(row => row.Key == key);
        if (box == null)
        {
            return null;
        }

        float item = Math.Min(box.Height, 22f);
        return OriginalDropLists.Over(key, null, list.Items, (box.X, box.Y + box.Height - item, box.Width, item), list.Allowed);
    }

    private void PickFromList(string key, string suffix)
    {
        if (DropdownFor(key) is { } list && int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            && index >= 0 && index < list.Items.Count && list.Allowed(index))
        {
            list.Select(index);
        }

        _open = null;
        _host.FocusKey(key);
    }

    private void ComposePlayers(BoardLayers layers)
    {
        var lobby = Lobby;
        var players = lobby?.Players ?? Array.Empty<DogfightLobbySeat>();
        layers.Lines.Add(Line(10046, "MULTIPLAYER LOBBY", 60f, 22f, 0f, Black));
        string count = Strings.Format(10048, players.Count, NetSeats.MaxPlayers);
        layers.Lines.Add(Line(10048, string.Empty, 34f, 54f, 0f, Black,
            text: count.Length > 0 ? count : $"Players ({players.Count} of {NetSeats.MaxPlayers})"));
        layers.Lines.Add(Line(10052, "Ready", 256f, 54f, 0f, Black));
        var face = Regular(10575);
        float size = face?.Pixels ?? TextFallback;
        var mark = new BoardArt(BoardArtLibrary.Ui, MarkArt, 4);
        for (int i = 0; i < players.Count && i < VisiblePlayers; i++)
        {
            float y = ListY + (i * ListPitch);
            var colour = lobby != null && i == lobby.You ? OwnName : Black;
            layers.Lines.Add(new BoardLine(players[i].Name, ListX + 2f, y + ((ListPitch - size) / 2f), NameWidth - 4f, size,
                BoardInk.Row, -1, Face: face, Colour: colour));
            layers.Pictures.Add(new BoardPicture(mark, ListX + NameWidth, y + 6f, players[i].Ready ? 3 : 1));
        }
    }

    private void ComposeChat(BoardLayers layers)
    {
        var lobby = Lobby;
        var chat = lobby?.Chat ?? Array.Empty<DogfightChatLine>();
        string own = lobby != null && lobby.You < lobby.Players.Count ? lobby.Players[lobby.You].Name : string.Empty;
        var face = Regular(10575);
        float size = face?.Pixels ?? TextFallback;
        float pitch = size + 2f;
        int fits = Math.Max(1, (int)(ChatHeight / pitch));
        int first = Math.Max(0, chat.Count - fits);
        for (int i = first; i < chat.Count; i++)
        {
            float y = ChatY + ((i - first) * pitch);
            layers.Lines.Add(new BoardLine(chat[i].Name, ChatX + 4f, y, ChatNameColumn - 8f, size, BoardInk.Row, -1,
                Face: face, Colour: chat[i].Name == own ? OwnName : Black));
            layers.Lines.Add(new BoardLine(chat[i].Text, ChatX + ChatNameColumn, y, ChatWidth - ChatNameColumn - 4f, size,
                BoardInk.Row, -1, Face: face, Colour: Black));
        }
    }

    // One widget in its state, and the words the script writes beside it.
    private void ComposeWidget(OriginalRow row, bool focused, bool pressed, BoardLayers layers)
    {
        switch (row.Kind)
        {
            case OriginalRowKind.TextButton:
                int tab = Array.IndexOf(TabKeys, row.Key);
                layers.Lines.Add(Line(TabIds[tab], TabNames[tab], row.X, row.Y + 6f, row.Width, row.Enabled ? Black : TabDisabled,
                    BoardJustify.Center));
                break;
            case OriginalRowKind.Dropdown:
                ComposeDropdown(row, focused, layers);
                break;
            case OriginalRowKind.TextField:
                ComposeBox(row, focused, layers);
                break;
            case OriginalRowKind.Radio:
                ComposeCheck(row, focused, pressed, layers);
                break;
            default:
                ComposePlaque(row, focused, pressed, layers);
                break;
        }
    }

    private void ComposeDropdown(OriginalRow row, bool focused, BoardLayers layers)
    {
        var fill = row.Enabled ? (R: (byte)222, G: (byte)207, B: (byte)156) : (R: (byte)181, G: (byte)174, B: (byte)156);
        layers.Fills.Add(new BoardFill(row.X, row.Y, row.Width, row.Height, fill.R, fill.G, fill.B));
        layers.Fills.Add(new BoardFill(row.X, row.Y, row.Width, row.Height, 0, 0, 0, Border: true));
        if (focused && row.Enabled)
        {
            layers.Fills.Add(_host.FocusMark(row));
        }

        int faceId = row.Key is EnvironmentKey or TypeKey ? 10558 : 10144;
        var face = Regular(faceId);
        float size = face?.Pixels ?? TextFallback;
        float textY = row.Key == PlaneKey ? row.Y + 5f : row.Y + ((row.Height - size) / 2f) - 1f;
        layers.Lines.Add(new BoardLine(row.Label, row.X + 8f, textY, row.Width - 34f, size, BoardInk.Row, -1, Face: face, Colour: Black));
        layers.Pictures.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, ArrowArt), row.X + row.Width - 24f, row.Y + 1f,
            Opacity: row.Enabled ? 1f : DisabledArrow));

        // The Environment and Type boxes carry their titles, and a gun or a rocket box its own.
        switch (row.Key)
        {
            case EnvironmentKey:
                layers.Lines.Add(Line(10096, "Mission Environment", row.X, row.Y - 20f, 0f, Black));
                break;
            case TypeKey:
                layers.Lines.Add(Line(10097, "Mission Type", row.X, row.Y - 20f, 0f, Black));
                break;
            case var gun when gun.StartsWith(GunKeyPrefix, StringComparison.Ordinal):
                layers.Lines.Add(Line(1008, string.Empty, row.X, row.Y - 23f, 0f, Black, text: GunTitle(Index(gun, GunKeyPrefix))));
                break;
            case var cell when cell.StartsWith(CellKeyPrefix, StringComparison.Ordinal):
                string place = (Index(cell, CellKeyPrefix) + 1).ToString(CultureInfo.InvariantCulture) + ")";
                layers.Lines.Add(Line(1008, string.Empty, row.X - 15f, row.Y + 3f, 0f, Black, text: place));
                break;
        }
    }

    private string GunTitle(int slot)
    {
        var gun = Lobby is { } lobby ? GunFor(StockDef(lobby.Airframe), slot + 1) : null;
        if (gun == null)
        {
            return Word(10548, "No Gun");
        }

        int idx = Math.Clamp((gun.Caliber - 30) / 10, 0, 4);
        return Word(3320 + idx, $".{30 + (idx * 10)}-cal.");
    }

    private void ComposeBox(OriginalRow row, bool focused, BoardLayers layers)
    {
        var face = Regular(row.Key == ChatKey ? 10575 : 10105);
        float size = face?.Pixels ?? TextFallback;
        if (row.Key == ChatKey)
        {
            layers.Lines.Add(Line(10060, "Chat:", row.X - 42f, row.Y, 0f, Black));
        }
        else
        {
            var fill = row.Enabled ? (R: (byte)209, G: (byte)180, B: (byte)120) : (R: (byte)181, G: (byte)174, B: (byte)156);
            layers.Fills.Add(new BoardFill(row.X, row.Y, row.Width, row.Height, fill.R, fill.G, fill.B));
            layers.Fills.Add(new BoardFill(row.X, row.Y, row.Width, row.Height, 0, 0, 0, Border: true));
        }

        var caret = focused && row.Enabled && !_host.DialogOpen ? new BoardCaret(0, 0, 0, 1f, row.Height - 4f) : (BoardCaret?)null;
        layers.Lines.Add(new BoardLine(row.Label, row.X + 4f, row.Y + ((row.Height - size) / 2f) - 1f, row.Width - 8f, size,
            BoardInk.Row, -1, Caret: caret, Face: face, Colour: Black));
    }

    private void ComposeCheck(OriginalRow row, bool focused, bool pressed, BoardLayers layers)
    {
        var lobby = Lobby;
        var options = lobby?.Options;
        bool on = row.Key switch
        {
            TimeRadioKey => options?.Victory == DogfightVictory.Time,
            ScoreRadioKey => options?.Victory == DogfightVictory.Score,
            LimitedLivesKey => options?.LimitedLives == true,
            AutoRespawnKey => options?.AutoRespawn == true,
            ReadyKey => lobby?.Ready == true,
            _ => false,
        };

        layers.Pictures.Add(new BoardPicture(row.Art!, row.X, row.Y, (on ? 4 : 0) + StateFrame(row, focused, pressed)));
        var (id, word) = row.Key switch
        {
            TimeRadioKey => (10105, "Time (mins):"),
            ScoreRadioKey => (10107, "Score"),
            TeamsKey => (10108, "Restrict Number of Teams"),
            LimitedLivesKey => (10110, "Limited Lives"),
            AutoRespawnKey => (10111, "Auto Respawn"),
            CustomPlanesKey => (10112, "Allow Custom Planes"),
            OutlawKey => (10113, "Outlaw Components"),
            _ => (0, string.Empty),
        };

        if (row.Key == ReadyKey)
        {
            bool host = lobby?.IsHost == true;
            layers.Lines.Add(Line(host ? 10063 : 10059, "Ready?", row.X - 100f, row.Y - 20f, 160f, ReadyLabel, BoardJustify.Right));
        }
        else if (id != 0)
        {
            float lift = row.Key == AutoRespawnKey ? 3f : 2f;
            layers.Lines.Add(Line(id, word, row.X + (row.Key is TimeRadioKey or ScoreRadioKey ? 25f : 20f), row.Y - lift, 0f,
                row.Enabled || row.Key is TimeRadioKey or ScoreRadioKey or LimitedLivesKey or AutoRespawnKey ? Black : TabDisabled));
        }
    }

    private void ComposePlaque(OriginalRow row, bool focused, bool pressed, BoardLayers layers)
    {
        bool subTab = row.Key is DefaultPlanesKey or CustomTabKey or GunsTabKey or RocketsTabKey;
        bool picked = row.Key == DefaultPlanesKey || (row.Key == GunsTabKey && !Rockets) || (row.Key == RocketsTabKey && Rockets);
        int frame = !row.Enabled ? 0 : subTab && picked ? 3 : ComposedBoard.PlaqueFrame(row.Art!.Frames, focused, pressed);
        layers.Pictures.Add(new BoardPicture(row.Art!, row.X, row.Y, frame));
        var (id, word) = row.Key switch
        {
            LaunchKey => (10102, "LAUNCH!"),
            SelectKey => Lobby?.IsHost == false ? (10508, "View...") : (10103, "Select..."),
            DefaultPlanesKey => (10115, "Default Planes"),
            CustomTabKey => (10116, "Custom Planes"),
            GunsTabKey => (10120, "Guns"),
            RocketsTabKey => (10121, "Rockets"),
            BootKey => (10054, "Boot"),
            TeamKey => (10056, "Create Team"),
            SendKey => (10061, "Send"),
            LeaveKey => (10062, "Leave Game"),
            _ => (0, string.Empty),
        };

        if (id == 0)
        {
            return;
        }

        BoardTint tint;
        if (subTab)
        {
            tint = !row.Enabled ? LabelDisabled : picked ? Picked : Black;
        }
        else if (row.Key == LaunchKey && row.Enabled && !focused && !pressed)
        {
            tint = _blink == 0 ? LaunchBlink : LabelNormal;
        }
        else
        {
            tint = LabelTint(row.Enabled, focused, pressed);
        }

        var face = Regular(id);
        float size = face?.Pixels ?? TextFallback;
        layers.Lines.Add(new BoardLine(Word(id, word), row.X, row.Y + ((row.Height - size) / 2f) - 1f, row.Width, size,
            BoardInk.Row, -1, Justify: BoardJustify.Center, Face: face, Colour: tint));
    }

    // The words and pictures a tab page carries beside its widgets.
    private void ComposePage(BoardLayers layers)
    {
        if (Lobby is not { } lobby)
        {
            return;
        }

        switch (Tab)
        {
            case LobbyTab.Mission:
                ComposeMission(lobby, layers);
                break;
            case LobbyTab.Plane:
                ComposePlane(lobby, layers);
                break;
            case LobbyTab.Ammo:
                layers.Lines.Add(Line(10549, "Select Ammo", PageX + 11f, PageY + 43f, 0f, Black));
                layers.Pictures.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, IconArt, IconFrames), PageX + 251f, PageY + 102f,
                    lobby.Airframe));
                layers.Lines.Add(Line(10550, "No Information Available", PageX + 251f, PageY + 176f, 192f, Black,
                    BoardJustify.Center, faceId: 10144));
                break;
            case LobbyTab.Scores:
                ComposeScores(lobby, layers);
                break;
        }
    }

    private void ComposeMission(DogfightLobby lobby, BoardLayers layers)
    {
        var options = lobby.Options;
        int type = Math.Clamp((int)options.MissionType, 0, TypeDescriptions.Length - 1);
        layers.Lines.Add(Line(TypeDescriptions[type], string.Empty, PageX + 25f, PageY + 134f, 200f, Black));
        layers.Lines.Add(Line(10098, "Victory Conditions", PageX + 241f, PageY + 46f, 0f, Black));
        layers.Lines.Add(Line(10099, "Teams", PageX + 241f, PageY + 110f, 0f, Black));
        layers.Lines.Add(Line(10100, "Lives", PageX + 241f, PageY + 170f, 0f, Black));
        layers.Lines.Add(Line(10101, "Planes", PageX + 241f, PageY + 227f, 0f, Black));

        // The team count spinners, greyed with the checkbox they belong to.
        foreach (float x in new[] { PageX + 300f, PageX + 387f })
        {
            layers.Fills.Add(new BoardFill(x, PageY + 150f, 42f, 22f, 181, 174, 156));
            layers.Fills.Add(new BoardFill(x, PageY + 150f, 42f, 22f, 0, 0, 0, Border: true));
            layers.Lines.Add(Line(10105, string.Empty, x, PageY + 154f, 30f, TabDisabled, BoardJustify.Center, "2"));
        }

        layers.Lines.Add(Line(10109, "to", PageX + 360f, PageY + 154f, 0f, Black));
    }

    private void ComposePlane(DogfightLobby lobby, BoardLayers layers)
    {
        layers.Lines.Add(Line(10114, "Select Plane", PageX + 11f, PageY + 43f, 0f, Black));
        int airframe = lobby.Airframe;
        string plane = Word(10566, "Plane:") + " " + Word(10565, "Stock");
        layers.Lines.Add(Line(10566, "Plane:", PageX + 225f, PageY + 75f, 0f, Black, text: plane));
        layers.Lines.Add(Line(3000 + airframe, ShortNames[airframe], PageX + 225f, PageY + 90f, 220f, Black, faceId: 10566));
        layers.Pictures.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, IconArt, IconFrames), PageX + 251f, PageY + 102f, airframe));
        var fit = PlaneFit.For(airframe, null, StockDef(airframe));
        var ratings = PlaneRatings.For(fit);
        for (int i = 0; i < RatingLabels.Length; i++)
        {
            int at = Math.Clamp(ratings[i], 0, RatingWords.Length - 1);
            string text = $"{RatingLabels[i]}  {Word(501 + at, RatingWords[at])}";
            layers.Lines.Add(Line(1008, string.Empty, PageX + 225f, PageY + 170f + (15f * i), 0f, Black, text: text));
        }

        float y = PageY + 230f;
        foreach (int calibre in Calibres)
        {
            if (fit.Barrels.TryGetValue(calibre, out int barrels))
            {
                int idx = Math.Clamp((calibre - 30) / 10, 0, 4);
                string text = $"({barrels}) {Word(3320 + idx, $".{calibre}-cal.")}";
                layers.Lines.Add(Line(1008, string.Empty, PageX + 225f, y, 0f, Black, text: text));
                y += 15f;
            }
        }

        if (fit.Hardpoints > 0)
        {
            layers.Lines.Add(Line(1008, "Hardpoints", PageX + 225f, y, 0f, Black,
                text: $"({fit.Hardpoints}) {Word(1008, "Hardpoints")}"));
        }
    }

    // The scores page: the headers over the last match's lines, best first. Hits % stays blank,
    // since no end counts a pilot's hits.
    private void ComposeScores(DogfightLobby lobby, BoardLayers layers)
    {
        for (int i = 0; i < ScoreHeaderIds.Length; i++)
        {
            layers.Lines.Add(Line(ScoreHeaderIds[i], ScoreHeaders[i], PageX + ScoreHeaderAt[i].X, PageY + ScoreHeaderAt[i].Y, 0f, Black));
        }

        var scores = lobby.Scores;
        for (int i = 0; i < scores.Count && i < VisiblePlayers; i++)
        {
            float y = PageY + 69f + (ListPitch * i);
            var line = scores[i];
            layers.Lines.Add(Line(10575, string.Empty, PageX + 24f, y, 150f, Black, text: line.Name));
            int[] numbers = { line.Points, line.Kills, line.Deaths };
            for (int column = 0; column < numbers.Length; column++)
            {
                layers.Lines.Add(Line(10575, string.Empty, PageX + ScoreHeaderAt[column + 1].X, y, 0f, Black,
                    text: numbers[column].ToString(CultureInfo.InvariantCulture)));
            }
        }
    }

    // The open list over the finished page: its items on the box's own fill, the picked one marked.
    private void ComposeOpenList(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        var fills = new List<BoardFill>();
        var lines = new List<BoardLine>();
        int picked = _open != null && DropdownFor(_open) is { } open ? open.Current : -1;
        var face = Regular(_open is EnvironmentKey or TypeKey ? 10558 : 10144);
        float size = face?.Pixels ?? TextFallback;
        float top = float.MaxValue, bottom = float.MinValue, left = 0f, width = 0f;
        foreach (var row in rows)
        {
            if (row.Visible && row.Kind == OriginalRowKind.ListRow)
            {
                top = Math.Min(top, row.Y);
                bottom = Math.Max(bottom, row.Y + row.Height);
                left = row.X;
                width = row.Width;
            }
        }

        if (top < bottom)
        {
            fills.Add(new BoardFill(left, top, width, bottom - top, 222, 207, 156));
            fills.Add(new BoardFill(left, top, width, bottom - top, 0, 0, 0, Border: true));
        }

        for (int i = 0; i < rows.Count; i++)
        {
            var item = rows[i];
            if (!item.Visible || item.Kind != OriginalRowKind.ListRow)
            {
                continue;
            }

            if (OriginalDropLists.IndexOf(item.Key) == picked)
            {
                fills.Add(new BoardFill(item.X, item.Y, item.Width, item.Height, 180, 147, 78));
            }
            else if (i == focus)
            {
                fills.Add(new BoardFill(item.X, item.Y, item.Width, item.Height, 209, 180, 120));
            }

            lines.Add(new BoardLine(item.Label, item.X + 8f, item.Y + ((item.Height - size) / 2f) - 1f, item.Width - 12f, size,
                BoardInk.Row, i, Face: face, Colour: item.Enabled ? Black : TabDisabled));
        }

        layers.Overlays.Add(new BoardPanel(fills, Array.Empty<BoardPicture>(), lines));
    }

    private sealed record DropdownList(IReadOnlyList<string> Items, int Current, Func<int, bool> Allowed, Action<int> Select);
}
