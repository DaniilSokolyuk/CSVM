namespace CSVM.Flight.Airframe;

/// <summary>The one seam through which a sim step reads this frame's pilot intent: a scripted hold
/// sequence, an AI pilot, or the keyboard/pad. Resolved once, at or before
/// <see cref="FlightController.Bind"/>, because which arm flies a given aircraft cannot change
/// afterward: <see cref="FlightController.Pilot"/> arrives only through the build DTO and the
/// scripted segment list only from the caller, both before the first sim step. Ground-blow probing
/// and the AI ground-blow write stay on the node after <see cref="Read"/> returns: they need the
/// live world, which a source does not have.</summary>
public interface IFlightInputSource
{
    /// <summary>This frame's stick/throttle read, before ground-blow probing is applied.</summary>
    FlightInput Read(float dt);
}

/// <summary>Plays back a scripted sequence of held inputs, the profile a playtest capture or an
/// authored demo flies instead of a live stick. Owns the segment list and its own elapsed-time
/// clock, so a suite can construct one directly with no <see cref="FlightController"/> in the
/// process. Segments run for their duration in order; the last one (or a duration &#8804; 0) holds
/// until <see cref="Reset"/>.</summary>
public sealed class ScriptedInputSource : IFlightInputSource
{
    private readonly (FlightInput Input, float Duration)[] _segments;
    private float _elapsed;

    public ScriptedInputSource((FlightInput Input, float Duration)[] segments) => _segments = segments;

    public FlightInput Read(float dt)
    {
        _elapsed += dt;
        float t = _elapsed;
        for (int i = 0; i < _segments.Length - 1; i++)
        {
            if (_segments[i].Duration <= 0f || t < _segments[i].Duration)
                return _segments[i].Input;
            t -= _segments[i].Duration;
        }
        return _segments[^1].Input;
    }

    /// <summary>Restarts the sequence from its first segment, a respawn's fresh start.</summary>
    public void Reset() => _elapsed = 0f;
}

/// <summary>Reads <see cref="FlightController.Pilot"/>'s AI decision for this frame.</summary>
public sealed class PilotInputSource : IFlightInputSource
{
    private readonly FlightController _owner;

    public PilotInputSource(FlightController owner) => _owner = owner;

    public FlightInput Read(float dt) => _owner.NextPilotInput(dt);
}

/// <summary>Reads this pane's keyboard and gamepad.</summary>
public sealed class KeyboardInputSource : IFlightInputSource
{
    private readonly FlightController _owner;

    public KeyboardInputSource(FlightController owner) => _owner = owner;

    public FlightInput Read(float dt) => _owner.ReadKeyboard(dt);
}
