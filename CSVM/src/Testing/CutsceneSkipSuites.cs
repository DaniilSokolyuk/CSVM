using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The suite over the player's cutscene skip: which episodes offer one, and what a key
/// press during an episode that does not costs. CM02's capture is the case, played through the
/// animation runtime on a Realtime clock (INSTR-26) twice, once undisturbed and once with the skip
/// key pressed at two seconds, because the swap that capture carries is the one cutscene event
/// whose timing a player can see go wrong.</summary>
internal static class CutsceneSkipSuites
{
    // CM02's story position and the airframe the capture swaps away from, as the swap suite reads
    // them: this capture is the one shipped cutscene whose codes change the aircraft under the
    // player, so a skip's timing is observable in it and nowhere else.
    private const int Cm01Seq = 0;
    private const int Cm02Seq = 1;
    private const string StartPlane = "player_bhawk";
    private const string CapturedPlane = "player_balmoral";

    // The code that re-places the player after a drop-off. Named here rather than read off the
    // host, because what this suite asks of it is a property of the shipped data: a definition
    // that re-places the pilot must not also be one the player can cut short.
    private const int CodeReplacePlayer = 951;

    // The played frame. The budget outlasts the wing walk's own 19.25 s motion, which is what the
    // capture's called definition raises its reveal behind.
    private const float StepDt = 1f / 60f;
    private const float PlayBudgetS = 30f;
    private const float SkipAtS = 2f;

    /// <summary>Plays CM02's capture over that mission's built world twice, undisturbed and with
    /// the skip key pressed part way, and drives the arming code against the same host.</summary>
    // The skip key removed the picture from a cutscene the original arms no skip on, handing
    // the player back an aeroplane mid wing walk.
    [Suite("campaign-cutscene-skip",
        "which cutscene episodes offer the player a skip, over CM02's BUILT world: the "
        + "mission's capture definition and its whole call closure author no hold code, "
        + "which is the code that arms a skip, so playing that capture on a realtime clock "
        + "and pressing the skip key two seconds in is "
        + "refused -- the episode keeps the session and its picture, and the swap, the hide "
        + "and the hand-over land at the same second and in the same end state as the run "
        + "played undisturbed -- while an episode that has raised the hold code takes the "
        + "same key press and restores on it")]
    internal static void CutsceneSkip(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), Cm02Seq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {Cm02Seq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");
        ctx.RequireData(SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder), $"{chapter}/{folder} zrdr");

