using System;
using System.Globalization;
using System.IO;
using System.Linq;
using CSVM.Extraction;
using CSVM.Session.Launch;
using CSVM.UI.Boards;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// What the window shows instead of the menu when the data root holds no extraction, or one stamped
/// under another schema. It names the problem and the install folder, with Extract as the default
/// press. It shows a progress view while <see cref="ExtractionFlow"/> runs, a failure view with retry,
/// and hands back to the launcher once the data is there. Keyboard, pad, mouse and touch all drive it
/// through Godot's own focus navigation; the folder picker is <see cref="InstallPicker"/>.
/// ⚠ Do not decide staleness here; <see cref="ExtractionStamp.Standing"/> owns that, and an unstamped
/// tree never reaches this screen. The launcher decides when to ask (<see cref="ExtractionFlow.ProblemAt"/>).
/// </summary>
public sealed partial class NoGameDataScreen : CanvasLayer
{
    /// <summary>The one sentence naming how the data is produced, for the screen and the log line
    /// beside it.</summary>
    public const string Instruction =
        "Choose your Crimson Skies install folder below and press Extract. The install is only read, never changed.";

    /// <summary>The progress view's body.</summary>
    public const string RunningBody =
        "CSVM is reading your Crimson Skies install and writing the game data into the folder below. This takes about a minute. The install is only read, never changed.";

    /// <summary>The failure view's body; the failures themselves follow it.</summary>
    public const string FailedBody =
        "The extraction stopped before it finished. The messages below say where. Try again, or choose another install folder.";

    // Authored against the launchscreen's own 720p metrics, like the corner stamp's 15: a title
    // read at a glance, and a body a couple of points above it.
    private const int TitleFontSize = 30;
    private const int BodyFontSize = 17;

    // Held off the window edge on all four sides, so a long install path wraps inside the screen
    // rather than against the frame.
    private const int EdgeMarginPx = 64;

    // A finger's width, so the buttons are touch targets on a handheld.
    private static readonly Vector2 ButtonSize = new(190, 46);

    private static readonly Color TitleColour = new(0.93f, 0.83f, 0.55f);
    private static readonly Color BodyColour = new(0.84f, 0.87f, 0.92f);
    private static readonly Color DimColour = new(0.58f, 0.62f, 0.69f);
    private static readonly Color NoticeColour = new(0.96f, 0.66f, 0.40f);

    private ExtractionFlow _flow = null!;
    private Action _continue = null!;
    private Action _quit = null!;
    private bool _continued;
    private ExtractionView? _shown;

    private Label _title = null!;
    private Label _body = null!;
    private string _askingBody = string.Empty;
    private Label _hint = null!;
    private Control _asking = null!;
    private LineEdit _path = null!;
    private Button _chooseButton = null!;
    private Label _notice = null!;
    private Control _running = null!;
    private Label _phase = null!;
    private ProgressBar _bar = null!;
    private Label _line = null!;
    private Control _failed = null!;
    private Label _failures = null!;

    /// <summary>Gets the flow this screen shows.</summary>
    public ExtractionFlow Flow => _flow;

    /// <summary>Gets the folder picker.</summary>
    public InstallPicker Picker { get; private set; } = null!;

    /// <summary>Gets the default press of the asking view.</summary>
    public Button ExtractButton { get; private set; } = null!;

    /// <summary>Gets the stale view's way on without re-extracting; hidden for missing data.</summary>
    public Button PlayAnywayButton { get; private set; } = null!;

    /// <summary>Gets the running view's cancel.</summary>
    public Button CancelButton { get; private set; } = null!;

    /// <summary>Gets the failure view's default press, which runs the extraction again.</summary>
    public Button RetryButton { get; private set; } = null!;

    /// <summary>Gets the view the screen last laid out.</summary>
    public ExtractionView? Shown => _shown;

    /// <summary>Gets the title as it reads now.</summary>
    public string TitleText => _title.Text;

    /// <summary>Gets the body as it reads now.</summary>
    public string BodyText => _body.Text;

    /// <summary>Gets the folder field's text.</summary>
    public string PathText => _path.Text;

    /// <summary>Gets the notice under the folder field.</summary>
    public string NoticeText => _notice.Visible ? _notice.Text : string.Empty;

    /// <summary>Gets the failure view's text.</summary>
    public string FailureText => _failures.Text;

