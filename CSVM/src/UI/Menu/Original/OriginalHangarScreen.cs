using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;

namespace CSVM.UI.Menu.Original;

/// <summary>The colours the plane-construction screens write in, read off their own rows. The
/// right page has its authored text and title colours, the left page white, and the tab bar its
/// label tail. The standing tab draws in its disabled colour.</summary>
public sealed record OriginalHangarInks(
    MenuLayoutColor Text, MenuLayoutColor Title, MenuLayoutColor Page,
    MenuLayoutColor TabLabel, MenuLayoutColor TabCurrent, MenuLayoutColor TabDepressed);

/// <summary>
/// The Original hangar over the shared <see cref="HangarFeature"/>, with one of the six tab
/// sections composed on the hub's right page. The PLANE NAME screen, the Plane Construction hub,
/// the CONSTRUCTION TOTALS page and the INVENTORY come from <c>[@PlaneName@]</c>,
/// <c>[@PlaneConstruction@]</c>, <c>[@Purchase@]</c> and <c>[@Hangar@]</c>. The tab bar is the
/// layout's own seven <c>0x1100</c> edges, each tab reachable from the rest; the open list,
/// keyboard and pad focus are remake-only. READY TO PURCHASE opens the totals, CANCEL drops the
/// scratch plane and SELL PLANES opens the inventory. Each tab's dropdowns bind to the feature's
/// operations, and a pick that changes the airframe raises the defaults ask as a dialog. Entered
/// wallet-free from Instant Action's Build Custom Plane and over the wallet from the cabin's PLANE
/// CONSTRUCTION, either door naming the default-build airframe.
/// </summary>
public sealed class OriginalHangarScreen : IOriginalScreenModule
{
    /// <summary>The layout section the name screen is composed from.</summary>
    public const string PlaneNameSection = "PlaneName";

    /// <summary>The name screen's own pane, the dialog its rows are placed on.</summary>
    public const string PlaneNamePaneKey = "PN_P_BACKGROUND";

    /// <summary>The layout section the hub's chrome is composed from.</summary>
    public const string PlaneConstructionSection = "PlaneConstruction";

    /// <summary>The layout section the totals page is composed from.</summary>
    public const string PurchaseSection = "Purchase";

    /// <summary>The layout section the inventory is composed from.</summary>
    public const string InventorySection = "Hangar";

    /// <summary>The tab page's description box as a scrolled list, which the authored widget keys
    /// per tab and the pointer needs one name for.</summary>
    public const string DescriptionListKey = "PX_DESCRIPTION";

    /// <summary>The name screen's edit box.</summary>
    public const string NameFieldKey = "PN_E_NAME";

    /// <summary>The hub's own edit box, which renames the build in place.</summary>
    public const string HubNameFieldKey = "PX_E_NAME";

    /// <summary>The name screen's Load Default Configuration checkbox.</summary>
    public const string NameDefaultsKey = "PN_B_DEFAULT";

    /// <summary>The name screen's OK button, into the hub.</summary>
    public const string NameOkKey = "PN_B_OK";

    /// <summary>The name screen's Cancel button, dropping the build.</summary>
    public const string NameCancelKey = "PN_B_CANCEL";

    /// <summary>The hub's SELL PLANES button, into the inventory.</summary>
    public const string SellPlanesKey = "PX_B_Sell";

    /// <summary>The hub's READY TO PURCHASE button, into the totals page.</summary>
    public const string ReadyKey = "PX_B_Ready";

    /// <summary>The hub's CANCEL PURCHASE button, dropping the build.</summary>
    public const string CancelBuildKey = "PX_B_Cancel";

    /// <summary>The totals page's Purchase Now button, the commit.</summary>
    public const string PurchaseNowKey = "PUR_B_PURCHASE";

    /// <summary>The inventory's plane dropdown; its items are keyed <c>HA_D_PILOTPLANE:&lt;index&gt;</c>.</summary>
    public const string InventoryPlanesKey = "HA_D_PILOTPLANE";

    /// <summary>The inventory's Sell button.</summary>
    public const string InventorySellKey = "HA_B_SELLP";

    /// <summary>The inventory's Export button, the campaign's verb. Live: the screen's script
    /// never deactivates it, and the still shows it drawn like Sell beside it.</summary>
    public const string InventoryExportKey = "HA_B_EXPORTP";

    /// <summary>The inventory's Done button, back to the hub.</summary>
    public const string InventoryDoneKey = "HA_B_DONE";

    /// <summary>The row the inventory's plane line is drawn on, replacing the 108 <c>HA_T_PLANE</c>
    /// authors. Its dashed box covers rows 101 to 124 of <c>PS_BackGround.jpg</c>. A line's
    /// baseline falls one font size under its row, so the authored 108 dropped the 14-pixel line's
    /// baseline onto the box's bottom rule. Row 104 centres its cap band on the box instead.</summary>
    public const float InventoryPlaneRow = 104f;

    /// <summary>The first row of the dashed box's bottom rule in <c>PS_BackGround.jpg</c>, what the
    /// plane line's baseline has to stand clear of.</summary>
    public const float InventoryPlaneBoxBottom = 122f;

    /// <summary>The airframe tab's dropdown.</summary>
    public const string AirframeDropKey = "AF_D_AIRFRAME";

    /// <summary>The engine tab's dropdown.</summary>
    public const string EngineDropKey = "EN_D_ENGINE";

    /// <summary>The paint tab's pattern dropdown.</summary>
    public const string PatternDropKey = "PT_D_PATTERN";

    /// <summary>The paint tab's nose decal dropdown, the first of its three.</summary>
    public const string NoseDecalKey = "PT_D_DECALS0";

    // The plane picture's authored corner, the four PX_P_PLANE panes' own.
    private const float PlaneX = 16f;
    private const float PlaneY = 44f;

    // Where the hub's name box begins and ends, its authored row carrying neither. The script sets
    // both. The box starts at the end of the PLANE NAME title, R = px_t_planename's x plus its
    // drawn width. It runs from there to 302 (SNA.WB = 302 - R, PLANECONSTRUCTION.SCRIPT). The
    // right edge is therefore the script's own. The left stands in for a title measurement this
    // engine-free half cannot make.
    private const float HubNameX = 120f;
    private const float HubNameRight = 302f;

    // The caret an edit box draws after its text, in the box's own CursorColor. It is two authored
    // pixels wide and a pixel clear of the box top and bottom. That is how it stands in the
    // reference shot of the PLANE NAME dialog.
    private const float CaretWidth = 2f;
    private const float CaretInset = 1f;

    // Text sizes against the authored 15-pixel item height and the page's own text rows.
    private const float HubTitleFont = 16f;
    private const float HubTextFont = 12f;
    private const float HubItemFont = 12f;
    private const float HubLabelFont = 14f;
    private const float DescFont = 11f;
    private const float DescLine = 14f;

    // How far inside the description box's own rectangle its text sits, across and down.
    private const float DescInset = 6f;
    private const float DescTop = 4f;

    // A dropdown's fallback item height where the layout row is missing.
    private const float FallbackHubItemHeight = 15f;

    // Fallback strip sizes for widgets whose art the layout does not name. They match the shared
    // shell and Instant Action fallbacks a hangar strip could stand in for.
    private const float FallbackDropWidth = 160f;
    private const float FallbackArrowWidth = 15f;
    private const float FallbackArrowHeight = 14f;
    private const float FallbackButtonWidth = 220f;
    private const float FallbackButtonHeight = 42f;
    private const float FallbackThumbHeight = 21f;

    // The shell's own dialog answer keys (OriginalShell.DialogOkKey and its three siblings,
    // OriginalShellDialog.cs), restated here so this module holds no reference to OriginalShell.
    private const string DialogOkKey = "DIALOG:OK";
    private const string DialogYesKey = "DIALOG:YES";
    private const string DialogNoKey = "DIALOG:NO";
    private const string DialogCancelKey = "DIALOG:CANCEL";

    // Where the decal picker's grid stands on the paint page, as the panel's own top-left corner,
    // and the tile size the sheet falls back to. Measured off OriginalScreenshots/CustomPlane
    // Decal Select.png. The five-across window is wider than the box it hangs from, and one grid
    // serves all three decal boxes. So it is the page's own rectangle and not the box's.
    private const float DecalGridX = 406f;
    private const float DecalGridY = 374f;
    private const float DecalTile = 66f;

    // The grid panel's one-pixel frame, which its tiles and its scroll column stand inside.
    private const float DecalGridBorder = 1f;

    // The funds the wallet-free doors build against, the figure their own cash note shows.
    private const int ExportFunds = 50000;

    // Where a tab writes its label inside its strip, as pixels up from the strip's bottom edge.
    // PX_Tab.png's rest and rollover frames are opaque over the bottom twenty rows of a 36-pixel
    // frame alone. A label centred in the frame would stand off that squat plaque and against the
    // page above it. The stills' own baseline: docs/org/menu-inventory.md, Part 4.
    private const float TabLabelLift = 8f;

    // The inventory's own Export label (IDS_PS_B_EXPORT), the only export word the shipped table
    // carries for a button; the wallet-free commit borrows it.
    private const int ExportLabelString = 1139;

    // The weight line the set-airframe callback swaps in, IDS_PX_OVERALLSPEED_TITLE's own
    // "CURRENT WEIGHT: Pending". It carries no figure and is never red
    // (docs/org/hangar.md, "The two red figures").
    private const int PendingWeightString = 1032;

    // What the three tabs with no component of their own write on their name row, the shipped
    // IDS_PX_ARMORNAME set. The other three name the airframe, the engine or the gun instead.
    private const int ArmourNameString = 1150;
    private const int HardpointsNameString = 1151;
    private const int PaintNameString = 1152;

    // What the wallet-free inventory calls removing a plane, on the button, over the page and in
    // the confirm. Remake-only, as the hub's export strips are. Nothing is paid for a wallet-free
    // build, so the shipped Sell words all read wrong, and the table carries no delete word.
    private const string DeleteLabel = "Delete";
    private const string DeletePrompt = "Delete a Plane";
    private const string DeleteQuestion = "Are you sure you want to delete it?";

    private static readonly Regex FontTag = new(@"\[[A-Za-z0-9]+\]", RegexOptions.Compiled);

    private static readonly string[] RatingWords = { "Poor", "Fair", "Average", "Good", "Excellent" };

    private static readonly (OriginalScreen Screen, string Key, string Section)[] Tabs =
    {
        (OriginalScreen.HangarAirframe, "PX_B_AIRFRAME", "AirFrame"),
        (OriginalScreen.HangarEngine, "PX_B_ENGINE", "Engine"),
        (OriginalScreen.HangarArmor, "PX_B_ARMOR", "Armor"),
        (OriginalScreen.HangarGuns, "PX_B_GUNS", "Guns"),
        (OriginalScreen.HangarHardpoints, "PX_B_HARDPOINTS", "HardPoints"),
        (OriginalScreen.HangarPaint, "PX_B_PAINT", "Paint"),
    };

    private readonly MenuLayout _layout;
    private readonly Func<string, (int Width, int Height)?> _measure;
    private readonly IOriginalScreenHost _host;
    private readonly HangarFeature? _hangar;
    private readonly CustomPlaneStore? _planes;
    private OriginalScreen _hangarReturn = OriginalScreen.TopLevel;
    private OriginalScreen _hangarTab = OriginalScreen.HangarAirframe;
    private string _hangarName = string.Empty;
    private bool _hangarDefaults = true;
    private int _hangarDefaultAirframe = HangarFeature.DefaultAirframe;
    private string? _hangarOpen;
    private int _hangarListTop;
    private int _inventoryIndex;
    private string? _builtPlane;
    private int _descTop;
    private int _descLines;
    private int _descFits;
    private string _descBody = string.Empty;
    private ListWindow? _descWindow;

    /// <summary>A hangar module over <paramref name="hangar"/> and the store it builds into, reading
    /// <paramref name="layout"/> and calling back into <paramref name="host"/> for the state and the
    /// seams every screen family shares.</summary>
    public OriginalHangarScreen(
        HangarFeature hangar, CustomPlaneStore? planes, MenuLayout layout,
        Func<string, (int Width, int Height)?> measure, IOriginalScreenHost host)
    {
        _hangar = hangar ?? throw new ArgumentNullException(nameof(hangar));
        _planes = planes;
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _measure = measure ?? throw new ArgumentNullException(nameof(measure));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Inks = ReadInks(layout);
    }

    /// <summary>The colours the plane-construction screens write in.</summary>
    public OriginalHangarInks Inks { get; }

    /// <summary>The hangar's own string table, or null over a shell built without a feature.</summary>
    public UiStrings? Strings => _hangar?.Strings;

