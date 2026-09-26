using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CSVM.Flight.Airframe;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using CSVM.Mech3;

namespace CSVM.UI.Menu;

/// <summary>
/// The campaign wallet a hangar build is priced and gated against, when a campaign profile funds
/// it. Null on the wallet-free doors (the top level, the Instant Action plane pick), which never
/// check funds; the cabin's Plane Construction hands the feature one over its profile. Every
/// operation reads or writes the profile behind it; the feature never sees the profile itself.
/// </summary>
public interface IHangarWallet
{
    /// <summary>The wallet balance in dollars.</summary>
    int Funds { get; }

    /// <summary>Whether the profile has a slot for another bought plane.</summary>
    bool HasFreeSlot { get; }

    /// <summary>Whether a build's total cost is affordable right now.</summary>
    bool CanAfford(int cost);

    /// <summary>Whether an airframe is offered yet at the profile's progress.</summary>
    bool IsAirframeAvailable(int airframe);

    /// <summary>Whether the named owned plane is an unsellable reward aircraft.</summary>
    bool IsSpecial(string planeName);

    /// <summary>Whether the named owned plane can be sold right now.</summary>
    bool CanSell(string planeName);

    /// <summary>The airframe of the named owned plane, or null when the profile does not own it.</summary>
    int? OwnedAirframe(string planeName);

    /// <summary>The profile's own aircraft as buildable defs, in the ownership list's order.</summary>
    IReadOnlyList<CustomPlaneDef> OwnedBuilds();

    /// <summary>Deducts a completed build's cost and records ownership.</summary>
    void Purchase(string planeName, int airframe, int cost);

    /// <summary>Credits the wallet for the named plane, removes it from the profile and deletes its
    /// build. False, with nothing changed, when <see cref="CanSell"/> would refuse.</summary>
    bool Sell(string planeName);
}

/// <summary>
/// The hangar as a shared feature: one scratch <see cref="CustomPlaneDef"/> with its lifecycle
/// (started bare, from the default configuration or as a copy of a saved plane, committed or
/// discarded), the decoded rules over it (the airframe-defaults ask, the pattern a picked airframe
/// may wear, the purchase gate in the original's own words, the name-collision rule over the whole
/// build store), the store operations (the saved roster, the commit, the sale or deletion) and the
/// campaign wallet when a profile funds the build. Engine-free and presentation-neutral: Built-in
/// walks it through <c>HangarFlow</c>'s nine screens, Original through its tab hub, and both leave
/// the store in the same state for the same presses. Nothing is written before
/// <see cref="Commit"/>, so cancelling from anywhere is residue-free by construction and
/// <see cref="Discard"/> only drops the scratch plane.
/// </summary>
public sealed class HangarFeature : IMenuFeature
{
    /// <summary>The airframe the default configuration starts on, the Devastator (langui 3005),
    /// which is the plane the original's construction screens open over.</summary>
    public const int DefaultAirframe = 5;

    /// <summary>The longest name a plane may carry: the original's saved-plane index is 33-byte
    /// records (docs/formats/paint.md), so 32 characters plus the terminator.</summary>
    public const int MaxNameLength = 32;

    /// <summary>The GUNS dropdown's row count: five calibres single, five twinned, No Gun.</summary>
    public const int GunCycleRows = 11;

    /// <summary>The prefix both presentations put on a row the wallet cannot cover. A mark only:
    /// the row stays pickable, since the decoded flow refuses at the purchase and never at the
    /// part (docs/org/hangar.md, "The cash note").</summary>
    public const string UnaffordableMark = "✕ ";

    // Beyond letters and digits: the separators a plane name uses. Every one is legal in a
    // filename, which is what keeps the store's own sanitisation from rewriting a typed name.
    private const string NamePunctuation = " -'";

    // The four hangar zones in the record's own order, as the vehicle defs spell them.
    private static readonly string[] ArmourZones = { "nose", "tail", "leftwing", "rightwing" };

    private readonly Func<int, string> _nodeOfAirframe;
    private readonly Func<StockLoadouts?>? _stockFits;
    private readonly Dictionary<int, int[]> _stockArmour = new();

