using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CSVM.Utils;

/// <summary>
/// One boundary of a session build, in the order the build crosses them. Each step names the
/// authored fraction at its own index of <see cref="LoadProgress.Milestones"/>, so the table and
/// this list are the one ordering; nothing derives a fraction from a measured duration.
/// Decode: docs/org/loading-screen.md.
/// </summary>
public enum LoadStep
{
    /// <summary>The sky and the saved options, before any of the session is built.</summary>
    RenderState,

    /// <summary>The chapter's mip bias, the launch's first read of chapter data.</summary>
    ChapterPaths,

    /// <summary>The session scaffold: the world root, the resolvers, the dice and the clock.</summary>
    Scaffold,

    /// <summary>The rigs, the viewer set, the screen wash and the smoke tunables.</summary>
    Rigs,

    /// <summary>The build's own paths, resolved against the chapter and the mission.</summary>
    Paths,

    /// <summary>The Instant Action and campaign directors and the cutscene host.</summary>
    Directors,

    /// <summary>The texture and sound archives, open.</summary>
    Archives,

    /// <summary>The mission's reader files.</summary>
    MissionFiles,

    /// <summary>The chapter's gamez, built into a scene.</summary>
    WorldScene,

    /// <summary>The clutter: the forests, the bushes and the city blocks.</summary>
    Clutter,

    /// <summary>The animation program, compiled.</summary>
    AnimProgram,

    /// <summary>The animation runtime, bound to the built world.</summary>
    RuntimeBound,

    /// <summary>The sound prewarm.</summary>
    SoundPrewarm,

    /// <summary>The world stage finished: the plane, the labs and the spectator.</summary>
    WorldStage,

    /// <summary>The flight rigs: the player's aeroplane, its cameras and its HUD.</summary>
    PlayerRigs,

    /// <summary>The build is done. The screen is torn down from here, so nothing above it is
    /// ever drawn.</summary>
    Finished,
}

/// <summary>
/// The load screen's progress while a build holds the frame loop: the original's authored
/// milestone table, its monotonic setter and its wall-clock-throttled repaint pump. Engine-free,
/// so the table, the fill width and the propeller's frame settle without a window;
/// <c>UI.Screens.LoadBoard</c> supplies the <see cref="Repaint"/> that actually draws.
/// Ambient like <see cref="StartupProfile"/>: a build reports its steps unconditionally and a
/// launch with no screen over it leaves <see cref="Current"/> null, so every call is a no-op.
/// Decode: docs/org/loading-screen.md.
/// </summary>
public sealed class LoadProgress
{
    /// <summary>How long the pump holds off between draws that leave the bar where it was, the
    /// original's own throttle, so a step reported twice at one fraction turns the propeller at
    /// most ten times a second.</summary>
    public const double PumpSeconds = 0.1;

    /// <summary>The propeller cycle's authored rate.</summary>
    public const double PropellerFps = 6.0;

    /// <summary>How many frames the cycle holds. The extraction carries the six range endpoints
    /// the script names, so the cycle is six frames, not the 38 the numbering spans.</summary>
    public const int PropellerFrames = 6;

    private readonly Func<double> _seconds;
    private readonly List<StepTrace> _trace = new();

    private double _lastDraw = double.NegativeInfinity;
    private float _fraction;
    private int _draws;

    /// <summary>Builds a progress over a wall clock in seconds, its own by default. A caller
    /// hands one in to walk the table without waiting on a real clock.</summary>
    public LoadProgress(Func<double>? seconds = null)
    {
        var watch = Stopwatch.StartNew();
        _seconds = seconds ?? (() => watch.Elapsed.TotalSeconds);
    }