    /// <summary>Whether seat 0's typed characters feed one of the hangar's own text fields right
    /// now. The field is the name screen's edit box, or the hub's own box while the focus stands in
    /// it. None of the two while a dialog stands over the screen. An armed typed cheat also takes
    /// the keyboard, but its latch is the shell's, so the shell adds it to its own reading.</summary>
    public bool CapturingText =>
        !_host.DialogOpen
        && (_host.Screen == OriginalScreen.PlaneName || (IsHub && _host.FocusedKey == HubNameFieldKey));

    /// <summary>The name typed on the name screen so far.</summary>
    public string HangarName => _hangarName;

    /// <summary>Whether the name screen's Load Default Configuration box is checked.</summary>
    public bool LoadDefaultsChecked => _hangarDefaults;

    /// <summary>The open hangar dropdown's key, or null when none is open.</summary>
    public string? OpenHangarDropdown => _hangarOpen;

    /// <summary>The inventory's picked saved plane, an index into the feature's roster.</summary>
    public int InventoryIndex => _inventoryIndex;

    /// <summary>The name the last completed build saved under, or null when none has this session.</summary>
    public string? LastBuiltPlane => _builtPlane;

    /// <summary>The tab the hub last showed, which SELL PLANES and Back on the totals page return to.</summary>
    public OriginalScreen HangarTab => _hangarTab;

    /// <summary>Whether the screen showing is the plane-construction hub, a tab or the purchase
    /// page: where the cash figure and the hub's own name box stand.</summary>
    public bool IsHub => IsHangarTab || _host.Screen == OriginalScreen.HangarPurchase;

    /// <summary>The wallet the build showing is priced against, or null where the door opened
    /// wallet-free or no build is open.</summary>
    public IHangarWallet? OpenWallet => _hangar?.Wallet;

    private bool IsHangarTab => _host.Screen >= OriginalScreen.HangarAirframe && _host.Screen <= OriginalScreen.HangarPaint;

    /// <summary>Whether the screen showing is one of the hangar's: the name screen, a tab, the
    /// totals page or the inventory.</summary>
    public bool Owns(OriginalScreen screen) => screen >= OriginalScreen.PlaneName;

    /// <summary>Opens one of the showing tab's dropdowns as a press on it would, for a scripted
    /// pose. False when the key names no list on the tab showing. A grid opens on the row its pick
    /// stands in, as a press does.</summary>
    public bool OpenHangarDropdownOn(string key)
    {
        if (!IsHangarTab || HangarListFor(key) is not { Items.Count: > 0 } list)
        {
            return false;
        }

        _hangarOpen = key;
        int current = Math.Max(0, list.Current);
        _hangarListTop = list.Columns > 1 ? current - (current % list.Columns) : 0;
        _host.FocusedRow = current;
        return true;
    }

    /// <summary>Opens the hangar from the screen showing, a build over the saved-plane store. It is
    /// wallet-free from Instant Action's Build Custom Plane and over <paramref name="wallet"/> from
    /// the cabin's PLANE CONSTRUCTION. Entry is through the name screen, as the original's own
    /// chain does. The screen the door was pressed on is where CANCEL and a commit return to. The
    /// parameter <paramref name="doorAirframe"/> is the door's own default-build airframe. Nothing
    /// happens when the shell has no feature or no store.</summary>
    public void OpenHangar(IHangarWallet? wallet, int doorAirframe)
    {
        if (_hangar == null || _planes == null)
        {
            return;
        }

        // Over a wallet the build store is the campaign's own. Ownership and the file it names
        // cannot then end up in two directories when a suite or an aid seats a scratch store.
        _hangar.Open(wallet != null ? _host.CampaignPlanes ?? _planes : _planes, wallet);
        _hangarDefaultAirframe = doorAirframe;
        _hangarReturn = Owns(_host.Screen) ? OriginalScreen.TopLevel : _host.Screen;
        _hangarName = string.Empty;
        _hangarDefaults = true;
        _hangarOpen = null;
        _hangarTab = OriginalScreen.HangarAirframe;
        _host.Open(OriginalScreen.PlaneName);
    }

    /// <summary>Opens a hangar tab directly on a default-configuration build named
    /// <paramref name="name"/>, the screenshot aids' door. It is the name screen's OK with the box
    /// checked, then the tab. It runs over <paramref name="wallet"/> when the shot wants the
    /// campaign's cash note. Nothing happens without a feature or a store.</summary>
    public void OpenHangarTab(OriginalScreen tab, string name, IHangarWallet? wallet, int doorAirframe)
    {
        if (_hangar == null || _planes == null)
        {
            return;
        }

        OpenHangar(wallet, doorAirframe);
        _hangarName = name;
        AcceptName();
        if (tab != OriginalScreen.HangarAirframe)
        {
            ShowHangarScreen(tab);
        }
    }

    // The rest of this class stays in the original's own narrative order, a helper beside the
    // handful of public entry points it serves. It is not hoisted into two blocks for SA1202's
    // sake, the same trade FlightController.cs makes.
#pragma warning disable SA1202

    private static OriginalHangarInks ReadInks(MenuLayout layout)
    {
        var hub = layout.Screen(PlaneConstructionSection);
        var airframe = layout.Screen("AirFrame");
        var black = new MenuLayoutColor(255, 0, 0, 0);
        var white = new MenuLayoutColor(255, 255, 255, 255);
        var purple = new MenuLayoutColor(255, 0x37, 0x27, 0x7A);
        var text = airframe?.Widget("AF_T_AIRFRAME");
        var title = airframe?.Widget("AF_T_TITLE");
        var page = hub?.Widget("PX_T_PLANENAME");
        var tab = hub?.Widget("PX_B_AIRFRAME");
        return new OriginalHangarInks(
            text != null && text.TryColor("Color", out var t) ? t : black,
            title != null && title.TryColor("Color", out var h) ? h : new MenuLayoutColor(255, 0x26, 0x1E, 0x47),
            page != null && page.TryColor("Color", out var p) ? p : white,
            tab != null && tab.TryColor("ColorActive", out var a) ? a : white,
            tab != null && tab.TryColor("ColorDisabled", out var d) ? d : purple,
            tab != null && tab.TryColor("ColorDepressed", out var e) ? e : purple);
    }

    private static string Fill(string text, params object[] args)
    {
        string words = FontTag.Replace(text, string.Empty).Replace("\r", string.Empty).Replace("\n", " ").Trim();
        for (int i = 0; i < args.Length; i++)
        {
            string value = Convert.ToString(args[i], CultureInfo.InvariantCulture) ?? string.Empty;
            words = words.Replace($"%{i + 1}!d!", value).Replace($"%{i + 1}!s!", value);
        }

        return words;
    }

    private static string Rating(int stars) => RatingWords[Math.Clamp(stars, 0, RatingWords.Length - 1)];

    private static string ListKey(string key, int index) => key + ":" + index.ToString(CultureInfo.InvariantCulture);

    private static (float X, float Y, float Width, float Height) HubBox(MenuLayoutWidget widget) =>
        (widget.Int("X"), widget.Int("Y"), widget.Int("Width", (int)FallbackDropWidth), widget.Int("ItemHeight", (int)FallbackHubItemHeight));

    private static BoardTint Tint(PaintColour colour) => new(colour.R, colour.G, colour.B);

    // The screen a tab key names, or null for a key that is not a tab.
    private static OriginalScreen? TabOf(string key)
    {
        foreach (var tab in Tabs)
        {
            if (tab.Key == key)
            {
                return tab.Screen;
            }
        }

        return null;
    }

    private static string SectionOf(OriginalScreen screen)
    {
        foreach (var tab in Tabs)
        {
            if (tab.Screen == screen)
            {
                return tab.Section;
            }
        }

        return screen == OriginalScreen.HangarPurchase ? PurchaseSection : PlaneConstructionSection;
    }

