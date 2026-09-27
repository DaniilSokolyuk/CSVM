using System.Collections.Generic;
using CSVM.Utils;
using Godot;

namespace CSVM.Mech3;

/// <summary>
/// A clutter stamp draws exactly while its stamping world node is visible in the tree. A kind's
/// placements share one MultiMesh, so a switched-off subtree cannot hide its trees by inheritance.
/// A hidden stamp collapses its basis and loses its solid shape, as a crater's victim does.
/// ⚠ Derive the state from the stamping node's visibility, never from the record's ACTIVE bit. A
/// script can switch a subtree on later, and its trees must come back with it.
/// ⚠ Restore only what this hid, so a stamp a crater destroyed stays destroyed.
/// </summary>
public sealed class ClutterActivation
{
    private readonly IReadOnlyList<ClutterBuilder.KindExport> _exports;

    // Per export, per placement: whether this collapsed it. Parallel to each export's Placements.
    private readonly bool[][] _hidden;

    private ClutterActivation(IReadOnlyList<ClutterBuilder.KindExport> exports)
    {
        _exports = exports;
        _hidden = new bool[exports.Count][];
        for (int k = 0; k < exports.Count; k++)
        {
            _hidden[k] = new bool[exports[k].Placements.Count];
        }
    }

    /// <summary>Bumped on every stamp this hides or shows, so a copy of the stamps elsewhere
    /// (<see cref="MapEdgeExtender"/>'s continuation) re-reads <see cref="IsHidden"/> only after a
    /// change.</summary>
    public int Version { get; private set; }

    /// <summary>Stamps currently hidden because their stamping node is not visible.</summary>
    public int HiddenCount { get; private set; }

    /// <summary>Stamping nodes bound to a built world node, and those the world build never
    /// created, whose stamps are left as they are.</summary>
    public int BoundOwners { get; private set; }

    /// <inheritdoc cref="BoundOwners"/>
    public int UnboundOwners { get; private set; }

    /// <summary>Binds every export's stamps to the built world node their stamping gamez node
    /// became, found under <paramref name="world"/> by <see cref="AnimRuntime.IndexMeta"/>. Syncs
    /// on the node's <c>VisibilityChanged</c>, which Godot propagates to descendants. Also syncs on
    /// <c>TreeEntered</c>: the bootstrap switches a world before it joins the tree, when a
    /// visibility write emits nothing. Null when nothing was exported.</summary>
    public static ClutterActivation? Bind(Node3D world, IReadOnlyList<ClutterBuilder.KindExport>? exports)
    {
        if (exports == null || exports.Count == 0)
        {
            return null;
        }

        var activation = new ClutterActivation(exports);
        var stamps = new Dictionary<int, List<(int Export, int Placement)>>();
        for (int k = 0; k < exports.Count; k++)
        {
            var owners = exports[k].Owners;
            for (int i = 0; i < owners.Count; i++)
            {
                if (!stamps.TryGetValue(owners[i], out var list))
                {
                    stamps[owners[i]] = list = new List<(int, int)>();
                }
                list.Add((k, i));
            }
        }

        var built = new Dictionary<int, Node3D>();
        Collect(world, stamps, built);
        foreach (var (index, list) in stamps)
        {
            if (!built.TryGetValue(index, out var owner))
            {
                activation.UnboundOwners++;
                continue;
            }
            activation.BoundOwners++;
            owner.VisibilityChanged += () => activation.Sync(owner, list);
            owner.TreeEntered += () => activation.Sync(owner, list);
            activation.Sync(owner, list);
        }
        Log.Info("world", $"clutter activation: {activation.BoundOwners} stamping node(s) bound, {activation.UnboundOwners} never built");
        return activation;
    }

    /// <summary>Whether placement <paramref name="placement"/> of export <paramref name="export"/>
    /// is hidden because its stamping node is.</summary>
    public bool IsHidden(int export, int placement) => _hidden[export][placement];

    // The first built node per stamping index, in tree order, which is the claimant the animation
    // runtime's by-index lookup also keeps. By index rather than GetChildren() (PERF-20).
    private static void Collect(Node node, Dictionary<int, List<(int, int)>> wanted, Dictionary<int, Node3D> built)
    {
        if (node is Node3D n3d && n3d.HasMeta(AnimRuntime.IndexMeta))
        {
            int index = n3d.GetMeta(AnimRuntime.IndexMeta).AsInt32();
            if (wanted.ContainsKey(index))
            {
                built.TryAdd(index, n3d);
            }
        }
        for (int i = 0, count = node.GetChildCount(); i < count; i++)
        {
            Collect(node.GetChild(i), wanted, built);
        }
    }

    private static void SetCollider(ClutterBuilder.KindExport export, int i, bool enabled)
    {
        if (export.Colliders is { } colliders && colliders[i] is { } slot)
        {
            PhysicsServer3D.BodySetShapeDisabled(slot.Body, slot.Shape, !enabled);
        }
    }

    private void Sync(Node3D owner, List<(int Export, int Placement)> stamps)
    {
        if (!owner.IsInsideTree())
        {
            return; // resolved for real when the built world joins the tree
        }
        bool shown = owner.IsVisibleInTree();
        foreach (var (k, i) in stamps)
        {
            Apply(k, i, shown);
        }
    }

    private void Apply(int k, int i, bool shown)
    {
        var export = _exports[k];
        if (export.Instances is not { } mm || shown != _hidden[k][i])
        {
            return;
        }
        if (shown)
        {
            mm.SetInstanceTransform(i, export.Placements[i]);
            SetCollider(export, i, enabled: true);
            _hidden[k][i] = false;
            HiddenCount--;
        }
        else
        {
            var placed = mm.GetInstanceTransform(i);
            if (placed.Basis.Determinant() == 0f)
            {
                return; // a crater's victim, which nothing brings back
            }
            mm.SetInstanceTransform(i, new Transform3D(placed.Basis.Scaled(Vector3.Zero), placed.Origin));
            SetCollider(export, i, enabled: false);
            _hidden[k][i] = true;
            HiddenCount++;
        }
        Version++;
    }
}
