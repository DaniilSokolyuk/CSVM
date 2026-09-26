using System;
using System.Collections.Generic;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Hangar;
using CSVM.UI.Menu;

namespace CSVM.UI.Campaign;

/// <summary>What a plane selection row does. Every row on this screen is one of the four.</summary>
internal enum PlaneRowKind
{
    Pick,
    Export,
    Accept,
    Cancel,
}

/// <summary>One drawn row: what it does, and which crew slot it belongs to.</summary>
internal readonly record struct PlaneRow(PlaneRowKind Kind, int Slot = 0);

/// <summary>
/// The plane selection screen (<c>Campaign Flight Check Change Plane.png</c>,
/// <c>PLANESELECTION.SCRIPT</c>, <c>docs/formats/campaign-screens.md</c>): a drop-down, a
/// silhouette, four ratings and a gun and hardpoint list per crew slot, EXPORT per slot, and
/// ACCEPT/CANCEL SELECTIONS. The flight check's CHANGE PLANE opens it and names the slot the
/// cursor lands on (<see cref="CampaignFlow.PlaneSlot"/>). It edits live, restores the picks it
/// opened with on CANCEL, and writes them into the profile on ACCEPT.
/// A guest's check opens this screen over that guest's own roster
/// (<see cref="CampaignGuest.Choices"/>): one PILOT slot, no EXPORT, a refusal of its own since
/// langui 710 names a Pilot and a Wingman a guest's check has no concept of, and an ACCEPT that
/// moves the guest's pick and writes nothing.
/// </summary>
public sealed class CampaignPlaneSelectionPage : CampaignPage
{
    // The two crew slots, and the layout's own y for each block: PS_D_PILOTPLANE at 132 against
    // PS_D_WINGPLANE at 350, every other widget of the pair the same 218 pixels apart. ⚠ Except
    // PS_T_WINGPLANE, authored at 323; the wingman's plane line is pinned at the pilot's 106 + 218.
    private const int Slots = 2;
    private const float SlotDrop = 218f;

    // The authored geometry of one crew slot's block, the pilot's rows of [@PlaneSelection@], as
    // the fallback under each row read; the wingman's rows are the same keys with a W.
    private const string Section = CampaignLayout.PlaneSelectionSection;
    private const float ComboX = 138f;
    private const float ComboY = 132f;
    private const float ComboWidth = 271f;
    private const float ItemHeight = 15f;
    private const int ItemsDisplayed = 13;
    private const float HeadingY = 102f;
    private const float PlaneNameX = 236f;
    private const float PlaneNameY = 106f;
    private const float SilhouetteX = 444f;
    private const float SilhouetteY = 138f;
    private const float RatingX = 430f;
    private const float RatingY = 228f;
    private const float RatingPitch = 17f;
    private const float RatingWidth = 140f;
    private const float WeaponsX = 578f;
    private const float WeaponsWidth = 160f;

    // The screen's own chrome, at PS_T_TITLE and PS_T_MISSIONINFO. ⚠ The title is drawn
    // left-justified where its row says centred in its 190; the justification is pinned.
    private const float TitleX = 132f;
    private const float TitleY = 36f;
    private const float TitleWidth = 190f;
    private const float MissionX = 136f;
    private const float MissionY = 70f;
    private const float MissionWidth = 500f;
    private const float TitleFont = 20f;
    private const float BodyFont = 12f;

    // The plane-icon sheet stacks one silhouette per airframe, in the airframe id's own order.
    private const int SilhouetteFrames = 12;

    // IDS_PS_B_EXPORT, the plaque's own words, and the weapon column's hardpoint row.
    private const int ExportLabel = 1139;
    private const int HardpointsLabel = 1008;

    // IDS_PS_CANTFLYSAMEPLANE, the words the seated player's refused pick is answered with.
    private const int SamePlaneRefusal = 710;

    // A guest's refusal, which is ours rather than the original's: 710 names Pilot and Wingman,
    // and a guest's check has neither, only the other humans on the field.
    private const string GuestRefusal = "Each player must fly a different plane.";

