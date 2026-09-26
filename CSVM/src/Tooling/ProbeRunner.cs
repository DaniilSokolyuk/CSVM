using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Tooling;

/// <summary>The `--dump-*`/`--run-tests`/`--*-test`/`--destroy=` probe wrappers the Launcher and
/// GameSession quit into: each reads a <see cref="SessionSpec"/> (passed per call, since a
/// menu launch can replace the caller's spec between calls) plus the base paths settled once in
/// `_Ready`, produces a report to stdout and `./.scratch/`, and hands back a verdict for the
/// caller's exit code. No reference back to the host node, the two probes that need one
/// (<see cref="RunTestSuites"/>, for its throwaway world host; <see cref="RunEffectsTest"/>, for
/// the effect-play point) take exactly the camera/node they need as parameters.</summary>
public sealed class ProbeRunner
{
    private readonly string _repoRoot;
    private readonly string _dataRoot;
    private readonly string _zrdrPath;
    private readonly string _soundsPath;
    private readonly string _interpPath;
    private readonly string _messagesPath;
    private readonly string _planesGamezPath;

    public ProbeRunner(string repoRoot, string dataRoot, string zrdrPath, string soundsPath,
        string interpPath, string messagesPath, string planesGamezPath)
    {
        _repoRoot = repoRoot;
        _dataRoot = dataRoot;
        _zrdrPath = zrdrPath;
        _soundsPath = soundsPath;
        _interpPath = interpPath;
        _messagesPath = messagesPath;
        _planesGamezPath = planesGamezPath;
    }

    /// <summary>Applies the <c>--rocket=&lt;wep_id&gt;</c> testing override: replaces every hardpoint's
    /// ordnance with the named weapon, resetting each pylon's capacity/ammo to that weapon's
    /// <c>CLUSTER_SIZE</c>. A no-op (with a warning) if the id is unknown. Must run before the pylon
    /// models are mounted and the controller's ordnance-type list is built. All 11 stock loadouts
    /// carry HE (wep_06), so this is the only way to exercise a different pylon model.</summary>
    public static void ApplyRocketOverride(Loadout loadout, WeaponDefs weapons, string wepId, bool verbose)
    {
        if (weapons.Get(wepId) is not { } weapon)
        {
            GD.PushWarning($"--rocket='{wepId}' is not a known weapon id, hardpoints keep their stock ordnance");
            return;
        }
        int per = weapon.ClusterSize ?? 0;
        foreach (var hp in loadout.Hardpoints)
        {
            hp.Weapon = weapon;
            hp.Capacity = per;
            hp.Ammo = per;
        }
        if (verbose)
        {
            Log.Info("core", $"--rocket: hardpoints -> {weapon.Id} ({weapon.Name}), flyout model '{weapon.Flyout?.Model ?? "-"}', {per}/pylon");
        }
    }

