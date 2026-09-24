using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CSVM.Mech3;
using Godot;

namespace CSVM.Testing;

/// <summary>A clutter stamp draws exactly while the world node it was stamped from is visible.
/// CM01 (C3/M01) switches a whole island off by the mission script's area verb. Every stamp under
/// a hidden node must be collapsed and every other stamp must stand. The toggle is then driven both
/// ways on an island node and a visible node. A cratered stamp stays dead through it, and the
/// map-edge copies follow their source stamps.</summary>
internal static class ClutterActivationSuites
{
    private const string Chapter = "C3";
    private const string Mission = "M01";
    private const string EdgeChapter = "C1";

    [Suite("clutter-activation",
        "CM01's area-deactivated island leaves no trees on the water: every clutter stamp whose " +
        "stamping node is hidden is collapsed and every other stamp stands; switching the island's " +
        "node back on restores its stamps exactly and switching it off hides them again; a visible " +
        "node switched off and on does the same; a stamp a crater destroyed stays destroyed through " +
        "a hide and a show; and the map-edge copies of a border stamp hide and show with it")]
    internal static void ClutterActivation(TestContext ctx)
    {
        var report = new StringBuilder();
        ctx.WithWorld(Chapter, collision: false, Mission, world => Drive(ctx, world, report));
        // C3's stamps stand nowhere near its map edge, so the continuation is read over C1's forest.
        ctx.WithWorld(EdgeChapter, collision: false, world => MapEdgeCopies(ctx, world, report));
        ctx.WriteArtifact("test-clutter-activation.txt", report.ToString());
    }

    private static void Drive(TestContext ctx, TestWorld world, StringBuilder report)
    {
        var clutter = world.Session.Clutter;
        var exports = clutter?.ExportedKinds;
        var activation = clutter?.Activation;
        ctx.Check(exports != null && activation != null, $"{Chapter} builds clutter and binds its activation");
        if (exports == null || activation == null)
        {
            return;
        }

        var runtime = world.Session.Runtime;
        report.AppendLine(CultureInfo.InvariantCulture,
            $"bound owners {activation.BoundOwners}, never built {activation.UnboundOwners}, hidden stamps {activation.HiddenCount}");

        // The island: stamps whose stamping node the mission script left invisible.
        var (hiddenOwners, shownOwners) = Owners(exports, runtime);
        int islandStamps = CountStamps(exports, hiddenOwners);
        report.AppendLine(CultureInfo.InvariantCulture,
            $"stamping nodes hidden by the mission: {hiddenOwners.Count} ({islandStamps} stamps), visible: {shownOwners.Count}");
        ctx.Check(hiddenOwners.Count > 0, $"CM01's area verb hides stamping nodes hidden={hiddenOwners.Count}");
        ctx.Same(islandStamps, activation.HiddenCount, $"the activation hides exactly the hidden nodes' stamps");
        CheckAgreement(ctx, exports, runtime, "at mission start", report);

        // Both directions on one island node, through the switch that actually hid it.
        if (hiddenOwners.Count > 0 && runtime.FindNodeByIndex(hiddenOwners[0]) is { } islandNode
            && SwitchedOff(islandNode) is { } islandSwitch)
        {
            AnimRuntime.SetSubtreeActive(islandSwitch, true);
            ctx.Check(activation.HiddenCount < islandStamps,
                $"switching '{islandSwitch.Name}' on shows its stamps hidden={activation.HiddenCount} of {islandStamps}");
            CheckAgreement(ctx, exports, runtime, $"after '{islandSwitch.Name}' on", report);
            AnimRuntime.SetSubtreeActive(islandSwitch, false);
            ctx.Same(islandStamps, activation.HiddenCount, $"switching '{islandSwitch.Name}' off again hides them");
            CheckAgreement(ctx, exports, runtime, $"after '{islandSwitch.Name}' off", report);
        }

        if (shownOwners.Count > 0 && runtime.FindNodeByIndex(shownOwners[0]) is { } shownNode)
        {
            var root = world.Session.Root.GetNodeOrNull<Node3D>("clutter");
            VisibleNode(ctx, exports, activation, runtime, root, shownNode, shownOwners[0], islandStamps, report);
        }
    }

