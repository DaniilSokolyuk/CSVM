using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CSVM.Mech3;
using CSVM.Net;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;

namespace CSVM.UI.Menu.Original;

/// <summary>
/// The original's Multiplayer Connection screen and the LAN games list behind its Connect, a
/// standalone module over <see cref="NetPlayFeature"/>. The multiplayer scripts place their
/// widgets inline, so every corner here is the scripts' own, not the layout's
/// (<c>docs/org/menu-inventory.md</c>). Only LAN TCP/IP, which searches, and Internet, which joins
/// the typed address, are offered; the original's other three ways are left off the page. Build
/// Custom Plane draws greyed, and Host and Create Game open the Multiplayer Lobby as a Dogfight's
/// host. A join started here is followed on a messagebox over the page until it ends.
/// </summary>
public sealed class OriginalConnectionScreen : IOriginalScreenModule
{
    /// <summary>The LAN TCP/IP radio, whose Connect searches the LAN.</summary>
    public const string LanKey = "MP_R_LAN";

    /// <summary>The Internet radio, whose Connect joins the typed address.</summary>
    public const string InternetKey = "MP_R_INTERNET";

    /// <summary>The IP Address edit box.</summary>
    public const string AddressKey = "MP_E_IP";

    /// <summary>Build Custom Plane, greyed while guests fly stock planes.</summary>
    public const string BuildKey = "MP_B_BUILD";

    /// <summary>Host, which opens the Multiplayer Lobby as a Dogfight's host.</summary>
    public const string HostKey = "MP_B_HOST";

    /// <summary>Connect, by the way the radios name.</summary>
    public const string ConnectKey = "MP_B_CONNECT";

    /// <summary>Exit Multiplayer, back to the main menu.</summary>
    public const string ExitKey = "MP_B_EXIT";

    /// <summary>The games list's auto refresh checkbox.</summary>
    public const string RefreshKey = "MPG_C_REFRESH";

    /// <summary>Create Game, the games list's Host.</summary>
    public const string CreateKey = "MPG_B_CREATE";

    /// <summary>Join Game, live once a joinable row is picked.</summary>
    public const string JoinKey = "MPG_B_JOIN";

    /// <summary>The games list's Exit, back to the Connection page.</summary>
    public const string GamesExitKey = "MPG_B_EXIT";

    /// <summary>The Searching box's Cancel, back to the Connection page.</summary>
    public const string CancelKey = "MPG_B_CANCEL";

    /// <summary>How many games the list shows. It does not scroll.</summary>
    public const int VisibleGames = 12;

    /// <summary>The column the list opens sorted by, # of Players.</summary>
    public const int DefaultSort = 1;

    /// <summary>How often an empty list asks again, in seconds.</summary>
    public const double EmptyRefreshSeconds = 1.0;

    /// <summary>How often a filled list asks again while auto refresh is checked, in seconds.</summary>
    public const double AutoRefreshSeconds = 5.0;

    /// <summary>The longest mission name the Mission Environment column holds before it takes the
    /// mission's shortcode instead, in characters.</summary>
    public const int EnvironmentFit = 24;

    private const string SortKeyPrefix = "MPG_R_SORT_";
    private const string GameKeyPrefix = "MPG_L_GAME_";

    private const string OptionsBackground = "MP_OPTIONSBACKGROUND.JPG";
    private const string GamesBackground = "MP_GAMESBACKGROUND.JPG";
    private const string SearchingBackground = "MP_ERRORMESSAGEBACKGROUND.JPG";
    private const string RadioArt = "MP_B_RADIO.PNG";
    private const string SmallArt = "MP_B_SMALL.PNG";
    private const string MediumArt = "MP_B_MEDIUM.PNG";
    private const string LargeArt = "MP_B_LARGE.PNG";
    private const string CheckboxArt = "MP_B_CHECKBOXLARGE.PNG";
    private const string ExitArt = "MP_B_EXITMULTIPLAYER.PNG";
    private const string BuildArt = "GN_B_BUILDCUSTOMPLANE.PNG";
    private const string SortHighlightArt = "MP_GAMESALPHA.PNG";

