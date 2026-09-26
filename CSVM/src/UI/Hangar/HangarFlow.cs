using System;
using System.Collections.Generic;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.UI.Menu;

namespace CSVM.UI.Hangar;

/// <summary>The hangar's screens, in the order the original walks them.</summary>
public enum HangarScreen
{
    /// <summary>Start a new plane, or load a saved one to edit.</summary>
    PlaneSelection,
    Airframe,
    Engine,
    Armour,
    Guns,
    Hardpoints,
    Paint,

    /// <summary>The plane's name, which is also its identity in the store.</summary>
    Name,

    /// <summary>The itemised review and the Build action.</summary>
    Purchase,
}

/// <summary>How a hangar flow ended, or that it is still running.</summary>
public enum HangarExit
{
    /// <summary>Still on a screen.</summary>
    None,

    /// <summary>Backed out; the scratch plane was discarded unsaved.</summary>
    Cancelled,

    /// <summary>Committed; the scratch plane is in the store under
    /// <see cref="HangarFlow.BuiltPlaneName"/>.</summary>
    Built,
}

/// <summary>
/// One hangar screen, as the shell draws and drives it. Everything is plain text and plain
/// indices, so a page is engine-free and testable: the shell owns every Godot control. A page
/// edits <see cref="HangarFlow.Scratch"/> in place through <see cref="Step"/>, which is the
/// launchscreen's live-stepper idiom; <see cref="Accept"/> returning false hands the press back to
/// the flow, which advances to the next screen. The pick screens (airframe, engine) select on
/// <see cref="Accept"/> instead and leave <see cref="Step"/> inert (E49).
/// </summary>
public interface IHangarPage
{
    /// <summary>Which screen this page is.</summary>
    HangarScreen Screen { get; }

    /// <summary>The heading, from the screen's own langui string.</summary>
    string Title { get; }

    /// <summary>How many rows the page draws right now.</summary>
    int RowCount { get; }

    /// <summary>The row the cursor lands on when the flow arrives, 0 for most screens. A screen
    /// whose rows ARE the picks opens on its current pick, so confirming twice walks the flow
    /// through without rewriting anything (E49's double-enter idiom).</summary>
    int OpeningRow { get; }

    /// <summary>The picture the shell should draw beside the list right now, or null for none,
    /// which every screen without art, and any screen missing its extraction, simply is.</summary>
    HangarArt? Art { get; }

    /// <summary>A second, smaller picture for the focused row, or null for none. Only the paint
    /// screen's three decal rows have one: the decal's own tile out of the shipped sheet.</summary>
    HangarArt? RowArt(int row);

    /// <summary>Row <paramref name="row"/>'s text.</summary>
    string RowText(int row);

    /// <summary>The detail line under the list for the focused row, or "" for none.</summary>
    string Detail(int row);

    /// <summary>The plane the shell's totals row prices while <paramref name="row"/> is focused:
    /// the scratch plane on every build screen, and null where the row is an action rather than a
    /// plane, which hides the row (E50).</summary>
    CustomPlaneDef? TotalsPlane(int row);

    /// <summary>The build's total cost with <paramref name="row"/>'s pick in effect (the build as it
    /// stands on a stepper row, the alternative on a pick row), or null where the row prices nothing
    /// (an action, a free pick, a name). What the wallet mark compares against the funds.</summary>
    int? CostWith(int row);

    /// <summary>The horizontal stepper on the focused row. Returns whether anything changed.</summary>
    bool Step(int row, int dir);

    /// <summary>The confirm press on the focused row. Returns true when the page handled it;
    /// false lets the flow advance to the next screen.</summary>
    bool Accept(int row);

    /// <summary>The flow has just arrived on this screen, before its opening row is read. A page
    /// is built once and kept, so this is the only place it can act on the scratch plane it is
    /// being shown this time rather than the one it was built over.</summary>
    void Entered();
}

/// <summary>One picture a page asks the shell to draw: a decoded TGA (blueprint, icon, paint
/// preview) plus the caption under it. The page decodes; the shell owns the one Godot texture.</summary>
public sealed record HangarArt(TgaImage Image, string Caption);

