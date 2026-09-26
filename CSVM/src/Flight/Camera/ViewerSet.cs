namespace CSVM.Flight.Camera;

using System.Collections.Generic;
using Godot;

/// <summary>Every pane's camera, session-owned and bound once, so every draw rule that needs
/// "what do the cameras see" shares one registration. Decode/history: docs/architecture.md's
/// entry on this file, docs/org/tracers.md.
/// <see cref="Positions"/> and <see cref="Poses"/> are the two read shapes; both skip a freed
/// camera rather than throw. What "nearest" means stays the consumer's own arithmetic
/// (<see cref="Hud.ScreenSize"/>); this class only carries cameras.
/// ⚠ Not <c>GameSession.PlayerPositions</c>, the "where are the humans" gameplay seam. This one
/// answers "what do the cameras see" for draw rules. Keep them separate.</summary>
public sealed class ViewerSet
{
    private readonly List<Camera3D> _cameras = new();

    /// <summary>The raw bound cameras, unfiltered, for a consumer (the tracer floor) that needs
    /// each viewer's own FOV and pane height alongside its position, not just the position, and
    /// that already skips a freed instance itself while building its per-viewer sample.</summary>
    public IReadOnlyList<Camera3D> Cameras => _cameras;

    /// <summary>Replaces the bound set. Called once per rig build (session start; splitscreen panes
    /// are not rebuilt on respawn), never incrementally.</summary>
    public void Bind(IEnumerable<Camera3D> cameras)
    {
        _cameras.Clear();
        _cameras.AddRange(cameras);
    }

    /// <summary>Every live viewer's position only, B13's nearest-rig query, where forward is
    /// irrelevant (a world light is budgeted by range, not by which way a camera is looking).</summary>
    public List<Vector3> Positions()
    {
        var result = new List<Vector3>(_cameras.Count);
        foreach (var cam in _cameras)
        {
            if (cam == null || !GodotObject.IsInstanceValid(cam))
                continue;
            result.Add(cam.GlobalPosition);
        }
        return result;
    }

    /// <summary>Every live viewer's position and forward, B11's per-particle view-space depth
    /// comparison needs both; a Euclidean nearest can pick the wrong pane for a fade that is
    /// authored along the camera's own forward axis, not straight-line range.</summary>
    public List<ViewerPose> Poses()
    {
        var result = new List<ViewerPose>(_cameras.Count);
        Poses(result);
        return result;
    }

    /// <summary>The same poses into a caller-owned buffer, cleared first, for the one consumer
    /// that reads them EVERY frame (<c>EffectAmbience</c>), where a fresh list per frame is a
    /// per-frame allocation for a set that changes only when the rigs are rebuilt.</summary>
    public void Poses(List<ViewerPose> into)
    {
        into.Clear();
        foreach (var cam in _cameras)
        {
            if (cam == null || !GodotObject.IsInstanceValid(cam))
                continue;
            into.Add(new ViewerPose(cam.GlobalPosition, -cam.GlobalTransform.Basis.Z));
        }
    }

    /// <summary>One viewer's world position and forward direction (`-Z`, the engine's camera-local
    /// forward), B11's view-space depth query.</summary>
    public readonly record struct ViewerPose(Vector3 Position, Vector3 Forward);
}
