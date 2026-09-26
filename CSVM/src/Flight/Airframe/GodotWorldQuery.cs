using System.Collections.Generic;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>The only adapter over Godot's <c>DirectSpaceState</c>: resolves the live
/// <see cref="World3D"/> at each call rather than caching it, since the wrapped node may be bound
/// before it joins the tree.</summary>
public sealed class GodotWorldQuery : IWorldQuery
{
    private readonly Node3D _node;

    // One query object per shape, reused across calls rather than made fresh at each one: both are
    // finalizable Godot wrappers, and a per-part sweep every physics tick is where that rate is set
    // (docs/verification.md PERF-20). ⚠ Every field a call reads must be assigned on that call --
    // a value left from the previous one is silently still in force. Reuse is safe because the
    // intersect calls below are native and cannot re-enter this class, and the sweep and the ray
    // hold separate objects, so a ray issued between two sweep parts disturbs nothing.
    private readonly PhysicsRayQueryParameters3D _ray = new();
    private readonly PhysicsShapeQueryParameters3D _shape = new();

    // The empty exclusion assigned when a caller passes none, so clearing costs no wrapper either.
    private readonly Godot.Collections.Array<Rid> _noExclude = new();

    public GodotWorldQuery(Node3D node) => _node = node;

    public bool Sweep(IReadOnlyList<PlaneCollider.Part> parts, Transform3D baseTransform, Vector3 motion,
        uint mask, Godot.Collections.Array<Rid>? exclude, out SweepReport report)
    {
        report = default;
        var space = _node.GetWorld3D()?.DirectSpaceState;
        if (space == null)
            return false;

        float mLen = motion.Length();
        var normal = mLen > 1e-6f ? -motion / mLen : Vector3.Up;
        var contact = baseTransform.Origin + motion;
        float stopFrac = 1f;
        string part = "";
        string hitName = "";
        Node? hitBody = null;
        bool hit = false;
        foreach (var p in parts)
        {
            var query = _shape;
            query.Shape = p.Shape;
            query.Transform = baseTransform * p.Local;
            query.Motion = motion;
            query.CollisionMask = mask;
            query.Exclude = exclude ?? _noExclude;
            var cast = space.CastMotion(query); // [safe, unsafe] fractions; [1,1] = clear
            if (cast[0] >= 1f || cast[0] >= stopFrac)
                continue;
            hit = true;
            stopFrac = cast[0];
            part = p.Name;
            // Slightly PAST the first-overlap pose: at exactly cast[1] GetRestInfo can come back
            // empty, leaving the wrong head-on fallback normal on what was really a shallow graze.
            query.Transform = query.Transform.Translated(
                motion * cast[1] + (mLen > 1e-6f ? motion / mLen * 0.05f : Vector3.Zero));
            query.Motion = Vector3.Zero;
            using var rest = space.GetRestInfo(query);
            if (rest.Count > 0)
            {
                contact = (Vector3)rest["point"];
                normal = (Vector3)rest["normal"];
                hitBody = GodotObject.InstanceFromId((ulong)rest["collider_id"]) as Node;
                hitName = hitBody != null ? $"{hitBody.GetParent()?.Name}/{hitBody.Name}" : "world";
            }
            else
            {
                contact = (baseTransform * p.Local).Origin + motion * cast[1];
                normal = mLen > 1e-6f ? -motion / mLen : Vector3.Up;
                hitBody = null;
                hitName = "world";
            }
        }
        report = new SweepReport(stopFrac, contact, normal, part, hitBody, hitName);
        return hit;
    }

    public bool Ray(Vector3 from, Vector3 to, uint mask, Godot.Collections.Array<Rid>? exclude,
        out RayReport report)
    {
        report = default;
        var space = _node.GetWorld3D()?.DirectSpaceState;
        if (space == null)
            return false;
        _ray.From = from;
        _ray.To = to;
        _ray.CollisionMask = mask;
        _ray.Exclude = exclude ?? _noExclude;
        // Disposed, not left to the finalizer: one per aircraft probe per tick (PERF-20).
        using var hit = space.IntersectRay(_ray);
        if (hit.Count == 0)
            return false;
        report = new RayReport(
            (Vector3)hit["position"], (Vector3)hit["normal"], hit["collider"].Obj as Node);
        return true;
    }

    public bool Overlaps(IReadOnlyList<PlaneCollider.Part> parts, Transform3D pose, uint mask,
        Godot.Collections.Array<Rid>? exclude)
    {
        var space = _node.GetWorld3D()?.DirectSpaceState;
        if (space == null)
            return false;
        foreach (var p in parts)
        {
            var query = _shape;
            query.Shape = p.Shape;
            query.Transform = pose * p.Local;
            query.Motion = Vector3.Zero;
            query.CollisionMask = mask;
            query.Exclude = exclude ?? _noExclude;
            // One hit is enough, this only asks whether the box is free.
            var hits = space.IntersectShape(query, 1);
            using var hitsCore = (Godot.Collections.Array)hits; // the typed array is not disposable itself
            if (hits.Count > 0)
                return true;
        }

        return false;
    }
}