/// <summary>
/// The Build Custom Plane flow, Built-in's walk of the shared <see cref="HangarFeature"/>: one
/// scratch <see cref="CustomPlaneDef"/> walked through <see cref="Order"/>, with back/next
/// navigation and a commit at the end. Engine-free, so the screen order, the cancel semantics and
/// the scratch lifecycle test off engine; the launchscreen is only its renderer and input source.
/// The rules, the store operations and the scratch plane itself are the feature's; this class owns
/// the screen order, the cursor and the pages. Nothing is written until <see cref="Commit"/>
/// succeeds, which is what makes cancelling from any screen residue-free by construction.
/// </summary>
public sealed class HangarFlow
{
    /// <summary>The original's screen order (docs/org/hangar.md).</summary>
    public static readonly HangarScreen[] Order =
    {
        HangarScreen.PlaneSelection,
        HangarScreen.Airframe,
        HangarScreen.Engine,
        HangarScreen.Armour,
        HangarScreen.Guns,
        HangarScreen.Hardpoints,
        HangarScreen.Paint,
        HangarScreen.Name,
        HangarScreen.Purchase,
    };

    private readonly HangarFeature _feature;
    private readonly Dictionary<HangarScreen, IHangarPage> _pages = new();

    /// <summary>Opens a flow over <paramref name="store"/> through a private feature, reading its
    /// saved planes once for the plane-selection screen. <paramref name="strings"/>,
    /// <paramref name="dataRoot"/>, <paramref name="stockFits"/> and <paramref name="zrdrPath"/>
    /// may be null or empty; the affected labels, art and defaults then simply fall back or load
    /// empty. <paramref name="campaign"/> is null for both existing doors, keeping them
    /// wallet-free; only the cabin's Plane Construction passes one.</summary>
    public HangarFlow(CustomPlaneStore store, UiStrings strings, string? dataRoot = null,
        StockLoadouts? stockFits = null, string? zrdrPath = null, CampaignWallet? campaign = null,
        Random? nameRng = null)
        : this(new HangarFeature(strings, PlanePickerRoster.AirframeNode, () => stockFits, zrdrPath),
            store, dataRoot, campaign, nameRng)
    {
    }

    /// <summary>Opens a flow over the shared <paramref name="feature"/>, which is opened on
    /// <paramref name="store"/> funded by <paramref name="campaign"/> (null for a wallet-free door).
    /// The launchscreen builds every flow this way, over the host's one feature.</summary>
    public HangarFlow(HangarFeature feature, CustomPlaneStore store, string? dataRoot = null,
        CampaignWallet? campaign = null, Random? nameRng = null)
    {
        _feature = feature ?? throw new ArgumentNullException(nameof(feature));
        NameRng = nameRng ?? new Random();
        DataRoot = dataRoot;
        Campaign = campaign;
        _feature.Open(store, campaign);
    }

    /// <summary>The shared feature this flow walks.</summary>
    public HangarFeature Feature => _feature;

    /// <summary>The langui table the screens title and label themselves from.</summary>
    public UiStrings Strings => _feature.Strings;

    /// <summary>The generator the PLANENAME screen rolls names from. Handed in rather than drawn
    /// here so the flow stays engine-free: the session's own stream (<c>Rng.PlaneName</c>) lives
    /// behind Godot's generator, and a test wants a pinned seed and an exact name.</summary>
    public Random NameRng { get; }

    /// <summary>The folder <c>extracted/</c> sits in, or null when the caller has none. Pages
    /// resolve their TGAs under it and treat absence as no art.</summary>
    public string? DataRoot { get; }

    /// <summary>The stock-fit table the airframe-defaults ask reads its gun and hardpoint
    /// defaults back off, or null when the caller has none (those defaults then load empty).</summary>
    public StockLoadouts? StockFits => _feature.StockFits;

    /// <summary>The zrdr scope <see cref="Flight.Airframe.PlaneStats"/> reads vehicle defs from, for the stock
    /// armour allocations; null or unreadable reads as armour defaults of 0.</summary>
    public string? ZrdrPath => _feature.ZrdrPath;

    /// <summary>The campaign wallet this flow prices against, or null over the two existing doors
    /// (Instant Action's Build button, the top-level entry), which stay wallet-free by construction
    /// Non-null only when the cabin's Plane Construction opened this flow.</summary>
    public CampaignWallet? Campaign { get; }

    /// <summary>The airframe whose defaults the pending ask (langui 206) offers, or null when
    /// none is showing. Raised only by an explicit confirm on an airframe row that is not already
    /// the pick (E49); the airframe page renders it as a two-row confirm.</summary>
    public int? DefaultsAsk => _feature.DefaultsAsk;

    /// <summary>String 206 with both names formatted in, captured when the ask was raised (so
    /// the plane-being-built name is the one it had then).</summary>
    public string DefaultsAskText => _feature.DefaultsAskText;

    /// <summary>The plane-selection roster: the store's saved planes over the two wallet-free
    /// doors, and the profile's own aircraft over a campaign flow (<see cref="Campaign"/>). Re-read
    /// only by <see cref="DeleteSaved"/>: the other writer is this flow's own commit, which ends
    /// it.</summary>
    public IReadOnlyList<CustomPlaneDef> Saved => _feature.Saved;