    // A radio's frames are greyed, normal, rollover and selected; its label and description stand
    // to its right and under it.
    private const float RadioX = 50f;
    private const float RadioSize = 18f;
    private const float RadioLabelOffset = 33f;
    private const float RadioHitWidth = 250f;
    private const float DescriptionX = 83f;
    private const float FieldX = 184f;
    private const float FieldWidth = 150f;
    private const float FieldHeight = 18f;
    private const float AddressDrop = 17f;
    private const float FieldLabelOffsetX = 101f;
    private const float FieldLabelOffsetY = 8f;
    private const float PanelX = 59f;
    private const float PanelY = 380f;
    private const float PanelWidth = 329f;
    private const float TextFallback = 12f;

    // The games list: the header bars, the rows under them and the plaque line along the foot.
    private const float HeaderY = 141f;
    private const float HeaderHeight = 30f;
    private const float ListX = 53f;
    private const float ListY = 175f;
    private const float ListWidth = 690f;
    private const float ListPitch = 25f;
    private const float ButtonLineY = 507f;
    private const float SearchingX = 254f;
    private const float SearchingY = 160f;
    private const float SortHighlightAlpha = 80f / 255f;
    private const float PickedAlpha = 0x80 / 255f;

    // The two ways stand at the script's first two radio places and keep its pitch. They also keep
    // the box's drop under the Internet radio and the ways' own string ids.
    private static readonly string[] WayKeys = { LanKey, InternetKey };
    private static readonly string[] WayNames = { "LAN TCP/IP", "Internet" };
    private static readonly int[] WayLabelIds = { 10003, 10004 };
    private static readonly int[] WayDescriptionIds = { 10010, 10011 };
    private static readonly float[] WayY = { 98f, 134f };
    private static readonly float[] DescriptionY = { 117f, 177f };
    private static readonly float[] ColumnWidths = { 145f, 100f, 166f, 145f, 115f };
    private static readonly float[] HighlightOffsets = { 0f, 147f, 249f, 414f, 557f };
    private static readonly string[] Headers = { "Game Name", "# of Players", "Mission Type", "Mission Environment", "Status" };

    // The scripts' label colours: greyed, normal, rollover and pressed on the plaques and radios,
    // and the panel's cream.
    private static readonly BoardTint LabelDisabled = new(142, 142, 142);
    private static readonly BoardTint LabelNormal = new(226, 224, 206);
    private static readonly BoardTint LabelRollover = new(198, 188, 140);
    private static readonly BoardTint LabelPressed = new(255, 204, 102);
    private static readonly BoardTint Cream = new(204, 200, 179);
    private static readonly BoardTint Black = new(0, 0, 0);
    private static readonly BoardTint Ink = new(8, 8, 8);
    private static readonly BoardTint White = new(255, 255, 255);
    private static readonly BoardTint Unjoinable = new(0x80, 0x80, 0x80);
    private static readonly BoardTint SearchingRed = new(255, 0, 0);
    private static readonly BoardTint CreateDisabled = new(165, 165, 164);
    private static readonly BoardTint SortTint = new(242, 208, 139);

    private readonly Func<NetPlayFeature?> _net;
    private readonly IOriginalScreenHost _host;
    private readonly string? _dataRoot;
    private readonly Action _openLobby;
    private UiStrings? _strings;
    private (string Address, int Port)? _picked;
    private double _sinceAsk;
    private int _heard;

    // A join this page started, followed until it ends, and the words of the box standing for it.
    private bool _following;
    private string? _shown;

    /// <summary>A Connection module over the door <paramref name="net"/> answers, which is null on
    /// a shell with no multiplayer door. It reads its words from the string table under
    /// <paramref name="dataRoot"/> and calls back into <paramref name="host"/>. Host and Create Game
    /// call <paramref name="openLobby"/>.</summary>
    public OriginalConnectionScreen(Func<NetPlayFeature?> net, IOriginalScreenHost host, string? dataRoot, Action? openLobby = null)
    {
        _openLobby = openLobby ?? (() => { });
        _net = net ?? throw new ArgumentNullException(nameof(net));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _dataRoot = dataRoot;
    }

    /// <summary>The picked way, one of the radio keys.</summary>
    public string Way { get; private set; } = LanKey;

    /// <summary>The column the list is sorted by, 0 to 4.</summary>
    public int Sort { get; private set; } = DefaultSort;

    /// <summary>Whether auto refresh is checked.</summary>
    public bool AutoRefresh { get; private set; }

    /// <summary>The games the list shows, sorted and cut to <see cref="VisibleGames"/>.</summary>
    public IReadOnlyList<LanGame> Listed => Sorted();

    /// <summary>Whether seat 0's typed characters feed the IP Address box: the page is showing,
    /// no box stands over it and the box has the focus.</summary>
    internal bool CapturingText =>
        _host.Screen == OriginalScreen.Connection && !_host.DialogOpen && _host.FocusedKey == AddressKey;