    // A visible node switched off and on, then the same with one of its stamps cratered first.
    private static void VisibleNode(TestContext ctx, IReadOnlyList<ClutterBuilder.KindExport> exports,
        ClutterActivation activation, AnimRuntime runtime, Node3D? clutterRoot, Node3D node, int owner,
        int islandStamps, StringBuilder report)
    {
        int own = CountStamps(exports, new List<int> { owner });
        AnimRuntime.SetSubtreeActive(node, false);
        ctx.Same(islandStamps + own, activation.HiddenCount, $"switching '{node.Name}' off hides its {own} stamps");
        AnimRuntime.SetSubtreeActive(node, true);
        ctx.Same(islandStamps, activation.HiddenCount, $"switching '{node.Name}' back on shows them");
        CheckAgreement(ctx, exports, runtime, $"after '{node.Name}' off and on", report);

        // ⚠ Restore the victim afterwards: a cached world must not carry a crater into a later suite.
        var (k, i) = FirstStamp(exports, owner);
        var mm = exports[k].Instances!;
        var at = exports[k].Placements[i].Origin;
        var shape = CraterShape.At(at, radius: 0.01f);
        int killed = ClutterCull.Destroy(clutterRoot, shape);
        AnimRuntime.SetSubtreeActive(node, false);
        AnimRuntime.SetSubtreeActive(node, true);
        bool dead = mm.GetInstanceTransform(i).Basis.Determinant() == 0f;
        ctx.Check(killed > 0 && dead, $"a stamp a crater destroyed stays destroyed through a hide and a show killed={killed} dead={dead}");
        report.AppendLine(CultureInfo.InvariantCulture, $"crater at ({at.X:0.0}, {at.Z:0.0}): killed {killed}, dead after the toggle {dead}");
        for (int e = 0; e < exports.Count; e++)
        {
            var copy = exports[e].Instances!;
            for (int j = 0; j < copy.InstanceCount; j++)
            {
                if (copy.GetInstanceTransform(j).Basis.Determinant() == 0f && !activation.IsHidden(e, j))
                {
                    copy.SetInstanceTransform(j, exports[e].Placements[j]);
                }
            }
        }
    }

    // A border stamp's copies past the map edge: built with it switched off they draw nothing,
    // and they come back when it does.
    private static void MapEdgeCopies(TestContext ctx, TestWorld world, StringBuilder report)
    {
        var clutter = world.Session.Clutter;
        var gridNode = world.Gamez.FindByName("world1");
        if (clutter?.ExportedKinds is not { } exports || gridNode == null
            || world.Session.Builder.CreateEdgeExtender(clutter) is not { } ext)
        {
            ctx.Check(false, $"{EdgeChapter} builds clutter and a map-edge continuation");
            return;
        }
        world.Stage.AddChild(ext);
        var nodes = new List<Node3D>();
        try
        {
            // Every visible stamping node goes off, so whichever stamps the window copies follow.
            var (_, shownOwners) = Owners(exports, world.Runtime);
            foreach (int owner in shownOwners)
            {
                if (world.Runtime.FindNodeByIndex(owner) is { } n)
                {
                    nodes.Add(n);
                    AnimRuntime.SetSubtreeActive(n, false);
                }
            }

            // One focus past the middle of each edge, so every side's border cells are copied.
            float tileX = (gridNode.AreaRight - gridNode.AreaLeft) / gridNode.PartitionCols;
            float tileZ = (gridNode.AreaBottom - gridNode.AreaTop) / gridNode.PartitionRows;
            float midX = (gridNode.AreaLeft + gridNode.AreaRight) / 2f;
            float midZ = (gridNode.AreaTop + gridNode.AreaBottom) / 2f;
            var focus = new[]
            {
                new Vector3(gridNode.AreaLeft - (1.5f * tileX), 0f, midZ),
                new Vector3(gridNode.AreaRight + (1.5f * tileX), 0f, midZ),
                new Vector3(midX, 0f, gridNode.AreaTop - (1.5f * tileZ)),
                new Vector3(midX, 0f, gridNode.AreaBottom + (1.5f * tileZ)),
            };
            ext.Update(focus);
            var (copies, hidden) = ext.ClutterCopyCensus();
            report.AppendLine(CultureInfo.InvariantCulture, $"map edge, every stamping node off: {copies} copies, {hidden} hidden");
            ctx.Check(copies > 0, $"the continuation copies stamps past the map edge copies={copies}");
            ctx.Same(copies, hidden, $"every copy of a hidden stamp draws nothing");

            foreach (var n in nodes)
            {
                AnimRuntime.SetSubtreeActive(n, true);
            }
            nodes.Clear();
            ext.Update(focus);
            var (_, shownHidden) = ext.ClutterCopyCensus();
            report.AppendLine(CultureInfo.InvariantCulture, $"map edge, switched back on: {shownHidden} hidden");
            ctx.Same(0, shownHidden, $"the copies come back with their source stamps");
        }
        finally
        {
            foreach (var n in nodes)
            {
                AnimRuntime.SetSubtreeActive(n, true);
            }
            ext.QueueFree();
        }
    }