    /// <summary>The plane being built. Replaced outright when the plane-selection screen starts a
    /// new build or loads a saved one; edited in place by every screen after that.</summary>
    public CustomPlaneDef Scratch => _feature.Scratch;

    /// <summary>The saved plane this flow opened to edit, or null for a new build. The commit
    /// writes over that file by design, so it is the one name the PLANENAME screen must not warn
    /// about overwriting.</summary>
    public string? EditingName => _feature.EditingName;

    /// <summary>The screen showing.</summary>
    public HangarScreen Screen { get; private set; } = HangarScreen.PlaneSelection;

    /// <summary>The focused row on that screen.</summary>
    public int Row { get; private set; }

    /// <summary>The page drawing the current screen.</summary>
    public IHangarPage Page => PageFor(Screen);

    /// <summary>Whether the flow is still running, and how it ended if not.</summary>
    public HangarExit Exit { get; private set; }

    /// <summary>The name the commit saved under, once <see cref="Exit"/> is
    /// <see cref="HangarExit.Built"/>.</summary>
    public string? BuiltPlaneName => _feature.BuiltPlaneName;

    /// <summary>The refusal line a failed commit left, or "". Cleared by any navigation.</summary>
    public string Message => _feature.Message;

    /// <summary>The persistent second stats line every hangar screen shows under its heading
    /// the build's total price and its weight against the airframe's
    /// capacity, recomputed from <see cref="HangarEconomy.Price"/> on demand and flagged with the
    /// original's own word (langui 1227) when over. Which plane it prices is the page's to say
    /// (<see cref="IHangarPage.TotalsPlane"/>): the plane-selection screen prices the saved plane
    /// under the cursor, and "" hides the line where the focused row is not a plane at all.</summary>
    public string TotalsLine =>
        Page.TotalsPlane(RowInPage()) is { } plane ? _feature.TotalsLine(plane) : string.Empty;

    /// <summary>Whether <see cref="TotalsLine"/> is over capacity, so the shell can colour the
    /// flag as well as print it.</summary>
    public bool TotalsOverweight =>
        Page.TotalsPlane(RowInPage()) is { } plane
        && HangarEconomy.Price(plane).Verdict == PurchaseVerdict.Overweight;

    /// <summary>The money on hand beside the totals on every screen after the buy row, langui 1149
    /// with the profile's funds; "" over the wallet-free doors and on the plane-selection screen,
    /// whose buy row already carries it as its detail.</summary>
    public string WalletLine =>
        Campaign is null || Screen == HangarScreen.PlaneSelection ? string.Empty : _feature.WalletLine();

    /// <summary>Whether the plane <see cref="TotalsLine"/> prices is beyond the wallet, so the shell
    /// can colour the line; false wherever <see cref="WalletLine"/> is empty.</summary>
    public bool WalletShort =>
        WalletLine.Length > 0 && Page.TotalsPlane(RowInPage()) is { } plane
        && _feature.Unaffordable(HangarEconomy.Price(plane).Total.Cost);

    /// <summary>Whether an airframe has been picked outright on the AIRFRAME screen. A new plane
    /// starts false, so nothing reads as chosen before the pilot chooses (E49): the model still
    /// carries airframe 0 underneath, but no row is ticked and the first confirm is a pick rather
    /// than an advance. Loading a saved plane starts true.</summary>
    public bool AirframeChosen => _feature.AirframeChosen;

    /// <summary>The heading string id for each screen, from the original's own tab labels.</summary>
    public static int TitleStringId(HangarScreen screen) => screen switch
    {
        HangarScreen.PlaneSelection => 1017,
        HangarScreen.Airframe => 1004,
        HangarScreen.Engine => 1005,
        HangarScreen.Armour => 1006,
        HangarScreen.Guns => 1007,
        HangarScreen.Hardpoints => 1008,
        HangarScreen.Paint => 1009,
        HangarScreen.Name => 1401,
        _ => 1010,
    };

    /// <summary>The fallback heading for a screen when the langui table is missing.</summary>
    public static string TitleFallback(HangarScreen screen) => screen switch
    {
        HangarScreen.PlaneSelection => "PLANE SELECTION",
        HangarScreen.Armour => "Armor",
        HangarScreen.Name => "PLANE NAME",
        _ => screen.ToString(),
    };