    private UiStrings Strings => _strings ??= (_dataRoot is { } root ? UiStrings.TryLoad(root) : null) ?? UiStrings.Empty;

    /// <summary>A game row's key by its place in <see cref="Listed"/>.</summary>
    public static string GameKey(int index) => GameKeyPrefix + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>A sort radio's key by its column.</summary>
    public static string SortKey(int column) => SortKeyPrefix + column.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether a screen is this module's.</summary>
    public bool Owns(OriginalScreen screen) => screen is OriginalScreen.Connection or OriginalScreen.ConnectionGames;

    /// <summary>The Multiplayer plaque's door: the Connection page on the way it was left on.</summary>
    public void OpenConnection()
    {
        _host.CloseDialog();
        _host.Open(OriginalScreen.Connection);
    }

    /// <summary>LAN TCP/IP's Connect from the Connection page: the games list on a fresh search, or
    /// the no-network box when the search will not open.</summary>
    public void SearchLan()
    {
        Way = LanKey;
        Connect();
    }

    /// <summary>A game's five cells as the list writes them. A game of another version names that
    /// version in its Status cell.</summary>
    public IReadOnlyList<string> Cells(LanGame game)
    {
        var advert = game.Advert;
        return new[]
        {
            CoopDoorText.GameName(advert),
            CoopDoorText.PlayerCount(advert),
            CoopDoorText.MissionType(advert),
            CoopDoorText.Environment(advert, MissionName, name => name.Length <= EnvironmentFit),
            CoopDoorText.Status(game, _net()?.Version ?? NetBuildVersion.Unknown),
        };
    }

    /// <summary>The showing page's rows. The Connection page is its radios, its two boxes and its
    /// plaques. The games list is only the Searching box's Cancel while it has heard nothing.
    /// </summary>
    public void BuildRows(List<OriginalRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (_host.Screen == OriginalScreen.Connection)
        {
            ConnectionRows(rows);
        }
        else if (Listed.Count == 0)
        {
            var (x, y) = CancelCorner();
            rows.Add(Strip(CancelKey, MediumArt, x, y, true, 0, 96f, 37f));
        }
        else
        {
            GamesRows(rows);
        }
    }

    /// <summary>Neither page scrolls.</summary>
    public void Lists(List<OriginalList> lists)
    {
    }

    /// <summary>A sideways step crosses columns on both pages.</summary>
    public bool StepSideways(IReadOnlyList<OriginalRow> rows, int focus, int direction) => false;

    /// <summary>Neither page opens a list.</summary>
    public bool CloseDropdown() => false;

    /// <summary>The showing page's answer to an activated row.</summary>
    public MenuExit? Activate(OriginalRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        switch (row.Key)
        {
            case LanKey:
            case InternetKey:
                Way = row.Key;
                break;
            case AddressKey:
                // Enter in the box is the box's own Connect over the Internet.
                Way = InternetKey;
                Connect();
                break;
            case ConnectKey:
                Connect();
                break;
            case ExitKey:
                Leave();
                break;
            case HostKey:
            case CreateKey:
                _openLobby();
                break;
            case RefreshKey:
                AutoRefresh = !AutoRefresh;
                break;
            case JoinKey:
                JoinPicked();
                break;
            case GamesExitKey:
            case CancelKey:
                BackToConnection();
                break;
            case var sort when sort.StartsWith(SortKeyPrefix, StringComparison.Ordinal):
                Sort = IndexOf(sort, SortKeyPrefix);
                break;
            case var game when game.StartsWith(GameKeyPrefix, StringComparison.Ordinal):
                PickOrJoin(IndexOf(game, GameKeyPrefix));
                break;
        }

        return null;
    }

    /// <summary>Back leaves the games list for the Connection page, and the Connection page for the
    /// main menu, the way each page's own exit does.</summary>
    public bool Back()
    {
        if (_host.Screen == OriginalScreen.ConnectionGames)
        {
            BackToConnection();
        }
        else
        {
            Leave();
        }

        return true;
    }

