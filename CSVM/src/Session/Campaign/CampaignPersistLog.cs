using System.Collections.Generic;
using System.Globalization;
using CSVM.Mech3;
using CSVM.Session.Objectives;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.Campaign;

/// <summary>One persisted world object's carried state: which node it is, what shape it was left
/// in, the def that made it persistent, and which mission left it that way. <see cref="Node"/> is
/// the flat gamez node index (<see cref="AnimRuntime.IndexMeta"/>), which is stable across every
/// build of one chapter's world and unambiguous where names repeat; <see cref="NodeName"/> and
/// <see cref="Def"/> are carried for reading a stored log, not for matching. <see cref="Seq"/> is
/// the capturing mission's story position, which is what keeps a mission's own wreckage out of its
/// own replay; a log written before the field existed carries -1.</summary>
public readonly record struct PersistedObject(
    int Node, string Def, string NodeName, bool Destroyed, float Health, int Seq = -1);

/// <summary>
/// The cross-mission state log (`BL-243`): what a campaign mission left destroyed, carried into
/// later missions of the SAME chapter, keyed by the story position that captured it
/// (docs/formats/saved-games.md, "<c>Mission.NNN</c>").
/// ⚠ A mission opens only on positions BEFORE its own. A chapter-wide fold with no such cut
/// re-applies a mission's own wreckage on a replay, which opens the chapter's first mission on the
/// world its previous sortie left.
/// Only <see cref="AnimDefinition.PersistLog"/> defs are carried, and a node counts as persisted
/// when ANY def bound to it carries the flag, the reader's wildcard twin or the compiled def
/// <c>AnimProgram</c> handed the reader's flag to. What the log holds is destruction, not the full
/// <c>Mission.NNN</c> world state, so a running looping animation is out of scope.
/// </summary>
public sealed class CampaignPersistLog
{
    // Chapter (the cm_sequence 'campaign' world-folder number) -> capturing story position ->
    // node index -> its state. The middle key is what the backwards walk cuts on.
    private readonly Dictionary<int, Dictionary<int, Dictionary<int, PersistedObject>>> _byChapter = new();

    /// <summary>Every chapter this log holds state for.</summary>
    public IReadOnlyCollection<int> Chapters => _byChapter.Keys;

    /// <summary>How many object states are held across all chapters.</summary>
    public int Count
    {
        get
        {
            int total = 0;
            foreach (int chapter in _byChapter.Keys)
            {
                total += For(chapter).Count;
            }

            return total;
        }
    }

    /// <summary>Reads the persisted-object states out of a live world: every node a
    /// <c>PERSIST_LOG</c> def binds that is no longer intact, reported from the pool the hit path
    /// actually damages (<see cref="DestructibleRegistry.Resolve"/>). A node with no gamez index is
    /// skipped, since nothing could key it back to the same object in a later build.</summary>
    public static List<PersistedObject> Capture(AnimRuntime runtime)
    {
        var registry = runtime.Destructibles;
        var seen = new HashSet<ulong>();
        var states = new List<PersistedObject>();
        foreach (var inst in registry.All)
        {
            if (!inst.Def.PersistLog || !seen.Add(inst.Anchor.GetInstanceId()))
            {
                continue;
            }

            int index = NodeIndex(inst.Anchor);
            var live = registry.Resolve(inst.Anchor) ?? inst;
            if (index < 0 || (live.Status == DestructibleRegistry.State.Healthy
                && live.Health >= live.MaxHealth))
            {
                continue;
            }

            states.Add(new PersistedObject(index, inst.Def.Name, inst.Anchor.Name,
                live.Status == DestructibleRegistry.State.Destroyed, live.Health));
        }

        return states;
    }

    /// <summary>Whether a mission that ended this way commits its capture to the log. The original
    /// writes the world-state carrier once, in the mission-end pass, and only for a won mission
    /// (docs/formats/saved-games.md). ⚠ Never commit a loss: <see cref="Merge"/> only ever
    /// escalates damage, so a failed attempt's wreckage could not be taken back on the retry.</summary>
    public static bool CommitsOn(MissionOutcome outcome) => outcome == MissionOutcome.Won;

    /// <summary>Every state held for one chapter, whichever mission captured it, escalated across
    /// the chapter's story positions. This is the whole stored log, which is what a profile writes
    /// and what a reader counts; <see cref="Through"/> is what a mission opens on.</summary>
    public IReadOnlyList<PersistedObject> For(int chapter) => Fold(chapter, null);

    /// <summary>The chapter's state as of <paramref name="throughSeq"/>, the position of the most
    /// recent EARLIER mission of this chapter (<see cref="CampaignSequence.PreviousInSameChapter"/>).
    /// Null is that chapter's first mission, whose backwards walk finds no file at all. A state
    /// stored without a position (an older log) counts as that earlier mission's, since some
    /// earlier mission is what recorded it.</summary>
    public IReadOnlyList<PersistedObject> Through(int chapter, int? throughSeq) =>
        throughSeq is { } seq ? Fold(chapter, seq) : new List<PersistedObject>();