    // The words a finished export is answered with. ⚠ Its %1!s! is the airframe's title, not the
    // plane's name: the original's dialog reads "Your Devastator has been exported" for a wingman
    // aircraft named "The Knave" (OriginalScreenshots/Campaign Flight Check Change Plane Export
    // dialog.png).
    private const int ExportedMessage = 702;

    // The four rating words, langui 501-505 in the order Poor to Excellent.
    private static readonly string[] RatingWords = { "Poor", "Fair", "Average", "Good", "Excellent" };

    private static readonly CampaignProfileDef EmptyProfile = new() { Name = string.Empty };

    private readonly CampaignCombo[] _combos = new CampaignCombo[Slots];
    private readonly int[] _opened = new int[Slots];
    private readonly bool? _wingmanOverride;

    // Whose roster the combos were filled from (a profile, or one guest) and how long it was. A
    // different one refills them, since a combo's entries are a statement about that roster.
    private object? _filledFor;
    private int _filledCount = -1;

    /// <summary>Binds the page to its flow. <paramref name="wingman"/> lets a test fix the mission's
    /// wingman flag without an extraction to read <c>cm_sequence.zrd</c> from; production callers
    /// (the <see cref="CampaignFlow.Registry"/> line) pass none.</summary>
    public CampaignPlaneSelectionPage(CampaignFlow flow, bool? wingman = null)
        : base(flow)
    {
        _wingmanOverride = wingman;
        var layout = flow.Layout;
        for (int slot = 0; slot < Slots; slot++)
        {
            string key = slot == 0 ? "PS_D_PILOTPLANE" : "PS_D_WINGPLANE";
            var (x, y, width) = layout.Box(Section, key, ComboX, ComboY + (slot * SlotDrop), ComboWidth);
            _combos[slot] = new CampaignCombo(
                x, y, width,
                layout.Int(Section, key, "ItemHeight", (int)ItemHeight),
                layout.Int(Section, key, "TotalDisplayed", ItemsDisplayed));
        }
    }

    /// <inheritdoc/>
    public override CampaignScreen Screen => CampaignScreen.PlaneSelection;

    /// <inheritdoc/>
    public override string Title => Flow.Strings.Text(3450 + Flow.MissionSeq, $"Mission {Flow.MissionSeq + 1}");

    /// <inheritdoc/>
    public override int RowCount => Rows().Count;

    /// <summary>The cursor opens on the combo of whichever slot's CHANGE PLANE was pressed.</summary>
    public override int OpeningRow
    {
        get
        {
            var rows = Rows();
            for (int row = 0; row < rows.Count; row++)
            {
                if (rows[row].Kind == PlaneRowKind.Pick && rows[row].Slot == Flow.PlaneSlot)
                {
                    return row;
                }
            }

            return 0;
        }
    }

    /// <inheritdoc/>
    public override string Footer => Flow.OpenCombo != null
        ? "↑↓  Choose       Enter / A  Take       Esc / B  Close"
        : "↑↓  Choose       ←→  Change       Enter / A  Select       Esc / B  Back";

    /// <summary>Each crew slot's aircraft silhouette, the airframe's own frame of the icon
    /// sheet, at <c>PS_P_PILOTPLANE</c> and <c>PS_P_WINGPLANE</c>.</summary>
    public override IReadOnlyList<BoardPicture> Pictures
    {
        get
        {
            Fill();
            var layout = Flow.Layout;
            var fallback = new BoardArt(BoardArtLibrary.Ui, "FC_PlaneIcons.Png", SilhouetteFrames);
            var pictures = new List<BoardPicture>(Slots);
            for (int slot = 0; slot < ActiveSlots; slot++)
            {
                if (PlaneFor(slot) is { } plane)
                {
                    string key = slot == 0 ? "PS_P_PILOTPLANE" : "PS_P_WINGPLANE";
                    var (x, y) = layout.At(Section, key, SilhouetteX, SilhouetteY + (slot * SlotDrop));
                    pictures.Add(new BoardPicture(layout.Art(Section, key, fallback), x, y, plane.Airframe));
                }
            }

            return pictures;
        }
    }