    /// <summary>The shared feature's <see cref="HangarFeature.StockWingCounts"/>, kept here for the
    /// campaign pages that read it under this name.</summary>
    public static (int Left, int Right) StockWingCounts(HardpointSpec? stock) => HangarFeature.StockWingCounts(stock);

    /// <summary>The shared feature's <see cref="HangarFeature.LoadStockWeapons"/>, kept here for
    /// the campaign's EXPORT, which reads an airframe at rest under this name.</summary>
    public static void LoadStockWeapons(CustomPlaneDef def, LoadoutDef? fit) => HangarFeature.LoadStockWeapons(def, fit);

    /// <summary>Moves the row cursor, wrapping like every other launchscreen list.</summary>
    public bool Move(int dir)
    {
        int count = Page.RowCount;
        if (dir == 0 || count <= 1)
        {
            return false;
        }

        _feature.ClearMessage();
        Row = ((Row + dir) % count + count) % count;
        return true;
    }

    /// <summary>Applies the horizontal stepper to the focused row.</summary>
    public bool Step(int dir)
    {
        if (dir == 0)
        {
            return false;
        }

        _feature.ClearMessage();
        return Page.Step(ClampedRow(), dir);
    }

    /// <summary>The confirm press: the page first, then the flow's own advance to the next
    /// screen. On the last screen the page commits, so nothing advances past it.</summary>
    public bool Accept()
    {
        _feature.ClearMessage();
        int row = ClampedRow();
        if (Page.Accept(row))
        {
            return true;
        }

        return Advance();
    }

    /// <summary>The back press: the previous screen, or cancelling the flow from the first one.
    /// The scratch plane is dropped with it, and since nothing is written before
    /// <see cref="Commit"/>, that leaves no residue at all.</summary>
    public bool Back()
    {
        _feature.ClearMessage();
        int at = Array.IndexOf(Order, Screen);
        if (at <= 0)
        {
            Exit = HangarExit.Cancelled;
            _feature.Discard();
            return true;
        }

        Screen = Order[at - 1];
        Page.Entered();
        Row = Page.OpeningRow;
        ClampedRow();
        return true;
    }

    /// <summary>Moves to the next screen in <see cref="Order"/>, or does nothing on the last.</summary>
    public bool Advance()
    {
        int at = Array.IndexOf(Order, Screen);
        if (at < 0 || at >= Order.Length - 1)
        {
            return false;
        }

        Screen = Order[at + 1];
        Page.Entered();
        Row = Page.OpeningRow;
        ClampedRow();
        return true;
    }

    /// <summary>Saves the scratch plane and ends the flow, or refuses and leaves
    /// <see cref="Message"/> saying why. The gate is <see cref="HangarEconomy"/>'s verdict plus a
    /// name, in the original's own words; funds are never checked over the two IA/top-level doors
    /// (Decision 2). Over a campaign flow (<see cref="Campaign"/> non-null, B13) the same commit
    /// also refuses a full hangar, an unavailable airframe or an unaffordable total, and on success
    /// moves the money and records ownership. Returns whether the plane was saved.</summary>
    public bool Commit()
    {
        if (!_feature.Commit())
        {
            return false;
        }

        Exit = HangarExit.Built;
        return true;
    }

    /// <summary>Removes a saved plane from the store and re-reads <see cref="Saved"/>: the
    /// original's Sell Plane (<c>ps_b_sellp</c>), free over the two wallet-free doors; over a
    /// campaign flow (B13) it is an actual sale, <see cref="Campaign"/>'s decoded price crediting
    /// the wallet, refused (nothing changed, <see cref="Message"/> says why) for a reward aircraft
    /// or below the two-plane floor. Cursor and scratch plane are untouched either way.</summary>
    public bool DeleteSaved(string name)
    {
        bool gone = _feature.DeleteSaved(name);
        ClampedRow();
        return gone;
    }

    /// <summary>The reward aircraft's own refusal, langui 704 with both of its arguments filled
    /// (the airframe's short name and the plane's). Read raw the string reaches the pilot with its
    /// <c>%1!s!</c> placeholders still in it, which is why it is composed here.</summary>
    public string CannotSellText(string planeName) => _feature.CannotSellText(planeName);

    /// <summary>Whether a commit under this name would land on a plane that already exists: the
    /// whole build directory, plus a campaign flow's owned planes that were never built into it.
    /// Compared on the store's file identity, since two names can sanitise to one file.</summary>
    public bool IsNameTaken(string name) => _feature.IsNameTaken(name);

    /// <summary>Puts the cursor on a row, clamped into the page's current list. A page that
    /// changes how many rows it draws calls this so the cursor lands on something real.</summary>
    public void FocusRow(int row)
    {
        Row = Math.Max(0, row);
        ClampedRow();
    }

