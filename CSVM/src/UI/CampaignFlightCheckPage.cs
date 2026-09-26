using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Flight;
using CSVM.Mech3;
using CSVM.Session;
using CSVM.UI.Menu;

namespace CSVM.UI;

/// <summary>What a flight check row does on Accept/Step. Info rows are display only.</summary>
internal enum FlightRowKind
{
    Info,
    ChangeAmmo,
    ChangePlane,
    ReturnToBriefing,
    FlyMission,
}

/// <summary>One gun group's picked calibre and whether the mount carries a second barrel, the
/// flight check's own reading of a plane's guns, resolved either from a hangar build
/// (<see cref="CustomPlaneDef.Guns"/>) or, for a plane with no build on file (the two starter
/// Devastators, a granted reward aircraft), from the airframe's stock fit
/// (<c>docs/formats/loadouts.md</c>).</summary>
internal readonly record struct FlightCheckGun(int Caliber, bool Twin);

/// <summary>One drawn row: its label, its <see cref="CampaignPage.Detail"/> text, what it does, the
/// ammo/plane slot (0 pilot, 1 wingman) a <see cref="FlightRowKind.ChangeAmmo"/>/
/// <see cref="FlightRowKind.ChangePlane"/> row acts on, and the airframe an info row's silhouette
/// (<see cref="ICampaignPage.RowArt"/>) is drawn from, if any.</summary>
internal readonly record struct FlightRow(
    string Text, string Detail, FlightRowKind Kind, int Slot = 0, int? Silhouette = null,
    string Guns = "", string Rockets = "");

/// <summary>Everything the flight check's rows are composed from that a press can change. It is
/// what says whether a redraw may reuse the rows it drew last time, so it holds identities and not
/// contents: a loadout edit hands the record fresh <see cref="OwnedPlane.Ammo"/> and
/// <see cref="OwnedPlane.Ordnance"/> arrays (<c>CampaignFeature.CommitLoadout</c>) and a hangar
/// visit hands the flow a freshly read profile (<c>CampaignFeature.Resume</c>), so both show up
/// here as a different object.</summary>
internal readonly record struct FlightCheckState(
    object? Profile, int MissionSeq, int Player, bool Wingman,
    bool PilotChangePlane, bool WingChangePlane,
    OwnedPlane? Pilot, object? PilotAmmo, object? PilotOrdnance,
    OwnedPlane? Wing, object? WingAmmo, object? WingOrdnance,
    bool Guest = false, bool Ready = false);

/// <summary>
/// The flight check screen (<c>Campaign Flight Check.png</c>, <c>FLIGHTCHECK.SCRIPT</c>,
/// <c>docs/formats/campaign-screens.md</c>): the mission title, a PILOT row and, when the mission's
/// <c>cm_sequence</c> wingman flag is set, a WINGMAN row, CHANGE AMMO per row (into
/// <see cref="CampaignScreen.Ammo"/>), CHANGE PLANE where the slot's own rules allow, RETURN TO BRIEFING
/// and FLY MISSION. The row list carries only these actionable items; each plane's dense text
/// (title, both eight-row lists) and the objectives note live in <see cref="Detail"/>, the split
/// <see cref="CampaignRosterPage"/> uses for its own descriptive text. CHANGE PLANE opens
/// <see cref="CampaignScreen.PlaneSelection"/> on the row's own slot, a guest's check and the
/// seated player's alike, so one place enforces the duplicate rule. With guests joined this one
/// page draws the whole sequence, a player at a time (<see cref="CampaignFlightField"/>).
/// </summary>
public sealed class CampaignFlightCheckPage : CampaignPage
{
    // The ordnance table's own "None" row (docs/formats/campaign-screens.md, the Ammo selection
    // decode): a pylon resolving to this row carries nothing, whether or not the pylon itself exists.
    private const int NoOrdnance = 11;

    // The two crew slots, the original's -1 and -2 throughout the campaign callbacks. Each carries
    // its own CHANGE PLANE answer: the script bars the pilot's button alone on the grant missions
    // (docs/formats/campaign-screens.md, "Plane change").
    private const int PilotSlot = 0;
    private const int WingmanSlot = 1;