        var report = new StringBuilder();
        report.AppendLine($"seq {Cm02Seq} -> {chapter}/{folder}");
        CheckDropOffKeepsItsReplace(ctx, report);
        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(chapter, collision: false, folder,
                world => Drive(ctx, world, chapter, report));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-cutscene-skip-{chapter}-{folder}.txt", report.ToString());
        ctx.Note($"played and pressed the skip key through the cutscene {chapter}/{folder} authors");
    }

    // The first definition in the mission's compiled program that raises a swap code, with the code
    // and the root node it raises it on: read out of the shipped data rather than named here.
    internal static (string Anim, int Code, string Root)? SwapCallIn(AnimProgram program)
    {
        foreach (var def in program.Defs)
        {
            if (def.AnimName is not { } anim)
            {
                continue;
            }

            string root = def.RootName is { Length: > 0 } named ? named : def.Name;
            foreach (var sequence in def.Sequences)
            {
                foreach (var ev in sequence.Events)
                {
                    if (!string.Equals(ev.Kind, "Callback", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int code = (int)(ev.Data.Num("value") ?? -1f);
                    if (AirframeSwapCodes.For(code) != null)
                    {
                        return (anim, code, root);
                    }
                }
            }
        }

        return null;
    }

    // The mission at one story position, or null where the sequence table carries none.
    internal static CampaignMission? MissionOf(IReadOnlyList<CampaignMission> missions, int seq)
    {
        foreach (var mission in missions)
        {
            if (mission.Seq == seq)
            {
                return mission;
            }
        }

        return null;
    }

    // The drop-off's own stake in the gate, read off CM01's shipped definitions: the code that
    // re-places the pilot is raised by a definition that raises no hold code, so no key press can
    // cut that episode short and leave the pilot at the pose it flew in on.
    private static void CheckDropOffKeepsItsReplace(TestContext ctx, StringBuilder report)
    {
        if (MissionOf(CampaignSequence.Load(ctx.ZrdrPath), Cm01Seq) is not { } mission)
        {
            return;
        }

        string path = SessionPaths.MissionZrdr(ctx.DataRoot, mission.ChapterFolder.ToUpperInvariant(),
            mission.MissionFolder.ToUpperInvariant());
        ctx.RequireData(path, $"{mission.ChapterFolder}/{mission.MissionFolder} zrdr");
        var replacers = new List<string>();
        var armers = new List<string>();
        foreach (var def in AnimDefs.LoadArchive(path))
        {
            var codes = CodesIn(def);
            if (codes.Contains(CodeReplacePlayer))
            {
                replacers.Add(def.AnimName ?? def.Name);
            }

            if (codes.Contains(CutsceneController.CodeHoldsWorld))
            {
                armers.Add(def.AnimName ?? def.Name);
            }
        }

        report.AppendLine($"seq {Cm01Seq} -> {mission.ChapterFolder}/{mission.MissionFolder}: " +
            $"code {CodeReplacePlayer} in [{string.Join(",", replacers)}], " +
            $"code {CutsceneController.CodeHoldsWorld} in [{string.Join(",", armers)}]");
        ctx.Check(replacers.Count > 0,
            $"CM01 authors a definition that re-places the player after its drop-off, so there is something a skip could drop");
        ctx.Check(armers.Count == 0,
            $"and no definition in that mission raises the code that arms a skip, so the drop-off plays out and its re-place always lands");
    }

    // Every code one definition authors, its RESET_STATE block included: the intros keep four of
    // their eight there, so a sequence-only read would miss half of what a definition raises.
    private static List<int> CodesIn(AnimDefinition def)
    {
        var codes = new List<int>();
        foreach (var sequence in def.Sequences)
        {
            Collect(sequence.Events, codes);
        }

        if (def.ResetState != null)
        {
            Collect(def.ResetState.Events, codes);
        }

        return codes;
    }

    private static void Collect(IReadOnlyList<AnimEvent> events, List<int> into)
    {
        foreach (var ev in events)
        {
            if (string.Equals(ev.Kind, "Callback", StringComparison.Ordinal))
            {
                into.Add((int)(ev.Data.Num("value") ?? -1f));
            }
        }
    }

    private static void Drive(TestContext ctx, TestWorld world, string chapter, StringBuilder report)
    {
        var call = SwapCallIn(world.Session.Program)
            ?? throw new SuiteSkippedException("no definition in this mission authors a swap code");
        report.AppendLine($"'{call.Root}-{call.Anim}' authors callback {call.Code}");
        CheckAuthoredCodes(ctx, world, call.Anim, report);

        Outcome? played = null;
        Outcome? pressed = null;
        WithStagedCapture(ctx, world, chapter, call,
            staged => played = Play(ctx, world, staged, call, pressKeyAtS: null, report));
        WithStagedCapture(ctx, world, chapter, call,
            staged => pressed = Play(ctx, world, staged, call, SkipAtS, report));
        Compare(ctx, played!, pressed!, report);
        CheckArmedEpisode(ctx, world, call.Anim, report);
    }

    // The rule the gate is read off, in the mission's own compiled data: what arms a skip is the
    // hold code, and this capture's whole call closure authors none.
    private static void CheckAuthoredCodes(TestContext ctx, TestWorld world, string anim,
        StringBuilder report)
    {
        var codes = new List<int>();
        foreach (var def in world.Session.Program.Subset(new[] { anim }).Defs)
        {
            codes.AddRange(CodesIn(def));
        }

        report.AppendLine($"'{anim}' and its call closure author codes {string.Join(",", codes)}");
        ctx.Check(codes.Count > 0,
            $"'{anim}' and what it calls raise callbacks at all, so the codes below are read rather than assumed");
        ctx.Check(!codes.Contains(CutsceneController.CodeHoldsWorld),
            $"and none of them is code {CutsceneController.CodeHoldsWorld}, the code that arms a skip, so this capture offers the player no skip in the original either");
    }

    // One played leg. `pressKeyAtS` null plays the capture undisturbed; a value presses the skip
    // key at that point on the clock and goes on stepping, so both legs are read at one budget.
    private static Outcome Play(TestContext ctx, TestWorld world, Staged staged,
        (string Anim, int Code, string Root) call, float? pressKeyAtS, StringBuilder report)
    {
        var (roster, rig, before, cutscene, captured, wingman) = staged;
        var savedClock = GameClock.Current;
        var savedHost = world.Runtime.CallbackHost;
        var outcome = new Outcome();
        try
        {
            var clock = new GameClock { Mode = GameClock.RunMode.Realtime };
            GameClock.Current = clock;
            cutscene.BindWorld(world.Runtime);
            cutscene.HostDefinitions(ClosureOf(world, call.Anim));
            cutscene.BindRigs(new[] { rig }, () => roster.AiAircraft);
            cutscene.SwapAirframe = order => roster.RunSwap(rig, order, handsOver: true);
            world.Runtime.CallbackHost = cutscene.Host;
            world.Runtime.Play(call.Anim);

            bool pressed = false;
            for (int i = 0; i < (int)(PlayBudgetS / StepDt); i++)
            {
                float now = i * StepDt;
                if (!pressed && now >= SkipAtS)
                {
                    pressed = true;
                    outcome.PressedAtS = now;
                    outcome.Armed = cutscene.Skippable;
                    outcome.Skipped = pressKeyAtS != null && cutscene.Skip();
                    // Read before the next frame runs: what a refused key press must NOT have done
                    // is hand the player back an aeroplane the capture is still flying over.
                    outcome.PresentingAfterKey = cutscene.Presenting;
                    outcome.PlayingAfterKey = cutscene.Playing;
                }

                clock.BeginFrame(StepDt);
                world.Runtime.Advance(StepDt);
                cutscene.Tick();
                if (!outcome.Swapped && !ReferenceEquals(rig.Controller, before))
                {
                    outcome.Swapped = true;
                    outcome.SwappedAtS = now;
                }

                // ⚠ Read the hidden bit, never InPlay: a stand-in flown into the sea reads out of
                // the world for a reason this leg is not about (INSTR-27).
                if (outcome.Swapped && !captured.Inert)
                {
                    outcome.ShownAgain = true;
                }
            }

            outcome.Def = rig.Controller?.Loadout?.Def.Def ?? "-";
            outcome.EndHidden = captured.Inert;
            outcome.EndHandedOver = !wingman.Inert;
            outcome.Codes = new List<int>(cutscene.Codes);
        }
        finally
        {
            world.Runtime.CallbackHost = savedHost;
            GameClock.Current = savedClock;
        }

        report.AppendLine($"{(pressKeyAtS == null ? $"played undisturbed (at {outcome.PressedAtS:0.##} s: playing={outcome.PlayingAfterKey} presenting={outcome.PresentingAfterKey})" : $"skip key at {outcome.PressedAtS:0.##} s (took={outcome.Skipped}, armed={outcome.Armed}, still presenting={outcome.PresentingAfterKey})")}: " +
            $"swapped={outcome.Swapped} at {outcome.SwappedAtS:0.##} s, end def '{outcome.Def}' " +
            $"hidden={outcome.EndHidden} handed-over={outcome.EndHandedOver} " +
            $"shown-again={outcome.ShownAgain} codes {string.Join(",", outcome.Codes)}");
        return outcome;
    }

    private static void Compare(TestContext ctx, Outcome played, Outcome pressed, StringBuilder report)
    {
        ctx.Check(played.Swapped && played.PlayingAfterKey && played.PresentingAfterKey,
            $"played undisturbed, the capture has swapped the player's aircraft and is still running its wing walk at the {SkipAtS:0} s this suite presses the key at");
        ctx.Check(!pressed.Armed && !pressed.Skipped,
            $"the key press is refused, because nothing in this capture raised the code that arms a skip");
        ctx.Check(pressed.PresentingAfterKey && pressed.PlayingAfterKey,
            $"so the episode still has the session with its picture up, rather than the player being dropped back into an aeroplane mid wing walk");
        ctx.Check(SameCodes(played.Codes, pressed.Codes),
            $"and the episode raises the same codes in the same order it raises undisturbed, rather than being restored early and a later authored code starting a second episode behind it");
        ctx.Check(Mathf.Abs(played.SwappedAtS - pressed.SwappedAtS) < StepDt,
            $"the swap lands at the same second either way ({played.SwappedAtS:0.##} s and {pressed.SwappedAtS:0.##} s), so the key press changed nothing about when the player changes aircraft");
        ctx.Check(string.Equals(played.Def, pressed.Def, StringComparison.OrdinalIgnoreCase),
            $"the player ends on the same airframe, '{pressed.Def}'");
        ctx.Check(played.EndHidden == pressed.EndHidden,
            $"the capture's own aircraft in the same visible state (hidden={pressed.EndHidden})");
        ctx.Check(played.EndHandedOver == pressed.EndHandedOver,
            $"and the hand-over in the same state (handed over={pressed.EndHandedOver})");
        ctx.Check(!pressed.ShownAgain,
            $"with the captured aircraft still off screen at the end of the episode");
        report.AppendLine($"end state: played '{played.Def}' vs key-pressed '{pressed.Def}', " +
            $"swap at {played.SwappedAtS:0.##} s vs {pressed.SwappedAtS:0.##} s");
    }

    // The other side of the gate, on the same host: the hold code arms a skip, and an armed episode
    // takes one. Driven directly because no definition in this mission raises that code -- the two
    // that do outside the intros are C1/M04's own intro and C3/M03's zeppelin camera.
    private static bool SameCodes(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void CheckArmedEpisode(TestContext ctx, TestWorld world, string anim,
        StringBuilder report)
    {
        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        var savedClock = GameClock.Current;
        try
        {
            GameClock.Current = new GameClock { Mode = GameClock.RunMode.Realtime };
            cutscene.BindWorld(world.Runtime);
            cutscene.HostDefinitions(new[] { anim });
            ctx.Check(cutscene.Host(CutsceneController.CodeOutOfFlight, anim) && !cutscene.Skippable,
                $"an episode that has only put the player out of flight offers no skip");
            ctx.Check(cutscene.Host(CutsceneController.CodeHoldsWorld, anim) && cutscene.Skippable,
                $"the same episode offers one the moment it raises code {CutsceneController.CodeHoldsWorld}");
            ctx.Check(cutscene.Skip(),
                $"and the key press is taken");
            ctx.Check(!cutscene.Playing && !cutscene.Skippable && !cutscene.HoldsWorld,
                $"which ends the episode and disarms with it, the way the handoff does");
            report.AppendLine($"armed episode: skip taken, codes {string.Join(",", cutscene.Codes)}");
        }
        finally
        {
            GameClock.Current = savedClock;
            cutscene.Free();
        }
    }

    // One staged capture: the mission's own world, a flown human rig off a session-shaped roster,
    // the capture animation's aircraft and the hand-over block. Each leg re-stages, since a swap
    // replaces the rig it ran on and neither leg can read the other's world back.
    private static void WithStagedCapture(TestContext ctx, TestWorld world, string chapter,
        (string Anim, int Code, string Root) call, Action<Staged> leg)
    {
        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, chapter));
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var pane = new SubViewport();
        ctx.Host.AddChild(pane);
        var rig = new PlayerRig { Index = 0, Camera = ctx.Camera, HudParent = pane, Viewport = pane };
        var rigs = new[] { rig };
        FlightRoster? roster = null;
        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        try
        {
            roster = BuildRoster(ctx, world, chapter, textures, pool, rigs);
            roster.BuildPlayers(rigs);
            var before = rig.Controller ?? throw new InvalidOperationException("no rig was built");
            var captured = StageAi(roster, call.Root, CapturedPlane,
                before.WorldPosition + (before.NoseDirection * 300f), inert: false);
            var wingman = StageAi(roster, AirframeHandover.WingmanName, StartPlane,
                before.WorldPosition + new Vector3(0f, 0f, 5000f), inert: true);
            leg(new Staged(roster, rig, before, cutscene, captured, wingman));
        }
        finally
        {
            // Only what this suite put on the host: the host is shared with every suite in the
            // run, and a leaked 'wingman_4' renames the next one.
            var live = rig.Controller;
            var members = new List<FlightController>(roster?.AiAircraft ?? Array.Empty<FlightController>());
            roster?.ClearMembership();
            live?.Free();
            foreach (var ai in members)
            {
                ai.Free();
            }

            cutscene.Free();
            pane.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    // Every definition the capture can reach, its own plus the CALL_ANIMATION closure: what the
    // session's own host answers for, and the only way the called wing walk's codes are hosted.
    private static IReadOnlyList<string> ClosureOf(TestWorld world, string anim)
    {
        var names = new List<string>();
        foreach (var def in world.Session.Program.Subset(new[] { anim }).Defs)
        {
            if (def.AnimName is { } name && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    // One roster aircraft standing in for a block this suite's world builds no roster for. Their
    // names are what the swap resolves them by, and that is all it reads them for.
    private static FlightController StageAi(FlightRoster roster, string name, string planeNode,
        Vector3 at, bool inert)
    {
        var aim = at - Vector3.Forward;
        return roster.SpawnAi(new AiSpawn(planeNode, at, aim, AiPilot.HoldingCourse(at, aim),
            Team: AimAssist.PlayerTeam, Inert: inert, NodeName: name));
    }

    private static FlightRoster BuildRoster(TestContext ctx, TestWorld world, string chapter,
        TextureArchive textures, ProjectilePool pool, IReadOnlyList<PlayerRig> rigs)
    {
        var spec = SessionSpec.Parse(new[] { $"--plane={StartPlane}" });
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var resources = new AircraftAssemblyResources
        {
            PlanesGamez = planesGamez,
            StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
            AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
            CamParamsFor = _ => new CamParams(),
            PaintRng = new RandomNumberGenerator(),
            ZrdrPath = ctx.ZrdrPath,
            StockLoadouts = StockLoadouts.Load(),
            WeaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null),
            WeaponMessages = Messages.Load(ctx.MessagesPath),
            Textures = textures,
            Shakes = ShakeDefs.Load(ctx.ZrdrPath),
        };
        return new FlightRoster(FlightRosterPolicy.From(spec),
            new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof")),
            new WorldEffectsFactory(spec, ctx.Host, () => Vector3.Zero), ctx.Host, resources,
            new FlightWorldBindings
            {
                Projectiles = pool,
                Gamez = planesGamez,
                ChapterZrdrPath = SessionPaths.ChapterZrdr(ctx.DataRoot, chapter),
            },
            new HumanRosterBindings
            {
                RigCount = rigs.Count,
                Rigs = rigs,
                PauseState = new PauseState(),
                MenuInputFor = _ => new MenuInput(),
                ExitSession = () => { },
            }, new SkipFlightStarts());
    }

    // What one leg leaves behind: what the key press did, and the state both legs are compared on
    // once the budget is spent.
    private sealed class Outcome
    {
        public bool Swapped;
        public float SwappedAtS = -1f;
        public float PressedAtS = -1f;
        public bool Skipped;
        public bool Armed;
        public bool PresentingAfterKey;
        public bool PlayingAfterKey;
        public bool ShownAgain;
        public bool EndHidden;
        public bool EndHandedOver;
        public string Def = "-";
        public List<int> Codes = new();
    }

    // The actors one staged capture hands its leg: the session-shaped roster and rig, the aircraft
    // flying before the swap, the host, the capture animation's own aircraft, and the block the
    // outgoing aeroplane goes to.
    private sealed record Staged(
        FlightRoster Roster,
        PlayerRig Rig,
        FlightController Before,
        CutsceneController Cutscene,
        FlightController Captured,
        FlightController Wingman);

    // One start, high enough over the mission's own terrain that the aircraft is flying rather than
    // resolving a ground contact on the frame the swap rebuilds it.
    private sealed class SkipFlightStarts : IFlightStarts
    {
        public IReadOnlyList<FlightStart> ChooseStarts(IReadOnlyList<SpawnPoint>? spawns,
            string missionZrdrPath, int spawnBase, int playerCount)
        {
            var starts = new FlightStart[playerCount];
            for (int i = 0; i < playerCount; i++)
            {
                var position = new Vector3(i * 60f, 1200f, 0f);
                starts[i] = new FlightStart(position, position + Vector3.Forward, 0.5f, 90f);
            }

            return starts;
        }
    }
}