    /// <summary>Starts a fresh build (the plane-selection screen's New Plane row) with nothing
    /// committed: no airframe is chosen, so the airframe screen opens on a plain list with no row
    /// ticked and no question asked (E49). The defaults ask waits for the pilot's own pick.</summary>
    public void StartNewPlane() => _feature.StartNewPlane();

    /// <summary>Starts from a saved plane, as a copy: editing and abandoning it must not touch
    /// what is on disk, so the store's own canonical serialisation is the deep copy. No
    /// defaults ask: the plane already is what its builder chose, and its airframe is already
    /// chosen, so the screen opens on that row ticked.</summary>
    public void StartFromSaved(CustomPlaneDef saved) => _feature.StartFromSaved(saved);

    /// <summary>The AIRFRAME screen's confirm on a row: picking an airframe that is not already
    /// the pick switches to it, asks where the build was edited, and returns true so the press
    /// stays on the screen. Confirming the row that already IS the pick returns false, which is
    /// what lets the flow advance (E49's double-enter idiom). A new plane's first confirm always
    /// picks, even on the airframe the model was carrying underneath. The cursor moves onto the
    /// ask's answers where one was raised and stays on the picked row where none was.</summary>
    public bool PickAirframe(int airframe)
    {
        if (!_feature.PickAirframe(airframe))
        {
            return false;
        }

        Row = DefaultsAsk is null ? airframe : 0;
        return true;
    }

    /// <summary>Raises the airframe-defaults ask, string 206's own question over the new airframe's
    /// short name and the one the build was opened on. The cursor moves onto the answers.</summary>
    public void RaiseDefaultsAsk(int airframe, int previousAirframe)
    {
        _feature.RaiseDefaultsAsk(airframe, previousAirframe);
        Row = 0;
    }

    /// <summary>Answers the pending ask: Yes takes the new airframe's whole stock build, No takes
    /// it bare, and either way the picks 206 warns about are gone. The cursor lands back on the
    /// chosen airframe.</summary>
    public void AnswerDefaultsAsk(bool loadDefaults)
    {
        if (DefaultsAsk is null)
        {
            return;
        }

        _feature.AnswerDefaultsAsk(loadDefaults);
        Row = Scratch.Airframe;
    }

    /// <summary>Cancels the pending ask: the airframe goes back to the one the build was opened on
    /// and every other pick survives, which is what 206 offers Cancel for. The cursor lands back
    /// on that airframe.</summary>
    public void CancelDefaultsAsk()
    {
        if (DefaultsAsk is null)
        {
            return;
        }

        _feature.CancelDefaultsAsk();
        Row = Scratch.Airframe;
    }

    /// <summary>Loads an airframe's defaults into the scratch plane, the ask's accept arm: gun
    /// picks and per-wing hardpoint counts read back off the airframe's stock fit, engine id 1
    /// (the stock Lvl-2 tier), armour the stock zone allocations in units. Paint and name are
    /// not the airframe's to default and stay as they are.</summary>
    public void LoadAirframeDefaults(int airframe) => _feature.LoadAirframeDefaults(airframe);

    /// <summary>The airframe's own name (langui 3000 + id).</summary>
    public string AirframeName(int airframe) => _feature.AirframeName(airframe);

    /// <summary>The airframe's short name (langui 3020 + id, "Devastator" to 3005's "Hughes P21-J
    /// MKIII Devastator"), which is the form the sell strings take their %1 in.</summary>
    public string AirframeShortName(int airframe) => _feature.AirframeShortName(airframe);

    /// <summary>The engine's name for an airframe (langui 3100 + airframe*6 + id), or the
    /// no-engine line for id 6.</summary>
    public string EngineName(int airframe, int engine) => _feature.EngineName(airframe, engine);

    /// <summary>One gun slot's pick as the original names it: the calibre's own name (langui
    /// 3310 + calibre), prefixed "(2) " when twinned (format 506), or None for an empty slot.</summary>
    public string GunName(GunChoice gun) => _feature.GunName(gun);

    /// <summary>Whether a row's pick would leave the build beyond the wallet: the page's
    /// <see cref="IHangarPage.CostWith"/> against the funds, never true over a wallet-free door.</summary>
    public bool RowUnaffordable(int row) =>
        Campaign is not null && Page.CostWith(row) is { } cost && _feature.Unaffordable(cost);

    /// <summary>Row <paramref name="row"/>'s text as the shell draws it: the page's own, prefixed
    /// with <see cref="HangarFeature.UnaffordableMark"/> where <see cref="RowUnaffordable"/> holds.
    /// The mark changes nothing about what the row does.</summary>
    public string RowText(int row) =>
        (RowUnaffordable(row) ? HangarFeature.UnaffordableMark : string.Empty) + Page.RowText(row);

