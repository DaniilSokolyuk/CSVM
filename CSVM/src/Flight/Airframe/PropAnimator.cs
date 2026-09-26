using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.Mech3.Anim;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>
/// Spins a flying aircraft's propeller/rotor blur discs each frame. Cheaper and
/// simpler than an AnimationPlayer: a flat list of (node, rest pose, rate) that the
/// flight loop advances through <see cref="SpinMotion.ComposeSpin"/>, the same
/// accumulate-from-rest decode <c>AnimRuntime</c> plays authored <c>XYZ_ROTATION</c>
/// spins (zeppelin props, signage) through, so a plane's own props share one settled
/// unit conversion instead of a second hand one. Built from a plane model that
/// PlaneBuilder rendered with <c>spinningProps</c> (the blur layers present, the
/// static disc hidden). See <see cref="PropParts"/> for what spins and how fast.
/// </summary>
public sealed class PropAnimator
{
    private readonly List<Spinner> _spinners;

    private PropAnimator(List<Spinner> spinners) => _spinners = spinners;

    public int Count => _spinners.Count;

    /// <summary>Walks the built plane tree and collects every propeller/rotor blur node;
    /// null if the model has none (so callers can skip creating an animator).</summary>
    public static PropAnimator? Build(Node3D planeRoot)
    {
        var spinners = new List<Spinner>();
        Collect(planeRoot, spinners);
        return spinners.Count > 0 ? new PropAnimator(spinners) : null;
    }

    /// <summary>Advances every disc about its own local axis. <paramref name="speedFactor"/>
    /// scales the rate (typically throttle-derived); pass 0 to freeze (e.g. while crashed).</summary>
    public void Advance(double delta, float speedFactor)
    {
        float dt = (float)delta * speedFactor;
        if (dt == 0f)
            return;
        foreach (var s in _spinners)
            s.Advance(dt);
    }

    private static void Collect(Node node, List<Spinner> spinners)
    {
        if (node is Node3D n3d && PropParts.Spin(PropParts.Classify(n3d.Name), out var axis, out var deg))
            spinners.Add(new Spinner(n3d, axis * Mathf.DegToRad(deg)));
        foreach (var child in node.GetChildren())
            Collect(child, spinners);
    }

    // One spinning disc: its rest pose and rate (radians/second, local axes),
    // accumulated total time. Recomputes an absolute pose from rest every Advance
    // via SpinMotion.ComposeSpin rather than stepping `RotateObjectLocal`,
    // so a long session cannot drift, same reasoning as `SpinMotion` itself.
    private sealed class Spinner
    {
        private readonly Node3D _node;
        private readonly Basis _rest;
        private readonly Vector3 _rate; // radians/second, local axes
        private float _t;

        public Spinner(Node3D node, Vector3 rateRadPerSec)
        {
            _node = node;
            _rest = node.Transform.Basis;
            _rate = rateRadPerSec;
        }

        public void Advance(float dt)
        {
            _t += dt;
            var xf = _node.Transform;
            xf.Basis = SpinMotion.ComposeSpin(_rest, _rate, _t);
            _node.Transform = xf;
        }
    }
}