    private static int IndexOf(IReadOnlyList<int> values, int value)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] == value)
            {
                return i;
            }
        }

        return -1;
    }

    // Which numbered dropdown of a family the focus is on or in, or null.
    private static int? FocusedSlot(IReadOnlyList<OriginalRow> rows, int focus, string prefix)
    {
        if (focus < 0 || focus >= rows.Count)
        {
            return null;
        }

        string key = rows[focus].Key;
        int colon = key.IndexOf(':');
        return OriginalWidgets.Indexed(colon > 0 ? key[..colon] : key, prefix);
    }

    // A strip's depressed frame, the fourth of a four-state strip; a shorter strip has only one.
    private static int DepressedFrame(int frames) => frames >= 4 ? 3 : 0;

    // The paper buttons whose authored label colours are the page's black, not the tabs' white.
    private static bool IsPaperButton(string key) =>
        key is PurchaseNowKey or InventorySellKey or InventoryExportKey;

    // A list whose rows move the bill. The pricing is kept on the list, so the hub can show what a
    // row under the cursor would cost and weigh without taking it.
    // ⚠ Do not mark the rows the funds cannot cover. Every reference shot of an open or closed
    // combo here draws bare rows. The decoded screen reports funds at the purchase alone
    // (docs/org/hangar.md, "The purchase gate"), with the cost figure reddening meanwhile.
    private static HangarList PricedList(
        string[] items, int current, Action<int> select, Func<int, HangarBill> billWith) =>
        new(items, current, select, BillWith: billWith);

    // A rating's bar: the five authored segment panes, the first n drawn for n stars.
    private static void ComposeBar(MenuLayoutScreen hub, List<BoardPicture> pictures, string prefix, int filled)
    {
        for (int i = 0; i < filled && i < 5; i++)
        {
            if (hub.Widget(prefix + i.ToString(CultureInfo.InvariantCulture)) is { Art.Count: > 0 } segment)
            {
                pictures.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, segment.Art[0]), segment.Int("X"), segment.Int("Y")));
            }
        }
    }

    private static void PurchaseLine(MenuLayoutScreen page, List<BoardLine> lines, string key, string name, CostWeight line)
    {
        if (page.Widget(key) is { } item)
        {
            lines.Add(new BoardLine(name, item.Int("X"), item.Int("Y"), item.Int("Width"), HubTextFont, BoardInk.Row));
        }

        PurchaseFigure(page, lines, key + "WEIGHT", line.Weight.ToString(CultureInfo.InvariantCulture));
        PurchaseFigure(page, lines, key + "COST", "$" + line.Cost.ToString(CultureInfo.InvariantCulture));
    }

    private static void PurchaseFigure(MenuLayoutScreen page, List<BoardLine> lines, string key, string text)
    {
        if (page.Widget(key) is { } figure)
        {
            lines.Add(new BoardLine(text, figure.Int("X"), figure.Int("Y"), figure.Int("Width"), HubTextFont, BoardInk.Row, -1, false, Justify(figure)));
        }
    }

    // One text list of the totals page and its two figure columns, spaced by the list's own item
    // height and spacing.
    private static void PurchaseList(MenuLayoutScreen page, List<BoardLine> lines, string key, IReadOnlyList<(string Name, CostWeight Line)> items)
    {
        var list = page.Widget(key + "LIST");
        var weights = page.Widget(key + "WEIGHT");
        var costs = page.Widget(key + "COST");
        if (list == null)
        {
            return;
        }

        float pitch = list.Int("Height", 16) + list.Int("ItemSpacing", 1);
        for (int i = 0; i < items.Count; i++)
        {
            // One line per item, unwrapped: the list's pitch is one row, so a wrapped name would
            // overwrite the row under it.
            float dy = i * pitch;
            lines.Add(new BoardLine(items[i].Name, list.Int("X"), list.Int("Y") + dy, 0f, DescFont, BoardInk.Row));
            if (weights != null)
            {
                lines.Add(new BoardLine(items[i].Line.Weight.ToString(CultureInfo.InvariantCulture), weights.Int("X"), weights.Int("Y") + dy,
                    weights.Int("Width"), HubTextFont, BoardInk.Row, -1, false, Justify(weights)));
            }

            if (costs != null)
            {
                lines.Add(new BoardLine("$" + items[i].Line.Cost.ToString(CultureInfo.InvariantCulture), costs.Int("X"), costs.Int("Y") + dy,
                    costs.Int("Width"), HubTextFont, BoardInk.Row, -1, false, Justify(costs)));
            }
        }
    }

    // A named aircraft and its airframe, the compound the campaign's plane selection writes on one
    // row too. Where the two are the same word it stands once, since "Devastator   Devastator" is
    // not a second fact.
    private static string PlaneLine(HangarFeature hangar, CustomPlaneDef plane)
    {
        string airframe = hangar.AirframeName(plane.Airframe);
        return plane.Name == airframe ? airframe : plane.Name + "   " + airframe;
    }

    // An overridden Y is a row whose line is measured off the artwork rather than taken from the
    // layout.
    private static void InventoryLine(
        MenuLayoutScreen screen, List<BoardLine> lines, string key, string text, float size, float? y = null)
    {
        if (screen.Widget(key) is { } widget)
        {
            lines.Add(new BoardLine(text, widget.Int("X"), y ?? widget.Int("Y"), widget.Int("Width"), size, BoardInk.Row));
        }
    }

    // A text row at its authored place in a chosen ink. The hangar's pages author black text the
    // shared reader would otherwise draw in the heading colour. A given text stands in for the
    // row's own, which is how a page whose verbs differ from the shipped ones says so.
    private static void AddHangarText(MenuLayoutScreen screen, List<BoardLine> lines, string key, float size, BoardInk ink, string? text = null)
    {
        if (screen.Widget(key) is not { } widget)
        {
            return;
        }

        string words = text ?? widget.Text ?? string.Empty;
        if (words.Length == 0)
        {
            return;
        }

        lines.Add(new BoardLine(Fill(words), widget.Int("X"), widget.Int("Y"), widget.Int("Width"), size,
            IsWhite(widget) ? BoardInk.Dialog : ink, -1, false, Justify(widget)));
    }

    // One built row's label replaced where it stands, so the rows' order (the focus walk) is the
    // same on both doors.
    private static void Relabel(List<OriginalRow> rows, string key, string label)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Key == key)
            {
                rows[i] = rows[i] with { Label = label };
            }
        }
    }

    // Moves rows built at their authored coordinates onto the section's own pane, every row from
    // the given one on. A section whose pane is centred authors its rows relative to that corner.
    private static void Relocate(List<OriginalRow> rows, int first, (float X, float Y) origin)
    {
        for (int i = first; i < rows.Count; i++)
        {
            rows[i] = rows[i] with { X = rows[i].X + origin.X, Y = rows[i].Y + origin.Y };
        }
    }

    // One frame of typing into a plane-name box.
    private static bool TypeInto(ref string text, MenuCommands commands, List<string> cues)
    {
        bool changed = false;
        foreach (char c in commands.Typed)
        {
            if (HangarFeature.AcceptsNameChar(c) && text.Length < HangarFeature.MaxNameLength)
            {
                text += c;
                changed = true;
                cues.Add(OriginalCues.Text);
            }
            else
            {
                cues.Add(OriginalCues.TextError);
            }
        }

        if (commands.Erase && text.Length > 0)
        {
            text = text[..^1];
            changed = true;
        }

        return changed;
    }

    // The n-th art a row names as a strip. Arrows and radios carry their frame count in the row.
    // The dropdown arrows are the same four-frame strips the page buttons draw.
    private static BoardArt? StripArt(IReadOnlyList<string> art, int index, int frames = 4) =>
        index < art.Count && art[index].Length > 0 ? new BoardArt(BoardArtLibrary.Ui, art[index], Math.Max(1, frames)) : null;

    private static bool IsWhite(MenuLayoutWidget widget, string field = "Color") =>
        widget.TryColor(field, out var c) && c.R == 255 && c.G == 255 && c.B == 255;

    private static BoardJustify Justify(MenuLayoutWidget widget) => widget.Int("Justify") switch
    {
        1 => BoardJustify.Center,
        2 => BoardJustify.Right,
        _ => BoardJustify.Left,
    };

    // A strip's one-frame size from the measurer, or the fallback when the file is not there.
    private (float Width, float Height) StripSize(BoardArt? art, float fallbackWidth, float fallbackHeight)
    {
        if (art == null || _host.Measure(art.Name) is not { } size)
        {
            return (fallbackWidth, fallbackHeight);
        }

        return (size.Width, (float)Math.Floor(size.Height / (float)Math.Max(1, art.Frames)));
    }

    private void AddStrip(MenuLayoutScreen screen, List<OriginalRow> rows, string key, OriginalRowKind kind, bool enabled, int column)
    {
        if (screen.Widget(key) is not { } widget)
        {
            return;
        }

        var art = StripArt(widget.Art, 0, widget.Frames);
        var size = StripSize(art, FallbackButtonWidth, FallbackButtonHeight);
        rows.Add(new OriginalRow(key, widget.Text ?? string.Empty, kind, widget.Int("X"), widget.Int("Y"),
            size.Width, size.Height, enabled, column, art));
    }

    // The shared pane rule (OriginalWidgets) over the shell's own cached measurer, bound here so no
    // call site has to carry it.
    private (float X, float Y) PaneOrigin(MenuLayoutScreen screen, string key) =>
        OriginalWidgets.PaneOrigin(screen, key, _host.Measure);

    // The scroll-text box as a panel, in its authored back and border colours. The component's
    // figures go on their own lines, the shipped heading a line under them, the prose flowed in
    // the room left. The prose is a note because how many lines it wraps to is a font measurement.
    // A body longer than the room scrolls inside the box, the way the authored widget's own slider
    // and arrows say it does. The note hands back the two line counts the window is drawn and
    // clamped from.
    private void ComposeDescription(MenuLayoutWidget widget, HangarInfo info, BoardLayers layers)
    {
        float x = widget.Int("X");
        float y = widget.Int("Y");
        float width = widget.Int("Width", 300);
        float height = widget.Int("Height", 150);
        if (widget.TryColor("BackColor", out var back))
        {
            layers.Fills.Add(new BoardFill(x, y, width, height, back.R, back.G, back.B));
        }

        if (widget.TryColor("BorderColor", out var border))
        {
            layers.Fills.Add(new BoardFill(x, y, width, height, border.R, border.G, border.B, Border: true));
        }

        int row = 0;
        foreach (string figure in info.Figures)
        {
            layers.Lines.Add(new BoardLine(figure, x + DescInset, y + DescTop + (row++ * DescLine), width - (2f * DescInset), DescFont, BoardInk.Row, -1, true));
        }

        if (info.Heading.Length > 0)
        {
            // The shipped string's own blank line stands between the figures and the heading.
            row++;
            layers.Lines.Add(new BoardLine(info.Heading, x + DescInset, y + DescTop + (row++ * DescLine), width - (2f * DescInset), DescFont, BoardInk.Row, -1, true));
        }

        _descWindow = null;
        if (info.Prose.Length == 0)
        {
            _descBody = string.Empty;
            _descLines = 0;
            _descTop = 0;
            return;
        }

        if (_descBody != info.Prose)
        {
            // A new body is a new box, so the window goes back to the head. That is where the
            // original's own box stands every time a tab or a picked component changes it.
            _descBody = info.Prose;
            _descLines = 0;
            _descTop = 0;
        }

        float top = y + DescTop + (row * DescLine);
        float room = Math.Max(0f, y + height - DescTop - top);
        _descTop = Math.Clamp(_descTop, 0, Math.Max(0, _descLines - _descFits));
        layers.Notes.Add(new BoardNote(
            new[] { info.Prose }, x + DescInset, top, width - (2f * DescInset),
            room, 0f, DescFont, BoardInk.Row, Italic: true, Cut: true, Skip: _descTop,
            Counted: (total, fits) => (_descLines, _descFits) = (total, fits)));
        _descWindow = DescriptionWindow(widget, x + width, top, room);
        ComposeDescriptionBar(widget, layers);
    }

    // The prose window as the pointer sees it, counted in wrapped lines rather than in list rows.
    // It is the box's own text area, with the thumb column inside its right edge between the
    // arrows. Null while the body fits, which is when the original's box carries no slider either.
    private ListWindow? DescriptionWindow(MenuLayoutWidget widget, float right, float top, float room)
    {
        if (_descFits <= 0 || _descLines <= _descFits)
        {
            return null;
        }

        var arrow = StripSize(StripArt(widget.Art, 1), FallbackArrowWidth, FallbackArrowHeight);
        var thumb = StripSize(StripArt(widget.Art, 0, 1), arrow.Width, FallbackThumbHeight);
        float track = Math.Max(1f, room - (2f * arrow.Height));
        float tall = Math.Min(track, Math.Max(thumb.Height, track * _descFits / _descLines));
        return new ListWindow(
            widget.Int("X"), top, widget.Int("Width", 300), room,
            right - arrow.Width, ListWindow.ThumbYFor(top + arrow.Height, track, tall, _descTop, _descLines - _descFits),
            thumb.Width, tall, top + arrow.Height, track, _descLines, _descFits, _descTop);
    }

    // Puts the description box's window at that line, clamped to the body it holds.
    private void ScrollDescription(int top) => _descTop = Math.Clamp(top, 0, Math.Max(0, _descLines - _descFits));

    // The box's own slider column, at its right edge between the two authored arrows, drawn only
    // while the body is longer than the box. The arrows are pictures rather than rows for the
    // reason the keys list's are. The wheel and the thumb move this window, and the hub's focus
    // order is the one the original's tab key walks.
    private void ComposeDescriptionBar(MenuLayoutWidget widget, BoardLayers layers)
    {
        if (_descWindow is not { } window)
        {
            return;
        }

        var up = StripArt(widget.Art, 1);
        var down = StripArt(widget.Art, 2);
        var bar = StripArt(widget.Art, 0, 1);
        float arrow = window.TrackTop - window.Y;
        if (up != null && down != null && bar != null
            && _host.Measure(up.Name) != null && _host.Measure(down.Name) != null && _host.Measure(bar.Name) != null)
        {
            layers.Pictures.Add(new BoardPicture(up, window.ThumbX, window.Y));
            layers.Pictures.Add(new BoardPicture(down, window.ThumbX, window.Y + window.Height - arrow));
            layers.Pictures.Add(new BoardPicture(bar, window.ThumbX, window.ThumbY));
            return;
        }

        layers.Fills.Add(new BoardFill(window.ThumbX, window.Y, window.ThumbWidth, window.Height, 255, 255, 255, 0.3f, Border: true));
        layers.Fills.Add(new BoardFill(window.ThumbX, window.ThumbY, window.ThumbWidth, window.ThumbHeight, 255, 255, 255, 0.6f));
    }

    private void AddPane(MenuLayoutScreen screen, List<BoardPicture> pictures, string key) =>
        OriginalWidgets.AddPane(screen, pictures, key, _host.Measure);

    // Enters one of the hub's screens. The tabs remember themselves for the inventory's Done and
    // the totals page's Back, and no list stays open across the change.
    private void ShowHangarScreen(OriginalScreen screen)
    {
        _hangarOpen = null;
        if (screen >= OriginalScreen.HangarAirframe && screen <= OriginalScreen.HangarPaint)
        {
            _hangarTab = screen;
        }

        _host.Open(screen);
    }

    // The name screen's OK: the typed name onto a bare or default-configuration build, then the
    // first tab. An empty box is refused at the press, PLANENAME.SCRIPT's own else arm. Langui 203
    // is raised under a 0x1 mask, so the box wears the warning icon and one OK. The focus goes
    // back into the box behind it. A box of nothing but spaces is refused with it, the store
    // naming a file after what was typed.
    private void AcceptName()
    {
        if (_hangar == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_hangarName))
        {
            _host.RaiseDialog(
                _hangar.Strings.Text(203, "You must enter a name for your new plane."),
                DialogIcon.Warning,
                Ok(() => _host.FocusKey(NameFieldKey)));
            return;
        }

        if (_hangarDefaults)
        {
            _hangar.StartDefaultPlane(_hangarDefaultAirframe);
        }
        else
        {
            _hangar.StartNewPlane();
        }

        _hangar.Scratch.Name = _hangarName.Trim();
        ShowHangarScreen(OriginalScreen.HangarAirframe);
    }

    // The airframe swap's own question where the feature has one pending, as MESSAGEBOX.SCRIPT's
    // three-button box. Yes takes the new airframe's stock build, No takes it bare, and Cancel puts
    // the airframe back (AIRFRAME.SCRIPT's three mailbox arms, docs/org/hangar.md). The box is
    // raised from here rather than from the pick. Only the swap that changed an edited build
    // raises one, and the feature is what knows that.
    private void RaisePendingDefaultsAsk()
    {
        if (_hangar?.DefaultsAsk == null)
        {
            return;
        }

        _host.RaiseDialog(
            _hangar.DefaultsAskText, DialogIcon.Query,
            Yes(() => AnswerDefaults(true)),
            NoCentred(() => AnswerDefaults(false)),
            Cancel(() => AnswerDefaults(null)));
    }

    // One arm of the ask: the stock build, the bare airframe, or (null) the airframe put back.
    private void AnswerDefaults(bool? stock)
    {
        if (stock is { } load)
        {
            _hangar?.AnswerDefaultsAsk(load);
        }
        else
        {
            _hangar?.CancelDefaultsAsk();
        }

        _host.FocusKey(AirframeDropKey);
    }

    // Leaves the hangar without saving: the scratch plane is dropped and the screen the door was
    // pressed on comes back.
    private void CancelHangar()
    {
        _hangar?.Discard();
        _hangarOpen = null;
        ReturnFromHangar();
    }

    // The commit. On success the roster both presentations pick from gains the plane at once, the
    // scratch build is dropped and the entry screen comes back. A refusal stays with its reason.
    private void PurchaseNow()
    {
        if (_hangar == null || !_hangar.Commit())
        {
            return;
        }

        _builtPlane = _hangar.BuiltPlaneName;
        _host.RefreshRosterFromStore();
        _hangar.Discard();
        ReturnFromHangar();
    }

    // Back onto the entry screen. The cabin re-reads its profile on the way, so a purchase or a
    // sale through the wallet shows on it. The Instant Action screen re-reads its Pilot Plane
    // list, so a build saved here is offered without leaving it.
    private void ReturnFromHangar()
    {
        if (_hangarReturn == OriginalScreen.CampaignCabin)
        {
            _host.ResumeCampaign();
            return;
        }

        if (_hangarReturn == OriginalScreen.InstantAction)
        {
            _host.RefreshInstantActionRoster();
        }

        _host.Open(_hangarReturn);
    }

    // Typed characters and Backspace into whichever hangar edit box is showing, each box's own
    // rule. The name screen and the hub share the hangar name's character set and cap.
    public bool TypeName(MenuCommands commands, List<string> cues)
    {
        if (_host.Screen == OriginalScreen.PlaneName)
        {
            return TypeInto(ref _hangarName, commands, cues);
        }

        if (!IsHub || _hangar == null || _host.FocusedKey != HubNameFieldKey)
        {
            return false;
        }

        // The hub's box renames the build in place: the scratch plane's own name, so no second
        // plane is started and no pick is re-asked.
        string name = _hangar.Scratch.Name;
        bool typed = TypeInto(ref name, commands, cues);
        _hangar.Scratch.Name = name;
        return typed;
    }

    public bool CloseDropdown()
    {
        if (_hangarOpen == null)
        {
            return false;
        }

        string key = _hangarOpen;
        _hangarOpen = null;
        _host.FocusKey(key);
        return true;
    }

    // Back inside the hangar. An open list closes, and the totals page and the inventory return
    // to the tab. A tab or the name screen cancels the build. Every hangar screen answers its own
    // Back, so the shell's default way out is never reached from here.
    public bool Back()
    {
        if (CloseDropdown())
        {
            return true;
        }

        if (_host.Screen is OriginalScreen.HangarPurchase or OriginalScreen.HangarInventory)
        {
            ShowHangarScreen(_hangarTab);
            return true;
        }

        CancelHangar();
        return true;
    }

    // A sideways step inside the hangar. On a dropdown it picks the next value with wrap, and on
    // the tab bar and the buttons it walks along them. A text field or a checkbox takes none.
    public bool StepSideways(IReadOnlyList<OriginalRow> rows, int focus, int direction)
    {
        if (focus < 0 || focus >= rows.Count)
        {
            return false;
        }

        var row = rows[focus];
        if (row.Kind == OriginalRowKind.Dropdown && HangarListFor(row.Key) is { } list && list.Items.Count > 0)
        {
            int next = ((list.Current + direction) % list.Items.Count + list.Items.Count) % list.Items.Count;
            if (next != list.Current)
            {
                list.Select(next);
            }

            _host.FocusKey(row.Key);
            RaisePendingDefaultsAsk();
            return true;
        }

        if (row.Kind is not (OriginalRowKind.Button or OriginalRowKind.TextButton))
        {
            return false;
        }

        int i = focus;
        for (int n = 0; n < rows.Count; n++)
        {
            i = (i + direction + rows.Count) % rows.Count;
            if (rows[i].Enabled && rows[i].Kind is OriginalRowKind.Button or OriginalRowKind.TextButton)
            {
                _host.FocusedRow = i;
                return true;
            }
        }

        return true;
    }

    public void BuildRows(List<OriginalRow> rows)
    {
        if (_hangar == null)
        {
            return;
        }

        switch (_host.Screen)
        {
            case OriginalScreen.PlaneName:
                BuildPlaneNameRows(rows);
                break;
            case OriginalScreen.HangarInventory:
                if (!BuildOpenHangarList(rows))
                {
                    BuildInventoryRows(rows);
                }

                break;
            default:
                if (!BuildOpenHangarList(rows))
                {
                    BuildHubRows(rows);
                }

                break;
        }
    }

    private void BuildPlaneNameRows(List<OriginalRow> rows)
    {
        var screen = _layout.Screen(PlaneNameSection);
        if (screen == null)
        {
            return;
        }

        int first = rows.Count;
        if (screen.Widget(NameFieldKey) is { } field)
        {
            rows.Add(new OriginalRow(NameFieldKey, _hangarName, OriginalRowKind.TextField, field.Int("X"), field.Int("Y"),
                field.Int("Width", 200), field.Int("Height", 16), true, 0, null));
        }

        if (screen.Widget(NameDefaultsKey) is { } box)
        {
            var art = StripArt(box.Art, 0, box.Frames);
            var size = StripSize(art, 16f, 16f);
            rows.Add(new OriginalRow(NameDefaultsKey, string.Empty, OriginalRowKind.Radio, box.Int("X"), box.Int("Y"),
                size.Width, size.Height, true, 0, art));
        }

        // OK stands whatever the box holds: PLANENAME.SCRIPT deactivates nothing and answers an
        // empty box at the press instead.
        AddStrip(screen, rows, NameOkKey, OriginalRowKind.TextButton, true, 0);
        AddStrip(screen, rows, NameCancelKey, OriginalRowKind.TextButton, true, 0);
        Relocate(rows, first, PaneOrigin(screen, PlaneNamePaneKey));
    }

    // The hub's rows in focus order: the page's dropdowns (or the totals page's Purchase Now),
    // then the six tabs, then SELL PLANES, READY and CANCEL. No tab is gated. The hub's script
    // latches the standing one and never deactivates any of them, so all six stay hittable. The
    // standing one is told apart by the frame it draws in.
    private void BuildHubRows(List<OriginalRow> rows)
    {
        var hub = _layout.Screen(PlaneConstructionSection);
        if (_host.Screen == OriginalScreen.HangarPurchase)
        {
            if (_layout.Screen(PurchaseSection) is { } purchase)
            {
                AddStrip(purchase, rows, PurchaseNowKey, OriginalRowKind.TextButton, _hangar!.CanCommit, 0);
                if (_hangar.Wallet == null)
                {
                    // The export door commits with Export, as its own still shows; the shipped
                    // table's only export button label is the inventory's.
                    Relabel(rows, PurchaseNowKey, _hangar.Strings.Text(ExportLabelString, "Export"));
                }
            }
        }
        else if (_layout.Screen(SectionOf(_host.Screen)) is { } page)
        {
            foreach (var widget in page.Widgets)
            {
                if (widget.TypeCode == "D" && HangarListFor(widget.Key) is { } list)
                {
                    var box = HubBox(widget);
                    string value = list.Current >= 0 && list.Current < list.Items.Count ? list.Items[list.Current] : string.Empty;
                    rows.Add(new OriginalRow(widget.Key, value, OriginalRowKind.Dropdown, box.X, box.Y, box.Width, box.Height,
                        true, 0, StripArt(widget.Art, 4)));
                }
            }
        }

        if (hub == null)
        {
            return;
        }

        foreach (var tab in Tabs)
        {
            AddStrip(hub, rows, tab.Key, OriginalRowKind.TextButton, true, 0);
        }

        AddStrip(hub, rows, SellPlanesKey, OriginalRowKind.Button, true, 0);
        AddStrip(hub, rows, ReadyKey, OriginalRowKind.Button, _host.Screen != OriginalScreen.HangarPurchase, 0);
        AddStrip(hub, rows, CancelBuildKey, OriginalRowKind.Button, true, 0);
        // The name box comes last so the page still opens on its own first control. It sits at the
        // top left of the page, which is where the pointer finds it.
        if (hub.Widget(HubNameFieldKey) is { } name)
        {
            rows.Add(new OriginalRow(HubNameFieldKey, _hangar!.Scratch.Name, OriginalRowKind.TextField,
                HubNameX, name.Int("Y"), HubNameRight - HubNameX, name.Int("Height", 16), true, 0, null));
        }

        if (_hangar!.Wallet == null)
        {
            // The wallet-free door wears the export strips the stills show on the Instant Action
            // path (READY TO EXPORT, CANCEL EXPORT), when the extraction carries them.
            ExportVariant(rows, ReadyKey, "PX_B_ReadyToExport.png");
            ExportVariant(rows, CancelBuildKey, "PX_B_CancelExport.png");
        }
    }

    private void ExportVariant(List<OriginalRow> rows, string key, string art)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Key == key && rows[i].Art is { } strip && _host.Measure(art) != null)
            {
                rows[i] = rows[i] with { Art = strip with { Name = art } };
            }
        }
    }

    private void BuildInventoryRows(List<OriginalRow> rows)
    {
        var screen = _layout.Screen(InventorySection);
        if (screen == null || _hangar == null)
        {
            return;
        }

        if (screen.Widget(InventoryPlanesKey) is { } planes && HangarListFor(InventoryPlanesKey) is { } list)
        {
            var box = HubBox(planes);
            string value = list.Current >= 0 && list.Current < list.Items.Count ? list.Items[list.Current] : string.Empty;
            rows.Add(new OriginalRow(InventoryPlanesKey, value, OriginalRowKind.Dropdown, box.X, box.Y, box.Width, box.Height,
                list.Items.Count > 0, 0, StripArt(planes.Art, 4)));
        }

        AddStrip(screen, rows, InventorySellKey, OriginalRowKind.TextButton, _hangar.Saved.Count > 0, 0);
        if (_hangar.Wallet == null)
        {
            // Wallet-free the removal is a delete, and there is nowhere to export to. The export
            // door's own store is the one the sortie pickers already read. So the Export row is
            // not built at all rather than built dead, and Sell takes the delete word.
            Relabel(rows, InventorySellKey, DeleteLabel);
        }
        else
        {
            AddStrip(screen, rows, InventoryExportKey, OriginalRowKind.TextButton, _hangar.Saved.Count > 0, 0);
        }

        AddStrip(screen, rows, InventoryDoneKey, OriginalRowKind.Button, true, 0);
    }

    // An open list's rows. Every item is keyed <key>:<index> under the box, and the ones outside
    // the authored window are unseen and unhit. The window follows the focused item, and the
    // list's own arrows sit inside its right edge while there is more list that way. A scrolling
    // list gives an arrow's width of itself to the chrome, the way Instant Action's gutter does.
    private bool BuildOpenHangarList(List<OriginalRow> rows)
    {
        if (_hangarOpen == null)
        {
            return false;
        }

        string section = _host.Screen == OriginalScreen.HangarInventory ? InventorySection : SectionOf(_host.Screen);
        if (_layout.Screen(section)?.Widget(_hangarOpen) is not { } widget || HangarListFor(_hangarOpen) is not { } list)
        {
            _hangarOpen = null;
            return false;
        }

        var box = HubBox(widget);
        int count = list.Items.Count;
        var grid = OpenDecalGrid(widget, list);
        int window = grid?.Window ?? Math.Clamp(widget.Int("TotalDisplayed", count), 1, Math.Max(1, count));
        int focused = _host.FocusedRow;
        if (focused >= 0 && focused < count)
        {
            // A grid scrolls by whole rows, so the window follows the row the focus stands in.
            int first = focused - (focused % list.Columns);
            if (first < _hangarListTop)
            {
                _hangarListTop = first;
            }
            else if (first >= _hangarListTop + window)
            {
                _hangarListTop = first - window + list.Columns;
            }
        }

        _hangarListTop = Math.Clamp(_hangarListTop, 0, Math.Max(0, count - window));
        _hangarListTop -= _hangarListTop % list.Columns;
        var up = StripArt(widget.Art, 3);
        var down = StripArt(widget.Art, 4);
        var upSize = StripSize(up, FallbackArrowWidth, FallbackArrowHeight);
        var downSize = StripSize(down, FallbackArrowWidth, FallbackArrowHeight);
        float column = window < count ? upSize.Width : 0f;
        for (int i = 0; i < count; i++)
        {
            bool visible = i >= _hangarListTop && i < _hangarListTop + window;
            (float X, float Y) at = grid is { } cells
                ? cells.Cell(i - _hangarListTop)
                : (box.X, box.Y + (box.Height * (i - _hangarListTop + 1)));
            rows.Add(new OriginalRow(ListKey(_hangarOpen, i), list.Items[i], OriginalRowKind.ListRow,
                at.X, at.Y, grid?.Tile ?? (box.Width - column), grid?.Tile ?? box.Height, true, 0, null, visible));
        }

        if (window < count)
        {
            float arrowX = grid?.ScrollX ?? (box.X + box.Width - upSize.Width);
            float upY = grid is { } head ? head.Y + DecalGridBorder : box.Y + box.Height;
            float downY = grid is { } foot
                ? foot.Y + foot.Height - DecalGridBorder - downSize.Height
                : box.Y + (box.Height * (window + 1)) - downSize.Height;
            rows.Add(new OriginalRow(_hangarOpen + ":up", string.Empty, OriginalRowKind.Button,
                arrowX, upY, upSize.Width, upSize.Height, _hangarListTop > 0, 0, up));
            rows.Add(new OriginalRow(_hangarOpen + ":down", string.Empty, OriginalRowKind.Button,
                arrowX, downY, downSize.Width, downSize.Height, _hangarListTop + window < count, 0, down));
        }

        return true;
    }

    // The open list's grid where the list is one, null where its rows are a single column. The
    // decal picker's own five columns come off the list and its rows off the authored
    // TotalDisplayed. Its tile comes off the sheet the paint page carries.
    private DecalGrid? OpenDecalGrid(MenuLayoutWidget widget, HangarList list)
    {
        if (list.Columns <= 1)
        {
            return null;
        }

        var tile = StripSize(DecalArt(), DecalTile, DecalTile);
        int rows = Math.Max(1, widget.Int("TotalDisplayed", 1));
        float arrow = StripSize(StripArt(widget.Art, 3), FallbackArrowWidth, FallbackArrowHeight).Width;
        return new DecalGrid(DecalGridX, DecalGridY, tile.Height, list.Columns, rows, arrow);
    }

    // The hangar's lists for the pointer. An open dropdown's list stands alone while one is open,
    // and the tab page's description box otherwise. That box is the only other thing on these
    // screens a wheel or a dragged thumb moves.
    public void Lists(List<OriginalList> lists)
    {
        if (_hangarOpen != null && OpenHangarListWindow() is { } window)
        {
            lists.Add(new OriginalList(_hangarOpen, window, ScrollHangarList));
        }
        else if (_descWindow is { } box)
        {
            lists.Add(new OriginalList(DescriptionListKey, box, ScrollDescription));
        }
    }

    // The open list's window under its box. The thumb stands between its two arrows inside the
    // right edge, as long as the share of the list the window shows. Null while the items fit the
    // authored window.
    private ListWindow? OpenHangarListWindow()
    {
        if (_hangarOpen == null)
        {
            return null;
        }

        string section = _host.Screen == OriginalScreen.HangarInventory ? InventorySection : SectionOf(_host.Screen);
        if (_layout.Screen(section)?.Widget(_hangarOpen) is not { } widget || HangarListFor(_hangarOpen) is not { } list)
        {
            return null;
        }

        var box = HubBox(widget);
        int count = list.Items.Count;
        var grid = OpenDecalGrid(widget, list);
        int window = grid?.Window ?? Math.Clamp(widget.Int("TotalDisplayed", count), 1, Math.Max(1, count));
        if (count <= window)
        {
            return null;
        }

        var upSize = StripSize(StripArt(widget.Art, 3), FallbackArrowWidth, FallbackArrowHeight);
        var downSize = StripSize(StripArt(widget.Art, 4), FallbackArrowWidth, FallbackArrowHeight);
        var thumb = StripSize(StripArt(widget.Art, 0, 1), upSize.Width, 11f);
        if (grid is { } cells)
        {
            // A grid's window is measured in grid rows, so a wheel notch and a thumb drag move a
            // whole row of tiles. The thumb fills the track in proportion, which the still shows.
            int gridRows = (count + cells.Columns - 1) / cells.Columns;
            int lastRow = Math.Max(0, gridRows - cells.Rows);
            float track = cells.TrackHeight(upSize.Height, downSize.Height);
            float thumbHeight = ListWindow.ThumbHeightFor(track, cells.Rows, gridRows, thumb.Height);
            float trackTop = cells.TrackTop(upSize.Height);
            return new ListWindow(
                cells.X, cells.Y, cells.Width, cells.Height,
                cells.ScrollX, ListWindow.ThumbYFor(trackTop, track, thumbHeight, _hangarListTop / cells.Columns, lastRow),
                thumb.Width, thumbHeight,
                trackTop, track, gridRows, cells.Rows, Math.Clamp(_hangarListTop / cells.Columns, 0, lastRow));
        }

        float top = box.Y + box.Height;
        float height = window * box.Height;
        float trackHeight = height - upSize.Height - downSize.Height;
        float plainThumb = ListWindow.ThumbHeightFor(trackHeight, window, count, thumb.Height);
        int first = Math.Clamp(_hangarListTop, 0, count - window);
        return new ListWindow(
            box.X, top, box.Width, height,
            box.X + box.Width - upSize.Width, ListWindow.ThumbYFor(top + upSize.Height, trackHeight, plainThumb, first, count - window), thumb.Width, plainThumb,
            top + upSize.Height, trackHeight, count, window, first);
    }

    // Puts the open list's window at top. A focused item the move would hide is pulled to the
    // window's nearer edge, since the window otherwise follows the focus back.
    private void ScrollHangarList(int top)
    {
        if (_hangarOpen == null || HangarListFor(_hangarOpen) is not { } list || OpenHangarListWindow() is not { } window)
        {
            return;
        }

        // A grid's window counts rows of tiles, so the top it is given is one of those rows.
        _hangarListTop = Math.Clamp(top, 0, window.LastTop) * list.Columns;
        int focused = _host.FocusedRow;
        if (focused >= 0 && focused < list.Items.Count)
        {
            _host.FocusedRow = Math.Clamp(
                focused, _hangarListTop, _hangarListTop + (window.Rows * list.Columns) - 1);
        }
    }

    // One dropdown's list over the feature: its items, the standing pick and what a pick does. For
    // the paint colours and decals it also carries the swatch or tile a row draws instead of words.
    private HangarList? HangarListFor(string key)
    {
        if (_hangar is not { } hangar)
        {
            return null;
        }

        var scratch = hangar.Scratch;
        switch (key)
        {
            case AirframeDropKey:
                var names = new string[HangarEconomy.Airframes.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    names[i] = hangar.AirframeName(i);
                }

                return PricedList(names, hangar.AirframeChosen ? scratch.Airframe : -1,
                    i => hangar.PickAirframe(i), hangar.BillWithAirframe);
            case EngineDropKey:
                var engines = new string[CustomPlaneDef.EngineNone + 1];
                for (int i = 0; i < engines.Length; i++)
                {
                    engines[i] = i == CustomPlaneDef.EngineNone ? hangar.Strings.Text(1165, "None") : hangar.EngineName(scratch.Airframe, i);
                }

                return PricedList(engines, scratch.Engine, i => hangar.SetEngine(i), hangar.BillWithEngine);
            case PatternDropKey:
                var patterns = hangar.WearablePatterns();
                var labels = new string[patterns.Count];
                for (int i = 0; i < labels.Length; i++)
                {
                    labels[i] = hangar.PatternLabel(patterns[i]);
                }

                return new HangarList(labels, IndexOf(patterns, scratch.PaintPattern), i => hangar.SetPattern(patterns[i]));
            case InventoryPlanesKey:
                var saved = new string[hangar.Saved.Count];
                for (int i = 0; i < saved.Length; i++)
                {
                    saved[i] = hangar.Saved[i].Name;
                }

                _inventoryIndex = Math.Clamp(_inventoryIndex, 0, Math.Max(0, saved.Length - 1));
                return new HangarList(saved, saved.Length == 0 ? -1 : _inventoryIndex, i => _inventoryIndex = i);
        }

        if (OriginalWidgets.Indexed(key, "AR_D_POINT") is { } zone && zone < 4)
        {
            var units = new string[CustomPlaneDef.MaxArmourUnits + 1];
            for (int i = 0; i < units.Length; i++)
            {
                units[i] = hangar.ArmourLabel(i);
            }

            return PricedList(units, hangar.ArmourUnits(zone), i => hangar.SetArmour(zone, i),
                i => hangar.BillWithArmour(zone, i));
        }

        if (OriginalWidgets.Indexed(key, "GN_D_GUN") is { } slot && slot < CustomPlaneDef.GunSlots)
        {
            var guns = new string[HangarFeature.GunCycleRows];
            for (int i = 0; i < guns.Length; i++)
            {
                guns[i] = hangar.GunCycleName(i);
            }

            return PricedList(guns, HangarFeature.GunCycleIndex(scratch.Guns[slot]), i => hangar.SetGun(slot, i),
                i => hangar.BillWithGun(slot, i));
        }

        if (OriginalWidgets.Indexed(key, "HP_D_POINT") is { } wing && wing < 2)
        {
            var counts = new string[CustomPlaneDef.MaxHardpointsPerWing + 1];
            for (int i = 0; i < counts.Length; i++)
            {
                counts[i] = hangar.HardpointsLabel(i);
            }

            return PricedList(counts, wing == 0 ? scratch.LeftHardpoints : scratch.RightHardpoints,
                i => hangar.SetHardpoints(wing, i), i => hangar.BillWithHardpoints(wing, i));
        }

        var tables = HangarPaintTables.Default;
        if (OriginalWidgets.Indexed(key, "PT_D_COLORS") is { } colourSlot && colourSlot < HangarPaintTables.Slots)
        {
            int rows = Math.Max(1, tables.Swatches.Count);
            var blanks = new string[rows];
            Array.Fill(blanks, string.Empty);
            return new HangarList(blanks, scratch.PaintColours[colourSlot], i => hangar.SetColour(colourSlot, i),
                Swatch: i => Tint(tables.Resolve(i, tables.DefaultShadeFor(i))));
        }

        if (OriginalWidgets.Indexed(key, "PT_D_SHADES") is { } shadeSlot && shadeSlot < HangarPaintTables.Slots)
        {
            int colour = scratch.PaintColours[shadeSlot];
            int rows = Math.Max(1, tables.ShadeCount(colour));
            var blanks = new string[rows];
            Array.Fill(blanks, string.Empty);
            return new HangarList(blanks, scratch.PaintShades[shadeSlot], i => hangar.SetShade(shadeSlot, i),
                Swatch: i => Tint(tables.Resolve(colour, i)));
        }

        if (OriginalWidgets.Indexed(key, "PT_D_DECALS") is { } decalSlot && decalSlot < HangarPaintTables.Slots)
        {
            var decals = new string[HangarPaintTables.DecalCount];
            for (int i = 0; i < decals.Length; i++)
            {
                string name = tables.DecalName(i);
                decals[i] = name.Length == 0 ? i.ToString("00", CultureInfo.InvariantCulture) : name;
            }

            return new HangarList(
                decals, hangar.Decal(decalSlot), i => hangar.SetDecal(decalSlot, i), Tile: i => i,
                Columns: HangarPaintTables.DecalGridColumns);
        }

        return null;
    }

    // Sell asks first, the sell path's own two-button messagebox. That is langui 700 over the
    // plane's short airframe name and its value, Yes and No, on HANGAR.SCRIPT's 0x4 mask and so
    // the query icon. A refused sale (a reward aircraft, the two-plane floor) comes back as the
    // one-button 0x1 box in the feature's words, under the warning. Wallet-free the same box
    // asks the delete question instead, since a plane that cost nothing has no sale value.
    private void AskToSell()
    {
        if (_hangar == null || _inventoryIndex < 0 || _inventoryIndex >= _hangar.Saved.Count)
        {
            return;
        }

        var plane = _hangar.Saved[_inventoryIndex];
        string question = _hangar.Wallet == null
            ? $"This {_hangar.AirframeShortName(plane.Airframe)} will be removed from your hangar.  {DeleteQuestion}"
            : _hangar.Strings.Format(700, _hangar.AirframeShortName(plane.Airframe), HangarEconomy.Price(plane).Total.Cost);
        if (question.Length == 0)
        {
            question = $"Your {_hangar.AirframeShortName(plane.Airframe)} is worth ${HangarEconomy.Price(plane).Total.Cost}. Are you sure you want to sell it?";
        }

        _host.RaiseDialog(
            Fill(question.Replace("<B>", string.Empty).Replace("<b>", string.Empty)),
            DialogIcon.Query,
            Yes(() =>
            {
                if (_hangar.DeleteSaved(plane.Name))
                {
                    _host.RefreshRosterFromStore();
                    return;
                }

                string refusal = _hangar.Message;
                _hangar.ClearMessage();
                _host.RaiseDialog(refusal, DialogIcon.Warning, Ok());
            }),
            No());
    }

    // Export answers with the screen's own confirmation (langui 702 over the plane's short
    // airframe name) and writes nothing. The original's export copies the record into the store
    // Multiplayer and Instant Action read. Both presentations here already pick from that one
    // store, so the plane is offered to them the moment it is built.
    private void ExportPicked()
    {
        if (_hangar == null || _inventoryIndex < 0 || _inventoryIndex >= _hangar.Saved.Count)
        {
            return;
        }

        string name = _hangar.AirframeShortName(_hangar.Saved[_inventoryIndex].Airframe);
        string message = _hangar.Strings.Format(702, name);
        if (message.Length == 0)
        {
            message = $"Your {name} has been exported and is now available for Multiplayer and Instant Action missions.";
        }

        _host.RaiseDialog(Fill(message), DialogIcon.Warning, Ok());
    }

    public MenuExit? Activate(OriginalRow row)
    {
        if (_hangar == null)
        {
            return null;
        }

        int colon = row.Key.IndexOf(':');
        if (colon > 0)
        {
            string prefix = row.Key[..colon];
            string suffix = row.Key[(colon + 1)..];
            if (suffix is "up" or "down")
            {
                // An arrow steps one row, which on a grid is a whole row of tiles.
                int step = HangarListFor(prefix)?.Columns ?? 1;
                _hangarListTop += suffix == "up" ? -step : step;
                return null;
            }

            if (HangarListFor(prefix) is { } list)
            {
                list.Select(int.Parse(suffix, CultureInfo.InvariantCulture));
                _hangarOpen = null;
                _host.FocusKey(prefix);
                RaisePendingDefaultsAsk();
            }

            return null;
        }

        switch (row.Key)
        {
            case NameFieldKey:
            case HubNameFieldKey:
                // An edit box takes the focus and nothing else; the typing is the screen's.
                return null;
            case NameDefaultsKey:
                _hangarDefaults = !_hangarDefaults;
                return null;
            case NameOkKey:
                AcceptName();
                return null;
            case NameCancelKey:
            case CancelBuildKey:
                CancelHangar();
                return null;
            case SellPlanesKey:
                _hangarOpen = null;
                _host.Open(OriginalScreen.HangarInventory);
                return null;
            case ReadyKey:
                ShowHangarScreen(OriginalScreen.HangarPurchase);
                return null;
            case PurchaseNowKey:
                PurchaseNow();
                return null;
            case InventorySellKey:
                AskToSell();
                return null;
            case InventoryExportKey:
                ExportPicked();
                return null;
            case InventoryDoneKey:
                ShowHangarScreen(_hangarTab);
                return null;
        }

        if (TabOf(row.Key) is { } tab)
        {
            ShowHangarScreen(tab);
            return null;
        }

        if (row.Kind == OriginalRowKind.Dropdown && HangarListFor(row.Key) is { } open && open.Items.Count > 0)
        {
            _hangarOpen = row.Key;
            int current = Math.Max(0, open.Current);
            // A grid opens on the row its pick stands in, which is where the reference still shows
            // the picked decal. A text list opens at its head, and the window follows the focus.
            _hangarListTop = open.Columns > 1 ? current - (current % open.Columns) : 0;
            _host.FocusedRow = current;
        }

        return null;
    }

    // The hangar screens as drawn. No strokes: the blueprint pages draw their outlines as fills and
    // pictures, the pen being the campaign scrapbook's alone.
    public void Compose(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        if (_hangar == null)
        {
            return;
        }

        switch (_host.Screen)
        {
            case OriginalScreen.PlaneName:
                ComposePlaneName(rows, focus, layers);
                return;
            case OriginalScreen.HangarInventory:
                ComposeInventory(rows, focus, layers);
                return;
        }

        ComposeHubChrome(rows, focus, layers);
        if (_host.Screen == OriginalScreen.HangarPurchase)
        {
            ComposePurchasePage(layers.Lines);
        }
        else
        {
            ComposeTabPage(rows, focus, layers);
        }

        // With a list up the rows are its own. The page under it is drawn from the closed widgets,
        // and the list becomes an overlay over the finished page.
        IReadOnlyList<OriginalRow> widgets = rows;
        int widgetFocus = focus;
        int widgetPressed = _host.PressedRow;
        if (_hangarOpen != null)
        {
            var closed = new List<OriginalRow>();
            BuildHubRows(closed);
            widgets = closed;
            widgetFocus = -1;
            widgetPressed = -1;
            for (int i = 0; i < closed.Count; i++)
            {
                if (closed[i].Key == _hangarOpen)
                {
                    widgetFocus = i;
                }
            }
        }

        for (int i = 0; i < widgets.Count; i++)
        {
            ComposeHangarRow(widgets[i], i == widgetFocus, i == widgetPressed, i, layers);
        }

        if (_hangarOpen != null)
        {
            ComposeOpenList(rows, focus, layers.Overlays);
        }
    }

