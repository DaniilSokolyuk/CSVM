using System.Collections.Generic;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>The one seam onto the live physics world every aircraft query goes through: a shape
/// swept along a motion, a ray, and a standing-still overlap test.
/// <see cref="GodotWorldQuery"/> is the only adapter over <c>DirectSpaceState</c>; nothing outside
/// it may touch that type.</summary>
public interface IWorldQuery
{
    /// <summary>Sweeps every named part along <paramref name="motion"/> from
    /// <paramref name="baseTransform"/>, masked and with <paramref name="exclude"/> excluded; the
    /// earliest stop across all parts wins. False (default report) when every part clears the
    /// whole motion.</summary>
    bool Sweep(IReadOnlyList<PlaneCollider.Part> parts, Transform3D baseTransform, Vector3 motion,
        uint mask, Godot.Collections.Array<Rid>? exclude, out SweepReport report);

    /// <summary>Casts one ray from <paramref name="from"/> to <paramref name="to"/>, masked and
    /// with <paramref name="exclude"/> excluded. False (default report) when nothing is in the
    /// way.</summary>
    bool Ray(Vector3 from, Vector3 to, uint mask, Godot.Collections.Array<Rid>? exclude,
        out RayReport report);

    /// <summary>True when any named part, placed at <paramref name="pose"/> and moving nowhere,
    /// touches something the mask carries. It asks only whether the airframe is free, so one
    /// intersection is enough and no contact detail comes back.</summary>
    bool Overlaps(IReadOnlyList<PlaneCollider.Part> parts, Transform3D pose, uint mask,
        Godot.Collections.Array<Rid>? exclude);
}

/// <summary>A sweep's answer: where the earliest part stopped, its contact and normal, which of
/// the swept parts it was, and the struck collider (null off world geometry with no live node, or
/// when nothing was hit).</summary>
public readonly record struct SweepReport(
    float StopFraction, Vector3 Contact, Vector3 Normal, string Part, Node? Collider, string ColliderName);

/// <summary>A ray's answer: where and along what normal it stopped, and the struck collider (null
/// off world geometry with no live node, or when nothing was hit).</summary>
public readonly record struct RayReport(Vector3 Position, Vector3 Normal, Node? Collider);