    /// <summary>The showing page as drawn.</summary>
    public void Compose(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(layers);
        string focused = focus >= 0 && focus < rows.Count ? rows[focus].Key : string.Empty;
        int pressedAt = _host.DialogOpen ? -1 : _host.PressedRow;
        string pressed = pressedAt >= 0 && pressedAt < rows.Count ? rows[pressedAt].Key : string.Empty;
        if (_host.Screen == OriginalScreen.Connection)
        {
            ComposeConnection(rows, focused, pressed, layers);
        }
        else
        {
            ComposeGames(focused, pressed, layers);
        }

        if (_host.SeatPanel(false) is { } strip)
        {
            layers.Overlays.Add(strip);
        }
    }

    /// <summary>One menu frame's upkeep while a page shows, after the door was stepped. The list
    /// asks again on its timer, and a join started here is followed on its box. Returns whether
    /// the picture changed.</summary>
    public bool Tick(double dt)
    {
        if (_net() is not { } net || !Owns(_host.Screen))
        {
            return false;
        }

        bool changed = false;
        if (_host.Screen == OriginalScreen.ConnectionGames && net.Searching)
        {
            _sinceAsk += dt;
            double every = net.Games.Count == 0 ? EmptyRefreshSeconds : AutoRefresh ? AutoRefreshSeconds : double.PositiveInfinity;
            if (_sinceAsk >= every)
            {
                _sinceAsk = 0.0;
                net.Search();
            }

            int heard = Hash(net.Games);
            if (heard != _heard)
            {
                // The first answers replace the Searching box, and the cursor lands on the first game.
                bool first = _heard == 0 && net.Games.Count > 0;
                _heard = heard;
                changed = true;
                if (first)
                {
                    _host.FocusKey(GameKey(0));
                }
            }
        }

        return FollowJoin(net) || changed;
    }

    /// <summary>Typed characters and Backspace into the IP Address box, which picks the Internet
    /// way as the original's box does when typed into.</summary>
    internal bool TypeAddress(MenuCommands commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (!CapturingText || _net() is not { } net || (commands.Typed.Length == 0 && !commands.Erase))
        {
            return false;
        }

        string before = net.Address;
        net.TypeAddress(commands.Typed);
        if (commands.Erase)
        {
            net.EraseAddress();
        }

        if (net.Address != before)
        {
            Way = InternetKey;
            return true;
        }

        return false;
    }

    /// <summary>Lets go of the join this page followed, its guest having gone on to its host's
    /// boards. The box standing for it goes with the screen change.</summary>
    internal void StopFollowing()
    {
        _following = false;
        _shown = null;
    }

    /// <summary>Follows the door's join again, for a guest coming back from its host's boards. An
    /// ended link then raises the box saying why, as a join that failed here does.</summary>
    internal void FollowAgain()
    {
        _following = true;
        _shown = null;
    }

    private static int IndexOf(string key, string prefix) =>
        int.TryParse(key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int index) ? index : -1;

    private static int Hash(IReadOnlyList<LanGame> games)
    {
        var hash = default(HashCode);
        hash.Add(games.Count);
        foreach (var game in games)
        {
            hash.Add(game);
        }

        return games.Count == 0 ? 0 : hash.ToHashCode() | 1;
    }

    private static BoardTint LabelTint(bool enabled, bool focused, bool pressed) =>
        !enabled ? LabelDisabled : pressed ? LabelPressed : focused ? LabelRollover : LabelNormal;

    private static (float X, float Y) CancelCorner() => (SearchingX + 150f, SearchingY + 150f);

    private static float SortX(int column) => column < 2 ? 145f + (133f * column) : 148f + (133f * column);

    private static float SortY(int column) => column < 2 ? 116f : 118f;

    private static float ColumnX(int column)
    {
        float x = ListX;
        for (int i = 0; i < column; i++)
        {
            x += ColumnWidths[i];
        }

        return x;
    }

    private static int CompareBy(int column, IReadOnlyList<string> a, IReadOnlyList<string> b, LanGame left, LanGame right) =>
        column == 1
            ? right.Advert.Players.CompareTo(left.Advert.Players)
            : string.Compare(a[column], b[column], StringComparison.OrdinalIgnoreCase);

    private string MissionName(int seq) =>
        Strings.Text(3450 + seq, $"Mission {(seq + 1).ToString(CultureInfo.InvariantCulture)}");

    private string Word(int id, string fallback)
    {
        string text = Strings.Text(id, fallback).Trim();
        return text.Length > 0 ? text : fallback;
    }

    // A string's face in regular weight: the original's capture draws every multiplayer face that
    // way, its B tags included.
    private LanguiFace? Regular(int id) => LanguiFace.Parse(Strings.Face(id)) is { } face ? face with { Bold = false } : null;