    /// <summary>--destroy=&lt;name&gt; (F42, docs/cli.md): kill every destructible whose def name, anim
    /// name or anchor <c>cs_name</c> matches (case-insensitive), through the real weapon-hit path
    /// (<see cref="Mech3.AnimRuntime.DamageAt"/>). Resolves each match to its authoritative instance
    /// and dedupes by anchor, so one physical object bound through several pools dies once. Returns
    /// how many distinct objects were destroyed.</summary>
    public static int TriggerDestroy(Mech3.AnimRuntime runtime, string name, out Aabb bounds)
    {
        bounds = default;
        static string AnchorName(Node3D n) =>
            n.HasMeta(Mech3.AnimRuntime.NameMeta) ? n.GetMeta(Mech3.AnimRuntime.NameMeta).AsString()
                                                  : n.Name.ToString();

        // Distinct physical objects to kill, keyed by authoritative anchor so a def bound through
        // both its reader wildcard and its compiled twin counts once.
        var targets = new Dictionary<ulong, Mech3.DestructibleRegistry.Instance>();
        foreach (var inst in runtime.Destructibles.All)
        {
            bool match =
                inst.Def.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                || (inst.Def.AnimName?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false)
                || AnchorName(inst.Anchor).Contains(name, StringComparison.OrdinalIgnoreCase);
            if (!match)
            {
                continue;
            }
            var target = runtime.Destructibles.Resolve(inst.Anchor) ?? inst;
            targets[target.Anchor.GetInstanceId()] = target;
        }

        if (targets.Count == 0)
        {
            // No match: list a sample of what IS destructible here so the tester can correct the name
            // without a separate --damage-test run (that report is still the full list).
            var sample = new List<string>();
            var seen = new HashSet<string>();
            foreach (var inst in runtime.Destructibles.All)
            {
                if (seen.Add(inst.Def.Name))
                {
                    sample.Add(inst.Def.Name);
                }
                if (sample.Count >= 20)
                {
                    break;
                }
            }
            Log.Info("core", $"--destroy='{name}': no destructible matched. {runtime.Destructibles.DistinctAnchors} object(s) present; some def names: {string.Join(", ", sample)} (--damage-test lists them all)");
            return 0;
        }

        // A cap so naming a common wildcard (many towers/panels) can't start hundreds of death
        // sequences in one frame; the framed object is what the screenshot needs. Loud when it bites.
        const int cap = 64;
        int killed = 0;
        var refused = new List<string>();
        bool haveBounds = false;
        foreach (var target in targets.Values)
        {
            if (killed >= cap)
            {
                Log.Info("core", $"--destroy='{name}': capped at {cap} of {targets.Count} matches (name a more specific def/node to kill fewer)");
                break;
            }
            if (target.Status == Mech3.DestructibleRegistry.State.Destroyed)
            {
                continue;
            }
            // The first killed object's world-space bounds, captured before the kill: the caller
            // auto-frames the freecam on it (the anchor's own origin is often far from its geometry).
            // MeshInstance-based, so it merges the healthy + destroyed variants either way.
            if (!haveBounds)
            {
                bounds = UI.Overlays.OrbitCamera.MergedAabb(target.Anchor);
                haveBounds = true;
            }
            // Spend more than the whole health pool so a single call kills it outright (DamageAt runs
            // the death sequence at zero). Feeding the anchor node is exactly how --damage-hd drives it.
            runtime.DamageAt(target.Anchor, target.MaxHealth + 1f);
            // ⚠ Count what died, never what was asked to die. A pool whose owner is still deactivated
            // is Dormant and refuses the hit, and a probe that reports those as kills hands the reader
            // an empty log to explain (a deactivated zeppelin's parts are the whole authored case).
            if (target.Status == Mech3.DestructibleRegistry.State.Destroyed)
            {
                killed++;
            }
            else
            {
                refused.Add(target.Def.Name);
            }
        }
        var c = bounds.GetCenter();
        string at = haveBounds ? Log.Format($" near ({c.X:0}, {c.Y:0}, {c.Z:0})") : "";
        string held = refused.Count == 0 ? ""
            : $"; {refused.Count} refused the hit, out of the world until something wakes them ({string.Join(", ", refused.Distinct())})";
        Log.Info("core", $"--destroy='{name}': destroyed {killed} object(s){at} ({string.Join(", ", targets.Values.Where(t => t.Status == Mech3.DestructibleRegistry.State.Destroyed).Select(t => t.Def.Name).Distinct())}){held}");
        return killed;
    }

    /// <summary>Wakes campaign objective <paramref name="number"/> and drives every node its
    /// <c>INACTIVEn</c> conditions name inactive, the scripted twin of flying whatever completes
    /// it: the graph then completes it off its own conditions on the next step, rather than a
    /// mark being faked into the display. Drives <c>--debug-objective=</c> and the objectives
    /// suite alike. Returns whether the mission arms an objective of that number.</summary>
    public static bool ForceObjective(Mech3.AnimRuntime? runtime, Session.Campaign.CampaignDirector director, int number)
    {
        if (runtime == null || director.Graph is not { } graph)
        {
            return false;
        }

        foreach (var def in director.Script.Objectives)
        {
            if (def.Number != number)
            {
                continue;
            }

            DriveInactive(runtime, def);
            graph.Wake(number);
            return true;
        }

        return false;
    }