    // The page for a screen, built on first sight and kept, so a page may hold state of its own.
    // Every screen's page is real and lives in its own file; the placeholder survives only as
    // the default arm's guard.
    private IHangarPage PageFor(HangarScreen screen)
    {
        if (!_pages.TryGetValue(screen, out var page))
        {
            page = screen switch
            {
                HangarScreen.PlaneSelection => new HangarPlaneSelectionPage(this),
                HangarScreen.Airframe => new HangarAirframePage(this),
                HangarScreen.Engine => new HangarEnginePage(this),
                HangarScreen.Armour => new HangarArmourPage(this),
                HangarScreen.Guns => new HangarGunsPage(this),
                HangarScreen.Hardpoints => new HangarHardpointsPage(this),
                HangarScreen.Paint => new HangarPaintPage(this),
                HangarScreen.Name => new HangarNamePage(this),
                HangarScreen.Purchase => new HangarPurchasePage(this),
                _ => new HangarPlaceholderPage(this, screen),
            };
            _pages[screen] = page;
        }

        return page;
    }

    // The focused row, kept inside the page's current list: a page whose row count shrank under
    // the cursor (a shorter saved-plane roster, a screen swapped by a later item) must not index
    // past its own end.
    private int ClampedRow()
    {
        Row = Math.Clamp(Row, 0, Math.Max(0, Page.RowCount - 1));
        return Row;
    }

    // The same clamp for a reader that must not move the cursor while answering: the totals line
    // is drawn every frame, and a getter that writes Row would fight the pilot's own navigation.
    private int RowInPage() => Math.Clamp(Row, 0, Math.Max(0, Page.RowCount - 1));
}

/// <summary>
/// The base every hangar page shares: its flow, its scratch plane, its strings, and the heading
/// resolved from the screen's own langui id. A page overriding nothing but
/// <see cref="RowCount"/> and <see cref="RowText"/> is a legal screen that advances on confirm and
/// backs out on cancel, which is what the C21 placeholders are.
/// </summary>
public abstract class HangarPage : IHangarPage
{
    /// <summary>Binds the page to the flow whose scratch plane it edits.</summary>
    protected HangarPage(HangarFlow flow) => Flow = flow;

    /// <inheritdoc/>
    public abstract HangarScreen Screen { get; }

    /// <inheritdoc/>
    public virtual string Title =>
        Flow.Strings.Text(HangarFlow.TitleStringId(Screen), HangarFlow.TitleFallback(Screen));

    /// <inheritdoc/>
    public abstract int RowCount { get; }

    /// <inheritdoc/>
    public virtual int OpeningRow => 0;

    /// <inheritdoc/>
    public virtual HangarArt? Art => null;

    /// <summary>The flow this page belongs to.</summary>
    protected HangarFlow Flow { get; }

    /// <summary>The plane being built.</summary>
    protected CustomPlaneDef Scratch => Flow.Scratch;

    /// <inheritdoc/>
    public abstract string RowText(int row);

    /// <inheritdoc/>
    public virtual string Detail(int row) => string.Empty;

    /// <inheritdoc/>
    public virtual CustomPlaneDef? TotalsPlane(int row) => Scratch;

    /// <inheritdoc/>
    public virtual int? CostWith(int row) => HangarEconomy.Price(Scratch).Total.Cost;

    /// <inheritdoc/>
    public virtual bool Step(int row, int dir) => false;

    /// <inheritdoc/>
    public virtual bool Accept(int row) => false;

    /// <inheritdoc/>
    public virtual void Entered()
    {
    }

    /// <inheritdoc/>
    public virtual HangarArt? RowArt(int row) => null;
}

/// <summary>
/// The flow's first screen: start a new plane, or load one of the store's saved planes to edit.
/// Confirming either seats the scratch plane and lets the flow advance, so the screens after this
/// one always have a plane to edit.
///
/// <para>It also removes planes, the role the original's Sell Plane button (<c>ps_b_sellp</c>)
/// fills. The gesture is two-stage rather than a stepper, because a stepper on a destructive action
/// is one stray nudge away from losing a build: a trailing row opens a second list whose every row
/// names the plane it acts on. Over a campaign flow the screen is the profile's INVENTORY (langui
/// 1257), its roster is what the profile owns and its removal is a sale (docs/org/hangar.md).</para>
/// </summary>
public sealed class HangarPlaneSelectionPage : HangarPage
{
    private bool _removing;