    /// <summary>Folds a mission's captured states into the chapter's log under
    /// <paramref name="seq"/>, its own story position. Damage only ever escalates: a state already
    /// destroyed stays destroyed, and a repeat of the same node keeps the lower health. ⚠ Never let
    /// a fresh capture heal a node, or replaying a mission would undo what an earlier one left
    /// wrecked, which is the one thing the original's backwards walk cannot do.</summary>
    public void Merge(int chapter, int seq, IEnumerable<PersistedObject> states)
    {
        if (!_byChapter.TryGetValue(chapter, out var bySeq))
        {
            _byChapter[chapter] = bySeq = new Dictionary<int, Dictionary<int, PersistedObject>>();
        }

        if (!bySeq.TryGetValue(seq, out var byNode))
        {
            bySeq[seq] = byNode = new Dictionary<int, PersistedObject>();
        }

        foreach (var raw in states)
        {
            var state = raw with { Seq = seq };
            if (!byNode.TryGetValue(state.Node, out var held))
            {
                byNode[state.Node] = state;
                continue;
            }

            if (held.Destroyed || (!state.Destroyed && state.Health >= held.Health))
            {
                continue;
            }

            byNode[state.Node] = state;
        }
    }

    /// <summary>Applies <see cref="Through"/>'s states to a freshly built world of that chapter,
    /// after its bootstrap: each is put into the state it was left in, silently, through
    /// <see cref="AnimRuntime.CarryState"/>. The original opens a later mission on the destroyed
    /// pose, not on a replayed death, so no effect, sound or choreography runs here; a later hit
    /// finds the pool destroyed and is a no-op. Returns how many objects it acted on; objects the
    /// world does not carry are skipped, as with scenery a mission never places.</summary>
    public int ApplyTo(AnimRuntime runtime, int chapter, int? throughSeq)
    {
        var registry = runtime.Destructibles;
        var byIndex = new Dictionary<int, Node3D>();
        foreach (var inst in registry.All)
        {
            int index = NodeIndex(inst.Anchor);
            if (index >= 0)
            {
                byIndex[index] = inst.Anchor;
            }
        }

        var carried = Through(chapter, throughSeq);
        int applied = 0;
        foreach (var state in carried)
        {
            if (!byIndex.TryGetValue(state.Node, out var anchor)
                || registry.Resolve(anchor) is not { } live)
            {
                continue;
            }

            // ⚠ Never DamageAt here; it replays the death choreography at mission open.
            if (runtime.CarryState(live, state.Destroyed, state.Health))
            {
                applied++;
            }
        }

        string through = throughSeq is { } s
            ? s.ToString(CultureInfo.InvariantCulture)
            : "none, this chapter's first mission";
        Log.Info("core", $"persist log: {applied} of {carried.Count} carried object(s) restored silently in chapter {chapter} (through seq {through}, {For(chapter).Count} held)");
        return applied;
    }

    /// <summary>Replaces this log's whole contents, for a profile load. Each state keeps the story
    /// position it was stored under.</summary>
    public void Reset(IEnumerable<KeyValuePair<int, IReadOnlyList<PersistedObject>>> chapters)
    {
        _byChapter.Clear();
        foreach (var (chapter, states) in chapters)
        {
            foreach (var state in states)
            {
                Merge(chapter, state.Seq, new[] { state });
            }
        }
    }

    private static int NodeIndex(Node3D node) =>
        node.HasMeta(AnimRuntime.IndexMeta) ? (int)node.GetMeta(AnimRuntime.IndexMeta) : -1;

    // The chapter's buckets folded into one state per node, escalating the same way Merge does,
    // taking every position at or before `throughSeq` (all of them when it is null).
    private List<PersistedObject> Fold(int chapter, int? throughSeq)
    {
        var byNode = new Dictionary<int, PersistedObject>();
        var order = new List<int>();
        if (_byChapter.TryGetValue(chapter, out var bySeq))
        {
            foreach (var (seq, states) in bySeq)
            {
                // An unrecorded position (-1) is an older log's, so it belongs to whatever earlier
                // mission wrote it: carried whenever any earlier mission exists at all.
                if (throughSeq is { } cut && seq > cut)
                {
                    continue;
                }

                foreach (var state in states.Values)
                {
                    if (!byNode.TryGetValue(state.Node, out var held))
                    {
                        order.Add(state.Node);
                        byNode[state.Node] = state;
                        continue;
                    }

                    if (held.Destroyed || (!state.Destroyed && state.Health >= held.Health))
                    {
                        continue;
                    }

                    byNode[state.Node] = state;
                }
            }
        }

        var folded = new List<PersistedObject>(order.Count);
        foreach (int node in order)
        {
            folded.Add(byNode[node]);
        }

        return folded;
    }
}