    /// <summary>Destroys (or, where nothing is destructible, deactivates) every node one
    /// objective's <c>INACTIVE</c> paths name, the same resolve-then-kill shape
    /// <c>CampaignMissionEnd</c>'s driver uses.</summary>
    public static void DriveInactive(Mech3.AnimRuntime runtime, Session.Objectives.ObjectiveDef def)
    {
        foreach (var path in def.Inactive)
        {
            if (ResolvePath(runtime, path) is not { } node)
            {
                continue;
            }

            if (runtime.Destructibles.Resolve(node) is { MaxHealth: > 0f } live)
            {
                runtime.DamageAt(node, live.MaxHealth);
            }
            else
            {
                Mech3.AnimRuntime.SetSubtreeActive(node, false);
            }
        }
    }

    /// <summary>Walks a node path name by name, each step searched under the node the last one
    /// resolved to; null when any step finds nothing.</summary>
    public static Node3D? ResolvePath(Mech3.AnimRuntime runtime, IReadOnlyList<string> path)
    {
        Node3D? node = null;
        foreach (var name in path)
        {
            var found = runtime.FindNodes(name, node);
            if (found.Count == 0)
            {
                return null;
            }

            node = found[0];
        }

        return node;
    }

    /// <summary>--dump-markers[=plane] (docs/cli.md): the airframe marker-rig report, stdout and
    /// <c>./.scratch/markers_dump.txt</c>, that <c>docs/formats/markers.md</c> regenerates from.
    /// See <see cref="Mech3.MarkerRig"/>.</summary>
    /// <returns>Whether the report was produced; the caller turns this into the exit code.</returns>
    public bool DumpMarkers(SessionSpec spec)
    {
        // Windowed launches shouldn't steal focus for a report that renders nothing and quits.
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
        var r = Probes.Markers(_planesGamezPath, spec.DumpMarkersPlane);
        if (r.Error != null)
        {
            Log.Error("core", $"--dump-markers: {r.Error}");
            return false;
        }
        Log.Raw(r.Text);
        // ./.scratch/ inside the workspace, per PROJECT_CONTEXT.md, never the OS temp dir.
        WriteScratch("markers_dump.txt", r.Text);
        Log.Info("core", $"{r.Summary} → ./.scratch/markers_dump.txt");
        return true;
    }