    /// <summary>Gets the progress bar's value, 0 to 100.</summary>
    public double BarValue => _bar.Value;

    /// <summary>Gets the progress view's phase line.</summary>
    public string PhaseLine => _phase.Text;

    /// <summary>Whether <paramref name="dataRoot"/> holds nothing to play from: no
    /// <c>extracted</c> directory, or an empty one. Engine-free, so the launcher's branch and the
    /// unit that covers it read the same rule.</summary>
    public static bool Missing(string dataRoot)
    {
        string dir = Path.Combine(dataRoot, "extracted");
        return !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any();
    }

    /// <summary>The asking view's title for <paramref name="problem"/>.</summary>
    public static string Title(DataProblem problem) => problem switch
    {
        DataProblem.Older => "Game data is out of date",
        DataProblem.Newer => "Game data is from a newer CSVM",
        DataProblem.Incomplete => "Game data is incomplete",
        _ => "No game data found",
    };

    /// <summary>The asking view's body for <paramref name="problem"/>, naming the stamp's schema
    /// <paramref name="found"/> against this build's.</summary>
    public static string Body(DataProblem problem, int? found)
    {
        string versions = string.Create(CultureInfo.InvariantCulture,
            $"(data version {found?.ToString(CultureInfo.InvariantCulture) ?? "?"}, this build reads {ExtractionStamp.Schema})");
        return problem switch
        {
            DataProblem.Older =>
                $"The game data in this folder was extracted for an older version of CSVM {versions}. Extract it again from your Crimson Skies install to play. The install is only read, never changed.",
            DataProblem.Newer =>
                $"The game data in this folder was extracted by a newer version of CSVM {versions}. Start that version, or extract the data again for this one. The install is only read, never changed.",
            DataProblem.Incomplete =>
                "The last extraction into this folder did not finish, so parts of the game data are missing. " + Instruction,
            _ => "CSVM plays Crimson Skies from your own copy of the game, and this folder holds none of it yet. " + Instruction,
        };
    }

    /// <summary>The screen over <paramref name="flow"/>, as one layer the caller parents. It ticks
    /// the flow itself. <paramref name="onContinue"/> runs once, when the data is ready or the
    /// player plays on; <paramref name="onQuit"/> is the Quit button.</summary>
    public static NoGameDataScreen Build(ExtractionFlow flow, Action onContinue, Action onQuit)
    {
        // The launchscreen's own tier: this screen is what stands in its place, and nothing else
        // is drawn while it is up.
        var screen = new NoGameDataScreen { Layer = HudLayers.Board, _flow = flow, _continue = onContinue, _quit = onQuit };
        screen.Compose();
        return screen;
    }

    /// <summary>Opens the folder picker, starting at <paramref name="from"/> or the field's folder.</summary>
    public void OpenPicker(string? from = null)
    {
        if (_flow.View == ExtractionView.Running)
        {
            return;
        }

        Picker.Open(from ?? NearestFolder(_path.Text));
    }

    /// <summary>The Extract press: starts the run over the flow's folder, which the field writes as
    /// it is edited, or shows why not.</summary>
    public void Extract()
    {
        _flow.Extract();
        Refresh();
    }

    /// <summary>A folder the picker chose, taken as if the picker had returned it.</summary>
    public void TakePick(string folder)
    {
        bool install = _flow.Pick(folder);
        Refresh();
        (install ? ExtractButton : _chooseButton)?.GrabFocus();
    }

    // Focus can only be grabbed once the screen is in the tree, so the first layout is redone here.
    public override void _Ready()
    {
        _shown = null;
        Refresh();
    }

    public override void _Process(double delta)
    {
        _flow.Tick();
        Refresh();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // A running extraction keeps the window: Esc or B asks it to stop instead of quitting under it.
        if (_flow.View == ExtractionView.Running && @event.IsActionPressed("ui_cancel"))
        {
            _flow.Cancel();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _ExitTree() => _flow.Cancel();

    // The nearest existing folder at or above a typed path, so the picker opens somewhere near it.
    private static string? NearestFolder(string typed)
    {
        string? dir = typed.Trim().Trim('"');
        while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return string.IsNullOrEmpty(dir) ? null : dir;
    }

    private static Label Line(string text, int fontSize, Color colour)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", colour);
        return label;
    }

    private static Button Press(string text, Action pressed)
    {
        var button = new Button { Text = text, CustomMinimumSize = ButtonSize };
        button.Pressed += pressed;
        return button;
    }

    private static HBoxContainer Row(params Control[] items)
    {
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 16);
        foreach (var item in items)
        {
            row.AddChild(item);
        }

        return row;
    }

