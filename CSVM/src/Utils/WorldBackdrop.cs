using Godot;

namespace CSVM.Utils;

/// <summary>
/// What the process's one <c>WorldEnvironment</c> draws where nothing opaque covers it. That
/// environment outlives every session and its background is a <c>ProceduralSkyMaterial</c>, a
/// grey-blue gradient belonging to no menu and no mission, so the menu runs over flat black
/// instead and a session gets the sky back. <c>Session/Launch/Launcher.cs</c> owns every call: black on
/// each menu show and at the quits that still draw a frame, the sky at each launch.
/// ⚠ No session may run under the black. With the background flat the sky no longer feeds the
/// glossy water's specular, so the launch path restores it before a world is built.
/// </summary>
public static class WorldBackdrop
{
    /// <summary>Flat black behind whoever owns the screen. The sky material is left where it is, so
    /// <see cref="Sky"/> puts back the one the launch path already built. A null environment is a
    /// run with no lighting rig, and a no-op.</summary>
    public static void Black(Godot.Environment? env)
    {
        if (env == null)
        {
            return;
        }

        env.BackgroundMode = Godot.Environment.BGMode.Color;
        env.BackgroundColor = Colors.Black;
    }

    /// <summary>The sky again: the state the lighting rig is built in, and the one a session's
    /// world reads its reflections from.</summary>
    public static void Sky(Godot.Environment? env)
    {
        if (env == null)
        {
            return;
        }

        env.BackgroundMode = Godot.Environment.BGMode.Sky;
    }

    /// <summary>Whether the background is the flat black <see cref="Black"/> writes, which is what
    /// a frame is asked while the menu owns the screen.</summary>
    public static bool IsBlack(Godot.Environment env) =>
        env.BackgroundMode == Godot.Environment.BGMode.Color && env.BackgroundColor == Colors.Black;
}