#pragma warning restore SA1202

    private void ComposePlaneName(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        var screen = _layout.Screen(PlaneNameSection);
        if (screen == null)
        {
            return;
        }

        AddPane(screen, layers.Backdrop, "PX_P_BACKGROUND");
        AddPane(screen, layers.Backdrop, PlaneNamePaneKey);
        var lines = layers.Lines;
        int first = lines.Count;
        AddHangarText(screen, lines, "PN_T_TITLE", HubLabelFont, BoardInk.Row);
        AddHangarText(screen, lines, "PN_T_DEFAULT", HubTextFont, BoardInk.Row);
        var origin = PaneOrigin(screen, PlaneNamePaneKey);
        for (int i = first; i < lines.Count; i++)
        {
            lines[i] = lines[i] with { X = lines[i].X + origin.X, Y = lines[i].Y + origin.Y };
        }

        // The rows were placed on the pane when they were built, so they draw where they are.
        for (int i = 0; i < rows.Count; i++)
        {
            ComposeHangarRow(rows[i], i == focus, i == _host.PressedRow, i, layers);
        }
    }

    // The hub's own frame. It is the page background, the plane on the blueprint, and the name
    // and cost over it. The cash note stands over a wallet, the airframe figures under the
    // picture. Every figure stands on the build the row under the cursor would make, which is the
    // same build the blueprint already previews.
    private void ComposeHubChrome(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        var hub = _layout.Screen(PlaneConstructionSection);
        if (hub == null || _hangar is not { } hangar)
        {
            return;
        }

        AddPane(hub, layers.Backdrop, "PX_P_BACKGROUND");
        ComposePlanePicture(layers.Pictures);

        var lines = layers.Lines;
        var bill = HubBill(rows, focus);
        int? standing = HubAirframe();
        // The two figures the script arms off its own checks. The cost reddens past the wallet,
        // the weight past the capacity, one literal red on every screen. A wallet-free door checks
        // no funds and reddens neither (docs/org/hangar.md, "The two red figures").
        bool overFunds = hangar.Unaffordable(bill.Total.Cost);
        bool pendingWeight = standing == null || PreviewingAirframe(rows, focus);
        bool overWeight = !pendingWeight && bill.Verdict == PurchaseVerdict.Overweight;
        AddHangarText(hub, lines, "PX_T_PLANENAME", HubLabelFont, BoardInk.Dialog);
        if (hub.Widget("PX_T_PLANECOST") is { } cost)
        {
            lines.Add(new BoardLine(Fill(cost.Text ?? "PLANE COST:  $%1!d!", bill.Total.Cost), cost.Int("X"), cost.Int("Y"), 0f,
                HubLabelFont, overFunds ? BoardInk.Alarm : BoardInk.Dialog));
        }

        // The cash note's two authored rows, on every tab and the totals page and on both doors.
        // A wallet-free build wears the export door's own funds. The figure takes the problems
        // ink off the same answer the cost line reddens on, so the pair never disagree on screen.
        AddHangarText(hub, lines, "PX_T_CASHTITLE", HubTextFont, BoardInk.Row);
        if (hub.Widget("PX_T_CASH") is { } cash)
        {
            int funds = hangar.Wallet?.Funds ?? ExportFunds;
            lines.Add(new BoardLine("$" + funds.ToString(CultureInfo.InvariantCulture), cash.Int("X"), cash.Int("Y"),
                cash.Int("Width"), HubLabelFont, overFunds ? BoardInk.Heading : BoardInk.Row, -1, false, Justify(cash)));
        }

        if (hub.Widget("PX_T_AIRFRAME") is { } airframe)
        {
            string name = standing is { } chosen ? hangar.AirframeName(chosen) : string.Empty;
            lines.Add(new BoardLine(Fill(airframe.Text ?? "AIRFRAME: %1!s!", name), airframe.Int("X"), airframe.Int("Y"),
                airframe.Int("Width"), HubTextFont, BoardInk.Dialog));
        }

        if (hub.Widget("PX_T_WEIGHTCAPACITY") is { } capacity)
        {
            lines.Add(new BoardLine(Fill(capacity.Text ?? "WEIGHT CAPACITY: %1!d! lbs.", bill.Capacity), capacity.Int("X"), capacity.Int("Y"),
                capacity.Int("Width"), HubTextFont, BoardInk.Dialog));
        }

        if (hub.Widget("PX_T_CURRENTWEIGHT") is { } weight)
        {
            string text = pendingWeight
                ? Fill(hangar.Strings.Text(PendingWeightString, "CURRENT WEIGHT: Pending"))
                : Fill(weight.Text ?? "CURRENT WEIGHT: %1!d! lbs.", bill.Total.Weight);
            lines.Add(new BoardLine(text, weight.Int("X"), weight.Int("Y"), weight.Int("Width"), HubTextFont,
                overWeight ? BoardInk.Alarm : BoardInk.Dialog));
        }

        if (hub.Widget("PX_T_AGILITY") is { } agility)
        {
            lines.Add(new BoardLine("AGILITY:  " + Rating(bill.AgilityStars), agility.Int("X"), agility.Int("Y"), 0f, HubTextFont, BoardInk.Dialog));
            ComposeBar(hub, layers.Pictures, "PX_P_AD", bill.AgilityStars);
        }

        if (hub.Widget("PX_T_ARMOR") is { } armour)
        {
            lines.Add(new BoardLine("ARMOR:  " + Rating(bill.ArmourStars), armour.Int("X"), armour.Int("Y"), 0f, HubTextFont, BoardInk.Dialog));
            ComposeBar(hub, layers.Pictures, "PX_P_SI", bill.ArmourStars);
        }
    }

    // The plane on the blueprint. The airframe tab shows the focused airframe's blueprint, every
    // other tab the paint composite. That composite is the three region masks tinted with the
    // picked colours under the detail plate, from the pattern's own icon set. A pair with no set
    // falls back to the blueprint.
    private void ComposePlanePicture(List<BoardPicture> pictures)
    {
        var hangar = _hangar!;
        int airframe = hangar.AirframeChosen ? hangar.Scratch.Airframe : FocusedAirframe();
        if (_host.Screen == OriginalScreen.HangarAirframe || !hangar.AirframeChosen)
        {
            pictures.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, $"PX_{FocusedAirframe()}_BLUEPRINT.TGA"), PlaneX, PlaneY));
            return;
        }

        var scratch = hangar.Scratch;
        string prefix = $"PX_ICON_{airframe}_{scratch.PaintPattern}_";
        if (_host.Measure(prefix + "0.TGA") == null)
        {
            pictures.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, $"PX_{airframe}_BLUEPRINT.TGA"), PlaneX, PlaneY));
            return;
        }

        PaintColour[] colours = { scratch.Colour1, scratch.Colour2, scratch.Colour3 };
        for (int slot = 0; slot < colours.Length; slot++)
        {
            pictures.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, $"{prefix}{slot + 1}.TGA"), PlaneX, PlaneY, Tint: Tint(colours[slot])));
        }

        pictures.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, prefix + "0.TGA"), PlaneX, PlaneY));
    }

    // The airframe the airframe tab previews: the focused list item while the list is open, else
    // the standing pick, else the first row.
    private int FocusedAirframe() => HubAirframe() ?? 0;

    // The airframe the hub's figures stand on. It is the row under the cursor in the open airframe
    // list, else the pick already taken. Null before a pilot has chosen one, which is what leaves
    // the weight line pending and both figures plain.
    private int? HubAirframe()
    {
        var hangar = _hangar!;
        if (_host.Screen == OriginalScreen.HangarAirframe && _hangarOpen == AirframeDropKey)
        {
            int focus = _host.FocusedRow;
            if (focus >= 0 && focus < HangarEconomy.Airframes.Length)
            {
                return focus;
            }
        }

        return hangar.AirframeChosen ? hangar.Scratch.Airframe : null;
    }

    // Whether the figures stand on an airframe row the cursor is only previewing. The set-airframe
    // callback leaves the weight line pending, so a candidate airframe is weighed against nothing.
    // ⚠ Every other list keeps its comparison; the original judges those against the capacity.
    private bool PreviewingAirframe(IReadOnlyList<OriginalRow> rows, int focus) =>
        _hangarOpen == AirframeDropKey && FocusedItem(rows, focus, AirframeDropKey) != null;

    // The build the hub's figures price. It is the scratch plane as it stands, or as it would
    // stand with the row under the cursor in an open list taken. Nothing is written, so leaving a
    // list without a pick puts every figure back.
    private HangarBill HubBill(IReadOnlyList<OriginalRow> rows, int focus)
    {
        var hangar = _hangar!;
        if (_hangarOpen is { } key && HangarListFor(key) is { BillWith: { } billWith }
            && FocusedItem(rows, focus, key) is { } row)
        {
            return billWith(row);
        }

        return hangar.Bill;
    }

    // One tab's right page. Its title, rules and labels stand at their authored places. The name
    // line takes the focused item, and the description box the decoded figures.
    private void ComposeTabPage(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        var page = _layout.Screen(SectionOf(_host.Screen));
        if (page == null || _hangar is not { } hangar)
        {
            return;
        }

        var lines = layers.Lines;
        foreach (var widget in page.Widgets)
        {
            if (widget.TypeCode == "P" && widget.Art.Count > 0 && widget.Frames == 1 && widget.Key != "PT_P_DECALS")
            {
                layers.Pictures.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, widget.Art[0]), widget.Int("X"), widget.Int("Y")));
            }
            else if (widget.TypeCode == "T" && widget.Key.EndsWith("_T_TITLE", StringComparison.Ordinal))
            {
                AddHangarText(page, lines, widget.Key, HubTitleFont, BoardInk.Heading);
            }
            else if (widget.TypeCode == "T" && widget.Text is { Length: > 0 })
            {
                AddHangarText(page, lines, widget.Key, HubLabelFont, BoardInk.Row);
            }
        }

        var scratch = hangar.Scratch;
        var stats = HangarEconomy.Airframes[scratch.Airframe];
        string name;
        HangarInfo info;
        switch (_host.Screen)
        {
            case OriginalScreen.HangarAirframe:
                int airframe = FocusedAirframe();
                var bare = HangarEconomy.Price(new CustomPlaneDef { Airframe = airframe });
                name = hangar.AirframeName(airframe);
                info = HangarDescriptions.Airframe(
                    hangar.Strings, airframe, Rating(bare.AgilityStars), Rating(bare.ArmourStars));
                break;
            case OriginalScreen.HangarEngine:
                int engine = FocusedItem(rows, focus, EngineDropKey) ?? scratch.Engine;
                name = engine == CustomPlaneDef.EngineNone ? hangar.Strings.Text(1165, "None") : hangar.EngineName(scratch.Airframe, engine);
                info = HangarDescriptions.Engine(hangar.Strings, scratch.Airframe, engine);
                break;
            case OriginalScreen.HangarArmor:
                name = hangar.Strings.Text(ArmourNameString, "ABOUT ARMOR");
                info = HangarDescriptions.Armour(hangar.Strings);
                break;
            case OriginalScreen.HangarGuns:
                for (int slot = 0; slot < CustomPlaneDef.GunSlots; slot++)
                {
                    if (page.Widget("GN_T_GUNTITLE" + slot.ToString(CultureInfo.InvariantCulture)) is { } title)
                    {
                        lines.Add(new BoardLine(hangar.Strings.Text(stats.SlotTitle(slot), $"Slot {slot + 1}"), title.Int("X"), title.Int("Y"), 0f, HubLabelFont, BoardInk.Row));
                    }
                }

                int gunSlot = FocusedSlot(rows, focus, "GN_D_GUN") ?? 0;
                string gunKey = "GN_D_GUN" + gunSlot.ToString(CultureInfo.InvariantCulture);
                int gunRow = FocusedItem(rows, focus, gunKey) ?? HangarFeature.GunCycleIndex(scratch.Guns[gunSlot]);
                var gun = HangarFeature.GunOfCycle(gunRow);
                name = hangar.GunCycleName(gunRow);
                info = HangarDescriptions.Gun(hangar.Strings, stats, gun, gunSlot);
                break;
            case OriginalScreen.HangarHardpoints:
                name = hangar.Strings.Text(HardpointsNameString, "ABOUT HARDPOINTS");
                info = HangarDescriptions.Hardpoints(hangar.Strings);
                break;
            default:
                name = hangar.Strings.Text(PaintNameString, "ABOUT PAINT AND DECALS");
                info = HangarDescriptions.Paint(hangar.Strings);
                break;
        }

        foreach (var widget in page.Widgets)
        {
            bool nameRow = widget.Key == "AF_T_AIRFRAME" || widget.Key.EndsWith("NAME", StringComparison.Ordinal);
            if (widget.TypeCode == "T" && string.IsNullOrEmpty(widget.Text) && nameRow)
            {
                lines.Add(new BoardLine(name, widget.Int("X"), widget.Int("Y"), widget.Int("Width"), HubLabelFont, BoardInk.Row));
            }
            else if (widget.TypeCode == "S")
            {
                ComposeDescription(widget, info, layers);
            }
        }
    }

    // The item under the focus in an open list of the given dropdown, or null.
    private int? FocusedItem(IReadOnlyList<OriginalRow> rows, int focus, string key)
    {
        if (_hangarOpen != key || focus < 0 || focus >= rows.Count)
        {
            return null;
        }

        return OriginalWidgets.Indexed(rows[focus].Key, key + ":");
    }

    // The totals page. It draws the column heads, one line per priced component at the authored
    // lines, and the totals row. The problems text takes the commit's own words.
    private void ComposePurchasePage(List<BoardLine> lines)
    {
        var page = _layout.Screen(PurchaseSection);
        if (page == null || _hangar is not { } hangar)
        {
            return;
        }

        foreach (string key in new[] { "PUR_T_TITLE", "PUR_T_ITEMTITLE", "PUR_T_WEIGHTTITLE", "PUR_T_COSTTITLE", "PUR_T_AIRFRAMETITLE", "PUR_T_ENGINETITLE", "PUR_T_ARMORTITLE", "PUR_T_GUNSTITLE", "PUR_T_HARDPOINTTITLE", "PUR_T_TOTALSTITLE" })
        {
            AddHangarText(page, lines, key, key == "PUR_T_TITLE" ? HubTitleFont : HubTextFont, key == "PUR_T_TITLE" ? BoardInk.Heading : BoardInk.Row);
        }

        var scratch = hangar.Scratch;
        var bill = hangar.Bill;
        var stats = HangarEconomy.Airframes[scratch.Airframe];
        PurchaseLine(page, lines, "PUR_T_AIRFRAME", hangar.AirframeName(scratch.Airframe), bill.Airframe);
        if (scratch.Engine != CustomPlaneDef.EngineNone)
        {
            PurchaseLine(page, lines, "PUR_T_ENGINE", hangar.EngineName(scratch.Airframe, scratch.Engine), bill.Engine);
        }

        var armour = new List<(string Name, CostWeight Line)>();
        for (int zone = 0; zone < 4; zone++)
        {
            int units = hangar.ArmourUnits(zone);
            if (units > 0)
            {
                int shown = units * HangarEconomy.ArmourUnitsPerStep;
                string label = hangar.Strings.Format(1191 + zone, shown);
                armour.Add((label.Length > 0 ? label : $"Zone {zone + 1}: {shown} units",
                    new CostWeight(units * HangarEconomy.ArmourStepCost, units * HangarEconomy.ArmourStepWeight)));
            }
        }

        PurchaseList(page, lines, "PUR_T_ARMOR", armour);
        var guns = new List<(string Name, CostWeight Line)>();
        for (int slot = 0; slot < CustomPlaneDef.GunSlots; slot++)
        {
            if (!scratch.Guns[slot].IsEmpty)
            {
                // The gun's own name alone: the slot title would wrap the authored 220-pixel list
                // column onto the next row.
                guns.Add((hangar.GunName(scratch.Guns[slot]), bill.Guns[slot]));
            }
        }

        PurchaseList(page, lines, "PUR_T_GUN", guns);
        var hardpoints = new List<(string Name, CostWeight Line)>();
        for (int wing = 0; wing < 2; wing++)
        {
            int count = wing == 0 ? scratch.LeftHardpoints : scratch.RightHardpoints;
            if (count > 0)
            {
                string label = hangar.Strings.Format(1176 + wing, count);
                hardpoints.Add((label.Length > 0 ? label : $"{(wing == 0 ? "Left" : "Right")} Wing: {count}",
                    new CostWeight(count * HangarEconomy.HardpointCost, count * HangarEconomy.HardpointWeight)));
            }
        }

        PurchaseList(page, lines, "PUR_T_HARDPOINT", hardpoints);
        PurchaseFigure(page, lines, "PUR_T_TOTALSWEIGHT", bill.Total.Weight.ToString(CultureInfo.InvariantCulture));
        PurchaseFigure(page, lines, "PUR_T_TOTALSCOST", "$" + bill.Total.Cost.ToString(CultureInfo.InvariantCulture));
        string problems = hangar.Message.Length > 0 ? hangar.Message : hangar.Refusal() ?? string.Empty;
        if (problems.Length > 0 && page.Widget("PUR_T_PROBLEMS") is { } trouble)
        {
            lines.Add(new BoardLine(problems, trouble.Int("X"), trouble.Int("Y"), trouble.Int("Width"), HubTextFont, BoardInk.Heading));
        }
    }

    // The inventory: the page, its title and prompt, the plane dropdown, the picked plane's icon,
    // name, figures and guns, and the three buttons.
    private void ComposeInventory(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        var screen = _layout.Screen(InventorySection);
        if (screen == null || _hangar is not { } hangar)
        {
            return;
        }

        AddPane(screen, layers.Backdrop, "HA_BACKGROUND");
        var lines = layers.Lines;
        AddHangarText(screen, lines, "HA_T_TITLE", HubTitleFont, BoardInk.Heading);
        AddHangarText(screen, lines, "HA_T_PROMPT", HubLabelFont, BoardInk.Row,
            hangar.Wallet == null ? DeletePrompt : null);
        if (_inventoryIndex >= 0 && _inventoryIndex < hangar.Saved.Count)
        {
            var plane = hangar.Saved[_inventoryIndex];
            var bill = HangarEconomy.Price(plane);
            if (screen.Widget("HA_P_PILOTPLANE") is { Art.Count: > 0 } icon)
            {
                layers.Pictures.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, icon.Art[0], Math.Max(1, icon.Frames)),
                    icon.Int("X"), icon.Int("Y"), Math.Clamp(plane.Airframe, 0, Math.Max(0, icon.Frames - 1))));
            }

            // ⚠ The plane line belongs at HA_T_PLANE. HANGAR.SCRIPT binds both its text objects
            // there and none to HA_T_PILOTPLANE, which the shipped build authors and never draws.
            // The wide row starts the line inside the box and wraps it onto the pull-down.
            InventoryLine(screen, lines, "HA_T_PLANE", PlaneLine(hangar, plane), HubLabelFont, InventoryPlaneRow);
            InventoryLine(screen, lines, "HA_T_AGILITYP", "AGILITY: " + Rating(bill.AgilityStars), HubTextFont);
            InventoryLine(screen, lines, "HA_T_ARMORP", "ARMOR: " + Rating(bill.ArmourStars), HubTextFont);
            // ⚠ Draw no Value row without a wallet. A sale is what 1258 prices, and a plane built on
            // the export door is never bought, so it is deleted rather than sold. What the cabin path
            // supplies and this one cannot is left out, never invented (docs/org/menu-inventory.md).
            if (hangar.Wallet != null)
            {
                string value = hangar.Strings.Format(1258, bill.Total.Cost);
                InventoryLine(screen, lines, "HA_T_VALUEP", value.Length > 0 ? value : $"Value: ${bill.Total.Cost}", HubTextFont);
            }

            if (screen.Widget("HA_A_PLANEWEAPONSP") is { } weapons)
            {
                float pitch = weapons.Int("Height", 16) + weapons.Int("ItemSpacing", 1);
                int shown = 0;
                for (int slot = 0; slot < CustomPlaneDef.GunSlots; slot++)
                {
                    if (!plane.Guns[slot].IsEmpty)
                    {
                        lines.Add(new BoardLine(hangar.GunName(plane.Guns[slot]), weapons.Int("X"), weapons.Int("Y") + (shown++ * pitch),
                            0f, DescFont, BoardInk.Row));
                    }
                }
            }
        }

        IReadOnlyList<OriginalRow> widgets = rows;
        int widgetFocus = focus;
        if (_hangarOpen != null)
        {
            var closed = new List<OriginalRow>();
            BuildInventoryRows(closed);
            widgets = closed;
            widgetFocus = 0;
        }

        for (int i = 0; i < widgets.Count; i++)
        {
            ComposeHangarRow(widgets[i], i == widgetFocus, _hangarOpen == null && i == _host.PressedRow, i, layers);
        }

        if (_hangarOpen != null)
        {
            ComposeOpenList(rows, focus, layers.Overlays);
        }
    }

    // One hangar row as drawn. It is the edit box with its caret, the checkbox from its eight-state
    // strip, and a dropdown's box with its value, swatch or tile. The strips go through the shared
    // plaque drawing. The paper buttons write their labels in the page's text ink, the tabs in
    // their own label tail.
    private void ComposeHangarRow(OriginalRow row, bool focused, bool pressed, int index, BoardLayers layers)
    {
        switch (row.Kind)
        {
            case OriginalRowKind.TextField:
                ComposeHangarField(row, focused, index, layers);
                return;
            case OriginalRowKind.Radio when row.Art != null:
                // An eight-state checkbox strip: the four button states unmarked, then the same four marked.
                int state = row.Enabled ? (pressed ? 3 : focused ? 2 : 1) : 0;
                layers.Plaques.Add(new BoardPlaque(row.Art, row.X, row.Y, index, (_hangarDefaults ? 4 : 0) + state, string.Empty, BoardInk.LabelNormal));
                return;
            case OriginalRowKind.Dropdown:
                ComposeHangarDropdown(row, focused, pressed, index, layers);
                return;
            case OriginalRowKind.TextButton when row.Art != null && TabOf(row.Key) == _host.Screen:
                // The standing tab is latched, not gated. It draws its depressed frame in that
                // frame's own ink, and stays hittable like any sibling. That frame is the full pale
                // tab, against the squat purple one the other five wear.
                layers.Plaques.Add(new BoardPlaque(row.Art, row.X, row.Y, index, DepressedFrame(row.Art.Frames), row.Label,
                    BoardInk.LabelActivate, row.Height - TabLabelLift));
                return;
            case OriginalRowKind.TextButton when row.Art != null && TabOf(row.Key) != null:
                // A tab standing by: its own state frame, and the tab bar's one label baseline,
                // which the latched tab above shares.
                int tabFrame = row.Enabled ? ComposedBoard.PlaqueFrame(row.Art.Frames, focused, pressed) : 0;
                layers.Plaques.Add(new BoardPlaque(row.Art, row.X, row.Y, index, tabFrame, row.Label,
                    row.Enabled ? ComposedBoard.PlaqueInk(focused, pressed) : BoardInk.Detail, row.Height - TabLabelLift));
                return;
            case OriginalRowKind.TextButton when row.Art != null && IsPaperButton(row.Key):
                int paperFrame = row.Enabled ? ComposedBoard.PlaqueFrame(row.Art.Frames, focused, pressed) : 0;
                layers.Plaques.Add(new BoardPlaque(row.Art, row.X, row.Y, index, paperFrame, row.Label, row.Enabled ? BoardInk.Row : BoardInk.Detail));
                return;
        }

        _host.ComposeGenericRow(row, focused, pressed, index, layers);
    }

    // An edit box in the three colours its own row authors. They are the frame where one is named,
    // the text in the box's colour, and the caret while the box holds the focus. Nothing is filled
    // behind it, the pane under the box carrying its ground.
    private void ComposeHangarField(OriginalRow row, bool focused, int index, BoardLayers layers)
    {
        var box = EditBox(row.Key);
        if (box != null && box.TryColor("FrameColor", out var frame))
        {
            layers.Fills.Add(new BoardFill(row.X, row.Y, row.Width, row.Height, frame.R, frame.G, frame.B, 1f, Border: true));
        }

        BoardCaret? caret = focused && box != null && box.TryColor("CursorColor", out var cursor)
            ? new BoardCaret(cursor.R, cursor.G, cursor.B, CaretWidth, Math.Max(1f, row.Height - (2f * CaretInset)))
            : null;
        layers.Lines.Add(new BoardLine(
            row.Label, row.X, row.Y + CaretInset, row.Width, HubItemFont,
            box != null && IsWhite(box, "TextColor") ? BoardInk.Dialog : BoardInk.Row, index, false, BoardJustify.Left, caret));
    }

    // The layout row an edit box was built from, whose own colour fields it draws in.
    private MenuLayoutWidget? EditBox(string key) =>
        _layout.Screen(_host.Screen == OriginalScreen.PlaneName ? PlaneNameSection : PlaneConstructionSection)?.Widget(key);

    private void ComposeHangarDropdown(OriginalRow row, bool focused, bool pressed, int index, BoardLayers layers)
    {
        var list = HangarListFor(row.Key);
        if (focused)
        {
            layers.Fills.Add(new BoardFill(row.X, row.Y, row.Width, row.Height, 0, 0, 0, 0.10f));
        }

        layers.Fills.Add(new BoardFill(row.X, row.Y, row.Width, row.Height, 0, 0, 0, 1f, Border: true));
        float arrowWidth = 0f;
        if (row.Art != null)
        {
            var size = StripSize(row.Art, FallbackArrowWidth, FallbackArrowHeight);
            arrowWidth = size.Width;
            int frame = row.Enabled ? ComposedBoard.PlaqueFrame(row.Art.Frames, focused, pressed) : 0;
            layers.Pictures.Add(new BoardPicture(row.Art, row.X + row.Width - size.Width, row.Y + ((row.Height - size.Height) / 2f), frame));
        }

        if (list != null && list.Current >= 0)
        {
            if (list.Swatch?.Invoke(list.Current) is { } swatch)
            {
                layers.Fills.Add(new BoardFill(row.X + 2f, row.Y + 2f, Math.Max(1f, row.Width - arrowWidth - 4f), row.Height - 4f, swatch.R, swatch.G, swatch.B));
                return;
            }

            if (list.Tile?.Invoke(list.Current) is { } tile && DecalArt() is { } sheet)
            {
                var tileSize = StripSize(sheet, 66f, 66f);
                layers.Pictures.Add(new BoardPicture(sheet, row.X + 2f, row.Y + ((row.Height - tileSize.Height) / 2f), tile));
                return;
            }
        }

        layers.Lines.Add(new BoardLine(row.Label, row.X + 4f, row.Y + 1f, Math.Max(1f, row.Width - arrowWidth - 6f), HubItemFont,
            focused ? BoardInk.RowFocused : BoardInk.Row, index));
    }

    // The decal sheet, the paint section's own fifty-frame pane.
    private BoardArt? DecalArt()
    {
        var pane = _layout.Screen("Paint")?.Widget("PT_P_DECALS");
        return pane is { Art.Count: > 0 } ? new BoardArt(BoardArtLibrary.Ui, pane.Art[0], Math.Max(1, pane.Frames)) : null;
    }

    // An open list as the overlay over the finished page. It draws the visible items on a paper
    // panel, the focused one marked, swatches and tiles where the list has them, and the arrows.
    private void ComposeOpenList(IReadOnlyList<OriginalRow> rows, int focus, List<BoardPanel> overlays)
    {
        if (_hangarOpen == null || HangarListFor(_hangarOpen) is not { } list)
        {
            return;
        }

        var panelFills = new List<BoardFill>();
        var panelLines = new List<BoardLine>();
        var panelPictures = new List<BoardPicture>();
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

        // The paper runs the authored box's full width, the scroll column included. The rows gave
        // that column up so their bands and their words keep off the chrome, not the panel. A grid
        // stands on its own rectangle instead, the tiles and the chrome inside its frame.
        string section = _host.Screen == OriginalScreen.HangarInventory ? InventorySection : SectionOf(_host.Screen);
        var opened = _layout.Screen(section)?.Widget(_hangarOpen);
        var grid = opened == null ? null : OpenDecalGrid(opened, list);
        float panelWidth = opened != null ? HubBox(opened).Width : width;
        if (grid is { } panel)
        {
            left = panel.X;
            top = panel.Y;
            bottom = panel.Y + panel.Height;
            panelWidth = panel.Width;
        }

        if (top < bottom)
        {
            panelFills.Add(new BoardFill(left, top, panelWidth, bottom - top, 0xE6, 0xDA, 0xBE, 0.97f));
            panelFills.Add(new BoardFill(left, top, panelWidth, bottom - top, 0, 0, 0, 1f, Border: true));
        }

        var sheet = DecalArt();
        for (int i = 0; i < rows.Count; i++)
        {
            var item = rows[i];
            if (!item.Visible)
            {
                continue;
            }

            if (item.Kind == OriginalRowKind.Button && item.Art != null)
            {
                int frame = item.Enabled ? ComposedBoard.PlaqueFrame(item.Art.Frames, i == focus, i == _host.PressedRow) : 0;
                panelPictures.Add(new BoardPicture(item.Art, item.X, item.Y, frame));
                continue;
            }

            int index = OriginalWidgets.Indexed(item.Key, _hangarOpen + ":") ?? -1;
            if (i == focus)
            {
                // A tile keeps its own colours, so the grid marks the row under the focus with a
                // frame rather than a wash over the art. That frame is the one the still draws
                // round the picked decal.
                panelFills.Add(grid == null
                    ? new BoardFill(item.X, item.Y, item.Width, item.Height, 0, 0, 0, 0.12f)
                    : new BoardFill(item.X, item.Y, item.Width, item.Height, 0, 0, 0, 1f, Border: true));
            }

            if (index >= 0 && list.Swatch?.Invoke(index) is { } swatch)
            {
                panelFills.Add(new BoardFill(item.X + 2f, item.Y + 2f, item.Width - 4f, item.Height - 4f, swatch.R, swatch.G, swatch.B));
                continue;
            }

            if (index >= 0 && list.Tile?.Invoke(index) is { } tile && sheet != null)
            {
                var tileSize = StripSize(sheet, DecalTile, DecalTile);
                if (grid != null)
                {
                    // In the grid the tile is the whole cell and its name is not written: the
                    // still shows fifty pictures and no words.
                    panelPictures.Add(new BoardPicture(sheet, item.X, item.Y, tile));
                    continue;
                }

                panelPictures.Add(new BoardPicture(sheet, item.X + 2f, item.Y + ((item.Height - tileSize.Height) / 2f), tile));
                panelLines.Add(new BoardLine(item.Label, item.X + tileSize.Width + 6f, item.Y + 2f, Math.Max(1f, item.Width - tileSize.Width - 8f), DescFont, BoardInk.Row, i));
                continue;
            }

            panelLines.Add(new BoardLine(item.Label, item.X + 4f, item.Y + 1f, item.Width - 8f, HubItemFont,
                i == focus ? BoardInk.RowFocused : BoardInk.Row, i));
        }

        if (OpenHangarListWindow() is { } window && opened != null && StripArt(opened.Art, 0, 1) is { } thumb)
        {
            // The thumb fills its track in proportion. The tile is drawn stretched to the height
            // the window gives it rather than at the art's own.
            panelPictures.Add(new BoardPicture(thumb, window.ThumbX, window.ThumbY, Height: window.ThumbHeight));
        }

        overlays.Add(new BoardPanel(panelFills, panelPictures, panelLines));
    }

    // The messagebox script's own answer words, read through the hangar feature's own string
    // table. The one-button box takes langui 100 (OK), the two-button pair 102 and 103 (Yes, No).
    // The 0x8 box takes 102, 103 and 101 across all three slots.
    private OriginalDialogAnswer Ok(Action? run = null) =>
        new(DialogOkKey, CampaignBoards.DialogCenterKey, DialogWord(100, "OK"), run);

    private OriginalDialogAnswer Yes(Action run) =>
        new(DialogYesKey, CampaignBoards.DialogLeftKey, DialogWord(102, "Yes"), run);

    private OriginalDialogAnswer No() =>
        new(DialogNoKey, CampaignBoards.DialogRightKey, DialogWord(103, "No"), null);

    private OriginalDialogAnswer NoCentred(Action run) =>
        new(DialogNoKey, CampaignBoards.DialogCenterKey, DialogWord(103, "No"), run);

    private OriginalDialogAnswer Cancel(Action run) =>
        new(DialogCancelKey, CampaignBoards.DialogRightKey, DialogWord(101, "Cancel"), run);

    private string DialogWord(int id, string fallback)
    {
        string word = _hangar?.Strings.Text(id, fallback) ?? fallback;
        return word.Length > 0 ? word : fallback;
    }

    // The decal picker's open window. It carries the panel's corner on the page and the tile size
    // the sheet measures. It also carries its five columns over the authored TotalDisplayed rows,
    // and the scroll column inside the panel's right edge. Everything a grid draws and hits comes
    // off this.
    private readonly record struct DecalGrid(float X, float Y, float Tile, int Columns, int Rows, float Arrow)
    {
        internal int Window => Columns * Rows;

        internal float Width => (Columns * Tile) + Arrow + (2f * DecalGridBorder);

        internal float Height => (Rows * Tile) + (2f * DecalGridBorder);

        internal float ScrollX => X + DecalGridBorder + (Columns * Tile);

        internal float TrackTop(float arrowHeight) => Y + DecalGridBorder + arrowHeight;

        internal float TrackHeight(float upHeight, float downHeight) =>
            Math.Max(1f, (Rows * Tile) - upHeight - downHeight);

        // Where the slot-th visible tile stands, counted across the row first.
        internal (float X, float Y) Cell(int slot) =>
            (X + DecalGridBorder + (slot % Columns * Tile), Y + DecalGridBorder + (slot / Columns * Tile));
    }

    // One dropdown's list, its standing pick, and what picking one does. A row draws a swatch or
    // decal tile in place of words where the list has one. The list also carries what the build
    // would cost and weigh with a row taken. A list whose rows change nothing priced carries no
    // bill. Columns over one makes the open list a grid, which the decal picker alone is. Its
    // window scrolls a row of Columns at a time, and its flat index is the grid's own row *
    // Columns + column.
    private sealed record HangarList(
        IReadOnlyList<string> Items, int Current, Action<int> Select,
        Func<int, BoardTint?>? Swatch = null, Func<int, int?>? Tile = null,
        Func<int, HangarBill>? BillWith = null, int Columns = 1);
}
