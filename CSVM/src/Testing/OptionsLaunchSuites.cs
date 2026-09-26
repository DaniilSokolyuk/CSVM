using System.IO;
using CSVM.Flight.Camera;
using CSVM.Flight.Hangar;
using CSVM.Session.Roster;
using CSVM.Utils;

namespace CSVM.Testing;

/// <summary>The saved gameplay options at launch, through the real options store: a saved value
/// reaches the roster policy a plain launch builds, the <c>--difficulty=</c> flag outranks the
/// saved tier, a <c>--det</c> run never reads either, and a value the store refuses leaves the
/// default standing.
/// ⚠ The store is pointed at this suite's own scratch directory and restored in a finally, so
/// nothing here reads or writes the options saved at this machine's controls.</summary>
internal static class OptionsLaunchSuites
{
    [Suite("options-difficulty-launch",
        "The saved difficulty at launch: a saved hard reaches FlightRosterPolicy.Difficulty on a plain "
        + "launch, --difficulty=normal on the command line keeps Normal over the same saved word, a "
        + "--det run reads no saved option, a saved hardest reaches the policy as Hardest, and a word "
        + "outside the store's set loads as never set so the launch keeps the default")]
    internal static void OptionsDifficultyLaunch(TestContext ctx)
    {
        string dir = Path.Combine(ctx.ScratchDir, "options-difficulty-launch");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        string? previous = OptionsStore.DirectoryOverride;
        OptionsStore.DirectoryOverride = dir;
        try
        {
            OptionsStore.UserOptions().Save(new OptionsDef { Difficulty = Difficulty.Word(Difficulty.Hard) });
            ctx.Check(Launched(System.Array.Empty<string>()) == Difficulty.Hard,
                $"a saved hard reaches the roster policy on a plain launch ({Launched(System.Array.Empty<string>())})");
            ctx.Check(Launched(new[] { "--difficulty=normal" }) == Difficulty.Normal,
                $"--difficulty=normal keeps Normal over the saved hard ({Launched(new[] { "--difficulty=normal" })})");
            ctx.Check(Launched(new[] { "--difficulty=hardest" }) == Difficulty.Hardest,
                $"and --difficulty=hardest keeps Hardest over it ({Launched(new[] { "--difficulty=hardest" })})");
            ctx.Check(Launched(new[] { "--det" }) == Difficulty.Normal,
                $"a --det run reads no saved option and flies Normal ({Launched(new[] { "--det" })})");

            OptionsStore.UserOptions().Save(new OptionsDef { Difficulty = Difficulty.Word(Difficulty.Hardest) });
            ctx.Check(Launched(System.Array.Empty<string>()) == Difficulty.Hardest,
                $"a saved hardest reaches the policy as Hardest ({Launched(System.Array.Empty<string>())})");

            // Instant Action's name for the same tier is the flag's convenience, not a word the file
            // carries: written by hand, it loads as never set and the launch keeps the default.
            File.WriteAllText(Path.Combine(dir, "options.json"),
                "{\"version\": 1, \"difficulty\": \"veteran\"}", new System.Text.UTF8Encoding(false));
            ctx.Check(OptionsStore.UserOptions().Load().Difficulty == null && Launched(System.Array.Empty<string>()) == Difficulty.Normal,
                $"a word outside the store's set loads as never set and the launch keeps Normal ({Launched(System.Array.Empty<string>())})");
        }
        finally
        {
            OptionsStore.DirectoryOverride = previous;
        }
    }

    [Suite("options-targeting-launch",
        "The saved nearest-after-a-kill setting at launch: with nothing saved the policy carries the "
        + "decoded head rule, a saved true reaches FlightRosterPolicy.NearestAfterKill, a saved "
        + "false reaches it as off, a --det run reads no saved option, and a hand-written value of "
        + "another JSON kind loads as never set so the launch keeps the decoded rule")]
    internal static void OptionsTargetingLaunch(TestContext ctx)
    {
        string dir = Path.Combine(ctx.ScratchDir, "options-targeting-launch");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        string? previous = OptionsStore.DirectoryOverride;
        OptionsStore.DirectoryOverride = dir;
        try
        {
            ctx.Check(!LaunchedTargeting(System.Array.Empty<string>()),
                $"with no options file the launch flies the decoded head rule ({LaunchedTargeting(System.Array.Empty<string>())})");

            OptionsStore.UserOptions().Save(new OptionsDef { NearestAfterKill = true });
            ctx.Check(LaunchedTargeting(System.Array.Empty<string>()),
                $"a saved true reaches the roster policy on a plain launch ({LaunchedTargeting(System.Array.Empty<string>())})");
            ctx.Check(!LaunchedTargeting(new[] { "--det" }),
                $"a --det run reads no saved option, so a golden shot cannot depend on one machine's file ({LaunchedTargeting(new[] { "--det" })})");

            OptionsStore.UserOptions().Save(new OptionsDef { NearestAfterKill = false });
            ctx.Check(!LaunchedTargeting(System.Array.Empty<string>()),
                $"a saved false reaches the policy as off ({LaunchedTargeting(System.Array.Empty<string>())})");

            // A switch has no vocabulary, so what stands in for an unknown word is a value of
            // another JSON kind: written by hand, it loads as never set.
            File.WriteAllText(Path.Combine(dir, "options.json"),
                "{\"version\": 1, \"nearestAfterKill\": \"yes\"}", new System.Text.UTF8Encoding(false));
            ctx.Check(OptionsStore.UserOptions().Load().NearestAfterKill == null
                && !LaunchedTargeting(System.Array.Empty<string>()),
                $"a quoted value loads as never set and the launch keeps the decoded rule ({OptionsStore.UserOptions().Load().NearestAfterKill})");
        }
        finally
        {
            OptionsStore.DirectoryOverride = previous;
        }
    }

