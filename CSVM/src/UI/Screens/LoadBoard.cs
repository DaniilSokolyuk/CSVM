using System.Globalization;
using System.IO;
using System.Text;
using CSVM.UI.Boards;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// The load screen drawn over the whole window while a session builds: the composition
/// <see cref="LoadScreens"/> makes, through the campaign boards' authored-pixel surface.
/// A build is one synchronous block that stalls the frame loop, so this board draws from inside
/// it: the build reports each of its steps to <see cref="LoadProgress"/>, and the pump puts the
/// bar's fill and the propeller's frame straight on the moving layer and asks for a frame there.
/// ⚠ Install the pump nowhere but this node's own tree lifetime; a build with no screen over it
/// (every CLI launch bar <c>--debug-load</c>) must leave <see cref="LoadProgress.Current"/> null
/// and gain no draw.
/// </summary>
public sealed partial class LoadBoard : Control
{
    private ComposedBoard _board = LoadScreens.Empty;
    private LoadMotion _motion = LoadScreens.MotionFor(false, null);
    private BoardPalette _palette = BoardPalette.Paper;
    private string _dataRoot = string.Empty;
    private ComposedBoardView? _view;
    private LoadProgress? _progress;
    private int _shots;

    /// <summary>Where each presented pump frame is photographed to (<c>--debug-load=</c>), empty
    /// for the ordinary launch that photographs nothing. A read back of the GPU per pump, which is
    /// why it is off unless a run asks for it.</summary>
    public string CaptureDir { get; set; } = string.Empty;

    /// <summary>Builds the board for one launch. <paramref name="campaign"/> picks the paper sheet
    /// over the blackboard, and <paramref name="missionType"/> the Instant Action dialog whose four
    /// texts the blackboard writes, null for a mode of ours. <paramref name="subject"/> is the
    /// heading a mode of ours takes in place of a dialog. <paramref name="sheet"/> is the campaign
    /// screen's whole content, null where the extraction could not answer for it.</summary>
    public static LoadBoard Build(
        string dataRoot, string zrdrPath, string messagesPath, bool campaign, string subject,
        string? missionType, LoadSheet? sheet = null)
    {
        var board = new LoadBoard
        {
            _dataRoot = dataRoot,
            _board = LoadScreens.For(campaign, subject, missionType, zrdrPath, messagesPath, sheet),
            _motion = LoadScreens.MotionFor(campaign, sheet),
            _palette = campaign ? BoardPalette.Paper : BoardPalette.Chalk,
            MouseFilter = MouseFilterEnum.Ignore,
            FocusMode = FocusModeEnum.None,
        };
        board.SetAnchorsPreset(LayoutPreset.FullRect);
        return board;
    }

    /// <summary>Populates on entry rather than in <see cref="Build"/>: the view sizes itself off
    /// the viewport, which a node outside the tree cannot read. Takes the build's progress with
    /// it, so the screen is the pump and the pump dies with the screen.</summary>
    public override void _Ready()
    {
        var view = ComposedBoardView.Build(_dataRoot);
        AddChild(view);
        _view = view;
        view.Show(_board, _palette, string.Empty, string.Empty);
        _progress = new LoadProgress { Repaint = Repaint };
        LoadProgress.Current = _progress;
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        if (ReferenceEquals(LoadProgress.Current, _progress))
        {
            LoadProgress.Current = null;
        }

        if (_progress is { } progress && progress.Trace.Count > 0)
        {
            Log.Info("ui", $"load screen: {Steps(progress)}");
        }
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        // Track the window (resizable) so the artwork always covers it, the whole-window rule every
        // shared board follows.
        Position = Vector2.Zero;
        Size = GetViewportRect().Size;
    }

    // The build's own steps against wall time, one log line: which fraction each left the bar at,
    // the second of the build it arrived at, and a '-' where the screen was not drawn for it.
    private static string Steps(LoadProgress progress)
    {
        var line = new StringBuilder();
        line.Append(progress.Trace.Count).Append(" step(s), ")
            .Append(progress.Draws).Append(" draw(s):");
        foreach (var step in progress.Trace)
        {
            line.Append(CultureInfo.InvariantCulture, $" {step.Step}={step.Fraction:0.00}@{step.Seconds:0.000}s");
            if (!step.Drawn)
            {
                line.Append('-');
            }
        }

        return line.ToString();
    }

    // One pumped repaint: the fill at the fraction the build has reached and the propeller frame
    // the wall clock is on, put on the moving layer and presented there, since the build owns the
    // loop until it returns and no queued redraw of ours would be flushed before it does.
    private void Repaint()
    {
        if (_view is not { } view || _progress is not { } progress)
        {
            return;
        }

        var art = view.ArtSize(new BoardArt(BoardArtLibrary.Rimage, _motion.FillArt));
        view.PresentMoving(
            LoadScreens.Moving(_motion, art.X, art.Y, progress.Fraction, progress.Frame));
        Capture(progress);
    }

    // The frame just presented, read back off the GPU. The only way to see what a build's own
    // forced frames put on screen, since nothing yields between them.
    private void Capture(LoadProgress progress)
    {
        if (CaptureDir.Length == 0)
        {
            return;
        }

        Directory.CreateDirectory(CaptureDir);
        string name = string.Format(
            CultureInfo.InvariantCulture, "pump{0:D2}_{1:0.00}.png", _shots++, progress.Fraction);
        string path = Path.Combine(CaptureDir, name);
        if (GetViewport()?.GetTexture()?.GetImage() is not { } shot || shot.IsEmpty())
        {
            Log.Warn("ui", $"load screen: no image from the viewport for {path}");
            return;
        }

        var err = shot.SavePng(path);
        Log.Info(
            "ui",
            $"load screen: pump {name} fraction={progress.Fraction} draw={progress.Draws} save={err}");
    }
}