    // One string of the table in the face its row names, in an authored colour.
    private BoardLine Line(
        int id, string fallback, float x, float y, float width, BoardTint colour, BoardJustify justify = BoardJustify.Left,
        string? text = null)
    {
        var face = Regular(id);
        return new BoardLine(
            text ?? Word(id, fallback), x, y, width, face?.Pixels ?? TextFallback, BoardInk.Row, -1,
            Justify: justify, Face: face, Colour: colour);
    }

    private OriginalRow Strip(
        string key, string art, float x, float y, bool enabled, int column, float fallbackWidth, float fallbackHeight, int frames = 4)
    {
        var strip = new BoardArt(BoardArtLibrary.Ui, art, frames);
        var size = OriginalWidgets.StripSize(strip, _host.Measure, fallbackWidth, fallbackHeight);
        return new OriginalRow(key, string.Empty, OriginalRowKind.Button, x, y, size.Width, size.Height, enabled, column, strip);
    }

    private OriginalRow Radio(string key, float x, float y, bool enabled, float hitWidth) =>
        new(key, string.Empty, OriginalRowKind.Radio, x, y, hitWidth, RadioSize, enabled, 0, new BoardArt(BoardArtLibrary.Ui, RadioArt, 4));

    private void ConnectionRows(List<OriginalRow> rows)
    {
        for (int i = 0; i < WayKeys.Length; i++)
        {
            rows.Add(Radio(WayKeys[i], RadioX, WayY[i], true, RadioHitWidth));
            if (WayKeys[i] == InternetKey)
            {
                rows.Add(new OriginalRow(AddressKey, _net()?.Address ?? string.Empty, OriginalRowKind.TextField,
                    FieldX, WayY[i] + AddressDrop, FieldWidth, FieldHeight, true, 0, null));
            }
        }

        rows.Add(Strip(BuildKey, BuildArt, 117f, 468f, false, 0, 200f, 32f));
        rows.Add(Strip(HostKey, SmallArt, 514f, 424f, _net() != null, 1, 74f, 37f));
        rows.Add(Strip(ConnectKey, MediumArt, 610f, 424f, true, 1, 96f, 37f));
        rows.Add(Strip(ExitKey, ExitArt, 514f, 559f, true, 1, 200f, 32f));
    }

    private void GamesRows(List<OriginalRow> rows)
    {
        for (int column = 0; column < Headers.Length; column++)
        {
            rows.Add(Radio(SortKey(column), SortX(column), SortY(column), true, RadioSize));
        }

        var listed = Listed;
        for (int i = 0; i < listed.Count; i++)
        {
            rows.Add(new OriginalRow(GameKey(i), CoopDoorText.GameName(listed[i].Advert), OriginalRowKind.ListRow,
                ListX, ListY + (i * ListPitch), ListWidth, ListPitch, true, 0, null));
        }

        rows.Add(new OriginalRow(RefreshKey, string.Empty, OriginalRowKind.Radio, 47f, ButtonLineY, 200f, 37f, true, 0,
            new BoardArt(BoardArtLibrary.Ui, CheckboxArt, 8)));
        rows.Add(Strip(CreateKey, LargeArt, 394f, ButtonLineY, _net() != null, 1, 131f, 37f));
        rows.Add(Strip(JoinKey, LargeArt, 529f, ButtonLineY, PickedGame() is { } game && CoopDoorText.Joinable(game.Advert), 1, 131f, 37f));
        rows.Add(Strip(GamesExitKey, SmallArt, 665f, ButtonLineY, true, 1, 74f, 37f));
    }

    private List<LanGame> Sorted()
    {
        var games = new List<LanGame>(_net()?.Games ?? Array.Empty<LanGame>());
        var cells = games.ToDictionary(game => game, Cells);
        games.Sort((left, right) =>
        {
            int by = CompareBy(Math.Clamp(Sort, 0, Headers.Length - 1), cells[left], cells[right], left, right);
            return by != 0 ? by : string.CompareOrdinal($"{left.Address}:{left.Port}", $"{right.Address}:{right.Port}");
        });
        return games.Count > VisibleGames ? games.GetRange(0, VisibleGames) : games;
    }

    private LanGame? PickedGame()
    {
        if (_picked is not { } picked)
        {
            return null;
        }

        foreach (var game in Listed)
        {
            if (game.Address == picked.Address && game.Port == picked.Port)
            {
                return game;
            }
        }

        return null;
    }

