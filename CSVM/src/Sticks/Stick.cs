namespace CSVM.Sticks;

/// <summary>
/// One device as SDL lists it before it is opened. That is enough to decide whether CSVM wants it,
/// so the gap-filler never opens a pad Godot already reads. <c>Instance</c> is SDL's id for this
/// connection (a replug gets a new one); <c>Guid</c> is SDL's 32-hex-digit joystick GUID.
/// <c>Gamepad</c> is whether SDL has a gamepad mapping for it, which the Linux skip rule reads.
/// </summary>
public sealed record StickListing(int Instance, string Name, StickModel Model, string Guid, bool Gamepad = false);

/// <summary>
/// One opened stick in the roster, with the control counts SDL reported when it opened.
/// <c>Instance</c> is the key every read goes through. Button reads stop at
/// <see cref="StickRoster.MaxButtons"/> whatever <c>Buttons</c> says.
/// </summary>
public sealed record Stick(int Instance, string Name, StickModel Model, string Guid, int Axes, int Buttons, int Hats);
