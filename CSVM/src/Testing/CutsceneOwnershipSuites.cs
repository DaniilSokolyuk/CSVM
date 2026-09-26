using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>The suite over which definition a cutscene episode belongs to when the objective script
/// starts it, rather than a landings row. The original holds the STARTED instance in its trigger
/// slot, so the episode ends when that definition and everything in its call closure has, however
/// deep the callee raising the first code sits; CSVM books the same slot on every trigger path.
/// Driven over C5/M02's built world and its own objective chain, whose ending is the one shipped
/// case of the shape: an objective wakes a definition that raises no code and calls the one that
/// does. Decode: docs/formats/anim-definitions/cutscenes.md.</summary>
internal static class CutsceneOwnershipSuites
{
    private const string Chapter = "C5";
    private const string Folder = "M02";
    private const float StepDt = 1f / 60f;

    // How long the woken episode is given to reach its own end, and how long a definition woken
    // ahead of it is left running so a slot it wrongly claimed would still be live.
    private const float PlayBudgetS = 40f;
    private const float SettleS = 2f;

    [Suite("campaign-cutscene-ownership",
        "which definition an episode belongs to when the OBJECTIVE SCRIPT starts it rather than "
        + "a landings row, over C5/M02's BUILT world and its own objective chain: an ordinary "
        + "WAKE_ANIM left running claims no cutscene slot, the mission's ending is woken and "
        + "its first code comes from a callee, yet the episode belongs to the definition the "
        + "objective started and outlives that callee to the code the mission ends on")]
    internal static void CutsceneOwnership(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        var mission = MissionIn(CampaignSequence.Load(ctx.ZrdrPath), Chapter, Folder)
            ?? throw new SuiteSkippedException($"cm_sequence carries no {Chapter}/{Folder}");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Folder);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Folder} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, Chapter), $"{Chapter} textures");

        var script = ObjectiveScript.Load(missionZrdr);
        var report = new StringBuilder();
        report.AppendLine($"{Chapter}/{Folder}, story position {mission.Seq}");
        ctx.ExtraPrewarmSoundNames = script.SoundGroupNames();
        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(Chapter, collision: false, Folder,
                world => Drive(ctx, world, mission, script, report));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-cutscene-ownership-{Chapter}-{Folder}.txt", report.ToString());
        ctx.Note($"woke {Chapter}/{Folder}'s own objective cutscene and read which definition owns it");
    }

    private static void Drive(TestContext ctx, TestWorld world, CampaignMission mission,
        ObjectiveScript script, StringBuilder report)
    {
        var started = OwnerOf(world, script);
        string? startedAnim = started?.WakeAnim?.Anim;
        report.AppendLine($"objective-started cutscene: '{startedAnim ?? "(none)"}' from " +
            $"OBJECTIVE{started?.Number.ToString() ?? "?"}");
        ctx.Check(started != null,
            $"the mission's objective script wakes a definition whose call closure authors a CALLBACK");
        var inert = InertWakeOf(world, script, startedAnim);
        report.AppendLine($"non-cutscene wake ahead of it: '{inert?.WakeAnim?.Anim ?? "(none)"}' from " +
            $"OBJECTIVE{inert?.Number.ToString() ?? "?"}");
        if (started is not { } owner)
        {
            return;
        }

        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var director = CampaignDirector.Create(script, mission, profile, null);
        try
        {
            Play(ctx, world, cutscene, director, owner, inert, report);
        }
        finally
        {
            foreach (var spawned in director.Roster.Values)
            {
                spawned.Free();
            }

            world.Runtime.CallbackHost = null;
            world.Runtime.MissionTriggerOwner = null;
            cutscene.Free();
        }
    }

    private static void Play(TestContext ctx, TestWorld world, CutsceneController cutscene,
        CampaignDirector director, ObjectiveDef owner, ObjectiveDef? inert, StringBuilder report)
    {
        cutscene.BindWorld(world.Runtime, world.Session.Aircraft);
        cutscene.HostDefinitions(ClosureOf(world, owner.WakeAnim!.Value.Anim));
        // The session's own wiring, and the whole subject of this suite: the slot is on the runtime,
        // so the objective script's WAKE_ANIM writes it the same way an approach row's start does.
        world.Runtime.MissionTriggerOwner = anim => cutscene.Own(anim);
        float now = 0f;
        string? firstRaiser = null;
        float completedAt = -1f;
        world.Runtime.CallbackHost = (code, anim, root) =>
        {
            firstRaiser ??= anim;
            report.AppendLine($"  t={now,6:0.00} code {code} from '{anim}'");
            return cutscene.Host(code, anim, root);
        };
        director.Attach(new CampaignDirector.WorldInputs
        {
            Runtime = world.Runtime,
            Sounds = world.Runtime.Sounds,
            ListenerPosition = () => Vector3.Zero,
            PlayerAircraft = () => null,
            Rng = new Random(1),
        });
        var graph = director.Graph!;
        cutscene.MissionComplete = () =>
        {
            completedAt = now;
            graph.NotifyDockingComplete();
        };

        // A definition with no CALLBACK anywhere in its closure, woken first and left running: it
        // goes through the same trigger call, and a slot it claimed would outrank the real owner.
        if (inert is { } ahead)
        {
            graph.Wake(ahead.Number);
            for (float t = 0f; t < SettleS; t += StepDt)
            {
                world.Runtime.Advance(StepDt);
                graph.Step(StepDt);
                cutscene.Tick();
                now += StepDt;
            }

            report.AppendLine($"'{ahead.WakeAnim!.Value.Anim}' woken, state=" +
                $"{world.Runtime.AnimStateOf(ahead.WakeAnim!.Value.Anim)}, playing={cutscene.Playing}");
            ctx.Check(!cutscene.Playing, $"waking '{ahead.WakeAnim!.Value.Anim}' starts no episode");
        }

        string ownerAnim = owner.WakeAnim!.Value.Anim;
        graph.Wake(owner.Number);
        report.AppendLine($"woke OBJECTIVE{owner.Number}: '{ownerAnim}', playing={cutscene.Playing}, " +
            $"anim='{cutscene.Anim ?? "-"}'");
        ctx.Check(cutscene.Playing, $"waking OBJECTIVE{owner.Number} starts '{ownerAnim}' under the host");
        ctx.Check(firstRaiser != null && !string.Equals(firstRaiser, ownerAnim, StringComparison.OrdinalIgnoreCase),
            $"whose first code is raised by a callee ('{firstRaiser ?? "-"}'), not by the woken definition itself");
        ctx.Check(string.Equals(cutscene.Anim, ownerAnim, StringComparison.OrdinalIgnoreCase),
            $"yet the episode belongs to '{ownerAnim}', the definition the objective started, which is the original's trigger slot (read '{cutscene.Anim ?? "-"}')");

        float endedAt = -1f;
        for (float t = 0f; t < PlayBudgetS; t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            graph.Step(StepDt);
            cutscene.Tick();
            now += StepDt;
            if (endedAt < 0f && !cutscene.Playing)
            {
                endedAt = now;
                report.AppendLine($"  t={now,6:0.00} the host handed the session back");
            }
        }

        string raiser = firstRaiser ?? ownerAnim;
        report.AppendLine($"episode ended at t={endedAt:0.00}, completion code at t={completedAt:0.00}, " +
            $"'{raiser}' state={world.Runtime.AnimStateOf(raiser)}, outcome={graph.Outcome} ending={graph.Ending}");
        ctx.Check(endedAt < 0f || completedAt < 0f || endedAt >= completedAt - StepDt,
            $"the episode outlives the callee that raised its first code, ending no earlier than the code it ends on");
        ctx.Check(completedAt >= 0f,
            $"'{raiser}' raises the mission-completion code the ending hangs on");
        ctx.Check(graph.Ending || graph.Outcome == MissionOutcome.Won,
            $"and that code wins the mission, this mission's only ending");
    }

    // The objective whose WAKE_ANIM starts a definition that hosts a cutscene: read out of the
    // script and the compiled definitions rather than named here, so the case stays the data's.
    private static ObjectiveDef? OwnerOf(TestWorld world, ObjectiveScript script)
    {
        foreach (var def in script.Objectives)
        {
            if (def.WakeAnim is { } wake && world.Runtime.Handles(wake.Anim)
                && RaisersIn(world, wake.Anim).Count > 0)
            {
                return def;
            }
        }

        return null;
    }

    // An objective whose WAKE_ANIM starts a definition with no CALLBACK anywhere in its closure:
    // the ordinary mission trigger, which must never claim the cutscene slot.
    private static ObjectiveDef? InertWakeOf(TestWorld world, ObjectiveScript script, string? owner)
    {
        foreach (var def in script.Objectives)
        {
            if (def.WakeAnim is { } wake && world.Runtime.Handles(wake.Anim)
                && !string.Equals(wake.Anim, owner, StringComparison.OrdinalIgnoreCase)
                && RaisersIn(world, wake.Anim).Count == 0)
            {
                return def;
            }
        }

        return null;
    }

    private static List<string> RaisersIn(TestWorld world, string anim)
    {
        var found = new List<string>();
        foreach (var def in world.Runtime.CallClosureOf(anim))
        {
            foreach (var seq in def.Sequences)
            {
                foreach (var ev in seq.Events)
                {
                    if (ev.Kind == "Callback" && def.AnimName is { Length: > 0 } name
                        && !found.Contains(name))
                    {
                        found.Add(name);
                    }
                }
            }
        }

        return found;
    }

    private static IReadOnlyList<string> ClosureOf(TestWorld world, string anim)
    {
        var names = new List<string>();
        foreach (var def in world.Runtime.CallClosureOf(anim))
        {
            if (def.AnimName is { Length: > 0 } name && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static CampaignMission? MissionIn(IReadOnlyList<CampaignMission> missions,
        string chapter, string folder)
    {
        foreach (var mission in missions)
        {
            if (string.Equals(mission.ChapterFolder, chapter, StringComparison.OrdinalIgnoreCase)
                && string.Equals(mission.MissionFolder, folder, StringComparison.OrdinalIgnoreCase))
            {
                return mission;
            }
        }

        return null;
    }
}
