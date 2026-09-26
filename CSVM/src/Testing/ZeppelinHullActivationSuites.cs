using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Mech3;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>CM13 (C2/M03) over its own world: the Pandora's hull ships with its gamez active bit
/// clear and no mission script sets it back, so only the zeppelin record activates it. Everything
/// the end-of-race dock needs (the hook point, the hangar bay, both landing cones) hangs off that
/// hull, which is why the dock could arm and work against a hull nothing drew.</summary>
internal static class ZeppelinHullActivationSuites
{
    private const string Chapter = "C2";
    private const string Mission = "M03";
    private const string Hull = "piratezep";
    private const string Hook = "pzhookpoint";

    // What OBJECTIVE8 wakes once the race is won: it opens the hangar bay, calls pz_deploy_hook,
    // and switches both landing cones on. The dock is live from here.
    private const string DockAnim = "pzhomebase";

    [Suite("zeppelin-hull-activation",
        "CM13's Pandora over C2/M03's real world: C2 alone ships the piratezep node with its " +
        "gamez active bit clear and no mission .gw sets it back, so the world builds the hull " +
        "hidden and the zeppelin record is what switches it on. With it on, the hook point, " +
        "the hangar bay and both landing cones resolve UNDER the hull, and after pzhomebase " +
        "(the anim OBJECTIVE8 wakes at the end of the race) has run its dock choreography the " +
        "hull, the hook and the hangar bay all draw, rather than a live dock on an invisible " +
        "Pandora")]
    internal static void ZeppelinHullActivation(TestContext ctx)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, Chapter), $"{Chapter} textures");

        ZeppelinDef? def = null;
        foreach (var d in Zeppelins.Load(missionZrdr))
        {
            if (d.Node.Equals(Hull, StringComparison.OrdinalIgnoreCase))
            {
                def = d;
            }
        }

        ctx.Check(def is { Deactivated: false },
            $"{Chapter}/{Mission} authors a {Hull} record that is not deactivated, so it is in play from t=0");
        if (def == null)
        {
            return;
        }

        var nets = AiNets.Load(chapterZrdr);
        ctx.WithWorld(Chapter, collision: false, Mission, world => DriveDock(ctx, world, def, nets));
        ctx.Note($"{Chapter}/{Mission}: the record switches the Pandora's hull on, and the whole dock ({Hook}, hookbay, both landing cones) draws with it");
    }

    private static void DriveDock(TestContext ctx, TestWorld world, ZeppelinDef def,
        IReadOnlyList<AiNet> nets)
    {
        var report = new StringBuilder();
        int shipped = 0, inactive = 0;
        foreach (var node in world.Gamez.Nodes)
        {
            if (!node.Name.Equals(Hull, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            shipped++;
            if (!node.Active)
            {
                inactive++;
            }
        }

        ctx.Check(shipped == 1 && inactive == 1,
            $"{Chapter}'s gamez ships the one {Hull} node with its active bit CLEAR: shipped={shipped} inactive={inactive}");

        var host = world.Runtime.FindNodes(Hull, null) is { Count: > 0 } hits ? hits[0] : null;
        ctx.Check(host != null, $"the built world carries the {Hull} node");
        if (host == null)
        {
            return;
        }

        ctx.Check(!host.IsVisibleInTree(),
            $"…built hidden, because the world build honours that bit and the mission's own .gw never sets it");

        ZeppelinRuntime? zeps = null;
        try
        {
            zeps = new ZeppelinRuntime(new[] { def },
                name => world.Runtime.FindNodes(name, null) is { Count: > 0 } h ? h[0] : null,
                nets, null, world.Runtime.Motions.DrivesTransform);
            ctx.Host.AddChild(zeps);
            zeps.WireDamage(world.Runtime);

            ctx.Same(1, zeps.LiveCount, $"the record places one live zeppelin over the built world");
            ctx.Check(host.Visible && host.IsVisibleInTree(),
                $"…and the record IS the hull's activation: {Hull} draws once the runtime has it");
            ctx.Check(!zeps.IsDormant(Hull),
                $"…with nothing waiting on a script wake-up, so its parts are targets from t=0");

            // The hook and the two landing cones ride the hull, which is the whole point: the dock
            // could arm and work while nothing drew, because arming reads poses and not visibility.
            foreach (var name in new[] { Hook, "hookbay", "pz_manual_land", "pz_auto_land" })
            {
                var found = world.Runtime.FindNodes(name, null);
                var under = world.Runtime.FindNodes(name, host);
                ctx.Check(found.Count > 0 && under.Count == found.Count,
                    $"'{name}' is built and every copy of it sits on the hull: total={found.Count} under {Hull}={under.Count}");
            }

            report.AppendLine($"{Chapter}/{Mission}: {Hull} at {host.GlobalPosition} visible={host.IsVisibleInTree()}");

            var started = world.Runtime.Play(DockAnim);
            ctx.Check(started.Count > 0, $"'{DockAnim}', the anim OBJECTIVE8 wakes, starts count={started.Count}");

            // pz_deploy_hook's own longest track (top_seg's 10 s rotate) starts 3 s in and gates
            // the land_on activations behind WAIT_FOR_COMPLETION. The cones are not due before
            // 13 s, so this runs well past that.
            const float dt = 1f / 60f;
            for (int i = 0; i < 60 * 20; i++)
            {
                world.Runtime.Advance(dt);
                zeps.SimStep(dt);
            }

            var hook = world.Runtime.FindNodes(Hook, host);
            ctx.Check(hook.Count > 0 && hook[0].IsVisibleInTree(),
                $"with the dock live the hook draws too, rather than an invisible Pandora to dock with");
            var bay = world.Runtime.FindNodes("hookbay", host);
            ctx.Check(bay.Count > 0 && bay[0].IsVisibleInTree(),
                $"…and so does the hangar bay {DockAnim} switches on, so the choreography lands on a hull that is in the world");
            ctx.Check(host.IsVisibleInTree(),
                $"…the hull still drawing once the dock choreography has run its course");

            // Each cone is a distinct node (land_on under pz_manual_land, land_on~2's own sibling
            // under pz_auto_land) that pzhomebase's compiled symbol table addresses by its own
            // gamez index, never by ambiguous name, so both draw once their activation lands.
            var cone = world.Runtime.FindNodes("land_on", host);
            int shownCones = 0;
            foreach (var c in cone)
            {
                if (c.IsVisibleInTree())
                {
                    shownCones++;
                }
            }

            ctx.Check(cone.Count == 2 && shownCones == cone.Count,
                $"both landing cones draw once the dock choreography has run: cones {shownCones}/{cone.Count}");
            report.AppendLine($"dock live: hook visible={hook.Count > 0 && hook[0].IsVisibleInTree()} cones {shownCones}/{cone.Count}");
        }
        finally
        {
            zeps?.Free();
        }

        ctx.WriteArtifact("test-zeppelin-hull-activation.txt", report.ToString());
    }
}
