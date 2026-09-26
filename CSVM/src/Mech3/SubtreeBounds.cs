using Godot;

namespace CSVM.Mech3;

/// <summary>A built subtree's world-space extent, measured from its own meshes. Shared by the
/// animation runtime's effect siting and by every inspect tool, which is why it sits below both.
/// Debug drawings mark themselves with <see cref="OverlayMeta"/>, so a tool can parent one onto
/// the object it annotates without growing the next box measured over it.</summary>
public static class SubtreeBounds
{
    /// <summary>Node metadata marking a subtree as a tool's DRAWING rather than world content, such
    /// as collider wireframes, normal lines and highlight boxes. Both <see cref="WorldAabb"/> and
    /// the selection pick skip anything carrying it.</summary>
    public const string OverlayMeta = "csvm_overlay";

    // The same key as a StringName, built once. The string const converts to a fresh finalizable
    // StringName on every HasMeta call, which the per-node box walk cannot afford.
    private static readonly StringName OverlayMetaName = OverlayMeta;

    /// <summary>World-frame union of a subtree's own mesh AABBs. It is measured from the subtree
    /// itself, never a shared merge helper, and skips empty meshes, so nothing parked elsewhere
    /// can enter the box. Meshless subtree ⇒ a zero-size box at the
    /// node's own position.</summary>
    public static Aabb WorldAabb(Node3D root)
    {
        Aabb merged = default;
        bool any = false;
        // Indexed children and a prebuilt StringName, never GetChildren() or the string const.
        // Each of those allocates a finalizable Godot wrapper per node visited. A frequent walk
        // over a large world subtree is what fed the gen1 collection pauses.
        void Walk(Node n)
        {
            if (n is Node3D overlay && overlay.HasMeta(OverlayMetaName))
            {
                return; // a tool's drawing parked on this object is not part of its extent
            }
            if (n is MeshInstance3D { Mesh: { } mesh } mi)
            {
                var local = mesh.GetAabb();
                if (local.Size.LengthSquared() > 1e-9f)
                {
                    var box = mi.GlobalTransform * local;
                    merged = any ? merged.Merge(box) : box;
                    any = true;
                }
            }
            int count = n.GetChildCount();
            for (int i = 0; i < count; i++)
            {
                Walk(n.GetChild(i));
            }
        }
        Walk(root);
        return any ? merged : new Aabb(root.GlobalPosition, Vector3.Zero);
    }
}
