using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.UI.Campaign;
using CSVM.UI.Hangar;
using CSVM.UI.Menu;
using CSVM.UI.Screens;
using Godot;

namespace CSVM.Testing;

/// <summary>The whole campaign loop in one suite: a profile created on a clean store, the cabin,
/// the briefing, the flight check, an ammunition change that reaches the flown aircraft, the
/// campaign's first mission flown through its intro cutscene to a recorded end, and the cabin
/// again with Next Mission advanced. The store lives under <c>user://</c> and is deliberately not
/// cleared at the end, so a second process reads what the first one left.</summary>
internal static class CampaignLoopSuites
{
    /// <summary>The profile the loop creates. It lives in the suite's own directory under
    /// <c>user://</c>, never in <c>user://Profiles</c>, so no player's campaign is ever read,
    /// rewritten or deleted by a test run.</summary>
    internal const string ProfileName = "Loop";

    // The campaign's first story position. Everything about the flown mission (chapter, mission
    // folder, objectives) is read out of cm_sequence and that mission's own data, never named here.
    private const int FirstSeq = 0;

    // The ammunition the loop picks on the ammo screen: one step up from the stock slug, which is
    // CampaignLoadout.AmmoNames index 1. A fresh profile always starts at 0, so the pick is a
    // change on every run and the value in the file after a save is unambiguous.
    private const int ChosenAmmo = 1;

    private const float StepDt = 1f / 60f;

    // How far the briefing's reveal is run, and the only rows it ever draws: the three buttons.
    // An uncovered objective is written onto the parchment note, never added to the cursor's list.
    private const float BriefingBudgetS = 180f;
    private const int BriefingButtons = 3;

    // The flown approach: where the run puts the aircraft relative to the objective's own node,
    // and how long it is given to fly in. The lift is what keeps a straight approach clear of the
    // node's own geometry while staying inside the objective's radius.
    private const float ApproachRangeM = 500f;
    private const float ApproachLiftM = 120f;
    private const float ApproachAimUpM = 40f;
    private const float ApproachBudgetS = 40f;
    private const float ApproachThrottle = 0.7f;
    private const float ApproachSpeedMps = 90f;

    /// <summary>Walks the campaign loop end to end and leaves its profile on disk. What a second
    /// process finds there is checked at the top of the next run, which is the only place the
    /// persistence claim can be made: a first green run proves nothing about it.</summary>
    // ⚠ Do not point another suite at this store: user://Testing/campaign-loop/ is this suite's
    // alone, and what a second process finds there is the persistence claim itself. Registry order
    // never protected it and cannot: the order is alphabetical and no suite's position is authored.
    [Suite("campaign-loop",
        "the whole campaign loop on the campaign's first mission (E41): a profile created on a "
        + "store holding none, the cabin, the briefing, the flight check, an ammunition change "
        + "that reaches the flown aircraft's guns, the mission's intro cutscene holding the "
        + "objectives clock, its primary objective completed by flying the approach it names, a "
        + "track its own data cues, the authored end recorded into the profile, and the cabin "
        + "again with Next Mission advanced; the profile is left on disk, so a second run "
        + "reads what the first one wrote")]
    internal static void CampaignLoop(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), FirstSeq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {FirstSeq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string missionFolder = mission.MissionFolder.ToUpperInvariant();
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, missionFolder);
        ctx.RequireData(missionZrdr, $"{chapter}/{missionFolder} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");

        var report = new StringBuilder();
        string dir = ProfileDir();
        report.AppendLine($"profile store: {dir}");
        report.AppendLine($"seq {FirstSeq} '{mission.Desc}' -> {chapter}/{missionFolder}");
        var store = new CampaignProfileStore(dir);
        bool carried = CheckCarriedIn(ctx, store, report);

        store.Delete(ProfileName);
        ctx.Check(store.Load(ProfileName) == null,
            $"the loop starts on a store holding no profile of its own");

        var strings = UiStrings.TryLoad(ctx.DataRoot) ?? UiStrings.Empty;
        var stock = StockLoadouts.Load();
        var flow = new CampaignFlow(store, strings, ctx.DataRoot, planes: null, stock);
        var profile = WalkToTheFlightCheck(ctx, flow, store, report);
        int group = ChangeAmmo(ctx, flow, store, report);
        var fit = PressFlyMission(ctx, flow, store, profile, stock, group, report);