    // Every stamp's drawn state agrees with its stamping node's visibility, read off the MultiMesh.
    private static void CheckAgreement(TestContext ctx, IReadOnlyList<ClutterBuilder.KindExport> exports,
        AnimRuntime runtime, string when, StringBuilder report)
    {
        int wrong = 0, drawn = 0, collapsed = 0;
        string? example = null;
        for (int k = 0; k < exports.Count; k++)
        {
            var export = exports[k];
            for (int i = 0; i < export.Owners.Count; i++)
            {
                bool visible = runtime.FindNodeByIndex(export.Owners[i]) is not { } n || n.IsVisibleInTree();
                bool draws = export.Instances!.GetInstanceTransform(i).Basis.Determinant() != 0f;
                if (draws)
                {
                    drawn++;
                }
                else
                {
                    collapsed++;
                }
                if (draws != visible)
                {
                    wrong++;
                    example ??= $"{export.Texture} #{i} of node {export.Owners[i]} at {export.Placements[i].Origin}";
                }
            }
        }
        report.AppendLine(CultureInfo.InvariantCulture, $"{when}: {drawn} drawn, {collapsed} collapsed, {wrong} disagreeing{(example != null ? $" (e.g. {example})" : "")}");
        ctx.Same(0, wrong, $"{when}: every stamp draws exactly while its stamping node is visible");
    }

    private static (List<int> Hidden, List<int> Shown) Owners(IReadOnlyList<ClutterBuilder.KindExport> exports,
        AnimRuntime runtime)
    {
        var seen = new HashSet<int>();
        var hidden = new List<int>();
        var shown = new List<int>();
        foreach (var export in exports)
        {
            foreach (int owner in export.Owners)
            {
                if (!seen.Add(owner) || runtime.FindNodeByIndex(owner) is not { } n)
                {
                    continue;
                }
                (n.IsVisibleInTree() ? shown : hidden).Add(owner);
            }
        }
        return (hidden, shown);
    }

    private static int CountStamps(IReadOnlyList<ClutterBuilder.KindExport> exports, List<int> owners)
    {
        var set = new HashSet<int>(owners);
        int count = 0;
        foreach (var export in exports)
        {
            foreach (int owner in export.Owners)
            {
                if (set.Contains(owner))
                {
                    count++;
                }
            }
        }
        return count;
    }

    private static (int Export, int Placement) FirstStamp(IReadOnlyList<ClutterBuilder.KindExport> exports, int owner)
    {
        for (int k = 0; k < exports.Count; k++)
        {
            for (int i = 0; i < exports[k].Owners.Count; i++)
            {
                if (exports[k].Owners[i] == owner)
                {
                    return (k, i);
                }
            }
        }
        return (0, 0);
    }

    // The nearest node at or above this one that is itself switched off: the one a script hid.
    private static Node3D? SwitchedOff(Node3D node)
    {
        for (var n = node; n != null; n = n.GetParent() as Node3D)
        {
            if (!n.Visible)
            {
                return n;
            }
        }
        return null;
    }
}
