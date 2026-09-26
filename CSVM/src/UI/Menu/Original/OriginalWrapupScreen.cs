using System;
using System.Collections.Generic;
using CSVM.Flight.Modes;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Screens;

namespace CSVM.UI.Menu.Original;

/// <summary>
/// The Original presentation's Instant Action wrap-up page, a standalone module over the decoded
/// <c>[@IA_WrapUp@]</c> section: the magazine spread, the notepad's heading, the four decoded rows
/// off the final snapshot, the further lines on post-its, a stunt run's photographs beside them,
/// the outcome's tick box, and a CONTINUE
/// plaque back to the Instant Action screen. The page stands only while a snapshot is held, which the session
/// hands over when the wrap-up hold ends; the built-in presentation keeps its own board instead.
/// Readings: docs/formats/instant-action/wrap-up.md.
/// </summary>
public sealed class OriginalWrapupScreen : IOriginalScreenModule
{
    /// <summary>The layout section the page is composed from.</summary>
    public const string WrapupSection = InstantActionWrapupPage.Section;

    /// <summary>The CONTINUE plaque, back to the Instant Action screen.</summary>
    public const string ContinueKey = InstantActionWrapupPage.ContinueKey;

    /// <summary>The one row standing while a photograph is open, the whole page, which closes it.</summary>
    public const string ViewerKey = "IAWU_VIEWER";

    // Each print's row key is this and its index in marker order.
    private const string PrintKeyPrefix = "IAWU_PRINT_";

    // The plaque's size where the file cannot be measured: GN_B_Continue.png's own frame.
    private const float FallbackPlaqueWidth = 112f;
    private const float FallbackPlaqueHeight = 34f;

    private readonly CampaignLayout _layout;
    private readonly Func<string, (int Width, int Height)?> _measure;
    private readonly IOriginalScreenHost _host;
    private readonly Action _openInstantAction;

    // The photographs the page last drew as empty prints, whose landing is what repaints it.
    private readonly List<StuntShot> _pending = new();

    // The run the page is showing, final at the mission's ending. Null while no ended mission has
    // been handed over, which is every other moment the shell is alive.
    private IaWrapupSnapshot? _snapshot;

    // The print row a photograph was opened from, where closing it puts the cursor back.
    private string _viewedKey = string.Empty;

