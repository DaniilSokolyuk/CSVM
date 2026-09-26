using System;
using System.Collections.Generic;
using CSVM.Flight.Ai;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.World;

/// <summary>
/// The mission's scripted-path vehicles: the placement, the freeze, the goal-driven release and
/// the handoff back to the flight model. One registry per campaign mission, owned by
/// <see cref="Campaign.CampaignDirector"/>, which releases from <c>START_TAXI</c> and steps it beside the
/// objectives graph. The law itself is <see cref="PathFollower"/> and the route
/// <see cref="ScriptedPath"/>; this class only owns the lifecycle and the bodies.
/// </summary>
public sealed class ScriptedPathVehicles
{
    private readonly Func<string, IReadOnlyList<Node3D>> _findNodes;
    private readonly Dictionary<string, Entry> _placed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Binds the registry to the built world's one name resolver, which is what the
    /// authored path subtrees are found through.</summary>
    public ScriptedPathVehicles(Func<string, IReadOnlyList<Node3D>> findNodes)
    {
        _findNodes = findNodes;
    }

    /// <summary>Vehicles currently placed on a path, released or not.</summary>
    public int Count => _placed.Count;

    /// <summary>Places a spawned vehicle on its authored path, frozen on the first waypoint facing
    /// down the first leg. False when the chapter carries no such path, which leaves the vehicle
    /// flight-simulated rather than stationary. <paramref name="onComplete"/> is the handoff, raised
    /// once with the speed the path left the vehicle at. <paramref name="setPose"/> takes the
    /// position and heading (radians about up) in place of the body's own transform write, for a
    /// body whose pose a simulation of its own owns.</summary>
    public bool Place(string vehicle, string pathName, Node3D body, Action<float>? onComplete = null,
        Action<Vector3, float>? setPose = null)
    {
        if (ScriptedPath.Resolve(pathName, _findNodes) is not { } path)
        {
            return false;
        }

        // ⚠ Never seed this from the body's own pose: a roster block that authors a path authors
        // coordinates hundreds of metres off it, and the original overwrites them with waypoint 0
        // and the leg into waypoint 1 (docs/org/flightModel.md, "The scripted-path follower").
        var start = path.Waypoints[0];
        var leg = path.Waypoints.Count > 1 ? path.Waypoints[1] - start : Vector3.Zero;
        float heading = new Vector2(leg.X, leg.Z).LengthSquared() > 1e-6f
            ? Mathf.Atan2(-leg.X, -leg.Z)
            : body.GlobalRotation.Y;
        var follower = new PathFollower(path.Waypoints, start, heading);
        var entry = new Entry(follower, body, onComplete, setPose);
        _placed[vehicle] = entry;
        WritePose(entry, start, heading);
        return true;
    }

    /// <summary>Releases a placed vehicle, which starts it moving at once. False when nothing of
    /// that name is placed, which is what the director reports as an unconsumed directive.</summary>
    public bool Release(string vehicle)
    {
        if (!_placed.TryGetValue(vehicle, out var entry))
        {
            return false;
        }

        entry.Follower.Frozen = false;
        return true;
    }

    /// <summary>Whether a placed vehicle is still held at its start. Unplaced reads false.</summary>
    public bool IsFrozen(string vehicle) =>
        _placed.TryGetValue(vehicle, out var entry) && entry.Follower.Frozen;

    /// <summary>One tick of every placed vehicle. A finished one is dropped from the registry after
    /// its handoff, so nothing keeps writing a pose over the flight model's.</summary>
    public void Step(float dt)
    {
        List<string>? finished = null;
        foreach (var (name, entry) in _placed)
        {
            if (!GodotObject.IsInstanceValid(entry.Body))
            {
                (finished ??= new List<string>()).Add(name);
                continue;
            }

            entry.Follower.Step(dt);
            WritePose(entry, entry.Follower.Position, entry.Follower.Heading);
            if (!entry.Follower.Following)
            {
                entry.OnComplete?.Invoke(entry.Follower.Speed);
                (finished ??= new List<string>()).Add(name);
            }
        }

        if (finished == null)
        {
            return;
        }

        foreach (var name in finished)
        {
            _placed.Remove(name);
        }
    }

    // The one pose write, so the placement and the per-tick step cannot disagree about which of the
    // two channels a body takes.
    private static void WritePose(Entry entry, Vector3 position, float heading)
    {
        if (entry.SetPose is { } setPose)
        {
            setPose(position, heading);
            return;
        }

        entry.Body.GlobalPosition = position;
        entry.Body.GlobalRotation = new Vector3(0f, heading, 0f);
        // Only this branch. A SetPose callee owns its own node and its own draw path, which for
        // an aeroplane on a taxi path is the already-interpolated flight controller.
        RenderPoses.Record(entry.Body);
    }

    private readonly record struct Entry(PathFollower Follower, Node3D Body, Action<float>? OnComplete,
        Action<Vector3, float>? SetPose);
}
