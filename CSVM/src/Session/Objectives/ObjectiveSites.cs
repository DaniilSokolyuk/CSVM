using System;
using System.Collections.Generic;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using Godot;

namespace CSVM.Session.Objectives;

/// <summary>Which of a target record's two flags a collection pass reads: <c>objective</c>
/// (entity <c>+0x4d</c>), whose sites ride the Enemy cycle, or <c>other_target</c>
/// (<c>+0x4c</c>), whose sites ride the Non-Aircraft cycle.</summary>
public enum TargetFlag
{
    /// <summary>The <c>objective</c> flag: a companion on the Enemy cycle, so the ordinary
    /// selection draws one flagged site at a time.</summary>
    Objective,

    /// <summary>The <c>other_target</c> flag: the curated structures a mission puts on the
    /// Non-Aircraft cycle.</summary>
    OtherTarget,
}

/// <summary>
/// The flown mission's flagged target sites, offered to each player's target pool: the set is
/// <c>targets.zrd</c>'s own flagged entries, edited by <c>objectives.zrd</c>'s
/// <c>ADD_/REMOVE_OBJECTIVE_TARGET</c> and <c>ADD_/REMOVE_OTHER_TARGET</c> where a director runs
/// one, with the labels resolved through <see cref="Messages"/> and <c>SET_HELP_LABEL</c>. The
/// file's two flags pick the cycle: an <c>objective</c> entry is a companion flag on the Enemy
/// cycle, so the ordinary selection draws one site at a time, and an <c>other_target</c> entry puts
/// a structure on the Non-Aircraft cycle. Bound by <c>GameSession</c>; <see cref="Collect"/> runs
/// once per pane per frame and re-reads each site's position, so a site on a moving node is marked
/// where it is. ⚠ These are world SITES only. A roster block that flags itself (aiv slot 37) is
/// offered by <see cref="TargetPool"/> on its own aircraft's candidate instead, so nothing here
/// needs to know about an aeroplane.</summary>
public sealed class ObjectiveSites
{
    // One row per flag: the record's own flag, the graph store that flag's ADD_ directive fills,
    // and the removal list a completed objective drops such a key through. The two classes differ
    // in nothing else, so one pass serves both.
    private static readonly Dictionary<TargetFlag, FlagSource> Sources = new()
    {
        [TargetFlag.Objective] = new(t => t.Objective, g => g.ObjectiveTargets,
            d => d.RemoveObjectiveTarget),
        [TargetFlag.OtherTarget] = new(t => t.OtherTarget, g => g.OtherTargets,
            d => d.RemoveOtherTarget),
    };

    private readonly CampaignDirector? _director;
    private readonly Messages _messages;
    private readonly MissionTargets _targets;
    private readonly AnimRuntime? _runtime;
    private readonly Dictionary<string, ObjectiveSite> _sites =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Node3D> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _live = new();

    /// <summary>Reads a campaign director, the mission's target table and the world's node index.
    /// The director's graph may not exist yet; <see cref="Collect"/> polls for it and offers
    /// nothing until it does.</summary>
    public ObjectiveSites(CampaignDirector director, Messages messages, MissionTargets targets,
        AnimRuntime? runtime)
    {
        _director = director;
        _messages = messages;
        _targets = targets;
        _runtime = runtime;
    }

    /// <summary>Reads a mission's target table with no director behind it: the feed Instant Action
    /// and the multiplayer modes take. Those modes ship an <c>objectives.zrd</c> that carries the
    /// mission preamble and no target directive at all, so the table's own <c>objective</c> and
    /// <c>other_target</c> keys are the whole curated list and nothing edits it while the session
    /// runs.</summary>
    public ObjectiveSites(Messages messages, MissionTargets targets, AnimRuntime? runtime)
    {
        _messages = messages;
        _targets = targets;
        _runtime = runtime;
    }

    /// <summary>The target keys carrying one flag: <c>targets.zrd</c>'s own entries carrying it,
    /// less those a completed objective's <c>REMOVE_</c> directive names, plus everything the
    /// matching <c>ADD_</c> has added. A null script and graph are the director-free modes, the
    /// table alone. ⚠ Append both classes to ONE list, the objectives first: a key carrying both
    /// flags is an objective, and this pass skips what the earlier one took. ⚠ Do not build a
    /// class from <see cref="ObjectiveGraph.ObjectiveTargets"/> alone: that store starts empty.</summary>
    public static void CollectFlagged(TargetFlag flag, ObjectiveScript? script,
        ObjectiveGraph? graph, MissionTargets targets, List<string> into)
    {
        var source = Sources[flag];
        foreach (var entry in targets.ByNode)
        {
            if (source.Flagged(entry.Value) && !RemovedByCompletion(script, graph, entry.Key, source)
                && !Listed(into, entry.Key))
            {
                into.Add(entry.Key);
            }
        }

        if (graph == null)
        {
            return;
        }

        foreach (var key in source.Added(graph))
        {
            if (!Listed(into, key))
            {
                into.Add(key);
            }
        }
    }

