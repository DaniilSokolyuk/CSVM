using CSVM.Flight.Hud;
using CSVM.Flight.Modes;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// The end-of-run results overlay for Stunt Flying, <see cref="ResultsBoard"/>'s shell. When the
/// run's last Danger Zone is cleared (<see cref="StuntMission.RunCompleted"/>) this shows a
/// centred panel: the <see cref="StuntSplits"/> section (per-zone splits, total and best-time
/// comparison against <see cref="ScoreStore"/>) under the plane + chapter heading. A plain
/// Godot-UI overlay, distinct from the in-flight HUD's hand-drawn marker/dials. R and pad Y still
/// start a fresh run directly. Construction detail: this module's entry in
/// docs/architecture.md.</summary>
public sealed partial class StuntScoreboard : ResultsBoard
{
    // Base metrics at 720p (the default window); scaled up on taller viewports so the board reads
    // at 1080p/1440p/4K without ballooning. All TUNE.
    private const int TitleFont = 26;
    private const int ContextFont = 15;

    private StuntMission _mission = null!;
    private ScoreStore _store = null!;
    private string _scoreKey = "";
    private string _planeDisplay = "";
    private string _context = "";

    /// <summary>This pilot's Danger Zone photographs, drawn as a <see cref="StuntShotStrip"/> under
    /// the splits. Null, or a run that latched none, draws no strip at all.</summary>
    public StuntCapture? Shots { get; set; }

    // A rerun (StuntMission.Reset) clears AllComplete, which the shell's _Process turns into the
    // hide and the release.
    protected override bool StillEnded => _mission.AllComplete;

    /// <summary>Builds the (hidden) overlay and subscribes to the run's completion. Add it to the
    /// HUD canvas last so it draws over the marker/dials; feed nothing per-frame, it wakes itself
    /// on <see cref="StuntMission.RunCompleted"/>.</summary>
    public static StuntScoreboard Build(StuntMission mission, string planeDisplay, string context,
        ScoreStore store, string scoreKey, bool exitsToMenu, PauseState state,
        System.Func<int, MenuInput> inputFor)
    {
        var board = new StuntScoreboard
        {
            _mission = mission,
            _store = store,
            _scoreKey = scoreKey,
            _planeDisplay = planeDisplay,
            _context = context,
        };
        board.InitShell(state, exitsToMenu, inputFor);
        mission.RunCompleted += board.OnRunCompleted;
        return board;
    }

    public override void _ExitTree() => _mission.RunCompleted -= OnRunCompleted;

    // This board draws inside one pilot's pane, so its metrics are damped by the pane share
    // (identical to plain Max(1, h/720) at any full-screen view 720p or taller).
    protected override float BoardScale() => Mathf.Max(0.5f, HudMetrics.Scale(this, 720f));

    private void OnRunCompleted()
    {
        float total = _mission.Elapsed;
        float? prevBest = _store.GetBest(_scoreKey);
        bool newBest = _store.RecordIfBest(_scoreKey, total);
        string bestSuffix = newBest ? " (NEW BEST)"
            : prevBest.HasValue ? $" (best {StuntMission.FormatTime(prevBest.Value)})" : "";
        Log.Info("flight", $"stunt: run complete {StuntMission.FormatTime(total)}{bestSuffix}");
        // Log the split table too (the splits are otherwise only visible on the rendered board,
        // this makes a run's scoring reviewable from the headless log).
        float prev = 0f;
        int n = 1;
        foreach (var z in _mission.InCompletionOrder())
        {
            string name = z.Description.Length > 0 ? z.Description : z.DzName;
            Log.Info("flight",
                $"  split {n}. {name}: +{StuntMission.FormatTime(z.CompletedAt - prev)} (@ {StuntMission.FormatTime(z.CompletedAt)})");
            prev = z.CompletedAt;
            n++;
        }
        Populate(new StuntSummary(_mission, total, prevBest, newBest));
        Wake();
    }

    private void Populate(StuntSummary run)
    {
        float s = BoardScale();
        var body = BeginPanel(s);

        body.AddChild(Centered(Label("STUNT FLYING COMPLETE", (int)(TitleFont * s), TitleColor)));
        string ctx = _context.Length > 0 ? $"{_context}   ·   {_planeDisplay}" : _planeDisplay;
        body.AddChild(Centered(Label(ctx, (int)(ContextFont * s), ContextColor)));
        body.AddChild(Separator(s));

        StuntSplits.Add(body, run, s, separatorBeforeTotal: true);
        AddShotStrip(body, Shots, s);

        body.AddChild(Separator(s));
        AddStandardMenu(body, s);
    }
}