    // LAN TCP/IP opens the games list on a fresh search; Internet joins the typed address.
    private void Connect()
    {
        if (_net() is not { } net)
        {
            return;
        }

        if (Way == LanKey)
        {
            net.StopSearch();
            net.Search();
            if (!net.CanSearch || !net.Searching)
            {
                _host.RaiseDialog(Word(10022, "No local area network connection is detected."), DialogIcon.Warning, Ok(null));
                return;
            }

            _picked = null;
            _sinceAsk = 0.0;
            _heard = 0;
            _host.CloseDialog();
            _host.Open(OriginalScreen.ConnectionGames);
            return;
        }

        if (string.IsNullOrWhiteSpace(net.Address))
        {
            _host.RaiseDialog(Word(10025, "The Internet IP address is not recognized."), DialogIcon.Warning, Ok(null));
            return;
        }

        if (net.Stage is NetDoorStage.Failed)
        {
            net.Close();
        }

        net.OpenJoin();
        _following = true;
    }

    private void PickOrJoin(int index)
    {
        var listed = Listed;
        if (index < 0 || index >= listed.Count)
        {
            return;
        }

        var game = listed[index];
        if (PickedGame() is { } picked && picked.Address == game.Address && picked.Port == game.Port)
        {
            JoinPicked();
            return;
        }

        _picked = (game.Address, game.Port);
    }

    private void JoinPicked()
    {
        if (_net() is not { } net || PickedGame() is not { } game || !CoopDoorText.Joinable(game.Advert))
        {
            return;
        }

        // Refused before any socket opens, and the list stays up behind the box.
        if (!net.PlaysWith(game))
        {
            _host.RaiseDialog(CoopDoorText.VersionMismatch(game.Version, net.Version), DialogIcon.Warning, Ok(null));
            return;
        }

        if (net.Stage is NetDoorStage.Failed)
        {
            net.Close();
        }

        net.JoinGame(game);
        _following = true;
    }

    private void BackToConnection()
    {
        _net()?.StopSearch();
        _host.CloseDialog();
        _host.Open(OriginalScreen.Connection);
    }

    private void Leave()
    {
        _net()?.StopSearch();
        _host.CloseDialog();
        _host.Open(OriginalScreen.TopLevel);
    }

    // The box a followed join stands on: connecting, waiting on the host, or why it ended. An
    // ended join puts the page back on Connection. Answering any of them hangs up.
    private bool FollowJoin(NetPlayFeature net)
    {
        if (!_following)
        {
            return false;
        }

        string text;
        OriginalDialogAnswer answer;
        var icon = DialogIcon.Query;
        switch (net.Stage)
        {
            case NetDoorStage.Joining:
                text = $"Connecting to {net.Address}:{net.Port.ToString(CultureInfo.InvariantCulture)} ...";
                answer = new(OriginalShell.DialogCancelKey, CampaignBoards.DialogCenterKey, Word(101, "Cancel"), () => EndJoin(net));
                break;
            case NetDoorStage.Joined:
                text = CoopDoorText.WaitingStatus(net, MissionName);
                answer = new(OriginalShell.DialogCancelKey, CampaignBoards.DialogCenterKey, "Leave", () => EndJoin(net));
                break;
            case NetDoorStage.Failed:
                text = net.Fault;
                answer = Ok(() => EndJoin(net));
                icon = DialogIcon.Warning;
                break;
            default:
                bool standing = _shown != null;
                _following = false;
                _shown = null;
                return standing;
        }

        if (text == _shown && _host.DialogOpen)
        {
            return false;
        }

        _host.CloseDialog();
        if (net.Stage == NetDoorStage.Failed)
        {
            net.StopSearch();
            _host.Open(OriginalScreen.Connection);
        }

        _host.RaiseDialog(text, icon, answer);
        _shown = text;
        return true;
    }

    private void EndJoin(NetPlayFeature net)
    {
        _following = false;
        _shown = null;
        net.Close();
    }

    private OriginalDialogAnswer Ok(Action? run) =>
        new(OriginalShell.DialogOkKey, CampaignBoards.DialogCenterKey, Word(100, "OK"), run);

