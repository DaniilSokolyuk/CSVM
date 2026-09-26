using System;
using CSVM.Bindings;
using CSVM.Utils;
using Godot;

namespace CSVM.Sticks;

/// <summary>
/// What the rebinding screens need from the stick side. The save splits an accepted keymap so seat
/// 1's stick rows go to the profile files, and the opener shows the user profile folder.
/// <c>Launcher</c> hands both to the one <c>ControlsFeature</c>. So the remake screen and the
/// original-style page save the same way. The save is pure; the opener is engine code.
/// </summary>
public static class StickScreens
{
    /// <summary>Saves an accepted keymap. For player 1 the stick rows go to
    /// <paramref name="sticks"/>, skipped while sticks are off. The keymap file is then written from
    /// a copy without them. Any other player's keymap is written as it is.</summary>
    public static void Save(int player, BindingProfile keymap, StickProfileSet? sticks, Action<int, BindingProfile> writeKeymap)
    {
        ArgumentNullException.ThrowIfNull(keymap);
        ArgumentNullException.ThrowIfNull(writeKeymap);
        if (player != 1)
        {
            writeKeymap(player, keymap);
            return;
        }

        sticks?.SaveFrom(keymap);
        writeKeymap(player, StickProfileResolver.WithoutStickRows(keymap));
    }

    /// <summary>Creates the user profile folder if missing and opens it in the system file browser.
    /// Returns its OS path, or null with a log line when either step failed.</summary>
    public static string? OpenUserFolder()
    {
        string path = System.IO.Path.GetFullPath(StickProfiles.UserPath());
        var made = DirAccess.MakeDirRecursiveAbsolute(path);
        if (made != Error.Ok && !DirAccess.DirExistsAbsolute(path))
        {
            Log.Warn("core", $"stick profiles folder {path} could not be created: {made}");
            return null;
        }

        var opened = OS.ShellOpen(path);
        if (opened != Error.Ok)
        {
            Log.Warn("core", $"stick profiles folder {path} could not be opened: {opened}");
            return null;
        }

        Log.Info("core", $"stick profiles folder opened: {path}");
        return path;
    }
}