    [Suite("options-view-launch",
        "The saved Default View and Auto Head Turn at launch: a saved cockpit reaches the opening "
        + "view a plain launch builds, --view= on the command line outranks the saved word "
        + "(--view=chase included, which is also the default), a --det run reads neither, a word "
        + "outside the store's set loads as never set, the saved view is not folded outside flight, "
        + "and the head turn reaches the policy as saved while a never-set field leaves it null so "
        + "the headLook.autohead config key decides")]
    internal static void OptionsViewLaunch(TestContext ctx)
    {
        string dir = Path.Combine(ctx.ScratchDir, "options-view-launch");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        string? previous = OptionsStore.DirectoryOverride;
        OptionsStore.DirectoryOverride = dir;
        try
        {
            ctx.Check(LaunchedView(new[] { "--fly" }) == PilotViewMode.Chase,
                $"with no options file a launch opens in the chase view ({LaunchedView(new[] { "--fly" })})");

            OptionsStore.UserOptions().Save(new OptionsDef
            {
                DefaultView = PilotView.Name(PilotViewMode.Cockpit),
                AutoHeadTurn = true,
            });
            ctx.Check(LaunchedView(new[] { "--fly" }) == PilotViewMode.Cockpit,
                $"a saved cockpit reaches the opening view on a plain launch ({LaunchedView(new[] { "--fly" })})");
            ctx.Check(LaunchedView(new[] { "--fly", "--view=nose" }) == PilotViewMode.Nose,
                $"--view=nose outranks the saved cockpit ({LaunchedView(new[] { "--fly", "--view=nose" })})");
            // Chase is both the default and a nameable mode, so this is the case a value comparison
            // alone cannot answer: the spec's own explicit flag is what settles it.
            ctx.Check(LaunchedView(new[] { "--fly", "--view=chase" }) == PilotViewMode.Chase,
                $"and --view=chase does too, though chase is also the default ({LaunchedView(new[] { "--fly", "--view=chase" })})");
            ctx.Check(LaunchedView(new[] { "--fly", "--det" }) == PilotViewMode.Chase,
                $"a --det run reads no saved view, so a golden cannot depend on one machine's file ({LaunchedView(new[] { "--fly", "--det" })})");
            ctx.Check(LaunchedView(System.Array.Empty<string>()) == PilotViewMode.Chase,
                $"and a run that is not a flight takes no saved view either ({LaunchedView(System.Array.Empty<string>())})");
            ctx.Check(LaunchedAutoHead(new[] { "--fly" }) == true && LaunchedAutoHead(new[] { "--fly", "--det" }) == null,
                $"the saved head turn reaches the policy and a --det run reads none ({LaunchedAutoHead(new[] { "--fly" })}, {LaunchedAutoHead(new[] { "--fly", "--det" })})");

            OptionsStore.UserOptions().Save(new OptionsDef { AutoHeadTurn = false });
            ctx.Check(LaunchedAutoHead(new[] { "--fly" }) == false,
                $"a saved false reaches it as off rather than as never set ({LaunchedAutoHead(new[] { "--fly" })})");
            ctx.Check(LaunchedAutoHead(new[] { "--fly" }) is not null && LaunchedView(new[] { "--fly" }) == PilotViewMode.Chase,
                $"and a file naming no view leaves the opening view at the default ({LaunchedView(new[] { "--fly" })})");

            // The momentary arguments --view= also takes (a numpad digit, "back") name a look rather
            // than a selection, so the file may not carry them: written by hand, they load as unset.
            File.WriteAllText(Path.Combine(dir, "options.json"),
                "{\"version\": 1, \"defaultView\": \"back\"}", new System.Text.UTF8Encoding(false));
            ctx.Check(OptionsStore.UserOptions().Load().DefaultView == null
                && LaunchedView(new[] { "--fly" }) == PilotViewMode.Chase,
                $"a word outside the store's set loads as never set and the launch keeps chase ({LaunchedView(new[] { "--fly" })})");
        }
        finally
        {
            OptionsStore.DirectoryOverride = previous;
        }
    }

    // The launch's own read: the spec a command line parses to, the saved word folded in the way
    // Launcher.LaunchSession folds it, then the policy the roster is built from.
    private static int Launched(string[] args)
    {
        var spec = SessionSpec.Parse(args).WithSavedDifficulty(OptionsStore.UserOptions().Load().Difficulty);
        return FlightRosterPolicy.From(spec).Difficulty;
    }

    // The same read for the targeting setting, the second half of the same fold.
    private static bool LaunchedTargeting(string[] args)
    {
        var spec = SessionSpec.Parse(args).WithSavedNearestAfterKill(OptionsStore.UserOptions().Load().NearestAfterKill);
        return FlightRosterPolicy.From(spec).NearestAfterKill;
    }

    // The opening view the same fold reaches, which is what HumanFlightAdapter seeds the camera's
    // selected mode from.
    private static PilotViewMode LaunchedView(string[] args)
    {
        var spec = SessionSpec.Parse(args).WithSavedDefaultView(OptionsStore.UserOptions().Load().DefaultView);
        return FlightRosterPolicy.From(spec).ViewMode;
    }

    // The head turn the same fold reaches, null where never set, which is what leaves the
    // headLook.autohead config key deciding at FlightController's own gate.
    private static bool? LaunchedAutoHead(string[] args)
    {
        var spec = SessionSpec.Parse(args).WithSavedAutoHeadTurn(OptionsStore.UserOptions().Load().AutoHeadTurn);
        return FlightRosterPolicy.From(spec).AutoHeadTurn;
    }
}