    /// <summary>A wrap-up module over <paramref name="layout"/>'s own section, measuring its plaque
    /// through <paramref name="measure"/> and calling back into <paramref name="host"/> for the
    /// shell state every module shares. <paramref name="openInstantAction"/> is the door CONTINUE
    /// takes, the Instant Action screen's own opener rather than a bare screen switch, so the
    /// sortie's roster and environment are re-read on the way.</summary>
    public OriginalWrapupScreen(
        CampaignLayout layout, Func<string, (int Width, int Height)?> measure, IOriginalScreenHost host,
        Action openInstantAction)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _measure = measure ?? throw new ArgumentNullException(nameof(measure));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _openInstantAction = openInstantAction ?? throw new ArgumentNullException(nameof(openInstantAction));
    }

    /// <summary>The run the page is showing, or null while none has been handed over.</summary>
    public IaWrapupSnapshot? Snapshot => _snapshot;

    /// <summary>The further lines as the page writes them, for a caller reading the page back.</summary>
    public IReadOnlyList<string> ExtraLines =>
        _snapshot is { } shown ? InstantActionWrapupPage.ExtraLines(shown) : Array.Empty<string>();

    /// <summary>The four decoded rows and the heading as the page writes them, or nothing while no
    /// run is showing.</summary>
    public IReadOnlyList<BoardLine> PageRows =>
        _snapshot is { } shown ? InstantActionWrapupPage.Rows(shown, _layout) : Array.Empty<BoardLine>();

    /// <summary>The run's photographs as the page lays them out, in marker order, or nothing while
    /// no run is showing or the run took none.</summary>
    public IReadOnlyList<WrapupPrint> Prints =>
        _snapshot is { } shown ? InstantActionWrapupPage.Prints(shown, _layout) : Array.Empty<WrapupPrint>();

    /// <summary>The photograph open full size over the page, or null. The presentation shows it
    /// in its <see cref="ShotViewer"/>; the page only decides when it opens and closes.</summary>
    public StuntShot? Viewing { get; private set; }

    /// <summary>A print's row key by its index in <see cref="Prints"/>.</summary>
    public static string PrintKey(int index) => PrintKeyPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Whether a photograph the page drew as an empty print has landed since, answered once
    /// per landing: the caller's cue to compose the page again. A shot lands on the main thread
    /// (<see cref="StuntCapture.Settle"/>), which is where the presentation's tick reads this.</summary>
    public bool TakeLanded() => _pending.RemoveAll(shot => shot.Landed) > 0;

    /// <summary>Whether a screen is this module's.</summary>
    public bool Owns(OriginalScreen screen) => screen == OriginalScreen.InstantActionWrapup;

    /// <summary>Stands the page on one ended mission's final numbers and opens it.</summary>
    public void ShowWrapup(IaWrapupSnapshot snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Viewing = null;
        _host.Open(OriginalScreen.InstantActionWrapup);
    }

    /// <summary>The page's rows: the CONTINUE plaque at its authored corner, then one per print
    /// in marker order, taking the cursor and the pointer once its frame has landed. While a
    /// photograph is open the page is the one row the viewer covers, which closes it.</summary>
    public void BuildRows(List<OriginalRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (Viewing != null)
        {
            rows.Add(new OriginalRow(
                ViewerKey, string.Empty, OriginalRowKind.Button, 0, 0, BoardFit.AuthoredWidth, BoardFit.AuthoredHeight,
                true, 0, null));
            return;
        }

        if (_layout.Widget(WrapupSection, ContinueKey) == null)
        {
            rows.Add(_host.PlaqueRow(ContinueKey, "CONTINUE", 0, true, 0));
            return;
        }

        var art = InstantActionWrapupPage.ContinueArt(_layout);
        var (x, y) = InstantActionWrapupPage.ContinueAt(_layout);
        var size = OriginalWidgets.StripSize(art, _measure, FallbackPlaqueWidth, FallbackPlaqueHeight);
        rows.Add(new OriginalRow(ContinueKey, string.Empty, OriginalRowKind.Button, x, y, size.Width, size.Height, true, 0, art));
        var prints = Prints;
        for (int i = 0; i < prints.Count; i++)
        {
            var print = prints[i];
            rows.Add(new OriginalRow(
                PrintKey(i), print.Shot.DzName, OriginalRowKind.Button, print.X, print.Y, print.Width, print.Height,
                ShotViewer.CanOpen(print.Shot), 0, null));
        }
    }

    /// <summary>The page carries no scrolling list.</summary>
    public void Lists(List<OriginalList> lists)
    {
    }

    /// <summary>Nothing on the page changes a value, so a sideways step crosses columns instead.</summary>
    public bool StepSideways(IReadOnlyList<OriginalRow> rows, int focus, int direction) => false;

    /// <summary>The page opens no list.</summary>
    public bool CloseDropdown() => false;

    /// <summary>CONTINUE, the page's one door: back to the Instant Action screen the sortie was set
    /// up on, the run forgotten on the way out. A print opens its photograph full size, and the
    /// open photograph's row closes it.</summary>
    public MenuExit? Activate(OriginalRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Key == ContinueKey)
        {
            Leave();
        }
        else if (row.Key == ViewerKey)
        {
            CloseViewer();
        }
        else if (PrintFor(row.Key) is { } shot && ShotViewer.CanOpen(shot))
        {
            Viewing = shot;
            _viewedKey = row.Key;
        }

        return null;
    }

    /// <summary>Back closes an open photograph first. Otherwise it leaves the page the way CONTINUE
    /// does: the mission is over either way and there is nowhere else to stand.</summary>
    public bool Back()
    {
        if (Viewing != null)
        {
            CloseViewer();
            return true;
        }

        Leave();
        return true;
    }

    /// <summary>The page as drawn: the magazine spread behind everything, the four brushstrokes,
    /// the heading and the eight row lines, the post-its as fills with their further lines as one
    /// flowed note each, the photographs as prints (empty until each lands), the tick box as
    /// strokes, and the plaque.</summary>
    public void Compose(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(layers);
        if (_snapshot is not { } shown || _layout.Widget(WrapupSection, ContinueKey) == null)
        {
            _host.ComposePlainPage("INSTANT ACTION", rows, focus, layers);
            return;
        }

        var art = InstantActionWrapupPage.Pictures(_layout);
        layers.Backdrop.Add(art[0]);
        for (int i = 1; i < art.Count; i++)
        {
            layers.Pictures.Add(art[i]);
        }

        layers.Lines.AddRange(InstantActionWrapupPage.Rows(shown, _layout));
        foreach (var postIt in InstantActionWrapupPage.PostIts(shown, _layout))
        {
            layers.Fills.AddRange(InstantActionWrapupPage.PostItPaper(postIt));
            layers.Notes.Add(InstantActionWrapupPage.PostItNote(postIt));
        }

        _pending.Clear();
        foreach (var print in InstantActionWrapupPage.Prints(shown, _layout))
        {
            layers.Fills.AddRange(InstantActionWrapupPage.PrintPaper(print));
            if (InstantActionWrapupPage.PrintPicture(print) is { } picture)
            {
                layers.Pictures.Add(picture);
            }
            else if (!print.Shot.Landed)
            {
                _pending.Add(print.Shot);
            }
        }

        layers.Strokes.AddRange(InstantActionWrapupPage.TickStrokes(shown.Won, _layout));

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Art is { } plaque)
            {
                layers.Plaques.Add(new BoardPlaque(
                    plaque, row.X, row.Y, i, ComposedBoard.PlaqueFrame(plaque.Frames, i == focus, i == _host.PressedRow),
                    string.Empty, BoardInk.LabelNormal));
            }
            else if (i == focus && row.Key.StartsWith(PrintKeyPrefix, StringComparison.Ordinal))
            {
                layers.Fills.Add(_host.FocusMark(row));
            }
        }

        if (_host.SeatPanel(onPaper: true) is { } strip)
        {
            layers.Overlays.Add(strip);
        }
    }

    // The way off the page, shared by CONTINUE and Back: the run is dropped so a later entry to the
    // shell cannot show a stale one, and the Instant Action screen's own door reopens the setup.
    private void Leave()
    {
        _snapshot = null;
        Viewing = null;
        _pending.Clear();
        _openInstantAction();
    }

    // The cursor goes back to the print the photograph was opened from.
    private void CloseViewer()
    {
        Viewing = null;
        _host.FocusKey(_viewedKey);
    }

    private StuntShot? PrintFor(string key)
    {
        if (!key.StartsWith(PrintKeyPrefix, StringComparison.Ordinal)
            || !int.TryParse(key.AsSpan(PrintKeyPrefix.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int index))
        {
            return null;
        }

        var prints = Prints;
        return index < prints.Count ? prints[index].Shot : null;
    }
}