    /// <summary>The screen's title and mission line, each slot's heading and named aircraft, its
    /// four ratings and its gun and hardpoint list, each at its own <c>PS_T_*</c> or
    /// <c>PS_A_*</c> row.</summary>
    public override IReadOnlyList<BoardLine> Captions
    {
        get
        {
            Fill();
            var layout = Flow.Layout;
            var (titleX, titleY, titleWidth) = layout.Box(Section, "PS_T_TITLE", TitleX, TitleY, TitleWidth);
            var (missionX, missionY, missionWidth) = layout.Box(Section, "PS_T_MISSIONINFO", MissionX, MissionY, MissionWidth);
            var lines = new List<BoardLine>
            {
                new("PLANE SELECTION", titleX, titleY, titleWidth, TitleFont, BoardInk.Heading),
                new(Title, missionX, missionY, missionWidth, 15, BoardInk.Detail, Italic: true),
            };
            for (int slot = 0; slot < ActiveSlots; slot++)
            {
                float drop = slot * SlotDrop;
                var (headX, headY, headWidth) = layout.Box(
                    Section, slot == 0 ? "PS_T_PILOT" : "PS_T_WINGMAN", ComboX, HeadingY + drop, 94f);
                lines.Add(new BoardLine(slot == 0 ? "PILOT" : "WINGMAN", headX, headY, headWidth, 15, BoardInk.Heading));
                if (PlaneFor(slot) is not { } plane)
                {
                    continue;
                }

                // The wingman's plane line keeps the pilot's row dropped by 218 (see SlotDrop).
                var (nameX, nameY, nameWidth) = layout.Box(
                    Section, slot == 0 ? "PS_T_PILOTPLANE" : "PS_T_WINGPLANE", PlaneNameX, PlaneNameY + drop, 400f);
                lines.Add(new BoardLine(
                    SlotLabel(plane), nameX, slot == 0 ? nameY : PlaneNameY + drop, nameWidth, BodyFont, BoardInk.Row));
                AddRatings(lines, plane, slot);
                var (weaponsX, weaponsY, weaponsWidth) = layout.Box(
                    Section, slot == 0 ? "PS_A_PLANEWEAPONSP" : "PS_A_PLANEWEAPONSW", WeaponsX, RatingY + drop, WeaponsWidth);
                lines.Add(new BoardLine(WeaponList(plane), weaponsX, weaponsY, weaponsWidth, BodyFont, BoardInk.Row));
            }

            return lines;
        }
    }

    /// <summary>How many crew slots this screen draws: both when the mission carries a wingman, the
    /// pilot's alone otherwise. A guest's check draws one, whatever the mission flies: the wingman
    /// belongs to the seated profile.</summary>
    public int ActiveSlots => Guest == null && HasWingman ? 2 : 1;

    private bool HasWingman => _wingmanOverride ?? Flow.MissionHasWingman;

    // Whose picker this is: 0 the seated player's, 1 and up a guest's, the same index the flight
    // check draws itself for.
    private int Player => Flow.Field.Current;

    // The guest this screen is picking for, or null on the seated player's own.
    private CampaignGuest? Guest =>
        Player > 0 && Player - 1 < Flow.Field.Guests.Count ? Flow.Field.Guests[Player - 1] : null;

    // The aircraft the combos list: a guest's own session-scoped choices, else the seated profile's.
    private IReadOnlyList<OwnedPlane> Roster =>
        Guest is { } guest ? guest.Choices : (Flow.Profile ?? EmptyProfile).Planes;

    /// <inheritdoc/>
    public override BoardButtonRef Button(int row)
    {
        var rows = Rows();
        if (row < 0 || row >= rows.Count)
        {
            return BoardButtonRef.None;
        }

        return rows[row].Kind switch
        {
            PlaneRowKind.Export => new BoardButtonRef(BoardButton.ExportPlane, rows[row].Slot),
            PlaneRowKind.Accept => new BoardButtonRef(BoardButton.AcceptSelections),
            PlaneRowKind.Cancel => new BoardButtonRef(BoardButton.CancelSelections),
            _ => BoardButtonRef.None,
        };
    }

    /// <inheritdoc/>
    public override CampaignCombo? Combo(int row)
    {
        Fill();
        var rows = Rows();
        return row >= 0 && row < rows.Count && rows[row].Kind == PlaneRowKind.Pick
            ? _combos[rows[row].Slot]
            : null;
    }

