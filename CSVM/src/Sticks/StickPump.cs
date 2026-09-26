using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CSVM.Utils;
using Godot;

namespace CSVM.Sticks;

/// <summary>
/// The engine side of the stick roster. It loads the stick library once per process and owns
/// the one live <see cref="StickRoster"/> (<see cref="Roster"/>). It pumps it every frame ahead
/// of every reader, so a stick plugged in mid-game appears on the next frame. It keeps pumping while the
/// window is unfocused, so the roster stays current; the roster's own gate blanks the reads.
/// Also hosts the <c>--dump-sticks</c> report.
/// </summary>
public sealed partial class StickPump : Node
{
    // ⚠ Do not move this behind the session node's -1000. A reader in the same frame would then
    // see last frame's stick state and last frame's roster.
    private const int PumpPriority = -1001;

    private static Godot.Collections.Array<int>? _godotRosterSeen;
    private static StickModel[] _godotModels = Array.Empty<StickModel>();

    private StickRoster? _roster;
    private StickProfileSet? _profiles;

    /// <summary>The live roster, or null when sticks are off: <c>--no-pads</c> (so <c>--det</c>),
    /// or no loadable <c>SDL2.dll</c>. Every stick read in the game goes through this one.</summary>
    public static StickRoster? Roster { get; private set; }

    /// <summary>Loads the library and builds the roster for this process, or logs why not and
    /// returns null. The caller adds the node to the tree, which is what starts the pump.
    /// <paramref name="repoRoot"/> is null in an exported build (docs/tooling.md's load order).</summary>
    public static StickPump? Start(string? repoRoot, string? dataRoot)
    {
        StickLabels.Register();
        if (Pads.Disabled)
        {
            Log.Info("core", $"sticks: off (--no-pads, or the --det bundle), SDL2 not loaded");
            return null;
        }

        var roster = Open(repoRoot, dataRoot);
        if (roster is null)
        {
            return null;
        }

        roster.Update();
        Roster = roster;
        return new StickPump
        {
            Name = nameof(StickPump),
            ProcessPriority = PumpPriority,
            ProcessMode = ProcessModeEnum.Always,
            _roster = roster,
            _profiles = StickProfiles.Start(roster),
        };
    }

    /// <summary><c>--dump-sticks</c>: loads the library, logs Godot's pad models, the stick roster
    /// and each stick's resting axes, held buttons and hats, and closes it again. False when no
    /// library loads. Ignores <see cref="Pads.Disabled"/> for the roster, which is a hardware fact,
    /// but not for the reads. A positive <paramref name="watchSeconds"/> then logs every control
    /// that moves for that long, which is how an axis number is matched to a physical movement.
    /// </summary>
    public static bool Dump(string? repoRoot, string? dataRoot, int watchSeconds = 0)
    {
        using var roster = Open(repoRoot, dataRoot);
        if (roster is null)
        {
            return false;
        }

        var godot = GodotModels();
        Log.Info("core", $"sticks dump: Godot pad roster models=[{string.Join(", ", godot)}]{(Pads.Disabled ? " (--no-pads: Godot's roster reads empty)" : string.Empty)}");
        roster.Update();

        // DirectInput answers zeros until a device has been polled a few times after opening.
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(20);
            roster.Update();
        }

        for (int i = 0; i < roster.Sticks.Count; i++)
        {
            var stick = roster.Sticks[i];
            Log.Info("core", $"sticks dump: [{i}] {StickRoster.Describe(stick)} {(roster.InputBlocked ? "reads blocked" : Readings(roster, stick))}");
        }

        Log.Info("core", $"sticks dump: {roster.Sticks.Count} stick(s), SDL {roster.Version}");
        if (watchSeconds > 0 && !roster.InputBlocked)
        {
            Watch(roster, watchSeconds);
        }

        return true;
    }

    // A roster change re-selects the profiles in the same frame, ahead of every reader.
    public override void _Process(double delta)
    {
        if (_roster?.Update() == true)
        {
            _profiles?.Refresh();
        }
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(Roster, _roster))
        {
            Roster = null;
        }

        StickProfiles.Stop(_profiles);
        _profiles = null;
        _roster?.Dispose();
        _roster = null;
    }

    private static StickRoster? Open(string? repoRoot, string? dataRoot)
    {
        string exeDir = Path.GetDirectoryName(OS.GetExecutablePath()) ?? string.Empty;
        var candidates = Sdl2Sticks.Candidates(exeDir, repoRoot, dataRoot);
        var native = Sdl2Sticks.Load(candidates, out string outcome);
        if (native is null)
        {
            Log.Warn("core", $"sticks: off, {outcome}");
            return null;
        }

        Log.Info("core", $"sticks: {outcome}");
        return new StickRoster(native, GodotModels, () => Pads.InputBlocked);
    }