    // The layout section every fixed element of this screen is read from, and the screen's own
    // title, FC_T_TITLE. ⚠ The title's x is pinned at the measured 138 and drawn left-justified
    // where the row says 132, centred in its 190; the row supplies y and width.
    private const string Section = CampaignLayout.FlightCheckSection;
    private const float TitleX = 138f;
    private const float TitleY = 36f;
    private const float TitleWidth = 190f;
    private const float TitleFont = 20f;

    // FC_T_MISSION, the mission's long name under the title.
    private const float MissionX = 136f;
    private const float MissionY = 70f;
    private const float MissionWidth = 400f;

    // The objectives note, at the authored positions of fc_t_objtitle and fc_t_objectives. The
    // title's face is the langui row's own [AB19I] tag: 19 pixels, italic.
    private const float ObjectivesTitleX = 558f;
    private const float ObjectivesTitleY = 80f;
    private const float ObjectivesTitleWidth = 130f;
    private const float ObjectivesTitleFont = 19f;
    private const float ObjectivesX = 554f;
    private const float ObjectivesY = 120f;
    private const float ObjectivesWidth = 206f;

    // The note's own face. The widget carries a 360000 height, the layout's "grows as it needs to"
    // sentinel, so it names no line pitch and this is measured off the reference screenshot rather
    // than decoded.
    private const float ObjectivesFont = 16f;

    // The two weapon tables' headings, at the authored y of fc_t_guntitlep and fc_t_guntitlew, and
    // the drop from a heading to its list. ⚠ The drop is pinned at the pilot pair's 17 for both
    // crews: FC_T_GUNLISTW and FC_T_ROCKETLISTW are authored at 400, one pixel lower.
    private const float GunColumnX = 240f;
    private const float RocketColumnX = 394f;
    private const float TableWidth = 150f;
    private const int PilotTableY = 167;
    private const int WingmanTableY = 382;
    private const int TableGap = 17;

    // FC_P_PILOTPLANE and FC_P_WINGPLANE, the crew silhouettes.
    private const float SilhouetteX = 144f;
    private const float PilotSilhouetteY = 190f;
    private const float WingmanSilhouetteY = 408f;

    // The weapon lists' face. The columns are 150 authored pixels wide and the ammunition names
    // carry their maker, so a row only fits at ten.
    private const float TableFont = 10f;

    // The plane-icon sheet stacks one silhouette per airframe, in the airframe id's own order.
    private const int SilhouetteFrames = 12;

    // Airframe id 0-10 to stock_loadouts.json's def key, the same row order
    // PlanePickerRoster.AirframeNodes and loadouts.md's stock table both use.
    private static readonly string[] AirframeDefKeys =
    {
        "pautogyro", "pavenger", "pbalmoral", "pbloodhawk", "pbrigand",
        "pdevastator", "pfirebrand", "pfury", "pkestrel", "ppeacemaker", "pwarhawk",
    };

    // The ammunition short names (langui 3360+index), in the same order loadouts.md's
    // "selectable.gun_ammo" carries them; index 4 is the profile record's own no-gun marker.
    private static readonly string[] AmmoShortNames =
        { "Slug", "Dum-dum", "Armor-piercing", "Explosive", "None" };

    // The ordnance table's short names (langui 3395+index), loadouts.md's
    // "selectable.pylon_ordnance" verbatim, index 11 being NoOrdnance.
    private static readonly string[] OrdnanceShortNames =
    {
        "Armor-piercing", "High explosive", "Flak", "Sonic", "Flash", "Rear flash",
        "Smoke", "Choker", "Beeper", "Seeker", "Torpedo", "None",
    };

    private static readonly CampaignProfileDef EmptyProfile = new() { Name = string.Empty };

    private readonly Dictionary<int, HangarArt?> _silhouettes = new();

    // The hangar build behind each aircraft name, and the profile it was read for (see BuildFor).
    private readonly Dictionary<string, CustomPlaneDef?> _builds = new();
    private readonly bool? _wingmanOverride;

    private CampaignProfileDef? _buildsFor;