    /// <summary>Binds the page to its flow.</summary>
    public HangarPlaneSelectionPage(HangarFlow flow)
        : base(flow)
    {
    }

    /// <inheritdoc/>
    public override HangarScreen Screen => HangarScreen.PlaneSelection;

    /// <inheritdoc/>
    public override int RowCount =>
        _removing ? Flow.Saved.Count + 1 : Flow.Saved.Count + (Flow.Saved.Count > 0 ? 2 : 1);

    /// <summary>The screen's own heading: the campaign's is the inventory it lists (langui 1257),
    /// and the two wallet-free doors keep PLANE SELECTION (1017).</summary>
    public override string Title =>
        Flow.Campaign is null ? base.Title : Flow.Strings.Text(1257, "INVENTORY");

    /// <inheritdoc/>
    public override string RowText(int row)
    {
        bool campaign = Flow.Campaign is not null;
        if (_removing)
        {
            return row < Flow.Saved.Count
                ? (campaign ? "Sell " : "Delete ") + Flow.Saved[row].Name
                : "Cancel";
        }

        if (row == 0)
        {
            return campaign ? "Buy a New Plane" : "New Plane";
        }

        if (row <= Flow.Saved.Count)
        {
            return Flow.Saved[row - 1].Name;
        }

        return campaign ? "Sell a plane" : "Delete a saved plane";
    }

    /// <inheritdoc/>
    public override string Detail(int row)
    {
        if (Flow.Campaign is { } campaign)
        {
            return CampaignDetail(campaign, row);
        }

        if (_removing)
        {
            return row < Flow.Saved.Count
                ? $"Removes {Flow.Saved[row].Name} from the hangar for good"
                : "Keep every saved plane";
        }

        if (row == 0)
        {
            return "Build a plane from a bare airframe";
        }

        return row <= Flow.Saved.Count
            ? Flow.AirframeName(Flow.Saved[row - 1].Airframe)
            : "Remove a saved plane from the hangar";
    }

    /// <inheritdoc/>
    /// <remarks>E50: the totals row prices the saved plane under the cursor, so the roster reads
    /// as a hangar rather than as a list of names. Every other row here is an action (New Plane,
    /// the delete stage, Cancel) and has no plane to price, so the row hides: the scratch plane's
    /// own totals would be a stale figure from a build this screen has not started yet.</remarks>
    public override CustomPlaneDef? TotalsPlane(int row) =>
        !_removing && row >= 1 && row <= Flow.Saved.Count ? Flow.Saved[row - 1] : null;

    /// <summary>Nothing here is bought: an owned plane is already paid for and the actions price
    /// nothing, so no row takes the wallet mark.</summary>
    public override int? CostWith(int row) => null;

    /// <inheritdoc/>
    public override bool Accept(int row)
    {
        if (_removing)
        {
            return AcceptRemove(row);
        }

        if (row > Flow.Saved.Count)
        {
            _removing = true;
            Flow.FocusRow(0);
            return true; // the removal list is this screen's own stage, not the next screen
        }

        if (row == 0)
        {
            Flow.StartNewPlane();
            return false; // let the flow advance to the first build screen
        }

        // A campaign plane cannot be edited in place: the decoded economy pays for a build in full
        // and credits a sale in full, so an edit would charge again and strand the old plane's
        // value. The row is the inventory entry it reads as, and the sell stage is what acts on it.
        if (Flow.Campaign is not null)
        {
            return true;
        }

        Flow.StartFromSaved(Flow.Saved[row - 1]);
        return false;
    }

    // The removal list's own press: a plane row removes (or sells) that plane and the list stays
    // up while any remain, Cancel (and the last plane going) returns to the pick list. Which row
    // Cancel is has to be read BEFORE the removal, since the roster can shrink under it. A refused
    // sale leaves the roster alone and the flow's own message saying why.
    private bool AcceptRemove(int row)
    {
        bool cancel = row >= Flow.Saved.Count;
        if (!cancel)
        {
            Flow.DeleteSaved(Flow.Saved[row].Name);
        }

        _removing = !cancel && Flow.Saved.Count > 0;
        // Stay on a plane row rather than sliding onto Cancel when the list's last one went.
        Flow.FocusRow(_removing ? Math.Min(row, Flow.Saved.Count - 1) : 0);
        return true;
    }

