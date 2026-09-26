using System;
using System.Collections.Generic;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using Godot;

namespace CSVM.Testing;

/// <summary>C5/M01's six Destroy Support Beam sites (<c>[rfspt4/5/6, lfspt1/2/3, "healthy",
/// "spprt"]</c>): each is its own <c>ADD_OBJECTIVE_TARGET</c> entry, removed only by its OWN
/// objective's <c>REMOVE_OBJECTIVE_TARGET</c>, never by a kill. <see cref="ObjectiveSites.Collect"/>
/// drops a site's <c>Live</c> flag the instant its resolved node's own destructible pool reads
/// <see cref="DestructibleRegistry.State.Destroyed"/>, whether or not that removal has run
/// (<see cref="ObjectiveSites.LiveDespiteState"/>). Driven against the chapter's own real node and
/// destructible wiring; the objective that ADDs the six keys gates on a flown TRAVELERS approach
/// this suite has no leg to satisfy, so a synthetic objective appended to the mission's own shipped
/// script carries the same six real paths and is woken directly.</summary>
internal static class CampaignSupportBeamSuites
{
    private const string Chapter = "C5";

    private const string Mission = "M01";

    private const string BeamKey = "rfspt4/healthy/spprt";

    [Suite("campaign-support-beam-destroyed",
        "C5/M01's rfspt4 Destroy Support Beam site over the chapter's own BUILT world: offered "
        + "live once its ADD_OBJECTIVE_TARGET fires, and dropped from Live the instant a lethal "
        + "hit marks its resolved 'spprt' node's own destructible pool Destroyed, though the "
        + "site's REMOVE_OBJECTIVE_TARGET has not run and its own objective has not completed")]
    internal static void CampaignSupportBeamDestroyed(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, Chapter), $"{Chapter} textures");

        CampaignMission? found = null;
        foreach (var m in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (m.ChapterFolder.Equals(Chapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(Mission, StringComparison.OrdinalIgnoreCase))
            {
                found = m;
            }
        }

        if (found is not { } mission)
        {
            throw new SuiteSkippedException($"{Chapter}/{Mission} is not in cm_sequence");
        }

        var shipped = ObjectiveScript.Load(missionZrdr);
        var targets = MissionTargets.Load(missionZrdr, SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter));
        var messages = Messages.Load(ctx.MessagesPath);

        // Appended past the last shipped number, so OBJECTIVE7's own real TRAVELERS-gated
        // ADD_OBJECTIVE_TARGET (the mission's real path to these six keys) is untouched and the
        // parser's contiguous-from-1 walk still reaches every shipped objective.
        int synthetic = shipped.Objectives.Count + 1;
        var script = ObjectiveScript.Parse(
            WithSyntheticAdd(Zrdr.LoadFileOrEmpty(missionZrdr, "objectives.json"), synthetic));
        ctx.Same(synthetic, script.Objectives.Count,
            $"the driven script is the shipped one with a single objective appended");

        var director = CampaignDirector.Create(script, mission,
            CampaignProfileDef.NewProfile("Zachary"), null, missionZrdr);

        ctx.WithWorld(Chapter, collision: false, Mission, world =>
        {
            var listener = ctx.Camera.GlobalPosition;
            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Gamez = world.Gamez,
                Sounds = world.Runtime.Sounds,
                ListenerPosition = () => listener,
                Rng = new Random(1),
            });
            var graph = director.Graph!;
            graph.Step(0.1f);
            graph.Wake(synthetic);
            // Step() resolves one objective per tick round robin over the mission's own 64 blocks
            // (script.Objectives.Count with the synthetic one appended), so completing a specific,
            // freshly-woken one needs more than one round rather than one Step call.
            for (float t = 0f; t < 8f; t += 0.1f)
            {
                graph.Step(0.1f);
            }

            ctx.Check(graph.IsObjectiveTarget(BeamKey),
                $"waking the synthetic objective lands '{BeamKey}' in the graph's own target store");

            var beam = ObjectiveSites.ResolveTarget(world.Runtime, ObjectiveTarget.Parse(BeamKey));
            ctx.Check(beam != null, $"'{BeamKey}' resolves to a real world node");
            var inst = beam == null ? null : world.Runtime.Destructibles.Resolve(beam);
            ctx.Check(inst != null, $"'{BeamKey}' resolves through a registered destructible pool");
            if (beam == null || inst == null)
            {
                return;
            }

            var sites = new ObjectiveSites(director, messages, targets, world.Runtime);
            var before = new List<AimCandidate>();
            sites.Collect(before);
            ctx.Check(FindBeam(before) is { Live: true },
                $"the beam is offered LIVE before it takes any damage");

            bool landed = world.Runtime.DamageAt(inst.DamageNode, inst.MaxHealth + 1f);
            ctx.Check(landed && inst.Status == DestructibleRegistry.State.Destroyed,
                $"a lethal hit on the beam's own damage node marks its pool Destroyed");
            ctx.Check(graph.IsObjectiveTarget(BeamKey),
                $"the kill runs no REMOVE_OBJECTIVE_TARGET: the key is still in the graph's own store");

            var after = new List<AimCandidate>();
            sites.Collect(after);
            var site = FindBeam(after);
            ctx.Check(site is { Live: false },
                $"the destroyed beam's site is still OFFERED (its own REMOVE_OBJECTIVE_TARGET never ran) but no longer LIVE, the destructible state alone took it out of the cycle");
        });
    }

    private static AimCandidate? FindBeam(List<AimCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Source is ObjectiveSite site && site.Target.Is(BeamKey))
            {
                return candidate;
            }
        }

        return null;
    }

    // The shipped body with one more objective on the end, waking on command rather than on a
    // flown TRAVELERS approach, carrying the mission's own three-deep beam path. The file is a
    // flat alternating key/value list, so this is an append.
    private static List<object?> WithSyntheticAdd(List<object?> root, int number)
    {
        if (root.Count == 0 || root[0] is not List<object?> body)
        {
            return root;
        }

        body.Add("OBJECTIVE" + number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        body.Add(new List<object?>
        {
            "BEGIN_DORMANT",
            new List<object?> { -1f },
            "ADD_OBJECTIVE_TARGET",
            new List<object?>
            {
                new List<object?> { "rfspt4", "healthy", "spprt" },
            },
        });
        return root;
    }
}