    // The last composition and the state it was composed from (see Rows). Null rows mean nothing
    // has been composed yet, which no state matches.
    private List<FlightRow>? _rows;
    private FlightCheckState _rowsState;
    private IReadOnlyList<BoardLine>? _captions;
    private IReadOnlyList<BoardPicture>? _pictures;

    private CustomPlaneStore? _planes;
    private StockLoadouts? _stock;
    private Messages? _messages;

    // The objectives note, read once per mission rather than once per repaint: it reads files, and
    // the flow keeps one page instance across every screen it draws. -2 is "not yet read for any
    // mission", which no MissionSeq ever is (the cabin's own default is -1). The mission itself is
    // the flow's, since the plane selection screen asks the same question.
    private int _resolvedSeq = -2;
    private string _objectives = string.Empty;

    /// <summary>Binds the page to its flow. <paramref name="planes"/>/<paramref name="stock"/> let a
    /// test supply an explicit store and stock-loadout table instead of the Godot-resolved
    /// defaults; <paramref name="wingman"/> lets a test fix the mission's wingman flag without a
    /// real extraction to read <c>cm_sequence.zrd</c> from. Production callers (the
    /// <see cref="CampaignFlow.Registry"/> line) pass none of the three.</summary>
    public CampaignFlightCheckPage(
        CampaignFlow flow, CustomPlaneStore? planes = null, StockLoadouts? stock = null, bool? wingman = null)
        : base(flow)
    {
        _planes = planes;
        _stock = stock;
        _wingmanOverride = wingman;
    }

    /// <inheritdoc/>
    public override CampaignScreen Screen => CampaignScreen.FlightCheck;

    /// <inheritdoc/>
    public override string Title => Flow.Strings.Text(3450 + Flow.MissionSeq, $"Mission {Flow.MissionSeq + 1}");

    /// <inheritdoc/>
    public override int RowCount => Rows().Count;

    /// <summary>Each crew slot's aircraft silhouette, the airframe's own frame of the icon sheet,
    /// at <c>FC_P_PILOTPLANE</c> and <c>FC_P_WINGPLANE</c>.</summary>
    public override IReadOnlyList<BoardPicture> Pictures
    {
        // Rows() first: it drops this cache when the state behind the board has moved.
        get => _pictures ??= ComposePictures(Rows());
    }

    /// <summary>The title and mission-name widgets, the objectives note down the right of the
    /// board, each slot's GUNS and ROCKETS headings, and the focused aircraft's weapon block under
    /// its own pair, which is what those two tables are. Each is at its own <c>FC_T_*</c> row.</summary>
    public override IReadOnlyList<BoardLine> Captions
    {
        get => _captions ??= ComposeCaptions(Rows());
    }

    /// <summary>The screen's own heading, which names the player on a guest's check: the sequence
    /// runs in one window, so the heading is the only thing that says whose turn it is.</summary>
    public string Heading => Player > 0 ? $"FLIGHT CHECK P{Player + 1}" : "FLIGHT CHECK";

    private bool HasWingman => _wingmanOverride ?? Flow.MissionHasWingman;

    // Whose check this is: 0 the seated player, 1 and up a guest. A guest's page is this same
    // screen re-entered, so everything below reads the player rather than assuming the profile's
    // own aircraft; the seated player's page reads the profile aircraft.
    private int Player => Flow.Field.Current;

    // The flow's hangar store, or null off-engine: every plane then reads as its stock fit.
    private CustomPlaneStore? Planes => _planes ??= Flow.Planes;

    // The flow's stock table, or null off-engine: a plane then reads as fit-less rather than the
    // page touching Godot for the res:// default.
    private StockLoadouts? Stock => _stock ??= Flow.Stock;

    /// <inheritdoc/>
    public override BoardButtonRef Button(int row)
    {
        var rows = Rows();
        if (row < 0 || row >= rows.Count)
        {
            return BoardButtonRef.None;
        }

        var found = rows[row];
        return found.Kind switch
        {
            FlightRowKind.ChangeAmmo => new BoardButtonRef(BoardButton.ChangeAmmo, found.Slot),
            FlightRowKind.ChangePlane => new BoardButtonRef(BoardButton.ChangePlane, found.Slot),
            FlightRowKind.ReturnToBriefing => new BoardButtonRef(BoardButton.ReturnToBriefing),
            FlightRowKind.FlyMission => new BoardButtonRef(BoardButton.FlyMission),
            _ => BoardButtonRef.None,
        };
    }