    // The campaign's own detail lines: the wallet the buy row spends (langui 1149), an owned
    // plane's airframe and value (1258), and on the sell stage the refusal a press would meet
    // (704 for a reward aircraft, 701 for the two-plane floor) in place of that value.
    private string CampaignDetail(CampaignWallet campaign, int row)
    {
        if (_removing)
        {
            if (row >= Flow.Saved.Count)
            {
                return "Keep every plane";
            }

            var plane = Flow.Saved[row];
            if (campaign.IsSpecial(plane.Name))
            {
                return Flow.CannotSellText(plane.Name);
            }

            return campaign.CanSell(plane.Name)
                ? Value(plane)
                : Flow.Strings.Text(701, "You must keep at least two planes in your hangar.");
        }

        if (row == 0)
        {
            return Flow.Strings.Text(1149, "$$$ on Hand:") + " $" + campaign.Funds;
        }

        if (row > Flow.Saved.Count)
        {
            return "Sell a plane back at its full build cost";
        }

        var owned = Flow.Saved[row - 1];
        return Flow.AirframeShortName(owned.Airframe) + "   " + Value(owned);
    }

    // One plane's worth, langui 1258's own line. Priced off the roster's own def rather than
    // through the context, which would re-read the plane's file on every frame this line is drawn;
    // the roster resolved that same def once, so the two figures cannot disagree.
    private string Value(CustomPlaneDef plane)
    {
        int price = HangarEconomy.Price(plane).Total.Cost;
        string text = Flow.Strings.Format(1258, price);
        return text.Length > 0 ? text : $"Value: ${price}";
    }
}

/// <summary>
/// A screen with no page of its own, which is only the switch's default arm now that every screen
/// has one: the right heading, a Continue row, and a summary of what the scratch plane currently
/// carries for that screen. It edits nothing, so a flow walked through it produces exactly the
/// plane the screens before it chose.
/// </summary>
public sealed class HangarPlaceholderPage : HangarPage
{
    private readonly HangarScreen _screen;

    /// <summary>Binds the page to its flow and the screen it stands in for.</summary>
    public HangarPlaceholderPage(HangarFlow flow, HangarScreen screen)
        : base(flow) => _screen = screen;

    /// <inheritdoc/>
    public override HangarScreen Screen => _screen;

    /// <inheritdoc/>
    public override int RowCount => 1;

    /// <inheritdoc/>
    public override string RowText(int row) => "Continue";

    /// <inheritdoc/>
    public override string Detail(int row) => Summary();

    // What the scratch plane carries for this screen right now, from the decoded strings and the
    // economy, so the placeholder shows real data rather than a stub line.
    private string Summary()
    {
        var bill = HangarEconomy.Price(Scratch);
        return _screen switch
        {
            HangarScreen.Airframe =>
                $"{Flow.AirframeName(Scratch.Airframe)}   ${bill.Airframe.Cost}   {bill.Airframe.Weight} lbs.",
            HangarScreen.Engine =>
                $"{Flow.EngineName(Scratch.Airframe, Scratch.Engine)}   ${bill.Engine.Cost}   {bill.Engine.Weight} lbs.",
            HangarScreen.Armour => ArmourSummary(),
            HangarScreen.Guns => GunSummary(),
            HangarScreen.Hardpoints =>
                Line(1176, "Left Wing", Scratch.LeftHardpoints) + "   " +
                Line(1177, "Right Wing", Scratch.RightHardpoints),
            _ => $"Pattern {Scratch.PaintPattern}",
        };
    }

    // One "<label>: <n>" line through its own langui format, falling back to the plain label when
    // the string table is missing.
    private string Line(int id, string label, int value)
    {
        string text = Flow.Strings.Format(id, value);
        return text.Length > 0 ? text : $"{label}: {value}";
    }

    // The four zones in the record's own order, each through its own langui line (1191-1194).
    private string ArmourSummary()
    {
        int[] units = { Scratch.ArmourNose, Scratch.ArmourTail, Scratch.ArmourLeftWing, Scratch.ArmourRightWing };
        string[] labels = { "Nose", "Tail", "Left Wing", "Right Wing" };
        var parts = new string[units.Length];
        for (int i = 0; i < units.Length; i++)
        {
            parts[i] = Line(1191 + i, labels[i], units[i]);
        }

        return string.Join("   ", parts);
    }

    // The four slots, each under the airframe's own slot title from the stat table.
    private string GunSummary()
    {
        var stats = HangarEconomy.Airframes[Scratch.Airframe];
        var parts = new string[CustomPlaneDef.GunSlots];
        for (int slot = 0; slot < parts.Length; slot++)
        {
            string title = Flow.Strings.Text(stats.SlotTitle(slot), $"Slot {slot + 1}");
            parts[slot] = $"{title}: {Flow.GunName(Scratch.Guns[slot])}";
        }

        return string.Join("   ", parts);
    }
}