    /// <summary>Writes one report into the workspace scratch folder, by absolute path. Relative
    /// paths resolve against the process working directory, not the repo, so a run launched from
    /// anywhere else would silently scatter its artifacts. The one shared write path, even
    /// <c>GameSession</c>'s <c>--weapon-test</c> report writes through this rather than
    /// duplicating it.</summary>
    public void WriteScratch(string fileName, string text)
    {
        var scratch = Path.Combine(_repoRoot, ".scratch");
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(scratch, fileName), text);
    }

    /// <summary>--run-tests[=filter]: boot the engine, run the registered assertion suites, print
    /// the PASS/FAIL/SKIP table plus <c>./.scratch/test-report.json</c>, and quit with a nonzero
    /// exit code if any suite failed (see <see cref="Testing.TestHarness"/>). <paramref name="parent"/>
    /// hosts the throwaway world node a suite may build; the caller adopts the fixed-step clock
    /// this creates via <paramref name="clock"/> and quits the tree with the returned code.</summary>
    public int RunTestSuites(SessionSpec spec, Node parent, Camera3D camera, out GameClock clock)
    {
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
        // A suite that ticks the sim must see the same clock a session gives it. Fixed-step, since
        // a test run is deterministic by nature: sim state is a function of the step count.
        clock = new GameClock { Mode = GameClock.RunMode.FixedStep };
        GameClock.Current = clock;
        var host = new Node3D { Name = "TestHost" };
        parent.AddChild(host);
        // ⚠ The one reference into CSVM.Testing from outside it: --run-tests has to reach the
        // harness from its dispatch. Anything else a suite shares with the game belongs here in Tooling.
        var ctx = new Testing.TestContext
        {
            RepoRoot = _repoRoot,
            DataRoot = _dataRoot,
            Chapter = spec.Chapter,
            Mission = spec.Mission,
            ZrdrPath = _zrdrPath,
            MessagesPath = _messagesPath,
            PlanesGamezPath = _planesGamezPath,
            InterpPath = _interpPath,
            SoundsPath = _soundsPath,
            PlaneName = spec.PlaneName,
            Mute = spec.Mute,
            LoadoutOverride = spec.LoadoutOverride,
            Host = host,
            Camera = camera,
        };
        if (DisplayServer.GetName() == "headless")
        {
            // The dummy renderer compiles no shaders, so a shader error cannot occur, and
            // therefore cannot be screened. Say so rather than letting the clean error census read
            // as proof.
            Log.Warn("test", $"headless display, no shaders compiled, so the error screen cannot see a shader error");
        }
        int code = Testing.TestHarness.Run(ctx, spec.RunTestsFilter);
        host.Free();
        GameClock.Current = null;
        return code;
    }

    /// <summary>--dump-weapons[=id|name] (docs/cli.md): the <see cref="Flight.Weapons.WeaponDefs"/> report,
    /// stdout and <c>./.scratch/weapons_dump.txt</c>, checked against weapons.md; a clean run (no
    /// UNHANDLED lines) is the pass.</summary>
    /// <returns>Whether the report was produced; the caller turns this into the exit code.</returns>
    public bool DumpWeapons(SessionSpec spec)
    {
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
        var r = Probes.Weapons(_zrdrPath, _messagesPath, spec.DumpWeaponsFilter);
        if (r.Error != null)
        {
            Log.Error("core", $"--dump-weapons: {r.Error}");
            return false;
        }
        Log.Raw(r.Text);
        WriteScratch("weapons_dump.txt", r.Text);
        Log.Info("core", $"{r.Summary} → ./.scratch/weapons_dump.txt");
        return true;
    }

    /// <summary>--dump-flight[=plane|=all] (docs/cli.md): the <see cref="Flight.Airframe.FlightModel"/> envelope
    /// report against its decoded targets, to stdout and <c>./.scratch/flight_dump.txt</c>. The only
    /// instrument that shows what a flight-constant change did to the whole envelope, and with
    /// <c>=all</c> it shows that across all eleven stock airframes in one run.</summary>
    /// <returns>Whether the report was produced; the caller turns this into the exit code.</returns>
    public bool DumpFlight(SessionSpec spec)
    {
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
        string plane = spec.DumpFlightPlane.Length > 0 ? spec.DumpFlightPlane : spec.PlaneName;
        var r = plane.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? Probes.FlightEnvelopeAll(_zrdrPath)
            : Probes.FlightEnvelope(_zrdrPath, plane);
        if (r.Error != null)
        {
            Log.Error("core", $"--dump-flight: {r.Error}");
            return false;
        }
        Log.Raw(r.Text);
        WriteScratch("flight_dump.txt", r.Text);
        Log.Info("core", $"{r.Summary} → ./.scratch/flight_dump.txt");
        return true;
    }

    /// <summary>--dump-loadout[=plane] (docs/cli.md): builds each plane and binds its stock loadout
    /// (<see cref="Flight.Weapons.Loadout"/>). It reports gun groups and hardpoints, or the loud
    /// error if a marker doesn't resolve. <c>--weapon-lab</c> binds
    /// <see cref="Flight.Weapons.Loadout.ForRig"/> instead.
    /// Writes to stdout and <c>./.scratch/loadout_dump.txt</c>.</summary>
    /// <returns>Whether the report was produced; the caller turns this into the exit code.</returns>
    public bool DumpLoadout(SessionSpec spec)
    {
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
        var r = Probes.Loadouts(_zrdrPath, _messagesPath, _planesGamezPath, _dataRoot,
            spec.DumpLoadoutFilter, spec.LoadoutOverride, forRig: spec.WeaponLab);
        if (r.Error != null)
        {
            Log.Error("core", $"--dump-loadout: {r.Error}");
            return false;
        }
        Log.Raw(r.Text);
        WriteScratch("loadout_dump.txt", r.Text);
        Log.Info("core", $"{r.Summary} → ./.scratch/loadout_dump.txt");
        return true;
    }

    /// <summary>--dump-mips[=name] (docs/cli.md): the chapter's mip-chain report against the
    /// authored artwork, to stdout and <c>./.scratch/mips_dump.txt</c>. Run once per <c>--mips=</c>
    /// policy: the generated chain averages the bright pixels away, the authored one keeps them.</summary>
    /// <returns>Whether the report was produced; the caller turns this into the exit code.</returns>
    public bool DumpMips(SessionSpec spec)
    {
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
        var r = Probes.MipChains(SessionPaths.ChapterTextures(_dataRoot, spec.Chapter),
            spec.Chapter, spec.DumpMipsFilter, _interpPath);
        if (r.Error != null)
        {
            Log.Error("core", $"--dump-mips: {r.Error}");
            return false;
        }
        Log.Raw(r.Text);
        WriteScratch("mips_dump.txt", r.Text);
        Log.Info("core", $"{r.Summary} → ./.scratch/mips_dump.txt");
        return r.Ok;
    }

    /// <summary>--dump-ai[=chapter] (docs/cli.md): a pure-data report over the five AI families,
    /// see <see cref="Probes.Ai"/>, to stdout and <c>./.scratch/ai_dump.txt</c>. No world, no
    /// scene: every family is read straight off the extraction.</summary>
    /// <returns>Whether the report was produced; the caller turns this into the exit code.</returns>
    public bool DumpAi(SessionSpec spec)
    {
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
        var r = Probes.Ai(_dataRoot, _zrdrPath, spec.DumpAiChapter);
        if (r.Error != null)
        {
            Log.Error("core", $"--dump-ai: {r.Error}");
            return false;
        }
        Log.Raw(r.Text);
        WriteScratch("ai_dump.txt", r.Text);
        Log.Info("core", $"{r.Summary} → ./.scratch/ai_dump.txt");
        return r.Ok;
    }

    /// <summary>--effects-test (docs/cli.md): the world-effects headless verify, see
    /// <see cref="Probes.Effects"/>. Plays every effect at the camera point so range-gated ones
    /// (gunhit's PLAYER_RANGE) pass. Reports to stdout and <c>./.scratch/effects_test.txt</c>.</summary>
    public void RunEffectsTest(SessionSpec spec, Camera3D camera, Mech3.AnimRuntime effects,
        IReadOnlyList<string> effectAnimNames, Node3D? stage = null)
    {
        var r = Probes.Effects(effects, effectAnimNames, camera.GlobalPosition, stage, spec.Chapter);
        Log.Raw(r.Text);
        WriteScratch("effects_test.txt", r.Text);
    }


    /// <summary>--damage-test[=name] / --damage-hd=N: sweep one live destructible instance per
    /// distinct def through its damage stages (or through discrete weapon hits) and report what
    /// each check found, see <see cref="Probes.Damage"/>, which the <c>damage-stages</c> /
    /// <c>damage-hd</c> suites assert on. Reports to stdout, <c>./.scratch/damage_test.txt</c> and
    /// <c>./.scratch/world_colliders.txt</c>.</summary>
    public void RunDamageTest(SessionSpec spec, Mech3.AnimRuntime runtime)
    {
        var r = Probes.Damage(runtime, spec.Chapter, spec.DamageTestFilter, spec.DamageHd);
        Log.Raw(r.Text);
        WriteScratch("damage_test.txt", r.Text);
        if (r.CollidableMeshes > 0)
        {
            WriteScratch("world_colliders.txt", r.CollidersText);
            Log.Info("core", $"damage-test: {r.CollidableMeshes} collidable meshes → ./.scratch/world_colliders.txt");
        }
        Log.Info("core", $"{r.Summary} → ./.scratch/damage_test.txt");
    }

}