    /// <summary>The sixteen authored fractions, one per <see cref="LoadStep"/> in step order.
    /// Monotonic, and the highest is 0.90: the original measures nothing and never sets 1.0, so a
    /// full bar is never drawn.</summary>
    public static IReadOnlyList<float> Milestones { get; } = new[]
    {
        0.01f, 0.02f, 0.04f, 0.07f, 0.10f, 0.10f, 0.20f, 0.30f,
        0.40f, 0.50f, 0.60f, 0.70f, 0.71f, 0.72f, 0.80f, 0.90f,
    };

    /// <summary>The build being drawn over, or null when nothing is. A CLI launch never shows a
    /// load screen, so it leaves this null and gains no draw.</summary>
    public static LoadProgress? Current { get; set; }

    /// <summary>What draws the screen at its current fraction and frame. Null until a board
    /// installs one, which is what keeps a headless walk of the table drawing nothing.</summary>
    public Action? Repaint { get; set; }

    /// <summary>How far the bar is filled, 0 before the first step.</summary>
    public float Fraction => _fraction;

    /// <summary>How many times the screen has been drawn since the build started.</summary>
    public int Draws => _draws;

    /// <summary>Every step the build has reported, in the order it reported them. What the load
    /// screen writes its one log line out of, so a bar reported not to move is read back against
    /// wall time instead of guessed at.</summary>
    public IReadOnlyList<StepTrace> Trace => _trace;

    /// <summary>Which of the cycle's frames is showing now.</summary>
    public int Frame => FrameAt(_seconds());

    /// <summary>Reports one build step to the screen over it, if there is one.</summary>
    public static void Report(LoadStep step) => Current?.Reach(step);

    /// <summary>The lit pixels of a fill bitmap <paramref name="fillWidth"/> wide at
    /// <paramref name="fraction"/>: the repaint's own pixel clip against the bitmap's width, which
    /// cuts through a lamp part-way rather than counting lamps.</summary>
    public static int FillPixels(int fillWidth, float fraction) =>
        fillWidth <= 0 ? 0 : (int)Math.Floor(fillWidth * Math.Clamp(fraction, 0f, 1f));

    /// <summary>The cycle's frame index at that many seconds, at the authored rate.</summary>
    public static int FrameAt(double seconds) =>
        seconds <= 0d ? 0 : (int)((long)(seconds * PropellerFps) % PropellerFrames);

    /// <summary>Sets the step's fraction and draws, which is the order the original's own load
    /// steps use: do the work, set the fraction, draw. A step the bar moved on is drawn whatever
    /// the clock says; the throttle holds off only a step that moved it nowhere.
    /// ⚠ Do not throttle a moved bar. Our phases are fast enough that a whole build can cross ten
    /// of them inside one throttle window, which shows the first fraction and then the mission.
    /// </summary>
    public void Reach(LoadStep step)
    {
        double now = _seconds();
        int before = _draws;
        if (Set(Milestones[(int)step]))
        {
            Draw(now);
        }
        else
        {
            Pump();
        }

        _trace.Add(new StepTrace(step, _fraction, now, _draws > before));
    }

    /// <summary>Stores a fraction, answering whether the bar moved. Monotonic: a fraction below
    /// the stored one is ignored, so no step drags the bar backwards and the table's repeated
    /// 0.10 is harmless.</summary>
    public bool Set(float fraction)
    {
        if (fraction <= _fraction)
        {
            return false;
        }

        _fraction = fraction;
        return true;
    }

    /// <summary>Draws, unless the last draw was less than <see cref="PumpSeconds"/> ago.</summary>
    public void Pump()
    {
        double now = _seconds();
        if (now - _lastDraw < PumpSeconds)
        {
            return;
        }

        Draw(now);
    }

    private void Draw(double now)
    {
        _lastDraw = now;
        _draws++;
        Repaint?.Invoke();
    }

    /// <summary>One reported step: which it was, the bar it left behind, the second of the build it
    /// arrived at, and whether the screen was drawn for it.</summary>
    public readonly record struct StepTrace(LoadStep Step, float Fraction, double Seconds, bool Drawn);
}