    /// <summary>Whether a site standing on this destructible state is still selectable: false once
    /// the resolved node's own pool reads <see cref="DestructibleRegistry.State.Destroyed"/>, no
    /// matter whether its objective has completed, the same read <c>ZeppelinRuntime.ZoneIsAlive</c>
    /// makes for a gasbag or engine zone. Null (unresolved node, or no registered pool) is live: a
    /// site that never was a destructible, or one not built yet, is not a dead one.</summary>
    public static bool LiveDespiteState(DestructibleRegistry.State? state) =>
        state != DestructibleRegistry.State.Destroyed;

    /// <summary>Where a target sits: the bare <c>TRAVELERS</c> point of the objective that edits
    /// that target, where the mission gives one, and null to fall back to the world node.
    /// ⚠ Prefer the point over the node. C3/M01's village target names a node standing at the world
    /// origin, 7.9 km from the point its own objective tests, so the node is not the site.</summary>
    public static Vector3? PointFor(ObjectiveScript? script, string key)
    {
        if (script == null)
        {
            return null;
        }

        foreach (var def in script.Objectives)
        {
            if (def.Travelers is { WherePoint: { } p } && Edits(def, key))
            {
                return new Vector3(p[0], p[1], p[2]);
            }
        }

        return null;
    }

    /// <summary>The one world node a target stands on: a bare name is the first global match,
    /// and a path is walked one name at a time, each found inside the node before it, so
    /// <c>piratezep/rock_zeppelin</c> is the hull's own child and never a ground node of the
    /// same name. Null when any step resolves to nothing.</summary>
    public static Node3D? ResolveTarget(AnimRuntime runtime, ObjectiveTarget target)
    {
        Node3D? node = null;
        foreach (var name in target.Path)
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

    /// <summary>Where a resolved site node's marker stands: the centre of the world bounding box
    /// of everything the node draws, which is what the original publishes for a mission structure
    /// (<c>docs/org/targeting.md</c>). Its own position is the fallback for a node that draws
    /// nothing at all. ⚠ Do not mark a site at the node's position. C1/M05's balloon groups stand
    /// on the water with the balloon 16 m above them, and C2's seaplane hangar
    /// (<c>sghangar</c>) stands at the world origin 8 km from its own body.</summary>
    public static Vector3 SiteAnchor(Node3D node)
    {
        Aabb? merged = null;
        CollectMeshBoxes(node, ref merged);
        return merged?.GetCenter() ?? node.GlobalPosition;
    }

    /// <summary>Appends this frame's live sites, each carrying the flag its own record authors, so
    /// the pool files an <c>objective</c> one on the Enemy cycle and an <c>other_target</c> one on
    /// the Non-Aircraft cycle. Every site is rebuilt from its live source, so a site under a moving
    /// node moves with it and a completed site is simply not offered again.</summary>
    public void Collect(List<AimCandidate> into)
    {
        var graph = _director?.Graph;
        // A campaign session offers nothing until its graph exists, since a directive may already
        // have edited the set; a director-free mode has no such wait.
        if (_director != null && graph == null)
        {
            return;
        }

        _live.Clear();
        CollectFlagged(TargetFlag.Objective, _director?.Script, graph, _targets, _live);
        int objectives = _live.Count;
        CollectFlagged(TargetFlag.OtherTarget, _director?.Script, graph, _targets, _live);
        for (int i = 0; i < _live.Count; i++)
        {
            Offer(_live[i], graph, objective: i < objectives, into);
        }
    }

    private static bool RemovedByCompletion(ObjectiveScript? script, ObjectiveGraph? graph,
        string key, FlagSource source)
    {
        if (script == null || graph == null)
        {
            return false;
        }

        foreach (var def in script.Objectives)
        {
            if (graph.CompletedOf(def.Number) && Holds(source.Removed(def), key))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Listed(List<string> keys, string key)
    {
        foreach (var listed in keys)
        {
            if (string.Equals(listed, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Every list an objective can name a site in, both flags' ADD_ and REMOVE_. A record's flag
    // picks the cycle its site rides, never where the site stands, so the objective's own point is
    // the answer for an other-target key exactly as it is for an objective one.
    private static bool Edits(ObjectiveDef def, string key) =>
        Holds(def.AddObjectiveTarget, key) || Holds(def.RemoveObjectiveTarget, key)
        || Holds(def.AddOtherTarget, key) || Holds(def.RemoveOtherTarget, key);

    private static bool Holds(IReadOnlyList<ObjectiveTarget> targets, string key)
    {
        foreach (var target in targets)
        {
            if (target.Is(key))
            {
                return true;
            }
        }

        return false;
    }

    // The world-frame box of everything `node` and its subtree draw, hidden parts included: the
    // original's own bounding box is the authored one over every child, and a wave that has not
    // been switched on yet still has to be marked where it stands.
    // ⚠ Walk by index rather than GetChildren(). This runs once per site per pane per frame, and
    // the Godot array GetChildren() allocates costs 1.4 ms of the 1.9 ms a 380-mesh zeppelin site
    // took before the change.
    private static void CollectMeshBoxes(Node node, ref Aabb? merged)
    {
        if (node is MeshInstance3D { Mesh: not null } mesh)
        {
            var box = mesh.GlobalTransform * mesh.GetAabb();
            merged = merged?.Merge(box) ?? box;
        }

        int children = node.GetChildCount();
        for (int i = 0; i < children; i++)
        {
            if (node.GetChild(i) is Node3D child)
            {
                CollectMeshBoxes(child, ref merged);
            }
        }
    }

    private void Offer(string node, ObjectiveGraph? graph, bool objective, List<AimCandidate> into)
    {
        if (Where(node) is not { } at)
        {
            return;
        }

        var resolved = Resolve(node);
        into.Add(new AimCandidate
        {
            Position = at,
            // A record naming a mission-structure node stamps its flags onto the object that
            // node already built and keeps its team; one naming any other node builds its own,
            // and the original builds those neutral, which is almost every site.
            Team = (resolved is { } site
                ? DestructibleRegistry.MissionStructureTeamOf(site)
                : null) ?? AimAssist.NeutralTeam,
            Live = LiveDespiteState(resolved is { } n ? _runtime?.Destructibles.Resolve(n)?.Status : null),
            ConeOverride = AimAssist.NoConeOverride,
            Source = SiteFor(node, graph, at, objective),
        });
    }

    // A message key resolves to itself when unknown, which is right for a readout and wrong for a
    // marker; a whitespace-only value is how a mission clears a label, so both read as absent.
    private string Text(string? key) =>
        string.IsNullOrWhiteSpace(key) ? "" : _messages.Get(key).Trim();

    private Vector3? Where(string key)
    {
        if (PointFor(_director?.Script, key) is { } point)
        {
            return point;
        }

        return Resolve(key) is { } node ? SiteAnchor(node) : null;
    }

    // Cached: FindNodes walks the world index by name and this runs once per pane per frame.
    // ⚠ Re-resolve an invalid or detached node rather than caching the miss. `piratezep` is
    // switched off and moved while the world is built, and a dock point sits under it.
    private Node3D? Resolve(string key)
    {
        if (_nodes.TryGetValue(key, out var cached)
            && GodotObject.IsInstanceValid(cached) && cached.IsInsideTree())
        {
            return cached;
        }

        if (_runtime == null)
        {
            return null;
        }

        var found = ResolveTarget(_runtime, ObjectiveTarget.Parse(key));
        if (found == null || !found.IsInsideTree())
        {
            return null;
        }

        _nodes[key] = found;
        return found;
    }

    // The strings are re-read every frame because SET_HELP_LABEL rewrites a live site's category;
    // the instance itself survives that, since it is the identity the selection is held by.
    private ObjectiveSite SiteFor(string key, ObjectiveGraph? graph, Vector3 at, bool objective)
    {
        if (!_sites.TryGetValue(key, out var site))
        {
            site = new ObjectiveSite { Node = key, Target = ObjectiveTarget.Parse(key) };
            _sites[key] = site;
        }

        // targets.zrd keys a path-authored entry by the same parent/child key the script uses,
        // and a bare one by the node; a path added by the script over a bare entry reads the node.
        var info = _targets.For(key);
        if (info == default)
        {
            info = _targets.For(site.Target.Node);
        }

        string name = Text(info.Description);
        string typeLabel = Text(info.CategoryLabel);
        // The script's own SET_HELP_LABEL outranks targets.zrd's authored label.
        string category = Text(graph != null && graph.HelpLabels.TryGetValue(key, out var written)
            ? written : info.HelpLabel);
        site.DisplayName = name.Length > 0 ? name : site.Target.Node;
        site.TypeLabel = typeLabel.Length > 0 ? typeLabel : null;
        site.Category = category.Length > 0 ? category : null;
        site.Position = at;
        site.Objective = objective;
        return site;
    }

    // What a collection pass reads for the flag it was handed, one row of `Sources`.
    private readonly record struct FlagSource(
        Func<MissionTarget, bool> Flagged,
        Func<ObjectiveGraph, IReadOnlyCollection<string>> Added,
        Func<ObjectiveDef, IReadOnlyList<ObjectiveTarget>> Removed);
}
