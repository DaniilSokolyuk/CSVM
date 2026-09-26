using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Menu;
using CSVM.UI.Menu.Original;
using CSVM.UI.Screens;

namespace CSVM.Tests;

/// <summary>The shell's side of <see cref="IOriginalScreenHost"/> as every module's test fake
/// answers it. It holds the screen showing, one focus per screen, the pointer's row and position,
/// and a standing messagebox. It counts each crossing into another family and each re-read a
/// module asks for. A family's own fake derives from this and states only what it does
/// differently. The stateless seams stand on the shell's own numbers, so a row a fake places or
/// marks stands where the shell would.</summary>
internal abstract class OriginalTestHost<TModule> : IOriginalScreenHost
    where TModule : IOriginalScreenModule
{
    // The shell's own sectionless column and row pitch, and a plaque's fallback size. A row placed
    // here stands where the shell would place it, with a rectangle to be hit in.
    private const float ColumnX = 319f;
    private const float FirstY = 300f;
    private const float Pitch = 40f;
    private const float PlaqueWidth = 162f;
    private const float PlaqueHeight = 28f;

    private readonly Func<string, (int Width, int Height)?> _measure;
    private readonly int[] _focus = new int[Enum.GetValues<OriginalScreen>().Length];

    protected OriginalTestHost(
        OriginalScreen screen, Func<string, (int Width, int Height)?> measure, bool canBuildPlane = false)
    {
        _measure = measure;
        Array.Fill(_focus, -1);
        Screen = screen;
        CanBuildPlane = canBuildPlane;
    }

    public TModule Module { get; set; } = default!;

    public OriginalScreen Screen { get; private set; }

    public OriginalDialog? Dialog { get; private set; }

    public IHangarWallet? HangarWallet { get; private set; }

    public int HangarOpens { get; private set; }

    public int CampaignResumes { get; private set; }

    public int InstantActionRefreshes { get; private set; }

    public int RosterRefreshes { get; private set; }

    public bool CanBuildPlane { get; set; }

    public bool DialogOpen => Dialog != null;

    public int PressedRow { get; protected set; } = -1;

    public int HoveredRow { get; protected set; } = -1;

    public int FocusBeforeDialog { get; private set; } = -1;

    public (float X, float Y)? Pointer { get; protected set; }

    public virtual CustomPlaneStore? CampaignPlanes => null;

    public virtual UiStrings MenuStrings => UiStrings.Empty;

    public IReadOnlyList<OriginalRow> Rows
    {
        get
        {
            var rows = new List<OriginalRow>();
            if (!RowsFollowOwnership || Module.Owns(Screen))
            {
                Module.BuildRows(rows);
            }

            return rows;
        }
    }

    // The focus the showing screen stands on, the shell's own EnsureFocus rule. It is the row it
    // was put on while that row is still there, else the first live one.
    public int Focus
    {
        get
        {
            var rows = Rows;
            int focus = _focus[(int)Screen];
            if (focus >= 0 && focus < rows.Count && (!FocusLeavesDeadRows || rows[focus].Enabled))
            {
                return focus;
            }

            focus = rows.ToList().FindIndex(r => r.Enabled);
            _focus[(int)Screen] = focus;
            return focus;
        }
    }

    // The focused key, the standing box's own answer while one stands, as the shell reads it.
    public string FocusedKey
    {
        get
        {
            if (Dialog is { } dialog)
            {
                return dialog.Answers[DialogFocus].Key;
            }

            int focus = Focus;
            return focus >= 0 ? Rows[focus].Key : string.Empty;
        }
    }

    public int FocusedRow
    {
        get => _focus[(int)Screen];
        set => _focus[(int)Screen] = value;
    }

    // Whether the rows are the module's only on a screen it owns, which is where the shell's own
    // dispatch sends them. A fake whose module draws over another family's screen reads them always.
    protected virtual bool RowsFollowOwnership => true;

    // Whether a focus left on a dead row is dropped for the first live one. Where it is not, the
    // screen keeps the row it was put on even once that row goes dark.
    protected virtual bool FocusLeavesDeadRows => true;

    // The answer a standing box stands on, which the axis walks.
    protected int DialogFocus { get; set; }

    public virtual void Open(OriginalScreen screen) => Screen = screen;

    public void FocusKey(string key)
    {
        var rows = Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Key == key)
            {
                _focus[(int)Screen] = i;
                return;
            }
        }
    }

    // A box raised over the screen, opened on its first answer. The focus under it is kept so the
    // screen still draws itself from it, as the shell's own raise does.
    public void RaiseDialog(string message, DialogIcon icon, params OriginalDialogAnswer[] answers)
    {
        FocusBeforeDialog = _focus[(int)Screen];
        Dialog = new OriginalDialog(message, icon, answers);
        DialogFocus = 0;
    }

    public void CloseDialog() => Dialog = null;

    // No frame loop behind this fake, so a module's own re-entrant press has nothing to run.
    public virtual void Frame(MenuCommands commands)
    {
    }

    // A cinema played in front of a fake's screen. The film is per call rather than per fake, since
    // no fake runs a frame loop and nothing reads whether one still stands. A play whose cinema
    // hands back at once opens its screen at once, and a deferred one opens it on the callback.
    public void PlayFilm(Action<Action> play, Action then) => new CinemaFilm().Play(play, then);

    public (int Width, int Height)? Measure(string art) => art.Length > 0 ? _measure(art) : null;

    public virtual void ResumeCampaign() => CampaignResumes++;

    public virtual void RefreshInstantActionRoster() => InstantActionRefreshes++;

    public void RefreshRosterFromStore() => RosterRefreshes++;

    public void OpenHangar(IHangarWallet? wallet)
    {
        HangarWallet = wallet;
        HangarOpens++;
    }

    public virtual MenuExit? BeginSeatWalk() => null;

    // The mission NEXT MISSION launches with no cheat behind it. The three typed latches are the
    // shell's, so no module's fake carries one and the ordinary mission is always the answer. What
    // a cheated buffer does to that mission is the shell's own test.
    public int CheatedMission(int ordinary) => ordinary;

    // One seat behind every fake, so no board of theirs carries the strip a second seat would add.
    public BoardPanel? SeatPanel(bool onPaper) => null;

    // Nothing drawn for a row kind the module has no drawing of its own for. A fake whose every row
    // is drawn by a shared board component never reaches this and wants no rule of its own here.
    public virtual void ComposeGenericRow(OriginalRow row, bool focused, bool pressed, int index, BoardLayers layers)
    {
    }

    public OriginalRow PlaqueRow(string key, string label, int row, bool enabled, int column) =>
        new(key, label, OriginalRowKind.TextButton, ColumnX, FirstY + (row * Pitch),
            PlaqueWidth, PlaqueHeight, enabled, column, null);

    public void ComposePlainPage(string heading, IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        layers.Lines.Add(new BoardLine(heading, ColumnX, FirstY - 44f, 0f, 20f, BoardInk.Heading));
        for (int i = 0; i < rows.Count; i++)
        {
            layers.Lines.Add(new BoardLine(rows[i].Label, rows[i].X, rows[i].Y, rows[i].Width, 16f,
                i == focus ? BoardInk.RowFocused : BoardInk.Row, i));
        }
    }

    public BoardFill FocusMark(OriginalRow row) =>
        new(row.X, row.Y, row.Width, row.Height, 188, 188, 188, 0.75f, Border: true);

    // One answer taken, the way the shell takes one. The box goes first, the focus behind it comes
    // back, then the answer runs over the bare screen.
    internal void Answer(string key)
    {
        var dialog = Dialog!;
        var answer = dialog.Answers.Single(a => a.Key == key);
        Dialog = null;
        _focus[(int)Screen] = FocusBeforeDialog;
        answer.Run?.Invoke();
    }
}
