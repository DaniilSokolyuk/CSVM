using System;
using System.Collections.Generic;
using Godot;

namespace CSVM.Flight.Audio;

/// <summary>
/// Where the session's audio listeners are, for every positional flight-audio path: the human
/// pilots' own positions, the same nearest-human seam <c>ProjectilePool</c> measures its weapon
/// one-shots against. A cull reads its distance through here rather than each audio component
/// keeping a copy, so an aircraft cannot come to answer one listener model for its engine and
/// another for its guns.
/// </summary>
public static class AudioListeners
{
    /// <summary>Squared range from <paramref name="at"/> to the nearest listener, or to its own
    /// viewport camera when <paramref name="listeners"/> is null or empty. Zero with no camera
    /// either: a node with nothing to be heard by is not evidence that it should be silent, and
    /// culling it would hide a missing-listener defect behind a working-looking silence.</summary>
    public static float NearestDistanceSq(Node3D at, Func<IReadOnlyList<Vector3>>? listeners)
    {
        ArgumentNullException.ThrowIfNull(at);
        var here = at.GlobalPosition;
        var ears = listeners?.Invoke();
        if (ears is { Count: > 0 })
        {
            float best = float.MaxValue;
            foreach (var ear in ears)
            {
                best = Mathf.Min(best, here.DistanceSquaredTo(ear));
            }
            return best;
        }
        return at.GetViewport()?.GetCamera3D() is { } camera
            ? here.DistanceSquaredTo(camera.GlobalPosition)
            : 0f;
    }
}