    // One theme for the screen and its picker, so both read at the same size on a handheld.
    private static Theme ScreenTheme()
    {
        var theme = new Theme { DefaultFontSize = BodyFontSize };
        theme.SetStylebox("normal", "Button", Box(new Color(0.12f, 0.14f, 0.19f), new Color(0.32f, 0.36f, 0.44f), 1));
        theme.SetStylebox("hover", "Button", Box(new Color(0.18f, 0.21f, 0.28f), new Color(0.45f, 0.50f, 0.60f), 1));
        theme.SetStylebox("pressed", "Button", Box(new Color(0.24f, 0.22f, 0.16f), TitleColour, 2));
        theme.SetStylebox("focus", "Button", Box(new Color(0, 0, 0, 0), TitleColour, 2));
        theme.SetColor("font_color", "Button", BodyColour);
        theme.SetColor("font_focus_color", "Button", TitleColour);
        theme.SetColor("font_hover_color", "Button", new Color(1, 1, 1));
        theme.SetStylebox("normal", "LineEdit", Box(new Color(0.08f, 0.09f, 0.12f), new Color(0.32f, 0.36f, 0.44f), 1));
        theme.SetStylebox("focus", "LineEdit", Box(new Color(0, 0, 0, 0), TitleColour, 2));
        theme.SetStylebox("background", "ProgressBar", Box(new Color(0.08f, 0.09f, 0.12f), new Color(0.32f, 0.36f, 0.44f), 1));
        theme.SetStylebox("fill", "ProgressBar", Box(new Color(0.72f, 0.60f, 0.32f), new Color(0.72f, 0.60f, 0.32f), 0));
        return theme;
    }

    private static StyleBoxFlat Box(Color fill, Color border, int borderPx)
    {
        var box = new StyleBoxFlat { BgColor = fill, BorderColor = border };
        box.SetBorderWidthAll(borderPx);
        box.SetCornerRadiusAll(3);
        box.SetContentMarginAll(8);
        return box;
    }

    private void Compose()
    {
        var theme = ScreenTheme();
        var backdrop = new ColorRect { Color = new Color(0.04f, 0.05f, 0.07f) };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(backdrop);

        // The margin is what gives the wrapped lines a measure: a box on the bare full rect wraps
        // against the window edge, which reads as text falling off the screen.
        var margin = new MarginContainer { Theme = theme };
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
        {
            margin.AddThemeConstantOverride(side, EdgeMarginPx);
        }

        var box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        box.AddThemeConstantOverride("separation", 18);
        _title = Line(Title(_flow.Problem), TitleFontSize, TitleColour);
        ExtractionStamp.Standing(_flow.DataRoot, out int? found);
        _askingBody = Body(_flow.Problem, found);
        _body = Line(_askingBody, BodyFontSize, BodyColour);
        box.AddChild(_title);
        box.AddChild(_body);
        box.AddChild(Line($"Data folder:  {Path.Combine(_flow.DataRoot, ExtractionRun.ExtractedFolder)}", BodyFontSize, DimColour));
        box.AddChild(_asking = AskingView());
        box.AddChild(_running = RunningView());
        box.AddChild(_failed = FailedView());
        box.AddChild(_hint = Line(string.Empty, BodyFontSize - 2, DimColour));
        margin.AddChild(box);
        AddChild(margin);

        Picker = InstallPicker.Create(theme);
        Picker.Chosen = TakePick;
        Picker.Canceled += () => _chooseButton.GrabFocus();
        var pickerHint = Line(
            "Controller: d-pad moves, A opens a folder, LB goes up a folder, Y chooses the folder shown, B closes.",
            BodyFontSize - 2, DimColour);

        // A wrapping label reports its height at a width of one character. The dialog grows to
        // its contents' minimum, so that would make it taller than the screen.
        pickerHint.AutowrapMode = TextServer.AutowrapMode.Off;
        Picker.GetVBox().AddChild(pickerHint);
        AddChild(Picker);
        Refresh();
    }

