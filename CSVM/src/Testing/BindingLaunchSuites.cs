using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Bindings;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using Godot;

namespace CSVM.Testing;

/// <summary>The launch-time keymap read, over the real seat rather than over the seam alone: a
/// stored profile reaches a human rig's live flight map, the two players' files stay apart, a file
/// this build cannot read leaves the seat on the shipped defaults, and the deterministic gate makes
/// the same stored file invisible.
/// ⚠ This suite deliberately opens the gate <c>--run-tests</c> closes, so it points
/// <see cref="BindingStore.DirectoryOverride"/> at its own scratch directory first and restores both
/// in a finally. Nothing here may read or write the keymap saved at this machine's controls.
/// </summary>
internal static class BindingLaunchSuites
{
    private static readonly Binding Zed = new(DeviceId.Keyboard, BindingControl.Key((int)Key.Z));

    [Suite("bindings-launch-load",
        "The saved keymap at launch: a human rig built with a stored profile flies the stored "
        + "control rather than the shipped one, player two's file leaves player one's seat alone, a "
        + "file that is not JSON and a file naming an action this build dropped both leave the seat "
        + "on the shipped defaults without throwing, a fire action the player unbound loads unbound "
        + "while the rest of the seat stays flyable, and with the deterministic gate shut the same "
        + "stored file changes nothing at all")]
    internal static void BindingsLaunchLoad(TestContext ctx)
    {
        string dir = Path.Combine(ctx.ScratchDir, "bindings-launch-load");
        Directory.CreateDirectory(dir);
        string? previousDir = BindingStore.DirectoryOverride;
        BindingStore.DirectoryOverride = dir;
        try
        {
            Write(dir, 1, NitroOnZed());
            Write(dir, 2, RocketsOnZed());
            LaunchBindings.Configure(deterministic: false);
            Loaded(ctx);
            Bound(ctx);
            PerPlayer(ctx);
            Damaged(ctx, dir);
            Gate(ctx);
        }
        finally
        {
            // Back to the state --run-tests launched in, whatever the checks above did.
            LaunchBindings.Configure(deterministic: true);
            BindingStore.DirectoryOverride = previousDir;
        }
    }

    // A human rig flies what its player saved, and an AI rig in the same mission does not read it.
    private static void Loaded(TestContext ctx)
    {
        var pilot = new FlightController { PlayerIndex = 0 };
        try
        {
            pilot.LoadSavedKeymap();
            ctx.Check(Holds(pilot, InputAction.Nitro, Zed),
                $"player one's seat flies the stored Nitro control ({Names(pilot, InputAction.Nitro)})");
            ctx.Check(pilot.FlightKeymap.Bindings(InputAction.PitchUp).Count > 0,
                $"…and every action the file left alone keeps its shipped default");
        }
        finally
        {
            pilot.Free();
        }
    }

    // The wiring, not the rule: a rig assembled the way a session assembles one picks the file up
    // on its own, and the AI rig beside it does not.
    private static void Bound(TestContext ctx)
    {
        var human = new FlightController();
        var ai = new FlightController();
        try
        {
            human.Bind(Build(0, human: true));
            ai.Bind(Build(0, human: false));
            ctx.Check(Holds(human, InputAction.Nitro, Zed),
                $"a human rig's own construction loads the file ({Names(human, InputAction.Nitro)})");
            ctx.Check(!Holds(ai, InputAction.Nitro, Zed),
                $"…and an AI rig built with the same index never reads a player's keymap");
        }
        finally
        {
            human.Free();
            ai.Free();
        }
    }

    private static FlightControllerBuild Build(int player, bool human) => new()
    {
        PlayerIndex = player,
        IsHumanPiloted = human,
        PlaneModel = new Node3D(),
        Shake = new PlaneShake(new ShakeDefs()),
    };

