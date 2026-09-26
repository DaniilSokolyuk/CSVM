using System.Collections.Generic;
using System.Linq;
using CSVM.Tooling;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// The node lab's export set: the nodes gathered with Ctrl+click or the panel's ± set, written as
/// one glTF at their world transforms. It is separate from the single selection every other
/// inspect tool reads, because a member is usually the current selection too. Each member is
/// outlined in cyan while it stays in the set, and nothing is built until the first one joins.
/// Full behaviour: this module's entry in docs/architecture.md.
/// </summary>
public sealed partial class ExportSet : Node
{
    // The set's outline: distinct from the selection's amber box, since a set member is usually
    // also the current selection.
    private static readonly Color SetCyan = new(0.35f, 0.85f, 1f);

    private readonly SelectionService _selection;
    private readonly List<Node3D> _members = new();
    private readonly List<MeshInstance3D> _boxes = new();

    public ExportSet(SelectionService selection)
    {
        _selection = selection;
        Name = "export_set";
    }

    /// <summary>The members, in the order they joined. A member freed under the set leaves it, so
    /// what a caller reads here is live rather than a list to be revalidated.</summary>
    public IReadOnlyList<Node3D> Members => _members;

    public override void _Ready()
    {
        _selection.CtrlPicked += OnCtrlPicked;
        // The count rides the selection's breadcrumb rather than a HUD of this module's own: the
        // set is gathered by Ctrl+click, which needs neither the panel nor its key.
        _selection.HudLine = CountLine;
    }

    public override void _ExitTree()
    {
        _selection.CtrlPicked -= OnCtrlPicked;
        _selection.HudLine = null;
    }

    public override void _Process(double delta)
    {
        for (int i = _members.Count - 1; i >= 0; i--)
        {
            if (IsInstanceValid(_members[i]))
            {
                _boxes[i].GlobalTransform = _members[i].GlobalTransform;
            }
            else
            {
                // A member freed under us (a destructible swapping to its wreck) leaves the set.
                RemoveAt(i);
            }
        }
    }

    /// <summary>Adds a node to the set, or removes it if it is already there. Returns true when the
    /// node is in the set afterwards.</summary>
    public bool Toggle(Node3D node)
    {
        int at = _members.IndexOf(node);
        if (at >= 0)
        {
            RemoveAt(at);
        }
        else
        {
            var box = SelectionService.NewBoxInstance("set_box", SetCyan, 19);
            AddChild(box);
            SelectionService.DrawBox(box, node, SelectionService.SubtreeWorldAabb(node));
            _members.Add(node);
            _boxes.Add(box);
        }
        Log.Info("ui", $"select set {(at >= 0 ? "remove" : "add")} cs_name={SelectionService.NameOf(node)} count={_members.Count}");
        _selection.RefreshHud();
        return at < 0;
    }

    /// <summary>Empties the set and removes its outlines.</summary>
    public void Clear()
    {
        for (int i = _members.Count - 1; i >= 0; i--)
        {
            RemoveAt(i);
        }
        Log.Info("ui", $"select set cleared");
        _selection.RefreshHud();
    }

    /// <summary>Writes every member into one timestamped GLB in <c>Exports/</c>, each at its world
    /// transform.</summary>
    public void WriteGltf()
    {
        var set = _members.Where(IsInstanceValid).ToList();
        if (set.Count == 0)
        {
            Log.Info("ui", $"nodelab export set, the set is empty (Ctrl+click objects, or ± set on a selection)");
            return;
        }
        Log.Info("ui", $"nodelab export set count={set.Count} nodes=[{string.Join(" ", set.Select(SelectionService.NameOf))}]");
        string name = set.Count == 1
            ? SelectionService.NameOf(set[0])
            : $"{SelectionService.NameOf(set[0])}+{set.Count - 1}";
        GltfExporter.ExportSetToExports(set, name);
    }

    private void OnCtrlPicked(Node3D node) => Toggle(node);

    private void RemoveAt(int i)
    {
        _boxes[i].QueueFree();
        _boxes.RemoveAt(i);
        _members.RemoveAt(i);
    }

    private string CountLine() =>
        _members.Count == 0
            ? ""
            : Log.Format($"\nexport set {_members.Count} node(s), N opens the node lab's Export set");
}