    private void ComposeConnection(IReadOnlyList<OriginalRow> rows, string focused, string pressed, BoardLayers layers)
    {
        layers.Backdrop.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, OptionsBackground), 0f, 0f));
        layers.Lines.Add(Line(10014, "CONNECTION", 36f, 32f, 0f, Black));
        layers.Lines.Add(Line(10013, "MULTIPLAYER OPTIONS", 410f, 32f, 0f, Black));
        foreach (var row in rows)
        {
            bool isFocused = row.Key == focused;
            bool isPressed = row.Key == pressed;
            int way = Array.IndexOf(WayKeys, row.Key);
            if (way >= 0)
            {
                ComposeRadio(row, row.Key == Way, isFocused, layers);
                layers.Lines.Add(Line(WayLabelIds[way], WayNames[way], row.X + RadioLabelOffset, row.Y, 0f, Ink));
                layers.Lines.Add(Line(WayDescriptionIds[way], string.Empty, DescriptionX, DescriptionY[way], 0f, Ink));
            }
            else if (row.Kind == OriginalRowKind.TextField)
            {
                ComposeField(row, isFocused, layers);
            }
            else if (row.Art != null)
            {
                ComposePlaque(row, isFocused, isPressed, layers);
            }
        }

        layers.Lines.Add(Line(10524, "To build custom planes for your multiplayer inventory, click Build Custom Plane.",
            PanelX, PanelY, PanelWidth, Cream, BoardJustify.Center));
    }

    private void ComposeGames(string focused, string pressed, BoardLayers layers)
    {
        layers.Backdrop.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, GamesBackground), 0f, 0f));
        string way = Word(10003, "LAN TCP/IP");
        string title = Strings.Format(10067, way);
        layers.Lines.Add(Line(10067, string.Empty, 69f, 80f, 0f, White, text: title.Length > 0 ? title : $"{way} Games"));
        int sort = Math.Clamp(Sort, 0, Headers.Length - 1);
        layers.Pictures.Add(new BoardPicture(
            new BoardArt(BoardArtLibrary.Ui, SortHighlightArt), 54f + HighlightOffsets[sort], HeaderY,
            Opacity: SortHighlightAlpha, Tint: SortTint, Crop: new BoardCrop(HighlightOffsets[sort], 0f, ColumnWidths[sort], 341f)));

        // The full page is drawn whatever rows stand, since the Searching box covers it rather than
        // replacing it.
        var rows = new List<OriginalRow>();
        GamesRows(rows);
        for (int column = 0; column < Headers.Length; column++)
        {
            var row = rows[column];
            ComposeRadio(row, column == sort, row.Key == focused, layers);
            layers.Lines.Add(Line(10068, "Sort by:", row.X - 60f, row.Y, 0f, White));
            layers.Lines.Add(Line(10069 + column, Headers[column], ColumnX(column), HeaderY + 8f, ColumnWidths[column], Black, BoardJustify.Center));
        }

        var listed = Listed;
        var picked = PickedGame();
        for (int i = 0; i < listed.Count; i++)
        {
            var game = listed[i];
            float y = ListY + (i * ListPitch);
            string key = GameKey(i);
            if (picked is { } p && p.Address == game.Address && p.Port == game.Port)
            {
                layers.Fills.Add(new BoardFill(ListX, y, ListWidth, ListPitch, 0x9f, 0x28, 0x20, PickedAlpha));
            }

            if (key == focused || (_host.HoveredRow >= 0 && key == HoveredKey()))
            {
                layers.Fills.Add(new BoardFill(ListX, y, ListWidth, ListPitch, 0xdf, 0xb8, 0x60, 1f, Border: true));
            }

            var cells = Cells(game);
            var ink = CoopDoorText.Joinable(game.Advert) && _net()?.PlaysWith(game) != false ? White : Unjoinable;
            for (int column = 0; column < cells.Count; column++)
            {
                layers.Lines.Add(new BoardLine(cells[column], ColumnX(column), y + 5f, ColumnWidths[column], TextFallback,
                    BoardInk.Row, -1, Justify: BoardJustify.Center, Colour: ink));
            }
        }

        var refresh = rows.First(row => row.Key == RefreshKey);
        int box = !refresh.Enabled ? 0 : refresh.Key == pressed ? 3 : refresh.Key == focused ? 2 : 1;
        layers.Pictures.Add(new BoardPicture(refresh.Art!, refresh.X, refresh.Y, (AutoRefresh ? 4 : 0) + box));
        layers.Lines.Add(Line(10076, "Auto refresh", refresh.X + 62f, refresh.Y + 8f, 0f, new BoardTint(250, 250, 250)));
        string[] labels = { "Create Game", "Join Game", "Exit" };
        int[] ids = { 10074, 10075, 10077 };
        string[] keys = { CreateKey, JoinKey, GamesExitKey };
        for (int i = 0; i < keys.Length; i++)
        {
            var row = rows.First(r => r.Key == keys[i]);
            ComposePlaque(row, row.Key == focused, row.Key == pressed, layers, ids[i], labels[i],
                row.Key == CreateKey ? CreateDisabled : LabelDisabled);
        }

        if (listed.Count == 0)
        {
            ComposeSearching(focused == CancelKey, pressed == CancelKey, layers);
        }
    }

    // The Searching box, over the page: its picture, the red words and its Cancel.
    private void ComposeSearching(bool focused, bool pressed, BoardLayers layers)
    {
        var (x, y) = CancelCorner();
        var cancel = Strip(CancelKey, MediumArt, x, y, true, 0, 96f, 37f);
        var face = Regular(10578);
        float size = face?.Pixels ?? TextFallback;
        layers.Overlays.Add(new BoardPanel(
            Array.Empty<BoardFill>(),
            new[]
            {
                new BoardPicture(new BoardArt(BoardArtLibrary.Ui, SearchingBackground), SearchingX, SearchingY),
                new BoardPicture(cancel.Art!, cancel.X, cancel.Y, ComposedBoard.PlaqueFrame(4, focused, pressed)),
            },
            new[]
            {
                Line(10577, "Searching ...", SearchingX + 80f, SearchingY + 80f, 0f, SearchingRed),
                new BoardLine(Word(10578, "Cancel"), cancel.X, cancel.Y + ((cancel.Height - size) / 2f) - 1f, cancel.Width, size,
                    BoardInk.Row, -1, Justify: BoardJustify.Center, Face: face, Colour: LabelTint(true, focused, pressed)),
            }));
    }

    private void ComposeRadio(OriginalRow row, bool selected, bool focused, BoardLayers layers)
    {
        int frame = !row.Enabled ? 0 : selected ? 3 : focused ? 2 : 1;
        layers.Pictures.Add(new BoardPicture(row.Art!, row.X, row.Y, frame));
    }

    private void ComposeField(OriginalRow row, bool focused, BoardLayers layers)
    {
        // The live box is only its black outline over the page.
        layers.Fills.Add(new BoardFill(row.X, row.Y, row.Width, row.Height, 0, 0, 0, Border: true));
        layers.Lines.Add(Line(10006, "IP Address:", row.X - FieldLabelOffsetX, row.Y + FieldLabelOffsetY - 8f, 0f, Ink));
        var face = Regular(10006);
        float size = face?.Pixels ?? TextFallback;
        var caret = focused && !_host.DialogOpen ? new BoardCaret(0, 0, 0, 1f, row.Height - 4f) : (BoardCaret?)null;
        layers.Lines.Add(new BoardLine(row.Label, row.X + 3f, row.Y + ((row.Height - size) / 2f) - 1f, row.Width - 6f, size,
            BoardInk.Row, -1, Caret: caret, Face: face, Colour: Ink));
    }

    private void ComposePlaque(
        OriginalRow row, bool focused, bool pressed, BoardLayers layers, int labelId = 0, string label = "",
        BoardTint? disabled = null)
    {
        // A picture rather than a plaque, since the plaque layer stands over the text. These words
        // are drawn in the script's own colours rather than a plaque ink.
        int frame = row.Enabled ? ComposedBoard.PlaqueFrame(row.Art!.Frames, focused, pressed) : 0;
        layers.Pictures.Add(new BoardPicture(row.Art!, row.X, row.Y, frame));
        if (labelId == 0)
        {
            (labelId, label) = row.Key switch
            {
                HostKey => (10015, "Host"),
                ConnectKey => (10016, "Connect"),
                _ => (0, string.Empty),
            };
        }

        if (labelId == 0)
        {
            return;
        }

        var face = Regular(labelId);
        float size = face?.Pixels ?? TextFallback;
        var tint = !row.Enabled && disabled is { } grey ? grey : LabelTint(row.Enabled, focused, pressed);
        layers.Lines.Add(new BoardLine(Word(labelId, label), row.X, row.Y + ((row.Height - size) / 2f) - 1f, row.Width, size,
            BoardInk.Row, -1, Justify: BoardJustify.Center, Face: face, Colour: tint));
    }

    private string HoveredKey()
    {
        var rows = new List<OriginalRow>();
        BuildRows(rows);
        int hovered = _host.HoveredRow;
        return hovered >= 0 && hovered < rows.Count ? rows[hovered].Key : string.Empty;
    }
}