    // Every name a commit would land on, by the store's own file identity: the whole build
    // directory plus, over a campaign flow, the owned planes that were never built into it. Held
    // rather than re-scanned because a name screen asks per frame.
    private HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);
    private StockLoadouts? _fits;
    private bool _fitsRead;

    // The build as the screen last opened it, which is what the changed-since test measures
    // against. The original keeps the whole 204-byte record (the copy at 0x006480cc, rewritten by
    // every default-configuration load); only the compared fields are kept here.
    private int[] _asOpened = Array.Empty<int>();

    // What AirframeChosen goes back to when the pending ask is cancelled: an unchosen build can be
    // edited on another tab first, and Cancel must not leave a row ticked nobody picked.
    private bool _askPreviousChosen;

    /// <summary>A feature labelling from <paramref name="strings"/>, resolving an airframe id to
    /// its planes.zbd node through <paramref name="nodeOfAirframe"/> (the stock-fit and vehicle
    /// tables are keyed by node), reading stock fits through <paramref name="stockFits"/> on first
    /// need and stock armour off the zrdr scope at <paramref name="zrdrPath"/>. Either source may be
    /// absent; the affected defaults then load empty, the hangar's missing-data idiom.</summary>
    public HangarFeature(UiStrings strings, Func<int, string> nodeOfAirframe, Func<StockLoadouts?>? stockFits = null, string? zrdrPath = null)
    {
        Strings = strings ?? throw new ArgumentNullException(nameof(strings));
        _nodeOfAirframe = nodeOfAirframe ?? throw new ArgumentNullException(nameof(nodeOfAirframe));
        _stockFits = stockFits;
        ZrdrPath = zrdrPath;
        Saved = Array.Empty<CustomPlaneDef>();
    }

    /// <summary>The langui table every label here is read from.</summary>
    public UiStrings Strings { get; }

    /// <summary>The zrdr scope the stock armour allocations are read from, or null.</summary>
    public string? ZrdrPath { get; }

    /// <summary>The stock-fit table the airframe defaults read guns and hardpoints off, read on
    /// first need; null when the caller gave none or the read failed.</summary>
    public StockLoadouts? StockFits
    {
        get
        {
            if (!_fitsRead)
            {
                _fitsRead = true;
                _fits = _stockFits?.Invoke();
            }

            return _fits;
        }
    }

    /// <summary>Whether a build is open: a store was handed in by <see cref="Open"/> and neither
    /// <see cref="Discard"/> nor a commit has ended it.</summary>
    public bool IsOpen => Store != null;

    /// <summary>The build store the open build reads and commits into, or null when none is open.</summary>
    public CustomPlaneStore? Store { get; private set; }

    /// <summary>The wallet funding the open build, or null on a wallet-free door.</summary>
    public IHangarWallet? Wallet { get; private set; }

    /// <summary>The roster a presentation offers to edit or sell: the store's saved planes over a
    /// wallet-free door, the profile's own aircraft over a wallet. Re-read by
    /// <see cref="DeleteSaved"/>; the other writer is this feature's own commit, which ends the
    /// build.</summary>
    public IReadOnlyList<CustomPlaneDef> Saved { get; private set; }

    /// <summary>The plane being built. Replaced outright by the three starts; edited in place by
    /// every screen after that.</summary>
    public CustomPlaneDef Scratch { get; private set; } = new();

    /// <summary>The saved plane this build opened to edit, or null for a new build. The commit
    /// writes over that file by design, so it is the one name a name screen must not warn about
    /// overwriting.</summary>
    public string? EditingName { get; private set; }

    /// <summary>Whether an airframe has been picked outright. A new plane starts false, so nothing
    /// reads as chosen before the pilot chooses: the model still carries airframe 0 underneath, but
    /// the first confirm is a pick rather than an advance. The default configuration and a saved
    /// plane start true.</summary>
    public bool AirframeChosen { get; private set; }

    /// <summary>Whether the name screen's Load Default Configuration box was checked when this
    /// build started, which is the answer an unasked airframe swap takes: the stock build when it
    /// was set, a bare airframe when it was clear (docs/org/hangar.md).</summary>
    public bool DefaultsWanted { get; private set; } = true;

    /// <summary>The airframe whose defaults the pending ask (langui 206) offers, or null when none
    /// is showing. Raised only by an airframe change over a build that was edited away from what
    /// the screen opened it as; see <see cref="EditedSinceOpened"/>.</summary>
    public int? DefaultsAsk { get; private set; }

    /// <summary>The airframe the pending ask would put back, which is Cancel's whole job, or null
    /// when no ask is showing.</summary>
    public int? DefaultsAskPrevious { get; private set; }

    /// <summary>String 206 with both airframe names formatted in, captured when the ask was
    /// raised.</summary>
    public string DefaultsAskText { get; private set; } = string.Empty;

    /// <summary>Whether the build differs from what the screen opened it as, in the fields the
    /// original's own changed-since test reads: engine, hardpoints, armour, guns and paint, but
    /// neither the airframe nor the name nor the ammunition. This is the gate the airframe swap's
    /// question stands behind (docs/org/hangar.md, "When the airframe swap asks").</summary>
    public bool EditedSinceOpened => !EditableFields(Scratch).SequenceEqual(_asOpened);

    /// <summary>The refusal line the last failed commit or sale left, or "".</summary>
    public string Message { get; private set; } = string.Empty;

    /// <summary>The name the commit saved under, once one has; null while the build is open.</summary>
    public string? BuiltPlaneName { get; private set; }

    /// <summary>The scratch plane priced through the decoded economy, recomputed on demand.</summary>
    public HangarBill Bill => HangarEconomy.Price(Scratch);

    /// <summary>Whether <see cref="Commit"/> would save right now; see <see cref="Refusal"/>.</summary>
    public bool CanCommit => Refusal() == null;

    /// <summary>Whether a character may be typed into a plane name: letters, digits and the three
    /// separators a name uses, every one legal in a filename.</summary>
    public static bool AcceptsNameChar(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
        || NamePunctuation.IndexOf(c) >= 0;

    /// <summary>D32's wing rule read backwards: the stock fit's authored pylons counted per wing
    /// off the rig's own split, odd to port and even to starboard (<see cref="Loadout.WingCounts"/>),
    /// so a stock-fit plane bounds its cells by the pylons the airframe hangs.</summary>
    public static (int Left, int Right) StockWingCounts(HardpointSpec? stock) =>
        Loadout.WingCounts(stock);

    /// <summary>Writes an airframe's stock guns and per-wing hardpoint counts onto
    /// <paramref name="def"/>, leaving every other field alone: stock caliber 30..70 is calibre row
    /// 0..4 and a two-marker slot is the twin mount, and the pylons are split by
    /// <see cref="StockWingCounts"/>. The airframe-defaults arm and the campaign's EXPORT of a plane
    /// that has no build both read an airframe at rest through this one place.</summary>
    public static void LoadStockWeapons(CustomPlaneDef def, LoadoutDef? fit)
    {
        ArgumentNullException.ThrowIfNull(def);
        for (int slot = 0; slot < CustomPlaneDef.GunSlots; slot++)
        {
            def.Guns[slot] = default;
        }

        if (fit != null)
        {
            foreach (var gun in fit.Guns)
            {
                if (gun.Slot is >= 1 and <= CustomPlaneDef.GunSlots)
                {
                    def.Guns[gun.Slot - 1] = new GunChoice((gun.Caliber - 30) / 10, gun.Markers.Count >= 2);
                }
            }
        }

        (def.LeftHardpoints, def.RightHardpoints) = StockWingCounts(fit?.Hardpoints);
    }

    /// <summary>Where a gun pick sits in the GUNS dropdown's eleven-row cycle: singles 0-4, twins
    /// 5-9, No Gun 10.</summary>
    public static int GunCycleIndex(GunChoice gun) =>
        gun.Calibre is not { } calibre ? GunCycleRows - 1 : gun.Twin ? calibre + 5 : calibre;

    /// <summary>The gun pick a cycle row names.</summary>
    public static GunChoice GunOfCycle(int row) => row switch
    {
        >= GunCycleRows - 1 or < 0 => default,
        < 5 => new GunChoice(row, Twin: false),
        _ => new GunChoice(row - 5, Twin: true),
    };

    /// <summary>Writes one zone's armour press count, holding the two wings together: the ARMOR
    /// screen keeps a combo box per wing and changing either changes the other, so a press on either
    /// wing lands on both dwords. ⚠ The rule is the screen's, not the record's, which keeps the
    /// wings apart so the mission side can damage one at a time. A saved plane whose wings disagree
    /// keeps them until a wing is set.</summary>
    public static void SetZoneUnits(CustomPlaneDef plane, int zone, int units)
    {
        ArgumentNullException.ThrowIfNull(plane);
        switch (zone)
        {
            case 0: plane.ArmourNose = units; break;
            case 1: plane.ArmourTail = units; break;
            default:
                plane.ArmourLeftWing = units;
                plane.ArmourRightWing = units;
                break;
        }
    }

    /// <summary>Opens a build over <paramref name="store"/>, funded by <paramref name="wallet"/> or
    /// wallet-free when null, with the roster read once and a bare scratch plane. Opening again
    /// drops whatever build was open, as leaving a presentation for another door does.</summary>
    public void Open(CustomPlaneStore store, IHangarWallet? wallet = null)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Wallet = wallet;
        BuiltPlaneName = null;
        Message = string.Empty;
        StartNewPlane();
        ReadRoster();
    }

    /// <summary>Starts a fresh build with nothing committed: no airframe is chosen, so the
    /// airframe screen opens on a plain list with no row ticked and no question asked. The
    /// defaults ask waits for the pilot's own pick.</summary>
    public void StartNewPlane()
    {
        Scratch = new CustomPlaneDef();
        EditingName = null;
        AirframeChosen = false;
        DefaultsWanted = false;
        ClearDefaultsAsk();
        RememberAsOpened();
    }

    /// <summary>Starts a fresh build on the default configuration: <paramref name="airframe"/>'s
    /// stock engine, guns, hardpoints and armour, already chosen, so a pilot who asked for it lands
    /// on a plane that flies. The airframe is the caller's, the original loading the stock template
    /// of whichever the build already stands on (docs/org/hangar.md, "What Load Default
    /// Configuration loads"); out of range falls back to <see cref="DefaultAirframe"/>. The name
    /// stays as the caller sets it.</summary>
    public void StartDefaultPlane(int airframe = DefaultAirframe)
    {
        int pick = airframe >= 0 && airframe < HangarEconomy.Airframes.Length ? airframe : DefaultAirframe;
        StartNewPlane();
        DefaultsWanted = true;
        LoadAirframeDefaults(pick);
        NormalisePattern(pick);
        AirframeChosen = true;
        RememberAsOpened();
    }

    /// <summary>Starts from a saved plane, as a copy: editing and abandoning it must not touch
    /// what is on disk, so the store's own canonical serialisation is the deep copy. No defaults
    /// ask: the plane already is what its builder chose, and its airframe is already chosen.</summary>
    public void StartFromSaved(CustomPlaneDef saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        Scratch = CustomPlaneStore.Deserialize(CustomPlaneStore.Serialize(saved)) ?? new CustomPlaneDef();
        EditingName = saved.Name;
        AirframeChosen = true;
        ClearDefaultsAsk();
        RememberAsOpened();
    }

    /// <summary>Picks an airframe: one that is not already the pick switches to it and snaps the
    /// paint pattern onto one the airframe may wear, returning true. Picking the standing pick
    /// returns false, so a presentation can treat the second confirm as an advance. A new plane's
    /// first pick always picks, even on the airframe the model was carrying underneath. An edited
    /// build's swap raises the ask; an unedited one takes the checkbox's answer unasked.</summary>
    public bool PickAirframe(int airframe)
    {
        if (AirframeChosen && Scratch.Airframe == airframe)
        {
            return false;
        }

        // Read before the write: the swap itself is not one of the pilot's edits, and neither is
        // the pattern snap the new airframe forces below.
        bool edited = EditedSinceOpened;
        bool wasChosen = AirframeChosen;
        int was = Scratch.Airframe;
        Scratch.Airframe = airframe;
        AirframeChosen = true;
        NormalisePattern(airframe);
        if (was == airframe)
        {
            // The original's airframe callback compares the id before and after and does nothing
            // at all when it has not moved, so a first confirm on the airframe the model was
            // already carrying is a pick and no more.
            return true;
        }

        if (edited)
        {
            RaiseDefaultsAsk(airframe, was, wasChosen);
        }
        else
        {
            ApplyDefaultConfiguration(DefaultsWanted);
        }

        return true;
    }

    /// <summary>Raises the airframe-defaults ask, string 206's own question over two short airframe
    /// names (langui 3020 + id): %1 the airframe just picked, %2 the one the build was opened on.
    /// <paramref name="previousAirframe"/> and <paramref name="previousChosen"/> are what Cancel
    /// restores.</summary>
    public void RaiseDefaultsAsk(int airframe, int previousAirframe, bool previousChosen = true)
    {
        string was = AirframeShortName(previousAirframe);
        string text = Strings.Format(206, AirframeShortName(airframe), was);
        DefaultsAskText = text.Length > 0 ? text
            : $"Do you want the default armor, engine, and guns for this new airframe ({AirframeShortName(airframe)})?  " +
              $"If you do not want to lose the changes you've made to the {was} you were building, click Cancel.";
        DefaultsAsk = airframe;
        DefaultsAskPrevious = previousAirframe;
        _askPreviousChosen = previousChosen;
    }

    /// <summary>Answers the pending ask: either way the build is rebuilt from the new airframe's
    /// stock template, taken whole for Yes and stripped back to a bare airframe for No, which is
    /// why 206 says Cancel is the only way to keep an edit. Nothing pending answers nothing.</summary>
    public void AnswerDefaultsAsk(bool loadDefaults)
    {
        if (DefaultsAsk is null)
        {
            return;
        }

        ClearDefaultsAsk();
        ApplyDefaultConfiguration(loadDefaults);
    }

    /// <summary>Cancels the pending ask: the airframe goes back to the one the build was opened on
    /// and every other pick is left alone, so the edits 206 warns about survive. Nothing pending
    /// cancels nothing.</summary>
    public void CancelDefaultsAsk()
    {
        if (DefaultsAskPrevious is not { } was)
        {
            return;
        }

        bool chosen = _askPreviousChosen;
        ClearDefaultsAsk();
        Scratch.Airframe = was;
        AirframeChosen = chosen;
        NormalisePattern(was);
    }

    /// <summary>Loads the default configuration over the whole build, the original's own callback
    /// 2212: the standing airframe's stock template, stripped back to a bare airframe when
    /// <paramref name="stock"/> is false. The name and the paint are not the airframe's to set and
    /// stay. The build's opened-as copy is retaken, so this is where an edit is measured from.</summary>
    public void ApplyDefaultConfiguration(bool stock)
    {
        LoadAirframeDefaults(Scratch.Airframe);
        if (!stock)
        {
            StripToBareAirframe();
        }

        RememberAsOpened();
    }

    /// <summary>Loads an airframe's defaults into the scratch plane, the ask's accept arm: gun
    /// picks and per-wing hardpoint counts read back off the airframe's stock fit, engine id 1
    /// (the stock Lvl-2 tier), armour the stock zone allocations in units. Paint and name are
    /// not the airframe's to default and stay as they are.</summary>
    public void LoadAirframeDefaults(int airframe)
    {
        Scratch.Airframe = airframe;
        Scratch.Engine = 1;
        LoadStockWeapons(Scratch, StockFits?.ForModel(_nodeOfAirframe(airframe)));
        int[] armour = StockArmourUnits(airframe);
        Scratch.ArmourNose = armour[0];
        Scratch.ArmourTail = armour[1];
        Scratch.ArmourLeftWing = armour[2];
        Scratch.ArmourRightWing = armour[3];
        Scratch.Clamp();
    }

    /// <summary>Picks the engine, 0-5 or <see cref="CustomPlaneDef.EngineNone"/>. Returns whether it changed.</summary>
    public bool SetEngine(int engine)
    {
        int next = Math.Clamp(engine, 0, CustomPlaneDef.EngineNone);
        if (Scratch.Engine == next)
        {
            return false;
        }

        Scratch.Engine = next;
        return true;
    }

    /// <summary>Sets one zone's armour in units, zone 0-3 in the record's order (nose, tail, left
    /// wing, right wing), clamped to 0-12. A wing zone sets both wings through
    /// <see cref="SetZoneUnits"/>. Returns whether anything moved, so a wing pick that only levels
    /// a pair which disagreed still redraws.</summary>
    public bool SetArmour(int zone, int units)
    {
        int next = Math.Clamp(units, 0, CustomPlaneDef.MaxArmourUnits);
        var before = (Scratch.ArmourNose, Scratch.ArmourTail, Scratch.ArmourLeftWing, Scratch.ArmourRightWing);
        SetZoneUnits(Scratch, zone, next);
        return before != (Scratch.ArmourNose, Scratch.ArmourTail, Scratch.ArmourLeftWing, Scratch.ArmourRightWing);
    }

    /// <summary>One zone's armour in units, zone 0-3 in the record's order.</summary>
    public int ArmourUnits(int zone) => zone switch
    {
        0 => Scratch.ArmourNose,
        1 => Scratch.ArmourTail,
        2 => Scratch.ArmourLeftWing,
        _ => Scratch.ArmourRightWing,
    };

    /// <summary>Sets one gun slot from the GUNS dropdown's cycle row. Returns whether it changed.</summary>
    public bool SetGun(int slot, int cycleRow)
    {
        if (slot < 0 || slot >= CustomPlaneDef.GunSlots)
        {
            return false;
        }

        var next = GunOfCycle(cycleRow);
        if (Scratch.Guns[slot] == next)
        {
            return false;
        }

        Scratch.Guns[slot] = next;
        return true;
    }

    /// <summary>Sets one wing's hardpoint count, wing 0 left and 1 right, clamped to 0-4.</summary>
    public bool SetHardpoints(int wing, int count)
    {
        int next = Math.Clamp(count, 0, CustomPlaneDef.MaxHardpointsPerWing);
        int current = wing == 0 ? Scratch.LeftHardpoints : Scratch.RightHardpoints;
        if (current == next)
        {
            return false;
        }

        if (wing == 0)
        {
            Scratch.LeftHardpoints = next;
        }
        else
        {
            Scratch.RightHardpoints = next;
        }

        return true;
    }

    /// <summary>The paint patterns the scratch plane's airframe may wear, as record indices, from
    /// the pattern table's own availability masks. With no table loaded every pattern is offered,
    /// the hangar's missing-data idiom rather than an empty list.</summary>
    public IReadOnlyList<int> WearablePatterns()
    {
        var roster = new List<int>();
        for (int pattern = 0; pattern < HangarPaintTables.PatternCount; pattern++)
        {
            if (HangarPaintTables.Default.Available(pattern, Scratch.Airframe))
            {
                roster.Add(pattern);
            }
        }

        return roster;
    }

    /// <summary>Selects a paint pattern, copying its six colour/shade defaults over the plane's
    /// own and touching nothing else, which is all the original's SET handler does.</summary>
    public bool SetPattern(int pattern)
    {
        if (Scratch.PaintPattern == pattern)
        {
            return false;
        }

        Scratch.LoadPatternDefaults(pattern);
        return true;
    }

    /// <summary>Picks a paint slot's swatch row, which resets the slot's shade to that row's default.</summary>
    public bool SetColour(int slot, int colour)
    {
        if (slot < 0 || slot >= HangarPaintTables.Slots || Scratch.PaintColours[slot] == colour)
        {
            return false;
        }

        Scratch.SetPaintColour(slot, colour);
        return true;
    }

    /// <summary>Picks a paint slot's shade within its colour's ramp.</summary>
    public bool SetShade(int slot, int shade)
    {
        if (slot < 0 || slot >= HangarPaintTables.Slots)
        {
            return false;
        }

        int count = Math.Max(1, HangarPaintTables.Default.ShadeCount(Scratch.PaintColours[slot]));
        int next = Math.Clamp(shade, 0, count - 1);
        if (Scratch.PaintShades[slot] == next)
        {
            return false;
        }

        Scratch.PaintShades[slot] = next;
        return true;
    }

    /// <summary>Sets a decal slot (0 nose, 1 tail, 2 wing) to a texture of the 00-49 set.</summary>
    public bool SetDecal(int slot, int decal)
    {
        int next = Math.Clamp(decal, 0, HangarPaintTables.DecalCount - 1);
        if (Decal(slot) == next)
        {
            return false;
        }

        switch (slot)
        {
            case 0: Scratch.NoseDecal = next; break;
            case 1: Scratch.TailDecal = next; break;
            default: Scratch.WingDecal = next; break;
        }

        return true;
    }

    /// <summary>One decal slot's texture index, or <see cref="CustomPlaneDef.KeepDecal"/>.</summary>
    public int Decal(int slot) => slot switch
    {
        0 => Scratch.NoseDecal,
        1 => Scratch.TailDecal,
        _ => Scratch.WingDecal,
    };

    /// <summary>Why a commit would be refused right now, in the original's own words, or null when
    /// it would save: a name (langui 203), then the economy's verdict (1182 with 1227 OVERWEIGHT or
    /// 1171 No Engine Selected), then over a wallet a full hangar (204), an unavailable airframe or
    /// an unaffordable total (1226). Neither slots nor funds are checked on a wallet-free door.</summary>
    public string? Refusal()
    {
        if (string.IsNullOrWhiteSpace(Scratch.Name))
        {
            return Strings.Text(203, "You must enter a name for your new plane.");
        }

        var bill = Bill;
        if (bill.Verdict != PurchaseVerdict.Ok)
        {
            return Strings.Text(1182, "CAN'T PURCHASE:") + " " + (bill.Verdict == PurchaseVerdict.Overweight
                ? Strings.Text(1227, "OVERWEIGHT")
                : Strings.Text(1171, "No Engine Selected"));
        }

        if (Wallet is { } wallet)
        {
            // Ahead of the funds line because a full hangar is the refusal that names its own
            // remedy, and selling a plane back at full price answers both at once.
            if (!wallet.HasFreeSlot)
            {
                return HangarFullText();
            }

            if (!wallet.IsAirframeAvailable(Scratch.Airframe))
            {
                return Strings.Text(1182, "CAN'T PURCHASE:").TrimEnd() + " That airframe is not available yet.";
            }

            if (!wallet.CanAfford(bill.Total.Cost))
            {
                return Strings.Text(1182, "CAN'T PURCHASE:").TrimEnd() + " " + Strings.Text(1226, "INSUFFICIENT FUNDS");
            }
        }

        return null;
    }

    /// <summary>The full-hangar refusal in the original's own words (langui 204). Composed here
    /// rather than at each reader because a presentation that greys its purchase row ahead of the
    /// press must say what the press itself would say.</summary>
    public string HangarFullText() => Strings.Text(
        204,
        "You have reached your hangar limit of planes.  Click Sell Planes, and sell one or more planes.");

    /// <summary>Saves the scratch plane and ends the build, or refuses and leaves
    /// <see cref="Message"/> saying why (<see cref="Refusal"/>). Over a wallet the same commit also
    /// moves the money and records ownership. Returns whether the plane was saved.</summary>
    public bool Commit()
    {
        if (Store is not { } store)
        {
            Message = "No build is open.";
            return false;
        }

        if (Refusal() is { } refusal)
        {
            Message = refusal;
            return false;
        }

        int cost = Bill.Total.Cost;

        // The door decides the crossing: a build funded by a campaign wallet waits for that
        // campaign's EXPORT before any picker lists it, a build from a wallet-free door is already
        // an Instant Action aeroplane and carries no marker at all.
        Scratch.AwaitingExport = Wallet != null;
        store.Save(Scratch);
        BuiltPlaneName = Scratch.Name;
        Message = string.Empty;
        Wallet?.Purchase(Scratch.Name, Scratch.Airframe, cost);
        return true;
    }

    /// <summary>Removes a saved plane from the store and re-reads <see cref="Saved"/>: the
    /// original's Sell Plane, free over a wallet-free door; over a wallet it is an actual sale
    /// crediting the wallet, refused (nothing changed, <see cref="Message"/> says why) for a
    /// reward aircraft or below the two-plane floor. The scratch plane is untouched either way.</summary>
    public bool DeleteSaved(string name)
    {
        if (Store is not { } store)
        {
            return false;
        }

        bool gone;
        if (Wallet is { } wallet)
        {
            if (wallet.IsSpecial(name))
            {
                Message = CannotSellText(name);
                return false;
            }

            if (!wallet.CanSell(name))
            {
                Message = Strings.Text(701, "You must keep at least two planes in your hangar.");
                return false;
            }

            gone = wallet.Sell(name);
        }
        else
        {
            gone = store.Delete(name);
        }

        Message = string.Empty;
        ReadRoster();
        return gone;
    }

    /// <summary>Clears the refusal line, which any navigation does.</summary>
    public void ClearMessage() => Message = string.Empty;

    /// <summary>Whether a commit under this name would land on a plane that already exists: the
    /// whole build directory, plus a wallet's owned planes that were never built into it. Compared
    /// on the store's file identity, since two names can sanitise to one file.</summary>
    public bool IsNameTaken(string name) => _taken.Contains(CustomPlaneStore.FileKey(name));

    /// <summary>Whether saving under the scratch plane's name would overwrite somebody else's
    /// file. The plane being edited is not somebody else: a build opened from a saved plane is
    /// meant to write back over it.</summary>
    public bool Overwrites()
    {
        if (string.IsNullOrWhiteSpace(Scratch.Name) || !IsNameTaken(Scratch.Name))
        {
            return false;
        }

        return !string.Equals(
            CustomPlaneStore.FileKey(Scratch.Name),
            CustomPlaneStore.FileKey(EditingName ?? string.Empty),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The reward aircraft's own refusal, langui 704 with both of its arguments filled
    /// (the airframe's short name and the plane's). Read raw the string reaches the pilot with its
    /// placeholders still in it, which is why it is composed here.</summary>
    public string CannotSellText(string planeName)
    {
        int airframe = Wallet?.OwnedAirframe(planeName) ?? 0;
        string text = Strings.Format(704, AirframeShortName(airframe), planeName);
        return text.Length > 0 ? text : $"This {AirframeShortName(airframe)}, {planeName}, cannot be sold.";
    }

    /// <summary>The airframe's own name (langui 3000 + id).</summary>
    public string AirframeName(int airframe) =>
        Strings.Text(3000 + airframe, $"Airframe {airframe}");

    /// <summary>The airframe's short name (langui 3020 + id), the form the sell strings take
    /// their %1 in.</summary>
    public string AirframeShortName(int airframe) =>
        Strings.Text(3020 + airframe, $"Airframe {airframe}");

    /// <summary>The engine's name for an airframe (langui 3100 + airframe*6 + id), or the
    /// no-engine line for id 6.</summary>
    public string EngineName(int airframe, int engine) =>
        engine == CustomPlaneDef.EngineNone
            ? Strings.Text(1171, "No Engine Selected")
            : Strings.Text(3100 + (airframe * 6) + engine, $"Engine {engine}");

    /// <summary>One gun slot's pick as the original names it: the calibre's own name (langui
    /// 3310 + calibre), prefixed "(2) " when twinned (format 506), or None for an empty slot.</summary>
    public string GunName(GunChoice gun)
    {
        if (gun.Calibre is not { } calibre)
        {
            return Strings.Text(1165, "None");
        }

        string name = Strings.Text(3310 + calibre, $".{30 + (calibre * 10)}-cal.");
        if (!gun.Twin)
        {
            return name;
        }

        string prefix = Strings.Format(506, 2);
        return (prefix.Length == 0 ? "(2)" : prefix.TrimEnd()) + " " + name;
    }

    /// <summary>A GUNS dropdown row as it reads: the shared gun naming for a mounted pick, the
    /// empty row's own string (langui 3315) for No Gun.</summary>
    public string GunCycleName(int row)
    {
        var gun = GunOfCycle(row);
        return gun.IsEmpty ? Strings.Text(3315, "No Gun") : GunName(gun);
    }

    /// <summary>An armour amount in the ARMOR dropdown's own words: langui 1165 None for no units,
    /// else format 1170 with the stored figure, units x5.</summary>
    public string ArmourLabel(int units)
    {
        if (units == 0)
        {
            return Strings.Text(1165, "None");
        }

        string text = Strings.Format(1170, units * 5);
        return text.Length > 0 ? text : $"{units * 5} units";
    }

    /// <summary>A hardpoint count in the HARDPOINTS dropdown's own words: langui 1165 None, 1168 for
    /// one, format 1169 for more.</summary>
    public string HardpointsLabel(int count)
    {
        if (count == 0)
        {
            return Strings.Text(1165, "None");
        }

        if (count == 1)
        {
            return Strings.Text(1168, "1 Hardpoint");
        }

        string text = Strings.Format(1169, count);
        return text.Length > 0 ? text : $"{count} Hardpoints";
    }

    /// <summary>The pattern dropdown's own label (langui 3425 + index), else the pattern table's
    /// name in capitals, else the index.</summary>
    public string PatternLabel(int pattern)
    {
        string name = HangarPaintTables.Default.PatternName(pattern);
        return Strings.Text(3425 + pattern, name.Length == 0 ? $"pattern {pattern}" : name.ToUpperInvariant());
    }

    /// <summary>The persistent totals line every build screen shows: the plane's total price and
    /// its weight against the airframe's capacity, flagged with the original's own word (langui
    /// 1227) when over.</summary>
    public string TotalsLine(CustomPlaneDef plane)
    {
        ArgumentNullException.ThrowIfNull(plane);
        var bill = HangarEconomy.Price(plane);
        string line = $"${bill.Total.Cost}   {bill.Total.Weight} / {bill.Capacity} lbs.";
        return bill.Total.Weight > bill.Capacity
            ? line + "   ⚠ " + Strings.Text(1227, "OVERWEIGHT")
            : line;
    }

    /// <summary>The money on hand as the construction screens show it over a wallet, langui 1149
    /// (<c>$$$ on Hand:</c>) with the funds, or "" on a wallet-free door, where nothing is drawn.</summary>
    public string WalletLine() =>
        Wallet is { } wallet
            ? Strings.Text(1149, "$$$ on Hand:") + " $" + wallet.Funds.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>Whether a build priced at <paramref name="cost"/> is beyond the wallet; always false
    /// on a wallet-free door. ⚠ Never gate a pick on this: the decoded flow refuses at the purchase
    /// (callback 2264), and hiding what cannot be afforded yet hides what is being saved toward.</summary>
    public bool Unaffordable(int cost) => Wallet is { } wallet && !wallet.CanAfford(cost);

    /// <summary>The build's total cost were <paramref name="airframe"/> picked, every other pick
    /// kept: what the wallet would have to cover after that row.</summary>
    public int CostWithAirframe(int airframe) => BillWithAirframe(airframe).Total.Cost;

    /// <summary>The build's total cost were <paramref name="engine"/> picked.</summary>
    public int CostWithEngine(int engine) => BillWithEngine(engine).Total.Cost;

    /// <summary>The build's total cost were zone <paramref name="zone"/> at <paramref name="units"/>.
    /// A wing row prices both wings, since picking on either moves the other.</summary>
    public int CostWithArmour(int zone, int units) => BillWithArmour(zone, units).Total.Cost;

    /// <summary>The build's total cost were slot <paramref name="slot"/> on cycle row <paramref name="cycleRow"/>.</summary>
    public int CostWithGun(int slot, int cycleRow) => BillWithGun(slot, cycleRow).Total.Cost;

    /// <summary>The build's total cost were wing <paramref name="wing"/> carrying <paramref name="count"/> hardpoints.</summary>
    public int CostWithHardpoints(int wing, int count) => BillWithHardpoints(wing, count).Total.Cost;

    /// <summary>The whole bill the build would carry were <paramref name="airframe"/> picked, every
    /// other pick kept: the cost the wallet mark reads, and the weight and capacity a screen
    /// previewing the row needs. ⚠ Prices a copy of the scratch plane, never the scratch plane
    /// itself, so asking what a row would cost can never become the pick.</summary>
    public HangarBill BillWithAirframe(int airframe)
    {
        var preview = Preview();
        preview.Airframe = airframe;
        return HangarEconomy.Price(preview);
    }

    /// <summary>The whole bill the build would carry were <paramref name="engine"/> picked.</summary>
    public HangarBill BillWithEngine(int engine)
    {
        var preview = Preview();
        preview.Engine = Math.Clamp(engine, 0, CustomPlaneDef.EngineNone);
        return HangarEconomy.Price(preview);
    }

    /// <summary>The whole bill the build would carry were zone <paramref name="zone"/> at
    /// <paramref name="units"/>, a wing row moving both wings the way a pick on either does.</summary>
    public HangarBill BillWithArmour(int zone, int units)
    {
        var preview = Preview();
        SetZoneUnits(preview, zone, Math.Clamp(units, 0, CustomPlaneDef.MaxArmourUnits));
        return HangarEconomy.Price(preview);
    }

    /// <summary>The whole bill the build would carry were slot <paramref name="slot"/> on cycle row
    /// <paramref name="cycleRow"/>.</summary>
    public HangarBill BillWithGun(int slot, int cycleRow)
    {
        var preview = Preview();
        if (slot >= 0 && slot < CustomPlaneDef.GunSlots)
        {
            preview.Guns[slot] = GunOfCycle(cycleRow);
        }

        return HangarEconomy.Price(preview);
    }

    /// <summary>The whole bill the build would carry were wing <paramref name="wing"/> carrying
    /// <paramref name="count"/> hardpoints.</summary>
    public HangarBill BillWithHardpoints(int wing, int count)
    {
        var preview = Preview();
        int next = Math.Clamp(count, 0, CustomPlaneDef.MaxHardpointsPerWing);
        if (wing == 0)
        {
            preview.LeftHardpoints = next;
        }
        else
        {
            preview.RightHardpoints = next;
        }

        return HangarEconomy.Price(preview);
    }

    /// <summary>Drops the open build: the scratch plane, its store and wallet, the roster, the ask
    /// and the messages. Called on a presentation switch and by a cancel; a committed plane is
    /// already in the store and is not touched.</summary>
    public void Discard()
    {
        Store = null;
        Wallet = null;
        Saved = Array.Empty<CustomPlaneDef>();
        _taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        BuiltPlaneName = null;
        Message = string.Empty;
        StartNewPlane();
    }

    // The fields the original's changed-since test compares, as one vector: engine, both hardpoint
    // counts, the paint pattern with its three colours, shades and decals, the four armour zones,
    // and the four gun slots with their twin bits. The airframe is left out because the swap has
    // already written it, and the name and the ammunition because the test never reads them.
    private static int[] EditableFields(CustomPlaneDef plane)
    {
        var fields = new List<int>
        {
            plane.Engine, plane.LeftHardpoints, plane.RightHardpoints, plane.PaintPattern,
            plane.ArmourNose, plane.ArmourTail, plane.ArmourLeftWing, plane.ArmourRightWing,
            plane.NoseDecal, plane.TailDecal, plane.WingDecal,
        };
        for (int slot = 0; slot < HangarPaintTables.Slots; slot++)
        {
            fields.Add(plane.PaintColours[slot]);
            fields.Add(plane.PaintShades[slot]);
        }

        for (int slot = 0; slot < CustomPlaneDef.GunSlots; slot++)
        {
            fields.Add(plane.Guns[slot].Calibre ?? -1);
            fields.Add(plane.Guns[slot].Twin ? 1 : 0);
        }

        return fields.ToArray();
    }

    // The scratch plane as the economy sees it, copied: everything Price reads and nothing else,
    // since paint and the name are free and unpriced. ⚠ Do not price a would-be pick by writing it
    // into Scratch and writing it back; a preview that threw mid-price would leave it taken.
    private CustomPlaneDef Preview()
    {
        var copy = new CustomPlaneDef
        {
            Airframe = Scratch.Airframe,
            Engine = Scratch.Engine,
            ArmourNose = Scratch.ArmourNose,
            ArmourTail = Scratch.ArmourTail,
            ArmourLeftWing = Scratch.ArmourLeftWing,
            ArmourRightWing = Scratch.ArmourRightWing,
            LeftHardpoints = Scratch.LeftHardpoints,
            RightHardpoints = Scratch.RightHardpoints,
        };

        for (int slot = 0; slot < CustomPlaneDef.GunSlots; slot++)
        {
            copy.Guns[slot] = Scratch.Guns[slot];
        }

        return copy;
    }

    // The roster and the taken-name set together, since a wallet's roster is ownership while the
    // names a commit can collide with are still the whole build directory: a campaign plane must
    // not silently overwrite an Instant Action build of the same name.
    private void ReadRoster()
    {
        if (Store is not { } store)
        {
            return;
        }

        var stored = store.List();
        Saved = Wallet is { } wallet ? wallet.OwnedBuilds() : stored;
        _taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in stored.Concat(Saved))
        {
            _taken.Add(CustomPlaneStore.FileKey(def.Name));
        }
    }

    // A bare airframe, the default configuration's own other arm: no engine, no armour, no
    // hardpoints and four empty gun slots. The original writes the same values over the template
    // it has just copied (docs/org/hangar.md, "What Load Default Configuration loads").
    private void StripToBareAirframe()
    {
        Scratch.Engine = CustomPlaneDef.EngineNone;
        Scratch.LeftHardpoints = 0;
        Scratch.RightHardpoints = 0;
        Scratch.ArmourNose = 0;
        Scratch.ArmourTail = 0;
        Scratch.ArmourLeftWing = 0;
        Scratch.ArmourRightWing = 0;
        for (int slot = 0; slot < CustomPlaneDef.GunSlots; slot++)
        {
            Scratch.Guns[slot] = default;
        }
    }

    private void ClearDefaultsAsk()
    {
        DefaultsAsk = null;
        DefaultsAskPrevious = null;
        DefaultsAskText = string.Empty;
        _askPreviousChosen = false;
    }

    private void RememberAsOpened() => _asOpened = EditableFields(Scratch);

    // A pattern the picked airframe may not wear snaps to its first available one, carrying that
    // pattern's own colour/shade defaults: a fresh scratch holds pattern 0 (blackhat), which most
    // airframes' availability masks exclude, and a paint screen must never open on a paint job the
    // plane cannot wear.
    private void NormalisePattern(int airframe)
    {
        var tables = HangarPaintTables.Default;
        if (tables.Available(Scratch.PaintPattern, airframe))
        {
            return;
        }

        for (int pattern = 0; pattern < HangarPaintTables.PatternCount; pattern++)
        {
            if (tables.Available(pattern, airframe))
            {
                Scratch.LoadPatternDefaults(pattern);
                return;
            }
        }
    }

    // The airframe's stock zone allocations in units (the destroyable_parts armour pools / 5,
    // docs/formats/vehicle.md), read through PlaneStats off the zrdr scope and cached. No
    // reachable extraction reads as all zeros, the missing-data idiom every hangar source uses.
    private int[] StockArmourUnits(int airframe)
    {
        if (_stockArmour.TryGetValue(airframe, out var units))
        {
            return units;
        }

        units = new int[ArmourZones.Length];
        if (ZrdrPath is { } zrdr)
        {
            try
            {
                foreach (var part in PlaneStats.Load(zrdr, _nodeOfAirframe(airframe)).DestroyableParts)
                {
                    int zone = Array.IndexOf(ArmourZones, part.Name.ToLowerInvariant());
                    if (zone >= 0)
                    {
                        units[zone] = (int)Math.Round(part.MaxArmor / CustomPlaneBuild.ArmourUnitScale);
                    }
                }
            }
            catch (Exception)
            {
                // An unreadable extraction is the same as none; the zeros stand.
            }
        }

        _stockArmour[airframe] = units;
        return units;
    }
}
