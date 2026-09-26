namespace CSVM.Flight.Hud;

using System.Collections.Generic;
using Godot;

/// <summary>Screen-space sizing math for world-space sprites, split out from
/// <see cref="Weapons.ProjectilePool"/> so the splitscreen rule it feeds can be unit-tested without a live
/// Godot camera.</summary>
public static class ScreenSize
{
    /// <summary>The minimum world-space size (m) that projects to <paramref name="pixels"/> on
    /// screen at <paramref name="distance"/> from a camera, inverting Godot's default vertical
    /// (KEEP_HEIGHT) perspective projection. Returns 0 for a degenerate
    /// distance, pixel target or viewport, which callers read as "no floor".</summary>
    /// <param name="viewportHeight">The camera's OWN viewport height, a splitscreen pane is
    /// shorter than the window.</param>
    public static float MinWorldSizeForPixels(float pixels, float distance, float fovDeg, float viewportHeight)
    {
        if (distance <= 0f || pixels <= 0f || viewportHeight <= 0f)
        {
            return 0f;
        }

        return pixels * 2f * distance * Mathf.Tan(Mathf.DegToRad(fovDeg) * 0.5f) / viewportHeight;
    }

    /// <summary>The floor to apply to ONE shared world-space mesh seen by several viewers: the
    /// SMALLEST size that meets <paramref name="pixels"/> for any of them. It must be the minimum
    /// and never the maximum: sizing for the farthest viewer inflates every nearer pane, the
    /// splitscreen defect docs/org/tracers.md describes. Degenerate viewers are skipped; a wholly
    /// degenerate set means no floor.</summary>
    public static float NearestFloor(float pixels, IReadOnlyList<ViewerSample> viewers)
    {
        float best = 0f;
        bool any = false;
        for (int i = 0; i < viewers.Count; i++)
        {
            var v = viewers[i];
            float size = MinWorldSizeForPixels(pixels, v.Distance, v.FovDeg, v.ViewportHeight);
            if (size <= 0f)
            {
                continue;
            }

            if (!any || size < best)
            {
                best = size;
                any = true;
            }
        }

        return best;
    }

    /// <summary>One viewer's projection inputs for <see cref="NearestFloor"/>: how far it is from
    /// the object, and the FOV and pane height it would draw that object through.</summary>
    public readonly record struct ViewerSample(float Distance, float FovDeg, float ViewportHeight);
}