        FlyTheMission(ctx, mission, chapter, missionFolder, missionZrdr, profile, store, fit, stock, report);
        ReturnToTheCabin(ctx, store, strings, stock, report);
        CheckTheClosingFilm(ctx, strings, report);

        ctx.WriteArtifact("test-campaign-loop.txt", report.ToString());
        ctx.Note($"walked the whole loop on {chapter}/{missionFolder}; state carried in from an earlier process: {carried}");
    }

    // The suite's own profile directory. user:// rather than .scratch/ on purpose: this is the
    // store the shipped campaign persists through, and it is what survives a process boundary.
    private static string ProfileDir() =>
        Path.Combine(ProjectSettings.GlobalizePath("user://"), "Testing", "campaign-loop", "Profiles");

    // What an earlier process left, which is the persistence claim itself. Nothing here can hold on
    // a first run over an empty directory, so the absence is noted rather than asserted.
    private static bool CheckCarriedIn(TestContext ctx, CampaignProfileStore store, StringBuilder report)
    {
        if (store.Load(ProfileName) is not { } earlier)
        {
            report.AppendLine("no earlier profile in the store: nothing to carry in");
            ctx.Note($"no state carried in: this run is the first over its user:// store, so the persistence checks below did not run");
            return false;
        }

        report.AppendLine($"carried in: missionsCompleted={earlier.MissionsCompleted}, " +
            $"results={earlier.MissionResults.Count}, planes={earlier.Planes.Count}");
        ctx.Same(FirstSeq + 1, earlier.MissionsCompleted,
            $"an earlier process's flown mission is still recorded in the profile file");
        ctx.Check(CampaignProgression.ResultOf(earlier, FirstSeq) is { } result
            && (result.Best.CompletedMask & CampaignProgression.PrimaryObjectiveMask) != 0,
            $"and its mission result carries the primary objective it completed");
        ctx.Check(AnyPlaneCarries(earlier, ChosenAmmo),
            $"and the ammunition the earlier run picked survived with it");
        ctx.Note($"state carried in from an earlier process: the profile file is the only path it could have taken");
        return true;
    }

    private static bool AnyPlaneCarries(CampaignProfileDef profile, int ammo)
    {
        foreach (var plane in profile.Planes)
        {
            foreach (int value in plane.Ammo)
            {
                if (value == ammo)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The out-of-mission screens, driven by the presses a player makes: the name field, CONTINUE,
    // NEXT MISSION, the briefing's own reveal, and GO TO FLIGHT CHECK.
    private static CampaignProfileDef WalkToTheFlightCheck(
        TestContext ctx, CampaignFlow flow, CampaignProfileStore store, StringBuilder report)
    {
        ctx.Check(flow.Screen == CampaignScreen.Roster, $"the campaign opens on the player roster");
        flow.FocusRow(0);
        flow.Accept();
        ctx.Check(flow.CapturesText, $"the first press arms the name field");
        flow.Type(ProfileName);
        flow.Accept();
        ctx.Check(flow.Screen == CampaignScreen.Cabin,
            $"CONTINUE on the typed name opens the cabin, screen={flow.Screen}");
        ctx.Check(store.Load(ProfileName) != null, $"and the new profile is on disk before anything is flown");
        var profile = flow.Profile
            ?? throw new InvalidOperationException("the cabin opened with no profile seated");
        ctx.Same(0, profile.MissionsCompleted, $"a new profile has flown nothing");
        ctx.Same(2, profile.Planes.Count, $"and starts on its two prebuilt aircraft");

        flow.FocusRow(0);
        flow.Accept();
        ctx.Check(flow.Screen == CampaignScreen.Briefing, $"NEXT MISSION opens the briefing, screen={flow.Screen}");
        ctx.Same(FirstSeq, flow.MissionSeq, $"on the campaign's first story position");

        if (flow.Page is CampaignBriefingPage briefing)
        {
            // The reveal is the narration's own clock, so the first uncovered line lands wherever
            // this mission's cue points put it rather than at a time this suite could name.
            float uncovered = 0f;
            for (float t = 0f; t < BriefingBudgetS && NoteEntries(briefing) == 0; t += StepDt)
            {
                briefing.Advance(StepDt);
                uncovered = t;
            }

            int written = NoteEntries(briefing);
            report.AppendLine($"briefing: state={(briefing.State != null ? "loaded" : "none")}, " +
                $"objectives={briefing.Objectives.Count}, first line at {uncovered:0.0}s, " +
                $"note lines={written}, rows={briefing.RowCount}, narration='{briefing.NarrationWav}'");
            ctx.Check(briefing.State != null, $"the briefing found this mission's own briefing state");
            ctx.Check(written > 0,
                $"and its reveal wrote an objective onto the parchment note by {uncovered:0.0}s, lines={written}");
            ctx.Same(BriefingButtons, briefing.RowCount,
                $"while the screen's rows stayed its three buttons, so a note line is read and not selected");
        }

        flow.FocusRow(CampaignBriefingPage.FlightCheckRow);
        flow.Accept();
        ctx.Check(flow.Screen == CampaignScreen.FlightCheck,
            $"GO TO FLIGHT CHECK opens the flight check, screen={flow.Screen}");
        return profile;
    }

    // The ammo screen, reached from the flight check's own CHANGE AMMO row: one gun group stepped
    // and the loadout accepted. Returns the group it changed.
    private static int ChangeAmmo(
        TestContext ctx, CampaignFlow flow, CampaignProfileStore store, StringBuilder report)
    {
        int row = RowOf(flow, "CHANGE AMMO");
        ctx.Check(row >= 0, $"the flight check offers CHANGE AMMO");
        flow.FocusRow(row);
        flow.Accept();
        ctx.Check(flow.Screen == CampaignScreen.Ammo, $"which opens the ammo screen, screen={flow.Screen}");

        int group = -1;
        for (int i = 0; i < 4 && group < 0; i++)
        {
            flow.FocusRow(i);
            if (flow.Step(1))
            {
                group = i;
            }
        }

        ctx.Check(group >= 0, $"a gun group on the pilot's aircraft takes an ammunition step");
        int accept = RowOf(flow, "ACCEPT LOADOUT");
        flow.FocusRow(accept);
        flow.Accept();
        ctx.Check(flow.Screen == CampaignScreen.FlightCheck,
            $"ACCEPT LOADOUT commits and returns to the flight check, screen={flow.Screen}");

        var saved = store.Load(ProfileName);
        int stored = saved != null && group >= 0
            ? saved.Planes[Math.Clamp(saved.SelectedPlane, 0, saved.Planes.Count - 1)].Ammo[group]
            : -1;
        report.AppendLine($"ammo: group {group} stepped to {stored} in the profile file");
        ctx.Same(ChosenAmmo, stored, $"the pick is in the profile file, not only in the page");
        return group;
    }

    // FLY MISSION: the flow hands the shell its job, and the shell's own save and fit resolution
    // (LaunchMenu.FlyCampaignMission) are what the mission then flies with.
    private static LoadoutChoice PressFlyMission(
        TestContext ctx, CampaignFlow flow, CampaignProfileStore store, CampaignProfileDef profile,
        StockLoadouts stock, int group, StringBuilder report)
    {
        int row = RowOf(flow, "FLY MISSION");
        ctx.Check(row >= 0, $"the flight check offers FLY MISSION");
        flow.FocusRow(row);
        flow.Accept();
        ctx.Check(flow.Exit == CampaignExit.FlyMission, $"the press asks the shell to fly, exit={flow.Exit}");
        store.Save(profile);

        var plane = profile.Planes[Math.Clamp(profile.SelectedPlane, 0, profile.Planes.Count - 1)];
        var fit = CampaignLoadout.For(plane, stock);
        report.AppendLine($"flying '{plane.Name}' airframe {plane.Airframe}, gun {group + 1} ammo '{fit.GunAmmoFor(group + 1)}'");
        ctx.Check(group < 0 || fit.GunAmmoFor(group + 1) == CampaignLoadout.AmmoNames[ChosenAmmo],
            $"the launch's fit names the picked ammunition, got '{fit.GunAmmoFor(group + 1)}'");
        return fit;
    }

    // The mission itself: the built world, the intro cutscene, the objectives director, the flown
    // approach that completes the primary, a mission-data music cue, and the authored end.
    private static void FlyTheMission(
        TestContext ctx, CampaignMission mission, string chapter, string missionFolder, string missionZrdr,
        CampaignProfileDef profile, CampaignProfileStore store, LoadoutChoice fit, StockLoadouts stock,
        StringBuilder report)
    {
        var script = ObjectiveScript.Load(missionZrdr);
        var director = CampaignDirector.Create(script, mission, profile, store);
        var music = new MusicPlayer(SoundDefs.Load(ctx.ZrdrPath), SoundDefs.LoadGroups(ctx.ZrdrPath));
        using var archive = new SoundArchive(ctx.SoundsPath);
        music.Loader = (def, looped) => archive.Find(def.WavName, looped, warn: false);
        ctx.Host.AddChild(music);
        director.Music = music;
        ctx.ExtraPrewarmSoundNames = script.SoundGroupNames();

        var plane = profile.Planes[Math.Clamp(profile.SelectedPlane, 0, profile.Planes.Count - 1)];
        string planeNode = PlanePickerRoster.AirframeNode(plane.Airframe);
        try
        {
            ctx.WithWorld(chapter, collision: false, missionFolder, world =>
                RunTheMission(ctx, world, director, script, planeNode, fit, stock, music, report));
        }
        finally
        {
            music.Stop();
            ctx.Host.RemoveChild(music);
            music.Free();
        }
    }

    // One flown mission over its built world, in the session's own order: the aircraft, the
    // director, the intro, the flown primary, a mission-data music cue, and the end.
    private static void RunTheMission(
        TestContext ctx, TestWorld world, CampaignDirector director, ObjectiveScript script,
        string planeNode, LoadoutChoice fit, StockLoadouts stock, MusicPlayer music, StringBuilder report)
    {
        var aim = PrimaryTarget(script, world);
        ctx.Check(aim != null, $"the mission's primary objective names a reference this world carries");
        if (aim is not { } target)
        {
            return;
        }

        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
        var pool = new ProjectilePool(textures, null, null);
        FlightController? rig = null;
        ctx.Host.AddChild(pool);
        try
        {
            rig = BuildRig(ctx, world, planeNode, target, pool, fit, stock, report);
            var craft = rig;
            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Sounds = world.Runtime.Sounds,
                ListenerPosition = () => craft.WorldPosition,
                PlayerAircraft = () => craft,
                Projectiles = pool,
                Rng = new Random(1),
            });
            var graph = director.Graph!;
            report.AppendLine($"{script.Objectives.Count} objective(s), {graph.Rows.Count} display row(s)");
            ctx.Same(script.Objectives.Count, graph.Count, $"every objective the mission authors is armed");
            ctx.Check(graph.Rows.Count > 0, $"and the mission has a player-visible objectives display");

            PlayTheIntro(ctx, world, director, graph, report);
            FlyThePrimary(ctx, director, graph, rig, pool, target, report);
            CueMissionMusic(ctx, script, director, graph, music, report);
            EndTheMission(ctx, script, director, report);
        }
        finally
        {
            rig?.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    // The story mission's intro definition, hosted the way a session hosts it: the codes reach the
    // host through the runtime's own dispatch, and the hold stops the objectives clock (callback
    // 20's objectives half). A session plays it during the world bootstrap, before the director is
    // attached; here the graph exists first so the held clock is observable.
    private static void PlayTheIntro(
        TestContext ctx, TestWorld world, CampaignDirector director, ObjectiveGraph graph, StringBuilder report)
    {
        string? intro = null;
        foreach (string name in CutsceneController.IntroAnims)
        {
            if (world.Runtime.Handles(name))
            {
                intro = name;
                break;
            }
        }

        ctx.Check(intro != null, $"the mission's world carries one of the story intro definitions");
        if (intro == null)
        {
            return;
        }

        var host = new CutsceneController();
        host.WorldHeld = held => director.HoldForCutscene(held);
        var stage = new Node3D { Name = "CampaignLoopCutscene" };
        var runtime = new AnimRuntime { AutoStart = false, ManualAdvance = true, SoundHandledElsewhere = true };
        runtime.CallbackHost = host.Host;
        ctx.Host.AddChild(host);
        ctx.Host.AddChild(stage);
        ctx.Host.AddChild(runtime);
        try
        {
            runtime.Bind(stage, world.Session.Program.Subset(intro));
            host.BindWorld(runtime);
            runtime.Play(intro);
            ctx.Check(host.Playing && host.Anim == intro,
                $"'{intro}' took the session through the runtime's own CALLBACK dispatch");
            ctx.Check(host.HoldsWorld && host.Presenting && host.OutOfFlight,
                $"the world is held, the chrome is off and the player is out of flight");

            for (float t = 0f; t < 5f; t += StepDt)
            {
                director.Step(StepDt);
            }

            ctx.Same(0, (long)graph.Elapsed, $"the objectives clock does not advance under the movie");
            runtime.Stop(intro);
            host.Tick();
            ctx.Check(!host.Playing && !host.HoldsWorld, $"the definition's end hands the session back");
            director.Step(StepDt);
            ctx.Check(graph.Elapsed > 0f, $"and the objectives run again once it has");
            report.AppendLine($"intro '{intro}': {host.Codes.Count} callback(s) hosted");
        }
        finally
        {
            ctx.Host.RemoveChild(runtime);
            ctx.Host.RemoveChild(stage);
            ctx.Host.RemoveChild(host);
            runtime.Free();
            stage.Free();
            host.Free();
        }
    }

    // The player's aircraft for the flown leg: the profile's own airframe, fitted with the
    // ammunition the ammo screen picked, placed on the approach the primary objective asks for.
    private static FlightController BuildRig(
        TestContext ctx, TestWorld world, string planeNode, Vector3 target, ProjectilePool pool,
        LoadoutChoice fit, StockLoadouts stock, StringBuilder report)
    {
        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
        try
        {
            var stats = PlaneStats.Load(ctx.ZrdrPath, planeNode);
            var model = new PlaneBuilder(GameZ.Load(ctx.PlanesGamezPath), textures).Build(planeNode);
            var rig = new FlightController
            {
                PlaneModel = model,
                Collider = PlaneCollider.Build(model),
                Damage = stats.DestroyableParts.Count > 0 || stats.VehicleHealth is > 0f
                    ? PlaneDamage.For(stats)
                    : null,
                PlayerIndex = FlightRoster.ShooterIdBase,
                IsHumanPiloted = true,
                Projectiles = pool,
                UseKeyboard = false,
                PadDevices = Array.Empty<int>(),
                AllowPause = false,
                Team = AimAssist.PlayerTeam,
                Name = "CampaignLoopPlayer",
            };
            rig.AddChild(model);
            ctx.Host.AddChild(rig);
            CheckBoundFit(ctx, model, planeNode, fit, stock, report);

            var approach = new Vector3(1f, 0f, 1f).Normalized();
            var start = target - (approach * ApproachRangeM) + (Vector3.Up * ApproachLiftM);
            rig.Setup(new FlightModel(stats), null, new CamParams(), start,
                target + (Vector3.Up * ApproachAimUpM), ApproachThrottle, ApproachSpeedMps);
            return rig;
        }
        finally
        {
            textures.Dispose();
        }
    }

    // The ammo screen's pick as the flying aircraft's own weapon: the shell's fit laid over the
    // airframe's stock loadout and bound to the built model, which is the session's own path.
    private static void CheckBoundFit(
        TestContext ctx, Node3D model, string planeNode, LoadoutChoice fit, StockLoadouts stock,
        StringBuilder report)
    {
        if (stock.ForModel(planeNode) is not { } stockDef)
        {
            return;
        }

        var bound = Loadout.Bind(fit.ApplyTo(stockDef), model, WeaponDefs.Load(ctx.ZrdrPath, null));
        string ammo = CampaignLoadout.AmmoNames[ChosenAmmo];
        bool carries = false;
        foreach (var gun in bound.Guns)
        {
            carries |= gun.Weapon.Id == StockLoadouts.GunWeaponId(CaliberOf(stockDef, gun.Slot), ammo);
        }

        report.AppendLine($"bound fit: {bound.Guns.Count} gun group(s) on {planeNode}, '{ammo}' mounted={carries}");
        ctx.Check(carries, $"the aircraft that flies carries the ammunition the ammo screen picked");
    }

    // The primary objective completed by flying it: the aircraft closes on the reference its own
    // TRAVELERS condition names, and the condition is read off the flown position, not planted.
    private static void FlyThePrimary(
        TestContext ctx, CampaignDirector director, ObjectiveGraph graph, FlightController rig,
        ProjectilePool pool, Vector3 target, StringBuilder report)
    {
        ctx.Check(!graph.Rows[0].Completed, $"the primary is not complete before the approach is flown");
        float closest = float.MaxValue;
        float flown = 0f;
        for (float t = 0f; t < ApproachBudgetS && !graph.Rows[0].Completed; t += StepDt)
        {
            pool.SimStep(StepDt);
            rig.SimStep(StepDt);
            director.Step(StepDt);
            closest = Mathf.Min(closest, rig.WorldPosition.DistanceTo(target));
            flown = t;
        }

        report.AppendLine($"approach: {flown:0.0}s flown, closest {closest:0} m, " +
            $"primary completed={graph.Rows[0].Completed}");
        ctx.Check(graph.Rows[0].Completed,
            $"flying the approach completed the primary objective, closest {closest:0} m");
        int primaryBit = 1 << graph.Rows[0].Priority;
        ctx.Check((graph.CompletedMask & primaryBit) != 0,
            $"which the recorded mask carries at bit {graph.Rows[0].Priority}, its IDENTITY priority rather than its row position");
    }

    // Where the primary objective's own TRAVELERS condition points: the world node it names, or the
    // literal point it authors. The primary is the lowest IDENTITY priority the mission carries.
    private static Vector3? PrimaryTarget(ObjectiveScript script, TestWorld world)
    {
        ObjectiveDef? primary = null;
        foreach (var def in script.Objectives)
        {
            if (def.Identity is { } identity && def.Travelers != null
                && (primary?.Identity is not { } best || identity.Priority < best.Priority))
            {
                primary = def;
            }
        }

        if (primary?.Travelers is not { } spec)
        {
            return null;
        }

        if (spec.WherePoint is { Length: >= 3 } point)
        {
            return new Vector3(point[0], point[1], point[2]);
        }

        var found = spec.WhereNode != null ? world.Runtime.FindNodes(spec.WhereNode) : null;
        return found is { Count: > 0 } ? found[0].GlobalPosition : null;
    }

    private static int CaliberOf(LoadoutDef def, int slot)
    {
        foreach (var gun in def.Guns)
        {
            if (gun.Slot == slot)
            {
                return gun.Caliber;
            }
        }

        return 0;
    }

    // A track the MISSION DATA cues, rather than a game state: an objective's own
    // COMPLETED_SOUND_GROUP naming a music group, woken and completed through the graph. The one
    // routing (director's sound-group executor) serves both the wake and the completion form.
    private static void CueMissionMusic(
        TestContext ctx, ObjectiveScript script, CampaignDirector director, ObjectiveGraph graph,
        MusicPlayer music, StringBuilder report)
    {
        string before = music.Current;
        foreach (var def in MusicObjectives(script))
        {
            graph.Wake(def.Number);
            for (float t = 0f; t < 3f && !graph.CompletedOf(def.Number); t += StepDt)
            {
                director.Step(StepDt);
            }

            if (!graph.CompletedOf(def.Number) || music.Current == before)
            {
                continue;
            }

            report.AppendLine($"OBJECTIVE{def.Number} '{def.CompletedSoundGroup}' -> {music.Current}");
            ctx.Check(true, $"a track the mission data cues is playing: '{def.CompletedSoundGroup}' -> {music.Current}");
            return;
        }

        ctx.Check(false, $"one of this mission's music sound groups reached the music channel");
    }

    // The objectives carrying a music COMPLETED_SOUND_GROUP, the prebattle cue first: it is the
    // one the plan named as never yet observed, and it sits on a BEGIN_DORMANT objective.
    private static List<ObjectiveDef> MusicObjectives(ObjectiveScript script)
    {
        var preferred = new List<ObjectiveDef>();
        var rest = new List<ObjectiveDef>();
        foreach (var def in script.Objectives)
        {
            if (def.CompletedSoundGroup is not { } group
                || !group.StartsWith("music", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (group.Contains("prebattle", StringComparison.OrdinalIgnoreCase))
            {
                preferred.Add(def);
            }
            else
            {
                rest.Add(def);
            }
        }

        preferred.AddRange(rest);
        return preferred;
    }

    // The mission's own end: its INSTANTWIN objective, woken through the graph, which records the
    // attempt into the profile, writes the file and raises the return-to-cabin exit.
    private static void EndTheMission(
        TestContext ctx, ObjectiveScript script, CampaignDirector director, StringBuilder report)
    {
        var graph = director.Graph!;
        int winner = 0;
        foreach (var def in script.Objectives)
        {
            if (def.InstantWin)
            {
                winner = def.Number;
                break;
            }
        }

        ctx.Check(winner > 0, $"the mission authors an INSTANTWIN objective");
        if (winner == 0)
        {
            return;
        }

        graph.Wake(winner);
        for (float t = 0f; t < 3f && !director.ReturnToCabin; t += StepDt)
        {
            director.Step(StepDt);
        }

        ctx.Check(director.ReturnToCabin, $"the mission end raised the return-to-cabin exit");
        if (director.Result is not { } result)
        {
            return;
        }

        report.AppendLine($"ended {result.Outcome}, mask 0x{result.Attempt.CompletedMask:x}, " +
            $"{result.Attempt.TimeMs} ms, primary={result.Recorded.PrimaryCompleted}, advanced={result.Recorded.Advanced}");
        ctx.Check(result.Outcome == MissionOutcome.Won, $"the outcome is the graph's own, {result.Outcome}");
        ctx.Check(result.Recorded.PrimaryCompleted, $"the flown primary is what the record gates on");
        ctx.Check(result.Recorded.Advanced, $"and the campaign position advanced");
    }

    // The cabin the campaign flow lands on for a fresh profile pick, checked here on a flow built
    // straight from the store (Launcher.OpenDebrief -> LaunchMenu.OpenCampaignScrapbook opens the
    // debrief on top of it instead, which is not this suite's own path).
    private static void ReturnToTheCabin(
        TestContext ctx, CampaignProfileStore store, UiStrings strings, StockLoadouts stock, StringBuilder report)
    {
        var reread = store.Load(ProfileName);
        ctx.Check(reread != null, $"the flown mission's profile reads back from its file");
        if (reread == null)
        {
            return;
        }

        var flow = new CampaignFlow(store, strings, ctx.DataRoot, planes: null, stock);
        flow.SelectProfile(reread);
        ctx.Check(flow.Screen == CampaignScreen.Cabin, $"the return lands on the cabin, screen={flow.Screen}");
        ctx.Same(FirstSeq + 1, reread.MissionsCompleted, $"the profile file carries the flown mission");
        ctx.Same(FirstSeq + 1, CampaignProgression.NextMissionSeq(reread), $"Next Mission is the one after it");
        ctx.Check(CampaignProgression.ResultOf(reread, FirstSeq) != null,
            $"and the mission's own result is recorded");
        ctx.Same(1, CampaignProgression.CompletedSeqs(reread).Count,
            $"Previous Missions has a finished mission to offer");

        flow.FocusRow(0);
        flow.Accept();
        ctx.Check(flow.Screen == CampaignScreen.Briefing && flow.MissionSeq == FirstSeq + 1,
            $"NEXT MISSION now opens the briefing on seq {flow.MissionSeq}");
        report.AppendLine($"cabin: missionsCompleted={reread.MissionsCompleted}, next={flow.MissionSeq}, " +
            $"results={reread.MissionResults.Count}");
    }

    // The closing film's gate at the door a mission end takes: the film plays after a win on the
    // campaign's last mission, whether that win finishes the campaign or replays it, and after
    // nothing else. Its own store, never the loop's, so no arm can write the persisted profile.
    private static void CheckTheClosingFilm(TestContext ctx, UiStrings strings, StringBuilder report)
    {
        int last = CampaignSequence.MissionCount - 1;
        string dir = Path.Combine(ctx.ScratchDir, "campaign-loop-film", "Profiles");
        FilmArm(ctx, strings, dir, last, last, won: true, film: true, report,
            $"a won last mission with the profile not yet showing the campaign complete");
        FilmArm(ctx, strings, dir, CampaignSequence.MissionCount, last, won: true, film: true, report,
            $"a won replay of the last mission on a complete profile");
        FilmArm(ctx, strings, dir, CampaignSequence.MissionCount, last, won: false, film: false, report,
            $"a lost replay of the last mission on a complete profile");
        FilmArm(ctx, strings, dir, CampaignSequence.MissionCount, FirstSeq, won: true, film: false, report,
            $"a won replay of an earlier mission on a complete profile");
    }

    // One arm of that gate, over the door itself: a profile that has finished missions, the story
    // position just flown and how it ended, against whether the film was handed over and whether
    // the book arrived on the frame it stopped.
    private static void FilmArm(
        TestContext ctx, UiStrings strings, string dir, int missionsDone, int seq, bool won, bool film,
        StringBuilder report, string what)
    {
        var recorder = new FilmRecorder();
        var feature = new CampaignFeature(
            strings, PlanePickerRoster.AirframeNode, closingCinema: new ClosingCinema(recorder.Play));
        feature.Open(new CampaignProfileStore(dir), null, null, ctx.DataRoot);
        var flow = new CampaignFlow(feature);

        flow.OpenScrapbookAfterMission(FilmProfile(missionsDone), seq, won);

        ctx.Same(film ? 1 : 0, recorder.Plays, $"{what}: films played");
        if (film)
        {
            ctx.Check(recorder.Name == ClosingCinema.Name, $"{what}: the film is {recorder.Name}");
            ctx.Check(flow.Screen == CampaignScreen.Cabin, $"{what}: the book waits behind it ({flow.Screen})");
            recorder.Stop();
        }

        ctx.Check(flow.Screen == CampaignScreen.Scrapbook && flow.MissionSeq == seq,
            $"{what}: the book is open on the flown mission ({flow.Screen}, seq {flow.MissionSeq})");
        report.AppendLine($"closing film: {what} -> plays={recorder.Plays}, screen={flow.Screen}");
    }

    // A profile that has completed its first missionsDone missions, which is the state the store
    // holds when the mission-end door opens.
    private static CampaignProfileDef FilmProfile(int missionsDone)
    {
        var profile = CampaignProfileDef.NewProfile("Film");
        for (int seq = 0; seq < missionsDone; seq++)
        {
            CampaignProgression.Record(profile, new MissionAttempt(
                seq, CampaignProgression.PrimaryObjectiveMask, 300_000, 400, 120, 5, "Gypsy Magic"));
        }

        return profile;
    }

    // How many lines the reveal has written onto the parchment, which is what the row count used to
    // stand in for before an objective stopped being a row.
    private static int NoteEntries(ICampaignPage page)
    {
        int entries = 0;
        foreach (var note in page.Notes)
        {
            entries += note.Entries.Count;
        }

        return entries;
    }

    private static int RowOf(CampaignFlow flow, string text)
    {
        for (int row = 0; row < flow.Page.RowCount; row++)
        {
            if (flow.Page.RowText(row).StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                return row;
            }
        }

        return -1;
    }

    private static CampaignMission? MissionOf(IReadOnlyList<CampaignMission> missions, int seq)
    {
        foreach (var m in missions)
        {
            if (m.Seq == seq)
            {
                return m;
            }
        }

        return null;
    }

    // The stand-in for Launcher.PlayCinema: it records what it was asked for and hands the film's
    // end back, so the arm decides when the cinema stops.
    private sealed class FilmRecorder
    {
        private Action? _then;

        public string? Name { get; private set; }

        public int Plays { get; private set; }

        public void Play(string name, Action then, CinemaSkip skip)
        {
            Name = name;
            Plays++;
            _then = then;
        }

        public void Stop() => _then?.Invoke();
    }
}