    private Control AskingView()
    {
        var view = new VBoxContainer();
        view.AddThemeConstantOverride("separation", 12);
        view.AddChild(Line("Crimson Skies install folder", BodyFontSize, DimColour));
        _path = new LineEdit
        {
            Text = _flow.InstallPath,
            PlaceholderText = "The folder that holds the ZBD and GOSDATA folders",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, ButtonSize.Y),
        };
        _path.TextChanged += text => _flow.InstallPath = text;
        _path.TextSubmitted += _ => Extract();
        _chooseButton = Press("Choose folder...", () => OpenPicker());
        var field = new HBoxContainer();
        field.AddThemeConstantOverride("separation", 12);
        field.AddChild(_path);
        field.AddChild(_chooseButton);
        view.AddChild(field);
        _notice = Line(string.Empty, BodyFontSize, NoticeColour);
        view.AddChild(_notice);
        ExtractButton = Press("Extract", Extract);
        PlayAnywayButton = Press("Play anyway", Continue);
        PlayAnywayButton.Visible = _flow.Problem is DataProblem.Older or DataProblem.Newer;
        view.AddChild(Row(ExtractButton, PlayAnywayButton, Press("Quit", () => _quit())));
        return view;
    }

    private Control RunningView()
    {
        var view = new VBoxContainer();
        view.AddThemeConstantOverride("separation", 12);
        _phase = Line(string.Empty, BodyFontSize, BodyColour);
        _bar = new ProgressBar { MinValue = 0, MaxValue = 100, CustomMinimumSize = new Vector2(0, 28) };
        _line = Line(string.Empty, BodyFontSize - 2, DimColour);
        _line.AutowrapMode = TextServer.AutowrapMode.Off;
        _line.ClipText = true;
        _line.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        CancelButton = Press("Cancel", () => _flow.Cancel());
        view.AddChild(_phase);
        view.AddChild(_bar);
        view.AddChild(_line);
        view.AddChild(Row(CancelButton));
        return view;
    }

    private Control FailedView()
    {
        var view = new VBoxContainer();
        view.AddThemeConstantOverride("separation", 12);
        _failures = Line(string.Empty, BodyFontSize, NoticeColour);
        RetryButton = Press("Try again", Extract);
        view.AddChild(_failures);
        view.AddChild(Row(RetryButton, Press("Choose another folder", () => OpenPicker()), Press("Quit", () => _quit())));
        return view;
    }

    // Runs the launcher's continuation at most once, whichever way the screen is left.
    private void Continue()
    {
        if (_continued)
        {
            return;
        }

        _continued = true;
        _continue();
    }

    // Lays the screen out for the flow's view. A view change moves focus to its default press, so a
    // pad or the Enter key always has something to press.
    private void Refresh()
    {
        var view = _flow.View;
        if (view == ExtractionView.Done)
        {
            Continue();
            return;
        }

        bool changed = _shown != view;
        _shown = view;
        _asking.Visible = view == ExtractionView.Asking;
        _running.Visible = view == ExtractionView.Running;
        _failed.Visible = view == ExtractionView.Failed;
        _title.Text = view switch
        {
            ExtractionView.Running => "Extracting game data",
            ExtractionView.Failed => "Extraction failed",
            _ => Title(_flow.Problem),
        };
        _body.Text = view switch
        {
            ExtractionView.Running => RunningBody,
            ExtractionView.Failed => FailedBody,
            _ => _askingBody,
        };
        _notice.Text = _flow.Notice ?? string.Empty;
        _notice.Visible = _flow.Notice != null;
        if (_path.Text != _flow.InstallPath)
        {
            _path.Text = _flow.InstallPath;
        }

        _phase.Text = _flow.Cancelling ? "Cancelling..." : _flow.PhaseText();
        _bar.Value = _flow.Fraction * 100;
        _line.Text = _flow.LatestLine;
        CancelButton.Disabled = _flow.Cancelling;
        _failures.Text = string.Join("\n", _flow.Failures);
        _hint.Text = view == ExtractionView.Running
            ? "Esc or B cancels. Cancelling stops at the next archive."
            : "Arrow keys or d-pad to move, Enter or A to press, Esc to quit.";
        if (changed && IsInsideTree())
        {
            (view switch
            {
                ExtractionView.Running => CancelButton,
                ExtractionView.Failed => RetryButton,
                _ => ExtractButton,
            }).GrabFocus();
        }
    }
}