    /// <inheritdoc/>
    public override HangarArt? RowArt(int row)
    {
        var rows = Rows();
        return row >= 0 && row < rows.Count && rows[row].Silhouette is { } airframe
            ? SilhouetteFor(airframe)
            : null;
    }

    /// <summary>The crew headings are text, not a control: a slot's name and aircraft are what the
    /// two action rows under them act on, so the cursor steps over them to reach those.</summary>
    public override bool Focusable(int row)
    {
        var rows = Rows();
        return row >= 0 && row < rows.Count && rows[row].Kind != FlightRowKind.Info;
    }

    /// <inheritdoc/>
    public override string RowText(int row)
    {
        var rows = Rows();
        return row >= 0 && row < rows.Count ? rows[row].Text : string.Empty;
    }

    /// <summary>The focused row's own description. The objectives note is NOT folded in here: it
    /// has its own authored widget on the board (see <see cref="Captions"/>), and a screen writes a
    /// block of text where the layout puts it rather than into the shell's hint band.</summary>
    public override string Detail(int row)
    {
        var rows = Rows();
        return row >= 0 && row < rows.Count ? rows[row].Detail : string.Empty;
    }

    /// <inheritdoc/>
    public override bool Accept(int row)
    {
        var rows = Rows();
        if (row < 0 || row >= rows.Count)
        {
            return false;
        }

        switch (rows[row].Kind)
        {
            case FlightRowKind.ChangeAmmo:
                Flow.SetAmmoSlot(rows[row].Slot);
                Flow.GoTo(CampaignScreen.Ammo);
                return true;
            case FlightRowKind.ChangePlane:
                Flow.SetPlaneSlot(rows[row].Slot);
                Flow.GoTo(CampaignScreen.PlaneSelection);
                return true;
            case FlightRowKind.ReturnToBriefing:
                Flow.Field.Rewind();
                Flow.GoTo(CampaignScreen.Briefing);
                return true;
            case FlightRowKind.FlyMission:
                // The last joined player's press launches; every earlier one advances to the
                // next check. GoTo on the screen already showing is the re-entry, the stack
                // returns to it rather than stacking a second copy, and the cursor opens afresh.
                if (Flow.Field.Advance())
                {
                    Flow.GoTo(CampaignScreen.FlightCheck);
                }
                else
                {
                    Flow.Request(CampaignExit.FlyMission);
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>Back on a guest's check returns to the player before them, the inverse of FLY
    /// MISSION. On the seated player's own it abandons the walk and is left unconsumed, so the
    /// flow leaves the screen.</summary>
    public override bool Back()
    {
        if (Flow.Field.Retreat())
        {
            return true;
        }

        Flow.Field.Rewind();
        return false;
    }

    private IReadOnlyList<BoardPicture> ComposePictures(List<FlightRow> rows)
    {
        var layout = Flow.Layout;
        var fallback = new BoardArt(BoardArtLibrary.Ui, "FC_PlaneIcons.Png", SilhouetteFrames);
        var pictures = new List<BoardPicture>(2);
        foreach (var row in rows)
        {
            if (row.Silhouette is { } airframe)
            {
                string key = row.Slot == 0 ? "FC_P_PILOTPLANE" : "FC_P_WINGPLANE";
                var (x, y) = layout.At(Section, key, SilhouetteX, row.Slot == 0 ? PilotSilhouetteY : WingmanSilhouetteY);
                pictures.Add(new BoardPicture(layout.Art(Section, key, fallback), x, y, airframe));
            }
        }

        return pictures;
    }

    private IReadOnlyList<BoardLine> ComposeCaptions(List<FlightRow> rows)
    {
        var layout = Flow.Layout;
        var (_, titleY, titleWidth) = layout.Box(Section, "FC_T_TITLE", TitleX, TitleY, TitleWidth);
        var (missionX, missionY, missionWidth) = layout.Box(Section, "FC_T_MISSION", MissionX, MissionY, MissionWidth);
        var lines = new List<BoardLine>
        {
            new(Heading, TitleX, titleY, titleWidth, TitleFont, BoardInk.Heading),
            new(Title, missionX, missionY, missionWidth, 15, BoardInk.Detail),
        };

        if (ObjectivesNote() is { Length: > 0 } note)
        {
            var (objTitleX, objTitleY, objTitleWidth) = layout.Box(
                Section, "FC_T_OBJTITLE", ObjectivesTitleX, ObjectivesTitleY, ObjectivesTitleWidth);
            var (objX, objY, objWidth) = layout.Box(
                Section, "FC_T_OBJECTIVES", ObjectivesX, ObjectivesY, ObjectivesWidth);
            lines.Add(new BoardLine(
                Flow.Strings.Text(1014, "Objectives").Trim(),
                objTitleX, objTitleY, objTitleWidth, ObjectivesTitleFont,
                BoardInk.Detail, Italic: true));
            lines.Add(new BoardLine(
                note, objX, objY, objWidth, ObjectivesFont, BoardInk.Detail, Italic: true));
        }

        foreach (var row in rows)
        {
            if (row.Kind != FlightRowKind.Info)
            {
                continue;
            }

            string crew = row.Slot == 0 ? "P" : "W";
            float fallbackY = row.Slot == 0 ? PilotTableY : WingmanTableY;
            var (gunX, gunY, gunWidth) = layout.Box(Section, "FC_T_GUNTITLE" + crew, GunColumnX, fallbackY, TableWidth);
            var (rocketX, rocketY, rocketWidth) = layout.Box(Section, "FC_T_ROCKETTITLE" + crew, RocketColumnX, fallbackY, TableWidth);
            lines.Add(new BoardLine("GUNS", gunX, gunY, gunWidth, 12, BoardInk.Heading));
            lines.Add(new BoardLine("ROCKETS", rocketX, rocketY, rocketWidth, 12, BoardInk.Heading));
            lines.Add(new BoardLine(row.Guns, gunX, gunY + TableGap, gunWidth, TableFont, BoardInk.Row));
            lines.Add(new BoardLine(row.Rockets, rocketX, rocketY + TableGap, rocketWidth, TableFont, BoardInk.Row));
        }

        return lines;
    }

    // ⚠ Do not compose the rows per call. The board recomposes every frame and asks for them a
    // dozen times over, so anything built here is built at frame rate; the screen spent most of a
    // frame on it. FlightCheckState is what a press can change, so an untouched board reuses the
    // rows, the captions and the pictures it drew last time.
    private List<FlightRow> Rows()
    {
        var state = CurrentState();
        if (_rows is { } cached && state == _rowsState)
        {
            return cached;
        }

        _rowsState = state;
        _captions = null;
        _pictures = null;
        _rows = BuildRows();
        return _rows;
    }

    private FlightCheckState CurrentState()
    {
        var profile = Flow.Profile ?? EmptyProfile;
        var pilot = Player > 0 ? Flow.Field.Plane(Player) : PlaneAt(profile, profile.SelectedPlane);
        var wing = Player == 0 && HasWingman ? PlaneAt(profile, profile.WingmanPlane) : null;
        return new FlightCheckState(
            Flow.Profile, Flow.MissionSeq, Player, HasWingman,
            Flow.Feature.ChangePlaneAllowed(PilotSlot), Flow.Feature.ChangePlaneAllowed(WingmanSlot),
            pilot, pilot?.Ammo, pilot?.Ordnance,
            wing, wing?.Ammo, wing?.Ordnance,
            Flow.Feature.IsGuest, Flow.Feature.GuestReady);
    }

    // Every row this screen draws, composed from the profile and the mission's wingman flag: the
    // PILOT block, its two action rows, the WINGMAN block and its own two action rows when the
    // mission carries a wingman, then RETURN TO BRIEFING and FLY MISSION. A guest's own check draws
    // one PILOT block and no wingman: the wingman is the seated profile's.
    private List<FlightRow> BuildRows()
    {
        var profile = Flow.Profile ?? EmptyProfile;
        var rows = new List<FlightRow>();
        if (Player > 0)
        {
            // A guest picks out of the stock eleven and copies, so neither of FLIGHTCHECK.SCRIPT's
            // plane-change gates applies: both are rules about the seated profile's own aircraft.
            AddSlot(rows, Flow.Field.Plane(Player), slot: PilotSlot, heading: "PILOT", changePlane: true);
        }
        else
        {
            AddSlot(rows, PlaneAt(profile, profile.SelectedPlane), slot: PilotSlot, heading: "PILOT",
                changePlane: Flow.Feature.ChangePlaneAllowed(PilotSlot));
            if (HasWingman)
            {
                AddSlot(rows, PlaneAt(profile, profile.WingmanPlane), slot: WingmanSlot, heading: "WINGMAN",
                    changePlane: Flow.Feature.ChangePlaneAllowed(WingmanSlot));
            }
        }

        rows.Add(new FlightRow("RETURN TO BRIEFING", string.Empty, FlightRowKind.ReturnToBriefing));

        // A co-op guest's launch is the host's, so its last button says it is ready instead.
        string fly = !Flow.Feature.IsGuest ? "FLY MISSION" : Flow.Feature.GuestReady ? "CANCEL READY" : "READY";
        rows.Add(new FlightRow(fly, string.Empty, FlightRowKind.FlyMission));
        return rows;
    }

    private OwnedPlane? PlaneAt(CampaignProfileDef profile, int index) =>
        profile.Planes.Count > 0
            ? profile.Planes[Math.Clamp(index, 0, profile.Planes.Count - 1)]
            : null;

    private void AddSlot(List<FlightRow> rows, OwnedPlane? plane, int slot, string heading, bool changePlane)
    {
        if (plane == null)
        {
            rows.Add(new FlightRow($"{heading}   (no plane)", string.Empty, FlightRowKind.Info, slot));
            return;
        }

        var (guns, rockets) = WeaponColumns(plane);
        rows.Add(new FlightRow($"{heading}   {SlotLabel(plane)}",
            LoadoutBlock(plane), FlightRowKind.Info, slot, plane.Airframe, guns, rockets));
        rows.Add(new FlightRow("CHANGE AMMO", string.Empty, FlightRowKind.ChangeAmmo, slot));
        if (changePlane)
        {
            // The plaque carries its label alone, the original's own FC_B_CHANGEPLANE: the plane
            // it would name is already written on the info row directly above.
            rows.Add(new FlightRow("CHANGE PLANE", string.Empty, FlightRowKind.ChangePlane, slot));
        }
    }

    // A named aircraft and its airframe, except where the two are the same word: a guest's stock
    // record is named for its airframe, and "Devastator   Devastator" is not a second fact.
    private string SlotLabel(OwnedPlane plane)
    {
        string title = AirframeTitle(plane.Airframe);
        return plane.Name == title ? title : $"{plane.Name}   {title}";
    }

    private string AirframeTitle(int airframe) => Flow.Strings.Text(3000 + airframe, $"Airframe {airframe}");

    // IDS_GUNSHORTNAME, NOT the 3310 long name: the original's flight-check row reads " .50-cal.",
    // the maker's name belonging to the hangar and the ammo screen. The string owns its leading
    // space, which is the gap the reference screenshot draws between "1)" and the calibre, so the
    // fallback carries it too and no caller inserts one.
    private string CalibreName(int caliber)
    {
        int idx = Math.Clamp((caliber - 30) / 10, 0, 4);
        return Flow.Strings.Text(3320 + idx, $" .{30 + (idx * 10)}-cal.");
    }

    private string AmmoName(int index)
    {
        int i = Math.Clamp(index, 0, AmmoShortNames.Length - 1);
        return Flow.Strings.Text(3360 + i, AmmoShortNames[i]);
    }

    private string OrdnanceName(int index)
    {
        int i = Math.Clamp(index, 0, OrdnanceShortNames.Length - 1);
        return Flow.Strings.Text(3395 + i, OrdnanceShortNames[i]);
    }

    // A plane's eight gun rows and eight rocket rows, each drawn as its bare number plus whatever
    // that slot carries. Nothing is inserted between the two: the calibre string owns its own
    // leading space and the ordnance short name owns its lack of one, which is what makes the
    // original read "1)  .50-cal. Slug" beside "1)High explosive". An empty slot is its number.
    private (List<string> Guns, List<string> Rockets) WeaponRows(OwnedPlane plane)
    {
        var groups = ResolveGuns(plane);
        var (left, right) = ResolveHardpoints(plane);
        var gunLines = new List<string>(8);
        var rocketLines = new List<string>(8);
        for (int row = 0; row < 8; row++)
        {
            int n = row + 1;
            gunLines.Add($"{n}){GunRowText(groups, plane.Ammo, row)}");
            rocketLines.Add($"{n}){RocketRowText(plane.Ordnance, left, right, row)}");
        }

        return (gunLines, rocketLines);
    }

    // The same eight rows as two separate columns, which is how the screen's own two list widgets
    // carry them: one under GUNS, one under ROCKETS, never a single wide block.
    private (string Guns, string Rockets) WeaponColumns(OwnedPlane plane)
    {
        var (guns, rockets) = WeaponRows(plane);
        return (string.Join("\n", guns), string.Join("\n", rockets));
    }

    // The eight gun rows beside the eight rocket rows, one plane's full loadout block: the row's
    // own Detail text, for a caller with one column to fill rather than the board's two widgets.
    private string LoadoutBlock(OwnedPlane plane)
    {
        var (guns, rockets) = WeaponRows(plane);
        var lines = new List<string>(guns.Count);
        for (int row = 0; row < guns.Count; row++)
        {
            lines.Add($"{guns[row]}    {rockets[row]}");
        }

        return string.Join("\n", lines);
    }

    private string GunRowText(FlightCheckGun?[] guns, IReadOnlyList<int> ammo, int row)
    {
        int group = row >> 1;
        if (guns[group] is not { } gun || (row % 2 == 1 && !gun.Twin))
        {
            return string.Empty;
        }

        int ammoIndex = group < ammo.Count ? ammo[group] : 0;
        return $"{CalibreName(gun.Caliber)} {AmmoName(ammoIndex)}";
    }

    // ⚠ The stored pylon value is NOT a rocket-table row: CSVM's own encoding is one-based with 0
    // meaning "never picked" (CampaignLoadout.PylonRow). Reading it as a row drew every untouched
    // pylon as armor-piercing where the ammo screen and the original both say high explosive, and
    // shifted every deliberate pick by one.
    private string RocketRowText(IReadOnlyList<int> ordnance, int left, int right, int row)
    {
        bool exists = row < 4 ? row < left : row - 4 < right;
        if (!exists || row >= ordnance.Count)
        {
            return string.Empty;
        }

        int table = CampaignLoadout.PylonRow(ordnance[row]);
        return table == NoOrdnance ? string.Empty : OrdnanceName(table);
    }

    // A plane's four gun groups: a hangar build's own picks when one is on file under the plane's
    // name, else the airframe's stock fit (the two starter Devastators and every granted reward
    // aircraft carry no CustomPlaneStore entry, docs/org/hangar.md, "the campaign wallet").
    private FlightCheckGun?[] ResolveGuns(OwnedPlane plane)
    {
        var groups = new FlightCheckGun?[CustomPlaneDef.GunSlots];
        if (BuildFor(plane) is { } custom)
        {
            for (int i = 0; i < custom.Guns.Length && i < groups.Length; i++)
            {
                if (custom.Guns[i].Calibre is { } calibre)
                {
                    groups[i] = new FlightCheckGun(30 + (calibre * 10), custom.Guns[i].Twin);
                }
            }

            return groups;
        }

        if (StockFor(plane.Airframe) is { } stock)
        {
            foreach (var gun in stock.Guns)
            {
                int slot = gun.Slot - 1;
                if (slot >= 0 && slot < groups.Length)
                {
                    groups[slot] = new FlightCheckGun(gun.Caliber, gun.Markers.Count >= 2);
                }
            }
        }

        return groups;
    }

    // A plane's per-wing hardpoint counts, same build-or-stock precedence as the guns; the stock
    // half reuses HangarFlow's own count off the rig's wing split rather than re-deriving it.
    private (int Left, int Right) ResolveHardpoints(OwnedPlane plane)
    {
        if (BuildFor(plane) is { } custom)
        {
            return (custom.LeftHardpoints, custom.RightHardpoints);
        }

        return HangarFlow.StockWingCounts(StockFor(plane.Airframe)?.Hardpoints);
    }

    // The hangar build a record flies with, if any. ⚠ A guest's stock record is named for its
    // airframe, so it must never be looked up by name: a hangar plane called "Devastator" would
    // otherwise fit that guest with somebody else's build.
    // ⚠ Do not drop the cache: CustomPlaneStore.Load re-reads and re-parses the file on every
    // call, and the board asks for these rows dozens of times per repaint. It is keyed by profile
    // instance because a hangar visit replaces that instance (CampaignFeature.Resume).
    private CustomPlaneDef? BuildFor(OwnedPlane plane)
    {
        if (Flow.Field.IsStock(plane))
        {
            return null;
        }

        if (!ReferenceEquals(_buildsFor, Flow.Profile))
        {
            _builds.Clear();
            _buildsFor = Flow.Profile;
        }

        if (!_builds.TryGetValue(plane.Name, out var build))
        {
            build = Planes?.Load(plane.Name);
            _builds[plane.Name] = build;
        }

        return build;
    }

    private LoadoutDef? StockFor(int airframe) =>
        Stock?.For(AirframeDefKeys[Math.Clamp(airframe, 0, AirframeDefKeys.Length - 1)]);

    // The mission's objectives note (uiData 2022 / gosCallback 28, campaign-screens.md): the
    // display list's rows concatenated (docs/formats/objectives.md, "IDENTITY and the objectives
    // display"). ⚠ Do not number the lines here; the number is part of the MSG_ text. Read once
    // per mission and never once per repaint, since Captions asks for it every time the board is
    // composed. Empty, never a crash, when the data root, the mission or the file is unavailable.
    private string ObjectivesNote()
    {
        Resolve();
        return _objectives;
    }

    private string ReadObjectivesNote()
    {
        if (Flow.DataRoot is not { } root || Flow.Mission is not { } mission)
        {
            return string.Empty;
        }

        try
        {
            var script = ObjectiveScript.Load(
                SessionPaths.MissionZrdr(root, mission.ChapterFolder, mission.MissionFolder));
            var messages = MessagesFor(root);
            var lines = new List<string>();
            foreach (var identity in script.DisplayIdentities())
            {
                lines.Add(messages.Get(identity.MessageKey));
            }

            return string.Join("\n", lines);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException)
        {
            return string.Empty;
        }
    }

    // Reads the objectives note for whichever mission the flow now names, once. Keyed on the
    // sequence number rather than a bare "done" flag: the flow keeps this page instance, so the
    // same page draws the second mission after it drew the first.
    private void Resolve()
    {
        if (_resolvedSeq == Flow.MissionSeq)
        {
            return;
        }

        _resolvedSeq = Flow.MissionSeq;
        _objectives = ReadObjectivesNote();
    }

    private Messages MessagesFor(string root) =>
        _messages ??= Messages.Load(Path.Combine(root, "extracted", "messages.json"));

    // One airframe's blueprint TGA, the hangar's own plane preview path
    // (extracted/rof/ASSETS/GRAPHICS/PX_<n>_BLUEPRINT.TGA, HangarAirframePage), reused rather
    // than authoring new silhouette art. Decoded once and kept, including a miss.
    private HangarArt? SilhouetteFor(int airframe)
    {
        if (_silhouettes.TryGetValue(airframe, out var art))
        {
            return art;
        }

        art = null;
        if (Flow.DataRoot is { } root)
        {
            string path = Path.Combine(root, "extracted", "rof", "ASSETS", "GRAPHICS",
                $"PX_{airframe}_BLUEPRINT.TGA");
            if (TgaImage.TryLoad(path) is { } image)
            {
                art = new HangarArt(image, AirframeTitle(airframe));
            }
        }

        _silhouettes[airframe] = art;
        return art;
    }
}
