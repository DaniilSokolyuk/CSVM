using Godot;

namespace CSVM.Flight.Weapons;

/// <summary>The original's alternate-frame collision sweep (<c>FUN_0048d7f0</c>'s parity gate,
/// docs/org/flightModel.md "Collision response"): an aircraft sweeps on every other sim step, and
/// a skipped step's motion is carried into the next sweep rather than lost, which is the
/// <c>obj+0x6B0</c> accumulator. Pure, so a suite drives it without a node.
/// ⚠ The parity gates the SWEEP, never the spend. A contact the sweep resolves always spends the
/// damage pair; gating the spend instead lets a contact resolved on the other step place and bounce
/// the airframe for free, and a bounce that clears the surface before the next step never spends
/// at all. That is the defect this replaced.</summary>
public sealed class SweepCadence
{
    private bool _onParity;
    private Vector3? _skippedFrom;

    /// <summary>Back to the phase a fresh airframe starts on: the next step sweeps, from wherever
    /// it enters.</summary>
    public void Reset()
    {
        _onParity = false;
        _skippedFrom = null;
    }

    /// <summary>Advances one sim step. <paramref name="entering"/> is the position the step
    /// entered with, before the plant moved. Answers whether this step sweeps and, when it does,
    /// the origin the sweep runs from: the pose the skipped step before it entered with, so the
    /// carried motion is swept whole, or <paramref name="entering"/> itself right after a reset.</summary>
    public bool Advance(Vector3 entering, out Vector3 from)
    {
        _onParity = !_onParity;
        if (!_onParity)
        {
            _skippedFrom = entering;
            from = entering;
            return false;
        }

        from = _skippedFrom ?? entering;
        _skippedFrom = null;
        return true;
    }
}