    /// <inheritdoc/>
    public override string RowText(int row)
    {
        Fill();
        var rows = Rows();
        if (row < 0 || row >= rows.Count)
        {
            return string.Empty;
        }

        return rows[row].Kind switch
        {
            PlaneRowKind.Pick => _combos[rows[row].Slot].Text,
            PlaneRowKind.Export => Flow.Strings.Text(ExportLabel, "Export"),
            PlaneRowKind.Accept => "ACCEPT SELECTIONS",
            _ => "CANCEL SELECTIONS",
        };
    }

    /// <inheritdoc/>
    public override string Detail(int row)
    {
        var rows = Rows();
        if (row < 0 || row >= rows.Count)
        {
            return string.Empty;
        }

        return rows[row].Kind switch
        {
            PlaneRowKind.Pick => "Choose the aircraft this crew slot flies",
            PlaneRowKind.Export => "Copies this plane and its loadout to Instant Action and multiplayer",
            PlaneRowKind.Accept => "Keeps these aircraft and returns to the flight check",
            _ => "Leaves both crew slots flying what they flew",
        };
    }

    /// <summary>The closed field's own stepper. It takes the same door a committed pick does, so a
    /// step and a picked list row are refused on the same terms.</summary>
    public override bool Step(int row, int dir)
    {
        Fill();
        var rows = Rows();
        if (row < 0 || row >= rows.Count || rows[row].Kind != PlaneRowKind.Pick || dir == 0)
        {
            return false;
        }

        int slot = rows[row].Slot;
        return Take(slot, _combos[slot].Next(dir));
    }

