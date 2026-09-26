using System.Collections.Generic;
using CSVM.Flight.Hud;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using Godot;

namespace CSVM.Testing;

/// <summary>A two-player stunt race over C1/IA1's authored Danger Zones, driven through the same
/// per-pilot <see cref="TargetSelection"/> the pilot HUD reads: each pane cycles its own zones, one
/// pane's step never moves the other's marker, and a pilot who clears the course empties their own
/// cycle alone. Also pins the marker the zone draws, which is the objective marker every other
/// site takes rather than a style of its own.</summary>
internal static class StuntTargetSuites
{
    private const string Chapter = "C1";
    private const string Mission = "IA1";

    // A non-destructive objective's own label, the colour class a Danger Zone falls in
    // (Target::GetColor, docs/org/targeting.md): anything but Destroy/Disable/Disable Engines/Damage.
    private const string ProtectLabel = "Protect";

    // Two poses over C1's map, one per pilot. They differ on purpose: the cycle is sorted against
    // each plane's own basis and position, so two pilots racing one course see two orders.
    private static readonly Vector3 P1Pose = new(-5000f, 300f, -6000f);
    private static readonly Vector3 P2Pose = new(-7000f, 300f, -2500f);

    [Suite("stunt-target-cycle",
        "a two-player stunt race over C1/IA1's danger zones on the pilots' own target cycles: "
        + "every unflown zone is an objective-class entry labelled 'Danger Zone [Fly Through] - "
        + "<name>' in the non-destructive objective's colour, each pane steps its own zones, one "
        + "pane's step leaves the other's marker alone, and clearing the course empties that "
        + "pilot's cycle and nobody else's")]
    internal static void StuntTargetCycle(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(ctx.MessagesPath, $"message table");

        ctx.WithWorld(Chapter, collision: false, Mission, world =>
        {
            var mine = StuntMission.Load(world.Gamez, missionZrdr, Messages.Load(ctx.MessagesPath));
            ctx.Check(mine != null, $"{Chapter}/{Mission} ships danger zones for a stunt run");
            if (mine is not { } p1Run)
            {
                return;
            }

            var p2Run = p1Run.ForAnotherPlayer();
            var p1 = Selection(p1Run, P1Pose);
            var p2 = Selection(p2Run, P2Pose);

            ctx.Check(p1.Pool.Enemy.Count == p1Run.TotalCount
                      && p2.Pool.Enemy.Count == p2Run.TotalCount,
                $"each pilot's Enemy/Objective cycle holds all {p1Run.TotalCount} unflown zones");
            ctx.Check(AllObjectives(p1.Pool.Enemy) && AllObjectives(p2.Pool.Enemy),
                $"every zone rides the cycle as an OBJECTIVE, the class the original's marker is");
            CheckMarker(ctx, p1);

            // The auto-acquire: with the class flags non-zero from the start each pilot is already
            // pointed at the head of their own cycle, which is the nearest zone to them.
            var p1First = p1.Current;
            var p2First = p2.Current;
            ctx.Check(p1First is { } a && p2First is { } b && !a.IsSameTarget(b),
                $"each pane auto-acquires ITS OWN zone object: P1 '{Named(p1First)}', P2 '{Named(p2First)}'");

            // One pane's step. The other pilot's marker must not move: a stunt marker is per
            // player, and a unification that made every pane agree would have broken the feature.
            Step(p1, P1Pose);
            ctx.Check(p1.Current is { } stepped && p1First is { } was && !stepped.IsSameTarget(was),
                $"P1's step moves P1's marker: '{Named(p1First)}' to '{Named(p1.Current)}'");
            ctx.Check(p2.Current is { } held && p2First is { } start && held.IsSameTarget(start),
                $"…and leaves P2's marker on '{Named(p2.Current)}'");

            // Walking the whole cycle comes back to where it started, so the one action covers
            // every zone rather than needing a second key for the rest of them.
            for (int i = 1; i < p1Run.TotalCount; i++)
            {
                Step(p1, P1Pose);
            }

            ctx.Check(p1.Current is { } wrapped && p1First is { } head && wrapped.IsSameTarget(head),
                $"P1's step walks all {p1Run.TotalCount} zones and wraps back to '{Named(p1First)}'");

            // A cleared zone leaves the cycle. P2 clears the whole course; P1's is untouched.
            p2Run.DebugCompleteAll();
            Rebuild(p2, p2Run, P2Pose);
            Rebuild(p1, p1Run, P1Pose);
            ctx.Check(p2.Pool.Enemy.Count == 0 && p2.Current == null,
                $"P2's cleared course leaves P2 nothing to point at");
            ctx.Check(p1.Pool.Enemy.Count == p1Run.TotalCount && p1.Current != null,
                $"…and P1 still holds all {p1Run.TotalCount} zones with a marker on '{Named(p1.Current)}'");
            ctx.Note($"{Chapter}/{Mission}: {p1Run.TotalCount} zones on two independent cycles");
        });
    }

    private static bool AllObjectives(IReadOnlyList<TargetRef> cycle)
    {
        foreach (var target in cycle)
        {
            if (!target.Objective || target.Kind != AimTargetKind.Structure)
            {
                return false;
            }
        }

        return cycle.Count > 0;
    }

    private static string Named(TargetRef? target) => target is { } t ? t.DisplayName : "nothing";

    // The marker the selected zone draws, through the same two statics TargetHud draws every
    // objective with: the original's three label lines and Target::GetColor's own answer.
    private static void CheckMarker(TestContext ctx, TargetSelection selection)
    {
        if (selection.Current is not { } target)
        {
            ctx.Check(false, $"the pilot has a zone selected to draw");
            return;
        }

        var lines = new List<string>(3);
        TargetHud.LabelLines(target, "7 o'clock", lines, keepSlots: false);
        ctx.Check(lines.Count == 3 && lines[0] == "Danger Zone [Fly Through] -"
                  && lines[1] == target.DisplayName && lines[2] == "7 o'clock",
            $"the zone's marker is the original's own three lines: [{string.Join(" | ", lines)}]");

        var protect = TargetRef.ForStructure(target.Candidate, TargetClass.Enemy, target.Name,
            "Zeppelin", ProtectLabel, objective: true);
        ctx.Check(TargetHud.MarkerColor(target, AimAssist.PlayerTeam)
                  == TargetHud.MarkerColor(protect, AimAssist.PlayerTeam),
            $"…in the non-destructive objective's colour, the one a {ProtectLabel} marker takes");
    }

    // One press of the Enemy/Objective step, then the re-resolve that publishes it. Separate calls
    // because the original's handler steps the list the last frame built and the next frame's pass
    // is what shows the result; the pose is the rebuild's, so the sort cannot move underneath.
    private static void Step(TargetSelection selection, Vector3 at)
    {
        selection.NextEnemy();
        selection.Resolve(at, Basis.Identity);
    }

    private static TargetSelection Selection(StuntMission run, Vector3 at)
    {
        var selection = new TargetSelection();
        Rebuild(selection, run, at);
        return selection;
    }

    private static void Rebuild(TargetSelection selection, StuntMission run, Vector3 at)
    {
        var zones = new List<AimCandidate>();
        run.CollectTargets(zones);
        selection.Rebuild(new AimCandidateSet(), null, AimAssist.PlayerTeam, null, at,
            Basis.Identity, zones);
    }
}