    // Godot's pad models, recomputed only when Pads hands back a new roster: GetJoyInfo marshals a
    // dictionary per pad, and the roster asks every frame. The same instance back means unchanged.
    private static IReadOnlyCollection<StickModel> GodotModels()
    {
        var pads = Pads.Connected();
        if (ReferenceEquals(pads, _godotRosterSeen))
        {
            return _godotModels;
        }

        var models = new List<StickModel>(pads.Count);
        foreach (int pad in pads)
        {
            var info = Input.GetJoyInfo(pad);
            string? vendor = info.TryGetValue("vendor_id", out var v) ? v.ToString() : null;
            string? product = info.TryGetValue("product_id", out var p) ? p.ToString() : null;
            if (StickModel.TryFromDecimal(vendor, product, out var model))
            {
                models.Add(model);
            }
        }

        _godotRosterSeen = pads;
        _godotModels = models.ToArray();
        return _godotModels;
    }

    private static string Readings(StickRoster roster, Stick stick)
    {
        var axes = new List<string>(stick.Axes);
        for (int a = 0; a < stick.Axes; a++)
        {
            axes.Add(Log.Format($"{roster.Axis(stick, a):0.00}"));
        }

        var held = new List<string>();
        for (int b = 0; b < Math.Min(stick.Buttons, StickRoster.MaxButtons); b++)
        {
            if (roster.Button(stick, b))
            {
                held.Add(Log.Format($"{b}"));
            }
        }

        var hats = new List<string>(stick.Hats);
        for (int h = 0; h < stick.Hats; h++)
        {
            hats.Add(roster.Hat(stick, h).ToString());
        }

        return $"axes_at_rest=[{string.Join(" ", axes)}] held=[{string.Join(" ", held)}] hats=[{string.Join(" ", hats)}]{StickRoster.QuirksText(stick.Model)}";
    }

    // The movement half of --dump-sticks=<seconds>. An axis is logged each time it travels a quarter
    // of its half-range from its last logged value. Buttons log on press and release, hats on any
    // change, polled every 20 ms like the dump's settle loop.
    private static void Watch(StickRoster roster, int seconds)
    {
        const float Step = 0.25f;
        var sticks = new List<Stick>(roster.Sticks);
        var axes = new Dictionary<(int, int), float>();
        var buttons = new HashSet<(int, int)>();
        var hats = new Dictionary<(int, int), Bindings.HatDirection>();
        foreach (var stick in sticks)
        {
            for (int a = 0; a < stick.Axes; a++)
            {
                axes[(stick.Instance, a)] = roster.Axis(stick, a);
            }
        }

        Log.Info("core", $"sticks watch: {seconds} s, move one control at a time");
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(20);
            roster.Update();
            for (int i = 0; i < sticks.Count; i++)
            {
                WatchOne(roster, i, sticks[i], Step, axes, buttons, hats);
            }
        }

        Log.Info("core", $"sticks watch: done");
    }

    private static void WatchOne(
        StickRoster roster, int index, Stick stick, float step,
        Dictionary<(int, int), float> axes, HashSet<(int, int)> buttons, Dictionary<(int, int), Bindings.HatDirection> hats)
    {
        string who = Log.Format($"[{index}] \"{stick.Name}\" {stick.Model}");
        for (int a = 0; a < stick.Axes; a++)
        {
            float value = roster.Axis(stick, a);
            float last = axes[(stick.Instance, a)];
            if (Math.Abs(value - last) >= step)
            {
                axes[(stick.Instance, a)] = value;
                Log.Info("core", $"sticks watch: {who} axis {a} {last:0.00} -> {value:0.00}");
            }
        }

        for (int b = 0; b < Math.Min(stick.Buttons, StickRoster.MaxButtons); b++)
        {
            bool held = roster.Button(stick, b);
            if (held != buttons.Contains((stick.Instance, b)))
            {
                _ = held ? buttons.Add((stick.Instance, b)) : buttons.Remove((stick.Instance, b));
                Log.Info("core", $"sticks watch: {who} button {b} {(held ? "pressed" : "released")}");
            }
        }

        for (int h = 0; h < stick.Hats; h++)
        {
            var direction = roster.Hat(stick, h);
            if (direction != hats.GetValueOrDefault((stick.Instance, h)))
            {
                hats[(stick.Instance, h)] = direction;
                Log.Info("core", $"sticks watch: {who} hat {h} {direction}");
            }
        }
    }
}