    /// <inheritdoc/>
    public override bool Accept(int row)
    {
        Fill();
        var rows = Rows();
        if (row < 0 || row >= rows.Count)
        {
            return false;
        }

        int slot = rows[row].Slot;
        switch (rows[row].Kind)
        {
            case PlaneRowKind.Pick:
                // An open list's confirm takes the row under the cursor; a closed one opens.
                return _combos[slot].Confirm() is { } picked
                    ? Take(slot, picked)
                    : _combos[slot].Expand();
            case PlaneRowKind.Export:
                return Export(slot);
            case PlaneRowKind.Accept:
                Commit();
                Flow.GoTo(CampaignScreen.FlightCheck);
                return true;
            case PlaneRowKind.Cancel:
                Restore();
                Flow.GoTo(CampaignScreen.FlightCheck);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Back closes an open list first, and otherwise leaves the screen the way CANCEL
    /// does: the picks the screen opened with are what the flight check goes back to.</summary>
    public override bool Back()
    {
        Fill();
        foreach (var combo in _combos)
        {
            if (combo.Collapse())
            {
                return true;
            }
        }

        Restore();
        return false;
    }

    // A named aircraft and its airframe, except where the two are the same word, the flight check's
    // own rule: "Devastator   Devastator" is not a second fact.
    private string SlotLabel(OwnedPlane plane)
    {
        string title = AirframeTitle(plane.Airframe);
        return plane.Name == title ? title : $"{plane.Name}   {title}";
    }

    // One combo entry, the reference screenshot's own "Blue Streak - Bloodhawk". A guest's stock
    // record is named for its airframe, and "Devastator - Devastator" is not a second fact.
    private string EntryLabel(OwnedPlane plane)
    {
        string title = AirframeTitle(plane.Airframe);
        return plane.Name == title ? title : $"{plane.Name} - {title}";
    }

    private string AirframeTitle(int airframe) => Flow.Strings.Text(3000 + airframe, $"Airframe {airframe}");

    // Every row this screen draws: a combo and an EXPORT per active crew slot, then the two commit
    // buttons. A slot with no aircraft to pick from still draws its combo, which is then empty.
    // ⚠ A guest gets no EXPORT: they fly a session copy carrying the owner's plane name, so the
    // write would land on the owner's stored record.
    private List<PlaneRow> Rows()
    {
        var rows = new List<PlaneRow>(6);
        bool guest = Guest != null;
        for (int slot = 0; slot < ActiveSlots; slot++)
        {
            rows.Add(new PlaneRow(PlaneRowKind.Pick, slot));
            if (!guest)
            {
                rows.Add(new PlaneRow(PlaneRowKind.Export, slot));
            }
        }

        rows.Add(new PlaneRow(PlaneRowKind.Accept));
        rows.Add(new PlaneRow(PlaneRowKind.Cancel));
        return rows;
    }

    // Fills the combos from whichever roster this screen is picking out of, the seated profile's or
    // one guest's, and remembers the picks it opened with, which is what CANCEL restores.
    private void Fill()
    {
        if (Guest is { } guest)
        {
            Load(guest, guest.Choices, guest.Choice, guest.Choice);
            return;
        }

        var profile = Flow.Profile ?? EmptyProfile;
        Load(profile, profile.Planes,
            Clamped(profile.Planes.Count, profile.SelectedPlane),
            Clamped(profile.Planes.Count, profile.WingmanPlane));
    }

    // Refilled when the thing behind the entries changed: a different profile, a different guest,
    // or an aircraft bought since. Keyed on the roster's owner rather than the list, since the two
    // guests of a three-player field hold equal-length lists of their own separate copies.
    private void Load(object owner, IReadOnlyList<OwnedPlane> planes, int pilot, int wingman)
    {
        if (ReferenceEquals(_filledFor, owner) && _filledCount == planes.Count)
        {
            return;
        }

        _filledFor = owner;
        _filledCount = planes.Count;
        var entries = new List<string>(planes.Count);
        foreach (var plane in planes)
        {
            entries.Add(EntryLabel(plane));
        }

        _opened[0] = pilot;
        _opened[1] = wingman;
        for (int slot = 0; slot < Slots; slot++)
        {
            _combos[slot].Load(entries, _opened[slot]);
        }
    }

    private int Clamped(int count, int index) => count == 0 ? 0 : Math.Clamp(index, 0, count - 1);

    // Applies a pick, or refuses it in the original's words. The refusal is the answer to a commit
    // (a picked list row or a closed field's step), never to movement inside an open list, which is
    // where message 10015 sits: refusing per movement would pop the dialog at every scrolled row.
    private bool Take(int slot, int pick)
    {
        if (pick == _combos[slot].Selected)
        {
            return false;
        }

        if (Clashes(slot, pick))
        {
            Flow.RaiseModal(Guest != null
                ? GuestRefusal
                : Flow.Strings.Text(SamePlaneRefusal, "Pilot and Wingman must fly different planes."));
            return true;
        }

        _combos[slot].Select(pick);
        return true;
    }

    // The script's permit test on 10015: the change is allowed when the two combos differ or when
    // only one crew slot is active; the seated pair's rule is the feature's, compared by name.
    // ⚠ A guest's own answer comes from the field, never from a second copy of the rule: the field
    // compares a stock entry by airframe and a profile copy by name, and it is the only thing that
    // knows what the other humans on the sortie took.
    private bool Clashes(int slot, int pick)
    {
        if (Guest != null)
        {
            return Flow.Field.Taken(Player, pick);
        }

        return ActiveSlots >= 2 && Flow.Feature.SeatedPairClashes(pick, _combos[slot == 0 ? 1 : 0].Selected);
    }

    // EXPORT: the plane and the loadout the campaign fitted it with, into the build store, which is
    // the feature's write, then the original's own words for it. A guest gets no EXPORT row, and
    // the feature refuses a stock record on its own.
    private bool Export(int slot)
    {
        if (PlaneFor(slot) is not { } plane || Guest != null || !Flow.Feature.ExportPlane(plane))
        {
            return false;
        }

        Flow.RaiseModal(ExportedText(AirframeTitle(plane.Airframe)));
        return true;
    }

    // langui 702 with the airframe's title in its one placeholder. The fallback carries the same
    // words, since a build with no extraction still exports and still owes the player an answer.
    private string ExportedText(string title)
    {
        string text = Flow.Strings.Format(ExportedMessage, title);
        return text.Length > 0
            ? text
            : $"Your {title} has been exported and is now available for Multiplayer and " +
              "Instant Action missions.";
    }

    // ACCEPT: the two picks into the profile and the profile to disk, the feature's write. A guest's
    // ACCEPT moves their own session-scoped pick instead and writes nothing at all, keeping the
    // profile unchanged.
    private void Commit()
    {
        if (Guest != null)
        {
            Flow.Field.Choose(Player, _combos[0].Selected);
            _opened[0] = _combos[0].Selected;
            return;
        }

        if (Flow.Profile is not { } profile || profile.Planes.Count == 0)
        {
            return;
        }

        Flow.Feature.CommitPlanes(_combos[0].Selected, HasWingman ? _combos[1].Selected : null);
        _opened[0] = profile.SelectedPlane;
        _opened[1] = profile.WingmanPlane;
    }

    // CANCEL and Back: both combos back to what the screen opened with, nothing written.
    private void Restore()
    {
        for (int slot = 0; slot < Slots; slot++)
        {
            _combos[slot].Collapse();
            _combos[slot].Select(_opened[slot]);
        }
    }

    private OwnedPlane? PlaneFor(int slot)
    {
        var roster = Roster;
        int at = _combos[slot].Selected;
        return at >= 0 && at < roster.Count ? roster[at] : null;
    }

    // The four rating lines, at PS_T_TOPSPEEDP and its three neighbours (the W rows for the wingman).
    private void AddRatings(List<BoardLine> lines, OwnedPlane plane, int slot)
    {
        var ratings = PlaneRatings.For(FitOf(plane));
        string[] labels = { "TOP SPEED:", "ARMOR:", "AGILITY:", "OFFENSE:" };
        string[] keys = { "PS_T_TOPSPEED", "PS_T_ARMOR", "PS_T_AGILITY", "PS_T_OFFENSE" };
        string crew = slot == 0 ? "P" : "W";
        float drop = slot * SlotDrop;
        for (int i = 0; i < labels.Length; i++)
        {
            var (x, y, width) = Flow.Layout.Box(
                Section, keys[i] + crew, RatingX, RatingY + drop + (i * RatingPitch), RatingWidth);
            lines.Add(new BoardLine(
                $"{labels[i]}   {RatingWord(ratings[i])}", x, y, width, BodyFont, BoardInk.Row));
        }
    }

    private string RatingWord(int stars)
    {
        int at = Math.Clamp(stars, 0, RatingWords.Length - 1);
        return Flow.Strings.Text(501 + at, RatingWords[at]);
    }

    // The gun and hardpoint column, the reference's own "(2) .50-cal." rows and its "(4)
    // Hardpoints" line, from the same build-or-stock resolution the flight check reads.
    private string WeaponList(OwnedPlane plane)
    {
        var fit = FitOf(plane);
        var lines = new List<string>();
        foreach (int calibre in new[] { 70, 60, 50, 40, 30 })
        {
            if (fit.Barrels.TryGetValue(calibre, out int barrels))
            {
                lines.Add($"({barrels}) {CalibreName(calibre)}");
            }
        }

        if (fit.Hardpoints > 0)
        {
            lines.Add($"({fit.Hardpoints}) {Flow.Strings.Text(HardpointsLabel, "Hardpoints")}");
        }

        return string.Join("\n", lines);
    }

    // IDS_GUNSHORTNAME, the same " .50-cal." the flight check's own weapon rows read, whose string
    // owns its leading space; the reference prints it here without one, so it is trimmed.
    private string CalibreName(int calibre)
    {
        int idx = Math.Clamp((calibre - 30) / 10, 0, 4);
        return Flow.Strings.Text(3320 + idx, $" .{30 + (idx * 10)}-cal.").Trim();
    }

    // ⚠ Asks the field whether the record is a stock airframe before touching CustomPlaneStore: a
    // guest's stock record is named for its airframe, and a hangar plane sharing that name would
    // otherwise fit it with somebody else's build.
    private PlaneFit FitOf(OwnedPlane plane) => PlaneFit.For(
        plane.Airframe,
        Flow.Field.IsStock(plane) ? null : Flow.Planes?.Load(plane.Name),
        Flow.Stock?.ForModel(PlanePickerRoster.AirframeNode(plane.Airframe)));
}
