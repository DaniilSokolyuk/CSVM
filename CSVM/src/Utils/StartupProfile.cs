using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CSVM.Utils;

/// <summary>
/// The always-on startup timing report: one <c>[perf] startup …</c> line per session build,
/// split into the phases the build spends its time in. It only reports, no thresholds, no
/// verdicts, no comparisons; a comparison needs a warm-up protocol this class deliberately does
/// not own. Line grammar, the phase vocabulary and <c>boot</c>/<c>rest</c>/<c>first_frame</c>'s
/// meaning: docs/org/startup-profile.md.
/// ⚠ The line asserts <c>total = boot + Σ(phases) + rest + first_frame</c>. Keep every phase a
/// leaf, never nested inside another, or the sum silently double-counts.
/// </summary>
public sealed class StartupProfile
{
    private readonly List<string> _order = new();
    private readonly Dictionary<string, double> _phases = new(StringComparer.Ordinal);
    private readonly long _buildStart = Stopwatch.GetTimestamp();
    private readonly double _bootMs;
    private readonly string _mode;

    private long _buildEnd;
    private double _buildMs = -1;
    private double _firstFrameMs = -1;
    private int _framesSinceBuild;
    private bool _emitted;

    /// <param name="mode">The session shape (<c>fly</c>, <c>freecam</c>, …), the same token the
    /// log file is named after.</param>
    /// <param name="bootMs">Milliseconds from engine start to this constructor.</param>
    public StartupProfile(string mode, double bootMs)
    {
        _mode = mode;
        _bootMs = bootMs;
    }

    /// <summary>The session currently being timed, or null when nothing is. The in-engine test
    /// harness builds worlds through the very same code and deliberately leaves this null, so its
    /// eight census worlds do not accumulate into one nonsense line.</summary>
    public static StartupProfile? Current { get; set; }

    /// <summary>What this session built, as one ready-formatted <c>key=value</c> fragment
    /// (<c>chapter=C1</c>, <c>plane=player_bhawk</c>), the line's scenario identity.</summary>
    public string Subject { get; set; } = "";

    /// <summary>The recorded phases in the order first seen, for a reader that wants to aggregate
    /// or categorize them (the harness's <c>PhaseAttribution</c>) without emitting the line.
    /// Never calls <see cref="EndBuild"/> itself, so a caller timing the build with its own
    /// stopwatch (the test harness's per-world-build watch) can read the phases mid-build.</summary>
    public IReadOnlyDictionary<string, double> Phases => _phases;

    /// <summary>Opens a phase measurement; hand the returned mark to <see cref="Record"/>.</summary>
    public static long Mark() => Stopwatch.GetTimestamp();

    /// <summary>Adds the time since <paramref name="mark"/> to a phase of the session being timed.
    /// A no-op when no session is under measurement, so the shared build code carries the calls
    /// unconditionally. Repeated calls with the same name accumulate, the three
    /// <c>GameZ.Load</c>s of a flight session are one <c>gamez</c> figure.</summary>
    public static void Record(string phase, long mark)
    {
        Current?.Add(phase, MsSince(mark));
    }

    /// <summary>Adds an already-measured span to a phase.</summary>
    public void Add(string phase, double ms)
    {
        if (!_phases.ContainsKey(phase))
        {
            _order.Add(phase);
            _phases[phase] = 0;
        }
        _phases[phase] += ms;
    }

    /// <summary>Closes the session build; everything after this counts towards
    /// <c>first_frame</c>. Idempotent, the first call wins, so a build that returns through
    /// several paths cannot restart the clock.</summary>
    public void EndBuild()
    {
        if (_buildMs >= 0)
        {
            return;
        }
        _buildEnd = Stopwatch.GetTimestamp();
        _buildMs = MsBetween(_buildStart, _buildEnd);
    }

    /// <summary>Call once per rendered frame. The second frame after the build is the one that
    /// proves the first has been drawn, so that is where the line is emitted.</summary>
    public void Frame()
    {
        if (_emitted || _buildMs < 0)
        {
            return;
        }
        _framesSinceBuild++;
        if (_framesSinceBuild < 2)
        {
            return;
        }
        _firstFrameMs = MsSince(_buildEnd);
        Emit();
    }

    /// <summary>Writes the line, once. Called for its own sake only by the teardown path, which
    /// catches the runs that quit inside the build and never render a frame.</summary>
    public void Emit()
    {
        if (_emitted)
        {
            return;
        }
        _emitted = true;
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
        EndBuild();
        Log.Info("perf", $"startup {Format()}");
    }

    /// <summary>The line body, every <c>key=value</c> after <c>startup</c>. Pure and invariant
    /// (no Godot API, no current culture), so it is readable from a test host and a decimal point
    /// never turns into a field separator.</summary>
    public string Format()
    {
        double phaseSum = 0;
        foreach (double ms in _phases.Values)
        {
            phaseSum += ms;
        }
        double build = _buildMs < 0 ? 0 : _buildMs;
        double total = _bootMs + build + Math.Max(_firstFrameMs, 0);

        var sb = new StringBuilder();
        sb.Append("mode=").Append(_mode);
        if (Subject.Length > 0)
        {
            sb.Append(' ').Append(Subject);
        }
        Append(sb, "total", total);
        Append(sb, "boot", _bootMs);
        foreach (string phase in _order)
        {
            Append(sb, phase, _phases[phase]);
        }
        Append(sb, "rest", build - phaseSum);
        sb.Append(" first_frame=");
        if (_firstFrameMs < 0)
        {
            sb.Append("none");
        }
        else
        {
            sb.Append(Ms(_firstFrameMs));
        }
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string key, double ms)
    {
        sb.Append(' ').Append(key).Append('=').Append(Ms(ms));
    }

    private static string Ms(double ms) => ms.ToString("0.0", CultureInfo.InvariantCulture);

    private static double MsSince(long mark) => MsBetween(mark, Stopwatch.GetTimestamp());

    private static double MsBetween(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
}