    // Two seats, two files: the second load must not reach into the first seat's map.
    private static void PerPlayer(TestContext ctx)
    {
        var one = new FlightController { PlayerIndex = 0 };
        var two = new FlightController { PlayerIndex = 1 };
        try
        {
            one.LoadSavedKeymap();
            two.LoadSavedKeymap();
            ctx.Check(Holds(two, InputAction.FireRockets, Zed) && !Holds(two, InputAction.Nitro, Zed),
                $"player two's seat flies player two's file ({Names(two, InputAction.FireRockets)})");
            ctx.Check(Holds(one, InputAction.Nitro, Zed) && !Holds(one, InputAction.FireRockets, Zed),
                $"…and player one's seat is untouched by it ({Names(one, InputAction.Nitro)})");
        }
        finally
        {
            one.Free();
            two.Free();
        }
    }

    // A file no reader can make sense of: the seat launches, and it launches flyable.
    private static void Damaged(TestContext ctx, string dir)
    {
        File.WriteAllText(Path.Combine(dir, BindingStore.FileNameFor(3)), "{ \"version\": 1, \"cont",
            new UTF8Encoding(false));
        var pilot = new FlightController { PlayerIndex = 2 };
        try
        {
            pilot.LoadSavedKeymap();
            ctx.Check(SameAsDefaults(pilot),
                $"a truncated keymap file launches the seat on the shipped defaults, whole");
            ctx.Check(pilot.FlightKeymap.Bindings(InputAction.FireGuns).Count > 0,
                $"…so the seat still has a fire button");
        }
        finally
        {
            pilot.Free();
        }
    }

    // The gate: the same stored file, the same seat, the deterministic run.
    private static void Gate(TestContext ctx)
    {
        LaunchBindings.Configure(deterministic: true);
        var pilot = new FlightController { PlayerIndex = 0 };
        try
        {
            pilot.LoadSavedKeymap();
            ctx.Check(!Holds(pilot, InputAction.Nitro, Zed) && SameAsDefaults(pilot),
                $"a deterministic run ignores the stored profile entirely ({Names(pilot, InputAction.Nitro)})");
        }
        finally
        {
            pilot.Free();
        }
    }

    private static bool Holds(FlightController pilot, InputAction action, Binding binding)
    {
        foreach (var held in pilot.FlightKeymap.Bindings(action))
        {
            if (ActionMap.SameControl(held, binding))
                return true;
        }

        return false;
    }

    private static bool SameAsDefaults(FlightController pilot)
    {
        var shipped = DefaultBindings.MapFor(InputContext.Flight, default);
        foreach (var action in DefaultBindings.ActionsIn(InputContext.Flight))
        {
            var mine = pilot.FlightKeymap.Bindings(action);
            var theirs = shipped.Bindings(action);
            if (mine.Count != theirs.Count)
                return false;
            for (int i = 0; i < mine.Count; i++)
            {
                if (mine[i] != theirs[i])
                    return false;
            }
        }

        return true;
    }

    private static string Names(FlightController pilot, InputAction action)
    {
        var parts = new List<string>();
        foreach (var binding in pilot.FlightKeymap.Bindings(action))
            parts.Add(BindingStore.Encode(binding));
        return $"{action}=[{string.Join(", ", parts)}]";
    }

    private static string NitroOnZed() => Rebound(InputAction.Nitro);

    private static string RocketsOnZed() => Rebound(InputAction.FireRockets);

    // A stored profile that genuinely differs from the shipped set, written through the real
    // serializer so the file is one the shipping reader would meet.
    private static string Rebound(InputAction action)
    {
        var profile = BindingProfile.Defaults(default, readsKeyboard: true);
        var map = profile.Map(InputContext.Flight);
        map.Clear(action);
        map.Add(action, Zed);
        return BindingStore.Serialize(1, profile);
    }

    private static void Write(string dir, int player, string json) =>
        File.WriteAllText(Path.Combine(dir, BindingStore.FileNameFor(player)), json, new UTF8Encoding(false));
}
